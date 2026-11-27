# Dev helper: stop Monitor Window Control, publish the single-file exe to ..\publish, start it again.
param([switch]$Fresh)
Stop-Process -Name MonitorWindowControl -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if ($Fresh) { Remove-Item "$env:APPDATA\MonitorWindowControl" -Recurse -Force -ErrorAction SilentlyContinue }
Push-Location (Join-Path $PSScriptRoot '..')
dotnet publish -c Release -o "..\publish" 2>&1 | Select-String "error|warning CS|->"
Pop-Location
Start-Process (Join-Path $PSScriptRoot '..\..\publish\MonitorWindowControl.exe')
