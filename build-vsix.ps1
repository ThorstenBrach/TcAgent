# Builds the installable package TcAgent-<version>.vsix (TcAgent + Visual Studio extension + client setup).
# Users only double-click the .vsix - no scripts, no administrator rights (per-user installation).
# On first start of Visual Studio the extension copies TcAgent to %LOCALAPPDATA%\TcAgent\app\<version>\, generates the
# Automation Interface interop from the local TwinCAT installation and, after asking, registers TcAgent in the AI clients.
#
#   powershell -ExecutionPolicy Bypass -File .\build-vsix.ps1
#
# Requirements on the BUILD machine: .NET 8 SDK, Visual Studio 2022, TwinCAT 3.1 XAE (for the interop at compile time).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
# the built-in knowledge is the submodule knowledge\ (TcAgent_Knowledge): without it the package would have no skills
if (-not (Test-Path "$here\knowledge\prompts")) { throw "knowledge\ is empty - run: git submodule update --init" }
Set-Location $here
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# 1) Interop for compiling (generated from the local TwinCAT type library, never shipped).
#    No developer build here: bin\publish\TcAgent.exe may be locked by running AI clients.
& "$here\build.ps1" -InteropOnly

# 2) TcAgent as a self-contained single file - users need no .NET runtime; interop excluded
$stage = Join-Path $here 'bin\vsix'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish "$here\TcAgent.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:InteropPrivate=false -o "$stage\tcagent" 2>&1 | Tee-Object -FilePath "$here\build-vsix.log"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed - see build-vsix.log" }
Get-ChildItem "$stage\tcagent" -Filter 'Interop.*.dll' | Remove-Item -Force
Get-ChildItem "$stage\tcagent" -Filter '*.pdb' | Remove-Item -Force
Copy-Item "$here\setup-clients.ps1", "$here\setup-runner.ps1", "$here\SKILL.md", "$here\THIRD-PARTY-NOTICES.md", "$here\LICENSE" "$stage\tcagent\"
# the self-contained .NET runtime brings third-party components of its own: ship its notices unchanged
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$rtPack = Get-ChildItem (Join-Path $nugetRoot 'microsoft.netcore.app.runtime.win-x64') -Directory -Filter '8.0.*' -ErrorAction SilentlyContinue |
          Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$rtNotices = if ($rtPack) { Join-Path $rtPack.FullName 'THIRD-PARTY-NOTICES.TXT' }
if (-not $rtNotices -or -not (Test-Path $rtNotices)) { throw "THIRD-PARTY-NOTICES.TXT of the .NET 8 runtime pack not found under $nugetRoot - it must be shipped with TcAgent.exe" }
Copy-Item $rtNotices "$stage\tcagent\THIRD-PARTY-NOTICES-DOTNET.TXT"
# never ship Visual Studio automation assemblies (Microsoft license) - TcAgent loads them from the user's VS
Get-ChildItem "$stage\tcagent" -Include 'envdte*.dll','Microsoft.VisualStudio.Interop.dll' -Recurse | Remove-Item -Force
New-Item -ItemType Directory -Force "$stage\tcagent\agents" | Out-Null
Copy-Item "$here\agents\TCAGENT_GUIDE.md" "$stage\tcagent\agents\"
# built-in knowledge (skills ...) next to TcAgent.exe - the single-file publish does not take content folders along
if (-not (Test-Path "$stage\tcagent\knowledge")) { Copy-Item "$here\knowledge" "$stage\tcagent\knowledge" -Recurse }
Remove-Item "$stage\tcagent\knowledge\.git" -Force -ErrorAction SilentlyContinue     # the link of the submodule, not content

