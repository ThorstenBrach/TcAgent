# Tool reference

All tools of the MCP server `tcagent`. Depending on the client they appear with a prefix (e.g. `mcp__tcagent__tc_build`).
Write tools are recorded in the audit log; runtime tools only access target systems listed in `TCAGENT_ALLOW_TARGETS`.
This file is generated from `src/ToolRegistry.cs`.

## Connection and status

### `tc_instances`

Lists running Visual Studio 2022 / TcXaeShell instances with an open solution.

### `tc_attach`

Connects to a running IDE instance (also happens automatically on the first tool call if the choice is unambiguous). Select by pid or part of the solution path. Without either: the IDE TcAgent was started from (TcAgent Chat), then `TCAGENT_SOLUTION`, then the only instance with a solution open. With several open solutions the client shows a selection list (MCP elicitation, if supported); otherwise a list is returned – ask the user and call again with `pid`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `solution` | string |  | Part of the solution path to select by |
| `pid` | integer |  | Process ID of the IDE |

### `tc_dialogs`

Shows open modal dialogs of the IDE (title, text, buttons) – while one is open, every TwinCAT call waits. `action=close` closes them like the X / Cancel button (never Yes/OK/Overwrite). Normally let the user close them. Any tool that runs into such a dialog returns after ~3 s with the dialog text instead of hanging.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `list` (default) or `close` |

### `tc_open`

Starts Visual Studio 2022 (or TcXaeShell) and opens a solution.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `solutionPath` | string | yes | Full path to the .sln |
| `visible` | boolean |  | Start the IDE visible (default true) |
| `progId` | string |  | e.g. VisualStudio.DTE.17.0 or TcXaeShell.DTE.17.0 |

### `tc_status`

Shows the current connection, solution and target NetId.

### `tc_version`

Shows the version of the TcAgent server.

### `tc_coding_rules`

Returns the project's coding rules (see README, coding rules section). Call before first writing code.

No parameters.

### `tc_coding_rules_add`

Adds a rule to the coding rules ("remember ...", "from now on always ..."). The agent phrases it briefly and generally.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `rule` | string | yes | the rule as one sentence |
| `scope` | string |  | `project` (default, `.tcagent\coding-rules.md` in the solution) or `personal` |
| `section` | string |  | Heading, e.g. Names, Structure, Formatting (default: Additions) |
| `dryRun` | boolean |  | Only show what would be added |

### `tc_risk_rules`

Risk rules for the static risk analysis (`tc_doc action=risks`, section Risks of the HTML page, `TcAgent.exe doc --risks`) - Markdown
files like the coding rules: `TCAGENT_RISK_RULES` (company, files/folders `;`-separated), `%LOCALAPPDATA%\TcAgent\risk-rules.md` and `risk-rules\*.md`
(personal), `<solution>\.tcagent\risk-rules.md` and `.tcagent\risk-rules\*.md` (project, in Git). Later files win (same rule id or
built-in setting). Format and examples: [risk-rules.example.md](risk-rules.example.md).

- `## Built-in rules` - `- <id>: off | on | severity=high|medium|low | <parameter>=<n>`; parameters: `long-code` limit (300), memberLimit (150);
  `deep-nesting` limit (5), mediumLimit (7); `magic-number` labels (3).
- `## Rule <id>` - own text rule: `find` (regex, every match is a finding, one per line) or `require` (regex that must occur in each
  selected object, finding if missing); optional `unless` (regex on the statement of the match - from its line to the next `;`, so multi-line calls count), `unlessInObject` (regex, skips the object),
  `in` (impl/decl/both), `kinds` (PROGRAM/PRG, FUNCTION_BLOCK/FB, FUNCTION, METHOD, PROPERTY, ACTION, TRANSITION, GVL, DUT, INTERFACE),
  `objects` (names with `*`/`?`), `comments: keep` (match in comments and strings too - by default they are blanked), `severity`
  (default medium), `message`, `why`, `enabled: false`. Regular expressions are case-insensitive, `^`/`$` per line, 2 s timeout.
- `## Checks` - one sentence per bullet; not machine checked, returned with the results so the agent verifies them by reading the code.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `show` (default: files, all rules with settings, errors), `test` (run an own rule on the project without saving), `add` (save an own rule), `builtin` (tune a built-in rule), `check` (save an agent check) |
| `id` | string |  | add/test: rule id (letters, digits, `-`, `_`) |
| `find` / `require` | string |  | add/test: regular expression (exactly one of them) |
| `unless`, `unlessInObject` | string |  | add/test: exceptions (regex) |
| `in`, `kinds`, `objects`, `comments` | string |  | add/test: where the rule looks |
| `severity`, `message`, `why` | string |  | add/test: severity (default medium) and text of the finding |
| `rule`, `setting` | string |  | builtin: rule id and `off`, `on`, `severity=...`, `<parameter>=<n>` |
| `text` | string |  | check: the check as one sentence |
| `scope` | string |  | add/builtin/check: `project` (default) or `personal` |
| `max` | integer |  | test: max. findings shown (default 50) |
| `plcProjectPath` | string |  | test: PLC project (empty = the only one) |
| `dryRun` | boolean |  | add/builtin/check: only show the entry |

`show` and `test` only read; `add`, `builtin` and `check` write the rule file (an entry that does not parse is refused).

### `tc_docs`

Looks up documentation, read-only: InfoSys, enabled URLs, document folders and network drives.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `search` (default), `read` or `sources` |
| `query` | string |  | search: search terms |
| `url` | string |  | read: URL or file path in an enabled source |
| `offset` | integer |  | read: start at character |
| `max` | integer |  | read: max. characters (default 12000) |

### `tc_docs_add`

Adds a knowledge source (URL, folder, network drive). Parameters: `source` (required), `scope` (`project`/`personal`), `note`.

### `tc_policy`

Shows the guardrails: target systems enabled for runtime actions, read-only mode, audit log path.
`action=audit_check` (read-only) compares the day's TcAgent log (`%LOCALAPPDATA%\TcAgent\logs\tcagent-<date>.log`, every tool call of every TcAgent process, with action/dryRun) with the audit log(s) and lists changing or failed calls that have no audit entry, each with the likely reason: the call never ended in its process (IDE dialog/hang, client closed, process killed - the audit is written when a call ends), an audit write error in the log, a different audit file of that process (`TCAGENT_AUDIT`, or `%LOCALAPPDATA%` redirected for packaged clients: `%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\TcAgent`, which is read as well), or - in logs of older versions without the action - a possibly read-only action. Also lists calls audited in another file than expected and the processes of the day (version, client, audit path, mode).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `show` (default) or `audit_check` |
| `date` | string |  | audit_check: day, e.g. `2026-10-12`, `12.10.` or `yesterday` (default today) |
| `max` | integer |  | audit_check: max. listed calls (default 100) |

Audit entries now also contain `pid`, `client` and - for late outcomes - `note` (e.g. "completed after an IDE dialog", "job rf1-... finished"). The audit, the daily log and the feedback files are appended with shared access and retries, so several TcAgent processes no longer lose lines to write conflicts. Failed calls are always audited; a call that ended after an IDE dialog (reported as error) gets a second entry with its real outcome.

### `tc_feedback`

Reports a problem, missing feature or improvement suggestion to TcAgent development. Always use it when a tool fails, behaves unexpectedly, a task could only be solved via a workaround, or a tool is missing. Without `action` (= `report`) `type` and `summary` are required (backward compatible). Every entry gets a stable id (`FB-1A2B3C4D`), status `open`, the TcAgent version (`serverVersion`), client and machine name.

For maintainers (control loop): `action=list` lists entries grouped by tool (most first) and severity - default the open ones; `action=summary` counts by status, severity, type, tool and version; `action=resolve id=FB-...[,FB-...] note=... version=0.21.0 [status=resolved|wontfix|duplicate|open]` marks entries (status changes are appended as own records; the newest wins). Entries of older versions without id get a stable id derived from time, tool and summary. The Markdown report for GitHub issues comes from the CLI: `TcAgent.exe feedback [--open] [--markdown out.md] [--summary] [--path <file|folder>]`.

Store (`TCAGENT_FEEDBACK`): not set = `<install>\..\..\feedback\feedback.jsonl` (VSIX: `%LOCALAPPDATA%\TcAgent\feedback`); a `.jsonl` file = that file; a folder or UNC path (e.g. `\\server\share\tcagent-feedback`) = shared team location: every machine writes its own file per day (`feedback-<yyyyMMdd>-<MACHINE>.jsonl`, no write conflicts), all `*.jsonl` there are read.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `report` (default), `list`, `summary` or `resolve` |
| `type` | string | report | bug / missing_feature / usability / docs |
| `summary` | string | report | One sentence: what is the problem / what is missing |
| `tool` | string |  | report: affected tool, e.g. tc_plc_create; list: only tools containing this text |
| `details` | string |  | Call/arguments, error message, context (TwinCAT version, project type) |
| `expected` | string |  | What should have happened |
| `workaround` | string |  | How the task was solved anyway (if at all) |
| `severity` | string |  | blocker / major / minor |
| `status` | string |  | list: open (default), resolved, wontfix, duplicate or all; resolve: new status (default resolved) |
| `id` | string |  | resolve: entry id(s), comma-separated |
| `note` | string |  | resolve: what was done / why |
| `version` | string |  | resolve: TcAgent version with the fix |
| `max` | integer |  | list: max. entries (default 200) |

### `tc_knowledge`

The knowledge the agent gets and where it comes from – levels company (`TCAGENT_COMPANY`), user (`%LOCALAPPDATA%\TcAgent`), project (`<solution>\.tcagent`) and the **platform** (automation framework) of the project ([KNOWLEDGE_LAYERS.md](KNOWLEDGE_LAYERS.md)). The platform comes from `.tcagent\config.json` or is detected from the referenced libraries / terms listed in a platform's `platform.md`; if several match, the agent asks the user (`tc_ask_user`) and stores the answer. Per kind, one file per topic (`coding-rules\<name>.md`, `lessons\<file>.md` …) and the former single files; a later level replaces a rule file with the same name, `tcagent-locked` files cannot be replaced or switched off, a user file with `tcagent-force` stays against a platform file, `disabled.json` switches files off without changing them.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | `platforms`, `set_platform`, `list`, `effective`, `why`, `disable`, `enable` or `split` |
| `kind` | string |  | list/split: `coding-rules`, `risk-rules`, `lessons` or `doc-templates` (list: default all) |
| `platforms` | string |  | set_platform: comma-separated names or `none` |
| `name` | string |  | why: file name, e.g. `naming` or `coding-rules/naming` |
| `key` | string |  | disable/enable: `<kind>/<name>` |
| `scope` | string |  | disable/enable/split: `project` (default) or `user` |
| `dryRun` | boolean |  | split: only show the files |

