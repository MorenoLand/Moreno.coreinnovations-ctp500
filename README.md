# ThermalPrint

PC utility for printing to a Core Innovations CTP500BR Bluetooth thermal printer.

Target printer specs:

- 384 dots wide
- 200 DPI
- 48 mm paper
- BLE advertising name: `B Pink Printer`

## Build

```powershell
dotnet build .\ThermalPrint.csproj
```

Library-only build:

```powershell
dotnet build .\ThermalPrint.csproj -p:BuildLib=true
```
