# TrayBattery

Shows the battery level of wireless devices on the Windows 11 taskbar, just left of the
"Show hidden icons" arrow.

## Sources
- **Logitech G HUB**: reads mouse/headset/keyboard battery from G HUB's local API.
  If G HUB isn't running, TrayBattery starts it hidden in the system tray (`--minimized`). Devices that are off are hidden.
- **Bluetooth**: any connected Bluetooth device that Windows reports a battery level for
  (the same value shown in Settings > Bluetooth & devices). This covers most Bluetooth
  headsets/earbuds and Bluetooth LE mice, keyboards and controllers.

Cheap 2.4 GHz dongle mice usually don't report battery to Windows at all, so nothing can read them
unless their own app shows a level somewhere.

## Use
- Run `TrayBattery.exe`.
- Hover a device to see its full name; left-click opens G HUB or Bluetooth settings.
- Right-click for: show or hide individual devices, layout (icon above or beside the percentage),
  **Start with Windows**, refresh, and exit.
- Red = 20% or lower, green bolt = charging.
- It hides itself during full-screen apps and when the taskbar is auto-hidden.

Settings and a log are in `%APPDATA%\TrayBattery`. `settings.json` also has `OffsetX`
(extra pixels to shift left) and `LowThreshold`.

## Build
`powershell -ExecutionPolicy Bypass -File build.ps1` (uses the C# compiler that ships with Windows).

Flags: `--demo` shows fake devices; `--debug` logs raw G HUB messages.
