#!/usr/bin/env bash
# Runs the end-to-end checks on Linux: PHD2 (simulator profile) on a virtual screen, the proxy in
# front of it, and e2e.py's two clients against the proxy. Needs phd2, Xvfb and python3 installed.
#
#   tests/e2e/run.sh <path to the openphd2-multiscope executable> [logs dir]
#
# Leaves PHD2's logs, the proxy's output and a screenshot of the virtual screen in the logs dir, so a
# dialog PHD2 stopped on can be seen.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
proxy_exe="$1"
logs="${2:-$PWD/e2e-logs}"
mkdir -p "$logs"

xvfb_pid= phd2_pid= proxy_pid=
cleanup() {
  status=$?
  if command -v import >/dev/null; then import -display :99 -window root "$logs/screen.png" 2>/dev/null || true; fi
  [ -n "$proxy_pid" ] && kill "$proxy_pid" 2>/dev/null || true
  [ -n "$phd2_pid" ] && kill "$phd2_pid" 2>/dev/null || true
  [ -n "$xvfb_pid" ] && kill "$xvfb_pid" 2>/dev/null || true
  cp -r "$HOME/PHD2" "$logs/phd2-logs" 2>/dev/null || true
  exit "$status"
}
trap cleanup EXIT

wait_port() { # port seconds
  for _ in $(seq "$2"); do
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && return 0
    sleep 1
  done
  echo "nothing listening on port $1 after $2 s" >&2
  return 1
}

cp "$here/phd2-simulator.cfg" "$HOME/.PHDGuidingV2"

export DISPLAY=:99
Xvfb :99 -screen 0 1600x1200x24 >"$logs/xvfb.out" 2>&1 &
xvfb_pid=$!
sleep 2

phd2 >"$logs/phd2.out" 2>&1 &
phd2_pid=$!
wait_port 4400 60

"$proxy_exe" --port 4410 >"$logs/proxy.out" 2>&1 &
proxy_pid=$!
wait_port 4410 30

python3 "$here/e2e.py" 4410
