# Installs TcAgentBridge (chat window, inline questions, editor context) - and with it the server TcAgent.exe - into the
# TwinCAT XAE Shell. The XAE Shell is an isolated Visual Studio shell (32 bit: VS 2017 base, 64 bit: VS 2022 base) - a
# VSIX for VS 2022 cannot be installed there, so the extension is copied into its Extensions folder.
#
# Two ways:
#   - from the package TcAgent-XaeShell-<version>.zip of a release (this script next to the folders bridge-vs15 /
#     bridge-vs17 and tcagent): the extension is prebuilt, the server comes along - no Visual Studio needed on the PC
#   - from the source (this script in vsbridge\): the extension is compiled against the shell's own assemblies (needs
#     Visual Studio 2022 or its Build Tools); the server comes from -Vsix <TcAgent-<version>.vsix> or from the VSIX of
#     the same version installed in Visual Studio 2022
#
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1              install (asks for administrator rights)
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1 -Uninstall   remove it again
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1 -BuildOnly   only prepare - source: build into vsbridge\bin-xaeshell,
#                                                                               package: check it
#                                                                               (normal user, nothing installed, no prompt)
#   -Vsix <file>       source only: take the server from this VSIX (for a PC without Visual Studio 2022)
#   -ShellIde <path>   Common7\IDE folder of the shell (default: found automatically)
#
# Close the XAE Shell first (not needed for -BuildOnly).
# SPDX-License-Identifier: MIT
param([string]$ShellIde, [switch]$Uninstall, [switch]$Elevated, [switch]$BuildOnly, [string]$Vsix)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Find-Shell {
    $candidates = @("${env:ProgramFiles(x86)}\Beckhoff\TcXaeShell\Common7\IDE", "$env:ProgramFiles\Beckhoff\TcXaeShell\Common7\IDE",
                    "C:\TwinCAT\TcXaeShell\Common7\IDE")
    foreach ($c in $candidates) { if (Get-ChildItem $c -Filter 'TcXaeShell*.exe' -ErrorAction SilentlyContinue) { return $c } }
    throw "TwinCAT XAE Shell not found - pass -ShellIde '<shell>\Common7\IDE'"
}
if (-not $ShellIde) { $ShellIde = Find-Shell }
$shellExe = (Get-ChildItem $ShellIde -Filter 'TcXaeShell*.exe' | Select-Object -First 1).FullName
# 32-bit shell = VS 2017 base (15.x), 64-bit shell (TcXaeShell64) = VS 2022 base (17.x)
$shellMajor = [int]((Get-Item $shellExe).VersionInfo.FileMajorPart)
if ($shellExe -like '*TcXaeShell64.exe') { $shellMajor = [Math]::Max($shellMajor, 17) }
if ($shellMajor -lt 15) { $shellMajor = 15 }
$target = Join-Path $ShellIde 'Extensions\TcAgent\TcAgentBridge'
$out = Join-Path $here 'bin-xaeshell'
# the release package: the extension prebuilt for this shell base next to the script - nothing to compile
$package = Test-Path (Join-Path $here "bridge-vs$shellMajor\TcAgentBridge.dll")
if ($package) { $out = Join-Path $here "bridge-vs$shellMajor" }
# the server for the shell: from the package, or staged from -Vsix; otherwise the extension takes it from the VSIX in VS 2022
$serverDir = if (Test-Path (Join-Path $here 'tcagent\TcAgent.exe')) { Join-Path $here 'tcagent' } else { Join-Path $out 'tcagent' }
Write-Host "XAE Shell: $shellExe"

# manifest for the shell: its VS base version, installed like Beckhoff's own shell extensions (no VSIX installer)
function Write-ShellManifest([string]$src, [string]$dst) {
    $m = Get-Content $src -Raw
    $range = "[$shellMajor.0,$($shellMajor + 1).0)"
    $arch = if ($shellMajor -ge 17) { '<ProductArchitecture>amd64</ProductArchitecture>' } else { '' }
    $installation = '<Installation InstalledByMsi="true"><InstallationTarget Id="Microsoft.VisualStudio.Community" Version="' + $range + '">' + $arch + '</InstallationTarget></Installation>'
    $m = [regex]::Replace($m, '(?s)<Installation[^>]*>.*?</Installation>', $installation)
    $m = [regex]::Replace($m, '(?s)<Prerequisites>.*?</Prerequisites>', '')
    Set-Content $dst $m -Encoding UTF8
}

