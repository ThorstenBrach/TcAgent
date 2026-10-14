# TcAgent in CI pipelines

The same `TcAgent.exe` that serves AI clients also has a command line for build servers. It opens the solution in a
hidden Visual Studio, works through the TwinCAT Automation Interface and reports in a form every CI system understands:
exit code, JUnit XML and inline annotations.

| Command | What it does | Typical stage |
|---|---|---|
| `TcAgent.exe check --solution X.sln` | "Check all objects" of every PLC project – compiler errors, also in unused POUs; no code is generated | every push / pull request |
| `TcAgent.exe build --solution X.sln` | builds the solution | before packaging |
| `TcAgent.exe analyze --solution X.sln --sarif sa.sarif` | static analysis (TE1200 license) – rule violations as annotations, SARIF for GitHub code scanning (4026.20+) | pull requests |
| `TcAgent.exe library --solution X.sln --out MyLib.library` | builds and saves the PLC project as library | releases of your own libraries |
| `TcAgent.exe scope --solution X.sln --out signals.csv --seconds 30` | records with the Scope configuration of the solution and exports the data (PLC must be running, e.g. after `test`) | HIL/SIL runs, signal evidence |
| `TcAgent.exe doc --solution X.sln --out doc/X.html` | program documentation as HTML (call hierarchy, diagrams, risks, reference; with the explanations stored by the agent) | documentation as build artifact |
| `TcAgent.exe doc --solution X.sln --risks --fail-on high` | prints the static risk list (tc_doc action=risks, including the project's `.tcagent\risk-rules.md`); exit code 1 on risks of that severity or higher | code review gate with your own rules |
| `TcAgent.exe archive --solution X.sln --out X.tszip` | archive of the whole solution | releases, support |
| `TcAgent.exe test --solution X.sln` | builds, activates on the test target, starts the PLC, waits for TcUnit, collects the results | every push to `main`, nightly |
| `TcAgent.exe sil --solution X.sln --plc Tests` | software in the loop: like `test` on the runtime of the build PC, checks Run mode, starts the PLC if needed, optional Scope recording, always cleans up | every push / pull request |
| `TcAgent.exe hil --solution X.sln --target 5.1.2.3.1.1 --allow 5.1.2.3.1.1` | hardware in the loop: the same on a remote test PLC with real I/O – only with an explicit allowance | nightly, release candidates |
| `TcAgent.exe feedback --open --markdown feedback.md` | Markdown report of the agents' `tc_feedback` entries (grouped by tool, ready for GitHub issues); `--summary` for counts, `--path` for another store; no IDE needed | weekly triage of a shared feedback folder |

```powershell
TcAgent.exe check --solution .\MyMachine.sln --junit results\check.xml
TcAgent.exe test  --solution .\MyMachine.sln --junit results\tests.xml --timeout 900
```

**Exit codes:** `0` OK · `1` compiler errors or failed tests · `2` wrong call, or `sil`/`hil` refused because the target is
not allowed · `3` infrastructure problem (IDE could not be started, no route, activation failed, Run mode not reached,
TcUnit did not finish in time). Pipelines can treat `3` as "retry" and `1` as "red"; `2` needs a fix in the pipeline definition.

Without arguments `TcAgent.exe` is the MCP server as before; `TcAgent.exe help` lists all options.

## What `test` does

1. Opens the solution in a new, hidden Visual Studio (or `--attach` to a running one).
2. Checks the target: only targets allowed by `TCAGENT_ALLOW_TARGETS` (default: this PC, `127.0.0.1.1.1`).
   `--target <AmsNetId>` sets another one – it must be allowed as well.
3. Disables all I/O devices (the build machine has no EtherCAT hardware). Only in a Visual Studio started by TcAgent;
   `--keep-io` keeps them, `--disable-io` forces it.
4. Builds the solution – compiler errors stop here (exit code 1, errors in the JUnit file).
5. Creates the boot project with autostart for the PLC projects (`--plc` selects them), so the PLC starts by itself.
6. Activates the configuration and restarts TwinCAT in run mode.
7. Waits for TcUnit (`TESTS FINISHED RUNNING`) and until all result lines have arrived, then writes the JUnit file.
8. Closes Visual Studio **without saving** – the changes from steps 3 and 5 never reach your repository.

## SIL – software in the loop (`sil`)

`sil` runs the TcUnit tests on the TwinCAT runtime **of the build machine** and leaves it clean afterwards:

```powershell
TcAgent.exe sil --solution .\MyMachine.sln --plc MyMachine_Tests --junit results\sil.xml --timeout 600
```

1. Target: `--target local` (default) = `127.0.0.1.1.1` / the PC's own AmsNetId. A separate runtime on the same PC
   (TwinCAT usermode runtime) is passed with `--target <its AmsNetId>` and must be in `TCAGENT_ALLOW_TARGETS`.
   The whitelist check is the one of the MCP tools `tc_activate`, `tc_plc_online` and `tc_runtime_mode`;
   `TCAGENT_MODE=ask`/`propose` refuse the run as well (exit 2).
2. The runtime must answer via ADS (system service, port 10000) – otherwise exit 3 before anything is changed.
3. The project's target system is set to the test runtime (restored at the end), I/O devices are disabled (in a
   Visual Studio started by TcAgent; `--keep-io` / `--disable-io`), the solution is built, the selected PLC projects
   get a boot project with autostart.
