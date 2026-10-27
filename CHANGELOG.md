# Changelog

All notable changes to TcAgent (MCP server `TcAgent.exe` and the Visual Studio extension TcAgentBridge).
Versions follow [Semantic Versioning](https://semver.org/); server and extension share one version number.

## [Unreleased]

## [0.48.0] – 2026-10-27

### Added
- Knowledge window, tab Files: **Add existing …** (its own entry instead of a choice inside New …), **Rename …** (files
  and skill/workflow folders, the name in the header follows; `TcAgent.exe knowledge rename --path --name`), **Copy
  path**; installed items get **Uninstall …** in Files too.
- **Copy path** in every list; **Open** / **Open folder** for sources (the repository / TcAgent's local copy), installed
  items, documentation sources and search hits; **Delete …** for a backup.
- Search hits of a PDF open on the page of the hit (Acrobat/Reader, Foxit, SumatraPDF, browsers; the page comes from
  the text extraction).
- **Backup …** dialog: the levels (checkboxes), the folder (TcAgent's backup folder, changeable), the reason and "Open
  the folder afterwards"; then a message with every file written and its number of files
  (`knowledgebase backup --level a,b`).

### Changed
- One vocabulary in every tab, in one order: New … / Add existing … / Open / Open folder / Edit … / Rename … / Move to
  level … / Switch off · on / Delete … (Del) / Copy path, groups separated by lines ("Remove …" of sources and
  documentation sources is now "Delete …"; documentation: "Add link or folder …", "Add documents …").
- Errors are always shown as a message (error icon), hints as a message, everything else in the status line.

## [0.47.1] – 2026-10-27

### Changed
- Knowledge window: the toolbar and the context menu of every list come from one list of actions - the same entries with
  the same names in every tab (e.g. "Compare with installed version", "Revert to installed version …", "Uninstall …" in
  Files and Sources; "Delete …" / "Remove …" with the dots of a confirmation). Hints appear as a message, not only in the
  status line.
- **Back up now …** asks for an optional copy in a folder of your choice (`knowledgebase backup --out`) and offers to open
  the folder; **Restore …** opens a list of all backups, **Restore from a file …** takes a backup ZIP from anywhere (its
  level comes from the manifest).
- Tab Documentation: **Add link or folder …** and **Add documents (copy files) …** instead of "Add" and "Copy documents
  in"; **Edit …** with nothing selected no longer opens the add dialog, on a document folder it opens the folder;
  **Open knowledge.md** opens the list of the selected source's level or of a level you choose and creates it if
  missing; **Search like the agent …** lists the hits - double-click opens the document or the page
  (`knowledge docs --action search --json`).

## [0.47.0] – 2026-10-27

### Added
- **Backup and restore of the knowledge** (`KnowledgeBackup.cs`, `TcAgent.exe knowledgebase list|backup|diff|restore`):
  every kind on every level (company, user, team, platform, project) is backed up automatically before every change -
  knowledge window, chat tools, command line - as a ZIP with `manifest.json` (version, level, SHA-256), only when the
  level changed, the last 20 per level. Levels in Git: only uncommitted (and ignored) files. Restore of the whole level,
  one file or one skill/workflow: the difference first, then the confirmation, the current state backed up before.
  Knowledge window: **Backup …** / **Restore …** in the top bar, the backups in the tab History.
- Installed items: **Revert …**, **Keep as own version …** (`tcagent-detached`), **Diff** in the compare window of
  Visual Studio, conflicts of **Update and merge …** in the merge window of Visual Studio (`vsDiffMerge`); the state
  **⇅ both changed** besides locally changed and upstream newer, in colour in Files and Sources; refreshed when a file of
  a level is saved in the editor. `tc_sources action=revert|detach`, `TcAgent.exe sources revert|detach`.
- Install from a source into every level - team, company, platforms; a level kept in Git gets a commit on a new branch
  for a pull request.

### Changed
- Installed items on the company level and in platform folders are listed and checked too.

## [0.46.0] – 2026-10-27

### Added
- **Runtime data backup and restore** (`tc_backup`, `TcAgent.exe backup|restore`, `RuntimeBackup.cs`): persistent
  variables, the `RETAIN`/`PERSISTENT` variables of the code (GVLs and programs by path, function blocks in every
  instance) and include patterns for recipes and parameters (also `.tcagent\backup.json`) via ADS into one JSON file per
  PLC - for several PLCs at once on the command line. `diff` compares a backup with the PLC; `restore` writes only the
  values that differ, only to symbols that still exist with the same type, only to allowed targets, after confirmation
  and with the PLC stopped unless `whileRunning`.
- Sources tab: **Uninstall …** for installed items (also Del and the context menu; a copy goes to the backups);
  `tc_sources action=uninstall`, `TcAgent.exe sources uninstall`.

## [0.45.0] – 2026-10-27

### Added
- Items installed from sources that you changed: **My changes** (diff against the installed version), **Merge new
  version …** (preview first, then a three-way merge per file of the newer version of the source into your item - your
  version is backed up, conflicts are marked with `<<<<<<<`) and **Propose (pull request) …** (your changes as a commit on
  a new branch of the source without TcAgent's metadata, checked for private terms, plus a patch file; pushed only when
  you confirm - to the source or your fork - and the pull request page opens). In the Sources tab and in the context menu
  of the Files tab.
- `tc_sources action=changes|merge|propose` and `TcAgent.exe sources changes|merge|propose|push` (`SourcesMerge.cs`).

### Changed
- Tab Files: an item from a source shows **✎ changed** or **↑ newer** in the column Source (orange mark when changed).

## [0.44.0] – 2026-10-27

### Added
- Knowledge window, tab **Documentation**: every source of `tc_docs` – Beckhoff InfoSys, the links, folders and network
  shares of the `knowledge.md` of every level, `TCAGENT_KNOWLEDGE` and the document folders `knowledge\` – with type,
  level, state (documents found, pages already read, not reachable within 4 s) and the file it is defined in. Add to a
  level, edit, remove, copy documents in, open, open the `knowledge.md`, test search.
- `TcAgent.exe knowledge docs --action list|add|remove|copy|search` - the same for scripts.

### Changed
- `tc_docs action=sources` shows the level and the state of every source.
- Documentation sources are read from the team levels too (before: company, platform, user and project only).
- `tc_docs_add` refuses a source that is neither a URL nor a folder or network path (before it was listed but never read).

## [0.43.0] – 2026-10-26

### Added
- `TcAgent.exe diag`: TcAgent, the extension registered by setup, TwinCAT, the installed IDEs, the runtime state of this
  PC, Windows, .NET and the configured knowledge levels as a Markdown list for issue reports - no names, paths or
  addresses. **Copy diagnostics** in the chat menu ⋯ and in the knowledge window (tab Feedback) adds the extension
  version and the IDE the window runs in and copies the block.
- Feedback reports of the knowledge window end with this Diagnostics block (anonymized like the rest).

### Changed
- Issue forms: one required Diagnostics field instead of the separate version fields; the bug report asks where the
  problem is (tool, decision of the agent, IDE/TwinCAT) and how TcAgent ran (interactive, command line/pipeline);
  bug report and feature request have a check for existing issues; the feature request asks whether you can help.
  New issues get the label `needs-triage`.
- Questions moved from the issues to the GitHub Discussions (category Q&A, with a form); the issue chooser links there
  and to the private security report. `SECURITY.md` says what a report should contain.

## [0.42.6] – 2026-10-26

### Changed
- Issue templates: TcAgent version and the version of the TcAgent extension (bridge) are required fields of bug report
  and question - after an update the two can differ. The fields are in a fixed order: description, then versions, then
  the AI client.
- Knowledge window, tab Feedback: the report text fills in the bridge version, the TwinCAT version (registry) and the IDE
  with its version (the running Visual Studio or XAE Shell) by itself.

## [0.42.5] – 2026-10-26

### Changed
- Issue templates: TwinCAT version, IDE (Visual Studio 2022/2019, XAE Shell 64-bit/32-bit) and IDE version as required fields
  of bug report and question, the AI client as a field of its own; the feature request asks for them optionally. The
  feedback text of the knowledge window has the lines for TwinCAT and IDE version to fill in.

## [0.42.4] – 2026-10-26

### Added
- Issue template **Question** (question, what you tried, area, version, environment; label `question`). Bug report: field
  *Reproducible* (always / sometimes / once); feature request: *Importance for you* and the TcAgent version; feedback
  template: environment line.

## [0.42.3] – 2026-10-26

### Added
- **GitHub issue templates** (`.github/ISSUE_TEMPLATE`): bug report and feature request as issue forms (tool, what happened,
  expected, steps, workaround, severity, version, environment, log, a check that the report is anonymized), a template
  for reports from TcAgent (`tcagent_feedback.md`, label `feedback`) and a chooser without blank issues.
- The knowledge window shares a report with that template and a label by its type (bug, enhancement, documentation).

## [0.42.2] – 2026-10-26

### Added
- Knowledge window: context menus in Platforms (open, open folder, new, set for the project, detect, **delete** an own
  platform - also with Del), Sources (browse & install, edit, remove - Del, add; installed: diff, check for updates, update
  all) and Feedback (share, copy, save, set status, anonymize again).

## [0.42.1] – 2026-10-26

### Added
- Knowledge window: the company definition of a platform (`<company>\platforms\<name>`) as a level of its own for New, Add
  existing and Move ("platform · <name> (company)"); **New platform** can be defined at the company level. **Levels …**
  moved to the top bar (visible in every tab).

## [0.42.0] – 2026-10-26

### Added
- **Knowledge window, Levels …:** set the company level, the team level(s) and platform repositories (`TCAGENT_COMPANY`,
  `TCAGENT_TEAM`, `TCAGENT_PLATFORMS`) - stored in `settings.json`; Set up clients passes them on to the AI clients.
- **New / Add existing / Move offer every level:** user, project, team(s), company and every known platform (also one that
  does not apply to the open project). A company or team level kept in Git gets the file in its working copy - commit and
  push it in the Git bar (the push goes to a new branch for a pull request); the original of a move stays until it is merged.

### Changed
- **TcAgent.exe reads `settings.json` \"env\" on every start** (MCP server, command line, the windows of the extension) - where
  the environment does not set a variable already (a client configuration or a pipeline wins). Before, the knowledge
  window did not see the company and team levels the AI clients got through their configuration.

## [0.41.4] – 2026-10-26

### Added
- Knowledge window, **New …**: create a new file from a template **or add an existing file** - a Markdown file (rule, risk
  rule, lesson) is copied into the folder of its kind, a skill or workflow with its whole folder (pick its `SKILL.md` /
  `WORKFLOW.md`; the name comes from its front matter). `TcAgent.exe knowledge new --key <kind>/x --path <file> --level <level>`.

## [0.41.3] – 2026-10-26

### Fixed
- Knowledge window: a new platform, skill, workflow or source with capitals or spaces in its name ("My Framework") was
  refused with a message in the status line only. Names are now made valid (`my-framework`), and a failed action
  (new, move, delete, add source) is shown as a dialog.

## [0.41.2] – 2026-10-26

### Fixed
- A test run started from the chat blocked the IDE for about 250 ms per event: the bridge handled every event of the run
  synchronously on the UI thread. The events are now handed over without waiting (background priority) - the run does not
  slow the IDE down, the TcUnit Tests window catches up when the UI is idle.

## [0.41.1] – 2026-10-26

### Fixed
- Knowledge window: the platform line next to the tabs opens the Platforms tab; an error while showing a page or filling
  the data is shown in the status bar and written to `bridge.log` instead of leaving the page unchanged.

## [0.41.0] – 2026-10-26

### Added
- **Knowledge window, Sources:** add, edit and remove sources; **Browse & install** shows what a source offers with kind
  and license and installs after a confirmation that lists license, files and scripts; Check for updates, Update all, Diff.
- **Knowledge window, Platforms:** the platforms with their detection (libraries, terms), which apply to the project and
  why, the libraries of the project; New, Open platform.md, Open folder, Set for this project, Detect automatically.
- **Knowledge window, Feedback:** the `tc_feedback` reports; the selected ones as an anonymized issue text (names of
  computer, user, solution and projects, `privateTerms`, user folders, solution and network paths, IP addresses /
  AmsNetIds replaced) that can be edited, copied, saved and shared with the TcAgent developers as a new issue in the
  browser (sent by the user; `feedbackUrl` in `settings.json`); Set status.
- CLI for the window: `TcAgent.exe sources add|remove|search|install|pull|diff`, `knowledge feedback --action issue|resolve`,
  `knowledge platform --action new|set|auto`.

## [0.40.0] – 2026-10-26

### Added
- **Knowledge window, tab Files** (formerly Effective): filter by level; **New …** (rule, risk rule, skill, workflow or lesson
  from a template, opened in the editor), **Open folder**, **Move to level …** (name check when moving up; company and a
  team in Git as a commit on a new branch for a pull request), **Delete** (Del key; own levels only), all of it also in the
  context menu.
- **Git in the knowledge window:** per level kept in a Git repository the local changes, commits to push and new commits
  in the repository (as of the last fetch), with Fetch, Pull (fast-forward), Commit and Push - a company or team level
  pushes to a new branch for a pull request; column Git per file.
- `TcAgent.exe knowledge new|move|delete|git` (`--key`, `--path`, `--level`, `--action`, `--message`) -
  `KnowledgeOps.cs`, tests `KnowledgeOpsTests`.

## [0.39.9] – 2026-10-26

### Changed
- **Skill mirror stays current by itself:** whenever the TcAgent server starts it mirrors the skills that apply everywhere
  (built-in, company, user, team) into the skill folders of the agent CLIs, in the background. Only changed skills are
  copied (folder checksum); a copy that was edited by hand is restored from its source. `TCAGENT_SKILL_MIRROR=0` switches
  it off. Copies, not links: shared levels on network drives, no writes into the source by an agent, no broken links
  after an update.

## [0.39.8] – 2026-10-26

### Fixed
- **Set up clients** created a backup of every touched configuration file on every run (hundreds of identical
  `*.bak-*` files). A file is now written only when its content changes, a backup only then, and per file the very first
  backup (the state before TcAgent) and the last 3 are kept.

## [0.39.7] – 2026-10-26

### Fixed
- **Chat after a TcAgent update:** a running chat session kept talking to the TcAgent server it started with (e.g. 0.39.2
  after installing a newer version), so fixes of the new version did not apply. Before the next question the chat now
  compares the registered server version with the one of the session and starts the session again with the new server
  (with a note in the chat).

## [0.39.6] – 2026-10-26

### Changed
- **Code changes only when asked:** asked to activate, an agent found the compiler error with its own `tc_check` and fixed
  it without asking (the gate never came into play). The chat prompt and the TcAgent guide now say: change code only when
  the user asked for a change; errors found on the way to something else are reported and the user is asked.

## [0.39.5] – 2026-10-26

### Changed
- **Activation gate:** after the refusal an agent fixed the code on its own and activated again. The refusal, the
  description of `tc_activate` and the TcAgent guide now say it clearly: show the errors and ask the user before changing
  any code - the user asked for an activation, not for a code change.

## [0.39.4] – 2026-10-26

### Fixed
- **TcUnit Tests window and runs from the chat:** a run started by an AI client (`tc_test_run`) was not shown in the
  window. TcAgent now forwards every event of the run to the window of this IDE (TcAgentBridge command `tcunit_event`):
  progress, running test, results and the final state appear live, as for a run started in the window; the window opens
  without taking the focus if it was closed. Cancel stays with the AI client.

## [0.39.3] – 2026-10-26

### Added
- `tc_test_skeleton instanceName`: the name of the instance under test (default `fbUnderTest`), so the generated tests
  follow the naming of the coding rules.

## [0.39.2] – 2026-10-26

### Fixed
- `tc_test_skeleton` (found in the first real use): every method parameter (`VAR_INPUT` and `VAR_IN_OUT`) gets a local
  variable and is passed - a call without a required input does not compile. The suite name follows the existing suites
  (e.g. `<FB>TestSuiteFB`, a trailing `FB` of the block dropped), the default folder is the one of the existing suites
  or a folder `Tests`/`Test` instead of the folder of the function block (which may be library source).

## [0.39.1] – 2026-10-26

### Fixed
- Chat window: changes of `chat-backends.json` (e.g. by **Set up clients**, which switches Copilot to ACP) took effect only
  after restarting Visual Studio - the window kept the backends it read when it was created. It now reads the file again
  after the setup and before every question, and restarts a running session whose backend settings changed.
- The `new-feature` workflow description names check and build with 0 errors.

## [0.39.0] – 2026-10-26

### Changed
- **Coding workflows end with a build with 0 errors, always preceded by a check.** The built-in `new-feature` workflow has
  a `check` step (`tc_check` until clean - a pre-compile without code generation, much faster) and then the checkpoint
  `build` (`requires: build-ok`). When the agent marks the build step done, TcAgent runs `tc_check` first and `tc_build`
  only if the check is clean; with check errors it reports them without building - several failed builds cost far more
  time than several checks.
- Guidance for every agent (tool description of `tc_build`, TcAgent guide, chat prompt, user guide): run `tc_check`
  before every `tc_build`.

## [0.38.0] – 2026-10-26

### Added
- **Chat window on the Agent Client Protocol (ACP)**: backend format `"acp"` (`vsbridge/ChatAcp.cs`) - a persistent
  session per backend (`initialize`, `session/new` with the solution folder and the tcagent MCP server, `session/prompt`),
  the answer streams live (text chunks, one line per tool call, failed tool calls), context usage in the status line,
  permission requests of the agent as permission cards (allow once / always for this session / deny), **Stop** sends
  `session/cancel` and keeps the session open. A missing sign-in is reported with the agent's own hint.
- **GitHub Copilot** is the first ACP backend: `copilot --acp --no-ask-user --allow-tool tcagent` (questions to the user
  go through `tc_ask_user`). `setup-clients.ps1` switches the former one-shot Copilot entry of `chat-backends.json` to ACP
  (also in the indented layout; a backup is kept).

### Changed
- The permission card names the asking agent instead of always "Claude".

## [0.37.0] – 2026-10-26

### Added
- **Team level** between the user and the platform: `TCAGENT_TEAM` - a shared folder or Git repository per team
  (several separated by `;`, shown as `team · <folder>`). Order: built-in → company → user → team → platform → project.
  For coding rules, risk rules, skills, workflows, lessons, knowledge and templates; skills of the team level are also
  mirrored into the agent CLIs.
- `scope=team` for `tc_coding_rules_add`, `tc_skills add`, `tc_lessons add` and `tc_sources install` (a plain shared
  folder directly; a team in Git only by promoting) and for promoting (`tc_sources promote`, `tc_improve apply`
  promote-lesson): a team in Git gets a commit on a new branch for a pull request, never pushed.
- A locked team file can be switched off by the team itself (its `disabled.json`) or the company, not by a user or a
  project. Tests in `KnowledgeLayersTests`.

### Changed
- `TCAGENT_LESSONS` (the former team lessons file) keeps working; `TCAGENT_TEAM\lessons\` replaces it.
- Concept: checkpoints belong to the step of a workflow (coding workflows end with 0 compiler errors, other workflows
  define their own); only the gate before `tc_activate` is global.

## [0.36.0] – 2026-10-26

### Added
- **Deterministic first** ([docs/DETERMINISTIC_FIRST.md](docs/DETERMINISTIC_FIRST.md)): what TcAgent already does
  without the agent, the conversions of this version and a list of candidates with effort and benefit.
- **`tc_test_skeleton`**: the TcUnit test suite of a function block, generated the same way every time - enable bools of
  the TcUnit Tests window, a complete `InitialOutputs` test (every output against its initial value) and one test per
  public method with an assertion that fails until the TODO is replaced; `VAR_IN_OUT` parameters get local variables.
  Tests: `TestSkeletonTests`.

### Changed
- **`tc_workflow action=step` on a checkpoint** whose check is missing or failed: TcAgent runs `tc_check` / `tc_build`
  itself and completes the step if it passes (before: refused, the agent had to check and call again).
- **`tc_workflow action=finish`** lists the PLC objects written during the run (`tc_plc_write`, `tc_plc_create`,
  `tc_test_skeleton`) - only in the summary, not in the run log.

## [0.35.0] – 2026-10-26

### Added
- **Window "TcAgent Knowledge"** in Visual Studio / XAE Shell (View > Other Windows, or **⋯ > Knowledge** in the chat):
  - **Overview** - KPI tiles (runs, first-time-right, failed checks, corrections and minutes per run, gate stops; the last
    10 runs against the 10 before, sparkline), the workflows, open improvement proposals, most frequent compiler errors.
  - **Effective** - every coding rule, risk rule, skill, workflow, lesson and template of every level with status and
    reason; open the file, switch it off for the project or the user, switch it on.
  - **Sources** - Git sources and installed items (commit, license, changed locally, newer upstream).
  - **History** - failed checks and corrections per run, moving average, adopted changes as markers, before/after of
    every change; export as CSV.
- **`TcAgent.exe knowledge json|disable|enable [--key <kind/name>] [--level user|project] [--solution <sln>]`** - the
  data of the window (files only, no IDE); the window itself does no work.
- README: rows for skills & workflows, self-improvement and skill sources.

## [0.34.0] – 2026-10-26

### Added
- **Knowledge sources** (`tc_sources`): Git repositories as sources of skills, workflows, coding rules, risk rules and
  lessons - `add`/`remove` (in `<level>\sources.json`), `search`, `install` (personal, project, platform), `status`,
  `pull`, `diff`, `promote` (own item one level up after the name check; company as a commit on a new branch for a pull
  request, never pushed). A clone per source in `%LOCALAPPDATA%\TcAgent\sources\`.
- **Lock file** `<level>\skills.lock.json`: source, URL, path, commit, checksum and license per installed item; updates
  only by `pull`. **`TcAgent.exe sources status|sync [--solution] [--dry-run]`** restores the pinned items in a pipeline
  (`--dry-run` only checks, exit code 1 on a difference).
- **License check** on install and pull: MIT, Apache 2.0, BSD, ISC, public domain accepted (notice copied along);
  source-available and unlicensed refused; others only with `acceptLicense`. Scripts of a skill are listed, never run.
- **Overlay skills**: `metadata.tcagent-extends: <skill>` - `tc_skills show` returns the extended skill first, then the
  additions. `tc_skills show` names source and license of an installed skill.
- Tests: `SourcesTests` (local Git repositories, no network).

## [0.33.0] – 2026-10-26

### Added
- **Run logs** of the workflows, locally in `%LOCALAPPDATA%\TcAgent\runs\<solution>\` (never in Git): workflow,
  duration, failed checks per step with the compiler error codes, checkpoint and activate stops, user corrections (a "no"
  to a confirmation, an answer other than the recommended option of `tc_ask_user`), skills used - counts and names only.
  The last 100 runs are kept, older ones are added up in `summary.json`.
- **`tc_improve`**: `runs` (KPIs per workflow: first-time-right, failed checks, corrections, minutes, trend of the last
  10 runs, most frequent compiler errors), `analyze` (proposals by fixed rules: `step-hint`, `revert`,
  `promote-lesson`, `frequent-error`, `duplicate-lesson`, `stale-tool`), `apply` (shows the change and asks the user;
  step hints go into a copy of the workflow, lessons move up to personal / platform / company - company as a commit on a
  new branch for a pull request, never pushed), `history` (every adopted change with the values before and over the
  next 10 runs), `revert`.
- **Name check** before anything moves up or into a hint: names of the computer, user, solution and projects plus
  `privateTerms` (`settings.json`, `.tcagent\config.json`).
- Tests: `ImproverTests`.

### Fixed
- User guide: the lesson scopes show the folders (one file per lesson) of 0.30.

## [0.32.0] – 2026-10-26

### Added
- **Engineering workflows** (`<level>\workflows\<name>\WORKFLOW.md`, same levels and platform as skills): the steps are
  the numbered list of the body, `1. **id** (requires: check-ok) – text`. Checkpoints `check-ok`, `build-ok`, `tests-ok`
  are enforced by TcAgent - a checkpoint step can neither be marked done before its check holds nor be skipped.
- **`tc_workflow`**: `list`, `show`, `start`, `step` (`skip`), `status`, `finish` (`abort`). A run counts failed checks,
  checkpoint stops and activate stops per step (the base for the run logs). `tc_coding_rules` also lists the workflows;
  `tc_knowledge` knows the kind `workflows`.
- **Built-in workflow `new-feature`**: requirements (skill `requirements-interview`) → change plan → code → check
  (`check-ok`) → tests (recommended) → review (skill `plc-code-review`) → save.
- Tests: `WorkflowsTests`.

### Changed
- **Hard gate before `tc_activate`**: TcAgent builds first (without "check all objects") and refuses the activation with
  build errors - before anybody is asked to confirm it. Saving stays allowed with errors. `TCAGENT_ACTIVATE_GATE=0`
  switches the gate off.

## [0.31.0] – 2026-10-26

### Added
- **Skills** in the open Agent Skills format (`<level>\skills\<name>\SKILL.md`), from the levels built-in, company, user,
  platform and project – a later level replaces a skill with the same name, `locked` / `disabled.json` as for the rules.
- **`tc_skills`**: `list`, `show`, `add` (name rules of the standard checked), `mirror`. `tc_coding_rules` ends with the
  list of skills (name + description); the agent loads a skill only when the task fits.
- **Built-in TwinCAT skills** (shipped in `knowledge\skills` next to `TcAgent.exe`; the methods of well-known open-source
  skills adopted, the content written for TwinCAT and the TcAgent tools): `requirements-interview`, `plc-debugging`,
  `plc-code-review`.
- **Mirror into the agent CLIs**: `TcAgent.exe skills mirror` (run by `setup-clients.ps1`) copies the skills that apply
  everywhere into `%USERPROFILE%\.claude\skills` and `%USERPROFILE%\.agents\skills` (or `skillMirrors` in
  `settings.json`). Only folders TcAgent created there (`.tcagent-mirror`) are updated or removed.
- `TcAgent.exe skills list|mirror [--solution <sln>] [--dry-run]`. Tests: `SkillsTests`.

## [0.30.0] – 2026-10-26

Knowledge layers ([KNOWLEDGE_LAYERS.md](docs/KNOWLEDGE_LAYERS.md)): one scheme for coding rules, risk rules, lessons,
knowledge sources and documentation templates.

### Added
- **Levels × platform**: company (`TCAGENT_COMPANY` – a network share or Git repository), user (`%LOCALAPPDATA%\TcAgent`),
  **platform** (the automation framework a project is built on: `<company or user>\platforms\<name>\`, or a repository of
  its own in `TCAGENT_PLATFORMS`) and project (`<solution>\.tcagent`). Order: company → user → company·platform →
  user·platform → project, the later one wins.
- **Platform of a project**: `.tcagent\config.json` `{"platforms": [...]}`, otherwise detected from the referenced
  libraries or terms listed in the platform's `platform.md`. Ambiguous → `tc_coding_rules` tells the agent to ask the user
  (`tc_ask_user`) and to store the answer (`tc_knowledge action=set_platform`).
- **One file per topic / lesson**: `coding-rules\<topic>.md`, `risk-rules\<topic>.md`, `lessons\<date>-<title>.md` (new
  lessons and new rules are written this way – no merge conflicts in Git); the former single files are still read.
- **Front matter in the Agent Skills format** with TcAgent's fields under `metadata`: `tcagent-locked` (a later level can
  neither replace nor switch off the file), `tcagent-force` (a user file stays against a platform file).
  `tc_coding_rules_add force=true` writes it – after the agent pointed out the contradiction and the user confirmed.
- **`disabled.json`** per level switches files off without changing them.
- **`tc_knowledge`**: `platforms`, `set_platform`, `list`, `effective` (what applies here and why), `why`, `disable` /
  `enable`, `split` (a former single file → one file per topic, after asking). Tests: `KnowledgeLayersTests`.
- Lessons: new scope `platform:<name>`; `tc_coding_rules_add scope=platform:<name>`.

### Changed
- `tc_coding_rules` shows the level of every rule file (company, user, platform, project; locked, forced) and the
  platform of the project.
- Knowledge sources and documentation templates also come from the company and platform levels.

## [0.29.0] – 2026-10-26

Client neutrality, part 1 – TcAgent works the same with every AI client (concept: `docs/KNOWLEDGE_LAYERS.md`).

### Added
- **`tc_ask_user`**: the agent asks the user when a decision or missing information blocks the work – 1-4 questions with
  options (recommended first) and a free answer, in **every** AI client. From the TcAgent Chat: a question card in the
  chat window; other clients: their own question dialog (MCP elicitation), the card in the IDE as fallback; without any
  channel the agent is told to ask in its reply. The chat prompt and the guide tell the agent to use it instead of
  ending a reply with a question (the chat still answers a CLI's own question tool). Tests: `AskQuestionsTests`.
- **`%LOCALAPPDATA%\TcAgent\settings.json`** – the single source of TcAgent's settings (`TCAGENT_*` environment variables)
  and of the registered server (path and version). `setup-clients.ps1` sets up every client from it; on the first run
  the values are taken over once from an existing client configuration (any client, not only one product). The VS
  extension reads the registered version from there.

### Changed
- **`AGENTS.md` is the single guide file in the project** (`setup-clients.ps1 -SolutionDir`): `CLAUDE.md` and
  `GEMINI.md` only import it (`@AGENTS.md`), `.github\copilot-instructions.md` points to it.
- Messages name no single AI product any more ("quit desktop apps completely" instead of one product's name).
- TcAgentBridge serves several requests at once (pipe with 4 instances): a question waiting for the user no longer
  blocks other calls.

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
  with the library references of each PLC project, in the format of the SRCI repository (`Name`, `Version`,
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

[Unreleased]: https://github.com/ThorstenBrach/TcAgent/compare/v0.27.0...HEAD
[0.27.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.26.2...v0.27.0
[0.26.2]: https://github.com/ThorstenBrach/TcAgent/compare/v0.24.3...v0.26.2
[0.24.3]: https://github.com/ThorstenBrach/TcAgent/compare/v0.23.1...v0.24.3
[0.23.1]: https://github.com/ThorstenBrach/TcAgent/compare/v0.20.4...v0.23.1
[0.20.4]: https://github.com/ThorstenBrach/TcAgent/compare/v0.18.0...v0.20.4
[0.18.0]: https://github.com/ThorstenBrach/TcAgent/compare/v0.13.0...v0.18.0
[0.13.0]: https://github.com/ThorstenBrach/TcAgent/releases/tag/v0.13.0
