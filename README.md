Windows companion for the Core Innovations CTP500BR BLE thermal printer (384 dots, 200 DPI, 48 mm).
Go + [Gio](https://gioui.org) GUI, WinRT Bluetooth via `tinygo.org/x/bluetooth`. One ~11 MB exe, no runtime or loose DLLs.

## Build

    .\build.ps1

Produces in `dist\`: `ThermalPrint.exe` (GUI), `ThermalPrint.dll` (C ABI, needs gcc to build), `tpcli.exe` (scan/probe/print from a shell).

## Features

- Live device picker, remembers and reconnects to the last printer (reads the C# build's `%AppData%\ThermalPrint\settings.json`).
- Image (button, drag-and-drop) + wrapped text with size/alignment/bold, composed at 384 dots; the preview is the exact 1-bit output.
- Threshold, Floyd-Steinberg dithering, invert; density, feed, self-test, stop; print progress; tray icon (close hides to tray).
- Paper/cover/error status through standard ESC/POS real-time status (`10 04 n`), checked before every print.

## What the printer answers (probed on a real CTP500BR)

- Answers `10 04 01..04` (idle replies `16 12 12 10`) and `1D 72 01` (`00`).
- Does **not** answer any `51 78 …` vendor frame (status, battery, info, device id), and no probed query returns a battery level, so no battery is shown.
- `libPrinterNative.so` contains no `51 78` frames; its checksum rule (CRC-8 poly 0x07) matches the frames in `thermal/protocol.go`, but those frames' effect on this printer (density, print mode) is unverified.

## DLL

`TP_Scan`, `TP_Connect`, `TP_Disconnect`, `TP_Connected`, `TP_Status`, `TP_PrintText`, `TP_PrintImageFile`, `TP_PrintBitmap`, `TP_LastError` — see `dll/main.go` and the generated `ThermalPrint.h`. Return 0 on success, negative on error.
