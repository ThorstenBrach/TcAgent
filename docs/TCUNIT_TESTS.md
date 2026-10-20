# TcUnit test window

> **Status: planned, not yet in TcAgent 0.27.0.** This page describes the feature as it is designed
> ([concept](https://claude.ai/code/artifact/c7d0d503-59d5-48b9-8d90-128e9a80b479)). How to *write* tests for it
> (section 3) already applies today: tests written this way run unchanged with `TcUnit.RUN()`, `sil` and `hil`.

The window **TcUnit Tests** shows the TcUnit tests of the open solution like the Test Explorer of Visual Studio:
a tree of PLC projects, suites and tests, run buttons, red/green results with the failure message, and a progress
bar while a run is going. It works in Visual Studio 2022 and in the TwinCAT XAE Shell (which has no Test Explorer).

---

## 1. Opening the window

- **View > Other Windows > TcUnit Tests**, or the test button in the toolbar of the TcAgent Chat window.
- The window reads the tests from the code of the open solution. It needs the VS extension TcAgentBridge
  ([INSTALLATION.md](INSTALLATION.md)); nothing has to be registered or listed by hand.
- The tree is read again after every build and whenever a `.TcPOU` is saved: new tests appear, deleted ones go,
  the results of the last run stay with their tests.

## 2. Using it

| Control | What it does |
|---|---|
| **Run all** | Runs every test of the selected PLC project. |
| **Run selected** | Runs only the ticked suites/tests (the others are switched off for this run, see section 4). |
| **Run failed** | Runs the tests that failed in the last run. |
| **Cancel** | Stops the PLC (reset cold); tests that have not finished stay "not run". |
| **Refresh** | Reads the tests from the code again. |
| Search box / status filter | Narrows the tree, e.g. to failed tests or to a name. |
| Double-click a test | Opens its method in the TwinCAT editor. |

Status symbols: not run, running, passed, failed, skipped (switched off for this run). Counters at the top show
passed / failed / skipped / not run; each suite shows its count and duration.

The **details pane** shows for the selected test its description (the `///` comment, section 3), the failure
message (`EXP`, `ACT`, `MSG` from TcUnit), the duration and a link to the code.

The **progress bar** shows "Test 12 of 87: <name>" and the estimated remaining time. A test turns green as soon as
the next one starts and no "FAILED TEST" message for it is in the log; it turns red as soon as such a message comes.
The final TcUnit summary ("TESTS FINISHED RUNNING") confirms all results at the end.

## 3. Writing tests for the window

The AI agent writes tests this way on its own (rule 8 in the TcAgent skill / agent guide). For tests written by hand:

```
/// Motor block: start, stop and fault handling
FUNCTION_BLOCK FB_Motor_Test EXTENDS TcUnit.FB_TestSuite
VAR
    bRunSuite                        : BOOL := TRUE;
    bRun_WhenStartedExpectRunning    : BOOL := TRUE;
    bRun_WhenStoppedExpectStandstill : BOOL := TRUE;
END_VAR

IF bRunSuite THEN
    IF bRun_WhenStartedExpectRunning THEN WhenStartedExpectRunning(); END_IF
    IF bRun_WhenStoppedExpectStandstill THEN WhenStoppedExpectStandstill(); END_IF
END_IF
```

```
/// The motor reports Running one cycle after Start
METHOD WhenStartedExpectRunning
TEST('WhenStartedExpectRunning');
...
TEST_FINISHED();
```

- **One test = one method**, called from the suite body; the name in `TEST('...')` is the method name.
- **Enable bools** with exactly these names and the initial value `TRUE`: `bRunSuite` per suite,
  `bRun_<TestMethod>` per test. Never set them to `FALSE` in code. Without them a suite still works, but it can
  only run as a whole ("whole suite only" in the window).
- Inline tests (`TEST('X') ... TEST_FINISHED();` directly in the body) get the same `IF bRun_X THEN ... END_IF`.
- **Descriptions:** a `///` comment above every test method and above every suite instance in the test program,
  e.g. `/// UT-GPIB: GPIB commands and GpibSerialFB (without hardware)`. It is the tooltip and the description in
  the window.
- **Test program:** `TcUnit.RUN_IN_SEQUENCE()` runs the tests one after the other and gives progress and results
  per test. With `TcUnit.RUN()` all suites run at the same time; the window then shows only overall progress and
  the single results at the end.

## 4. What happens during a run

1. **Target:** the local runtime by default. A test PLC in the network only if it is allowed in
   `TCAGENT_ALLOW_TARGETS`; then the window asks once per run.
2. **Build** if the code changed since the last run.
3. **Run mode:** if TwinCAT is in Config mode, the configuration is activated and TwinCAT restarts in Run mode.
   It stays in Run mode afterwards.
4. **Login and download** if the code is new, otherwise only login. Then **reset cold**: TcUnit starts from zero,
   all enable bools are `TRUE` again, the PLC is in Stop.
5. **Selection:** TcAgent writes *all* enable bools via ADS – `TRUE` for the selected tests, `FALSE` for the rest.
6. **Start** of the PLC; the window follows the progress (section 5).
7. **End:** after "TESTS FINISHED RUNNING" TcAgent makes a **reset cold** again, so the next run can start right away.

Clicking a run button is the confirmation for the whole local run; there is no dialog per step.

## 5. Configuration

Optional, in `<solution folder>\.tcagent\tcunit.json` (versioned in Git with the project):

```json
{
  "plcProject": "TIPC^Tests^Tests Project",
  "target": "local",
  "timeout": "00:05:00",
  "progress": "GVL_Progress.Progress"
}
```

| Key | Default | Meaning |
|---|---|---|
| `plcProject` | the only PLC project with TcUnit suites | PLC project the window runs (tree path) |
| `target` | `local` | `local` or the AmsNetId of a test PLC (must be in `TCAGENT_ALLOW_TARGETS`) |
| `timeout` | 5 minutes | maximum duration of a run; then the PLC is stopped and open tests stay "not run" |
| `progress` | empty | ADS path of a progress block, see below |

**Progress block (optional):** if the test project has a block like `TestProgressFB` in the SRCI tests
(`GVL_Progress.Progress` with the outputs `Total`, `Current`, `Done`, `Test`, `Remaining`, `Finished`), TcAgent reads
it about every 500 ms and shows its values. Without it TcAgent takes the running test from TcUnit itself and the
number of tests from the code.

## 6. Without the window

The same run is available on the command line and to the AI agent:

- `TcAgent.exe tcunit list --solution <sln>` – tests with file, line and description as JSON.
- `TcAgent.exe tcunit run --solution <sln> [--tests <names>] [--target local|<AmsNetId>]` – the run of section 4,
  result as JSON and optionally JUnit.
- In the chat: *"Run only the tests of FB_Motor_Test"* (tool `tc_test_run`).

Pipelines keep using `sil` / `hil` ([PIPELINE.md](PIPELINE.md)); there nobody switches tests off, so all tests run.
