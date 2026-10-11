# Registers TcAgent as an MCP server in all installed AI clients and distributes the agent guide.
#   Claude Desktop, Claude Code, GitHub Copilot (Visual Studio + Copilot CLI), Gemini CLI, OpenAI Codex CLI (ChatGPT)
# Changes only the "tcagent" entry or the marked section; a backup *.bak-<time> is created before every change.
#
#   powershell -ExecutionPolicy Bypass -File .\setup-clients.ps1
#   powershell -ExecutionPolicy Bypass -File .\setup-clients.ps1 -SolutionDir C:\Projects\MyMachine
#
# -SolutionDir: additionally creates AGENTS.md, GEMINI.md, CLAUDE.md and .github\copilot-instructions.md in the project (for Git/team).
param(
    [string]$TcAgentExe = (Join-Path $PSScriptRoot 'bin\publish\TcAgent.exe'),
    [string]$Solution = $null,        # TCAGENT_SOLUTION (part of the solution path) - only needed to pin one specific instance
    [string]$AllowTargets = $null,    # TCAGENT_ALLOW_TARGETS, empty = keep existing value or default "local"
    [string]$SolutionDir = $null,
    [string]$Search = $null           # TCAGENT_SEARCH for tc_docs: searxng:<URL> or google:<API key>:<search engine ID>
)
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$utf8 = New-Object System.Text.UTF8Encoding $false
if (-not (Test-Path $TcAgentExe)) { throw "TcAgent.exe not found: $TcAgentExe (run build.ps1 first)" }
$TcAgentExe = (Resolve-Path $TcAgentExe).Path
$done = New-Object System.Collections.Generic.List[string]

function Backup($path) { if (Test-Path $path) { Copy-Item $path "$path.bak-$stamp" -Force } }
function Save-Text($path, [string]$text) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Backup $path
    [IO.File]::WriteAllText($path, $text, $utf8)
}
function Read-Json($path) {
    if (Test-Path $path) {
        $raw = [IO.File]::ReadAllText($path)
        if ($raw.Trim().Length -gt 0) { return $raw | ConvertFrom-Json }
    }
    return New-Object PSObject
}
function Set-Prop($obj, $name, $value) { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force }
function Save-Json($path, $obj) { Save-Text $path ($obj | ConvertTo-Json -Depth 30) }
function Has-Cmd($name) { return [bool](Get-Command $name -ErrorAction SilentlyContinue) }

# ------------------------------------------------------------------ Environment variables (from an existing Claude configuration)
$claudeConfigs = @()
$claudeConfigs += Get-ChildItem "$env:LOCALAPPDATA\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
if (Test-Path "$env:APPDATA\Claude") { $claudeConfigs += "$env:APPDATA\Claude\claude_desktop_config.json" }
$envVars = [ordered]@{}
foreach ($c in $claudeConfigs) {
    if (-not (Test-Path $c)) { continue }
    $j = Read-Json $c
    if ($j.mcpServers -and $j.mcpServers.tcagent -and $j.mcpServers.tcagent.env) {
        $j.mcpServers.tcagent.env.PSObject.Properties | ForEach-Object { if (-not $envVars.Contains($_.Name)) { $envVars[$_.Name] = [string]$_.Value } }
    }
}
# Do not carry over TCAGENT_SOLUTION: TcAgent finds the IDE itself (its own IDE in the chat, otherwise the only instance or a selection list).
# An old fixed value would lead to the wrong target in other projects.
if ($envVars.Contains('TCAGENT_SOLUTION')) { $envVars.Remove('TCAGENT_SOLUTION') }
if ($Solution) { $envVars['TCAGENT_SOLUTION'] = $Solution }
if ($AllowTargets) { $envVars['TCAGENT_ALLOW_TARGETS'] = $AllowTargets }
if ($Search) { $envVars['TCAGENT_SEARCH'] = $Search }
$envObj = New-Object PSObject
foreach ($k in $envVars.Keys) { Set-Prop $envObj $k $envVars[$k] }
Write-Host "TcAgent: $TcAgentExe"
Write-Host ("Environment: " + (($envVars.GetEnumerator() | ForEach-Object { if ($_.Key -eq 'TCAGENT_SEARCH') { "$($_.Key)=$(($_.Value -split ':')[0]):***" } else { "$($_.Key)=$($_.Value)" } }) -join '; '))

