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
| `TcAgent.exe doc --solution X.sln --out doc/X.html` | program documentation as HTML (call hierarchy, diagrams, reference; with the explanations stored by the agent) | documentation as build artifact |
| `TcAgent.exe archive --solution X.sln --out X.tszip` | archive of the whole solution | releases, support |
| `TcAgent.exe test --solution X.sln` | builds, activates on the test target, starts the PLC, waits for TcUnit, collects the results | every push to `main`, nightly |
| `TcAgent.exe feedback --open --markdown feedback.md` | Markdown report of the agents' `tc_feedback` entries (grouped by tool, ready for GitHub issues); `--summary` for counts, `--path` for another store; no IDE needed | weekly triage of a shared feedback folder |

```powershell
TcAgent.exe check --solution .\MyMachine.sln --junit results\check.xml
TcAgent.exe test  --solution .\MyMachine.sln --junit results\tests.xml --timeout 900
```

**Exit codes:** `0` OK · `1` compiler errors or failed tests · `2` wrong call · `3` infrastructure problem (IDE could not be
started, activation failed, TcUnit did not finish in time). Pipelines can treat `3` as "retry" and `1` as "red".

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
| exit 3 "Activation failed" | missing licenses on the target, runtime not installed, or Hyper-V/VBS blocking the real-time. |
| exit 3 "no TcUnit output" | `TcUnit.RUN()` is not called, the task is not running, or the PLC did not start (boot project). |
| JUnit has only one entry | `LogExtendedResults` is `FALSE` in the TcUnit parameters. |
| "IDE DIALOG …" in the log | TwinCAT opened a dialog (error, license, question). It blocks the IDE, so TcAgent closes it like Cancel/X and reports it in the log, as CI warning and as suite "TwinCAT IDE dialogs" in the JUnit file. If TcUnit does not start within 30 s after an error dialog, the run stops with exit 3. `--keep-dialogs` leaves them open (debugging with `--visible`). |
| hangs after "Starting Visual Studio" | a modal dialog in the hidden IDE (first start, update notice) – run once with `--visible`, close the dialog. |