4. Activation and restart in Run mode, then TcAgent waits until the runtime **is** in Run mode (max. 120 s; otherwise
   exit 3 with the usual causes: licenses, real-time, configuration errors).
5. Waits for TcUnit like `test`. If the PLC shows no TcUnit output 30 s after Run mode, TcAgent logs in and starts
   the PLC once (the commands of `tc_plc_online`).
6. JUnit, annotations, exit code like `test`.
7. **Clean-up, always** (also after errors and timeouts): Scope recording stopped, logout, runtime switched to
   **Config mode** (PLC stopped; `--keep-running` leaves it running), target system restored; in an attached
   Visual Studio (`--attach`) also the I/O devices and the autostart setting are restored. Nothing is saved.

Optional Scope recording: `--scope <measurement project>/<scope>` and/or `--out <file>` record with a Scope
configuration of the solution from Run mode until TcUnit has finished and export it (default
`scope-sil.csv` next to the JUnit file). The file path is logged (`SCOPE OK: …`) and added to the JUnit file as suite
"Scope recording" (`[[ATTACHMENT|file]]`, shown by Jenkins and GitLab); a Scope problem is reported as skipped and
does not fail the run.

## HIL – hardware in the loop (`hil`)

`hil` is the same flow against a **remote test PLC** with real I/O:

```powershell
TcAgent.exe hil --solution .\MyMachine.sln --target 5.80.201.232.1.1 --allow 5.80.201.232.1.1 `
                --plc MyMachine_Tests --scope Measurement/Axes --out results\axes.csv --junit results\hil.xml
```

* **Explicit allowance.** A pipeline cannot answer a confirmation dialog, so the test PLC must be allowed explicitly –
  in `TCAGENT_ALLOW_TARGETS` of the agent, or with `--allow <AmsNetId>` in the pipeline definition (reviewed like
  code). Without it the run is refused with exit 2 before anything happens. `--allow` exists only on the command line,
  an AI agent (MCP server) cannot use it.
* **Route first.** TcAgent checks that the target answers via ADS. If not, it runs a broadcast search (the code of
  `tc_route_search`) and tells you whether the PLC is visible but the **route is missing** (add it once on the build
  machine – the pipeline never adds routes or handles credentials) or not reachable at all (power, subnet, firewall
  UDP 48899 / TCP 48898, wrong AmsNetId).
* **I/O stays enabled** – that is the point of HIL (`--disable-io` disables it anyway).
* **Machine safety is your responsibility.** Activation restarts TwinCAT on the target and the test program drives
  real outputs. Use a test bench, not a production machine. At the end the target is switched to Config mode
  (outputs off) unless `--keep-running` is given.
* One HIL job per test PLC at a time (GitLab `resource_group`, Azure exclusive lock / environment, GitHub `concurrency`).

## Library and its tests in one solution

Keep the library code and its tests as **two PLC projects in one solution** – the library project never contains test
code, nothing has to be removed or disabled before saving the library:

```
MyLib.sln
├── MyLib          PLC project: the library code (FBs, functions, interfaces), project information = library title/version,
│                  no task – it is not run itself
└── MyLib_Tests    PLC project: references TcUnit and MyLib (placeholder/version '*' = newest installed),
                   test suites + MAIN with TcUnit.RUN(), task