# ------------------------------------------------------------------ Claude Desktop
foreach ($c in ($claudeConfigs | Select-Object -Unique)) {
    $j = Read-Json $c
    if (-not $j.mcpServers) { Set-Prop $j 'mcpServers' (New-Object PSObject) }
    $e = New-Object PSObject; Set-Prop $e 'command' $TcAgentExe; Set-Prop $e 'args' @(); Set-Prop $e 'env' $envObj
    Set-Prop $j.mcpServers 'tcagent' $e
    Save-Json $c $j
    $done.Add("Claude Desktop: $c")
}

# ------------------------------------------------------------------ Claude Code
if (Has-Cmd 'claude') {
    $envArgs = @(); foreach ($k in $envVars.Keys) { $envArgs += @('-e', "$k=$($envVars[$k])") }
    # With 'Stop', Windows PowerShell 5.1 turns stderr of native programs into a terminating error -> relax it locally here
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $null = & claude mcp remove tcagent -s user 2>&1   # "No MCP server named ..." is normal the first time
        $out = & claude mcp add tcagent -s user @envArgs -- $TcAgentExe 2>&1
        if ($LASTEXITCODE -eq 0) { $done.Add("Claude Code: claude mcp add tcagent (user scope)") }
        else { Write-Warning "Claude Code: 'claude mcp add' failed: $out" }
    } finally { $ErrorActionPreference = $eap }
}

# ------------------------------------------------------------------ GitHub Copilot in Visual Studio (17.14 and later, agent mode)
$vsMcp = Join-Path $env:USERPROFILE '.mcp.json'
$j = Read-Json $vsMcp
if (-not $j.servers) { Set-Prop $j 'servers' (New-Object PSObject) }
$e = New-Object PSObject; Set-Prop $e 'type' 'stdio'; Set-Prop $e 'command' $TcAgentExe; Set-Prop $e 'args' @(); Set-Prop $e 'env' $envObj
Set-Prop $j.servers 'tcagent' $e
# Claude Code reads the same file as project configuration when started in the user folder,
# and requires 'mcpServers'. Empty key = valid for Claude, VS ignores it.
if (-not $j.mcpServers) { Set-Prop $j 'mcpServers' (New-Object PSObject) }
Save-Json $vsMcp $j
$done.Add("Copilot in Visual Studio: $vsMcp (select agent mode in Copilot Chat and enable the tcagent tools)")

# ------------------------------------------------------------------ GitHub Copilot CLI
$copilotDir = Join-Path $env:USERPROFILE '.copilot'
$copilotEntry = New-Object PSObject
Set-Prop $copilotEntry 'type' 'local'; Set-Prop $copilotEntry 'command' $TcAgentExe; Set-Prop $copilotEntry 'args' @()
Set-Prop $copilotEntry 'env' $envObj; Set-Prop $copilotEntry 'tools' @('*')
if ((Has-Cmd 'copilot') -or (Test-Path $copilotDir)) {
    $f = Join-Path $copilotDir 'mcp-config.json'
    $j = Read-Json $f
    if (-not $j.mcpServers) { Set-Prop $j 'mcpServers' (New-Object PSObject) }
    Set-Prop $j.mcpServers 'tcagent' $copilotEntry
    Save-Json $f $j
    $done.Add("Copilot CLI: $f")
}

