# TcUnit (bundled)

Unit test framework for TwinCAT 3 - https://github.com/tcunit/TcUnit, MIT license (see `LICENSE` in this folder).

TcAgent ships the library unchanged in `<version>\TcUnit.library`. It is installed into the TwinCAT library repository
only when TcUnit is missing there or older than this version:

* MCP tool: `tc_library action=ensure_tcunit` (installs if needed and adds the reference to the PLC project)
* command line: `TcAgent.exe tcunit ensure --solution <sln> [--plc <name>]` (installs if needed, reports the reference)

To ship a newer TcUnit, put its `.library` into a new folder named after its version (e.g. `1.4.0\TcUnit.library`)
and remove the old folder. TcAgent always takes the highest version folder.
