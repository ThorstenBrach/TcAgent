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
& $msbuild "$here\vsbridge\TcAgentBridge.csproj" /restore /t:Rebuild /p:Configuration=Release /v:minimal /nologo 2>&1 | Tee-Object -FilePath "$here\build-vsix.log" -Append
if ($LASTEXITCODE -ne 0) { throw "VSIX build failed - see build-vsix.log" }
$built = Get-ChildItem "$here\vsbridge\bin\Release" -Recurse -Filter 'TcAgent.vsix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw "TcAgent.vsix not found under vsbridge\bin\Release" }
$vsix = Join-Path $here "bin\TcAgent-$version.vsix"
Copy-Item $built.FullName $vsix -Force
Write-Host ""
Write-Host "OK: $vsix" -ForegroundColor Green
Write-Host "Install: double-click (close Visual Studio first). Uninstall: Extensions > Manage Extensions > TcAgent."
