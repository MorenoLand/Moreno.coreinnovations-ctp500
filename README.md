# ThermalPrint

PC utility for printing to a Core Innovations CTP500BR Bluetooth thermal printer.

Target printer specs:

- 384 dots wide
- 200 DPI
- 48 mm paper

## Build

```powershell
dotnet build .\ThermalPrint.csproj
```

Library-only build:

```powershell
dotnet build .\ThermalPrint.csproj -p:BuildLib=true
```

## Features

- Scans for and connects to the printer over BLE; shows battery and paper/cover/overheat status.
- Image and text are composed at the printer's fixed 384-dot width. The preview is the exact 1-bit output that is sent.
- Optional Floyd-Steinberg dithering for photos, threshold and invert controls, text size/alignment/bold with word wrap.
- Density, feed, self-test and stop commands. Print progress and connection loss are shown in the UI.

Command frames (`51 78 <cmd> 00 <len16> <payload> <crc8> FF`, CRC-8 poly 0x07) are built in `Cmd.Frame`.
