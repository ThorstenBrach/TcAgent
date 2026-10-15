# Sets up a GitHub Actions self-hosted runner for TwinCAT pipelines with TcAgent on this machine.
#
#   powershell -ExecutionPolicy Bypass -File .\setup-runner.ps1 -RepoUrl https://github.com/<owner>/<repo> -Token <registration token>
#
# The registration token: GitHub > repository > Settings > Actions > Runners > New self-hosted runner (valid for 1 hour).
# For an organization use -RepoUrl https://github.com/<org> and an organization runner token.
#
# What it does (as the CURRENT user - Visual Studio automation needs a user desktop, so the runner must not be a service):
#   1. TcAgent.exe install --path <TcAgentPath>     fixed path for the workflows (default C:\Tools\TcAgent)
#   2. TcAgent.exe doctor [--solution <sln>]        stops if the machine is not ready (-Force skips this)
#   3. downloads the latest GitHub runner (win-x64) to <RunnerDir> and registers it with the labels self-hosted, Windows,
#      X64, twincat (+ -Labels)
#   4. writes <RunnerDir>\.env with TCAGENT and TCAGENT_ALLOW_TARGETS - every job sees them as environment variables
#   5. creates a scheduled task that starts the runner at logon of this user (interactive, not as a service; run-tcagent.ps1
#      starts run.cmd with QuickEdit switched off, so a click into the window cannot pause a job)
#   6. -Start: starts the runner now in its own window
#
# Afterwards: on a dedicated build machine enable automatic logon for this user (Sysinternals Autologon or netplwiz),
# so the runner is back after a reboot. One runner per machine - a TwinCAT runtime runs one configuration at a time.
#
#   -Uninstall     removes the scheduled task and unregisters the runner (needs a removal token: -Token)
#
# SPDX-License-Identifier: MIT
param(
    [string]$RepoUrl,
    [string]$Token,
    [string]$RunnerDir = 'C:\actions-runner',
    [string]$TcAgentPath = 'C:\Tools\TcAgent',
    [string]$Name = $env:COMPUTERNAME,
    [string]$Labels = 'twincat',
    [string]$AllowTargets = 'local',
    [string]$Solution,
    [string]$TcAgentExe,
    [switch]$Force,
    [switch]$Start,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # PS 5.1: the progress bar makes Invoke-WebRequest very slow
$taskName = 'GitHub Actions Runner (TcAgent)'

function Step($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'TcAgent-runner.cmd') -Force -ErrorAction SilentlyContinue
    Write-Host "Autostart removed."
    if ((Test-Path "$RunnerDir\config.cmd") -and $Token) {
        Push-Location $RunnerDir
        try { & .\config.cmd remove --token $Token } finally { Pop-Location }
    } elseif (Test-Path "$RunnerDir\config.cmd") {
        Write-Host "To unregister the runner at GitHub: $RunnerDir\config.cmd remove --token <removal token>"
    }
    return
}

if (-not $RepoUrl -or -not $Token) { throw "Pass -RepoUrl https://github.com/<owner>/<repo> and -Token <registration token> (Settings > Actions > Runners > New self-hosted runner)." }
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($me.IsSystem) { throw "Run this as the user the runner should run as - not as SYSTEM." }
if (([Security.Principal.WindowsPrincipal]$me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Note: running elevated. The runner itself will run as $($me.Name) without elevation at logon." -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 1. TcAgent at a fixed path
Step "TcAgent -> $TcAgentPath"
if (-not $TcAgentExe) {
    # newest version installed by the VSIX (%LOCALAPPDATA%\TcAgent\app\<version>), else a source build next to this script
    $app = Join-Path $env:LOCALAPPDATA 'TcAgent\app'
    $TcAgentExe = Get-ChildItem $app -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'TcAgent.exe') } |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'TcAgent.exe' }
    if (-not $TcAgentExe -and (Test-Path "$PSScriptRoot\bin\publish\TcAgent.exe")) { $TcAgentExe = "$PSScriptRoot\bin\publish\TcAgent.exe" }
}
if (-not $TcAgentExe -or -not (Test-Path $TcAgentExe)) {
    throw "TcAgent.exe not found - install the VSIX and start Visual Studio 2022 once, or pass -TcAgentExe <path>."
}
& $TcAgentExe install --path $TcAgentPath
if ($LASTEXITCODE -ne 0) { throw "TcAgent install failed (exit $LASTEXITCODE)" }
$tc = Join-Path $TcAgentPath 'TcAgent.exe'

# ---------------------------------------------------------------- 2. doctor
Step "Checking this machine (TcAgent doctor)"
$docArgs = @('doctor')
if ($Solution) { $docArgs += @('--solution', $Solution) }
& $tc @docArgs
if ($LASTEXITCODE -ne 0) {
    if ($Force) { Write-Host "doctor reports problems - continuing because of -Force" -ForegroundColor Yellow }
    else { throw "This machine is not ready yet (see above). Fix it, or use -Force to set up the runner anyway." }
}

