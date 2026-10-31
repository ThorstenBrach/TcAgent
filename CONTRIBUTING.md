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

## Windows and dialogs

Every tool window and dialog of the extension follows the same rules - the knowledge window
(`vsbridge/KnowledgeWindow.cs`) is the reference and has the helpers for them.

* **Layout:** real tabs on top (a badge counts what needs attention there); context, refresh (F5) and a ⋯ menu for rare
  and global actions on the right. Per tab: heading with a short explanation, the main action first and highlighted,
  filters in their own row, the list, then the details of the selection (facts, the fitting actions as links, long
  content folded). The status line shows the last message on the left and what the tab holds on the right.
* **Actions:** one list of actions per list - toolbar and context menu show the same entries with the same names in the
  same order (New … / Add existing … / Open / Open folder / Edit … / Rename … / Move to level … / Switch off ▾ · Switch on /
  Delete … / Copy path). "…" only when a dialog follows, "▾" for a drop-down, rare actions under "More ▾". A button is
  only enabled when it fits the selection. Double-click and Enter open, Del deletes, Ctrl+F goes to the search box.
* **Feedback:** every action answers - success and hints in the status line or the info bar, errors always as a dialog;
  no dialog only to say "done". Long texts in a confirmation (a diff, a plan) scroll. Calls of TcAgent.exe run in the
  background (`await Cli(...)`) with the progress bar - never wait on the UI thread.
* **States and texts:** a state is a symbol and a word (✓ applies, ⊘ switched off, ✎ changed locally, ⚠ not reachable …),
  never a color alone; empty lists say what they are for and how to fill them; numbers with too little data show "—".
  Plain words over jargon; the same terms as in the User Guide.
* **Dialogs:** `Dialog(...)` (colors of the IDE), OK/Cancel at the bottom right, focus in the first required field,
  checked while typing (OK stays off until the input is valid, the reason below the field), long lists with a filter,
  folders and files with Browse …. Several items are chosen with **check boxes** (ticks stay when the filter changes;
  Space ticks the selected line), never with Ctrl/Shift selection - one click beside it would lose everything.
