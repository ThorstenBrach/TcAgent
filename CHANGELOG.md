# Changelog

All notable changes to TcAgent (MCP server `TcAgent.exe` and the Visual Studio extension TcAgentBridge).
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/); server and extension share one version number.

## [Unreleased]

### Added
- **Skills, workflows, prompts, rules and lessons can name the plugin they need:** `tcagent-requires-plugin` in their metadata, with versions
  (`line-check >=1.2 <2.0`). Without a fitting plugin that is ready and switched on, the item cannot be installed from a
  source, `pull` does not update to it, and an item already there shows `needs plugin` and does not take effect.
  The lists of skills and workflows say what is missing.

## [0.52.0] – 2026-11-04

The built-in knowledge in this version is TcAgent_Knowledge 1.0.0.

### Fixed
- **Visual Studio start: TcAgent blocked the IDE for up to 9 s,** which could bring up the bar saying that it does not respond. The chat window
  started the agent while it was created, on the UI thread of the IDE (a cold start of the agent took 8.8 s). The agent, the search for its
  program and the setup check at the start now run in the background; the window opens at once and shows "starting", the agent follows when the IDE has loaded (a question
  sent before that starts it right away). The stall monitor of the extension ticks every 250 ms instead of 50 ms and no longer
  names a tool of a server that has ended; the log says how long the chat window and the agent start took.
- **`tc_find_references` in big projects: no answer within 60 s, the IDE hung** (one report ended with a crash of the IDE, its cause
  is not known). The text search read every object through the Automation Interface, on the UI thread of the IDE. It now reads
  the project files (unsaved changes are not included, the result says so), the tree paths come from the same files, and the
  whole call answers within about 45 s: the cross references get 25 s and 8 s for reading them, a second query does not queue
  behind one still running, and the text search is returned in any case.
- Knowledge window, tab Platforms: Delete on a platform connected through `TCAGENT_PLATFORMS` ended with "not a file of a knowledge level". It
  now detaches the platform, after asking; its files stay where they are.
- `tc_delete` without `name` or `parentPath` says what is missing instead of "Value cannot be null (source)".

