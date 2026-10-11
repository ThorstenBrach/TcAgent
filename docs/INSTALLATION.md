# Installation and setup

This guide goes step by step from a fresh machine to the first agent working in TwinCAT.
All commands are in **PowerShell**. Steps that need administrator rights say so – everything else runs as a normal user.

## Quick install (recommended): VSIX

1. Close Visual Studio. Double-click `TcAgent-<version>.vsix` (from the releases, or built with `build-vsix.ps1`).
   It installs per user – no administrator rights.
2. Start Visual Studio 2022 with a TwinCAT solution. On first start TcAgent
   * copies itself to `%LOCALAPPDATA%\TcAgent\app\<version>\` (a stable path for the AI clients),
   * generates the Automation Interface interop from your local TwinCAT installation (`%LOCALAPPDATA%\TcAgent\lib\`),
   * asks whether it should register itself in the AI clients it finds (same as `setup-clients.ps1`, with backups).
   The result appears in the **TcAgent Chat** window; the log is `%LOCALAPPDATA%\TcAgent\setup.log`.
3. Sign in to at least one AI client ([step 5](#5-install-and-sign-in-to-ai-clients)). If you install a client later,
   click **Set up clients** in the chat window.

Updates: install the newer VSIX – clients are re-registered automatically. Uninstall: *Extensions > Manage Extensions*;
`%LOCALAPPDATA%\TcAgent\` and the client entries stay (backups `*.bak-*` next to each config file).

If you previously installed the extension with `vsbridge\install.ps1`, run `vsbridge\uninstall.ps1` as administrator
first – otherwise Visual Studio sees the extension twice.

The remaining sections describe the **manual / developer setup** from source.

## 1. Prerequisites

| What | Why | Check |
|---|---|---|
| Windows 10 (1809+) or 11 | | |
| TwinCAT 3.1 XAE in **Visual Studio 2022** | the IDE being controlled (tested with build 4026) | start VS, open a TwinCAT solution |
| TE1030 license (TwinCAT 3 Documentation Generation) | required | TwinCAT license manager |
| .NET 8 SDK | to build TcAgent | `dotnet --list-sdks` |
| Windows SDK or .NET Framework 4.8 Developer Pack | `TlbImp.exe` for the interop assembly | VS Installer under "Individual components" |
| At least one AI client | see [step 5](#5-install-and-sign-in-to-ai-clients) | |

TcXaeShell is supported as well but less tested. The VS extension (step 3) is only available for Visual Studio 2022.

## 2. Build TcAgent

```powershell
cd <path>\TcAgent
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

On the first run the script generates `lib\Interop.TCatSysManagerLib.dll` from the type library of your TwinCAT installation
and then builds `bin\publish\TcAgent.exe`.

**Important:** If an AI client that already uses TcAgent is running, `TcAgent.exe` is locked. Quit the client completely
first – for Claude Desktop also via the tray icon ("Quit"), not just by closing the window.

Smoke test without AI (Visual Studio with a TwinCAT solution open):

```powershell
powershell -ExecutionPolicy Bypass -File .\test.ps1
```

## 3. VS extension TcAgentBridge (recommended)

Code, build, tests, I/O and runtime work without the extension. **With** it you also get: cursor and selection,
go to definition, cross-references, library interfaces (including compiled libraries) and the chat window.

```powershell
# build (normal user)
powershell -ExecutionPolicy Bypass -File .\vsbridge\build.ps1

# close Visual Studio, then as ADMINISTRATOR:
powershell -ExecutionPolicy Bypass -File .\vsbridge\install.ps1
```

The extension is copied to `Common7\IDE\Extensions\TcAgentBridge` of the VS installation. To verify after starting VS:
log at `%LOCALAPPDATA%\TcAgent\bridge\bridge.log` (first line `==== TcAgentBridge … gestartet`).

Uninstall: `vsbridge\uninstall.ps1` as administrator.

