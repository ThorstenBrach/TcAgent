# Builds TcAgentBridge (chat window, inline questions, editor context) for the TwinCAT XAE Shell and installs it.
# The XAE Shell is an isolated Visual Studio shell (32 bit, VS 2017 base) - a VSIX for VS 2022 cannot be installed there,
# so the extension is compiled against the shell's own assemblies and copied into its Extensions folder.
#
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1              build + install (asks for administrator rights)
#   powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1 -Uninstall   remove it again
#   -ShellIde <path>   Common7\IDE folder of the shell (default: found automatically)
#
# Close the XAE Shell first. Needs Visual Studio 2022 (or its Build Tools) for the C# compiler.
# SPDX-License-Identifier: MIT
param([string]$ShellIde, [switch]$Uninstall, [switch]$Elevated)
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
    foreach ($n in 'Microsoft.VisualStudio.Shell.15.0', 'Microsoft.VisualStudio.Shell.Framework', 'Microsoft.VisualStudio.Shell.Interop',
                   'Microsoft.VisualStudio.Shell.Interop.8.0', 'Microsoft.VisualStudio.Shell.Interop.10.0', 'Microsoft.VisualStudio.OLE.Interop',
                   'Microsoft.VisualStudio.Threading', 'Microsoft.VisualStudio.Validation', 'Microsoft.VisualStudio.Shell.Immutable.10.0',
                   'Microsoft.VisualStudio.Shell.Immutable.11.0', 'Microsoft.VisualStudio.Imaging', 'EnvDTE', 'EnvDTE80',
                   'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml', 'System.Web.Extensions', 'System.Windows.Forms',
                   'System.Drawing', 'System.Core', 'System', 'System.IO.Compression', 'System.IO.Compression.FileSystem') {
        if ($index.ContainsKey($n)) { $refs.Add($index[$n]) }
    }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $built = $false
    for ($i = 1; $i -le 25; $i++) {
        $a = @('/nologo', '/target:library', '/platform:anycpu', '/optimize+', '/langversion:latest', '/define:XAESHELL',
               '/nowarn:1701,1702,CS1998,CS0618', "/out:$out\TcAgentBridge.dll") + ($refs | ForEach-Object { "/r:$_" }) +
             (Get-ChildItem "$here\*.cs" | ForEach-Object { $_.FullName })
        $output = & $csc @a 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { $built = $true; break }
        $missing = [regex]::Matches($output, 'CS0012[^\r\n]*?["''„“]([A-Za-z0-9_.]+), Version=') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        $added = 0
        foreach ($m in $missing) { if ($index.ContainsKey($m) -and -not $refs.Contains($index[$m])) { $refs.Add($index[$m]); $added++; Write-Host "  + reference $m" } }
        if ($added -eq 0) { Write-Host $output -ForegroundColor Red; throw "Build for the XAE Shell failed - please send this output" }
    }
    if (-not $built) { throw "Build for the XAE Shell failed" }

    Copy-Item "$here\TcAgentBridge.pkgdef" $out -Force
    Copy-Item "$here\..\LICENSE" "$out\LICENSE.txt" -Force
    Copy-Item "$here\..\docs\img\icon.png" "$out\icon.png" -Force
    # manifest for the shell: VS 15 base, installed like Beckhoff's own shell extensions (no VSIX installer)
    $m = Get-Content "$here\extension.vsixmanifest" -Raw
    $m = [regex]::Replace($m, '(?s)<Installation[^>]*>.*?</Installation>',
        '<Installation InstalledByMsi="true"><InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[15.0,16.0)" /></Installation>')
    $m = [regex]::Replace($m, '(?s)<Prerequisites>.*?</Prerequisites>', '')
    Set-Content "$out\extension.vsixmanifest" $m -Encoding UTF8
    Write-Host "Built: $out\TcAgentBridge.dll" -ForegroundColor Green
}

# ---------------------------------------------------------------- copy + register (administrator)
if ($isAdmin) { Invoke-Install }
else {
    Write-Host "Administrator rights are needed to write into $ShellIde - confirm the prompt."
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-ShellIde', "`"$ShellIde`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
}
