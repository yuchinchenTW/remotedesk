# Simple Remote MVP

Windows LAN-only remote desktop MVP with two executables:

- `RemoteHost.exe`: runs on the computer being controlled.
- `RemoteViewer.exe`: runs on the computer doing the controlling.

## What it does

- Streams the full virtual desktop as JPEG frames over TCP, so dual-monitor setups are shown as one combined image.
- Forwards mouse move, click, wheel, and basic keyboard key down/up events.
- Protects access with a required password.
- Reuses capture buffers, scales oversized desktops before encoding, and drops stale frames to keep latency lower.

## Limits

- One viewer at a time.
- No NAT traversal, relay, audio, clipboard, file transfer, or UAC bypass.
- For elevated windows, run `RemoteHost.exe` as administrator.

## Build

This repo builds with the built-in .NET Framework compiler already present on Windows:

```powershell
.\build.ps1
```

The output goes to `dist\`.

## Usage

1. Run `RemoteHost.exe` on the target PC.
2. Pick a port and password, then click `Start Host`.
3. Use any of the shown local IPv4 addresses.
4. Run `RemoteViewer.exe` on the controller PC.
5. Enter the host IP, the same port, and the same password.
6. Click inside the remote image before typing.
7. If the host has multiple monitors, they appear as one combined desktop.
