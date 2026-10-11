# Baut TcAgent (MCP-Server fuer TwinCAT). Voraussetzung: .NET 8 SDK, TwinCAT 3.1 (4024/4026), Internet fuer NuGet.
param([switch]$InteropOnly)   # nur die Interop erzeugen (fuer build-vsix.ps1)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
Set-Location $here

# 1) Interop-Assembly fuer das Automation Interface aus der TwinCAT-Typbibliothek erzeugen
$interop = Join-Path $here 'lib\Interop.TCatSysManagerLib.dll'
if (-not (Test-Path $interop)) {
    $tlbDir = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\TypeLib'
    $tlb = Get-ChildItem $tlbDir -Recurse -Filter TCatSysManager.tlb | Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
    if (-not $tlb) { throw "TCatSysManager.tlb nicht gefunden unter $tlbDir" }
    $tlbimp = Get-ChildItem 'C:\Program Files (x86)\Microsoft SDKs\Windows' -Recurse -Filter TlbImp.exe -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -notmatch 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tlbimp) { throw "TlbImp.exe nicht gefunden (Windows SDK / .NET Framework 4.8 Developer Pack im VS-Installer nachinstallieren)" }
    New-Item -ItemType Directory -Force -Path (Split-Path $interop) | Out-Null
    Write-Host "TlbImp: $($tlbimp.FullName)`nTLB:    $($tlb.FullName)"
    & $tlbimp.FullName $tlb.FullName /out:$interop /namespace:TCatSysManagerLib /nologo /silent
    if ($LASTEXITCODE -ne 0) { throw "TlbImp fehlgeschlagen" }
}

if ($InteropOnly) { return }

# 2) Bauen
dotnet publish "$here\TcAgent.csproj" -c Release -r win-x64 --self-contained false -o "$here\bin\publish" 2>&1 | Tee-Object -FilePath "$here\build.log"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen - siehe build.log" }
Write-Host "OK: $here\bin\publish\TcAgent.exe" -ForegroundColor Green