### `tc_skills`

Skills = how this team does a kind of task, in the open [Agent Skills](https://agentskills.io/specification) format (`<level>\skills\<name>\SKILL.md` with `name`, `description`, TcAgent's fields under `metadata`, optional further files). From the levels built-in (shipped: `requirements-interview`, `plc-debugging`, `plc-code-review`), company, user, platform and project – a later level replaces a skill with the same name. `tc_coding_rules` also returns the list (name and description); the agent loads a skill only when the task fits. `mirror` (also `TcAgent.exe skills mirror`, run by the client setup and – since 0.39.9 – in the background whenever the TcAgent server starts; only changed skills are copied, `TCAGENT_SKILL_MIRROR=0` switches that off) copies the skills that apply everywhere into the skill folders of the agent CLIs (`%USERPROFILE%\.claude\skills`, `%USERPROFILE%\.agents\skills`, or `skillMirrors` in `settings.json`); only folders TcAgent created there (marker `.tcagent-mirror`) are updated or removed.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | `list`, `show`, `add` or `mirror` |
| `name` | string |  | show/add: skill name (lowercase letters, digits, hyphens) |
| `description` | string |  | add: what the skill does and when to use it |
| `body` | string |  | add: the instructions (Markdown) |
| `inspiredBy` | string |  | add: source of the method the skill adopts |
| `scope` | string |  | add: `project` (default), `personal` or `platform:<name>` |
| `dryRun` | boolean |  | add/mirror: only show what would be written |

### `tc_workflow`

Engineering workflows = how this team **always** does a kind of task, with checkpoints. `<level>\workflows\<name>\WORKFLOW.md`
(front matter like a skill); the steps are the numbered list of the body: `1. **id** (requires: check-ok) – text`. From
the levels built-in (shipped: `new-feature` – requirements → change plan → code → check → build (0 errors) → tests → review → save), company,
user, platform and project; `tc_coding_rules` also returns the list. A run is done step by step; TcAgent counts failed
checks per step and refuses to mark a checkpoint step done until its check holds:

| Checkpoint | Holds when |
|---|---|
| `check-ok` | the last `tc_check` / `tc_build` during the step had 0 compiler errors |
| `build-ok` | the last `tc_build` during the step succeeded |
| `tests-ok` | the last `tc_test_run` / `tc_test_results` during the step had no failed test |

Checkpoint steps cannot be skipped. When the agent marks a `check-ok` / `build-ok` step done while its check is missing
or failed, TcAgent runs `tc_check` itself (since 0.36) and completes the step if it passes; for `build-ok` (since 0.39) it runs
`tc_check` first and `tc_build` only when the check is clean – otherwise it reports the check errors without building. `finish` lists
the PLC objects written during the run. The gate before `tc_activate` holds with and without a workflow.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | `list`, `show`, `start`, `step`, `status` or `finish` |
| `name` | string |  | show/start: workflow name |
| `task` | string |  | start: the task in a few words |
| `step` | string |  | step: id of the step that is done (optional – the active one) |
| `skip` | boolean |  | step: the step does not apply to this task |
| `note` | string |  | step: short note |
| `abort` | boolean |  | finish: stop the run before all steps are done |

### `tc_improve`

Self-improvement from the local run logs of the workflows (`%LOCALAPPDATA%\TcAgent\runs\<solution>\`, last 100 runs,
counts and names only). Proposals are found by fixed rules; the agent writes only the text of a step hint, and every
change is shown to the user and asked for (with `TCAGENT_CONFIRM=off` without a question).

| Proposal | Found when | `apply` |
|---|---|---|
| `step-hint` | a step needed ≥ 1.5 failed checks per run over the last runs (at least 5) | adds `Hint: <text>` to the step – in a copy of the workflow at `scope` (default project) |
| `revert` | an adopted change did not help over the next 10 runs | restores the file |
| `promote-lesson` | the same lesson exists in two or more projects | copies it to `personal`, `platform:<name>` or `company` (commit on a new branch, not pushed) |
| `frequent-error` | a compiler error code in 3 or more of the last 10 runs, no lesson mentions it | – (record a lesson with `tc_lessons`) |
| `duplicate-lesson` | two lesson files with the same title in one folder | – (merge them) |
| `stale-tool` | a skill or workflow names a tool that does not exist | – (correct the text) |

Hints and promoted lessons are refused while they name this computer, user, solution, a project, or a term of
`privateTerms` (`settings.json`, `.tcagent\config.json`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | `runs`, `analyze`, `apply`, `history` or `revert` |
| `workflow` | string |  | runs: only this workflow |
| `id` | string |  | apply: proposal id from `analyze`; revert: change id from `history` |
| `text` | string |  | apply `step-hint`: the hint (general, no customer or machine names) |
| `scope` | string |  | apply: step-hint `project` / `personal` / `platform:<name>`; promote-lesson `personal` / `platform:<name>` / `company` |
| `dryRun` | boolean |  | apply/revert: only show the change |

### `tc_sources`

Git repositories as sources of skills, workflows, coding rules, risk rules and lessons – an external skill collection
or a team, platform or company repository. Layout as usual in the Agent Skills ecosystem: folders with `SKILL.md` or
`WORKFLOW.md`, Markdown files in folders named `coding-rules`, `risk-rules` or `lessons`. The sources of a level are in
`<level>\sources.json`; TcAgent keeps a clone per source in `%LOCALAPPDATA%\TcAgent\sources\`. Installed items are pinned
in `<level>\skills.lock.json` (source, URL, path, commit, checksum, license) – `TcAgent.exe sources sync` restores exactly
these in a pipeline.

- **License** (the item's LICENSE file, its `license` field, the repository's LICENSE): MIT, Apache 2.0, BSD, ISC and
  public-domain style are accepted, the notice is copied along; source-available licenses and items without a license
  are refused; others (e.g. copyleft) only with `acceptLicense=true`. Recorded as `metadata.tcagent-license`, the origin
  as `tcagent-source` / `tcagent-commit`.
- **Scripts** in a skill are listed before the install – TcAgent never runs them.
- **Your changes** of an installed item: `changes` shows them (diff against the installed version); `merge` takes a newer
  version of the source into the changed item (three-way per file, `dryRun` first; conflicts are marked with `<<<<<<<`, the
  item is backed up to `%LOCALAPPDATA%\TcAgent\backups\sources\` first); `propose message=…` prepares them for the source
  as a commit on a new branch `tcagent/<user>-<item>-<date>` in `%LOCALAPPDATA%\TcAgent\contrib\<source>` plus a patch file –
  without TcAgent's own metadata, checked for private terms, never pushed by the agent (the user pushes in the knowledge
  window, to the source or a fork, and the pull request page opens).
- `install`, `pull` and `promote` show what they do and ask the user (with `TCAGENT_CONFIRM=off` without a question).
- **Overlay:** an own skill with `metadata.tcagent-extends: <skill>` shows the extended skill first and its own text as
  additions – TwinCAT parts for a general skill without changing the original.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | `list`, `add`, `remove`, `search`, `install`, `status`, `pull`, `diff` or `promote` |
| `name` | string |  | add/remove: source name |
| `url` | string |  | add: Git URL or path of the repository |
| `ref` | string |  | add: branch or tag (default `HEAD`) |
| `item` | string |  | install/pull/diff: `<source>/<name>`; promote: `<kind>/<name>`, e.g. `skills/driver-fb` |
| `kind` | string |  | install: `skills`, `workflows`, `coding-rules`, `risk-rules` or `lessons` (if a name exists in several kinds) |
| `query` | string |  | search: words in name or description |
| `scope` | string |  | add: `personal` / `project`; install: `personal` / `project` / `platform:<name>`; promote: `personal` / `platform:<name>` / `company` (commit on a new branch, not pushed) |
| `acceptLicense` | boolean |  | install/pull: take over a license that is neither accepted nor refused |
| `force` | boolean |  | install: overwrite an own file; pull: also update locally changed items |
| `dryRun` | boolean |  | install/pull/promote: only show |

### `tc_test_skeleton`

Generates the TcUnit test suite of a function block deterministically (docs/DETERMINISTIC_FIRST.md): `<FB>_Test EXTENDS
TcUnit.FB_TestSuite` with the enable bools of the TcUnit Tests window (`bRunSuite`, `bRun_<Test>`), a complete test
`InitialOutputs` (a fresh instance, one call, every output with a comparable type against its declared initial value or
the default of its type) and one test `Test_<Method>` per public method – arrange (the inputs as comments), act (the
call with every parameter as a local variable), and an assertion that **fails until the TODO is replaced**. The suite name and folder follow the existing suites of the project (e.g. `<FB>TestSuiteFB` in their folder, or a folder `Tests`). Private
and protected methods and `FB_init`/`FB_exit`/`FB_reinit` are skipped. The agent then writes the expected behaviour and
declares an instance of the suite in the test program.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | the function block (name or tree path) |
| `suiteName` | string |  | name of the suite (default: in the style of the existing suites, otherwise `<FB>_Test`) |
| `instanceName` | string |  | name of the instance under test (default `fbUnderTest` – follow the coding rules) |
| `parentPath` | string |  | folder for the suite (default: the folder of the existing suites, a folder `Tests`/`Test`, otherwise the folder of the function block) |
| `dryRun` | boolean |  | only show the generated code (plain-text ST) |

### `tc_ask_user`

Asks the user when a decision or missing information blocks the work: 1-4 questions, each with 2-4 options (recommended option first, with a short description); the user can always answer in free text. Works in every AI client: from the TcAgent Chat a question card in the chat window of the IDE; in other clients the client's own question dialog (MCP elicitation), with the card in the IDE as fallback. Without any channel the agent is told to ask in its reply instead. Returns the answers per question; a skipped question is reported as such - the agent must not invent an answer.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `questions` | array | yes | `[{question, header?, options: [{label, description?}], multiSelect?}]` – 1-4 questions, at most 6 options each |

## Project tree, configuration, I/O

### `tc_tree`

Shows the TwinCAT project tree (name, type, path). Without path: all roots (TIPC=PLC, TIID=I/O, TIRT=tasks, TISC=TwinSAFE if present ...).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string |  | Start path, e.g. TIPC or TIID^Device 1 (EtherCAT) |
| `depth` | integer |  | Depth (default 2) |

### `tc_safety`

TwinSAFE, **read only**. `action=list` (default) shows the safety configuration: the tree below `TISC` (the SAFETY node; also listed by `tc_tree` without path) with type/subtype/path per item and the values recognized in each item's ProduceXml – FSoE address, watchdog, connection ID, safe parameters (module fault is ComError, ErrAck ...), CRC/checksums – then summaries of TwinSAFE groups, alias devices, connections, safety parameters and CRCs, and the safety terminals of the I/O configuration (EL69xx, EL19xx/EL29xx, EK1960 ... by product name). If nothing is recognized, the shortened raw XML of the safety node is returned. Without a `TISC` node the project files on disk are read instead (`*.splcproj`, `*.sal` = group application, `*.sds` = alias device). `action=show path=...` returns one item with all recognized values, its children and the shortened ProduceXml; `action=files` reads the safety project files on disk.

Writing safety configuration is intentionally **not supported**: tc_safety never calls ConsumeXml, CreateChild, DeleteChild or a download, and every changing tool (`tc_xml_set`, `tc_delete`, `tc_link` ...) refuses paths in the `TISC` subtree. Changes, verification (CRC) and download of a TwinSAFE project belong to a person in the TwinSAFE editor. The online CRC of the safety logic on the target is not read.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | list (default), show or files |
| `path` | string |  | show: tree path of a safety item (from action=list) |
| `depth` | integer |  | list: tree depth below the safety node (default 4) |
| `maxChars` | integer |  | max. characters of raw XML per item (default 3000) |

### `tc_xml_get`

Reads the XML description of a tree item (ProduceXml): parameters, I/O configuration, task settings, online settings ...

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Tree path |
| `recursive` | boolean |  | Include children |

### `tc_xml_set`

Writes XML to a tree item (ConsumeXml) - changes configuration. Read the format with tc_xml_get first.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Tree path |
| `xml` | string | yes | XML in ProduceXml format (only the parts to change) |

### `tc_delete`

Deletes a child item in the tree. A backup is written first to `<solution>\.tcagent\backup\delete` (PLC objects as PLCopen XML – restore with `tc_import kind=plcopen`, other items as ProduceXml).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `parentPath` | string | yes | Path of the parent item |
| `name` | string | yes | Name of the child |

### `tc_io_add`

Creates I/O configuration: EtherCAT master under TIID, below it couplers and terminals by product name. With `children` a whole station is created in one call.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | yes | `ethercat_master`, `box` (coupler/terminal/box, needs `product`) or `raw` (with `subType`) |
| `parentPath` | string |  | Parent item (default `TIID`), e.g. `TIID^Device 1 (EtherCAT)` |
| `product` | string |  | e.g. `EK1100` or `EL6001-0000-0020` |
| `children` | string |  | further terminals under the new item, e.g. `EL1008,EL2008,EL6001` |
| `name` | string |  | Name in the tree (empty = `Term n (EK1100)`) |
| `subType` | integer |  | `raw` only: TwinCAT subtype |
| `dryRun` | boolean |  | Only show what would be created |

### `tc_io_scan`

Searches the I/O devices of the target (network adapters with EtherCAT, fieldbus cards ...) – read only. Needs config mode (`tc_runtime_mode mode=config`). Take one over with `tc_io_scan_add`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `raw` | boolean |  | Also show the XML of the devices found |

### `tc_io_scan_add`

Takes over a device found by `tc_io_scan`: creates it, or – with `devicePath` – assigns its adapter to an existing device (like *Adapter > Search* in the IDE). Then scans the boxes/terminals behind it. With terminals configured offline TwinCAT may ask – TcAgent then reports the dialog.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | integer | yes | Number of the device from `tc_io_scan` |
| `devicePath` | string |  | Existing device to assign the adapter to, e.g. `TIID^Device 1 (EtherCAT)` (empty = create new) |
| `name` | string |  | Name of a new device |
| `scanBoxes` | boolean |  | Scan boxes/terminals (default true) |
| `dryRun` | boolean |  | Only show what would happen |

### `tc_link`

Links PLC variables to I/O channels, one at a time or many at once. PLC variables only appear after `tc_build` if declared with `AT %I*`/`AT %Q*`.

Structures and groups in one step (like "Change Multi Link" in the IDE): if both sides of a pair have sub-items - a PLC STRUCT/ARRAY variable and a PDO group (e.g. `MAIN.SerialCom.ComIn` => `...^Term 2 (EL6001)^COM Inputs`) or two structured variables - the pair is expanded into single links: members with the same name first (`Status` => `Status`), the rest in tree order, arrays element by element (`D[0..21]` => `Data In 0..21`). Every single link is reported with how it was matched (by name / by order); `dryRun=true` shows all pairs without linking. If source elements remain unmatched, nothing is linked for that pair and the pairs found so far are listed for an explicit `pairs` call; extra target entries are reported as left unlinked. Struct members are taken from the tree children of the variable, or from flat siblings named `<var>.<member>`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | string |  | First variable path (variable, struct/array variable or group) |
| `target` | string |  | Second variable path |
| `pairs` | string |  | several links, one `<path> => <path>` per line (each line is expanded the same way) |
| `elementwise` | boolean |  | expand struct/group pairs into single links (default true; false = link exactly the given items) |
| `dryRun` | boolean |  | only check the paths and show every single link that would be created |

### `tc_io_vars`

Lists linkable variables under a tree item (terminal, device, PLC instance) with type - the exact paths for `tc_link`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Tree item |
| `filter` | string |  | only paths containing this text |
| `max` | integer |  | max. entries (default 300) |

### `tc_unlink`

Removes links of a variable (optionally only to target).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | string | yes | Variable path |
| `target` | string |  | Counterpart (empty = all) |

## PLC code

### `tc_plc_read`

Reads declaration and implementation of a PLC object (POU, method, property, DUT, GVL) including child objects.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Axis |

### `tc_plc_write`

Overwrites declaration and/or implementation of a PLC object (ST text). With dryRun=true (always in 'propose' mode) nothing is written; a diff is returned instead.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Tree path of the object |
| `declaration` | string |  | complete declaration part |
| `implementation` | string |  | complete implementation part |
| `dryRun` | boolean |  | Only show the diff, change nothing |

### `tc_plc_create`

Creates a PLC object and optionally sets declaration/implementation. kind: folder, program, function, function_block, dut_enum, dut_struct, dut_union, action, method, interface_method, property, interface_property, property_get, property_set, gvl, transition, interface, dut_alias

Properties automatically get Get and Set accessors. With `implements` it creates an FB with all methods/properties of the interface as stubs.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `parentPath` | string | yes | Parent folder/POU, e.g. ...^POUs or ...^POUs^FB_Axis for methods |
| `name` | string | yes | Object name |
| `kind` | string | yes | Object type |
| `language` | string |  | ST (default), IL, SFC, FBD, CFC, LD |
| `returnType` | string |  | Return type for function/method/property |
| `declaration` | string |  | complete declaration part |
| `implementation` | string |  | implementation part (for property: the Get accessor) |
| `implements` | string |  | function_block only: interface(s), comma-separated; creates IMPLEMENTS and all members (incl. EXTENDS) as stubs – project and library interfaces |
| `getImplementation` | string |  | property: code of the Get accessor |
| `setImplementation` | string |  | property: code of the Set accessor |
| `dryRun` | boolean |  | Only show what would be created |

### `tc_save`

Saves all modified files of the solution to disk (like Ctrl+Shift+S). Otherwise tool changes exist only in the IDE.

### `tc_plc_search`

Searches declarations and implementations of all PLC objects (full text or regex). Hits with tree path, part and line - e.g. to find usages of an FB, a variable or a call.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `pattern` | string | yes | Search text or regular expression |
| `regex` | boolean |  | treat pattern as regex (default false) |
| `caseSensitive` | boolean |  | Match case (default false) |
| `part` | string |  | decl, impl or all (default) |
| `path` | string |  | Only search below this tree path (default: all PLC projects) |
| `max` | integer |  | Max. hits (default 200) |

### `tc_sfc`

Sequential function charts (SFC step chains) as text. `action=read` returns the chain (steps, actions with qualifiers, transitions, `ALTERNATIVE`/`PARALLEL` branches, jumps) and the action/transition objects of the POU with their code; `action=write` replaces the chain with the given text; `action=xml` shows the raw PLCopen SFC body. Works through PLCopen XML export/import of the POU (the POU is re-created in its folder; the previous version is restored if TwinCAT rejects the chain). Step names become variables of the POU – never name a step like a variable, action or keyword (use `S_Init`, `S_Move` …). Create the POU with `tc_plc_create language=SFC`, actions/transitions with code with `tc_plc_create kind=action|transition`.

Under a step, `EMBEDDED <action>` names an embedded step action (main/entry/exit, e.g. `_aN010_active`) and `COMMENT <text>` the step comment. Keep these lines when writing – also when renaming the step; a write that would detach embedded actions is refused unless `force=true`. Graphical action objects are listed as such, not as empty.

**Duplicate on copy.** The step property "Duplication" (addData attribute `62e1754b-…`, identified from the attribute descriptions TwinCAT stores in the editor XML of the `.TcPOU`) decides whether copying a step in the editor copies its embedded actions or only references them. `action=write` gives new steps this property (`duplicate=new`, default); `duplicate=all` also switches it on for existing steps, `duplicate=none` keeps the TwinCAT default (off). `action=read` lists which steps have it (`// Duplicate on copy: …` line, ignored when the text is written back). New steps also accept `COMMENT` lines now (except with `duplicate=none`).

**Renumber** (`action=renumber`): renames steps to `<prefix><number>` (`start`, `step`, `digits`; prefix default = the existing one). Order `main` numbers the main sequence first, then each branch in order (recursively); `order=chain` uses the order of `action=read`; `steps` limits it to marked steps (list or range). The export is edited in place: step names, `JUMP` targets, step references in transition conditions and in the code of the POU's actions/transitions (`N010.t` → `N020.t`, all names at once, so swapped numbers stay correct), embedded step actions `_aN010_active` → `_aN020_active` (object and step attribute together; only if exactly this step uses the action and the new name is free – otherwise the name is kept and the result says why); comments, times, entry/exit actions and all other step data stay. New names are checked against steps that keep their names, variables, actions/transitions and keywords. References from outside the POU (`fbSeq.N010.x`) are listed and block the write unless `force=true` – fix them afterwards. Always run `dryRun=true` first: it shows the old → new table and the new chain.

**Comments** (`action=comments`): without `comments` it lists every step with its comment and embedded actions plus the project's coding guidelines (`tc_coding_rules`) so the agent can propose comments; with `comments={"N010": "…"}` it sets the comments of existing steps. If the guidelines require English, comments that look German are refused (`force=true` after asking the user).

**Flags** (`action=flags`): shows the SFC flags of the POU (`SFCInit`, `SFCReset`, `SFCQuitError`, `SFCPause`, `SFCError`, `SFCTrans`, `SFCErrorStep`, `SFCErrorPOU`, `SFCCurrentStep`, `SFCTip`, `SFCTipMode`, `SFCEnableLimit`, `SFCErrorAnalyzation`, `SFCErrorAnalyzationTable`) with Use/Declare, whether the variable is declared in the POU and the matching VSProperties of the POU item (ProduceXml). `flags={"SFCInit": true}` sets them: through the POU's VSProperties (ConsumeXml with only the changed properties) if TwinCAT offers them for all requested flags, otherwise through the SFC settings of the PLCopen export (re-import with rollback). A flag with Use=true and Declare=false breaks the build ("SFC flag variable 'SFCInit' is not defined") unless the variable is declared: enabling sets Declare=true (PLCopen path, or a matching Declare VSProperty) or adds `SFCInit : BOOL;` to the first `VAR` block of the declaration (VSProperty path without Declare property); if the variable is already declared, Declare stays false. The result says which. `UseDefaults=true` is switched off, otherwise the POU ignores its own flags.

```
STEP Init INITIAL
  ACTION N A_Init
TRANSITION bStart
STEP Run
ALTERNATIVE
  BRANCH
    TRANSITION bError
    STEP Error
    TRANSITION bAck
    JUMP Init
  BRANCH
    TRANSITION bDone
    STEP Done
END_ALTERNATIVE
TRANSITION TRUE
JUMP Init
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | read, write, xml, renumber, comments or flags |
| `path` | string | yes | tree path of the SFC POU, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Sequence |
| `chain` | string |  | write: the complete chain in the text form of action=read |
| `dryRun` | boolean |  | write/renumber/comments/flags: only show what would change |
| `force` | boolean |  | write: also when embedded step actions would lose their step; renumber: also when code outside the POU references renamed steps; comments: also when they do not look like the language the guidelines require (only after asking the user) |
| `duplicate` | string |  | write: `new` (default) = new steps get "Duplicate on copy", `all` = also existing steps, `none` = TwinCAT default |
| `prefix` | string |  | renumber: name prefix (default: the existing prefix, e.g. `N`) |
| `start` | integer |  | renumber: first number (default: number of the first selected step, e.g. N000 -> 0; otherwise 10) |
| `step` | integer |  | renumber: increment (default 10) |
| `digits` | integer |  | renumber: minimum digits, zero-padded (default 3 → `N010`) |
| `steps` | string |  | renumber: only these steps – comma-separated (`N020,N030`) or a range in the chosen order (`N020..N070`); default all |
| `order` | string |  | renumber: `chain` (default: order of action=read) or `main` (main sequence first, then the branches in order) |
| `renameActions` | boolean |  | renumber: rename embedded step actions `_a<old>_active` → `_a<new>_active` (default true) |
| `comments` | object |  | comments: `{"N010": "Wait for part present", ...}` – empty text removes a comment |
| `flags` | object |  | flags: `{"SFCInit": true, "SFCReset": false, ...}` |

## Editor context and navigation (TcAgentBridge)

### `tc_lessons`

Lessons learned across sessions: solutions to hard problems while coding or configuring TwinCAT. Each lesson records **what** the problem was (`symptom`: exact error text/code, behaviour), **why** (`cause`: explanation/background) and **how** it was solved (`solution`: concrete steps, values, code), optionally what did not work (`avoid`). Stored as Markdown, editable by hand: `<solution>\.tcagent\lessons.md` (project, versioned with Git), `%LOCALAPPDATA%\TcAgent\lessons.md` (personal), `TCAGENT_LESSONS` (team file). When a tool fails or `tc_check`/`tc_build` reports errors, matching lessons are appended to the result automatically (`TCAGENT_LESSONS_RECALL=0` turns this off); when a call succeeds after repeated failures the agent is reminded to save a lesson. `add`/`remove` count as project changes (audited, blocked in `ask` mode); `search`/`list` are read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | search, list, add or remove |
| `query` | string |  | search: error text, error code or topic |
| `tool` | string |  | search: ranks lessons of this tool higher; add: tool(s) involved |
| `title` | string |  | add: short, searchable title |
| `symptom` | string |  | add (required): the problem – exact error text/code, behaviour, situation |
| `cause` | string |  | add (required): why it happens – explanation, background; "unclear" if not known |
| `solution` | string |  | add (required): exactly how it was solved – steps, parameters, values, code |
| `avoid` | string |  | add: what did not work |
| `tags` | string |  | add: comma-separated |
| `scope` | string |  | add: project (default), personal or team |
| `id` | string |  | add: update this lesson; remove: lesson to delete |
| `max` | integer |  | search: max. results (default 5) |
| `dryRun` | boolean |  | add: only show the entry |

### `tc_userdoc`

User manual of function blocks, functions, interfaces and their data types (how to use them). `action=templates` lists example manuals (`<solution>\.tcagent\doc-templates`, `%LOCALAPPDATA%\TcAgent\doc-templates`, `TCAGENT_DOC_TEMPLATES`); `action=template name=...` returns one – the agent imitates its structure, style and format (Markdown, AsciiDoc or HTML). `action=data objects=...` (names, tree paths, a folder or `selection`) returns descriptions, inputs/outputs/in-outs, public methods and properties with parameters and all STRUCTs/ENUMs/aliases they use, with existing comments and missing ones marked, plus the code. `action=write file=... content=...` saves the document (default `<solution>\doc\user`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | templates, template, data or write |
| `name` | string |  | template: file name |
| `objects` | string |  | data: names, tree paths, a folder path or `selection` |
| `code` | boolean |  | data: include the implementation code (default true) |
| `plcProjectPath` | string |  | PLC project (empty = the only one) |
| `file` | string |  | write: file name/path (.md, .adoc, .html) |
| `content` | string |  | write: the complete document |

### `tc_refactor`

Context-based refactoring of names and comments, always confirmed by the user per change (dialog in Visual Studio or the client's question: **Apply / Apply all / Reject / Cancel**). `action=analyze` (target = `selection`: the marked range or the object open/clicked in the editor – method, FB, function, struct, enum – or a name/tree path) returns numbered code, declarations with comments and findings: missing comments, unclear names, external use (callers/ADS, I/O `AT %`, TcLinkTo, persistent/retain, HMI files). `action=apply changes=[{object, kind: comment|rename, target: variable|member|pou|method, name, newName, comment, reason}]` asks per change and applies it: comments as `// ...` on the declaration line or `/// ...` above the header; renames as whole-word edits outside strings and comments in the object (and its members/derived FBs for FB variables) and – for names visible outside – in every object of TwinCAT's cross references (needs TcAgentBridge). `dryRun=true` shows all edits as diffs.

`apply` runs as a background job, so a client behind a remote bridge with a call timeout (~60 s) does not lose the report while the user is still clicking: if not every change is decided after `waitSeconds` (default 45, `TCAGENT_JOB_WAIT`), the call returns *"Still waiting for the user's decisions - k of n done ... call tc_refactor action=result job=<id>"* (with the report so far) and the job keeps running; `action=result job=<id>` waits up to `waitSeconds` again and returns the final report (or the progress). It also returns early when a modal IDE dialog blocks the job. Only one apply job runs at a time; the last 5 jobs are kept in memory (not across a TcAgent restart). The confirmations run on the job thread (bridge dialog or MCP elicitation - answers are routed to it while the server keeps handling calls), every edit runs on the STA thread; an object that was changed after its change was planned is not overwritten (that change is reported as failed). A job that finished after its apply call returned is audited separately (`note: job ... finished`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | analyze, apply or result |
| `target` | string |  | analyze: `selection` (default), name or tree path |
| `changes` | array |  | apply: list of changes |
| `dryRun` | boolean |  | apply: only show the edits |
| `job` | string |  | result: job id from apply (default: the latest job) |
| `waitSeconds` | integer |  | apply/result: max. wait for the user's decisions before returning the progress (default 45) |

### `tc_doc`

Program documentation of a PLC project (project memory). `action=pending` lists every object (FB, method, property, action, function, interface) that has neither a comment description nor a stored explanation, with its code, so the agent writes a short description for each (`action=save summaries={...}` stores them without writing the page). The page also has an inheritance hierarchy (diagram and tree, library base classes included) and short descriptions in the call hierarchy. `action=outline` returns the call hierarchy (task -> program -> FBs/methods/functions), every object with its description, step chains (SFC and CASE state machines) and findings; `action=html` writes one self-contained HTML page with clickable call hierarchy, call graph, class diagram (EXTENDS/IMPLEMENTS, methods, properties), step chain diagrams, findings and a reference per object (description, explanation, inputs/outputs, calls, used by, code); `action=memory` returns the stored overview and explanations. Explanations are kept in `<solution>\.tcagent\doc-summaries.json` and reused by later runs and by `TcAgent.exe doc`. Diagrams use Mermaid (needs internet access in the browser; the hierarchy tree works offline).

Tasks: the PLC task references (TcTTO items directly below the PLC project) with their program calls are read, cycle time and priority come from the real-time task of the same name under TIRT; outline and page show `task (cycle 10 ms, priority 20) -> programs`, other TIRT tasks are listed separately. Findings: "PLC project has no task reference - no process image (tc_task action=assign)", task reference without TIRT task, task without program call, tasks with the same priority.

`action=risks` runs static, text based risk rules on the ST code (no compiler; results also in the outline as counts with the high ones, and as section **Risks** of the HTML page). Each entry: severity (high/medium/low), object, line (in the implementation; `(decl)` = declaration), rule id and explanation. Rules only report what they can resolve (declared variables of the object, its FB, base FBs, GVLs); unknown identifiers (library constants, enum values) are never reported.

| Rule | Severity | What is reported |
|---|---|---|
| `div-zero` | high/medium | `a / b`, `a MOD b` with a declared variable `b` that the object never compares (`b <> 0`, `IF b = 0` ...) or limits (MAX/LIMIT/SEL). High for integer/TIME (exception, PLC stop), medium for REAL/LREAL and structure members |
| `array-index` | high/medium | `arr[i]` on an ARRAY where `i` is no FOR loop variable, no constant and never compared or limited (LIMIT/MIN/MAX/MOD) in the object. High for writes, medium for reads. Off when the project has `CheckBounds` |
| `pointer-deref` | high/medium | `p^` on a POINTER without `p <> 0` check in the FB/object (high); REFERENCE TO used without `__ISVALIDREF` (medium). Once per FB |
| `loop-exit` | medium | WHILE/REPEAT without EXIT/RETURN whose body neither assigns nor calls a variable of the condition (conditions with function calls are skipped) |
| `global-multi-task` | high | a GVL or PROGRAM variable written by objects reached from more than one task (task -> programs -> call graph) |
| `output-multi-write` | medium | an output `AT %Q` (PROGRAM or GVL) written in more than one object (program, action, method, FB) |
| `unused-var` | low | locals never used, inputs never read (PROGRAM/FB including methods, actions, properties, derived FBs; METHOD locals). Skipped: AT-mapped, `{attribute}` above, FBs extending another FB (TcUnit suites), graphical code |
| `timer-in-method` | high | TON/TOF/TP/R_TRIG/F_TRIG/CTU... in VAR/VAR_TEMP of a METHOD or FUNCTION (re-initialized on every call) |
| `timer-conditional` | medium | a timer/edge instance of a PROGRAM/FB that is only called inside IF/CASE/loops |
| `case-else` | low | CASE without ELSE |
| `magic-number` | low | CASE with 3 or more numeric labels (use an ENUM) |
| `long-code` | low/medium | more than 300 code lines (POU) / 150 (method, action, property); more than twice: medium |
| `deep-nesting` | low/medium | IF/CASE/FOR/WHILE/REPEAT nested deeper than 5 (low) / 7 (medium) |

Own rules, thresholds, switched-off rules and agent checks: `risk-rules.md` files, see `tc_risk_rules`. The text output and the page list the own rules that fired and the agent checks.

Pipeline: `TcAgent.exe doc --solution X.sln --risks [--fail-on high]` prints the list (with the risk-rules.md files of the solution) and returns exit code 1 when there are risks of that severity or higher; errors in the rule files are printed as `RISK RULES ERROR`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | outline, pending, save, html, memory or risks |
| `max` | integer |  | pending: objects per call (default 15); risks: max. entries (default 200) |
| `minSeverity` | string |  | risks: high, medium or low (default low = all) |
| `filter` | string |  | risks: only this object (part of the name) or this rule id, e.g. div-zero |
| `maxChars` | integer |  | pending: max. code characters per object (default 2500) |
| `plcProjectPath` | string |  | PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = the only one) |
| `file` | string |  | html: target file (default `<solution>\doc\<PLC project>.html`) |
| `overview` | string |  | html: how the program works – tasks, main sequence, important blocks, interfaces |
| `summaries` | object |  | html: `{"FB_Axis": "what it does", "FB_Axis.MoveAbs": "..."}` – explanations per object (keys as in outline) |

### `tc_editor_context`

Shows what the user is currently working on in the IDE: active window/document, selected items, tree paths derived from them (usable directly with tc_plc_read) and - with the VS extension TcAgentBridge - cursor and selection. Use when the user talks about 'here', 'this FB' or 'the method'.

The first tree path is always the active document, resolved uniquely from its file path (`...\SdkControlFB.TcPOU@Start` -> `...^SdkControlFB^Start`), so two methods with the same name in different FBs are not mixed up; the code excerpt is taken from that object. With TcAgentBridge the DTE hint "cursor/selection not available" is dropped. A selection that ends at column 1 of the following line is reported as whole lines (e.g. `implementation, lines 12-14 (whole lines)`).

### `tc_goto_definition`

Go to definition: resolves a name the way TwinCAT itself does - in the scope of the object open in the editor. Without symbol, the word at the cursor (or the selection) is used. For variables returns type, declaration site and resolved type; for types/POUs the location in the project (tree path) or the library. Without an open editor, POUs/DUTs/GVLs and their members are looked up in the project tree. Requires the VS extension TcAgentBridge.

If no object is active in the editor (e.g. the TcAgent chat window has the focus), the bridge uses the last active editor document; if there is none, types are resolved at project level in every PLC project including its libraries (e.g. `MC_MoveAxesAbsoluteFB`). If nothing is found (or the bridge is not running), project objects with that name and the cached library signatures of `tc_lib_signatures` are searched; otherwise the referenced libraries are listed.

With `open=true` the definition is opened in the IDE editor and the cursor is put on the declaration line (variable declaration, or the header of the POU/DUT/GVL), like F12 - see `tc_editor_open`. Library objects cannot be opened.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol` | string |  | Name, e.g. _startTime, MC_MoveLinearAbsoluteFB, GVL_Progress.Progress (empty = word at cursor) |
| `open` | boolean |  | Also open the resolved object in the IDE editor at the declaration line (like F12) |

### `tc_editor_open`

Opens a PLC object (POU, method, property accessor, action, DUT, GVL) in the IDE editor - like a double-click in the Solution Explorer - and optionally puts the cursor on a line. Line numbers count per part, as in `tc_plc_read` / `tc_plc_search` / `tc_find_references`. Changes nothing in the project (read class, also allowed in `ask`/`propose` mode).

With TcAgentBridge (bridge actions `open` and `caret`) the cursor is set in the declaration or implementation editor and verified; the answer says "Opened ... cursor on implementation line N" or why the cursor could not be placed. Without the bridge the object is opened via DTE (Solution Explorer) and the cursor is usually not placed (the TwinCAT editor offers no DTE text selection).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Tree path of the object, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Axis^Start |
| `line` | integer |  | Line in the part (1-based); empty = only open |
| `part` | string |  | declaration (default) or implementation |
| `column` | integer |  | Column (1-based, default 1) |

### `tc_find_references`

Finds all usages of a symbol: cross-references as in TwinCAT's cross-reference list, plus a whole-word text search as a cross-check.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol` | string | yes | Name of the symbol (variable, FB, method, type) |
| `max` | integer |  | Max. hits per source (default 300) |

### `tc_ide`

Experimental: direct query of the VS extension TcAgentBridge in the IDE process. command: ping (is it running?), context (raw editor context), libs (libraries installed in the repository), probe (diagnostics: what is reachable in the IDE process; writes probe.txt).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `command` | string | yes | ping / context / libs / probe / lm_call / chat_show (open the chat window in TwinCAT) |
| `target` | string |  | lm_call (advanced diagnostics): query target |
| `method` | string |  | lm_call: read-only methods only (Get*/Find*/All*/Is*) |
| `args` | string |  | lm_call: arguments separated by /; $ph = project handle, $obj = object in the editor |

## Libraries

### `tc_plc_references`

Lists the library references of the PLC projects with resolved version and vendor (including placeholders). With installed=true also the libraries installed in the repository.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `plcProjectPath` | string |  | Path of the PLC project (default: all) |
| `installed` | boolean |  | Also list installed libraries |

### `tc_lib_signatures`

Signatures of a library - also from third-party compiled libraries (requires the VS extension TcAgentBridge). Without symbol: outline (one line per FB/function/type, with EXTENDS and number of inputs/outputs). With symbol: interface as an ST declaration incl. inherited inputs/outputs, methods and doc comments. The result is cached per library version.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `library` | string | yes | Library title, e.g. RobotLibrary or Tc2_Standard |
| `version` | string |  | Version (default: the one resolved in the project, otherwise the newest installed) |
| `symbol` | string |  | Name of an FB, function or type; method as FB.Method |
| `filter` | string |  | Outline only for names containing this text (e.g. MC_ or FB) |
| `source` | string |  | lm (default: with EXTENDS, methods, original casing) or doc (alternative export, fallback) |
| `refresh` | boolean |  | Regenerate the cache |

### `tc_plc_library_add`

Adds a library reference to a PLC.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `referencesPath` | string | yes | Path of the References node, e.g. TIPC^MyPlc^MyPlc Project^References |
| `library` | string | yes | Library name |
| `version` | string |  | Version or * (default) |
| `vendor` | string |  | Vendor, e.g. Beckhoff Automation GmbH |

### `tc_sbom`

Software bill of materials (SBOM) of the PLC project(s) as CycloneDX 1.6 JSON – for security/compliance (e.g. Cyber Resilience Act) and as pipeline artifact. One file per PLC project, default `<solution>\sbom\<PLC project>.cdx.json`; returns a short summary with the file path.

- `metadata.component`: the PLC project as `application`, version from the project information (`ProjectVersion` of the `.plcproj`, otherwise the XML description of the PLC project), properties `tcagent:plcPath`, `tcagent:git.commit` and `tcagent:git.branch` of the solution folder.
- `components`: one `library` per library reference with resolved version, `supplier`/`publisher` = vendor (distributor), purl `pkg:generic/<vendor>/<name>@<version>` (URL-encoded; there is no official purl type for TwinCAT libraries) and `hashes` (SHA-256 of the `.library`/`.compiled-library` file when it is found in a library repository: the repositories of the IDE, `%ProgramData%\Beckhoff\TwinCAT\3.1\Components\Plc\Managed Libraries`, `C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\...`, `%TWINCAT3DIR%\Components\Plc\Managed Libraries`). Properties: `tcagent:placeholder`, `tcagent:requestedVersion` (`*` or the version asked for), `tcagent:resolvedFrom`, `tcagent:license` (`unknown`).
- `dependencies`: the PLC project depends on all its library references.
- `*` references are resolved like TwinCAT does (newest version in the library repositories).
- Missing data is marked, never left out: `tcagent:hash = not found`, `tcagent:resolvedVersion = unresolved`, `tcagent:vendor = unknown`, `tcagent:version = not found`, git `not available`.

Pipeline: `TcAgent.exe sbom --solution X.sln [--out sbom\X.cdx.json] [--plc <names>]`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `plcProjectPath` | string |  | PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = all PLC projects) |
| `file` | string |  | Target file (default `<solution>\sbom\<PLC project>.cdx.json`; several PLC projects: `<name>.<PLC>.cdx.json`) |

### `tc_library_lock`

Library lock file `<solution>\.tcagent\libraries.lock.json`: the resolved library versions per PLC project (`name`, `vendor`, `placeholder`, `requested` = `*` or a fixed version, `resolved`), sorted so diffs in Git are clean. `action=check` (default, read only) compares the current resolution with the lock file and lists the differences – `+` added, `-` removed, `~` changed (version, requested version, vendor, library of a placeholder). `action=write` creates or updates it; if nothing changed the file is left untouched. With `plcProjectPath` only that project's entry is checked or replaced.

Pipeline gate: `TcAgent.exe lock --solution X.sln --check` – exit 0 = equal, 1 = differences, 2 = no lock file.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `check` (default) or `write` |
| `plcProjectPath` | string |  | PLC project (empty = all; with a project only its entry is checked/replaced) |

## Projects, export/import, libraries, analysis, mapping, routes, TcCOM

### `tc_new_solution`

Starts Visual Studio/XAE and creates a new solution with a TwinCAT XAE project (Beckhoff project template), then connects to it. Add a PLC project with tc_plc_project_add.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `folder` | string | yes | Parent folder; the solution goes to <folder>\<name>\<name>.sln |
| `name` | string | yes | Name of the solution and the TwinCAT project |
| `visible` | boolean |  | Show the IDE (default true) |
| `template` | string |  | Path of a .tsproj template (default: TwinCAT Project.tsproj of the installation) |
| `progId` | string |  | e.g. VisualStudio.DTE.17.0 or TcXaeShell.DTE.17.0 |

### `tc_plc_project_add`

Adds a PLC project: new from a template (default 'Standard PLC Template') or an existing .plcproj/.tpzip (file). The PLC instance and its task appear below TIPC; check with tc_tree path=TIPC.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Name of the PLC project (ignored with fileMode=original) |
| `template` | string |  | Template name or path, e.g. 'Standard PLC Template' or 'Empty PLC Template' |
| `file` | string |  | Existing .plcproj or .tpzip instead of a template |
| `fileMode` | string |  | with file: copy (default, into the solution folder), move or original (keep location) |
| `dryRun` | boolean |  | Only show what would happen |

### `tc_export`

Exports to a file: kind=plcopen (PLC objects as PLCopen XML, selection like 'POUs.MAIN;DUTs.ST_Data') or kind=item (any tree item with all children as .xti - I/O device, box, task, axis ...; a PLC project path TIPC^<Plc> as .tpzip).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | yes | plcopen or item |
| `file` | string | yes | Target file (.xml, .xti, .tpzip) |
| `selection` | string |  | plcopen: objects relative to the PLC project, folders with dots, several with ';' |
| `plcProjectPath` | string |  | plcopen: PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = the only one) |
| `path` | string |  | item: tree path of the item to export, e.g. 'TIID^Device 1 (EtherCAT)^Term 1 (EK1100)' |