```

The pipeline:

| Stage | Command | Why |
|---|---|---|
| check | `check --solution MyLib.sln` | compiler errors in both projects, also in unused POUs |
| build | `library --solution MyLib.sln --plc MyLib --install --out build\MyLib.library` | builds and installs the **candidate** into the library repository of the build machine, so `MyLib_Tests` tests exactly this code |
| sil | `sil --solution MyLib.sln --plc MyLib_Tests --junit results\sil.xml` | runs only the test project (boot project/autostart only for it) |
| library | `library --solution MyLib.sln --plc MyLib --out artifacts\MyLib.library` | saves the library from the library project only – published as artifact only when the tests are green |

Ready-made definitions: [library-github-actions.yml](pipeline/library-github-actions.yml),
[library-azure-pipelines.yml](pipeline/library-azure-pipelines.yml).

Notes: increase the library version in the project information for every release – the candidate installed in the build
stage has the same version and replaces the previous candidate on the build machine only. If the test project references a
fixed library version instead of `*`, the build stage must install exactly that version.

## Preparing the PLC project for TcUnit

* Reference the [TcUnit](https://tcunit.org) library and call `TcUnit.RUN();` cyclically in a program of a task.
* Test suites are function blocks that `EXTENDS TcUnit.FB_TestSuite`; instantiate them in that program.
* Keep `LogExtendedResults` at `TRUE` (TcUnit default) – then the JUnit file has one entry per test instead of only the summary.
* Recommended: a separate PLC project or solution for the tests, so the production PLC never contains test code.

## The build machine

TcAgent drives the real TwinCAT engineering, so the build agent is a Windows machine with:

* Visual Studio 2022 or TcXaeShell with TwinCAT 3.1 XAE, TE1030 license – started once interactively by the user the agent
  runs as (first-start settings).
* For `test`: TwinCAT runtime (XAR) on the same machine or a reachable test PLC, with all licenses the project needs
  activated. Trial licenses must be activated by a person; for permanent CI use real licenses or a license dongle.
* The agent must run **as a normal user account, not as a service account without a desktop** – Visual Studio's
  automation interface needs a user session. For GitHub/Azure/Jenkins agents: install the agent under that user, or start
  it interactively (`run.cmd`) and enable automatic logon on a dedicated machine.
* TcAgent from source: `build.ps1` produces `bin\publish\TcAgent.exe` (including the interop generated from the local
  TwinCAT installation). Copy that folder to the machine, or build there.

### Setting up a GitHub runner in four steps

1. **Install TcAgent** on the machine: the VSIX (double-click), then start Visual Studio 2022 once as the runner user – it
   copies TcAgent and generates the interop for this TwinCAT installation.
2. **Registration token:** GitHub > repository > Settings > Actions > Runners > *New self-hosted runner* (valid for 1 hour).
3. **As the runner user** (not as SYSTEM, not as a service), in the TcAgent folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup-runner.ps1 -RepoUrl https://github.com/<owner>/<repo> -Token <token> `
       -Solution D:\Projects\MyMachine\MyMachine.sln -Start
   ```

   It copies TcAgent to `C:\Tools\TcAgent` (`TcAgent.exe install`), checks the machine (`TcAgent.exe doctor`, with
   `-Solution` also an end-to-end open of the solution), downloads and registers the runner (labels `self-hosted, Windows,
   X64, twincat`), writes `C:\actions-runner\.env` (`TCAGENT`, `TCAGENT_ALLOW_TARGETS`) and creates a scheduled task that
   starts the runner at logon of this user. Options: `-RunnerDir`, `-TcAgentPath`, `-Name`, `-Labels`, `-AllowTargets`,
   `-Force` (set up even if doctor reports problems), `-Uninstall`.
4. **Dedicated build machine / VM / IPC:** enable automatic logon for that user (Sysinternals *Autologon*), so the runner
   comes back after a reboot. Lock the screen if needed – the session stays usable.

Then copy [github-actions.yml](pipeline/github-actions.yml) to `.github/workflows/` of the PLC repository. Its first step
`TcAgent.exe doctor` stops a job at once with a clear message if the runner is not ready.

### Checking a machine: `TcAgent.exe doctor`

```
TcAgent.exe doctor [--solution <sln>] [--target <AmsNetId>] [--progid <ProgID>]
```

| Check | FAIL / WARN when |
|---|---|
| Session | FAIL: runs in session 0 / as SYSTEM (service without desktop) |
| IDE | FAIL: neither `VisualStudio.DTE.17.0` nor the XAE Shell is registered |
| TwinCAT XAE | FAIL: `TCatSysManager.tlb` not found |
| Interop | FAIL: `Interop.TCatSysManagerLib.dll` neither next to TcAgent.exe nor in `%LOCALAPPDATA%\TcAgent\lib` |
| Runtime | WARN: no TwinCAT system service answers via ADS (needed for `test`/`sil`) |
| TcUnit | WARN: not in the library repository |
| git, temp folder | WARN: git missing · FAIL: temp folder not writable |
| Solution (`--solution`) | FAIL: the solution cannot be opened hidden like in a pipeline |

Licenses are only listed (TE1030 for TcAgent, the runtime licenses of the project for `test`/`sil`). Exit code 0 = ready,
3 = not ready.

### A fixed path: `TcAgent.exe install --path <folder>`

The VSIX installs TcAgent per user and per version (`%LOCALAPPDATA%\TcAgent\app\<version>`). `install` copies the running
TcAgent including the interop to a fixed folder – the workflows use `C:\Tools\TcAgent\TcAgent.exe`. Run it again after an
update (`setup-runner.ps1` does it as well).

One TwinCAT runtime can only run one configuration – run test jobs on a machine **one at a time**
(GitHub: one runner per machine, Azure: one agent, Jenkins: one executor).

## Examples

Ready-to-copy definitions in [docs/pipeline/](pipeline/):

| CI system | File |
|---|---|
| GitHub Actions (self-hosted runner) | [github-actions.yml](pipeline/github-actions.yml) – copy to `.github/workflows/` |
| Azure DevOps (self-hosted agent) | [azure-pipelines.yml](pipeline/azure-pipelines.yml) |
| Jenkins | [Jenkinsfile](pipeline/Jenkinsfile) |
| GitLab CI (Windows shell runner) | [gitlab-ci.yml](pipeline/gitlab-ci.yml) – copy to `.gitlab-ci.yml` |
| Library + tests, `check -> build -> sil -> library` (GitHub Actions) | [library-github-actions.yml](pipeline/library-github-actions.yml) |
| Library + tests, `check -> build -> sil -> library` (Azure DevOps) | [library-azure-pipelines.yml](pipeline/library-azure-pipelines.yml) |

Compiler messages appear as annotations directly in the pull request (GitHub Actions and Azure DevOps are detected
automatically), failed tests are listed in the CI's test tab via the JUnit file.

## Locally, before pushing

The same commands work against the Visual Studio you are working in – no second instance, nothing is closed:

```powershell
TcAgent.exe check --attach
TcAgent.exe test  --attach MyMachine --junit tests.xml
```

With `--attach` the I/O devices stay enabled and the boot project is created in your open project (it is not saved
unless you save). Use `--no-boot-project` if your PLC is started differently.

## Troubleshooting

| Symptom | Cause / remedy |
|---|---|
| exit 3 "Could not open the TwinCAT solution" | Visual Studio/XAE not installed for this user, or the agent runs without a desktop session. Try `--visible` interactively. |
| exit 3 "target system … is not allowed" | the target is not in `TCAGENT_ALLOW_TARGETS` – set it in the pipeline's environment. |
| exit 2 "REFUSED: … not allowed" (sil/hil) | the target is neither in `TCAGENT_ALLOW_TARGETS` nor passed with `--allow` (hil), or `TCAGENT_MODE` is `ask`/`propose`. |
| exit 3 "… answers the broadcast search but not via ADS" | the ADS route from the build machine to the test PLC is missing – add it once (TwinCAT router "Add Route…"). |
| exit 3 "No TwinCAT runtime answers on this PC" | XAR not installed / system service stopped, or the usermode runtime is not started (`--target <its NetId>`). |
| exit 3 "did not reach Run mode" | licenses on the target, real-time (Hyper-V/VBS, isolated cores), or configuration errors – see the IDE dialogs in the log. |
| exit 3 "Activation failed" | missing licenses on the target, runtime not installed, or Hyper-V/VBS blocking the real-time. |
| exit 3 "no TcUnit output" | `TcUnit.RUN()` is not called, the task is not running, or the PLC did not start (boot project). |
| JUnit has only one entry | `LogExtendedResults` is `FALSE` in the TcUnit parameters. |
| "IDE DIALOG …" in the log | TwinCAT opened a dialog (error, license, question). It blocks the IDE, so TcAgent closes it like Cancel/X and reports it in the log, as CI warning and as suite "TwinCAT IDE dialogs" in the JUnit file. If TcUnit does not start within 30 s after an error dialog, the run stops with exit 3. `--keep-dialogs` leaves them open (debugging with `--visible`). |
| hangs after "Starting Visual Studio" | a modal dialog in the hidden IDE (first start, update notice) – run once with `--visible`, close the dialog. |
