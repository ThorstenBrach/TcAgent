# Builds TcAgentBridge.dll for Visual Studio 2022 with the Roslyn compiler from the VS installation (normal user).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -version '[17.0,18.0)' -latest -property installationPath
if (-not $vs) { throw "Visual Studio 2022 not found" }
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { throw "Roslyn compiler not found: $csc" }
Write-Host "VS 2022: $vs"

# Index: assembly name -> path (VS assemblies first, then the framework)
$index = @{}
$roots = @("$vs\Common7\IDE\PublicAssemblies", "$vs\Common7\IDE\PrivateAssemblies", "$vs\Common7\IDE",
           "$vs\Common7\IDE\CommonExtensions\Microsoft", $fw, "$fw\WPF")
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    $recurse = $r -like '*CommonExtensions*'
    Get-ChildItem $r -Filter *.dll -Recurse:$recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.resources.dll' } | ForEach-Object {
            $n = [IO.Path]::GetFileNameWithoutExtension($_.Name)
            if (-not $index.ContainsKey($n)) { $index[$n] = $_.FullName }
            else {
                # present more than once (e.g. Microsoft.VisualStudio.Threading 15.8 and 17.x): take the highest assembly version
                try {
                    $vNew = [Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version
                    $vOld = [Reflection.AssemblyName]::GetAssemblyName($index[$n]).Version
                    if ($vNew -gt $vOld) { $index[$n] = $_.FullName }
                } catch { }
            }
        }
}
Write-Host "Index: $($index.Count) Assemblies"

$refs = New-Object System.Collections.Generic.List[string]
foreach ($n in 'Microsoft.VisualStudio.Shell.15.0', 'Microsoft.VisualStudio.Shell.Framework', 'Microsoft.VisualStudio.Interop',
               'Microsoft.VisualStudio.Shell.Interop', 'Microsoft.VisualStudio.OLE.Interop', 'Microsoft.VisualStudio.Threading',
               'Microsoft.VisualStudio.Validation', 'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml',
               'System.Web.Extensions', 'System.Windows.Forms', 'System.Drawing', 'System.Core',
               'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System') {
    if ($index.ContainsKey($n)) { $refs.Add($index[$n]) } else { Write-Host "  (not found: $n)" -ForegroundColor Yellow }
}

$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
for ($i = 1; $i -le 25; $i++) {
    $args_ = @('/nologo', '/target:library', '/optimize+', '/langversion:latest', '/nowarn:1701,1702,CS1998', "/out:$out\TcAgentBridge.dll") +
             ($refs | ForEach-Object { "/r:$_" }) + (Get-ChildItem "$here\*.cs" | ForEach-Object { $_.FullName })
    $output = & $csc @args_ 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { Write-Host $output; break }
    $missing = [regex]::Matches($output, 'CS0012[^\r\n]*?["''„“]([A-Za-z0-9_.]+), Version=') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $added = 0
    foreach ($m in $missing) {
        if ($index.ContainsKey($m) -and -not $refs.Contains($index[$m])) { $refs.Add($index[$m]); $added++; Write-Host "  + reference $m" }
        elseif (-not $index.ContainsKey($m)) { Write-Host "  ! not in index: $m" -ForegroundColor Yellow }
    }
    if ($added -eq 0) { Write-Host $output -ForegroundColor Red; throw "Build failed" }
}
if (-not (Test-Path "$out\TcAgentBridge.dll")) { throw "Build failed" }
Copy-Item "$here\TcAgentBridge.pkgdef", "$here\extension.vsixmanifest" $out -Force
Copy-Item "$here\..\LICENSE" "$out\LICENSE.txt" -Force
Copy-Item "$here\..\docs\img\icon.png" "$out\icon.png" -Force
Write-Host "OK: $out\TcAgentBridge.dll  ($($refs.Count) references)" -ForegroundColor Green
