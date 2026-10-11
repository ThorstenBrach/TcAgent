# Builds TcAgent (MCP server for TwinCAT). Requirements: .NET 8 SDK, TwinCAT 3.1 (4024/4026), internet access for NuGet.
param([switch]$InteropOnly)   # only generate the interop (for build-vsix.ps1)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
Set-Location $here

# 1) Generate the interop assembly for the Automation Interface from the TwinCAT type library
$interop = Join-Path $here 'lib\Interop.TCatSysManagerLib.dll'
if (-not (Test-Path $interop)) {
    $tlbDir = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\TypeLib'
    $tlb = Get-ChildItem $tlbDir -Recurse -Filter TCatSysManager.tlb | Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
    if (-not $tlb) { throw "TCatSysManager.tlb not found under $tlbDir" }
    $tlbimp = Get-ChildItem 'C:\Program Files (x86)\Microsoft SDKs\Windows' -Recurse -Filter TlbImp.exe -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -notmatch 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tlbimp) { throw "TlbImp.exe not found (install the Windows SDK / .NET Framework 4.8 Developer Pack via the VS Installer)" }
    New-Item -ItemType Directory -Force -Path (Split-Path $interop) | Out-Null
    Write-Host "TlbImp: $($tlbimp.FullName)`nTLB:    $($tlb.FullName)"
    & $tlbimp.FullName $tlb.FullName /out:$interop /namespace:TCatSysManagerLib /nologo /silent
    if ($LASTEXITCODE -ne 0) { throw "TlbImp failed" }
}

if ($InteropOnly) { return }

# 2) Build
dotnet publish "$here\TcAgent.csproj" -c Release -r win-x64 --self-contained false -o "$here\bin\publish" 2>&1 | Tee-Object -FilePath "$here\build.log"
if ($LASTEXITCODE -ne 0) { throw "Build failed - see build.log" }
Write-Host "OK: $here\bin\publish\TcAgent.exe" -ForegroundColor Green
