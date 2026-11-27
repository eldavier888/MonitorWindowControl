# Dev helper: stop Monitor Window Control (and its helper), rebuild everything with build.ps1, start it again.
param([switch]$Fresh)
Stop-Process -Name MonitorWindowControl, MonitorWindowControlHelper -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if ($Fresh) { Remove-Item "$env:APPDATA\MonitorWindowControl" -Recurse -Force -ErrorAction SilentlyContinue }
& (Join-Path $PSScriptRoot '..\..\build.ps1')
Start-Process (Join-Path $PSScriptRoot '..\..\publish\MonitorWindowControl.exe')