# ---------------------------------------------------------------- 3. GitHub runner
Step "GitHub Actions runner -> $RunnerDir"
New-Item -ItemType Directory -Force -Path $RunnerDir | Out-Null
if (-not (Test-Path "$RunnerDir\config.cmd")) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $rel = Invoke-RestMethod 'https://api.github.com/repos/actions/runner/releases/latest' -Headers @{ 'User-Agent' = 'TcAgent-setup-runner' }
    $asset = $rel.assets | Where-Object { $_.name -like 'actions-runner-win-x64-*.zip' } | Select-Object -First 1
    if (-not $asset) { throw "No win-x64 runner package in $($rel.tag_name)" }
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host "Downloading $($asset.name) ..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $RunnerDir)
    Remove-Item $zip -Force
}

if (Test-Path "$RunnerDir\.runner") {
    # config.cmd refuses to configure an already configured runner (--replace only replaces a runner of the same name at GitHub)
    Write-Host "Runner is already configured - registration skipped. To re-register: -Uninstall -Token <removal token>, then run this again." -ForegroundColor Yellow
} else {
    Push-Location $RunnerDir
    try {
        & .\config.cmd --unattended --url $RepoUrl --token $Token --name $Name --labels $Labels --work _work --replace
        if ($LASTEXITCODE -ne 0) { throw "Runner registration failed (exit $LASTEXITCODE) - is the token still valid (1 hour)?" }
    } finally { Pop-Location }
}

# ---------------------------------------------------------------- 4. environment for the jobs
Step "Environment for the jobs ($RunnerDir\.env)"
$envFile = Join-Path $RunnerDir '.env'
$lines = @()
if (Test-Path $envFile) { $lines = @(Get-Content $envFile | Where-Object { $_ -notmatch '^(TCAGENT|TCAGENT_ALLOW_TARGETS)=' }) }
$lines += "TCAGENT=$tc"
$lines += "TCAGENT_ALLOW_TARGETS=$AllowTargets"
Set-Content -Path $envFile -Value $lines -Encoding ASCII
Get-Content $envFile | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------- start script without QuickEdit
# A click into a console window with QuickEdit starts "Select" mode and Windows pauses the program at its next output -
# the job seems to hang. run-tcagent.ps1 switches QuickEdit off for the runner's window and then starts run.cmd.
$startScript = Join-Path $RunnerDir 'run-tcagent.ps1'
Set-Content -Path $startScript -Encoding ASCII -Value @'
# Starts the GitHub Actions runner with QuickEdit switched off (written by TcAgent setup-runner.ps1)
try {
    Add-Type -Namespace TcAgent -Name Console -MemberDefinition @"
[DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int handle);
[DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr handle, out uint mode);
[DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr handle, uint mode);
"@
    $h = [TcAgent.Console]::GetStdHandle(-10)
    $mode = [uint32]0
    if ([TcAgent.Console]::GetConsoleMode($h, [ref]$mode)) {
        [void][TcAgent.Console]::SetConsoleMode($h, [uint32](($mode -band 0xFFFFFFBF) -bor 0x80))   # QuickEdit off (0x40), extended flags (0x80)
    }
} catch { }
$host.UI.RawUI.WindowTitle = 'GitHub Actions Runner (TcAgent) - do not click into this window'
& (Join-Path $PSScriptRoot 'run.cmd')
'@
$startArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$startScript`""

# ---------------------------------------------------------------- 5. autostart at logon (interactive, not a service)
Step "Autostart at logon of $($me.Name)"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $startArgs -WorkingDirectory $RunnerDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $me.Name
$principal = New-ScheduledTaskPrincipal -UserId $me.Name -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Scheduled task '$taskName' starts the runner when $($me.Name) logs on."
} catch {
    # a logon trigger usually needs an elevated PowerShell - fall back to the user's Startup folder (no admin needed)
    $startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'TcAgent-runner.cmd'
    Set-Content -Path $startup -Value "@start `"GitHub Actions Runner`" /D `"$RunnerDir`" powershell.exe $startArgs" -Encoding ASCII
    Write-Host "Scheduled task not created ($($_.Exception.Message)) - using $startup instead (run elevated for the scheduled task)." -ForegroundColor Yellow
}

if ($Start) {
    Step "Starting the runner"
    Start-Process powershell.exe -ArgumentList $startArgs -WorkingDirectory $RunnerDir
}

Write-Host ""
Write-Host "Done. Runner '$Name' with labels: self-hosted, Windows, X64, $Labels" -ForegroundColor Green
Write-Host "Workflows: runs-on: [self-hosted, windows, twincat] and call `$env:TCAGENT (see docs/pipeline/github-actions.yml)."
Write-Host "Dedicated build machine: enable automatic logon for $($me.Name) (Sysinternals Autologon), so the runner survives a reboot."
if (-not $Start) { Write-Host "Start it now: $RunnerDir\run-tcagent.ps1 (right-click > Run with PowerShell) or log off and on again" }
