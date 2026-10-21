# TcUnit test window

The window **TcUnit Tests** shows the TcUnit tests of the open solution like the Test Explorer of Visual Studio:
a tree of PLC projects, suites and tests, run buttons, red/green results with the failure message, and a progress
bar while a run is going. It works in Visual Studio 2022 and in the TwinCAT XAE Shell (which has no Test Explorer).
The same run is available to the AI agent (tool `tc_test_run`) and on the command line (`TcAgent.exe tcunit`).

---

## 1. Opening the window

- **View > Other Windows > TcUnit Tests**, or the **Tests** button in the toolbar of the TcAgent Chat window.
- The window reads the tests from the code of the open solution. It needs the VS extension TcAgentBridge
  ([INSTALLATION.md](INSTALLATION.md)); nothing has to be registered or listed by hand.
- The tree is read again after every build and whenever a `.TcPOU`, `.TcGVL`, `.plcproj` or `.tcagent\tcunit.json`
  of the solution is saved: new tests appear, deleted ones go, the results of the last run stay with their tests.

## 2. Using it

| Control | What it does |
|---|---|
| **Run all** | Runs every test of the PLC project. |
| **Run selected** | Runs the ticked suites/tests – or, if nothing is ticked, the selected one. The others are switched off for this run (section 4). A suite with only some tests ticked shows a partly filled box: only the ticked tests run. |
| **Run failed** | Runs the tests that failed in the last run. |
| **Cancel** | Stops the run: reset of the PLC; tests that have not finished stay "not run". |
| **Refresh** | Reads the tests from the code again. |
| **Report** | HTML test report of the last run, opened in the browser (section 5, key `report`). |
| Search box / status filter | Narrows the tree, e.g. to failed tests or to a name (also suite names and descriptions). |
| Double-click a test, **Open code** | Opens the test method in the TwinCAT editor at its `TEST(...)` line. |

Status symbols: ○ not run, ◐ running, ✔ passed, ✖ failed, ⊘ skipped (switched off for this run, or skipped by TcUnit).
The counters at the top show passed / failed / skipped / not run; on the right the number of ticked tests and, while a
run is going, "4 of 6 done (67 %)". Each suite shows its number of tests, failures and duration. A test whose checkbox is greyed out has no `bRun_` bool: it can only run with its whole suite.

The **details pane** shows for the selected test its description (the `///` comment, section 3), the status and
duration, the failure message (`EXP`, `ACT`, `MSG` from TcUnit), the file and line and the path of its enable bool.

The **progress bar** shows "Test 12 of 87: <name>" and the estimated remaining time. TcAgent reads TcUnit's own test
table in the PLC about every 300 ms: a test turns red as soon as one of its assertions fails and green when TcUnit marks
it finished. After TcUnit has printed its results, the failure texts with `EXP`/`ACT` are taken from the runtime
messages.

Inside one suite TcUnit calls all `TEST()` tests in the same PLC cycles (only `TEST_ORDERED()` tests run one after the
other), so the name in the progress bar is the test TcUnit called last; the results per test are exact anyway. With
`TcUnit.RUN()` (all suites at the same time) the bar shows only "n of N tests finished".

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
  `bRun_<TestMethod>` per test. Never set them to `FALSE` in code and do not declare them `RETAIN`/`PERSISTENT`.
  Without them a suite still works, but it can only run as a whole ("whole suite only" in the window); a suite
  without `bRunSuite` always runs.
- Inline tests (`TEST('X') ... TEST_FINISHED();` directly in the body) get the same `IF bRun_X THEN ... END_IF`.
- **Descriptions:** a `///` comment above every test method and above every suite instance in the test program,
  e.g. `/// UT-GPIB: GPIB commands and GpibSerialFB (without hardware)`. It is the tooltip and the description in
  the window.
- **Test program:** `TcUnit.RUN_IN_SEQUENCE()` runs the suites one after the other and gives progress per test.
  With `TcUnit.RUN()` all suites run at the same time; the window then shows only the overall progress.
- Suites may extend other suites (`EXTENDS FB_MyBaseTestSuite`); their tests and bools count for the derived suite.
  Instances are found in `PROGRAM`s and GVLs, also as `ARRAY[..] OF` a suite; instances inside other function blocks
  are not listed.
- Test names that are not a literal (`TEST(sName)`) are known only at run time: such a suite is listed with the tests
  it has in literals; the others appear after the first run, and the suite runs as a whole.

## 4. What happens during a run

1. **Target:** the local runtime by default. A test PLC in the network only if `target` in `.tcagent\tcunit.json`
   names it and it is allowed in `TCAGENT_ALLOW_TARGETS` (for the window: a Windows environment variable of the
   user, Visual Studio passes it on); then the window asks once per run.