# ------------------------------------------------------------------ Gemini CLI
$geminiDir = Join-Path $env:USERPROFILE '.gemini'
if ((Has-Cmd 'gemini') -or (Test-Path $geminiDir)) {
    $f = Join-Path $geminiDir 'settings.json'
    $j = Read-Json $f
    if (-not $j.mcpServers) { Set-Prop $j 'mcpServers' (New-Object PSObject) }
    # trust=true: no confirmation per tcagent tool - the guardrails (whitelist, modes, audit) live in TcAgent itself
    $e = New-Object PSObject; Set-Prop $e 'command' $TcAgentExe; Set-Prop $e 'args' @(); Set-Prop $e 'env' $envObj; Set-Prop $e 'trust' $true
    Set-Prop $j.mcpServers 'tcagent' $e
    Save-Json $f $j
    $done.Add("Gemini CLI: $f")
}

# ------------------------------------------------------------------ OpenAI Codex CLI (ChatGPT)
$codexDir = Join-Path $env:USERPROFILE '.codex'
if ((Has-Cmd 'codex') -or (Test-Path $codexDir)) {
    $f = Join-Path $codexDir 'config.toml'
    $toml = if (Test-Path $f) { [IO.File]::ReadAllText($f) } else { '' }
    # remove existing tcagent sections (including the .env subsection)
    $toml = [regex]::Replace($toml, '(?ms)^\[mcp_servers\.tcagent(\.env)?\]\s*$.*?(?=^\[(?!mcp_servers\.tcagent)|\z)', '')
    $block = "`n[mcp_servers.tcagent]`ncommand = '$TcAgentExe'`nargs = []`n"
    if ($envVars.Count -gt 0) {
        $block += "`n[mcp_servers.tcagent.env]`n"
        foreach ($k in $envVars.Keys) { $block += "$k = '$($envVars[$k])'`n" }
    }
    Save-Text $f ($toml.TrimEnd() + "`n" + $block)
    $done.Add("Codex CLI (ChatGPT): $f")
}

# ------------------------------------------------------------------ Files for the chat window in TwinCAT
$tcDir = Join-Path $env:LOCALAPPDATA 'TcAgent'
$m = New-Object PSObject; Set-Prop $m 'mcpServers' (New-Object PSObject)
$e = New-Object PSObject; Set-Prop $e 'command' $TcAgentExe; Set-Prop $e 'args' @(); Set-Prop $e 'env' $envObj
Set-Prop $m.mcpServers 'tcagent' $e
Save-Json (Join-Path $tcDir 'mcp-claude.json') $m
$m2 = New-Object PSObject; Set-Prop $m2 'mcpServers' (New-Object PSObject); Set-Prop $m2.mcpServers 'tcagent' $copilotEntry
Save-Json (Join-Path $tcDir 'mcp-copilot.json') $m2
$chatCfg = Join-Path $tcDir 'chat-backends.json'
if (-not (Test-Path $chatCfg)) {
    Save-Text $chatCfg @'
{
  "default": "claude",
  "showOnStartup": true,
  "sendContext": true,
  "historyChars": 6000,
  "backends": {
    "claude":  { "name": "Claude (Claude Code)",  "exe": "claude",  "args": "-p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config --mcp-config {mcpClaude} --allowedTools mcp__tcagent WebSearch", "stdin": true, "format": "claude-stream" },
    "copilot": { "name": "GitHub Copilot (CLI)",  "exe": "copilot", "args": "-p {prompt} --allow-tool tcagent --additional-mcp-config @{mcpCopilot}", "stdin": false },
    "gemini":  { "name": "Gemini (Gemini CLI)",   "exe": "gemini",  "args": "--allowed-mcp-server-names tcagent", "stdin": true },
    "codex":   { "name": "ChatGPT (Codex CLI)",   "exe": "codex",   "args": "exec --skip-git-repo-check -", "stdin": true }
  }
}
'@
    $done.Add("Chat window: $chatCfg (backends; paths/arguments can be adjusted here)")
}
else {
    # upgrade the old Claude default (without progress display) to the new one; custom changes are kept
    $t = [IO.File]::ReadAllText($chatCfg)
    $new = '"args": "-p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config --mcp-config {mcpClaude} --allowedTools mcp__tcagent WebSearch", "stdin": true, "format": "claude-stream"'
    $olds = @('"args": "-p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config --mcp-config {mcpClaude} --allowedTools mcp__tcagent", "stdin": true, "format": "claude-stream"', '"args": "-p --output-format stream-json --verbose --strict-mcp-config --mcp-config {mcpClaude} --allowedTools mcp__tcagent", "stdin": true, "format": "claude-stream"', '"args": "-p --allowedTools mcp__tcagent --mcp-config {mcpClaude}", "stdin": true')
    $hit = $olds | Where-Object { $t.Contains($_) } | Select-Object -First 1
    if ($hit) {
        Copy-Item $chatCfg "$chatCfg.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Save-Text $chatCfg ($t.Replace($hit, $new))
        $done.Add("Chat window: $chatCfg - Claude backend switched to a persistent session with live progress")
    }
    else { $done.Add("Chat window: $chatCfg left unchanged (already exists)") }
}

