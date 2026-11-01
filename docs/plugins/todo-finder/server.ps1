# Example TcAgent plugin (docs/PLUGINS.md): a minimal MCP server over stdin/stdout in Windows PowerShell 5.1.
# One tool, find_todos: asks TcAgent to run tc_plc_search (request "tcagent/call") and returns the TODO/FIXME lines.
# The search runs with all of TcAgent's checks; this script never talks to the IDE or ADS itself.
#
# SPDX-License-Identifier: MIT
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$script:nextId = 0

function Send($msg) { [Console]::Out.WriteLine(($msg | ConvertTo-Json -Depth 20 -Compress)); [Console]::Out.Flush() }

function Reply($id, $result) { Send @{ jsonrpc = '2.0'; id = $id; result = $result } }

function Text($text, [bool]$isError = $false) { @{ content = @(@{ type = 'text'; text = $text }); isError = $isError } }

# runs a built-in TcAgent tool and waits for its answer (TcAgent sends nothing else while it works on our request)
function Invoke-TcAgent($name, $arguments) {
    $script:nextId++
    $id = "plugin-$script:nextId"
    Send @{ jsonrpc = '2.0'; id = $id; method = 'tcagent/call'; params = @{ name = $name; arguments = $arguments } }
    while ($true) {
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { exit 0 }
        $m = $line | ConvertFrom-Json
        if ($m.id -eq $id -and -not $m.method) {
            if ($m.error) { throw $m.error.message }
            return (($m.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n")
        }
    }
}

while ($null -ne ($line = [Console]::In.ReadLine())) {
    $m = $line | ConvertFrom-Json
    if ($null -eq $m.id) { continue }                  # notifications (initialized ...)
    switch ($m.method) {
        'initialize' {
            Reply $m.id @{ protocolVersion = '2025-06-18'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'todo-finder'; version = '1.0.0' } }
        }
        'ping' { Reply $m.id @{} }
        'tools/list' {
            Reply $m.id @{ tools = @(@{
                name = 'find_todos'
                description = 'Lists the TODO and FIXME comments of the PLC code (all PLC projects or below a tree path).'
                inputSchema = @{ type = 'object'; properties = @{ path = @{ type = 'string'; description = 'Only below this tree path (default: all PLC projects)' } } }
            }) }
        }
        'tools/call' {
            try {
                $search = @{ pattern = '\b(TODO|FIXME)\b'; regex = $true; part = 'all' }
                if ($m.params.arguments.path) { $search.path = [string]$m.params.arguments.path }
                $hits = Invoke-TcAgent 'tc_plc_search' $search
                Reply $m.id (Text ("Open TODO/FIXME comments:`n" + $hits))
            }
            catch { Reply $m.id (Text ("find_todos failed: " + $_.Exception.Message) $true) }
        }
        default { Send @{ jsonrpc = '2.0'; id = $m.id; error = @{ code = -32601; message = "Unknown method: $($m.method)" } } }
    }
}
