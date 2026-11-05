"""Two PHD2 clients against openphd2-multiscope: greeting, queries, guiding, a shared dither, a shared stop.

Needs a running PHD2 whose profile uses the simulator camera and mount (see phd2-simulator.cfg), and
the proxy in front of it. Exits non-zero when any check fails, so CI can gate on it.

    python e2e.py [proxy-port]      # default 4410
"""
import json, socket, sys, threading, time

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 4410
failures = []


class Client:
    def __init__(self, name):
        self.name = name
        self.sock = self._connect()
        self.sock.settimeout(None)  # the connect timeout would otherwise end the reader on a quiet stretch
        self.file = self.sock.makefile("r", encoding="utf-8", newline="\r\n")
        self.events, self.replies, self.lock = [], {}, threading.Lock()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()

    @staticmethod
    def _connect(attempts=60):
        # The proxy refuses clients until it has PHD2's greeting, so the first tries may be closed at once.
        for _ in range(attempts):
            try:
                sock = socket.create_connection(("127.0.0.1", PORT), timeout=5)
                sock.settimeout(5)
                first = sock.recv(1, socket.MSG_PEEK)
                if first:
                    return sock
                sock.close()
            except OSError:
                pass
            time.sleep(1)
        raise ConnectionError(f"no greeting from the proxy on port {PORT}")

    def _read(self):
        try:
            for line in self.file:
                msg = json.loads(line)
                with self.lock:
                    if "Event" in msg:
                        self.events.append(msg)
                    else:
                        self.replies[msg.get("id")] = msg
        except Exception:  # socket closed
            pass

    def call(self, method, params=None, timeout=60):
        self.next_id += 1
        rid = self.next_id
        req = {"method": method, "id": rid}
        if params is not None:
            req["params"] = params
        self.sock.sendall((json.dumps(req) + "\r\n").encode())
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                if rid in self.replies:
                    return self.replies.pop(rid)
            time.sleep(0.05)
        raise TimeoutError(f"{self.name}: no reply to {method}")

    def event_names(self):
        with self.lock:
            return [e["Event"] for e in self.events]

    def wait_event(self, name, after=0, timeout=120):
        end = time.time() + timeout
        while time.time() < end:
            if name in self.event_names()[after:]:
                return True
            time.sleep(0.1)
        return False

    def wait_state(self, state, timeout=30):
        end = time.time() + timeout
        while time.time() < end:
            if self.call("get_app_state").get("result") == state:
                return True
            time.sleep(0.5)
        return False


def check(label, ok):
    print(("PASS " if ok else "FAIL ") + label, flush=True)
    if not ok:
        failures.append(label)
    return ok


a, b = Client("A"), Client("B")
time.sleep(1)
check("greeting is Version then AppState", a.event_names()[:2] == ["Version", "AppState"])
with a.lock:
    greeting = a.events[:2]
check("greeting has PHD2's real version", greeting[0].get("PHDVersion") not in (None, "", "Unknown"))
check("greeting state agrees with get_app_state", greeting[1].get("State") == a.call("get_app_state").get("result"))
print("profiles:", a.call("get_profiles"), flush=True)
check("unknown method -> error -32601", a.call("find_star").get("error", {}).get("code") == -32601)
check("set_connected succeeds", "error" not in a.call("set_connected", [True]))
check("equipment connected", a.call("get_connected").get("result") is True)
print("pixel scale:", a.call("get_pixel_scale"), "exposure:", a.call("get_exposure"), flush=True)

# The first guide on a fresh profile calibrates the simulator first, hence the long wait.
settle = {"pixels": 1.5, "time": 5, "timeout": 120}
check("guide accepted", "error" not in a.call("guide", [settle, False]))
check("guiding settles (SettleDone on B too)", b.wait_event("SettleDone", timeout=420))
time.sleep(3)
check("GuideStep relayed", "GuideStep" in b.event_names())
with b.lock:
    steps = [e for e in b.events if e["Event"] == "GuideStep"]
check("GuideStep carries pixel distances", bool(steps) and "RADistanceRaw" in steps[-1] and "DECDistanceRaw" in steps[-1])

mark_a, mark_b = len(a.event_names()), len(b.event_names())
check("A's dither acknowledged", a.call("dither", {"amount": 3, "raOnly": False, "settle": settle}).get("result") == 0)
time.sleep(4)
check("no dither yet after A alone", "SettleBegin" not in a.event_names()[mark_a:])
check("B's dither acknowledged", b.call("dither", [3, False, settle]).get("result") == 0)
check("dither happened: A sees SettleDone", a.wait_event("SettleDone", after=mark_a, timeout=180))
check("dither happened: B sees SettleDone", b.wait_event("SettleDone", after=mark_b, timeout=180))
check("exactly one dither (one SettleBegin)", a.event_names()[mark_a:].count("SettleBegin") == 1)

mark_a = len(a.event_names())
a.call("stop_capture")
time.sleep(3)
# Judged by the event, not only the state: the simulator loses its star now and then, and PHD2
# reports "LostLock" while it is still guiding.
check("A alone cannot stop guiding",
      "GuidingStopped" not in a.event_names()[mark_a:]
      and a.call("get_app_state").get("result") in ("Guiding", "LostLock"))
# B leaves (shutdown: close() alone leaves the socket open while makefile() holds it),
# so the stop A asked for now has everyone it needs.
b.sock.shutdown(socket.SHUT_RDWR)
b.sock.close()
check("B leaving completes A's stop", a.wait_state("Stopped", timeout=30))
print("A events:", a.event_names(), flush=True)

print(f"\n{len(failures)} failed" if failures else "\nall checks passed", flush=True)
sys.exit(1 if failures else 0)