# ------------------------------------------------------------------ Agent guide (one source, four formats)
$guidePath = Join-Path $PSScriptRoot 'agents\TCAGENT_GUIDE.md'
if (Test-Path $guidePath) {
    $guide = [IO.File]::ReadAllText($guidePath)
    $blockText = "<!-- tcagent:begin (managed by setup-clients.ps1) -->`n$guide`n<!-- tcagent:end -->"
    function Merge-Guide($path) {
        $old = if (Test-Path $path) { [IO.File]::ReadAllText($path) } else { '' }
        $new = if ($old -match '(?s)<!-- tcagent:begin.*?<!-- tcagent:end -->') {
            [regex]::Replace($old, '(?s)<!-- tcagent:begin.*?<!-- tcagent:end -->', [System.Text.RegularExpressions.MatchEvaluator]{ param($x) $blockText })
        } else { ($old.TrimEnd() + "`n`n" + $blockText).TrimStart() }
        Save-Text $path ($new + "`n")
    }
    if (Test-Path $geminiDir) { Merge-Guide (Join-Path $geminiDir 'GEMINI.md'); $done.Add("Guide: $geminiDir\GEMINI.md") }
    if (Test-Path $codexDir) { Merge-Guide (Join-Path $codexDir 'AGENTS.md'); $done.Add("Guide: $codexDir\AGENTS.md") }
    if (Test-Path $copilotDir) { Merge-Guide (Join-Path $copilotDir 'copilot-instructions.md'); $done.Add("Guide: $copilotDir\copilot-instructions.md") }
    # Claude Code: as a user skill (loaded on demand, also in the chat window)
    $skillSrc = Join-Path $PSScriptRoot 'SKILL.md'
    if ((Has-Cmd 'claude') -and (Test-Path $skillSrc)) {
        $skillDst = Join-Path $env:USERPROFILE '.claude\skills\twincat-tcagent\SKILL.md'
        Save-Text $skillDst ([IO.File]::ReadAllText($skillSrc))
        $done.Add("Guide: $skillDst (Claude Code skill)")
    }
    if ($SolutionDir) {
        if (-not (Test-Path $SolutionDir)) { throw "SolutionDir not found: $SolutionDir" }
        foreach ($p in @('AGENTS.md', 'GEMINI.md', 'CLAUDE.md', '.github\copilot-instructions.md')) {
            Merge-Guide (Join-Path $SolutionDir $p); $done.Add("Guide in project: $SolutionDir\$p")
        }
    }
}
else { Write-Host "Note: $guidePath is missing - guides not distributed" -ForegroundColor Yellow }

Write-Host ""
Write-Host "Set up:" -ForegroundColor Green
$done | ForEach-Object { Write-Host "  - $_" }
Write-Host ""
Write-Host "CLIs that were not found are skipped. Then restart the clients (quit Claude Desktop completely, reload VS)."
