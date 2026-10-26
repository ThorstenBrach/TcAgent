# Contributing

Contributions are welcome – bug reports, experience with other TwinCAT versions, new tools.
Docs and issues are in English; issues and pull requests in German are welcome too.

## Reporting bugs, requests and questions

* **Bugs and feature requests:** the issue forms of the repository. They ask for a **Diagnostics** block – in the IDE
  the chat window menu **⋯ > Copy diagnostics**, without the IDE `TcAgent.exe diag` (versions only, no names or paths).
* **Questions:** the GitHub Discussions, category Q&A.
* **Security problems:** privately, see [SECURITY.md](SECURITY.md) – not as a public issue.
* Helpful: an excerpt from `%LOCALAPPDATA%\TcAgent\audit.jsonl` or `bridge\bridge.log` (without confidential content).

## Agent feedback

Agents report problems themselves via `tc_feedback` to `feedback/feedback.jsonl`. The knowledge window (tab Feedback)
shows them, anonymizes the text and opens a prepared issue – you check and send it yourself. They show where tools or
descriptions need improvement.

## Development

* **One language:** C#. `src/` is .NET 8, `vsbridge/` is .NET Framework 4.8 (Visual Studio extension; the same sources
  are compiled for the XAE Shell by `vsbridge/install-xaeshell.ps1` - stay compatible with the VS 2017 shell API).
* **New tool:** register it in `src/ToolRegistry.cs` or one of its partial files (`Add(...)`); classify write tools in
  `src/Policy.cs` as `ProjectWrite` or `RuntimeWrite` so guardrails and audit apply.
* **Descriptions are part of the interface:** agents read them. Short, precise, with an example path.
* **Keep docs in sync:** `agents/TCAGENT_GUIDE.md` and `SKILL.md` (same content), `docs/TOOLS.md`, and add a line to
  `CHANGELOG.md` under "Unreleased".
* **Versions:** server (`src/Program.cs`), extension (`vsbridge/TcAgentBridge.cs`, `extension.vsixmanifest`,
  `TcAgentBridge.pkgdef`) carry the same version number.
* No Beckhoff binaries and no generated interop assemblies in the repository.
* Test against a project without connected hardware.
* Unit tests for the file logic (no TwinCAT needed): `dotnet test tests\TcAgent.Tests`. The test project links the
  sources it tests from `src/` (the server project itself needs the TwinCAT interop).
