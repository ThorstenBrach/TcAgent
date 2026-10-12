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

Go to definition: resolves a name the way TwinCAT itself does - in the scope of the object open in the editor. Without symbol, the word at the cursor (or the selection) is used. For variables returns type, declaration site and resolved type; for types/POUs the location in the project (tree path) or the library. Without an open editor, POUs/DUTs/GVLs and their members are looked up in the project tree. Requires the VS extension TcAgentBridge.

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

Real-time settings (cores): `action=show` (available cores, cores for TwinCAT, isolated cores, real-time cores, base time / load limit / latency warning per core) or `set`. Changes the project; takes effect with `tc_activate`. Isolating cores additionally needs "Set on target" in the IDE and a reboot of the target.

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

TwinCAT Package Manager, changes: install, upgrade, uninstall, repair packages; add, edit, remove feeds. Always confirmed by the user and run with administrator rights (UAC prompt). Close Visual Studio / XAE Shell before changing XAE packages.

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
| `dryRun` | boolean |  | Only show the resulting HTML |

### `tc_hmi_symbol`

Server symbols of the HMI: list, map (PLC variables -> HMI symbols in TcHmiSrv.Config.default.json, type read via ADS if not given), unmap, runtimes (ADS runtimes PLC1 ...), add_runtime. Alternative without mapping by hand: PLC attribute {attribute 'TcHmiSymbol.AddSymbol'} + 'Map Automap Symbols' in Visual Studio. Best edit while the HMI project is not open.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | list, map, unmap, runtimes or add_runtime |
| `symbols` | string |  | map/unmap: PLC variables separated by ',' e.g. MAIN.bStart,MAIN.nSpeed (optionally with type: MAIN.nSpeed:INT) |
| `runtime` | string |  | ADS runtime name (default PLC1) |
| `type` | string |  | map: data type for all symbols without own type, e.g. BOOL, INT, LREAL, STRING(80) |
| `netId` | string |  | add_runtime: AmsNetId (default 127.0.0.1.1.1) |
| `port` | integer |  | add_runtime: ADS port (default 851) |
| `project` | string |  | HMI project (empty = the only one) |
| `dryRun` | boolean |  | Only show what would change |

## Motion

### `tc_motion`

Lists the motion configuration: NC tasks and their axes (name, type, tree path). Read only.

### `tc_motion_add`

Creates an NC task (`kind=nc_task`) or NC axes (`kind=axis`, several with `count`) under the Axes folder of an NC task. Parameters afterwards with `tc_xml_get`/`tc_xml_set`; the drive link is done in the IDE or via `tc_xml_set` on the axis. Takes effect after `tc_activate`.

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

### `tc_runtime_mode`

Switches the TwinCAT runtime of the target to config mode (needed for the I/O scan, stops the PLC) or restarts it in run mode with the active configuration. Allowed targets only, with confirmation. Current state: `tc_plc_state port=10000`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `mode` | string | yes | `config` or `run` |
| `netId` | string |  | AmsNetId (empty = target of the project) |


### `tc_task`

Real-time tasks: `action=list`, `create` (`name`, `cycleUs`, `priority`, `withImage`) or `set` (`path`, `cycleUs`, `priority`, `autoStart`, `cores` = real-time core(s) of the task, e.g. `1`). Takes effect after `tc_activate`.

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