### `tc_import`

Imports from files: kind=plcopen (PLCopen XML into a PLC project), kind=item (.xti below parentPath, e.g. an I/O device under TIID or an axis under TINC^...^Axes; variables are re-linked by name) or kind=pou_files (.TcPOU/.TcDUT/.TcGVL files).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | yes | plcopen, item or pou_files |
| `file` | string | yes | File; pou_files: several separated by ';' |
| `plcProjectPath` | string |  | plcopen/pou_files: PLC project (empty = the only one); pou_files may also name a folder of it |
| `existing` | string |  | plcopen: what to do with existing objects: none (default), rename, replace or skip |
| `selection` | string |  | plcopen: only these objects |
| `parentPath` | string |  | item: parent tree item (e.g. TIID) |
| `name` | string |  | item / pou_files (single file): name of the new item |
| `before` | string |  | item: insert before this child |
| `reconnect` | boolean |  | item: re-link variables by name (default true) |
| `dryRun` | boolean |  | Only show what would happen |

### `tc_plaintext`

PLC code as plain-text ST files and back (format: [PLAINTEXT.md](PLAINTEXT.md)). One `.st` file per POU/GVL/DUT/interface in `<folder>\<PLC project>\<folder in the project>\<name>.st`: declaration, implementation indented by two blanks, `END_FUNCTION_BLOCK`, then the methods (`END_METHOD`), properties (`GET … END_GET`, `SET … END_SET`, no `END_PROPERTY`) and actions (`END_ACTION`); `<folder>\<PLC project>\references.json` lists the library references of the `.plcproj` (`Name`, `Version`, `DistributorName`, `Namespace` – format in [PLAINTEXT.md](PLAINTEXT.md#referencesjson)). `action=export` writes the files (only changed ones; `clean` never removes `references.json`), `action=check` lists the differences between the folder and the project (`.st` files and `references.json`), `action=import` ignores `references.json` and takes the `.st` files back into the **existing** project: changed declarations/implementations are replaced (Ids kept), new methods/properties/actions and new `.st` files are created, nothing is deleted. The implementation is told apart from the declaration by its indentation (keep it); `// @implementation SFC - ...` marks a graphical body that stays as it is. Works on the project files; when the solution is open in the connected IDE, import writes through the IDE (like `tc_plc_write`/`tc_plc_create`/`tc_import kind=pou_files`) – then `tc_save` and `tc_check`. Refused when the solution is open in another IDE. export/check count as read tools; import with `dryRun` too.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | export, check or import |
| `solution` | string |  | Path of the .sln (default: the solution of the connected IDE) |
| `plc` | string |  | Only these PLC projects, comma-separated (plcproj name or PLC project name; default: all) |
| `folder` | string |  | Text folder (default: `<solution folder>\st`) |
| `clean` | boolean |  | export: also remove .st files whose object no longer exists (references.json is kept) |
| `dryRun` | boolean |  | import: only list the changes, write nothing |

### `tc_library`

Library management of a PLC project: list (references), add, add_placeholder, remove, resolve (effective version of a placeholder), freeze (placeholder versions), repositories, add_repository, remove_repository, install, uninstall, scan (all installed libraries).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list, add, add_placeholder, remove, resolve, freeze, repositories, add_repository, remove_repository, install, uninstall, scan |
| `plcProjectPath` | string |  | PLC project (empty = the only one) |
| `name` | string |  | Library or placeholder name, e.g. Tc2_Standard; also 'Tc2_Math, * (Beckhoff Automation GmbH)' |
| `version` | string |  | Version, '*' = newest (default) |
| `company` | string |  | Distributor, e.g. Beckhoff Automation GmbH |
| `library` | string |  | resolve / add_placeholder: library name the placeholder points to |
| `repository` | string |  | Repository name (install/uninstall/add_repository/remove_repository) |
| `path` | string |  | install: .library/.compiled-library file; add_repository: root folder |
| `index` | integer |  | add_repository: position (default 0) |
| `overwrite` | boolean |  | install: overwrite an installed version (default false) |
| `filter` | string |  | scan: only libraries containing this text |

### `tc_library_save`

Saves a PLC project as library (.library) and optionally installs it into the default repository - e.g. as a pipeline artifact.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file` | string | yes | Target .library file |
| `plcProjectPath` | string |  | PLC project (empty = the only one) |
| `install` | boolean |  | Also install into the repository (default false) |

### `tc_static_analysis`

Static code analysis (TE1200 license): coding rules, naming conventions, metrics. Results appear in the error list (tc_errors). Optional: load settings (.csa) first, export results as SARIF (TwinCAT 4026.20+).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `plcProjectPath` | string |  | PLC project (empty = all) |
| `settingsFile` | string |  | Load static analysis settings (.csa) before the run (4026+) |
| `sarifFile` | string |  | Export the results as SARIF file (4026.20+) |
| `metricsFile` | string |  | Export the standard metrics as CSV (4026.4+) |

### `tc_mapping_export`

Exports all variable links (mapping) of the configuration as XML - to a file or as text.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file` | string |  | Target file (empty = return the XML, shortened) |

### `tc_mapping_import`

Imports variable links from an XML file made with tc_mapping_export (optionally removes all existing links first). clearOnly=true only removes all links.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file` | string |  | Mapping XML file |
| `clearFirst` | boolean |  | Remove all existing links first (default false) |
| `clearOnly` | boolean |  | Only remove all links |
| `dryRun` | boolean |  | Only show what would happen |

### `tc_twincat_version`

Shows or selects the TwinCAT version (Remote Manager) the solution is built with - for several installed TwinCAT builds.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `version` | string |  | Version to select, e.g. 3.1.4026.19 (empty = show) |

### `tc_platform`

Shows or sets the target platform of the solution, e.g. 'TwinCAT RT (x64)' or 'TwinCAT RT (x86)'.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `platform` | string |  | Platform to set (empty = show the current one and the available ones) |

### `tc_archive`

Saves the whole solution as archive (.tszip) - e.g. as pipeline artifact or for support.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `file` | string | yes | Target .tszip file |

### `tc_variant`

Project variants: shows the current variant and the variant configuration, selects a variant (current, '[Group]' for a group) or replaces the variant configuration XML (config).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `current` | string |  | Variant to select, e.g. Variant1 or [Group1] |
| `config` | string |  | New variant configuration XML (<ProjectVariants>...) |

### `tc_route_search`

Searches ADS target systems: broadcast search in the local network, or a direct search by host name/IP (4020.10+). Read only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `host` | string |  | Host name or IP for a direct search (empty = broadcast) |

### `tc_route_add`

Adds an ADS route to a target system (on the target and locally), optionally also as project route. Needs the target's user name and password - ask the user for them, never guess. Runtime action with confirmation.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Route name, e.g. CX-12345 |
| `netId` | string | yes | AmsNetId of the target |
| `ip` | string |  | IP address of the target |
| `host` | string |  | Host name instead of the IP |
| `user` | string |  | User name on the target (e.g. Administrator) |
| `password` | string |  | Password on the target |
| `projectRoute` | boolean |  | Also add as project route (default false) |
| `noEncryption` | boolean |  | Route without encryption (older targets) |

### `tc_tccom`

TcCOM module instances: list, or add an instance of a module class (class id GUID or module name) and optionally let a task call it (taskPath). Parameters afterwards with tc_xml_get/tc_xml_set.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list or add |
| `classId` | string |  | add: class id (GUID) of the module |
| `module` | string |  | add: module name instead of the class id |
| `name` | string |  | add: instance name |
| `taskPath` | string |  | add: task that calls the module, e.g. TIRT^Task 1 |
| `dryRun` | boolean |  | Only show what would happen |

## Real-time cores, CoE startup list, package manager

### `tc_realtime`

Real-time settings (cores): `action=show` (available cores, cores for TwinCAT, isolated cores, cores left to Windows, real-time cores, base time / load limit / latency warning per core, plus any other values of the settings XML) or `set`. "Available cores" comes from the real-time settings XML if it contains a core count; otherwise the logical processor count of this PC is shown and labelled as such (a remote target may differ). Changes the project; takes effect with `tc_activate`. Isolating cores additionally needs "Set on target" in the IDE and a reboot of the target.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | show or set |
| `maxCpus` | integer |  | set: number of cores TwinCAT may use |
| `isolated` | integer |  | set: how many of them are isolated from Windows (0 = shared only) |
| `cores` | string |  | set: real-time cores, e.g. '1' or '1,2' (core 0 = first core) |
| `baseTimeUs` | integer |  | set: base time in microseconds for the listed cores, e.g. 1000 |
| `loadLimit` | integer |  | set: CPU limit in percent for the listed cores |
| `latencyWarning` | integer |  | set: latency warning for the listed cores (unit as shown by action=show) |
| `dryRun` | boolean |  | only show the XML |

### `tc_coe`

CoE startup list of an EtherCAT device (parameters written at a state transition, e.g. PS): `action=list`, `add` (index, subIndex, value + dataType or data as hex bytes, transition, comment) or `remove`. The list is written back as a whole; takes effect with `tc_activate`. Look up index/subindex in the device documentation.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list, add or remove |
| `path` | string | yes | tree path of the terminal/drive, e.g. TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 2 (EL3102) |
| `index` | string |  | add/remove: object index, e.g. 0x8000 |
| `subIndex` | string |  | add/remove: subindex, e.g. 0x06 or 6 |
| `value` | string |  | add: value, e.g. 1000, TRUE, 0.5 |
| `dataType` | string |  | add: BOOL, USINT/UINT8, UINT/UINT16, UDINT/UINT32, ULINT/UINT64, SINT/INT8, INT/INT16, DINT/INT32, LINT/INT64, REAL/REAL32, LREAL/REAL64 |
| `data` | string |  | add: raw bytes as hex instead of value/dataType, e.g. e803 |
| `transition` | string |  | add/remove: PS (default), SO, SP, OP, IP ... |
| `comment` | string |  | add: comment shown in the startup list |
| `dryRun` | boolean |  | only show the resulting XML |

### `tc_packages`

TwinCAT Package Manager (tcpkg, TwinCAT 4026+), read only: installed packages and workloads, available packages, all versions of one package, outdated packages, package details, feeds, feed check, tcpkg settings.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | installed, available, versions, outdated, show, feeds, verify_feeds or config |
| `package` | string |  | versions/show: package id, e.g. TE1300.ScopeViewProfessional.XAE; otherwise optional filter |
| `type` | string |  | available/installed: Workload to list workloads only |
| `feed` | string |  | only this feed |
| `prerelease` | boolean |  | include pre-release versions |

### `tc_packages_change`

TwinCAT Package Manager, changes: install, upgrade, uninstall, repair packages; add, edit, remove feeds. Always confirmed by the user and run with administrator rights (UAC prompt): tcpkg.exe of the program folders, started without a shell. Package ids and versions only `[A-Za-z0-9._-]`, feed names words of these characters with single blanks, sources an http(s) URL or an absolute folder - other values are refused before the confirmation. Close Visual Studio / XAE Shell before changing XAE packages.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | install, upgrade, uninstall, repair, feed_add, feed_edit or feed_remove |
| `packages` | string |  | package ids, optionally =version, e.g. TwinCAT.Standard.XAE=4026.22.0; upgrade also 'all' |
| `allowDowngrade` | boolean |  | upgrade: allow a lower version |
| `includeDependencies` | boolean |  | uninstall: also remove dependencies no longer needed |
| `prerelease` | boolean |  | install/upgrade: allow pre-release versions |
| `feed` | string |  | install/upgrade: only this feed |
| `name` | string |  | feed_*: feed name |
| `source` | string |  | feed_add/feed_edit: feed URL or folder |
| `priority` | integer |  | feed_add/feed_edit: search order (lower = first) |
| `enabled` | boolean |  | feed_edit: enable/disable |
| `newName` | string |  | feed_edit: rename |
| `dryRun` | boolean |  | only show the tcpkg command |

## Scope (TE130x Scope View)

### `tc_scope_new`

Adds a TwinCAT Scope configuration (Measurement project, TE130x Scope View) to the solution. Then add charts and channels with tc_scope.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Name of the measurement project |
| `template` | string |  | Path of a .tcmproj template (default: 'YT Scope Project' of the installation) |
| `folder` | string |  | Folder (default: next to the solution) |

### `tc_scope`

TwinCAT Scope: list (configurations, charts, axes, channels with their PLC variables), add_config (.tcscopex from the YT template into an empty measurement project), add_chart, add_axis, add_channel (channel for a PLC variable: type and size read via ADS, sampled with the task cycle), remove_channel, record_start, record_stop, export (file by extension: .csv, .svdx/.svd, .bin, .tdms, .dat - TDMS/DAT need a Scope license). Configuration changes are written to the .tcscopex file and the measurement project is reloaded in Visual Studio. Paths: '<project>/<file.tcscopex>/<chart>/<axis>' by name, missing parts = the first one.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list, add_config, add_chart, add_axis, add_channel, remove_channel, record_start, record_stop, export |
| `path` | string |  | e.g. 'TcAgentScope/YT Scope Project.tcscopex/Chart 1/Axis 1' (empty = the first scope/chart/axis) |
| `name` | string |  | add_config: file name (default YT Scope Project); add_chart/add_axis: name; add_channel: channel name (default: the symbol); remove_channel: channel name |
| `symbol` | string |  | add_channel: PLC variable, e.g. MAIN.fSine |
| `netId` | string |  | add_channel: AmsNetId (default 127.0.0.1.1.1) |
| `port` | integer |  | add_channel: ADS port (default 851) |
| `dataType` | string |  | add_channel: Scope data type if the PLC is not reachable: BIT, INT8, UINT8, INT16, UINT16, INT32, UINT32, INT64, UINT64, REAL32, REAL64 |
| `file` | string |  | export: target file |
| `template` | string |  | add_config: .tcscopex to copy (default: YT Scope Project of the installation) |

## TwinCAT HMI (TE2000, file based)

### `tc_hmi`

TwinCAT HMI project (file based): info (pages, startup view, ADS runtimes, mapped symbols), read (a view/content file), controls (available control types and their attributes from the installed control package), build (via Visual Studio). Create the HMI project once in Visual Studio; pages/controls/symbols then with tc_hmi_page, tc_hmi_control, tc_hmi_symbol.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | info, read, controls or build |
| `project` | string |  | Path of the .hmiproj or its name (empty = the only HMI project of the solution) |
| `file` | string |  | read: file relative to the project, e.g. Desktop.view or Content/Main.content |
| `filter` | string |  | controls: only types containing this text, e.g. Button |

### `tc_hmi_page`

Creates a view (.view) or content (.content) in the HMI project and registers it in the .hmiproj and tchmiconfig.json. Contents are shown in a TcHmiRegion (tc_hmi_control type=TcHmiRegion attributes={"target-content":"Content/X.content"}).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Name without extension, e.g. Overview |
| `kind` | string |  | view or content (default content) |
| `folder` | string |  | Folder relative to the project (default: views in the root, contents in Content) |
| `width` | integer |  | content: width in px (default 1024) |
| `height` | integer |  | content: height in px (default 600) |
| `startup` | boolean |  | view: make it the start view |
| `project` | string |  | HMI project (empty = the only one) |
| `reload` | boolean |  | If the HMI project is open it is unloaded for the change and reloaded (default true); `false` leaves it unloaded - ask the user to reload it |
| `dryRun` | boolean |  | Only show what would be created |

### `tc_hmi_control`

Adds, changes or removes a control in a view/content. Attributes without the data-tchmi- prefix, e.g. {"text":"Start","state-symbol":"%s%PLC1.MAIN.bStart%/s%"}; bindings %s%<mapped symbol>%/s%, two-way: %s%PLC1.MAIN.x|BindingMode=TwoWay%/s%. Object values (colors ...) as JSON objects. Events: writeSymbol/writeValue for a button press, or events = trigger JSON array. Check attribute names with tc_hmi action=controls.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | add, update or remove |
| `file` | string | yes | View/content relative to the project, e.g. Desktop.view |
| `id` | string | yes | Unique control id, e.g. btnStart |
| `type` | string |  | add: control type, e.g. TcHmiButton, TcHmiTextblock, TcHmiCheckbox, TcHmiRegion or full TcHmi.Controls.Beckhoff.TcHmiButton |
| `left` | number |  | Position/size in px |
| `top` | number |  |  |
| `width` | number |  |  |
| `height` | number |  |  |
| `attributes` | object |  | Further attributes {name: value}; null removes an attribute |
| `parent` | string |  | add: id of a container control to insert into (default: the root) |
| `writeSymbol` | string |  | Shortcut: on press write writeValue to this symbol, e.g. %s%PLC1.MAIN.bStart%/s% or PLC1.MAIN.bStart |
| `writeValue` | string |  | Value for writeSymbol (true/false/number/text, default true) |
| `events` | array |  | Trigger list as in the file: [{"event":"<id>.onPressed","actions":[...]}] (replaces existing) |
| `project` | string |  | HMI project (empty = the only one) |
| `reload` | boolean |  | If the HMI project is open it is unloaded for the change and reloaded (default true); `false` leaves it unloaded - ask the user to reload it |
| `dryRun` | boolean |  | Only show the resulting HTML |

### `tc_hmi_symbol`

Server symbols of the HMI: list, map (PLC variables -> HMI symbols in TcHmiSrv.Config.default.json, type read via ADS if not given), unmap, runtimes (ADS runtimes PLC1 ...), add_runtime. Alternative without mapping by hand: PLC attribute {attribute 'TcHmiSymbol.AddSymbol'} + 'Map Automap Symbols' in Visual Studio. Best edit while the HMI project is not open.

Changing actions of `tc_hmi_page`, `tc_hmi_control` and `tc_hmi_symbol` unload an open HMI project, edit the files and reload it. The 32-bit memory guard of the XAE Shell runs before the unload **and again before the reload**; if it trips, the change is kept and the project stays unloaded (the result says so). `reload=false` skips the automatic reload - then ask the user to reload the project (right-click > Reload Project) or to close and reopen the solution. If the IDE dies on the way (RPC `0x800706BE`/`0x800706BA`, `RPC_E_DISCONNECTED`) the call fails with a clear "IDE was lost" message (what was written, restart the IDE, `tc_attach`, do not repeat blindly) and TcAgent does not reconnect on its own for 30 s.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list, map, unmap, runtimes or add_runtime |
| `symbols` | string |  | map/unmap: PLC variables separated by ',' e.g. MAIN.bStart,MAIN.nSpeed (optionally with type: MAIN.nSpeed:INT) |
| `runtime` | string |  | ADS runtime name (default PLC1) |
| `type` | string |  | map: data type for all symbols without own type, e.g. BOOL, INT, LREAL, STRING(80) |
| `netId` | string |  | add_runtime: AmsNetId (default 127.0.0.1.1.1) |
| `port` | integer |  | add_runtime: ADS port (default 851) |
| `project` | string |  | HMI project (empty = the only one) |
| `reload` | boolean |  | If the HMI project is open it is unloaded for the change and reloaded (default true); `false` leaves it unloaded - ask the user to reload it |
| `dryRun` | boolean |  | Only show what would change |

## Motion

### `tc_motion`

Motion configuration. `action=list` (default, read only): NC tasks and their axes (name, type, tree path, linked I/O item). `action=link`: links an NC axis with its drive, like "Link To I/O..." in the axis settings - `ConsumeXml` on the axis with `<TreeItem><NcAxisDef><IoItem><PathName>TIID^...</PathName></IoItem></NcAxisDef></TreeItem>` (format from the Automation Interface sample "How to create/change a linkage of a nc axis, encoder or drive to an I/O device, box or terminal"). Because that sample is old and not yet verified on every TwinCAT 3 version, `link` is a dry run unless `dryRun=false` is passed; after writing, the axis XML is read back and the result says LINKED or WRITTEN, BUT NOT VERIFIED (with the I/O related XML of the axis). Changes the project only; takes effect with `tc_activate`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | `list` (default) or `link` |
| `axis` | string |  | link: NC axis, e.g. `TINC^NC-Task 1 SAF^Axes^Axis 1` |
| `drive` | string |  | link: I/O item of the drive below TIID, e.g. `TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 4 (EL7201)` |
| `dryRun` | boolean |  | link: default true = only show the XML; false = write |

### `tc_motion_add`

Creates an NC task (`kind=nc_task`) or NC axes (`kind=axis`, several with `count`) under the Axes folder of an NC task. Parameters afterwards with `tc_xml_get`/`tc_xml_set`; the drive link with `tc_motion action=link`. Takes effect after `tc_activate`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string | yes | `nc_task` or `axis` |
| `name` | string |  | Name (default `NC-Task n` / `Axis n`) |
| `count` | integer |  | axis: number of axes (default 1) |
| `ncTaskPath` | string |  | axis: NC task, e.g. `TINC^NC-Task 1 SAF` (empty = the only one) |
| `dryRun` | boolean |  | Only show what would be created |

## Build and test

### `tc_check`

Checks all PLC objects for compiler errors ("Check all objects", including unused POUs) - without a build, so it produces no executable code and no files. The default after code changes.

No parameters.

### `tc_build`

Builds the solution (produces executable code and files – slower than a check) and returns compiler errors/warnings. Needed before activating/downloading, for the build checkpoint of a workflow, or when the user asks for it. Always run `tc_check` first and fix its errors – it pre-compiles without generating code, so several fix rounds cost checks, not builds.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `rebuild` | boolean |  | Clean first |
| `checkAll` | boolean |  | Additionally check all objects (default false) |

### `tc_errors`

Reads the IDE error list, split into compiler/project messages and PLC runtime/TcUnit messages.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `kind` | string |  | compile, runtime or all (default) |
| `max` | integer |  | Max. entries (default 500) |

### `tc_test_results`

Returns the result of the last TcUnit run (suites, tests, passes, failures with test names and messages) from the PLC runtime messages.

### `tc_test_run`

TcUnit tests of the open solution ([TCUNIT_TESTS.md](TCUNIT_TESTS.md)). `action=list`: suites, instances and tests read
from the code with their `///` description, file and line and the enable bools (read only, no runtime).
`action=run` (default): runs the selection – build and activation only if the code changed since the last download,
reset of the PLC via ADS, enable bools `bRunSuite` / `bRun_<TestMethod>` (TRUE = selected, FALSE = the rest), start,
results per test (with `EXP`/`ACT`/`MSG`), reset again; TwinCAT stays in Run mode. Runtime action: allowed targets only,
with confirmation. Same run as `TcAgent.exe tcunit run` and the TcUnit Tests window.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string |  | run (default) or list |
| `tests` | string |  | Comma-separated: instance (`MAIN.fbMotor`), suite type (`FB_Motor_Test`), `MAIN.fbMotor@Test`, a test name or `@<file>` (saved test selection, relative to the solution folder); empty = all |
| `plc` | string |  | PLC project (default: the only one with tests, or `plcProject` in `.tcagent\tcunit.json`) |
| `target` | string |  | `local` (default) or the AmsNetId of a test PLC allowed in `TCAGENT_ALLOW_TARGETS` |
| `timeout` | integer |  | Max. seconds for the run (default 300 or `timeout` in `.tcagent\tcunit.json`) |
| `download` | boolean |  | Build and activate even if the runtime runs the current code |

## Runtime (enabled targets only)

### `tc_runtime_mode`

Switches the TwinCAT runtime of the target to config mode (needed for the I/O scan, stops the PLC) or restarts it in run mode with the active configuration. Allowed targets only, with confirmation. Current state: `tc_plc_state port=10000`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `mode` | string | yes | `config` or `run` |
| `netId` | string |  | AmsNetId (empty = target of the project) |


### `tc_task`

Real-time tasks: `action=list`, `create` (`name`, `cycleUs`, `priority`, `withImage`), `set` (`path`, `cycleUs`, `priority`, `autoStart`, `cores` = real-time core(s) of the task, e.g. `1`) or `assign` (`name` = TIRT task, `program` = called POU, default `MAIN`, `plcProjectPath`): creates the PLC task reference and the program call – without it the PLC has no process image. Takes effect after `tc_activate`.

### `tc_boot_project`

PLC boot project: `autostart` (true/false) and/or `generate`; without parameters the current state is shown. Optional `plcPath`.

### `tc_runtime_startup`

Runtime startup mode after boot: `mode=run` or `config`; without `mode` only shows it. Local only so far (registry, administrator rights).

### `tc_licenses`

Shows the solution's license node as XML; change via `tc_xml_set`. Trial licenses are activated by the user (security code).

### `tc_activate`

Optionally sets the target NetId, activates the configuration and restarts TwinCAT in Run mode.

**Gate (since 0.32):** before the activation (and before anybody is asked to confirm it) TcAgent builds the solution –
without "check all objects", which also checks code that is never called. With build errors the activation is refused
and the build report is returned – the agent is told to show the errors and ask before it changes any code.
`TCAGENT_ACTIVATE_GATE=0` switches the gate off.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `netId` | string |  | AmsNetId of the target, e.g. 5.12.34.56.1.1 (empty = current target) |
| `restart` | boolean |  | Restart TwinCAT (default true) |

### `tc_plc_online`

PLC online commands via the PLC project: login, download/online change, start, stop, logout.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `plcProjectPath` | string | yes | Path of the PLC project, e.g. TIPC^MyPlc^MyPlc Project |
| `login` | boolean |  |  |
| `start` | boolean |  |  |
| `stop` | boolean |  |  |
| `logout` | boolean |  |  |

### `tc_plc_state`

Reads the PLC runtime state via ADS (Run, Stop, Config ...).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `netId` | string |  | AmsNetId (empty = local) |
| `port` | integer |  | ADS port (default 851) |

### `ads_read`

Reads a variable at runtime via ADS (e.g. MAIN.nCounter).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol` | string | yes | Symbol path |
| `netId` | string |  | AmsNetId (empty = local) |
| `port` | integer |  | ADS port (default 851) |

### `ads_write`

Writes a variable at runtime via ADS.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol` | string | yes | Symbol path |
| `value` | string | yes | New value (text, converted to the variable type) |
| `netId` | string |  | AmsNetId (empty = local) |
| `port` | integer |  | ADS port (default 851) |

### `ads_symbols`

Lists runtime symbols via ADS (optionally filtered).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `filter` | string |  | Substring, e.g. MAIN. |
| `netId` | string |  | AmsNetId |
| `port` | integer |  | ADS port (851) |
