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

### `tc_feedback`

Reports a problem, missing feature or improvement suggestion to TcAgent development. Always use it when a tool fails, behaves unexpectedly, a task could only be solved via a workaround, or a tool is missing.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `type` | string | yes | bug / missing_feature / usability / docs |
| `summary` | string | yes | One sentence: what is the problem / what is missing |
| `tool` | string |  | Affected tool, e.g. tc_plc_create |
| `details` | string |  | Call/arguments, error message, context (TwinCAT version, project type) |
| `expected` | string |  | What should have happened |
| `workaround` | string |  | How the task was solved anyway (if at all) |
| `severity` | string |  | blocker / major / minor |

## Project tree, configuration, I/O

### `tc_tree`

Shows the TwinCAT project tree (name, type, path). Without path: all roots (TIPC=PLC, TIID=I/O, TIRT=tasks ...).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string |  | Start path, e.g. TIPC or TIID^Device 1 (EtherCAT) |
| `depth` | integer |  | Depth (default 2) |

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

Deletes a child item in the tree.

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

### `tc_link`

Links PLC variables to I/O channels, one at a time or many at once. PLC variables only appear after `tc_build` if declared with `AT %I*`/`AT %Q*`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | string |  | First variable path |
| `target` | string |  | Second variable path |
| `pairs` | string |  | several links, one `<path> => <path>` per line |
| `dryRun` | boolean |  | only check whether all paths exist |

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

## Editor context and navigation (TcAgentBridge)

### `tc_editor_context`

Shows what the user is currently working on in the IDE: active window/document, selected items, tree paths derived from them (usable directly with tc_plc_read) and - with the VS extension TcAgentBridge - cursor and selection. Use when the user talks about 'here', 'this FB' or 'the method'.

### `tc_goto_definition`

Go to definition: resolves a name the way TwinCAT itself does - in the scope of the object open in the editor. Without symbol, the word at the cursor (or the selection) is used. For variables returns type, declaration site and resolved type; for types/POUs the location in the project (tree path) or the library. Requires the VS extension TcAgentBridge.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `symbol` | string |  | Name, e.g. _startTime, MC_MoveLinearAbsoluteFB, GVL_Progress.Progress (empty = word at cursor) |

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

## Build and test

### `tc_check`

Checks all PLC objects for compiler errors ("Check all objects", including unused POUs) - without a build, so it produces no executable code and no files. The default after code changes.

No parameters.

### `tc_build`

Builds the solution (produces executable code and files) and returns compiler errors/warnings. Only needed before activating/downloading or when the user asks for it - to check for compiler errors use `tc_check`.

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

## Runtime (enabled targets only)

### `tc_task`

Real-time tasks: `action=list`, `create` (`name`, `cycleUs`, `priority`, `withImage`) or `set` (`path`, `cycleUs`, `priority`, `autoStart`). Takes effect after `tc_activate`.

### `tc_boot_project`

PLC boot project: `autostart` (true/false) and/or `generate`; without parameters the current state is shown. Optional `plcPath`.

### `tc_runtime_startup`

Runtime startup mode after boot: `mode=run` or `config`; without `mode` only shows it. Local only so far (registry, administrator rights).

### `tc_licenses`

Shows the solution's license node as XML; change via `tc_xml_set`. Trial licenses are activated by the user (security code).

### `tc_activate`

Optionally sets the target NetId, activates the configuration and restarts TwinCAT in Run mode.

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
