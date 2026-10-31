# Removes TcAgentBridge from Visual Studio 2022 (installed with install.ps1). Run AS ADMINISTRATOR. Close VS first.
# An extension installed from the VSIX is removed in Visual Studio: Extensions > Manage Extensions > TcAgent.
$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -version '[17.0,18.0)' -latest -property installationPath
if (-not $vsPath) { throw "Visual Studio 2022 not found" }
$ide    = Join-Path $vsPath 'Common7\IDE'
$target = Join-Path $ide 'Extensions\TcAgentBridge'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Administrator rights are needed: $target is in the Visual Studio installation." -ForegroundColor Red
    Write-Host "(-ExecutionPolicy Bypass only allows the script to run, it does not give these rights.) Nothing was changed."
    Write-Host "Open PowerShell with 'Run as administrator' (right-click) and run"
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit 1
}
if (Get-Process devenv -ErrorAction SilentlyContinue) { throw "Visual Studio is still running - please close it" }
if (Test-Path $target) { Remove-Item $target -Recurse -Force } else { Write-Host "Not installed: $target" }
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "TcAgentBridge removed from VS 2022."
