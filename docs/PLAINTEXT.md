# Plain-text ST files

TcAgent can write the PLC code of a TwinCAT project as plain Structured Text files – one `.st` file per POU, GVL, DUT
and interface, with the methods, properties and actions inside the POU file – and take changed files back into the
existing project. Use it for code reviews and diffs in Git, for editing with any text editor or AI tool, and as a CI
gate that keeps a committed text mirror of the project up to date.

```powershell
TcAgent.exe st export --solution MyMachine.sln              # project -> st\<PLC project>\...\<name>.st
TcAgent.exe st check  --solution MyMachine.sln              # exit 1 if the .st files or references.json differ
TcAgent.exe st import --solution MyMachine.sln --dry-run    # what would change in the project
TcAgent.exe st import --solution MyMachine.sln              # .st files -> existing project
```

The agent uses the same with `tc_plaintext` (`action` = `export`, `check` or `import`, see [TOOLS.md](TOOLS.md#tc_plaintext)).

All three commands work on the project files (`.plcproj`, `.TcPOU`, `.TcGVL`, `.TcDUT`, `.TcIO`) – no IDE is started,
a large project takes seconds. Tasks (`.TcTTO`) and other files are not exported.

## Folder layout

```
<folder>\<PLC project>\references.json                         library references of the PLC project
<folder>\<PLC project>\<folder in the PLC project>\<name>.st
```

For example `st\M2XLibrary\references.json` next to `st\M2XLibrary\Core\...`.

`<folder>` is `--out` (export/check) or `--from` (import), default `<solution folder>\st`. `<PLC project>` is the name of
the `.plcproj` file without extension. The folders below it are the folders of the PLC project
(`<Compile Include="Gpib\GpibSerialFB.TcPOU">` → `st\MyPlc\Gpib\GpibSerialFB.st`). The `.plcproj` files are found by
searching the solution folder (without `bin`, `obj`, `_Boot`, `_Libraries`, `.git`, `.vs`); `--plc` selects PLC projects
by `.plcproj` name or PLC project name, also with ` Project` (`--plc MyPlc` or `--plc "MyPlc Project"`).

## references.json

`export` writes the library references of each PLC project to `<folder>\<PLC project>\references.json`, in the format
of the [SRCI repository](https://github.com/ThorstenBrach/SRCI) (`RobotLibrary\references.json`): UTF-8 without BOM,
CRLF, 2 blanks indentation, no line end after the closing `]`.

```json
[
  {
    "Name": "RobotLibrarySysDep",
    "Version": "*",
    "DistributorName": "Thorsten Brach",
    "Namespace": null
  },
  {
    "Name": "Tc3_IotBase",
    "Version": "*",
    "DistributorName": "Beckhoff Automation GmbH",
    "Namespace": "IotBase"
  }
]
```

Source is the `.plcproj`:

| `.plcproj` | references.json |
|---|---|
| `<PlaceholderReference Include="Standard">` with `<DefaultResolution>Tc2_Standard, 3.4.5.0 (Beckhoff Automation GmbH)</DefaultResolution>` | `Name` `Tc2_Standard`, `Version` `3.4.5.0`, `DistributorName` `Beckhoff Automation GmbH` – from the default resolution, not from `Include` |
| `<PlaceholderResolution Include="Standard">` with `<Resolution>Tc2_Standard, 3.4.7.0 (…)</Resolution>` (placeholder properties in the library manager) | overrides the default resolution of that placeholder – the library TwinCAT really uses; an unreadable resolution is a warning and the default stays |
| `<LibraryReference Include="Tc3_IotBase,newest,Beckhoff Automation GmbH">` | `Name`, `Version`, `DistributorName` from `Include`; `newest` becomes `*` |
| `<Namespace>` | `null` when it equals the name, otherwise the namespace |
| `<Parameters>` of a placeholder (e.g. TcUnit's `GVL_PARAM_TCUNIT`) | not taken over |

The entries are sorted by name (ordinal, case-insensitive), so that Git diffs stay small. The versions are the ones
the project asks for (`*` = newest installed), not the resolved ones – for the resolved versions see `TcAgent.exe lock`
and `TcAgent.exe sbom`. A placeholder whose `DefaultResolution` is not `Name, Version (Distributor)` is left out with a
warning.

* `export` writes the file only when its content changes (counted with the `.st` files in "file(s) written" /
  "unchanged"); `--clean` never removes it – only `.st` files.
* `check` compares it like a `.st` file, line ends ignored: `missing: <PLC project>\references.json` or
  `differs: …` → exit 1.
* `import` ignores it: references are not written back into the project (no warning).

## File format

The text is TwinCAT's own declaration and implementation text, unchanged, plus `END_` markers on their own line
(column 1). Methods, properties and actions are written **inside** the POU, before its `END_` marker (IEC 61131-3 3rd
edition layout), in the order of the project file: a blank line after the POU body, before every sub-block and before
the closing `END_` marker.

```
// comments and attributes before the header belong to the declaration
FUNCTION_BLOCK FB_Axis EXTENDS FB_Base
VAR_INPUT
  Execute : BOOL;
END_VAR
_trig(CLK := Execute);                   <- implementation of the POU

/// comments directly above a header belong to that method/property/action
METHOD PUBLIC M_Start : BOOL
VAR_INPUT
  Speed : LREAL;
END_VAR
M_Start := TRUE;
END_METHOD

PROPERTY PUBLIC P_Speed : LREAL
GET
VAR
END_VAR
P_Speed := _speed;
END_GET
SET
_speed := P_Speed;
END_SET
END_PROPERTY

ACTION A_Reset:
_speed := 0;
END_ACTION

END_FUNCTION_BLOCK                       <- END_PROGRAM / END_FUNCTION / END_FUNCTION_BLOCK
```

| Object | Content of the `.st` file |
|---|---|
| POU (program, function block, function) | declaration, implementation, then `METHOD … END_METHOD`, `PROPERTY … GET … END_GET SET … END_SET END_PROPERTY`, `ACTION <name>: … END_ACTION`, then `END_PROGRAM`/`END_FUNCTION_BLOCK`/`END_FUNCTION`. Without sub-objects the `END_` marker follows the body directly |
| Interface (`.TcIO`) | declaration, the methods and properties with declarations only (`PROPERTY P : INT` / `GET` / `END_GET` / `END_PROPERTY`), then `END_INTERFACE` |
| GVL, DUT | the declaration only (`VAR_GLOBAL … END_VAR`, `TYPE … END_TYPE` are complete) |

**How the import finds the end of the POU body:** the first `METHOD <name>`, `PROPERTY <name>` or `ACTION <name>:` line
in column 1 ends it. Comment and attribute lines directly above that header (`//`, `(*`, `{attribute ...}`, no blank line
in between) belong to the sub-block – so keep a blank line between the POU code and the first sub-block, as the export
does. Blank lines at the end of the POU code do not count as a change.

**The older layout is still accepted:** `END_FUNCTION_BLOCK` (`END_PROGRAM`, `END_FUNCTION`, `END_INTERFACE`) right after
the body, the sub-blocks after it. Both layouts import identically, also mixed. Text folders written by earlier builds
therefore import unchanged, but `st check` reports their POUs with sub-objects as `differs` until the next `st export`
rewrites them (once). The export itself uses the older layout only for a POU that the inside layout cannot reproduce exactly
(e.g. a method declaration with a blank line between its doc comment and its `METHOD` line).

**Safety net:** after splitting, no declaration or implementation may contain a block line (`METHOD …`, `PROPERTY …`,
`ACTION …:`, `GET`, `SET`, `END_METHOD`, `END_PROPERTY`, `END_ACTION`, `END_GET`, `END_SET`, `END_FUNCTION_BLOCK`,
`END_PROGRAM`, `END_FUNCTION`, `END_INTERFACE` – also indented). Such a file is refused with its line number, e.g. a
`METHOD` without its `END_METHOD`, or an indented method inside the POU code.

### Declaration and implementation

On import the body of a POU, method, `GET` or `SET` is split into declaration and implementation:

1. at a line `// @implementation` if there is one;
2. otherwise after the **last** line that starts with `END_VAR`;
3. otherwise after the header line (`FUNCTION_BLOCK …`, `PROGRAM …`, `FUNCTION …`, `METHOD …`) – a `GET`/`SET` without
   `END_VAR` is all implementation.

The export checks every block: if this rule would not give back exactly TwinCAT's declaration and implementation – e.g.
a comment after the last `END_VAR`, a method without `VAR` blocks but with comments after the header, `END_VAR` in a
comment of the implementation – it writes the separator line `// @implementation` between them. Keep that line when you
edit the file; import removes it.

### Graphical implementations

SFC, LD, FBD, CFC (and other non-ST) implementations are not plain text. The file contains the declaration and the line

```
// @implementation SFC - not plain text, kept as it is in the project
```

instead of the implementation. Import keeps such implementations untouched – only the declaration can be changed.
Actions of an SFC chart are ordinary `ACTION` blocks when they are written in ST. SFC transitions, `<Folder>` entries
inside a POU and other XML (ObjectProperties …) are not exported and stay as they are.

### Line ends, trailing newlines, encoding

* `.st` files (and `references.json`) are UTF-8 without BOM, CRLF line ends. Non-ASCII characters (umlauts …) stay as they are. Import accepts
  LF line ends too (e.g. Git with `core.autocrlf`); `st check` compares independent of line ends.
* TwinCAT declarations usually end with a line end, implementations usually not. In the file the `END_` marker is always
  on its own line. On import a block counts as **unchanged** when its text equals what the export writes for it – then
  the project text is kept byte for byte, including its own line ends (some generated files use LF). A changed block
  keeps the style of the original text: a trailing line end only if the original had one, LF only if the original was
  LF-only, otherwise CRLF. New blocks: the declaration ends with a line end, the implementation not.

## What import does

* **Matching:** a `.st` file belongs to the object with the same PLC project, folder and name (file name = object name;
  the name in the declaration header must be the same). Methods, properties and actions are matched by name, case
  insensitive like IEC 61131-3; `GET`/`SET` by kind.
* **Changed blocks:** the CDATA text of the changed declaration/implementation is replaced. Ids, attributes, order and
  unchanged blocks stay byte-identical; the `<LineIds>` of a changed block are removed (TwinCAT creates them again).
* **New blocks:** new `METHOD`, `PROPERTY` (with its `GET`/`SET`), `ACTION`, or a new `GET`/`SET` of an existing property
  are added in TwinCAT's XML layout with new Ids.
* **New files:** a `.st` file without object creates a new `.TcPOU`, `.TcIO`, `.TcGVL` or `.TcDUT` (from the first
  `FUNCTION_BLOCK`/`PROGRAM`/`FUNCTION`, `INTERFACE`, `VAR_GLOBAL` or `TYPE` keyword) with the `TcPlcObject` version of
  the project's other files, and the `<Compile Include>` entry (plus missing `<Folder Include>` entries) in the
  `.plcproj` – inserted sorted, the rest of the file unchanged.
* **Never deleted:** objects without `.st` file are listed as "not in the text folder" and kept; methods, properties,
  actions and accessors missing in the file are listed as "kept".
* **Refused** (the whole import writes nothing, exit 1): a file that cannot be parsed (missing `END_` marker …), a
  renamed object (declaration header ≠ file name), an object moved to another folder, a sub-object whose kind changed
  (method ↔ action), a new object or block with a graphical implementation, a file that is not UTF-8, a new file whose
  `.TcPOU` already exists on disk without being in the `.plcproj`.
* Files are only written when their content changes; BOM and line ends of the project files are kept.
* Building a new project from `.st` files is not supported – import works on an existing PLC project.
* Code lines that start with a block keyword of the format (`END_METHOD`, `END_FUNCTION_BLOCK`, `METHOD <name>`, `GET`
  …, e.g. inside a block comment) cannot be represented: the export warns ("cannot be read back" / "does not reproduce
  the project exactly") and `st check` lists the object as `cannot be represented exactly`.

### The IDE must not have the solution open

TwinCAT keeps the project in memory and would overwrite files changed behind its back on the next save (or ask to
reload them). Therefore:

* `TcAgent.exe st import` (without `--dry-run`) looks in the Running Object Table for Visual Studio / XAE Shell
  instances and is **refused (exit 2) when one has the solution open**. If the check itself fails (e.g. an IDE does not
  answer within 60 s) the import is refused too, unless `--force` is given – use it only when you know the IDE is closed.
* `tc_plaintext action=import` (the agent) writes **through the connected IDE** when the solution is open there: changed
  declarations/implementations with the same write path as `tc_plc_write`, new methods/properties/actions/accessors
  like `tc_plc_create`, new objects as generated object files imported like `tc_import kind=pou_files`. Nothing is
  saved until `tc_save`; then `tc_check`. If the solution is open in a different IDE than the connected one the import
  is refused; if it is not open anywhere the files are written directly. `dryRun=true` (always in `propose` mode) only
  lists the changes.

`st export`/`st check` read the files on disk: save the project in the IDE first if it has unsaved changes.

## In the pipeline

```powershell
TcAgent.exe st check --solution MyMachine.sln     # exit 1: the committed st\ folder is out of date
```

Commit the `st` folder together with the project: pull requests then show readable ST diffs next to the XML, and
`st check` fails the pipeline when someone changed the project without running `st export`. In
`.tcagent/pipeline.json`: `{ "run": "st", "args": ["check"] }`. See [PIPELINE.md](PIPELINE.md#plain-text-st-files).

`st check` covers `references.json` too: a changed library reference without `st export` fails the gate.

Exit codes: `0` OK · `1` `check`: differences (listed as `missing`, `differs`, `not in the project`,
`cannot be represented exactly`); `import`: errors,
nothing written · `2` wrong call, no matching PLC project, or `import` refused because the solution is open in an IDE ·
`3` the project files cannot be read.
