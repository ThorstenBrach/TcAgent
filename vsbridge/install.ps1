# Installs TcAgentBridge into Visual Studio 2022. Run AS ADMINISTRATOR. Close VS first.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not (Test-Path "$here\bin\TcAgentBridge.dll")) { throw "Run build.ps1 first" }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -version '[17.0,18.0)' -latest -property installationPath
if (-not $vsPath) { throw "Visual Studio 2022 not found" }
if (Get-Process devenv -ErrorAction SilentlyContinue) { throw "Visual Studio is still running - please close it" }
$ide    = Join-Path $vsPath 'Common7\IDE'
$target = Join-Path $ide 'Extensions\TcAgentBridge'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item "$here\bin\*" $target -Force
Write-Host "Copied to $target"
Write-Host "devenv /updateconfiguration ..."
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "Done. Start VS 2022 with the TwinCAT solution. Log: $env:LOCALAPPDATA\TcAgent\bridge\bridge.log" -ForegroundColor Green
