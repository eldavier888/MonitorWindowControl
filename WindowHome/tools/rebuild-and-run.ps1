# Dev helper: stop WindowHome, publish the single-file exe to ..\publish, start it again.
param([switch]$Fresh)
Stop-Process -Name WindowHome -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if ($Fresh) { Remove-Item "$env:APPDATA\WindowHome" -Recurse -Force -ErrorAction SilentlyContinue }
Push-Location (Join-Path $PSScriptRoot '..')
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "..\publish" 2>&1 | Select-String "error|warning CS|->"
Pop-Location
Start-Process (Join-Path $PSScriptRoot '..\..\publish\WindowHome.exe')