# 3) Visual Studio extension + VSIX, built with the Visual Studio SDK build tools (Visual Studio's MSBuild)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -version '[17.0,18.0)' -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild of Visual Studio 2022 not found" }
$version = ([xml](Get-Content "$here\vsbridge\extension.vsixmanifest")).PackageManifest.Metadata.Identity.Version
Copy-Item "$here\vsbridge\extension.vsixmanifest" "$here\vsbridge\source.extension.vsixmanifest" -Force
Copy-Item "$here\LICENSE" "$here\vsbridge\LICENSE.txt" -Force
Copy-Item "$here\docs\img\icon.png" "$here\vsbridge\icon.png" -Force
if (Test-Path "$here\vsbridge\tcagent") { Remove-Item "$here\vsbridge\tcagent" -Recurse -Force }
Copy-Item "$stage\tcagent" "$here\vsbridge\tcagent" -Recurse
# a "Platform" variable of the console (set by some PCs or developer prompts, e.g. x64) moves MSBuild's output to
# bin\x64\Release - so the package is taken from wherever this build wrote it, and an older one is never shipped
$buildStart = Get-Date
& $msbuild "$here\vsbridge\TcAgentBridge.csproj" /restore /t:Rebuild /p:Configuration=Release /v:minimal /nologo 2>&1 | Tee-Object -FilePath "$here\build-vsix.log" -Append
if ($LASTEXITCODE -ne 0) { throw "VSIX build failed - see build-vsix.log" }
$built = Get-ChildItem "$here\vsbridge\bin" -Recurse -Filter 'TcAgent.vsix' | Where-Object { $_.LastWriteTime -ge $buildStart } |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw "TcAgent.vsix of this build not found under vsbridge\bin (only older packages) - see build-vsix.log" }
Write-Host "Package: $($built.FullName)"
$vsix = Join-Path $here "bin\TcAgent-$version.vsix"
Copy-Item $built.FullName $vsix -Force

# 3) package for a PC with the TwinCAT XAE Shell alone (no Visual Studio 2022 there): the extension prebuilt per shell base,
#    the server and install-xaeshell.ps1 - the script installs it without compiling
$pkg = Join-Path $here 'bin\xaeshell-package'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force "$pkg\bridge-vs17" | Out-Null
# VS 2022 base (64-bit shell): the extension of this VSIX build
Copy-Item (Join-Path $built.DirectoryName 'TcAgentBridge.dll'), "$here\vsbridge\TcAgentBridge.pkgdef", "$here\vsbridge\extension.vsixmanifest" "$pkg\bridge-vs17"
Copy-Item "$here\LICENSE" "$pkg\bridge-vs17\LICENSE.txt"; Copy-Item "$here\docs\img\icon.png" "$pkg\bridge-vs17\icon.png"
# VS 2017 base (32-bit shell): built against the shell of this PC, if one is installed here
& "$here\vsbridge\install-xaeshell.ps1" -BuildOnly *>&1 | Out-Null
$shellBuild = Join-Path $here 'vsbridge\bin-xaeshell'
$shellBase = ([regex]::Match((Get-Content "$shellBuild\extension.vsixmanifest" -Raw -ErrorAction SilentlyContinue), 'Version="\[(\d+)\.')).Groups[1].Value
if ((Test-Path "$shellBuild\TcAgentBridge.dll") -and $shellBase -and $shellBase -ne '17' -and (Get-Item "$shellBuild\TcAgentBridge.dll").LastWriteTime -ge $buildStart) {
    New-Item -ItemType Directory -Force "$pkg\bridge-vs$shellBase" | Out-Null
    Get-ChildItem $shellBuild -File | Copy-Item -Destination "$pkg\bridge-vs$shellBase"
    Copy-Item "$here\vsbridge\extension.vsixmanifest" "$pkg\bridge-vs$shellBase\extension.vsixmanifest" -Force
} else { Write-Host "No 32-bit XAE Shell on this PC - the XAE Shell package has no extension for it (only bridge-vs17)" -ForegroundColor Yellow }
Copy-Item "$stage\tcagent" "$pkg\tcagent" -Recurse
Copy-Item "$here\vsbridge\install-xaeshell.ps1" $pkg
@"
TcAgent $version for the TwinCAT XAE Shell - for a PC without Visual Studio 2022.

1. Close the XAE Shell. Extract this ZIP into a folder of its own (Explorer: right-click the ZIP > Properties > Unblock first).
2. In that folder, in PowerShell:  powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1
   (confirm the administrator prompt - the extension goes into the shell's folder under Program Files)
3. Start the XAE Shell: TcAgent sets itself up and asks whether the AI clients should be set up.
   View > Other Windows > TcAgent Chat. Remove it again: install-xaeshell.ps1 -Uninstall

Details: docs/INSTALLATION.md, section "Only the XAE Shell".
"@ | Set-Content "$pkg\README.txt" -Encoding UTF8
$shellZip = Join-Path $here "bin\TcAgent-XaeShell-$version.zip"
if (Test-Path $shellZip) { Remove-Item $shellZip -Force }
Compress-Archive -Path "$pkg\*" -DestinationPath $shellZip
Write-Host "XAE Shell package: $shellZip ($((Get-ChildItem $pkg -Directory -Filter 'bridge-vs*' | ForEach-Object Name) -join ', '))"
Write-Host ""
Write-Host "OK: $vsix" -ForegroundColor Green
Write-Host "Install: double-click (close Visual Studio first). Uninstall: Extensions > Manage Extensions > TcAgent."
