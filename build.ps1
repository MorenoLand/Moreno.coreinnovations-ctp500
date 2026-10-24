# Builds dist\ThermalPrint.exe (GUI) and dist\ThermalPrint.dll (+ .h) — needs Go and gcc (for the DLL only).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force dist | Out-Null
if (-not (Test-Path rsrc_windows_amd64.syso)) {
    Push-Location assets; windres -O coff -i app.rc -o ..\rsrc_windows_amd64.syso; Pop-Location
}
go test ./thermal
go build -ldflags "-H windowsgui -s -w" -trimpath -o dist\ThermalPrint.exe .
go build -buildmode=c-shared -ldflags "-s -w" -trimpath -o dist\ThermalPrint.dll .\dll
go build -ldflags "-s -w" -trimpath -o dist\tpcli.exe .\tpcli
Get-ChildItem dist | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
