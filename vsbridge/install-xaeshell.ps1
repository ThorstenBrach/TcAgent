# Builds TcAgentBridge (chat window, inline questions, editor context) for the TwinCAT XAE Shell and installs it.
# The XAE Shell is an isolated Visual Studio shell (32 bit, VS 2017 base) - a VSIX for VS 2022 cannot be installed there,
# so the extension is compiled against the shell's own assemblies and copied into its Extensions folder.
#
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1              build + install (asks for administrator rights)
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1 -Uninstall   remove it again
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1 -BuildOnly   only build into vsbridge\bin-xaeshell
#                                                                               (normal user, nothing installed, no prompt)
#   -ShellIde <path>   Common7\IDE folder of the shell (default: found automatically)
#
# Close the XAE Shell first (not needed for -BuildOnly). Needs Visual Studio 2022 (or its Build Tools) for the C# compiler.
# SPDX-License-Identifier: MIT
param([string]$ShellIde, [switch]$Uninstall, [switch]$Elevated, [switch]$BuildOnly)
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
Write-Host "XAE Shell: $shellExe"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Invoke-Install {
    if (Get-Process -Name 'TcXaeShell*' -ErrorAction SilentlyContinue) { throw "The XAE Shell is still running - please close it" }
    if ($Uninstall) {
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed: $target"
    } else {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item "$out\*" $target -Force
        Write-Host "Copied to $target"
    }
    # the isolated VS 2017 shell has no /updateconfiguration switch - touching this file makes it rebuild
    # its configuration (pkgdef merge) on the next start
    $flag = Join-Path $ShellIde 'Extensions\extensions.configurationchanged'
    if (Test-Path $flag) { (Get-Item $flag).LastWriteTime = Get-Date } else { New-Item -ItemType File $flag | Out-Null }
    Write-Host "Configuration refresh requested ($flag)"
    Write-Host "Done. Start the XAE Shell: View > Other Windows > TcAgent Chat (or Ctrl+Shift+Q in the code editor)." -ForegroundColor Green
    Write-Host "The server (TcAgent.exe) is taken from the VSIX of the same version installed in Visual Studio 2022 - double-click bin\TcAgent-<version>.vsix if not done yet."
}

# second stage (administrator): only copy + register
if ($Elevated) { try { Invoke-Install } catch { Write-Host $_ -ForegroundColor Red }; Read-Host "Enter to close"; return }

if (-not $Uninstall) {
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
    # manifest for the shell: its VS base version, installed like Beckhoff's own shell extensions (no VSIX installer)
    $m = Get-Content "$here\extension.vsixmanifest" -Raw
    $range = "[$shellMajor.0,$($shellMajor + 1).0)"
    $arch = if ($shellMajor -ge 17) { '<ProductArchitecture>amd64</ProductArchitecture>' } else { '' }
    $installation = '<Installation InstalledByMsi="true"><InstallationTarget Id="Microsoft.VisualStudio.Community" Version="' + $range + '">' + $arch + '</InstallationTarget></Installation>'
    $m = [regex]::Replace($m, '(?s)<Installation[^>]*>.*?</Installation>', $installation)
    $m = [regex]::Replace($m, '(?s)<Prerequisites>.*?</Prerequisites>', '')
    Set-Content "$out\extension.vsixmanifest" $m -Encoding UTF8
    Write-Host "Built: $out\TcAgentBridge.dll" -ForegroundColor Green
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