### Changed
- The built-in knowledge (skills, prompts, workflows, risk rules) now lives in its own repository
  [TcAgent_Knowledge](https://github.com/ThorstenBrach/TcAgent_Knowledge), included as the Git submodule `knowledge/`: every TcAgent version
  ships a known state of it. Nothing changes in the package; clone with `--recurse-submodules`.
- Knowledge window: the tab Documentation is now **Reference** ("Manuals, datasheets and library docs the agent searches",
  `tc_docs`) - it is not documentation of TcAgent and not the templates. The texts of the window say "reference source".

### Added
- **General TwinCAT risk rules** shipped with TcAgent (`knowledge/risk-rules/twincat-general.md`): 11 rules and 21 agent checks for
  every project (several based on the Top 20 Secure PLC Coding Practices), in addition to the rules in the code. **Pipelines:** two of them are `high` (password in the code, `__NEW`
  without `__DELETE`), so `doc --risks --fail-on high` can fail where it passed before.
- **New versions in the TcAgent Chat** (`src/Updates.cs`, `vsbridge/ChatUpdates.cs`, `TcAgent.exe update`): once a day, after the
  IDE has loaded, the extension asks GitHub for the releases (an anonymous request; pipelines and the agent never do). A newer
  version shows a line above the conversation and a dot on ⋯, **What's new** the release notes down to the installed version,
  with Download, Install, Open on GitHub, Skip this version and Later. The download only starts on a click and must match
  SHA256SUMS.txt of the release; Install opens the VSIX installer, nothing installs silently. The XAE Shell keeps the browser
  download and the documented steps of the ZIP. Switches in About; `updates.json` on the company or team level locks them.
- **Reset TcAgent** (Knowledge window ⋯ > Reset TcAgent, `TcAgent.exe reset`): back to a fresh installation, to choose levels, platforms, sources and
  plugins again. Confirmed by typing `delete all`. Nothing is deleted: your knowledge, plugins and modules move to
  `%LOCALAPPDATA%\TcAgent\reset-<date>\`, the connections leave `settings.json`. Projects with their `.tcagent` stay as they are.
- **The knowledge repository as a predefined source:** `tcagent-knowledge` (TcAgent_Knowledge) stands in the tab Sources from the start, so
  newer skills, prompts and rules can be installed between two versions of TcAgent. Nothing is read from it until somebody browses
  it or installs an item; it needs Git. Switch in ⋯ > About TcAgent, `"knowledgeSource": false` in `updates.json` locks it off.
- **Newer versions in the repo sources**: once a day the sources in use are fetched (no pull, no sign-in window) and a line in the
  chat leads to the tab Sources.
- **About TcAgent** (⋯ > About TcAgent, `TcAgent.exe about --json`): author, license, the versions of the extension, server, IDE and
  TwinCAT, the components with their licenses (the license texts in a window), the update switches. ⋯ also has **Check for updates**, which
  always answers in a dialog.
- **Shared folders in the TcAgent Chat** (`src/SharedFolders.cs`, `vsbridge/ChatShared.cs`, `TcAgent.exe shared`): Attach … > Share a
  folder, a folder dropped on the input, or ⋯ > Shared folders. The agent reads these folders without asking; changing a file there
  still asks. A question decides for folders and files: only this chat, or kept in the knowledge for this solution or all
  solutions (a file as a copy). The list stays on this PC, goes with Backup and Restore, and kept folders are also Reference
  sources for `tc_docs`. Drives, the user profile, its key folders and system folders are refused, with the reason.
- **Answer language in the TcAgent Chat** (⋯ > Answer language): the language of Windows (new default), the language of the
  question, or one of the languages of the Windows user (`answerLanguage` in `chat-backends.json`). Before, the answer
  followed the question, so prepared prompts in English gave English answers.
- Prepared prompt `/check-coding-rules`: compares the open object with the coding rules, apart from the risk analysis.
- Prompt list: right click **Edit** opens your own prompt, **Edit my copy** a copy of a built-in, company or team prompt on your level.
- Prompt list: it keeps one height while it is open, also with another category or a longer preview, so the categories stay under
  the mouse. Many categories wrap and scroll from the fourth row on.
- **Prompt library** (`src/Prompts.cs`, `vsbridge/ChatPrompts.cs`, [docs/KNOWLEDGE_LAYERS.md](docs/KNOWLEDGE_LAYERS.md#prompts)):
  prepared prompts as a new kind of the knowledge - `prompts\<name>.md` on every level, kind *Prompts* in the Files tab
  of the knowledge window, from repositories through Sources, in the backups. In the TcAgent Chat `/` at the start of a
  line or the button **Prompts** opens a list above the input with search, categories and a preview; Enter inserts the
  prompt (it is not sent, it can be adapted), Ctrl+Enter inserts and sends. `{object}`, `{selection}`, `{solution}` are
  filled from the editor; other values are asked in a card: `{input:…}` in the text (`|a|b` a choice, `=value` a default)
  or `params:` in the header (types text, number, choice, bool, pou = search list of POUs, GVLs and tasks; default, options,
  required, help); `tcagent-send: true` sends right after the card; recently used prompts come first; **Save as prompt …** under
  an own message. Six neutral prompts are shipped, one example for every form. `TcAgent.exe prompts [--json]`.

### Known issues
- The bar of Visual Studio about TcAgent can still appear on a start; blocks of several seconds remain while TwinCAT loads the project. Their
  cause is being looked at for 0.52.1.
- `tc_find_references` is not yet confirmed on a very large project, also for 0.52.1.

## [0.51.0] – 2026-11-02

### Added
- **Modules** (`src/Modules.cs`, [docs/PLUGINS.md](docs/PLUGINS.md)): the tools in 13 modules (core, plc-code, test,
  runtime, io, motion, hmi, scope, safety, docs, knowledge, packages, system) that can be switched on and off in a
  `modules.json` on the company, team, user and project level – company and team can lock a module, `core` stays on, a
  module needing a switched-off one is off too. A switched-off module is not in the tool list of the AI client and its
  tools are refused with the module's name; the command line keeps every tool. `TcAgent.exe modules [enable|disable]`.
  The server tells the client when the tool list changes with the solution (`tools.listChanged`).
- **Plugins** (`src/Plugins.cs`): own tools as programs in any language speaking MCP over stdin/stdout, in
  `<level>\plugins\<id>\plugin.json`, offered as `x_<id>_<tool>`. Every call goes through the policy, the confirmation
  and the audit log with the class the plugin declares (undeclared = changing, confirmed every time); a plugin uses
  TwinCAT through `tcagent/call`, i.e. through the built-in tools with all their checks. Plugins of the team and project
  levels run only after `TcAgent.exe plugins approve` for exactly their content (SHA-256); the company level can allow
  all, none or listed plugins. Example plugin `docs/plugins/todo-finder` (PowerShell).
- **Tab Modules in the Knowledge window** (`vsbridge/KnowledgeModules.cs`): a check box per module for me (default) or
  this project, grey with the reason when it cannot be changed here; the plugins of every level with their state;
  **Approve plugin …** shows the program, folder, level, tools and hash - only a mouse click on Approve approves (no
  Enter, no keyboard, no UI automation); details with the tools, dependency, window and the `modules.json` of every
  level; the status line counts the modules, plugins and tools the agent sees. `TcAgent.exe modules --json` lists the
  tool names and every level's settings, `TcAgent.exe plugins --json` the plugins.
- The TcUnit Tests window stays open when the module Tests is switched off and says so, with "Switch on (for me)".

### Changed
- Documentation: the TcUnit window follows runs started elsewhere (user guide, README), `tc_test_results` reports only
  the last run, `tc_plc_create` explains reserved names, the warning about TFS/VSS source control bindings (`tc_attach`,
  troubleshooting); the XAE Shell installation shows how to unblock the ZIP in PowerShell (`Unblock-File`) before
  extracting it; modules and plugins in the README, TOOLS.md, KNOWLEDGE_LAYERS.md, PIPELINE.md and the agent skill;
  the live times of the TcUnit window in TCUNIT_TESTS.md.

### Fixed
- **XAE Shell: no TcAgent window after installing from a ZIP that was not unblocked.** Every extracted file kept the mark
  "downloaded from the internet" (Zone.Identifier), `install-xaeshell.ps1` copied it along, and the shell (.NET Framework,
  no `loadFromRemoteSources`) refused to load the marked `TcAgentBridge.dll` (0x80131515) - no window, no setup on the
  first start, `%LOCALAPPDATA%\TcAgent\app` empty, while the script reported success. The script now stops before
  installing (also with `-BuildOnly`) and says what to do: Unblock the ZIP, extract it again, run it again. It never removes
  the mark itself - that stays the user's decision.
- **TcUnit Tests window: the time of a suite and the total stood still while a test ran** - they only counted finished tests.
  Now they count the running test too and go on every half second, like the line of the test itself; the progress line
  shows the time of the run as well ("Running · 120 of 3751 · <test> · 2:13 min"), so a long test does not look like a hang.
- **TcUnit run: up to a minute (and more) between the end of TcUnit and the result in the window.** After every run TcAgent
  read the IDE error list (up to 20,000 entries, up to 15 times) for the failure texts - slow with thousands of tests, and
  even without a failed test. Now only when a test failed, within 15 s; the window shows the numbers at once ("reading the
  failure messages …") and the texts follow.
- The MCP server ignores a byte order mark at the start of the input (clients on .NET Framework write one).
- The Knowledge and TcUnit windows close the input of the TcAgent.exe they start: an older TcAgent.exe that does not know
  a command (and starts the MCP server instead) ends at once instead of waiting.

## [0.50.0] – 2026-11-01

0.49.0 was not published as a release of its own; its changes are part of this one.

### Added
- **TcAgent with the TwinCAT XAE Shell alone** (no Visual Studio 2022, no build tools) – preview: tested in the 32-bit shell
  on a PC that also has Visual Studio 2022 (shell-only simulated with `TCAGENT_IDE_ASSEMBLIES`); a PC without Visual
  Studio 2022 and the 64-bit shell are not tested yet. Every release gets the package
  `TcAgent-XaeShell-<version>.zip` (built by `build-vsix.ps1`) with the extension prebuilt for the 32-bit shell
  (`bridge-vs15`, built against the shell) and the 64-bit shell (`bridge-vs17`, the Visual Studio 2022 build - not tested
  in that shell yet), the server and `install-xaeshell.ps1`. The script installs from the package without compiling and
  copies the server into the extension's folder; on its first start the shell sets TcAgent up like Visual Studio does
  (server to `%LOCALAPPDATA%\TcAgent\app`, interop from TwinCAT, AI clients after asking).
- `install-xaeshell.ps1 -Vsix <file>`: from the source, the server for the shell comes from a VSIX file instead of the VSIX
  installed in Visual Studio 2022. `-BuildOnly` also checks a package.
- `TcAgent.exe diag` names where the IDE automation (EnvDTE) was loaded from.
- `TCAGENT_IDE_ASSEMBLIES`: further folders with the IDE's automation assemblies (a shell installed elsewhere).

- **Source control bindings warning:** a solution still bound to TFS/VSS (the section in the .sln, `Scc*`/SAK entries in the
  project files, `.vspscc` files) made the IDE crash or refuse changes ("Cannot insert project") while TcAgent edited it -
  `tc_attach` and `doctor --solution` now warn and say how to unbind; TcAgent does not change these files.
- **`tc_plaintext` / `st import`: `fromPlc` / `--from-plc <name>`** - import a text folder that was exported from a PLC
  project with another name; without it the warning lists the folders found.
- **Delete feedback reports** in the Feedback tab: **Delete …** (Del) asks first (default No) and then removes the selected
  reports and their status records from the feedback file for good (`TcAgent.exe knowledge feedback --action delete
  --key <ids>`); the file is held exclusively while it is rewritten, other TcAgent processes retry their append.
- **docs/UI_GUIDELINES.md** – the UI/UX rules for every window and dialog of the extension (tabs and ⋯ menu, 4-6 main
  actions, master-detail, states as symbol and word, long actions in the background, keys, empty states, check boxes,
  visible safety information, vocabulary, privacy, theme colors, narrow windows, mockup before building); CONTRIBUTING.md
  and the agent guide (`agents/TCAGENT_GUIDE.md`, `SKILL.md`) point to it.
- TcUnit progress block: the optional outputs `Cycle` and `Cycles` of the running test (shown as "cycle x/y").
- The TcUnit window keeps the last 5 results of every test per solution (`%LOCALAPPDATA%\TcAgent\tcunit\history.json`).

### Changed
- `TcAgent.exe` is built against **EnvDTE 8.0** (NuGet EnvDTE 8.0.2 / EnvDTE80 8.0.3 instead of 17.0, plus stdole
  7.0.3302 - Microsoft, Visual Studio SDK license as before, not shipped): it uses the EnvDTE of Visual Studio 2022 (17.0,
  compatible) or of the 32-bit XAE Shell (8.0), which it now also finds in the shell's folder. Tested: "check" in a hidden
  Visual Studio 2022 and in a hidden XAE Shell with the shell's own EnvDTE.
- Messages without a server name the XAE Shell way too.

- **Knowledge window reworked after a UX review** (`vsbridge/KnowledgeWindow.cs`):
  - Top bar with real tabs (the selected one underlined, a badge counts what needs attention there), the platform as a
    short link, ⟳ (F5) and a ⋯ menu with Levels …, Backup now …, Restore …, Refresh and Copy diagnostics.
  - New tab **Backups**; History is now **Workflow runs** (with a note instead of an empty chart while there are fewer
    than two runs).
  - Overview starts with **what the agent gets in this project** (each entry opens the Files tab with that kind) and
    **Needs attention** (items from a source changed or newer, Git changes, documentation not reachable, open
    blocker/major feedback) with links to the tab; rates show "—" and "needs 5 runs" until there are 5 runs.
  - States as symbol and word instead of a colored dot: ✓ applies, ↷ replaced, ⊘ switched off, ✎ changed locally,
    ↑ newer in the source, ⇅ both changed, ✗ missing, ⚠ not reachable, ○ empty.
  - Files: one toolbar row with **Switch off ▾** (for this project / for me) and **Source ▾**, filters in their own row
    (search with hint and ✕, Ctrl+F), kinds as words, a detail header with the fitting actions as links and the file
    folded; the columns Git and Why only while a line has something in them (Source / license gets the room).
  - Sources: **Browse and install …** first and highlighted, rare actions under **More ▾**, Check for updates and Update
    all next to the heading, **Copy URL**, the output folded while it is empty, empty states.
  - **Browse and install** dialog: search, kind filter, "Only not installed", state column (installed where, not
    installable), details of the selected line, several items ticked with check boxes (the ticks stay when the filter
    changes; Space ticks the line) instead of a Ctrl/Shift selection that one click beside it would lose.
  - Platforms: the decision for this project on top with Set for this project … and Detect automatically.
  - Documentation: search like the agent right in the tab (Enter), state "○ empty – use Add documents …" in grey instead
    of an error, shorter intro.
  - Feedback: "Show:" filter label, the issue text as an editable section with its own heading.
- **The IDE stays responsive:** every call of TcAgent.exe from the knowledge window runs in the background with a thin
  progress bar; before, many actions (merge, propose, backup, restore, platforms, feedback text) blocked the IDE until
  they were done. Calls from the background now also pass the open solution (it was missing there).
- **Feedback:** successes and hints in the status line or a yellow info bar below the tabs (with a link such as Open
  folder) instead of a dialog to click away; errors stay dialogs. Long texts in confirmations (merge preview, update
  plan, restore difference, install list) scroll; for risky steps Enter cancels. The status line shows on the right
  what the tab holds.
- **Buttons follow the selection:** they are only enabled when they fit the selected entry (toolbar and context menu).
- **Keyboard:** F5 refreshes and Ctrl+F jumps to the search box while the window is active (the IDE no longer takes
  them); Enter opens and Del deletes in every list; toolbar buttons can be reached with Tab.
- **Dialogs in the colors of the IDE** (light, dark, blue), OK only when the input is valid (checked while typing, the
  reason below the field), the restore choice and the backup list with a filter.

- **TcUnit Tests window in the same style** (`vsbridge/TcUnitWindow.cs`):
  - Tool bar with words: Run all, Run selected (runs the ticked tests), Run failed, Stop, Report and ⋯ (Refresh F5,
    Expand/Collapse all, Save/Load selection …).
  - The counters as labelled filter chips All / Passed / Failed / Off, next to the search (Ctrl+F).
  - Progress line "Running · 4 of 18 · <test> · about … left", afterwards "✖ 2 failed · 16 passed · 24 switched off ·
    time · target …" in red or green.
  - Columns Test / Description / Result / Time instead of Duration / Traits / Error Message; the result as symbol and
    word, for suites in words ("✖ 1 failed · ✔ 3 passed").
  - Suites switched off for the run as one line "⊘ switched off (n)".
  - A test over many cycles shows "◐ running · cycle x/y" (from the progress block) or its running time.
  - Details like the HTML report: Expected / Actual / Message, code link, enable bool, Run again, Copy failure, the last
    5 runs of the test; build errors of a run appear there too.
  - The legend of the symbols in the status line; colors for the light and the dark theme.
  - Check boxes stay the way to choose tests – a click beside them loses nothing; a grey one says why ("no enable bool –
    runs only with its whole suite or Run all").
  - The root line says "runs suites in sequence" / "in parallel" instead of the TcUnit call; top right the state of the
    runs with the target ("Not run yet · 9 tests · target: local runtime").
  - **Runs started elsewhere are shown too:** a run started in the IDE (activate, login, start), by another client or
    a pipeline on the target of the window appears live like a run of the window. New command `TcAgent.exe tcunit
    watch` (the window starts it in the background): reads only over ADS – PLC state, TcUnit's progress and the enable
    bools that decide which tests run; it writes, resets and starts nothing, and the target must be allowed like for
    `ads_read`. Before, only runs of the window itself and of `tc_test_run` were shown.

- **TcAgent Chat in the same style** (`vsbridge/ChatView.cs`, `ChatWindow.cs`, `ChatCards.cs`, `ChatAcp.cs`):
  - The result of "Set up clients" as one line in an info bar ("TcAgent x is set up for n clients – restart the open
    ones") with **Details** (left-aligned, user folders as %LOCALAPPDATA% / %USERPROFILE%) and **Copy** - no longer a
    block of centered paths in the conversation. Other notices are one left-aligned line.
  - The start shows suggestions to click instead of a welcome text.
  - The backend with a short name ("Claude Code") and a dot for its state: ready / starting or working / error (the
    reason in the tooltip and the status line); a backend that starts per question is checked for its program.
  - Tool calls as compact lines: ◐ while running, ✓ / ✗ with the first line of the result and the time, **details**
    (parameters and result), **diff** for `tc_plc_write` / `tc_plc_create` (the saved object against what the agent
    writes, compare window). Claude's tool results are read now; Copilot (ACP) reports completed and failed calls.
  - Cards with **Goal**, **Consequence** and **Gate**, a filled primary button with the focus (Enter) and Esc for no;
    **Answer** only when every question is answered; a card closed by New chat / Stop says so.
  - Runtime confirmations (TCAGENT_CONFIRM) as a gate card in the chat while a chat answer runs in the open window:
    the target (AMS Net ID) in bold, a warning for a target that is not this PC, Execute / Don't execute. Safe by default:
    Enter never confirms (the focus is on Don't execute, Enter and Esc cancel), Execute reacts only to a mouse click -
    not to the keyboard or UI automation, so an agent cannot answer it; the card is scrolled into view. Without an open
    chat window or a running chat answer the dialog stays (default No, the warning for a remote target as well). The
    server passes the target and whether it is this PC to the bridge.
  - Input with a placeholder ("Ask Claude Code about this solution …"), a removable chip with the editor context that
    goes with the question, attachments as chips, **📎 Attach …**, the keys below the field in full (wrapped, never cut).
  - ⋯ menu grouped: Chat (editor context, check after changes, New chat, Copy conversation) / Windows / Setup and help
    (Set up clients, Copy diagnostics, Open user guide) and the version line.
  - Status line: the backend state, while working "◐ … is working · n tool calls · time · last tool", afterwards
    "✓ Done in … · n tool calls".
  - Esc stops the answer while the agent works; "Stopped by you" is a neutral line instead of a red error; answers
    have **Copy**; errors are marked with ⚠ (not only red); Copy diagnostics no longer opens a message box.

- Knowledge window: the actions on an item from a source are named **My changes** and **What's new upstream**; F2 renames
  a file, Ctrl+C copies the path of the selected line in every list; long paths in the details are shortened in the
  middle (Copy path copies the full one); red only for errors ("⇅ both changed", a worse trend and "not installable" are
  orange).

### Fixed
- `build-vsix.ps1` could ship an older package: with a `Platform` variable in the console (e.g. `x64`) MSBuild writes to
  `vsbridge\bin\x64\Release`, but the script only looked in `vsbridge\bin\Release` and took a package left there by an
  earlier build. It now takes the package this build wrote, wherever it is, and stops if there is none.
- Build warning IL3000 (`Assembly.Location` in the single-file build) in `TcAgent.exe ci` removed.

- **`tc_test_results`** showed FAILED details of earlier runs next to the summary of the last one: only the last run counts now
  (and with TcUnit's per-test lines only tests that failed in it).
- **`tc_plc_create`** with an IEC keyword, operator, standard function or type as name (e.g. method `Add`) only said "Name
  mismatch": it now says why and suggests a name.
- **`tc_archive`** described the archive as the whole solution: `.tszip` is the TwinCAT project (configuration with its PLC
  projects) - TwinCAT HMI and Scope projects are not in it; the solution archive `.tnzip` is made in the IDE.
- **`tc_plc_search`** over a large project could run until the client gave up and the IDE connection looked lost: a time
  budget (default 40 s, `seconds` up to 240) returns the hits so far with a hint (narrow with `path`, or search the
  exported .st files).

## [0.48.2] – 2026-10-31

### Fixed
- `vsbridge\install-xaeshell.ps1`: the build for the XAE Shell failed with CS0234 (`SVsDifferenceService`,
  `IVsDifferenceService`, `__VSDIFFSERVICEOPTIONS` in `Microsoft.VisualStudio.Shell.Interop`) - the compare window of the
  knowledge window needs `Microsoft.VisualStudio.Shell.Interop.11.0`, which is now referenced from the shell's own
  assemblies. A missing interop assembly of the shell is explained in the output.
- `vsbridge\install.ps1` and `uninstall.ps1` without administrator rights stopped with an `UnauthorizedAccessException`
  (uninstall even silently did nothing); both now check the rights before changing anything and say what to do
  (PowerShell "Run as administrator", or the VSIX, which needs no rights).

### Added
- `install-xaeshell.ps1 -BuildOnly`: only builds for the XAE Shell (normal user, nothing installed, no prompt).

## [0.48.1] – 2026-10-27

Release since v0.28.5: the knowledge of TcAgent in levels (company, user, team, platform, project) with skills,
workflows and Git sources, self-improvement from run logs, a knowledge window to manage all of it, backups of the
knowledge, a chat window on the Agent Client Protocol and issue forms with anonymized reports. The internal versions
0.29.0 to 0.48.0 were not published; their changes are summarized here.

### Added

**Client neutrality**
- `tc_ask_user`: questions of the agent with options (recommended first) and a free answer in every AI client – a card
  in the TcAgent Chat, the client's own question dialog (MCP elicitation) elsewhere.
- `%LOCALAPPDATA%\TcAgent\settings.json` as the single source of TcAgent's settings and of the registered server;
  `setup-clients.ps1` sets up every client from it. TcAgent.exe applies its `env` at every start (a variable set by the
  client or the pipeline wins).
- `AGENTS.md` as the single guide file in a project (`CLAUDE.md` and `GEMINI.md` import it).

**Knowledge levels and platforms** ([docs/KNOWLEDGE_LAYERS.md](docs/KNOWLEDGE_LAYERS.md))
- One scheme for coding rules, risk rules, skills, workflows, lessons, documentation sources and templates on the
  levels built-in → company (`TCAGENT_COMPANY`) → user → team (`TCAGENT_TEAM`, several with `;`) → platform → project;
  the later level wins. One file per topic, front matter in the Agent Skills format with `tcagent-locked` /
  `tcagent-force`, `disabled.json` switches files off without changing them.
- Platforms (automation frameworks): `<company or user>\platforms\<name>\` or own repositories (`TCAGENT_PLATFORMS`);
  set in `.tcagent\config.json` or detected from the referenced libraries and terms of `platform.md`, ambiguous → the
  agent asks.
- `tc_knowledge` (platforms, effective, why, disable/enable, split), `scope=team|platform:<name>` for the tools that add
  rules, skills and lessons; a shared level in Git changes by a commit on a new branch for a pull request, never pushed.

**Skills and workflows**
- Skills in the open Agent Skills format (`<level>\skills\<name>\SKILL.md`), `tc_skills` (list, show, add, mirror),
  built-in TwinCAT skills `requirements-interview`, `plc-debugging`, `plc-code-review`. The skills that apply everywhere
  are mirrored into the skill folders of the agent CLIs – by the client setup and at every server start, only what
  changed (`TCAGENT_SKILL_MIRROR=0` switches it off).
- Engineering workflows with checkpoints (`<level>\workflows\<name>\WORKFLOW.md`, `check-ok`, `build-ok`, `tests-ok`),
  `tc_workflow`, built-in workflow `new-feature` (requirements → change plan → code → check → build with 0 errors →
  tests → review → save). A checkpoint step runs the check itself.
- Hard gate before `tc_activate`: a build with 0 errors first (`TCAGENT_ACTIVATE_GATE=0` switches it off); the refusal
  tells the agent to show the errors and ask before changing code.

**Self-improvement**
- Run logs of the workflows, locally in `%LOCALAPPDATA%\TcAgent\runs\` (counts and names only, last 100 runs).
- `tc_improve`: KPIs per workflow, proposals by fixed rules (step hints, revert, promote lesson, frequent error,
  duplicate lesson, stale tool), apply after asking, evaluation over the next 10 runs, revert, promote with a check for
  project and machine names (`privateTerms`).

**Sources: skills, workflows and rules from Git repositories**
- `tc_sources` and `TcAgent.exe sources`: add a repository, search, install (license check: MIT, Apache 2.0, BSD, ISC
  accepted with their notice, source-available and unlicensed refused, others only after confirmation; scripts are
  listed, never run), status, pull, diff, promote. `<level>\skills.lock.json` pins every item (commit, checksum,
  license); `TcAgent.exe sources sync` restores exactly these in a pipeline.
- Overlay skills (`metadata.tcagent-extends`) adapt a general skill without copying it.
- Install into every level; a level kept in Git gets a commit on a new branch for a pull request.
- Local changes of installed items: the state locally changed / upstream newer / both changed (checked on opening,
  refreshing and saving); compare with the installed version (compare window of Visual Studio), revert, keep as own
  version (`tcagent-detached`), update and merge three-way (conflicts in the merge window of Visual Studio), propose
  upstream as a pull request (a branch in a working copy of the source plus a patch – pushed only when you confirm, to
  the source or your fork), uninstall. `tc_sources action=changes|merge|propose|revert|detach|uninstall`.

**Knowledge window** (View > Other Windows, or ⋯ > Knowledge in the chat)
- Overview (KPI tiles, workflows, improvement proposals), Files (every knowledge file of every level with its status:
  new, add existing, open, rename, move to level with name check, switch off/on, delete, copy path; Git per level with
  fetch, pull, commit, push), Sources (sources and installed items with all actions above, browse and install),
  Platforms (detection, set for the project, new, delete), Documentation (every source of `tc_docs` with its state:
  add links, folders and documents, search like the agent with hits that open – PDFs on the page of the hit), Feedback
  (see below), History (workflow runs, adopted changes, backups), Levels … (company, team and platform folders in
  `settings.json`).
- One vocabulary and order in every tab; toolbar and context menu show the same actions; errors as a message.
- `TcAgent.exe knowledge json|new|move|rename|delete|disable|enable|git|platform|docs|feedback|settings` – the actions
  of the window for scripts.

**Backup and restore of the knowledge**
- Every level is backed up automatically before every change (knowledge window, chat tools, command line) as a ZIP with
  `manifest.json` (TcAgent version, level, SHA-256 of every file), only when it changed since its last backup, the last
  20 per level, in `%LOCALAPPDATA%\TcAgent\backups\knowledge\`. A level in Git: its uncommitted changes only.
- Restore of the whole level, one file or one skill/workflow: the difference first, then the confirmation; the current
  state is backed up before. **Backup …** / **Restore …** in the knowledge window, `TcAgent.exe knowledgebase
  list|backup|diff|restore`.

**Chat window**
- The chat speaks the Agent Client Protocol (ACP): a persistent session, live text and tool calls, permission cards,
  Stop as `session/cancel`. GitHub Copilot is the first ACP backend (`copilot --acp`); `setup-clients.ps1` switches the
  former Copilot entry over. The stream-json way stays for Claude Code.
- Copy diagnostics in the ⋯ menu.

**Tests and deterministic first** ([docs/DETERMINISTIC_FIRST.md](docs/DETERMINISTIC_FIRST.md))
- `tc_test_skeleton`: the TcUnit test suite of a function block, generated the same way every time (enable bools,
  an `InitialOutputs` test, one test per public method with every parameter, naming and folder of the existing
  suites, `instanceName`).
- The TcUnit Tests window shows a run started by an AI client live.

**Feedback and support**
- The knowledge window anonymizes `tc_feedback` reports (names of computer, user, solution and projects,
  `privateTerms`, user folders, paths, IP addresses / AmsNetIds) for editing and opens them as a prepared GitHub issue –
  you check and send it yourself.
- `TcAgent.exe diag`: versions of TcAgent, extension, TwinCAT and IDEs, runtime state, Windows, .NET, knowledge levels
  as a Markdown list without names or paths.
- GitHub issue forms (bug report, feature request, reports from TcAgent) with a Diagnostics field, questions in the
  GitHub Discussions, private security reports (`SECURITY.md`).

**Runtime data backup – experimental**
- `tc_backup` / `TcAgent.exe backup|restore`: persistent variables, the `RETAIN`/`PERSISTENT` variables of the code and
  include patterns (recipes, parameters; `.tcagent\backup.json`) via ADS into one JSON file per PLC; `diff`; `restore`
  writes only values that differ to symbols that exist with the same type, only to allowed targets, after confirmation
  and with the PLC stopped unless `whileRunning`. **Experimental: not yet tested against a running PLC.**

### Changed
- Coding workflows end with a build with 0 errors, always preceded by `tc_check` (a pre-compile without code
  generation); the guidance of every agent says so.
- Agents change code only when the user asked for a change; errors found on the way are reported and the user is
  asked.
- `tc_coding_rules` shows the level of every rule file and the platform of the project and lists skills and workflows.
- Documentation sources are read from every level including the team levels; `tc_docs action=sources` shows level and
  state; `tc_docs_add` refuses entries that are neither a URL nor a folder.
- `TCAGENT_LESSONS` keeps working; `TCAGENT_TEAM\lessons\` replaces it.
- TcAgentBridge serves several requests at once; messages name no single AI product.
- `setup-clients.ps1` writes a configuration only when it changes and keeps the first and the last 3 backups per file.

### Fixed
- A running chat session kept talking to the server it started with after a TcAgent update; it now restarts with the
  new server. Changes of `chat-backends.json` take effect without restarting Visual Studio.
- A test run started from the chat blocked the IDE briefly for every event.
- `tc_test_skeleton` left out required inputs, which did not compile.
- Names with capitals or spaces (platform, skill, source) were refused with a message in the status line only.
- The backup list in the History tab got no height in a smaller window.

## [0.28.5] – 2026-10-25

### Changed
- TcAgent Chat: permission questions and follow-up questions of the agent are cards in the conversation instead of
  separate dialog windows. A permission card offers Allow once, Allow for this session and Deny. A question card shows
  its options to click, a field for another answer, Answer and Skip. After the answer the card stays in the conversation
  with the decision. New chat, Stop or the end of the session answer an open card with Deny / not answered.
- TcAgent Chat top bar: only the backend, **New chat** and a **⋯** menu remain. The menu holds Send editor context,
  Check after changes, TcUnit Tests, Set up clients and the version. The version shows in the top bar only when the
  server and the extension differ or the server is not set up.
- The agent in the chat knows that permission questions are shown to the user, so it no longer claims that there was
  no question. An error after an allowed call is a real tool error.

## [0.28.4] – 2026-10-24

### Added
- TcAgent Chat (Claude Code backend): when Claude needs a permission its rules do not give, e.g. reading a file
  outside the solution folder and the shared folders, the chat window now asks. Options: **Allow once**, **Allow for
  this session** (the rule Claude Code suggests, e.g. Read in that folder, until the chat session ends) or **Deny**
  (default; closing the dialog = deny). Before, such calls were refused without a question and the agent only reported
  that it had no access. Technically: the persistent session runs with `--permission-prompt-tool stdio` and the window
  answers the `can_use_tool` requests.
- TcAgent Chat (Claude Code backend): follow-up questions of the agent come as a dialog. Each question shows its
  options to click, with their descriptions (several at once if the question allows it), plus a field for a free answer.
  The answer goes back to the agent, and Cancel tells it that the question was not answered. The agent is asked to use
  this tool (`AskUserQuestion`) for decisions instead of ending its answer with a question.

### Fixed
- `tc_check` / `TcAgent.exe check` ("Check all objects") crashed the TwinCAT XAE Shell in solutions with Remote Manager
  3.1.4020.56 (access violation in `TwinCAT System Manager.dll` 4020.56, RPC 0x800706BE, unsaved changes lost;
  FB-23CB01DE, FB-BDB62B01, FB-CE48B5F9, FB-D1AD25B9). TcAgent called the newest `ITcPlcIECProject*` interface of its
  interop on the PLC project item from the tree enumeration. That item is a generic tree item; the call reached the
  old DLL with a method layout it does not have. Now the PLC projects are fetched with `LookupTreeItem` (the real
  project object), and `CheckAllObjects` is called by name (IDispatch). Checked with 4020.56 (library project plus TcUnit
  project, no crash, real results) and 4026.19.

## [0.28.3] – 2026-10-23

### Security
- `tc_packages_change`: tcpkg no longer runs through `cmd.exe` with administrator rights. A feed name like `x&calc` went
  unchecked into that command line. Now TcAgent itself runs elevated (`TcAgent.exe tcpkg-elevated`, UAC prompt) and
  starts `tcpkg.exe` directly, without a shell, and only from the program folders or `C:\TwinCAT`, never from a PATH
  folder the user can write to.
- tcpkg arguments are checked before the confirmation and again in the elevated process (`Guards.TcpkgArgs`):
  - package ids and versions: only `[A-Za-z0-9._-]`
  - feed names: words of `[A-Za-z0-9._-]` separated by single blanks (the Beckhoff feeds are named like "Beckhoff Stable Feed")
  - feed sources: an http(s) URL or an absolute folder, without quotes and shell characters
  - options: a fixed set only
  `tc_packages` (read only) checks the same.
- Confirmations: if nobody can be asked (no question dialog in the AI client, no TcAgentBridge dialog in the IDE),
  `ads_write`, activation, PLC start/stop and the other runtime actions are refused with a clear message instead of
  running unconfirmed. Running without a question needs the deliberate setting `TCAGENT_CONFIRM=off` (pipelines,
  unattended agents). The command line (`test`, `sil`, `hil`, `tcunit run`) is not affected.
- TcUnit test report:
  - The command of `report` in `.tcagent\tcunit.json` comes with the solution and runs only after you approve it once for
    this solution and exactly this command text: in the TcUnit Tests window a question shows the command, or use
    `tcunit report --approve <id>`. Until then TcAgent's own report is made. Approvals are kept per Windows user in
    `%LOCALAPPDATA%\TcAgent\tcunit-report-approved.json`.
  - TcAgent's own report: only absolute http(s) links are clickable, and they are encoded for the attribute. A status
    from `last.json` only becomes a known class name. A Content-Security-Policy allows no external resources and no
    script but the report's own (hash).
  - The window opens only an `.html` file that exists.
- TcUnit runs: `tc_test_run` names the target of `.tcagent\tcunit.json` in the confirmation and in the whitelist check
  (`TcUnitRun` checked it already). A saved test selection cannot set a target (a `"target"` key is refused).
  `--tests @file` / `tests=@file` read only `*.tcunit.json` and `*.txt` files.
- Unit tests: `GuardsTests` (tcpkg arguments, quoting, confirmation without a channel, report links, report approval),
  selection files without a target.

## [0.28.2] – 2026-10-22

### Added
- TcUnit Tests window: buttons **Expand all** / **Collapse all** (all suites) in the tool bar, as in the Test Explorer.

### Fixed
- Test report, view grouped by `reportGroupBy`: the number of suites of a group counts the suites that ran.

## [0.28.1] – 2026-10-22

0.28.0 was a local test build only; its changes are listed here.

### Added
- **TcUnit Tests window** in Visual Studio 2022 and the TwinCAT XAE Shell (View > Other Windows > TcUnit Tests, or the
  Tests button of the TcAgent Chat), close to the Test Explorer: tree PLC project > suite instance > test read from the
  code, Run all / Run selected / Run failed / Cancel / Refresh, search and status filter, counters, progress bar
  "Test n of N: name" with the remaining time, details with description, `EXP`/`ACT`/`MSG`, duration and code
  position; double-click opens the test method. The tree is read again after a build and when code files are saved.
  Guide: `docs/TCUNIT_TESTS.md`.
- `TcAgent.exe tcunit list --solution <sln>`: TcUnit suites, instances and tests from the project files (no IDE) as
  JSON – file, line, `///` description, paths of the enable bools `bRunSuite` / `bRun_<TestMethod>`; suites through
  base suites, instances in PROGRAMs and GVLs (also arrays), test names known only at run time are marked.
- `TcAgent.exe tcunit run` and tool **`tc_test_run`**: runs a selection of tests (`--tests` / `tests`: instances, suite
  types, `instance@test` or test names). Build and activation only if the code changed since the last download,
  reset of the PLC via ADS, all enable bools written (TRUE = selected, FALSE = the rest), start via ADS, live results
  per test from TcUnit's own test table, failure texts from the runtime messages, reset again – TwinCAT stays in Run
  mode, no IDE login. One JSON event per line for the window, `cancel` on stdin stops the run, optional JUnit file.
  Optional settings in `.tcagent\tcunit.json` (`plcProject`, `target`, `timeout`, `progress` block). A remote target
  must be allowed in `TCAGENT_ALLOW_TARGETS`; the window asks once per run for it.
- TcUnit Tests window: button **Report** (HTML test report of the last run, opened in the browser), counter "n of N
  test(s) selected" / "4 of 6 done (67 %)" during a run, a partly ticked suite shows the third checkbox state.
- `TcAgent.exe tcunit report --solution <sln>`: HTML report of the last run. Every run keeps its results as JUnit XML in
  `%LOCALAPPDATA%\TcAgent\tcunit\<solution>-<hash>\last.xml`; the report is made by the command of `report` in
  `.tcagent\tcunit.json` (placeholders `{junit}`, `{html}`, `{solution}`, `{solutionDir}`) or by TcAgent's own report
  for every project: suites and tests with the `///` descriptions of the code, result, duration, failure text, code
  position and enable bool, run information (target, git commit, TcAgent, selection), summary per suite, search and
  filters; tests switched off for the run are counted separately (run record `last.json` next to `last.xml`).
  `tcunit report --builtin` uses it even if `report` is set.
- Test case texts in the code: below its first line the `///` comment of a test method or suite may hold sections
  (`Setup:`, `Steps:` with `1.` lines, `Expected:` with `-` lines, `Link:` ...); `tcunit list` returns them as
  `details`, the window shows them in the details, the report under every test (the case of a generated suite under
  each of its tests).
- Test report grouped by a `///` section: `reportGroupBy` in `.tcagent\tcunit.json` (e.g. `Blocks` for the function
  block under test) adds a second view to the report, switchable with the suite view; a test with several values is
  listed under each of them. A `///` line directly above a call in the suite body adds to the text of the called method.
- TcUnit Tests window laid out like the Visual Studio Test Explorer: icon tool bar, status filter buttons with counts
  (several combinable), framed search box, list with resizable columns Test / Duration / Traits / Error Message,
  "Test Detail Summary". Test selections can be saved and loaded (`*.tcunit.json`); `tcunit run --tests @file` and
  `tc_test_run tests=@file` run a saved selection.
- Menu entry View > Other Windows > TcAgent Chat (Visual Studio 2022 and XAE Shell).
- Unit tests for the test discovery and the selection (`TcUnitDiscoveryTests`).

### Fixed
- Update of `%LOCALAPPDATA%\TcAgent\app\<version>\` while an AI client still runs `TcAgent.exe` from there: the running
  file is set aside (`*.old-<time>`, deleted at the next start) and the new one copied, instead of keeping the old
  TcAgent with the message "still in use".
## [0.27.0] – 2026-10-20

### Changed
- **Format of the plain-text ST files** (`st export|check|import`, `tc_plaintext`): every block is its declaration, an empty
  line, the implementation indented by two blanks, an empty line and the `END_` line; the POU ends with its own
  `END_FUNCTION_BLOCK` / `END_PROGRAM` / `END_FUNCTION`, methods, properties and actions follow after it; properties and
  interfaces have no `END_PROPERTY` / `END_INTERFACE`; GVLs and DUTs are followed by two empty lines; whitespace at the
  ends of the texts is not written. The import tells declaration and implementation apart by the indentation (no
  `// @implementation` lines any more); whitespace at the ends of a text or of its lines does not count as a change.
  Text folders of earlier builds are rewritten by the next `st export`; their import is refused (methods before the
  `END_` line, code not indented). Round trip without changes for all checked objects. Tests: `PlainTextRepositoryTests`
  (objects of a real library with their reference text files), `PlainTextLayoutTests` rewritten.
- THIRD-PARTY-NOTICES: System.Reactive, the Adobe Glyph List (BSD-3-Clause) and the Adobe Core 14 AFM files embedded in
  PdfPig with their license texts. The VSIX ships the third-party notices of the .NET runtime
  (`THIRD-PARTY-NOTICES-DOTNET.TXT` next to `TcAgent.exe`).
- README: the source code contains no code from Beckhoff; trademark owner Beckhoff Automation GmbH & Co. KG.
- Fixed versions of the shipped packages (instead of `6.1.*` / `0.1.*`) and a NuGet lock file `packages.lock.json` in
  the repository: the VSIX ships only versions whose licenses were checked. Beckhoff.TwinCAT.Ads 7.0.389 (current
  release line, MIT; adds TwinCAT.Ads.ConfigurationProviders and Microsoft.Extensions.Configuration*, MIT), PdfPig 0.1.16.
- `setup-clients.ps1` also reads the configuration of newer Claude Desktop versions (`%LOCALAPPDATA%\Claude-Data\Claude_*\`).
- TcUnit conventions for agents (SKILL.md, agent guide, USER_GUIDE): suites with the enable bools `bRunSuite` /
  `bRun_<Test>` and a `///` description per test, so tests can later be selected one by one.

### Security
- Pipeline examples: pull requests from forks never run on the self-hosted TwinCAT runner - the GitHub workflows skip
  them (`if:` on each job), the Azure Pipelines and GitLab examples name the setting that keeps them off; RUNNER_SETUP
  mentions the guard.
- Bridge pipe `TcAgentBridge.<pid>` only for the user who runs Visual Studio / the XAE Shell and only on this PC: own DACL,
  network access (SMB) denied. Before, Windows granted Everyone/Anonymous read access and the pipe was reachable over the network.
- TcAgent only talks to a bridge served by Visual Studio or the XAE Shell (server process id must match the pipe name)
  and connects with impersonation level Identification. A foreign program behind the pipe name is refused, and a
  confirmation asked through it counts as "no".

## [0.26.2] – 2026-10-17

### Added
- **Plain-text ST files** – `TcAgent.exe st export|check|import --solution <sln>` and the tool `tc_plaintext`: the PLC code
  as one `.st` file per POU, GVL, DUT and interface (`<solution>\st\<PLC project>\<folder>\<name>.st`) – TwinCAT's
  declaration and implementation text unchanged, methods (`METHOD … END_METHOD`), properties (`PROPERTY … GET … END_GET
  SET … END_SET END_PROPERTY`) and actions (`ACTION <name>: … END_ACTION`) inside the POU before `END_FUNCTION_BLOCK` /
  `END_PROGRAM` / `END_FUNCTION` / `END_INTERFACE` (IEC 61131-3 3rd edition layout). Works on the project files, no IDE.
  - `st export` writes only changed files (`--clean` removes files of deleted objects); `st check` exits 1 when the text
    folder differs from the project – a CI gate for a committed text mirror with readable diffs. Every exported file is
    read back first; an object the text cannot reproduce exactly is reported (warning, `st check`: difference).
  - `st import` takes edited files back into the existing project: changed declarations/implementations replaced with
    Ids, order and unchanged XML byte-identical (LineIds of changed blocks removed), new methods/properties/actions/
    accessors and new `.TcPOU`/`.TcIO`/`.TcGVL`/`.TcDUT` files (with `.plcproj` entries) created, nothing deleted;
    renames/moves refused, a file with a block line inside a declaration or implementation (e.g. a `METHOD` without
    `END_METHOD`) refused with its line; `--dry-run`; refused (exit 2) while an IDE has the solution open. Through
    `tc_plaintext` the import writes through the connected IDE instead (then `tc_save`, `tc_check`).
  - Import accepts both layouts: sub-objects inside the POU (the export format) or after its `END_` marker (the format
    of the first internal builds). A re-export rewrites such existing text folders once; until then `st check` reports
    their POUs with sub-objects as `differs`.
  - A `// @implementation` line separates declaration and implementation where the split is ambiguous; graphical bodies
    (SFC, LD, FBD, CFC) appear as `// @implementation SFC - ...` and stay untouched. Format: `docs/PLAINTEXT.md`.
- **references.json** – `st export|check` and `tc_plaintext` also write/compare `<folder>\<PLC project>\references.json`
  with the library references of each PLC project (`Name`, `Version`,
  `DistributorName`, `Namespace`; UTF-8 without BOM, CRLF, 2 blanks indentation). Placeholders from
  `<DefaultResolution>`, overridden by a project-specific `<PlaceholderResolution>` (the library TwinCAT really uses);
  library references from `Include` (`newest` → `*`); `Namespace` is `null` when it equals the name; placeholder
  parameters are left out; sorted by name for stable Git diffs. Written only when it changes; `st check` reports
  `missing`/`differs` (line ends ignored); `--clean` never removes it; `st import` ignores it.
- **Unit tests** – `tests\TcAgent.Tests` (xUnit, .NET 8, no TwinCAT needed): `dotnet test tests\TcAgent.Tests` –
  references.json and the `.st` layout (export format, round trip without changes, old layout still imported).

## [0.24.3] – 2026-10-15

Build server and pipeline release: tested end to end with a self-hosted GitHub Actions runner on a developer PC.

### Added
- **SBOM** – `tc_sbom` and `TcAgent.exe sbom --solution <sln> [--out <file.cdx.json>] [--plc <names>]` write a CycloneDX 1.6
  SBOM per PLC project (default `<solution>\sbom\<PLC project>.cdx.json`): the PLC project with its version and the git
  commit/branch, one component per library reference with resolved version (`*` resolved like TwinCAT: newest installed),
  vendor, placeholder, requested version, purl `pkg:generic/<vendor>/<name>@<version>` and the SHA-256 of the library file
  from the library repository; dependencies. Missing data is marked (`not found`, `unresolved`, `unknown`).
- **Library lock file** – `tc_library_lock` (action `check`/`write`) and `TcAgent.exe lock --solution <sln> [--plc <names>] [--check]`:
  `<solution>\.tcagent\libraries.lock.json` with the resolved library versions per PLC project, sorted for clean Git diffs
  (not rewritten when nothing changed); `--check` lists added/removed/changed libraries and exits 1 on differences,
  2 without lock file - a pipeline gate against "a different library version on the build machine".
- **Pipeline configuration in the repository** – `.tcagent/pipeline.json` describes the steps of the TwinCAT pipeline
  (`doctor`, `check`, `build`, `analyze`, `doc`, `test`/`sil`/`hil`, `library`, `archive` or any other TcAgent.exe
  command; options as JSON keys, comments allowed) and `TcAgent.exe ci` runs them in order, each as its own TcAgent.exe
  with a fresh IDE: live output with a header and a log group per step (GitHub Actions, Azure DevOps), stop at the first
  failing step unless `continueOnError`, `"enabled": false`, default JUnit file per step in the results folder, summary
  table on the console, in `results/ci-summary.md` and in the GitHub job summary. `--only`, `--from`, `--dry-run`,
  `--config`; `--init` writes a starter file. New templates `docs/pipeline/github-actions-ci.yml`,
  `azure-pipelines-ci.yml` and `pipeline.example.json`.
- **Runner guide** – `docs/RUNNER_SETUP.md`: your own TwinCAT build server with a GitHub Actions runner step by step
  (doctor, Git repository, token, `setup-runner.ps1`, first run, operation, build server, troubleshooting), tested on a
  developer PC; `.gitignore` template for TwinCAT projects `docs/pipeline/twincat.gitignore` (`**/` patterns, HMI packages).

### Changed
- `test`/`sil`/`hil` with an explicit `--target` that is not allowed are refused (exit 2) **before** Visual Studio is
  started - the check took a full solution load before.
- `doctor --solution` asks TwinCAT's library manager whether TcUnit is installed (independent of the repository folder).

### Fixed
- `TcAgent.exe test`/`sil`/`hil` with a target outside `TCAGENT_ALLOW_TARGETS` now ends with exit code 2 (refused) instead of
  3 (infrastructure) - the pipeline shows a configuration error, not a broken build machine.
- `ci`: an output path that already starts with the results folder (`"out": "results/doc.html"`) is no longer doubled.
- SBOM/lock: a TwinCAT version without readable library references reports the PLC project without libraries instead of stopping.
- `ci --init` in a subfolder (e.g. next to the solution in `TwinCAT\`) writes the pipeline file into the Git repository
  root, where the workflow runs `TcAgent.exe ci`; `ci` also finds a single `.tcagent\pipeline.json` up to two levels below
  the current folder.
- "Solution not found" lists the `.sln` files that exist in that folder (folder `X_2`, file `X2.sln`).
- Runner: `setup-runner.ps1` starts the runner through `run-tcagent.ps1` with QuickEdit switched off - a click into the
  runner window no longer pauses a job (scheduled task, Startup fallback and `-Start`).
- `doctor` without `--solution`: TcUnit not found in the usual library folders is a hint (INFO), no longer a warning - on
  TwinCAT 4026 it was a false alarm (the `ci` template's first step reported "READY with 1 warning"); `--solution` checks
  it through TwinCAT and still warns when it is really missing.

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

[Unreleased]: https://github.com/ThorstenBrach/TcAgent/compare/v0.51.0...HEAD
[0.51.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.50.0...v0.51.0
[0.50.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.48.2...v0.50.0
[0.48.2]: https://github.com/ThorstenBrach/TcAgent/compare/v0.48.1...v0.48.2
[0.48.1]: https://github.com/ThorstenBrach/TcAgent/compare/v0.28.5...v0.48.1
[0.28.5]: https://github.com/ThorstenBrach/TcAgent/compare/v0.28.3...v0.28.5
[0.28.3]: https://github.com/ThorstenBrach/TcAgent/compare/v0.27.0...v0.28.3
[0.27.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.26.2...v0.27.0
[0.26.2]: https://github.com/ThorstenBrach/TcAgent/compare/v0.24.3...v0.26.2
[0.24.3]: https://github.com/ThorstenBrach/TcAgent/compare/v0.23.1...v0.24.3
[0.23.1]: https://github.com/ThorstenBrach/TcAgent/compare/v0.20.4...v0.23.1
[0.20.4]: https://github.com/ThorstenBrach/TcAgent/compare/v0.18.0...v0.20.4
[0.18.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.13.0...v0.18.0
[0.13.0]: https://github.com/ThorstenBrach/TcAgent/releases/tag/v0.13.0
