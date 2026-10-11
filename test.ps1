# Smoke test without Claude: starts the server, sends initialize + tools/list + tc_instances and shows the responses.
$exe = Join-Path $PSScriptRoot 'bin\publish\TcAgent.exe'
$msgs = @(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}',
  '{"jsonrpc":"2.0","method":"notifications/initialized"}',
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"tc_instances","arguments":{}}}',
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"tc_attach","arguments":{}}}',
  '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"tc_tree","arguments":{"path":"TIPC","depth":3}}}'
)
$out = ($msgs -join "`n") | & $exe 2> (Join-Path $PSScriptRoot 'test.stderr.log')
$out | Set-Content (Join-Path $PSScriptRoot 'test.out.log') -Encoding UTF8
$out