function Get-ManifestVersion([string]$text) { ([regex]::Match($text, '<Identity [^>]*Version="([^"]+)"')).Groups[1].Value }

# Files from a ZIP downloaded from the internet carry its mark (Zone.Identifier) when the ZIP was not unblocked before
# extracting - copying keeps it, and the shell (.NET Framework, no loadFromRemoteSources) does not load a marked
# TcAgentBridge.dll (0x80131515): no TcAgent window, no setup on the first start. The script never removes the mark
# itself (that is the user's decision) - it stops before installing and says what to do.
function Assert-NotBlocked([string[]]$paths) {
    $blocked = @($paths | ForEach-Object { Get-ChildItem $_ -Recurse -File -ErrorAction SilentlyContinue } |
                 Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue })
    if ($blocked.Count -eq 0) { return }
    throw ("$($blocked.Count) file(s) of the package are marked 'downloaded from the internet' (e.g. $($blocked[0].Name)) - the XAE Shell " +
           "does not load the extension like that. Nothing was installed. Right-click the downloaded ZIP > Properties > Unblock " +
           "(or in PowerShell: Unblock-File <path of the ZIP>), extract it again into an empty folder and run this script there " +
           "(docs/INSTALLATION.md, XAE Shell).")
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Invoke-Install {
    if (Get-Process -Name 'TcXaeShell*' -ErrorAction SilentlyContinue) { throw "The XAE Shell is still running - please close it" }
    if ($Uninstall) {
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed: $target"
    } else {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Get-ChildItem $out -File | Copy-Item -Destination $target -Force
        Write-Host "Copied to $target"
        if (Test-Path (Join-Path $serverDir 'TcAgent.exe')) {
            # the extension finds the server in its own folder "tcagent" (as in the VSIX) and sets it up on the next start
            Remove-Item (Join-Path $target 'tcagent') -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item $serverDir (Join-Path $target 'tcagent') -Recurse -Force
            Write-Host "Server copied to $target\tcagent"
        }
    }
    # the isolated VS 2017 shell has no /updateconfiguration switch - touching this file makes it rebuild
    # its configuration (pkgdef merge) on the next start
    $flag = Join-Path $ShellIde 'Extensions\extensions.configurationchanged'
    if (Test-Path $flag) { (Get-Item $flag).LastWriteTime = Get-Date } else { New-Item -ItemType File $flag | Out-Null }
    Write-Host "Configuration refresh requested ($flag)"
    Write-Host "Done. Start the XAE Shell: View > Other Windows > TcAgent Chat (or Ctrl+Shift+Q in the code editor)." -ForegroundColor Green
    if ($Uninstall) { return }
    if (Test-Path (Join-Path $serverDir 'TcAgent.exe')) {
        Write-Host ("On its first start the shell sets TcAgent up: the server goes to %LOCALAPPDATA%\TcAgent\app\<version>, the interop is " +
                    "generated from TwinCAT, and it asks whether the AI clients should be set up (later: chat menu > Set up clients).")
    } else {
        Write-Host "The server (TcAgent.exe) is taken from the VSIX of the same version installed in Visual Studio 2022 - double-click bin\TcAgent-<version>.vsix if not done yet (or run this script with -Vsix <file>)."
    }
}

# second stage (administrator): only copy + register
if ($Elevated) { try { Invoke-Install } catch { Write-Host $_ -ForegroundColor Red }; Read-Host "Enter to close"; return }

