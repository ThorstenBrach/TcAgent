# Installs TcAgentBridge into Visual Studio 2022. Run AS ADMINISTRATOR. Close VS first.
# The usual way is the VSIX (double-click, per user, no administrator rights) - this script copies a build of
# vsbridge\build.ps1 into the Visual Studio installation instead.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not (Test-Path "$here\bin\TcAgentBridge.dll")) { throw "Run build.ps1 first" }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -version '[17.0,18.0)' -latest -property installationPath
if (-not $vsPath) { throw "Visual Studio 2022 not found" }
$ide    = Join-Path $vsPath 'Common7\IDE'
$target = Join-Path $ide 'Extensions\TcAgentBridge'

# the extension goes into the Visual Studio installation (Program Files): only an administrator may write there.
# Checked before anything is changed - -ExecutionPolicy Bypass only lets the script run, it gives no rights.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Administrator rights are needed: TcAgentBridge is copied into $target" -ForegroundColor Red
    Write-Host "(-ExecutionPolicy Bypass only allows the script to run, it does not give these rights.) Nothing was changed."
    Write-Host "Either: open PowerShell with 'Run as administrator' (right-click) and run"
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Write-Host "or install the VSIX instead (double-click bin\TcAgent-<version>.vsix) - per user, no administrator rights."
    exit 1
}
if (Get-Process devenv -ErrorAction SilentlyContinue) { throw "Visual Studio is still running - please close it" }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item "$here\bin\*" $target -Force
Write-Host "Copied to $target"
Write-Host "devenv /updateconfiguration ..."
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "Done. Start VS 2022 with the TwinCAT solution. Log: $env:LOCALAPPDATA\TcAgent\bridge\bridge.log" -ForegroundColor Green
