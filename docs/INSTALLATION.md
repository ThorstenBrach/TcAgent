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
   click **⋯ > Set up clients** in the chat window.

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

The TwinCAT XAE Shell works as well (TcAgent attaches to `TcXaeShell.DTE`). The VS extension (step 3, chat window and inline
question) is built for the shell with `vsbridge\install-xaeshell.ps1` (close the shell first; it builds as a normal user and
then asks for administrator rights for the copy into the shell's folder - `-BuildOnly` only builds, without installing) -
the VSIX itself only installs into Visual Studio 2022. The server (`TcAgent.exe`) for the shell comes from that VSIX: after an
update install the VSIX of the same version (double-click) and run `install-xaeshell.ps1`; on its next start the shell copies
the new server from the VSIX and registers it (the chat window shows "TcAgent server x ≠ extension y" in orange until then -
"⋯ > Set up clients" does it at once, then "New chat").

Optional, for the Scope and HMI tools: **TE1300 Scope View** (`tc_scope`) and **TE2000 HMI Engineering** (`tc_hmi*`).

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

# close Visual Studio, then in a PowerShell started with "Run as administrator":
powershell -ExecutionPolicy Bypass -File .\vsbridge\install.ps1
```

`install.ps1` copies into the Visual Studio installation (Program Files) and therefore needs administrator rights;
without them it stops before changing anything and says so. `-ExecutionPolicy Bypass` only allows the script to run - it
gives no rights. The VSIX (step 3, double-click) needs none.

The extension is copied to `Common7\IDE\Extensions\TcAgentBridge` of the VS installation. To verify after starting VS:
log at `%LOCALAPPDATA%\TcAgent\bridge\bridge.log` (first line `==== TcAgentBridge … gestartet`).

Uninstall: `vsbridge\uninstall.ps1` as administrator.

## 4. Configure AI clients

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-clients.ps1 -SolutionDir <folder of your TwinCAT solution>
```

The script

* keeps the settings of TcAgent (`TCAGENT_*`) in **`%LOCALAPPDATA%\TcAgent\settings.json`** – the single source for
  all clients (on the first run they are taken over once from an existing client configuration). Change a setting there
  and run the script again; every client gets the same values. `TcAgent.exe` itself also reads the `env` section of
  `settings.json` at every start (pipelines and the windows of the extension included); a variable that is already set
  in the environment wins,
* registers TcAgent as an MCP server in all clients it finds (Claude Desktop, Claude Code, Copilot in VS,
  Copilot CLI, Gemini CLI, Codex CLI),
* installs the agent guide for each client (`AGENTS.md`, `GEMINI.md`, `copilot-instructions.md`, the Claude Code skill);
  with `-SolutionDir` also in the project so it reaches the team via Git: **`AGENTS.md`** is the single source there,
  `CLAUDE.md` and `GEMINI.md` only import it, `.github\copilot-instructions.md` points to it,
* creates the chat window configuration (`%LOCALAPPDATA%\TcAgent\chat-backends.json`).

A file is only written when its content changes; then a backup `*.bak-<date>` is created first (per file the very first backup – the state before TcAgent – and the last 3 are kept). Only the `tcagent` entry is modified.
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
| `TCAGENT_MODE` | `agent` | `propose`: code changes only as a diff (reading, check and build still work, no other changes) · `ask`: read-only |
| `TCAGENT_CONFIRM` | `runtime` | Ask before runtime actions (activate, PLC start/stop, ADS write, start mode, runtime mode, routes, package changes): Yes/No dialog in Visual Studio from the TcAgent Chat, question dialog of the client elsewhere (if it supports MCP elicitation); if nobody can be asked, the action is refused. `all` = also before project changes, `off` = never ask (pipelines, unattended agents - a deliberate setting) |
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
| Chat window: attached PDF/images are not read | The Claude backend needs `Read` in `--allowedTools` - TcAgent adds it automatically together with `--add-dir` for the attachments folder; check a custom `chat-backends.json`. |
| Chat window: "… could not be started" | CLI not installed or not in PATH; enter the full path under `exe` in `chat-backends.json`. |
| Setup message "TcAgent.exe … being used by another process" | An AI client (e.g. Claude Desktop) still runs the previous TcAgent.exe. Quit it (tray icon > Quit), then "⋯ > Set up clients" in the TcAgent Chat. |
| `.tcscopex` shows a plain file icon, "Could not load file with the extension .tcscopex. Please upgrade your TwinCAT Measurement" | The Scope integration in VS and the installed Scope files have different versions. Update them together: `tcpkg upgrade TE1300.ScopeViewProfessional.XAE` (VS closed), then `devenv /updateconfiguration`. |
| `tc_scope record_start`: "not loaded in Visual Studio" | Open the .tcscopex once in the IDE (double-click), PLC in Run. |
| VS asks "Save dataset?" during a Scope change | Only with older TcAgent versions - current versions back the recording up and answer the question themselves. |
| `tc_hmi action=controls`: "Control package not found" | Build the HMI project once in the IDE (restores the NuGet packages into `<solution>\Packages`). |
| XAE Shell crashes during HMI/Scope changes (event log: `OutOfMemoryException`, TcXaeShell.exe) | The XAE Shell is a 32-bit process; a large project with HMI and Scope open exhausts its memory, reloading a project then crashes it. Restart the shell regularly, close designers you do not need, or use Visual Studio 2022 (64-bit) for HMI/Scope work. TwinCAT 4026 also offers a 64-bit XAE Shell (TcPkg setting `useTcXaeShell64`, see the TwinCAT installation documentation); TcAgent and `install-xaeshell.ps1` support it. TcAgent refuses unload/reload when the 32-bit shell is close to the limit. |
| `vsbridge\install.ps1` / `uninstall.ps1`: "Administrator rights are needed" (before 0.48.2: `UnauthorizedAccessException` at `New-Item … Extensions\TcAgentBridge`) | The script writes into the Visual Studio installation. Start PowerShell with "Run as administrator" and run the script again - or install the VSIX instead (per user, no rights needed). |
| `vsbridge\install-xaeshell.ps1`: CS0234 for `SVsDifferenceService` / `IVsDifferenceService` / `__VSDIFFSERVICEOPTIONS` in `Microsoft.VisualStudio.Shell.Interop` | Fixed in 0.48.2 (the build references `Microsoft.VisualStudio.Shell.Interop.11.0` of the shell). With a newer script and the error still there: is `-ShellIde` the `Common7\IDE` folder of the XAE Shell, and is `PublicAssemblies\Microsoft.VisualStudio.Shell.Interop.11.0.dll` there? `-BuildOnly` tries the build without installing. |
| `vsbridge\build.ps1`: CS1705 (version conflict) | Several versions of a VS assembly installed; the script uses the highest. Please report as an issue, with output. |

Logs: `%LOCALAPPDATA%\TcAgent\` (`audit.jsonl`, `logs\tcagent-<date>.log`, `bridge\bridge.log`, `libcache\`). Agent feedback:
`feedback\feedback.jsonl` in the TcAgent folder - or a shared team folder/UNC path via `TCAGENT_FEEDBACK` (one file per day
and machine); report with `TcAgent.exe feedback --open --markdown feedback.md`. Missing audit entries: `tc_policy action=audit_check`.
