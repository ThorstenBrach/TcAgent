TcAgentBridge - VS 2022 extension for TcAgent
=============================================
Runs inside the Visual Studio process and answers TcAgent requests over the named pipe
"TcAgentBridge.<pid>":  ping, context (cursor/selection in the TwinCAT editor), probe (diagnostics),
libs (installed libraries), lib_export (library signatures).
The extension is read-only; it changes neither the project nor libraries.

Install
  1. powershell -ExecutionPolicy Bypass -File .\build.ps1          (normal user)
  2. Close VS 2022, then AS ADMINISTRATOR:
     powershell -ExecutionPolicy Bypass -File .\install.ps1
  3. Start VS 2022 with the TwinCAT solution
Log: %LOCALAPPDATA%\TcAgent\bridge\bridge.log   Probe: ...\bridge\probe.txt
Cache: %LOCALAPPDATA%\TcAgent\libcache\<Library>_<Version>.json
Uninstall: run uninstall.ps1 as administrator.

TwinCAT XAE Shell (isolated VS shell, 32 bit)
  powershell -ExecutionPolicy Bypass -File .\install-xaeshell.ps1             (close the shell first; asks for admin rights)
  Builds against the shell's own assemblies and copies the extension to <shell>\Common7\IDE\Extensions\TcAgent.
  Remove: install-xaeshell.ps1 -Uninstall