## 4. Configure AI clients

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-clients.ps1 -SolutionDir <folder of your TwinCAT solution>
```

The script

* registers TcAgent as an MCP server in all clients it finds (Claude Desktop, Claude Code, Copilot in VS,
  Copilot CLI, Gemini CLI, Codex CLI), keeping existing `TCAGENT_*` settings,
* installs the agent guide as `AGENTS.md`, `GEMINI.md` and `copilot-instructions.md` (globally and, with
  `-SolutionDir`, also in the project so it reaches the team via Git),
* creates the chat window configuration (`%LOCALAPPDATA%\TcAgent\chat-backends.json`).

Before each change a backup `*.bak-<date>` is created; only the `tcagent` entry is modified.
Clients that are not installed are skipped – after installing one later, simply run the script again.

Optional parameters: `-Solution <part of the solution path>` (which VS instance), `-AllowTargets <AmsNetIds>`
(see [guardrails](#7-configure-guardrails)).

## 5. Install and sign in to AI clients

One is enough. The chat window in TwinCAT needs one of the command-line variants.

### Claude Code (Claude, with Pro/Max subscription)

Claude Desktop alone is not enough for the chat window – it needs Claude Code.

```powershell
irm https://claude.ai/install.ps1 | iex
```

If the installer reports that `%USERPROFILE%\.local\bin` is not in PATH:

```powershell
[Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path","User") + ";$env:USERPROFILE\.local\bin", "User")
```

Then open a **new** PowerShell window, check `claude --version`, run `claude` once and sign in in the browser
(`/exit` quits). Then run `setup-clients.ps1` again.

### GitHub Copilot in Visual Studio

Visual Studio 2022 **17.14** or later. `setup-clients.ps1` writes `%USERPROFILE%\.mcp.json`. In VS open Copilot Chat,
select **Agent** mode, enable the `tcagent` tools via the tools icon and allow tool calls the first
time.

### GitHub Copilot CLI

Install per GitHub's instructions, sign in with `copilot` (for Business/Enterprise the CLI must be enabled by
policy). Configuration: `~\.copilot\mcp-config.json`.

### Gemini CLI

Install per Google's instructions, sign in on first start of `gemini`. Configuration: `~\.gemini\settings.json`
(the tcagent server is marked as trusted there – the guardrails live in TcAgent itself).

### ChatGPT (OpenAI Codex CLI)

Install per OpenAI's instructions, sign in with `codex login` (ChatGPT subscription). Configuration: `~\.codex\config.toml`.

## 6. First test

1. Open Visual Studio with the TwinCAT solution (wait until the PLC project is loaded).
2. Start the AI client (restart Claude Desktop completely).
3. Ask: *"Connect to TwinCAT, show me the PLC project tree and build the project."*

With the extension, additionally: open a method, click into a variable name and ask
*"Where is this declared and where is it written?"*

## 7. Configure guardrails

In the client's MCP configuration under `env` (or via parameters of `setup-clients.ps1`):

| Variable | Default | Meaning |
|---|---|---|
| `TCAGENT_ALLOW_TARGETS` | `local` | Target systems for activation, online commands, ADS writes. Comma-separated AmsNetIds; `local` = this PC |
| `TCAGENT_MODE` | `agent` | `propose`: code changes only as a diff, nothing else · `ask`: read-only |
| `TCAGENT_SOLUTION` | – | Optional: part of the solution path to pin one VS instance. Normally not needed – the TcAgent Chat uses its own IDE; with several open solutions the user picks one from a list |
| `TCAGENT_AUDIT` | `%LOCALAPPDATA%\TcAgent\audit.jsonl` | Log of all write calls |

In addition, every client can require confirmation of individual tool calls – strongly recommended for runtime tools
(`tc_activate`, `tc_plc_online`, `ads_write`) on systems with real hardware.

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Tools do not appear in the client | Client not fully restarted (Claude Desktop: quit via tray icon). Check the path in the configuration. |
| `build.ps1` reports the file is locked | A client still has `TcAgent.exe` open – quit the client, build again. |
| "No running Visual Studio/XAE instance" | Open VS with a TwinCAT solution; with several instances pick one from the list the client shows, or use `tc_attach pid=…` / `solution=…`. |
| `tc_build` reports 0 errors although the code is wrong | Unused POUs are not compiled during build. Check for compiler errors with `tc_check` ("Check all objects") or `tc_build checkAll=true`. |
| TcUnit output appears as errors | These are runtime messages in the error list; `tc_errors kind=compile` or `tc_test_results` separate them. |
| `tc_ide command=ping` fails | Extension not installed or VS not restarted. Check `bridge.log`. |
| Yellow bar in VS "stopped responding … TcAgentBridge" | First export of a large library (runs on the UI thread). Do not disable – afterwards everything comes from the cache. |
| Cursor shows "declaration, line 1" | No click into the editor since VS was opened. Click into the code once. |
| Chat window: "… could not be started" | CLI not installed or not in PATH; enter the full path under `exe` in `chat-backends.json`. |
| `vsbridge\build.ps1`: CS1705 (version conflict) | Several versions of a VS assembly installed; the script uses the highest. Please report as an issue, with output. |

Logs: `%LOCALAPPDATA%\TcAgent\` (`audit.jsonl`, `bridge\bridge.log`, `libcache\`). Agent feedback:
`feedback\feedback.jsonl` in the TcAgent folder.
