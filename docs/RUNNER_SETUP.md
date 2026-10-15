# Your own TwinCAT build server: a GitHub Actions runner step by step

This guide sets up a **self-hosted GitHub Actions runner** that checks, builds and documents a TwinCAT 3 project on every
push – on your own PC first, later on a VM or a Beckhoff IPC used as build server. It follows the first real setup
(TwinCAT 3.1.4026, Visual Studio 2022, GitHub runner 2.338): about 30 minutes, the first pipeline run takes 5–15 minutes.

Other CI systems (Azure DevOps, Jenkins, GitLab) and all commands are described in [PIPELINE.md](PIPELINE.md).

```
 git push ──► GitHub ──► runner on your PC (as your user, in your desktop session)
                           └─ TcAgent.exe doctor / check / build / doc ──► hidden Visual Studio + TwinCAT XAE
                                                                        ──► results as artifact + test report in GitHub
```

## What you need

| | |
|---|---|
| Machine | Windows 10/11 or Windows Server with **TwinCAT 3.1 XAE** (Visual Studio 2022 or XAE Shell). For `sil` also the TwinCAT runtime (XAR) |
| User | a normal Windows user who is logged on – the TwinCAT automation interface needs a desktop session, so the runner **must not run as a Windows service or as SYSTEM** |
| TcAgent | the VSIX installed and Visual Studio 2022 started once as this user (or a source build, see [INSTALLATION.md](INSTALLATION.md)) |
| Licenses | TE1030 (engineering automation, part of XAE). For tests on the runtime the runtime licenses of the project – trial licenses must be activated by a person every 7 days, a build server needs real licenses or a dongle |
| GitHub | a repository with the TwinCAT project. **Make it private** (see step 2) |
| git | [Git for Windows](https://git-scm.com/download/win) |

One runner per machine: a TwinCAT runtime runs one configuration at a time.

## Step 1 – Is the machine ready? (`doctor`)

Open a command prompt in the folder of your solution and run (the path is the one the VSIX installed; the number is the
TcAgent version):

```
%LOCALAPPDATA%\TcAgent\app\<version>\TcAgent.exe doctor --solution MyMachine.sln
```

`doctor` checks the desktop session, Visual Studio/XAE, the TwinCAT interop, the local runtime, licenses, git and the temp
folder, and with `--solution` it opens the solution **hidden, exactly as the pipeline will** (the first time this takes
1–2 minutes). The end must be `READY.`:

```
OK    Session      desktop session 1
OK    IDE          VisualStudio.DTE.17.0, TcXaeShell.DTE.15.0 (the first one is used; --progid selects another)
OK    TwinCAT XAE  C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\
OK    Interop      C:\Users\...\AppData\Local\TcAgent\lib\Interop.TCatSysManagerLib.dll
OK    Runtime      127.0.0.1.1.1: Config
INFO  Licenses     1 license file(s) in C:\ProgramData\Beckhoff\TwinCAT\3.1\License
OK    git          C:\Program Files\Git\cmd\git.exe
OK    Solution     opened in 80 s, PLC projects: MyMachine
OK    TcUnit       TcUnit, 1.3.2 (library repository, read through the opened solution)

READY.
```

Every `FAIL` line says what to do. If opening hangs, a dialog in the hidden IDE is waiting (first start, license,
update notice): `doctor` stops it after 300 s and names the dialog – open the solution once normally in Visual Studio and
answer it.

## Step 2 – Put the project into a Git repository

Skip this if your project already is in GitHub.

**Private repository!** A self-hosted runner executes whatever a workflow says. On a public repository anyone could open a
pull request whose workflow runs on your PC. GitHub: *New repository* → name → **Private** → no README/.gitignore
(you bring your own).

1. Copy [pipeline/twincat.gitignore](pipeline/twincat.gitignore) as `.gitignore` into the **root of the repository**
   (the folder that will contain `.github`). It keeps out what TwinCAT and Visual Studio generate, NuGet packages of the
   HMI (`Packages/`, restored by Visual Studio), license files and TcAgent's local backups.
2. Copy [pipeline/github-actions.yml](pipeline/github-actions.yml) to `.github\workflows\twincat.yml` and set `SOLUTION`
   to the path of the solution **relative to the repository root**, e.g. `TwinCAT\MyMachine.sln`.
3. Put it under version control:

   ```
   cd /d D:\Projects\MyMachine
   git init -b main
   git add .
   git status --short | find /c /v ""
   ```

   The last line counts the files. Expect the PLC sources (`.TcPOU`, `.TcGVL`, `.TcDUT`, `.plcproj`, `.tsproj`, `.sln`),
   HMI/Scope projects referenced by the solution and the files under `.tcagent` – a few hundred at most. **Thousands** mean
   that packages or generated files slipped through: add the folder to `.gitignore`, then

   ```
   git rm -r -q --cached -f .
   git add .
   ```

   (`--cached` only removes files from the selection, nothing is deleted on disk.)

   Keep every project that the solution references (HMI, Scope, Safety) – the runner works on a fresh checkout, and a
   missing project makes the solution load with errors.
4. Commit and push:

   ```
   git commit -m "PLC project with TwinCAT pipeline"
   git remote add origin https://github.com/<owner>/<repo>.git
   git push -u origin main
   ```

The workflow starts at once and waits as *Queued* – there is no runner yet. That is fine.

## Step 3 – Registration token

GitHub → the repository → **Settings → Actions → Runners → New self-hosted runner** → *Windows*, *x64*.

You do **not** need the download and configure commands on that page – the script does that. You only need the **token**:
in the section *Configure* the line `./config.cmd --url … --token AB12CD…` – copy the long string after `--token`.
It is valid for one hour and works once; reload the page for a new one.

## Step 4 – Set up the runner (`setup-runner.ps1`)

Open **PowerShell as administrator** (so the logon task can be created – the runner itself will run without elevation)
and go to the folder with `setup-runner.ps1` – the TcAgent repository, or the VSIX installation
(`%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*\Extensions\*\tcagent\`):

```powershell
cd D:\Projects\TcAgent
.\setup-runner.ps1 -RepoUrl https://github.com/<owner>/<repo> -Token AB12CD... -Solution D:\Projects\MyMachine\TwinCAT\MyMachine.sln -Start
```

Paste the token directly after `-Token` – **without** angle brackets (`<…>` is a placeholder in this guide; PowerShell
answers it with *"Der Operator "<" ist für zukünftige Versionen reserviert"* / *"The '<' operator is reserved"*).

What the script does:

| Step | Result |
|---|---|
| `TcAgent.exe install --path C:\Tools\TcAgent` | TcAgent at a fixed path the workflows can call (the newest version installed by the VSIX) |
| `TcAgent.exe doctor --solution …` | stops here if the machine is not ready (`-Force` continues anyway) |
| download and register the runner | `C:\actions-runner`, name = computer name, labels `self-hosted, Windows, X64, twincat` |
| `C:\actions-runner\.env` | `TCAGENT=C:\Tools\TcAgent\TcAgent.exe`, `TCAGENT_ALLOW_TARGETS=local` – every job sees them |
| scheduled task *GitHub Actions Runner (TcAgent)* | starts the runner when this user logs on (falls back to the Startup folder without admin rights) |
| `-Start` | starts the runner now in its own window |

Expected end of the output:

```
√ Runner successfully added
== Environment for the jobs (C:\actions-runner\.env)
  TCAGENT=C:\Tools\TcAgent\TcAgent.exe
  TCAGENT_ALLOW_TARGETS=local
== Autostart at logon of MYPC\me
Scheduled task 'GitHub Actions Runner (TcAgent)' starts the runner when MYPC\me logs on.
Done. Runner 'MYPC' with labels: self-hosted, Windows, X64, twincat
```

Options: `-RunnerDir`, `-TcAgentPath`, `-Name`, `-Labels` (more labels, comma separated), `-AllowTargets` (AMS Net IDs the
pipeline may activate, default `local`), `-TcAgentExe` (a specific TcAgent.exe), `-Force`, `-Uninstall`.

## Step 5 – The first run

The runner window shows

```
√ Connected to GitHub
Listening for Jobs
Running job: check
```

and picks up the waiting run. Follow it in GitHub under **Actions → TwinCAT → the run → check**: every step with its log
and duration. Each TcAgent step opens the solution in its own hidden Visual Studio (about 1–2 minutes each), so the
template job (doctor, check, build, documentation) takes **5–10 minutes**. At the end: green check mark, the compiler
results as test report, and the artifact `twincat-results` with the JUnit files and the HTML documentation with the risk
list.

**Do not click into the runner window.** In a console window a click starts *Select* mode (the title shows
*Auswählen*/*Select*) and Windows pauses the program as soon as it writes – the job seems to hang. Press **Esc** to
continue. The window started by `setup-runner.ps1` (`run-tcagent.ps1`) has QuickEdit switched off, so a click cannot pause
it; a window started with `run.cmd` directly still can.

## Step 6 – Run it like in operation

`-Start` started the runner from the administrator PowerShell, so this first run was **elevated**. In operation the runner
runs as a normal user. Test that once:

1. Close the runner window.
2. Log off and on again (this also tests the autostart) – or right-click `C:\actions-runner\run-tcagent.ps1` →
   *Run with PowerShell*.
3. GitHub → Actions → TwinCAT → **Run workflow**. It must be green as well.

## Dedicated build server (VM or Beckhoff IPC)

Same steps on the build machine, plus:

* **Automatic logon** for the runner user (Sysinternals *Autologon*), so the runner is back after a reboot or a power cut.
  Lock the screen instead of logging off – logging off stops the runner.
* Switch off sleep and hibernation; Windows updates outside working hours.
* Real licenses or a license dongle – trial licenses need a person every 7 days.
* For tests on a separate PLC: add the ADS route from the build machine once and allow the target:
  `-AllowTargets "local,5.80.201.232.1.1"` (see *HIL* in [PIPELINE.md](PIPELINE.md)).
* Update TcAgent: install the new VSIX, start Visual Studio once, then `TcAgent.exe install --path C:\Tools\TcAgent` – or
  run `setup-runner.ps1` again (it skips the registration of an already registered runner).

## More in the pipeline

The template is a start; add steps as needed (all in [PIPELINE.md](PIPELINE.md)):

| Step | Command |
|---|---|
| TcUnit tests on the local runtime (SIL) | `TcAgent.exe sil --solution … --junit results/sil.xml` |
| tests on a real PLC (HIL) | `TcAgent.exe hil --target <AmsNetId> --allow <AmsNetId> …` |
| quality gate on risks | `TcAgent.exe doc --solution … --risks --fail-on high` |
| static analysis, coding rules | `TcAgent.exe analyze --solution … --sarif results/analysis.sarif` |
| library versions as locked | `TcAgent.exe lock --solution … --check` |
| SBOM (CycloneDX) | `TcAgent.exe sbom --solution … --out results/sbom.cdx.json` |
| all steps from `.tcagent/pipeline.json` | `TcAgent.exe ci` (template [github-actions-ci.yml](pipeline/github-actions-ci.yml)) |

## Troubleshooting

| Symptom | Cause / remedy |
|---|---|
| `Error: Solution not found: X.sln` | file name differs from the folder name (e.g. folder `MyMachine_2`, solution `MyMachine2.sln`) – the message lists the `.sln` files in that folder |
| `Der Operator "<" ist für zukünftige Versionen reserviert` | `<TOKEN>` typed literally – paste the token itself |
| `git rm`: *staged content different from both the file and the HEAD* | nothing committed yet – add `-f` (only removes from the selection) |
| thousands of files in `git status` | HMI NuGet packages (`Packages/`) or generated files – use the `.gitignore` template; patterns need `**/` to match in subfolders |
| runner registration fails | token older than one hour or already used – reload the GitHub page |
| run stays *Queued* | runner not started, or the workflow's `runs-on` labels do not match (`[self-hosted, windows, twincat]`) |
| job seems to hang, runner window title shows *Select*/*Auswählen* | a click into the console window paused it – press Esc; start the runner with `run-tcagent.ps1` (QuickEdit off) |
| `doctor` FAIL Session | the runner runs as a service or under SYSTEM – uninstall the service, use `setup-runner.ps1` |
| `doctor` FAIL Interop | Visual Studio 2022 was not started once after installing the VSIX as this user |
| step stops after 300 s with "IDE DIALOG …" | a dialog in the hidden IDE – open the solution once normally and answer it |
| doctor shows an old TcAgent version | `setup-runner.ps1` and `install` take the newest version in `%LOCALAPPDATA%\TcAgent\app` – start Visual Studio once after a VSIX update |

Remove the runner: `.\setup-runner.ps1 -Uninstall -Token XY12...` with a removal token (token from *Settings → Actions → Runners →
the runner → Remove*).
