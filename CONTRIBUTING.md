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
  `src/Policy.cs` as `ProjectWrite` or `RuntimeWrite` so guardrails and audit apply, and add it to its module in
  `src/Modules.cs` (a test checks that every tool has one). Tools that need no change of TcAgent itself can be a plugin
  instead ([docs/PLUGINS.md](docs/PLUGINS.md)).
* **Descriptions are part of the interface:** agents read them. Short, precise, with an example path.
* **Keep docs in sync:** `agents/TCAGENT_GUIDE.md` and `SKILL.md` (same content), `docs/TOOLS.md`, and add a line to
  `CHANGELOG.md` under "Unreleased".
* **Versions:** server (`src/Program.cs`), extension (`vsbridge/TcAgentBridge.cs`, `extension.vsixmanifest`,
  `TcAgentBridge.pkgdef`) carry the same version number.
* No Beckhoff binaries and no generated interop assemblies in the repository.
* Test against a project without connected hardware.
* Unit tests for the file logic (no TwinCAT needed): `dotnet test tests\TcAgent.Tests`. The test project links the
  sources it tests from `src/` (the server project itself needs the TwinCAT interop).

## Windows and dialogs

Every window and dialog of the extension follows **[docs/UI_GUIDELINES.md](docs/UI_GUIDELINES.md)**: real tabs and a ⋯
menu; 4-6 main actions with icon and text, the rest in the context menu / More ▾; master-detail with a detail header;
states as symbol and word, red only for errors; long actions never on the UI thread; success in the info bar, errors as
a dialog; buttons only enabled when they fit; the keys Enter/double-click, F2, Del, Ctrl+C, F5, Ctrl+F; empty states that
explain; check boxes for choices that take effort; safety-relevant things visible (scripts, gate cards); one vocabulary;
no user, computer or project names; VS theme colors, light and dark; narrow windows without cut-off texts. **A new window
starts with a mockup and its approval.** Reference code: `vsbridge/KnowledgeWindow.cs`, `TcUnitWindow.cs`, `ChatView.cs`.
