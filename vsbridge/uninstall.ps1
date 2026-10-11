# Entfernt TcAgentBridge aus Visual Studio 2022. ALS ADMINISTRATOR ausfuehren. VS vorher schliessen.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$ide = Join-Path (& $vswhere -version '[17.0,18.0)' -latest -property installationPath) 'Common7\IDE'
Remove-Item (Join-Path $ide 'Extensions\TcAgentBridge') -Recurse -Force -ErrorAction SilentlyContinue
Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
Write-Host "TcAgentBridge aus VS 2022 entfernt."
