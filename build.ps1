# Builds Monitor Window Control into .\publish\MonitorWindowControl.exe (one file; the tiny helper is packed inside it).
#
# Needs: the .NET SDK 10 (or newer), and for the helper the Visual Studio "Desktop development with C++" tools
# (the native linker). Usage:  .\build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# --- 1. The tiny helper (NativeAOT). The native compiler needs the MSVC linker and Windows SDK libraries on PATH/LIB.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio C++ build tools not found (install the 'Desktop development with C++' workload)." }
$msvc = Get-ChildItem "$vs\VC\Tools\MSVC" | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$sdkLib = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Lib" | Where-Object { Test-Path "$($_.FullName)\um\x64\kernel32.lib" } |
          Sort-Object { [version]$_.Name } | Select-Object -Last 1
$env:PATH = "$($msvc.FullName)\bin\Hostx64\x64;$env:PATH"
$env:LIB = "$($msvc.FullName)\lib\x64;$($sdkLib.FullName)\um\x64;$($sdkLib.FullName)\ucrt\x64"

dotnet publish "$root\MonitorWindowControlHelper\MonitorWindowControlHelper.csproj" -c Release -o "$root\MonitorWindowControlHelper\bin\publish" -p:IlcUseEnvironmentalTools=true
if ($LASTEXITCODE) { throw "Helper build failed." }

# --- 2. The settings app (embeds the helper built above).
dotnet publish "$root\MonitorWindowControl\MonitorWindowControl.csproj" -c Release -o "$root\publish"
if ($LASTEXITCODE) { throw "App build failed." }

Get-ChildItem "$root\MonitorWindowControlHelper\bin\publish\MonitorWindowControlHelper.exe", "$root\publish\MonitorWindowControl.exe" |
    Select-Object Name, @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } }
