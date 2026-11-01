# Modules and plugins

TcAgent's tools come in **modules** that can be switched on and off, and **plugins** add tools of your own as separate
programs. Both are set up on the same levels as the knowledge ([KNOWLEDGE_LAYERS.md](KNOWLEDGE_LAYERS.md)): company
(`TCAGENT_COMPANY`), team (`TCAGENT_TEAM`), user (`%LOCALAPPDATA%\TcAgent`) and project (`<solution>\.tcagent`).

## Modules

| Module | Tools for |
|---|---|
| `core` | connection, project tree, reading and writing PLC code, build, coding rules, policy, feedback – always on |
| `plc-code` | plain-text ST, refactoring, editor navigation, SFC, PLCopen export/import, library signatures |
| `test` | TcUnit runs and results, test skeletons, static analysis (needs `runtime`) |
| `runtime` | activate, PLC login/start/stop, ADS read/write, runtime backup, run mode, boot project |
| `io` | I/O devices, scan, variable links, mapping |
| `motion` | NC axes and their links |
| `hmi` | TwinCAT HMI pages, controls, symbols |
| `scope` | Scope View configurations and recordings |
| `safety` | reading the TwinSAFE configuration (never changed) |
| `docs` | project documentation, user manual, static risk analysis and its rules |
| `knowledge` | documents, knowledge levels, skills, workflows, lessons learned, improvement proposals, sources |
| `packages` | TwinCAT packages, libraries, library lock file, SBOM |
| `system` | new solutions and PLC projects, tasks, licenses, real time, CoE, routes, TcCOM, variants, archive |
| `plugin:<id>` | every plugin is a module of its own |

A switched-off module is not in the tool list of the AI client – a shorter list costs fewer tokens and makes the
choice of the right tool easier. If the agent still calls one of its tools, the answer names the module and how to
switch it on. A module that needs a switched-off one is off as well (`test` without `runtime`). The command line
(`TcAgent.exe check`, `tcunit`, `ci` …) is not affected: pipelines keep every function.

`modules.json` on a level:

```json
{ "off": ["hmi", "scope"], "on": ["motion"], "locked": ["hmi"] }
```

The later level wins (company → team → user → project), except for modules that the company or team level lists under
`locked` – a user or project cannot change those. `core` cannot be switched off.

```
TcAgent.exe modules                                  the state of every module and where it was set
TcAgent.exe modules --solution <sln> --json          the same with the project level, as JSON
TcAgent.exe modules disable hmi                      switches it off for you (user level)
TcAgent.exe modules enable scope --level project --solution <sln>
```

A running AI client sees a change after TcAgent was restarted. When an agent connects to another solution, TcAgent
reads its project level and tells the client that the tool list changed (`notifications/tools/list_changed`).

## Plugins

A plugin is a folder `<level>\plugins\<id>\` with a `plugin.json` and a program in any language that speaks
[MCP](https://modelcontextprotocol.io) over stdin/stdout – an existing MCP server can be used as it is.

```json
{
  "id": "todo-finder",
  "name": "TODO finder",
  "description": "Lists the TODO and FIXME comments of the PLC code.",
  "version": "1.0.0",
  "command": "powershell.exe",
  "args": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "server.ps1"],
  "tools": { "find_todos": "read" },
  "timeoutSec": 300
}
```

- `id`: lower-case letters, digits and `-`; the tools appear as `x_<id>_<tool>` (`-` becomes `_`), e.g.
  `x_todo_finder_find_todos`.
- `command` / `args`: a relative path is looked up in the plugin folder, which is also the working directory. The
  program gets `TCAGENT_PLUGIN` (its id) and `TCAGENT_SOLUTION_DIR`.
- `tools`: what every tool does – `read`, `write` (changes the project or files) or `runtime` (acts on a PLC). The
  class decides like for a built-in tool: the mode `ask` blocks everything but `read`, the confirmation asks before
  `runtime`, the audit log records every changing call. A tool that is **not declared** counts as changing and is
  confirmed before every call.
- `timeoutSec`: the longest a call may take (default 600, at most 3600).

TcAgent starts the plugins with the MCP server, lists their tools and passes the calls on. A plugin that ends or does
not start leaves its tools out; the reason is in the log (`%LOCALAPPDATA%\TcAgent\logs`).

### Using TcAgent from a plugin

A plugin reaches TwinCAT through TcAgent: it sends the JSON-RPC request `tcagent/call` with `{ "name": "<built-in
tool>", "arguments": { … } }` on its stdout and gets the result of the tool (`content`, `isError`) back. The call runs
with every check of a call by the agent – target whitelist, mode, TwinSAFE read-only, confirmation, backups before
changes, audit. A plugin cannot call the tools of a plugin, and not those of a switched-off module.

Nothing stops a separate program from opening its own ADS connection or Automation Interface session. Plugins are
programs that run as you – that is why the rules below decide which ones run at all.

### Which plugins run

| Level | Runs |
|---|---|
| company | always (unless the company forbids plugins) |
| user | always (unless the company forbids it) |
| team, project | only after you approved **exactly this content** once: `TcAgent.exe plugins approve <id> [--solution <sln>]` |

The approval is the SHA-256 of all files of the plugin folder (without `.git`): a changed file needs a new approval. A
plugin of a higher level wins over one with the same id further down. The company level's `modules.json` decides about
plugins in general:

```json
{ "plugins": "listed", "allowedPlugins": ["<sha256 of an allowed plugin>"] }
```

`"all"` (default), `"none"` or `"listed"` (only the listed contents – `TcAgent.exe plugins` shows the hash).
`TCAGENT_PLUGINS=0` starts no plugins at all.

```
TcAgent.exe plugins [--solution <sln>]               every plugin with level, status (ready, needs approval, not allowed …) and folder
TcAgent.exe plugins approve <id> --solution <sln>    approves a project plugin (what it runs and its hash are shown)
TcAgent.exe plugins revoke <id> --solution <sln>
```

### Example

[`docs/plugins/todo-finder`](plugins/todo-finder) is a complete plugin in Windows PowerShell: one tool that runs
`tc_plc_search` through `tcagent/call`. Copy the folder to `%LOCALAPPDATA%\TcAgent\plugins\todo-finder` and restart the
AI client.