if (-not $Uninstall -and $package) {
    # ---------------------------------------------------------------- release package: nothing to build
    if ($Vsix) { throw "-Vsix: this is a release package - the server comes with it (folder tcagent)" }
    Write-ShellManifest (Join-Path $out 'extension.vsixmanifest') (Join-Path $out 'extension.vsixmanifest')
    Write-Host "Prebuilt extension for the shell base VS $shellMajor`: $out"
    if (-not (Test-Path (Join-Path $serverDir 'TcAgent.exe'))) { throw "The package has no server (folder tcagent next to this script) - extract the whole ZIP" }
    Assert-NotBlocked @($out, $serverDir)
    if ($BuildOnly) { Write-Host "-BuildOnly: package checked (extension and server found), nothing installed."; return }
}
elseif (-not $Uninstall) {
    if (-not (Test-Path (Join-Path $here 'TcAgentBridge.csproj'))) {
        throw ("No extension for this shell (VS $shellMajor base) in this package (folders: " +
               ((Get-ChildItem $here -Directory -Filter 'bridge-vs*' | ForEach-Object Name) -join ', ') + ") - please report it as an issue")
    }
    # ---------------------------------------------------------------- build (normal user)
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -products * -version '[17.0,18.0)' -latest -property installationPath
    if (-not $vs) { throw "Visual Studio 2022 (or Build Tools) not found - needed for the C# compiler" }
    $csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
    if (-not (Test-Path $csc)) { throw "Roslyn compiler not found: $csc" }

    # references: assemblies of the SHELL first (VS 15 API), then the 32-bit .NET Framework
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $index = @{}
    foreach ($r in @("$ShellIde\PublicAssemblies", "$ShellIde\PrivateAssemblies", $ShellIde, "$ShellIde\CommonExtensions\Microsoft", $fw, "$fw\WPF")) {
        if (-not (Test-Path $r)) { continue }
        $recurse = $r -like '*CommonExtensions*'
        Get-ChildItem $r -Filter *.dll -Recurse:$recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.resources.dll' } | ForEach-Object {
                $n = [IO.Path]::GetFileNameWithoutExtension($_.Name)
                if (-not $index.ContainsKey($n)) { $index[$n] = $_.FullName }
            }
    }
    $refs = New-Object System.Collections.Generic.List[string]
    # Shell.Interop.11.0: IVsDifferenceService (compare window of the knowledge window) - a missing namespace member (CS0234)
    # is not added by the retry loop below, so every interop assembly the code uses directly is listed here
    foreach ($n in 'Microsoft.VisualStudio.Shell.15.0', 'Microsoft.VisualStudio.Shell.Framework', 'Microsoft.VisualStudio.Shell.Interop',
                   'Microsoft.VisualStudio.Shell.Interop.8.0', 'Microsoft.VisualStudio.Shell.Interop.10.0', 'Microsoft.VisualStudio.Shell.Interop.11.0',
                   'Microsoft.VisualStudio.OLE.Interop',
                   'Microsoft.VisualStudio.Threading', 'Microsoft.VisualStudio.Validation', 'Microsoft.VisualStudio.Shell.Immutable.10.0',
                   'Microsoft.VisualStudio.Shell.Immutable.11.0', 'Microsoft.VisualStudio.Imaging', 'EnvDTE', 'EnvDTE80',
                   'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml', 'System.Web.Extensions', 'System.Windows.Forms',
                   'System.Drawing', 'System.Core', 'System', 'System.IO.Compression', 'System.IO.Compression.FileSystem') {
        if ($index.ContainsKey($n)) { $refs.Add($index[$n]) }
    }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # menu commands (View > Other Windows > TcAgent Chat / TcUnit Tests): TcAgentBridge.vsct -> .cto with VSCT.exe of the
    # VS SDK build tools (NuGet cache, restored by build-vsix.ps1), as resource Menus.ctmenu in VSPackage.resources like the VS SDK build
    $menuRes = @()
    $vsct = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.vssdk.buildtools" -Recurse -Filter VSCT.exe -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    $cto = Join-Path $out 'TcAgentBridge.cto'; $resFile = Join-Path $out 'VSPackage.resources'
    if ($vsct) {
        & $vsct.FullName "$here\TcAgentBridge.vsct" $cto "-I$(Join-Path $vsct.Directory.Parent.FullName 'inc')" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $w = New-Object System.Resources.ResourceWriter($resFile)
            $w.AddResource('Menus.ctmenu', [IO.File]::ReadAllBytes($cto)); $w.Generate(); $w.Close()
            $menuRes = @("/resource:$resFile,VSPackage.resources")
        } else { Write-Host "VSCT failed - no menu entries (the TcUnit window opens from the chat's Tests button)" -ForegroundColor Yellow }
    } else { Write-Host "VSCT.exe not found (NuGet package Microsoft.VSSDK.BuildTools - run build-vsix.ps1 once) - no menu entries" -ForegroundColor Yellow }
    $built = $false
    for ($i = 1; $i -le 25; $i++) {
        $a = @('/nologo', '/target:library', '/platform:anycpu', '/optimize+', '/langversion:latest', '/define:XAESHELL',
               '/nowarn:1701,1702,CS1998,CS0618', "/out:$out\TcAgentBridge.dll") + $menuRes + ($refs | ForEach-Object { "/r:$_" }) +
             (Get-ChildItem "$here\*.cs" | ForEach-Object { $_.FullName })
        $output = & $csc @a 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { $built = $true; break }
        $missing = [regex]::Matches($output, 'CS0012[^\r\n]*?["''„“]([A-Za-z0-9_.]+), Version=') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        $added = 0
        foreach ($m in $missing) { if ($index.ContainsKey($m) -and -not $refs.Contains($index[$m])) { $refs.Add($index[$m]); $added++; Write-Host "  + reference $m" } }
        if ($added -eq 0) {
            Write-Host $output -ForegroundColor Red
            if ($output -match 'CS0234[^\r\n]*Microsoft\.VisualStudio\.Shell\.Interop') {
                Write-Host ("A type of Microsoft.VisualStudio.Shell.Interop is missing: an interop assembly of the shell was not found " +
                            "(searched: $ShellIde\PublicAssemblies, PrivateAssemblies, the IDE folder). Is -ShellIde the Common7\IDE folder of the XAE Shell?") -ForegroundColor Yellow
            }
            throw "Build for the XAE Shell failed - please send this output"
        }
    }
    if (-not $built) { throw "Build for the XAE Shell failed" }
    Remove-Item $cto, $resFile -ErrorAction SilentlyContinue

    Copy-Item "$here\TcAgentBridge.pkgdef" $out -Force
    Copy-Item "$here\..\LICENSE" "$out\LICENSE.txt" -Force
    Copy-Item "$here\..\docs\img\icon.png" "$out\icon.png" -Force
    Write-ShellManifest "$here\extension.vsixmanifest" "$out\extension.vsixmanifest"
    Write-Host "Built: $out\TcAgentBridge.dll" -ForegroundColor Green

    # the server: from a VSIX file (a PC without Visual Studio 2022), staged next to the build
    Remove-Item (Join-Path $out 'tcagent') -Recurse -Force -ErrorAction SilentlyContinue
    if ($Vsix) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Vsix).Path)
        try {
            $entries = @($zip.Entries | Where-Object { $_.FullName -like 'tcagent/*' -and $_.Name })
            if ($entries.Count -eq 0) { throw "$Vsix contains no server (folder tcagent) - is it a TcAgent VSIX?" }
            foreach ($e in $entries) {
                $to = Join-Path $out ($e.FullName -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $to, $true)
            }
            $vm = $zip.GetEntry('extension.vsixmanifest')
            $vsixVersion = if ($vm) { $r = New-Object IO.StreamReader($vm.Open()); try { Get-ManifestVersion $r.ReadToEnd() } finally { $r.Dispose() } }
        } finally { $zip.Dispose() }
        $ownVersion = Get-ManifestVersion (Get-Content "$here\extension.vsixmanifest" -Raw)
        if ($vsixVersion -and $vsixVersion -ne $ownVersion) {
            Write-Host "Note: the VSIX is version $vsixVersion, this extension $ownVersion - server and extension should have the same version." -ForegroundColor Yellow
        }
        Write-Host "Server from $Vsix`: $out\tcagent"
    }
    if ($BuildOnly) { Write-Host "-BuildOnly: nothing installed. Install with the same command without -BuildOnly (asks for administrator rights)."; return }
}
elseif ($BuildOnly) { throw "-BuildOnly and -Uninstall do not go together" }

# ---------------------------------------------------------------- copy + register (administrator)
if ($isAdmin) { Invoke-Install }
else {
    Write-Host "Administrator rights are needed to write into $ShellIde - confirm the prompt."
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-ShellIde', "`"$ShellIde`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
}