2. **Save all**, then TcAgent compares the code with what the runtime runs (the project files and the build time of
   the PLC program in the runtime).
3. **Only if it changed** (or TwinCAT is in Config mode, or the runtime runs another program): **build**, boot
   project, **activate** and restart TwinCAT in Run mode. TwinCAT stays in Run mode afterwards. Otherwise the run starts
   right away.
4. **Reset** of the PLC via ADS: the PLC stops, TcUnit starts from zero, all enable bools are `TRUE` again.
5. **Selection:** TcAgent writes *all* enable bools via ADS – `TRUE` for the selected tests, `FALSE` for the rest.
6. **Start** of the PLC via ADS; the window follows the progress (section 2).
7. **End:** after TcUnit has printed its results TcAgent makes a **reset** again, so the next run can start right away.

Clicking a run button is the confirmation for the whole local run; there is no dialog per step. TcAgent does not log
in with the IDE (online commands of the IDE ask questions in dialogs); start, stop and the bools go via ADS.
Every run is recorded in the audit log (`tc_policy` shows where).

## 5. Configuration

Optional, in `<solution folder>\.tcagent\tcunit.json` (versioned in Git with the project):

```json
{
  "plcProject": "Tests",
  "target": "local",
  "timeout": "00:05:00",
  "progress": "GVL_Progress.Progress",
  "report": "python ..\\..\\SRCI\\SRCI_TEST_CASES\\tools\\report.py --tcunit-xml {junit} --name TwinCAT -o {html}"
}
```

| Key | Default | Meaning |
|---|---|---|
| `plcProject` | the only PLC project with TcUnit suites | PLC project the window runs (name or tree path `TIPC^Tests^Tests Project`) |
| `target` | `local` | `local` or the AmsNetId of a test PLC (must be in `TCAGENT_ALLOW_TARGETS`) |
| `timeout` | 5 minutes | maximum duration of a run (`"hh:mm:ss"` or seconds); then the PLC is reset and open tests stay "not run" |
| `progress` | empty | ADS path of a progress block, see below |
| `report` | TcAgent's own report | command line that makes the HTML report (button **Report**, `tcunit report`), run in the solution folder; `{junit}` = results of the last run (JUnit XML), `{html}` = file to write, also `{solution}`, `{solutionDir}` |

**Progress block (optional):** if the test project has a block like `TestProgressFB` in the SRCI tests
(`GVL_Progress.Progress` with the outputs `Total`, `Current`, `Done`, `Test`, `Remaining`, `Finished`), TcAgent reads
it while the run goes and the progress bar shows its values. Without it TcAgent takes the running test from TcUnit
itself and the number of tests from the code.

## 6. Without the window

The same run is available on the command line and to the AI agent:

- `TcAgent.exe tcunit list --solution <sln> [--plc <name>]` – the tests from the code as JSON: projects, suites,
  instances, tests with file, line, description and the paths of the enable bools. No IDE needed.
- `TcAgent.exe tcunit run --solution <sln> | --attach [pid] [--tests <a,b>] [--plc <name>] [--target local|<AmsNetId>]
  [--timeout <s>] [--junit <file>] [--no-build] [--download]` – the run of section 4. `--tests` takes instances
  (`MAIN.fbMotor`), suite types (`FB_Motor_Test`), single tests (`MAIN.fbMotor@WhenStartedExpectRunning`,
  `FB_Motor_Test.WhenStartedExpectRunning` or just the test name); default all. The output is one JSON event per
  line (`plan`, `step`, `suite`, `running`, `result`, `progress`, `done`); a line `cancel` on stdin (or Ctrl+C) stops
  the run. `--download` builds and activates even if the code did not change. Exit code 0 = all passed, 1 = failed
  tests or build errors, 2 = refused target or unknown test names, 3 = infrastructure problem or timeout.
- `TcAgent.exe tcunit report --solution <sln> [--junit <file>] [--out <file.html>]` – the HTML report of the last run
  (every run keeps its results as JUnit XML in `%LOCALAPPDATA%\TcAgent\tcunit\<solution>-<hash>\last.xml`, outside the
  repository): the `report` command of the configuration, or TcAgent's own report (summary, pass rate, every suite with
  its tests, durations and failure texts). No IDE needed.
- In the chat: *"Run only the tests of FB_Motor_Test"* (tool `tc_test_run`, `action=list` lists the tests).

Pipelines keep using `sil` / `hil` ([PIPELINE.md](PIPELINE.md)); there nobody switches tests off, so all tests run.
