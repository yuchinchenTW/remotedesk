# Simple Remote MVP

Windows LAN-only remote desktop MVP with two executables:

- `RemoteHost.exe`: runs on the computer being controlled.
- `RemoteViewer.exe`: runs on the computer doing the controlling.

## What it does

- Streams the full virtual desktop as JPEG frames over TCP, so dual-monitor setups are shown as one combined image.
- Forwards mouse move, click, wheel, and basic keyboard key down/up events.
- `Ctrl+V` in the viewer sends local clipboard text to the remote PC and triggers paste there.
- The viewer supports zoom in/out buttons and `Ctrl+MouseWheel` for display scaling.
- Hosts broadcast their presence over UDP on the local LAN, and viewers list discovered hosts automatically.
- Protects access with a required password.
- Reuses capture buffers, scales oversized desktops before encoding, and drops stale frames to keep latency lower.
- On single-monitor hosts, it now prefers the Windows Desktop Duplication API for lower-latency capture and falls back to GDI if DXGI duplication is unavailable.
- On the Desktop Duplication path, the host now prefers dirty-region patch updates instead of re-sending the whole desktop every frame.
- On single-monitor hosts with `ffmpeg.exe` next to the app, it now prefers an H.264 stream over MPEG-TS for lower bandwidth and smoother playback.

## Limits

- One viewer at a time.
- Auto-discovery is LAN-only and uses UDP broadcast, so it does not replace ZeroTier or internet-wide discovery.
- Multi-monitor hosts currently keep using the older GDI capture path.
- The `ffmpeg` H.264 path currently targets single-monitor hosts. Multi-monitor hosts still fall back to the older JPEG path.
- No NAT traversal, relay, audio, clipboard, file transfer, or UAC bypass.
- For elevated windows, run `RemoteHost.exe` as administrator.

## Build

This repo builds with the built-in .NET Framework compiler already present on Windows:

```powershell
.\build.ps1
```

The output goes to `dist\`.
`build.ps1` also copies `ffmpeg.exe` into `dist\` when the bundled tool is present.

## Usage

1. Run `RemoteHost.exe` on the target PC.
2. Pick a port and password, then click `Start Host`.
3. Optional: set a display name. Viewers on the same LAN will see it automatically.
4. Use any of the shown local IPv4 addresses, or let the viewer auto-discover the host on the same LAN.
5. Run `RemoteViewer.exe` on the controller PC.
6. Select a discovered host from the right-side list or enter the host IP manually.
7. Enter the same password and connect.
8. Click inside the remote image before typing.
9. If the host has multiple monitors, they appear as one combined desktop.
