# Changelog

All notable changes to TcAgent (MCP server `TcAgent.exe` and the Visual Studio extension TcAgentBridge).
Versions follow [Semantic Versioning](https://semver.org/); server and extension share one version number.

## [Unreleased]

## [0.23.1] – 2026-10-14

### Added
- **Build server / runner** – `TcAgent.exe doctor [--solution <sln>]` checks whether a machine and user can run TcAgent
  pipelines (desktop session instead of a service, Visual Studio / XAE Shell, TwinCAT XAE, interop, runtime via ADS, TcUnit,
  git; with `--solution` an end-to-end open of the solution) and says what to fix; `TcAgent.exe install --path <folder>`
  copies TcAgent with its interop to a fixed path; `setup-runner.ps1` sets up a GitHub Actions self-hosted runner for
  TwinCAT (install, doctor, download + registration with label `twincat`, `.env` for the jobs, start at logon instead of a
  service). The pipeline templates start with `doctor`.
- CLI (all commands with `--solution`, and `doctor`): dialogs of the hidden IDE are now watched already while the solution
  loads - reported with their text and closed (Cancel) - and after a load timeout the hidden IDE started by the command is
  ended instead of being left running. `doctor` also looks for TcUnit and licenses in the TwinCAT 4026 folders
  (`%ProgramData%\Beckhoff\TwinCAT\3.1`).
- **User guide** – `docs/USER_GUIDE.md`: every feature explained by task, with example prompts, what the agent does,
  options, limits and what needs your confirmation; CI pipeline, guardrails, configuration reference and FAQ; screenshots
  of the program documentation and animations of linking and risk rules from the test project.
- README: overview graphic of the files on company, personal and project level (rules, risk rules, lessons, documentation
  sources, manual templates, program memory, feedback); architecture graphic updated.
- **SIL / HIL test runs** – `TcAgent.exe sil` runs TcUnit on the runtime of the build PC (Run-mode check, PLC login/start
  fallback, optional Scope recording, clean-up that always runs: runtime to Config mode, target, I/O and autostart
  restored); `TcAgent.exe hil` does the same on a remote test PLC, only with explicit allowance (`TCAGENT_ALLOW_TARGETS`
  or `--allow`), with ADS route check and broadcast-search diagnostics. Library and its tests as two PLC projects in one
  solution: docs and ready-made pipelines (`docs/pipeline/library-*.yml`).
- **Risk rules** – `risk-rules.md` files configure the risk analysis like the coding rules (company `TCAGENT_RISK_RULES`, personal
  `%LOCALAPPDATA%\TcAgent\risk-rules.md`, project `<solution>\.tcagent\risk-rules.md`; later wins): switch off built-in rules or change
  their severity and thresholds, own text rules (`find`/`require` regex with `unless` (checked on the whole statement), `kinds`, `objects`, `in`, `comments`), and checks
  the agent verifies by reading the code. New tool `tc_risk_rules` (`show`, `test` on the project without saving, `add`, `builtin`,
  `check`); template `docs/risk-rules.example.md`; used by `tc_doc action=risks`, the HTML page and `TcAgent.exe doc --risks`.
- **Risk analysis** – `tc_doc action=risks`: static analysis of the ST code with 13 documented rules (division without
  zero check, unguarded array index and pointer dereference, loops without exit, globals written in several tasks, timers
  in methods, CASE without ELSE …); "Risks" section in the HTML page, counts in the outline; `TcAgent.exe doc --risks
  [--fail-on high|medium|low]`.
- `tc_doc`: tasks from the PLC task references with cycle time and priority; finding "PLC project has no task reference".
- **TwinSAFE, read only** – `tc_safety`: safety projects, groups, alias devices, connections (FSoE address, watchdog),
  safety parameters, CRCs and safety terminals. Writing TwinSAFE configuration is intentionally not supported; all write
  tools refuse the TwinSAFE subtree.
- **Open in editor** – `tc_editor_open` opens a PLC object in the IDE editor at a declaration/implementation line;
  `tc_goto_definition open=true` jumps there like F12.
- `tc_sfc action=renumber` (systematic step names, marked steps, jump targets, step references and embedded actions
  renamed), `action=comments` (step comments following the coding guidelines), `action=flags` (SFCInit, SFCReset …,
  always declared); new steps get "Duplicate on copy".
- `tc_link`: a PLC struct/array is linked to a PDO group in one step, element by element (`dryRun` lists the pairs).
- `tc_motion action=link`: links an NC axis to its drive, with read-back check.
- `tc_feedback`: stable ids, status, machine; shared team folder (`TCAGENT_FEEDBACK`); `action=list|summary|resolve`;
  `TcAgent.exe feedback --open --markdown out.md`.
- `tc_policy action=audit_check`: changing calls from the daily log that are missing in the audit log, with the likely reason.

### Changed
- TcAgent Chat: shows the TcAgent version at the top right - in orange "server x ≠ extension y" when the chat still starts an
  older TcAgent.exe; the backend chosen last (Claude, Copilot, Gemini, Codex) is selected again on the next start.
- TwinCAT XAE Shell: the bridge takes the server (`TcAgent.exe`) from the VSIX of the same version installed in Visual Studio
  2022 and registers it - before, the shell's chat kept running the old server until Visual Studio 2022 was started.
- Uniform file names on every level (company, personal, project): `<topic>.md` plus an optional folder `<topic>\` for more
  files – `coding-rules\` (formerly `rules\`, still read), `risk-rules\` now also personal, documents for `tc_docs` in
  `knowledge\` (formerly `docs\`, still read).
- VSIX: a rebuild with the same version number is copied too (all files compared, not only the size of `TcAgent.exe`).
- After a deliberate IDE restart (IDE already running for a while) TcAgent reconnects on the next call.
- `tc_editor_open`: search tolerant to equal node names and lazily loaded nodes; falls back to opening the object's file;
  the error names the Solution Explorer nodes that were found. The cursor is set in the TwinCAT text editor itself;
  actions and transitions (no declaration part) get the cursor at their implementation line.
- Agent guide (SKILL.md, agents/TCAGENT_GUIDE.md): new section "Coding rules" - sources and precedence, why project
  rules live in the solution, applying and adding rules, difference to the guide and other knowledge sources; I/O linking
  order with task reference and the EtherCAT sync dialog; VS message boxes where only the title is visible.
- `tc_refactor apply`: when the AI client could not show the question (e.g. Claude Code as backend of the TcAgent Chat, which
  answers at once) and no IDE dialog was reachable, the change was reported as "cancelled by the user". Now only a real
  answer counts; otherwise nothing is applied and the result says that the confirmation could not be shown and why
  (e.g. TcAgentBridge not installed in the TwinCAT XAE Shell). Runtime confirmations: the same case is reported as
  "could not be shown" instead of "rejected by the user" (the action is still not executed).
- Program documentation: transition conditions with comparisons (`_pos >= _target`) broke the step chain diagram
  (Mermaid read the HTML-escaped `>` as a statement end and drew stray states); conditions now use `≥ ≤ ≠ ＞ ＜`.
- `tc_plc_search` with `path` = one object (e.g. a FB) also searches that object itself, not only its methods/actions.
- `tc_build`: the INFO messages "device … already exists in file …" (duplicate ESI device descriptions of the installation,
  100+ per build) are summed up in one line.
- `tc_refactor apply` runs as a job: after `waitSeconds` (default 45) it returns "still waiting – k of n done" with a job id,
  `action=result` returns the report - for AI clients that reach the PC through a bridge with a call timeout.
- `tc_realtime show`: available cores, cores left to Windows and the other real-time settings.
- `tc_editor_context`: the tree path comes from the active document's file (no mix-up of same-named methods); a selection
  ending at column 1 of the next line counts as whole lines. `tc_goto_definition` resolves library types at project level
  also while the chat window has the focus.
- HMI tools: `reload=false`; memory check also before the reload; a lost IDE during unload/reload gives a clear message.
- Audit, log and feedback files are appended with shared access and retries (several TcAgent processes); audit entries
  include pid, client and the action-based class; `tc_lessons` add/remove counts as a project change.

## [0.20.4] – 2026-10-13

### Added
- `tc_task action=assign`: PLC task reference and program call (process image) - feedback from agents.
- `tc_delete` writes a backup before deleting (PLCopen XML / ProduceXml in `<solution>\.tcagent\backup\delete`).
- `tc_sfc`: `EMBEDDED <action>` and `COMMENT <text>` lines under steps; embedded actions follow a renamed step.
- **User manual** – `tc_userdoc`: manual of FBs, functions, interfaces and the structs/enums they use, in the style and
  format of example documents you provide (Markdown, AsciiDoc or HTML); missing comments are explained from the code.
- **Refactoring** – `tc_refactor`: meaningful names and comments for the marked range or the clicked method, FB, function,
  struct or enum; every change is confirmed (Apply / Apply all / Reject / Cancel); renames update all usages (cross
  references) and warn about I/O links, persistent data and HMI bindings.
- **Program documentation**: short description for every FB, method, property and function (`tc_doc action=pending` /
  `save`), inheritance hierarchy (diagram and tree, library base classes included), descriptions in the call hierarchy.
- **Lessons learned** – `tc_lessons`: the agent saves hard-won solutions (what the problem was, why, how it was solved,
  what did not work) in a project, personal or team file and searches them; matching lessons are appended automatically
  when a tool fails or a build reports errors, and the agent is reminded to save one after repeated failed attempts.
- **Program documentation** – `tc_doc` explains a PLC project: call hierarchy (task -> program -> FBs, methods,
  functions), call graph, class diagram, step chains (SFC and CASE state machines), description per object, findings
  (unused POUs, very long POUs, global coupling) – as text for the agent and as one HTML page. The agent's explanations
  are kept as project memory (`.tcagent/doc-summaries.json`); pipeline command `TcAgent.exe doc`.
- **SFC step chains** – `tc_sfc` reads a sequential function chart as text (steps, actions, transitions, alternative and
  parallel branches, jumps) and writes it back from text (through PLCopen XML, in the format TwinCAT's import accepts;
  checks that step names do not collide with variables; keeps embedded step actions; accepts alternative branches that
  end with a transition; restores the previous version if the import fails).
- **TwinCAT Package Manager** – `tc_packages` (installed, available, versions, outdated, details, feeds, settings) and
  `tc_packages_change` (install, upgrade, uninstall, repair, feeds; confirmed by the user, runs elevated).
- **Real-time cores** – `tc_realtime` shows and sets cores for TwinCAT, isolated cores, real-time cores, base time, load
  limit; `tc_task cores=...` assigns a task to a core.
- **CoE startup list** – `tc_coe` lists, adds and removes startup parameters of EtherCAT devices.
- `tc_goto_definition` also works without an open editor (POUs, DUTs, GVLs and their members via the project tree).
- Log file `%LOCALAPPDATA%\TcAgent\logs\tcagent-<date>.log` (all TcAgent processes, every tool call with duration) -
  to trace problems afterwards, e.g. after an IDE crash.

### Changed
- `tc_doc` HTML: diagrams can be zoomed (buttons, Ctrl+mouse wheel, double click), moved by dragging and shown full screen;
  FB instances that are not called directly (e.g. TcUnit suites) appear in the call hierarchy.
- `tc_check` lists compiler messages only; messages without source position (imports, device descriptions) are shown
  separately and do not count, INFO lines are left out.
- Read-only actions of changing tools (`action=list/show/info/read`, e.g. `tc_task list`, `tc_scope list`, `tc_coe list`)
  count as reading: no confirmation, allowed in the `ask`/`propose` modes, not in the audit log.
- `install-xaeshell.ps1` also installs into the 64-bit XAE Shell (VS 2022 base).

### Fixed
- `tc_plc_read` shows SFC POUs as chain text instead of the internal editor XML (tens of thousands of characters);
  `tc_plc_write` refuses text for graphical bodies.
- `tc_delete` backups go to `<solution>\.tcagent\backup\delete` (git-ignored) and restore into the original folder;
  `tc_import` copies files from a redirected `%LOCALAPPDATA%` to the temp folder so that the IDE can open them.
- `tc_refactor` follows the coding guidelines: they are part of `analyze`, `apply` is held back until they were read, and
  variable comments go as `///` lines above the variable when the guidelines say so (`style=above|trailing` per change).
- `tc_delete` backup: PLCopen export through the temp folder (the IDE did not see the new backup folder), otherwise
  declaration and code as text - the ProduceXml fallback contains no code.
- Data loss (agent feedback): `tc_sfc write` with renamed steps detached their embedded actions, `read` showed graphical
  actions as empty, and the agent then deleted them. Now the write is refused when embedded actions would lose their step,
  graphical actions are labelled, and `tc_delete` keeps a backup.
- Installing an update removed the previous version's folder although a TcAgent Chat process still ran from it - every tool
  of that process then failed with an empty `Error:`. Folders in use are kept; errors are never empty and name the cause.
- `tc_refactor` rename: transition conditions and embedded code of SFC charts are renamed too (chart rewritten via `tc_sfc write`).
- `tc_check` no longer counts the build log of HMI projects (nuget restore, virtual mappings ...) as warnings.
- TcAgent's own confirmation dialogs (refactoring choice) were treated as blocking IDE dialogs - the call aborted early.
- SFC bodies (ImplementationText returns the editor XML) are recognized as SFC again instead of shown as code.
- `tc_refactor`: descriptions of actions/transitions (no declaration part) go as `///` block on top of their code;
  new trailing comments are aligned with the neighbouring comments.
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

[Unreleased]: https://github.com/ThorstenBrach/TcAgent/compare/v0.23.1...HEAD
[0.23.1]: https://github.com/ThorstenBrach/TcAgent/compare/v0.20.4...v0.23.1
[0.20.4]: https://github.com/ThorstenBrach/TcAgent/compare/v0.18.0...v0.20.4
[0.18.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.13.0...v0.18.0
[0.13.0]: https://github.com/ThorstenBrach/TcAgent/releases/tag/v0.13.0
