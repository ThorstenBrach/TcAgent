# Removes TcAgentBridge from Visual Studio 2022. Run AS ADMINISTRATOR. Close VS first.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$ide = Join-Path (& $vswhere -version '[17.0,18.0)' -latest -property installationPath) 'Common7\IDE'
Remove-Item (Join-Path $ide 'Extensions\TcAgentBridge') -Recurse -Force -ErrorAction SilentlyContinue
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "TcAgentBridge removed from VS 2022."
