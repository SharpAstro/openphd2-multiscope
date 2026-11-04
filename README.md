# OpenPHD2 Multi-scope proxy

A multiplexing proxy for PHD2's JSON-RPC event API. It lets several imaging programs (N.I.N.A.,
SGP, APT, one per camera on a multi-scope mount) share **one** PHD2, and dithers only once **all**
of them asked, so no program's dither ruins another's exposure.

It drives PHD2 through [TianWen](https://github.com/SharpAstro/tianwen)'s PHD2 driver, but you do not
need TianWen to use it: it is one self-contained executable.

## Usage

1. Start PHD2 as usual (with "Enable Server" on, the default).
2. Run `openphd2-multiscope`.
3. In each imaging program, point the PHD2 guider at port **4410** instead of 4400.

```
openphd2-multiscope [--phd2-host localhost] [--phd2-instance 1] [--listen 127.0.0.1] [--port 4410] [--shared-timeout 0]
```

| Option | Default | Meaning |
|---|---|---|
| `--phd2-host` | `localhost` | Where PHD2 runs |
| `--phd2-instance` | `1` | PHD2 instance (instance N listens on 4400 + N - 1) |
| `--listen` | `127.0.0.1` | Address to listen on; `0.0.0.0` for programs on other computers |
| `--port` | `4410` | Port the imaging programs connect to |
| `--shared-timeout` | `0` | Seconds a shared command waits for the other programs before acting anyway; 0 waits for all of them |

## What is shared, and how

The guider is shared, so a command one program sends can disturb the others. The proxy sorts the
commands into two kinds:

- **Shared: acts once every connected program asked** (or `--shared-timeout` passed): `dither`,
  `stop_capture`, `loop`, `set_paused` (pausing), `set_connected` (disconnecting). Each program gets
  its reply at once, as PHD2 would answer it; the events it waits for (`SettleBegin` ... `SettleDone`
  after a dither) arrive when the last program asks. A program that disconnects stops being waited for.
- **Immediate: acts at once**: `guide` (if PHD2 is already guiding, it only settles again),
  unpausing, connecting, and the queries `get_app_state`, `get_connected`, `get_pixel_scale`,
  `get_exposure`, `get_camera_frame_size`, `get_profiles`, `get_profile`.

Any other method gets a JSON-RPC "method not found" error straight away rather than no answer.

**Tip:** dither every N frames in each program so that they reach their dither at about the same
time (same exposure length, or a multiple), or a program whose exposure ended first waits for the
others, and may hit its own settle timeout. `--shared-timeout` bounds that wait.

## What is relayed

Every connected program receives PHD2's `Version` and `AppState` greeting, then `GuideStep` (in
guide-camera pixels), `Settling`, `SettleDone` and PHD2's other state events. Events whose fields
TianWen's driver does not report (`LockPositionSet`'s X/Y, `Alert`'s message) are relayed by name only.

If PHD2 goes away, the proxy closes every program's connection and reconnects every 5 s; the
programs reconnect on their own.

## Building

Needs the .NET 10 SDK (10.0.403 or later; see `global.json`).

```
dotnet build
dotnet test --project src/OpenPHD2MultiScope.Tests
dotnet publish src/OpenPHD2MultiScope -c Release -r win-x64    # one native executable
```

With a clone of `tianwen` next to this one, the build uses TianWen.Lib's source; otherwise the
`TianWen.Lib` package. `-p:UseLocalTianWen=false` builds against the package as CI does.
