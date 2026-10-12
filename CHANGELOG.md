# Changelog

All notable changes to TcAgent (MCP server `TcAgent.exe` and the Visual Studio extension TcAgentBridge).
Versions follow [Semantic Versioning](https://semver.org/); server and extension share one version number.

## [Unreleased]

## [0.19.0] – not released yet

### Added
- **Program documentation** – `tc_doc` explains a PLC project: call hierarchy (task -> program -> FBs, methods,
  functions), call graph, class diagram, step chains (SFC and CASE state machines), description per object, findings
  (unused POUs, very long POUs, global coupling) – as text for the agent and as one HTML page. The agent's explanations
  are kept as project memory (`.tcagent/doc-summaries.json`); pipeline command `TcAgent.exe doc`.
- **SFC step chains** – `tc_sfc` reads a sequential function chart as text (steps, actions, transitions, alternative and
  parallel branches, jumps) and writes it back from text (through PLCopen XML).
- **TwinCAT Package Manager** – `tc_packages` (installed, available, versions, outdated, details, feeds, settings) and
  `tc_packages_change` (install, upgrade, uninstall, repair, feeds; confirmed by the user, runs elevated).
- **Real-time cores** – `tc_realtime` shows and sets cores for TwinCAT, isolated cores, real-time cores, base time, load
  limit; `tc_task cores=...` assigns a task to a core.
- **CoE startup list** – `tc_coe` lists, adds and removes startup parameters of EtherCAT devices.
- `tc_goto_definition` also works without an open editor (POUs, DUTs, GVLs and their members via the project tree).
- Log file `%LOCALAPPDATA%\TcAgent\logs\tcagent-<date>.log` (all TcAgent processes, every tool call with duration) -
  to trace problems afterwards, e.g. after an IDE crash.

### Changed
- Read-only actions of changing tools (`action=list/show/info/read`, e.g. `tc_task list`, `tc_scope list`, `tc_coe list`)
  count as reading: no confirmation, allowed in the `ask`/`propose` modes, not in the audit log.
- `install-xaeshell.ps1` also installs into the 64-bit XAE Shell (VS 2022 base).

### Fixed
- TwinCAT XAE Shell (32-bit): before a project is unloaded and reloaded for a Scope/HMI change, TcAgent checks the shell's
  memory and refuses the step when it is close to the 32-bit limit - reloading then crashed the shell (OutOfMemoryException).

## [0.18.0] – 2026-10-12

### Added

- **CI pipeline mode** – `TcAgent.exe check | build | test | analyze | library | archive | scope` without an AI:
  exit codes (0 OK, 1 failed, 2 usage, 3 infrastructure), JUnit XML, annotations for GitHub Actions and Azure DevOps,
  TcUnit results in chronological order, SARIF for static analysis. Ready-made definitions for GitHub Actions,
  Azure DevOps, Jenkins and GitLab in `docs/pipeline/` ([docs/PIPELINE.md](docs/PIPELINE.md)).
- **IDE dialog guard** – modal dialogs of the IDE (fatal error of the target, license, save prompts) are detected;
  tools report the dialog text instead of hanging, the pipeline closes them (cancel only). New tool `tc_dialogs`.
- **Confirmations** (`TCAGENT_CONFIRM`) – runtime actions (activate, PLC start/stop, ADS write, runtime mode, routes)
  are confirmed by the user: Yes/No dialog in Visual Studio for the TcAgent Chat, MCP elicitation in other clients.
  A missing answer counts as "no".
- **Instance selection** – with several open TwinCAT solutions the client shows a selection list (MCP elicitation);
  the TcAgent Chat always works with its own IDE.
- **I/O scan** – `tc_runtime_mode` (config/run), `tc_io_scan` (adapters and EtherCAT masters, read-only),
  `tc_io_scan_add` (create the device or assign the adapter).
- **Motion, first step** – `tc_motion`, `tc_motion_add`: NC tasks and axes.
- **More of the Automation Interface** – `tc_new_solution`, `tc_plc_project_add`, `tc_export`/`tc_import`
  (PLCopen XML, XTI), `tc_library` (references, placeholders, repositories, install), `tc_library_save`,
  `tc_static_analysis` (TE1200, SARIF), `tc_mapping_export`/`tc_mapping_import`, `tc_archive`,
  `tc_twincat_version`, `tc_platform`, `tc_variant`, `tc_route_search`, `tc_route_add`, `tc_tccom`.
- **TwinCAT Scope** (TE130x) – `tc_scope_new`, `tc_scope`: list, `add_config` (configuration for an empty
  measurement project), `add_chart`, `add_axis`, `add_channel` (type read via ADS), `remove_channel`,
  `record_start`/`record_stop`, export to CSV, SVDX, BIN, TDMS, DAT; also as pipeline command `scope`.
  Unsaved recordings are backed up before a configuration change.
- **TwinCAT HMI** (TE2000), file based – `tc_hmi` (info, read, controls, build), `tc_hmi_page` (views and contents),
  `tc_hmi_control` (add/update/remove controls, bindings, button actions), `tc_hmi_symbol` (map PLC variables,
  ADS runtimes).
- **TcAgent Chat, new look** – your prompts right, the agent's answers left in bubbles, Markdown (headings, lists,
  tables, code blocks, links), tool calls as compact lines, Enter sends / Shift+Enter new line, prompt history with
  ↑/↓ (kept across sessions), short editor context in the prompt bubble.
- **Documents in the chat** – paste (Ctrl+V), drop or attach (+) files, screenshots and long texts; text files and
  Word documents go into the prompt, PDFs and images are read by the agent from the attachments folder.
- **TwinCAT XAE Shell** – chat window and inline question also in the XAE Shell: `vsbridge\install-xaeshell.ps1`.
- `CHANGELOG.md`.

### Changed

- Scope and HMI changes are written while the project is unloaded in the IDE and reloaded afterwards (the IDE would
  otherwise overwrite them); open editors of the project are saved first, BOM and formatting of the files are kept,
  backups go to `%LOCALAPPDATA%\TcAgent\backup\`.
- All messages, UI texts and comments in English.
- `tc_static_analysis` shows errors and warnings only.
- `tc_hmi info` and `tc_hmi_symbol list` show mapped PLC symbols only (server functions hidden).
- 73 tools (was 46).

### Fixed

- TcCOM module list (COM collection enumeration).
- Project unload/reload when another editor (e.g. Scope, HMI designer) has the focus.
- HMI control package found in `<solution>\Packages` (from the project's `tsconfig.json`).
- Clear message when the setup cannot replace `TcAgent.exe` because an AI client still uses it.
- No automatic reconnect within 30 s after the IDE crashed or was closed - queued changes no longer continue in a freshly
  restarted IDE.

## [0.13.0] – 2026-10-11

First public version: MCP server for Visual Studio 2022 / TwinCAT XAE with PLC code (read, write, create, implement
interfaces), navigation (go to definition, references, search), library signatures (also compiled libraries), check,
build, TcUnit results, I/O and tasks, runtime control via ADS with guardrails (allowed targets, modes, audit log),
coding rules, documentation sources, agent feedback, the TcAgent Chat window and the inline question (Ctrl+Shift+Q),
one-click VSIX installation with client setup for Claude, GitHub Copilot, Gemini and Codex.

[Unreleased]: https://github.com/ThorstenBrach/TcAgent/compare/v0.19.0...HEAD
[0.19.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.18.0...v0.19.0
[0.18.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.13.0...v0.18.0
[0.13.0]: https://github.com/ThorstenBrach/TcAgent/releases/tag/v0.13.0
