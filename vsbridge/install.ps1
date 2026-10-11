# Installiert TcAgentBridge in Visual Studio 2022. ALS ADMINISTRATOR ausfuehren. VS vorher schliessen.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not (Test-Path "$here\bin\TcAgentBridge.dll")) { throw "Erst build.ps1 ausfuehren" }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -version '[17.0,18.0)' -latest -property installationPath
if (-not $vsPath) { throw "Visual Studio 2022 nicht gefunden" }
if (Get-Process devenv -ErrorAction SilentlyContinue) { throw "Visual Studio laeuft noch - bitte schliessen" }
$ide    = Join-Path $vsPath 'Common7\IDE'
$target = Join-Path $ide 'Extensions\TcAgentBridge'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item "$here\bin\*" $target -Force
Write-Host "Kopiert nach $target"
Write-Host "devenv /updateconfiguration ..."
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "Fertig. VS 2022 mit der TwinCAT-Solution starten. Log: $env:LOCALAPPDATA\TcAgent\bridge\bridge.log" -ForegroundColor Green
