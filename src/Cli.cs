// TcAgent command line for CI pipelines - the same TcAgent.exe, without an AI:
//
//   TcAgent.exe check --solution <sln>             "Check all objects": compiler errors, no code generated
//   TcAgent.exe build --solution <sln>             build the solution
//   TcAgent.exe test  --solution <sln>             build, activate on the test target, run TcUnit, collect the results
//   TcAgent.exe sil   --solution <sln>             like test, on the runtime of this PC, with clean-up (CliTestRun.cs)
//   TcAgent.exe hil   --solution <sln> --target <AmsNetId> --allow <AmsNetId>   like sil, on an explicitly allowed remote PLC
//   TcAgent.exe sbom  --solution <sln> [--out <file.cdx.json>]   CycloneDX SBOM of the library references (CliSbom.cs)
//   TcAgent.exe lock  --solution <sln> [--check]   library lock file .tcagent\libraries.lock.json / compare with it (CliSbom.cs)
//   TcAgent.exe feedback [--open] [--markdown f]   report of the tc_feedback entries (Markdown for GitHub issues) - no IDE needed
//   TcAgent.exe doctor [--solution <sln>]           is this machine/user ready for pipelines? (CliDoctor.cs)
//   TcAgent.exe diag                                versions and environment for issue reports, as a Markdown list (CliDiag.cs)
//   TcAgent.exe install --path <folder>             copy TcAgent to a fixed folder for pipelines, e.g. C:\Tools\TcAgent
//   TcAgent.exe ci [--config <file>] [--dry-run]    run the steps of .tcagent\pipeline.json of the repository (CliCi.cs)
//   TcAgent.exe st export|check|import --solution <sln>   PLC code as plain-text .st files and back - no IDE (CliPlainText.cs)
//   TcAgent.exe tcunit list|run --solution <sln>   TcUnit tests from the code / run a selection with live progress (CliTcUnit.cs)
//
// Exit codes: 0 = OK, 1 = compiler errors / failed tests, 2 = wrong call, 3 = infrastructure problem (IDE, activation, timeout).
// Without arguments TcAgent.exe is the MCP server as before.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public static partial class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2, Infra = 3;

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "check" or "build" or "test" or "sil" or "hil" or "analyze" or "library" or "archive" or "scope" or "doc" or "feedback" or "help" or "--help" or "-h" or "/?" or "version" or "--version" or "doctor" or "install" or "sbom" or "lock" or "ci" or "st" or "tcunit" or "skills" or "sources" or "knowledge" or "diag" || args.Length > 0 && args[0] == "tcpkg-elevated";

    sealed class Options
    {
        public string Command, Solution, Attach, Junit, Target, ProgId, Out, Sarif, Settings, ScopePath, FailOn;
        public int Seconds = 10;
        public bool Install, Risks, CheckLock;
        public readonly List<string> Plc = new();
        public readonly List<string> Allow = new();
        public bool KeepRunning;
        public bool Visible, KeepOpen, Rebuild, CheckAll, WarningsAsErrors, NoBuild, NoBootProject;
        public bool? DisableIo;
        public bool CloseDialogs = true;
        public int TimeoutSec = 1800, LoadTimeoutSec = 300;
        public string Config, Only, From; public bool DryRun, Init;      // ci (CliCi.cs); From/DryRun also st import
        public string Sub; public bool Clean, Force;                      // st export|check|import (CliPlainText.cs)
        public readonly List<string> Tests = new(); public bool TimeoutGiven, Download, BuiltIn;   // tcunit run/report (CliTcUnit.cs)
        public string Approve;                                                                      // tcunit report --approve <id>
        public string Key, Level, Action, Message;                                                  // knowledge ... --key <kind/name> --level <level> --action <git action> --message <text>
        public string Name, Url, Ref, Item, Kind, Query, Libraries, Terms; public bool AcceptLicense;  // sources / knowledge platform (window)

    }

    public static int Run(string[] args)
    {
        // works on the feedback files only - no solution, no IDE
        if (args[0].Equals("feedback", StringComparison.OrdinalIgnoreCase)) return Feedback.RunCli(args);
        // the elevated part of tc_packages_change (Packages.cs), started by TcAgent itself through the UAC prompt
        if (args[0].Equals("tcpkg-elevated", StringComparison.Ordinal)) return ToolRegistry.RunElevatedTcpkg(args);
        Options o;
        try { o = ParseArgs(args); }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Usage; }
        if (o.Command is "help") { Console.WriteLine(Help); return Ok; }
        if (o.Command is "version") { Console.WriteLine($"TcAgent {Program.ServerVersion}"); return Ok; }
        if (o.Command is "diag") return Diag(o);                // CliDiag.cs - versions for issue reports, no IDE needed
        if (o.Command is "install") return Install(o);          // CliDoctor.cs - no IDE needed
        if (o.Command is "doctor") { Program.ClientName = "cli:doctor"; return Doctor(o); }
        if (o.Command is "ci") { Program.ClientName = "cli:ci"; return Ci(o); }   // CliCi.cs - every step is its own TcAgent.exe
        if (o.Command is "st") { Program.ClientName = "cli:st"; return PlainTextCli(o); }   // CliPlainText.cs - project files only, no IDE
        if (o.Command is "tcunit" && o.Sub == "list") { Program.ClientName = "cli:tcunit"; return TcUnitList(o); }   // CliTcUnit.cs - project files only
        if (o.Command is "tcunit" && o.Sub == "report") { Program.ClientName = "cli:tcunit"; return TcUnitReportCli(o); }
        if (o.Command is "skills") { Program.ClientName = "cli:skills"; return SkillsCli(o); }   // files only, no IDE
        if (o.Command is "sources") { Program.ClientName = "cli:sources"; return SourcesCli(o); }   // files and git only, no IDE
        if (o.Command is "knowledge") { Program.ClientName = "cli:knowledge"; return KnowledgeCli(o); }   // files only, no IDE (knowledge manager window)

        Program.ClientName = "cli:" + o.Command;

        // an explicit --target that is not allowed: refuse before the IDE is started (the same check runs again later)
        if (o.Command is "test" or "sil" or "hil" or "tcunit" && !string.IsNullOrWhiteSpace(o.Target) && !o.Target.Trim().Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            try { new Policy(o.Command == "hil" ? o.Allow : Enumerable.Empty<string>()).Check("tc_activate", () => o.Target.Trim()); }
            catch (Exception ex) { return Refused(o, "target", ex.Message + " (pass --target or set TCAGENT_ALLOW_TARGETS for the CI machine)"); }
        }

        var sta = new StaWorker();
        var tc = new TwinCatSession(sta);
        bool ownIde = false;
        DialogGuard guard = null;
        var total = Stopwatch.StartNew();
        try
        {
            // ---- connect
            try
            {
                if (o.Attach != null)
                {
                    int.TryParse(o.Attach, out var pid);
                    Say("Attaching to running IDE " + (o.Attach.Length > 0 ? o.Attach : "(the only one)"));
                    Say(sta.Run(() => tc.Attach(pid > 0 ? null : (o.Attach.Length > 0 ? o.Attach : null), pid)));
                }
                else
                {
                    var sln = Path.GetFullPath(o.Solution);
                    Say($"Starting Visual Studio ({(o.Visible ? "visible" : "hidden")}) with {sln}");
                    using var load = new LoadGuard(o.CloseDialogs);
                    try
                    {
                        sta.Run(() => tc.Open(sln, o.Visible, o.ProgId), o.LoadTimeoutSec * 1000);
                        ownIde = true;
                        sta.Run(() => { tc.WaitForTwinCat(o.LoadTimeoutSec); return 0; }, (o.LoadTimeoutSec + 30) * 1000);
                    }
                    catch (Exception ex)
                    {
                        var dlg = load.DialogText();
                        var killed = ownIde ? new List<int>() : load.KillStarted();
                        throw new Exception(Msg(ex) + (dlg != null ? " - IDE dialog(s) while loading: " + dlg : " - no IDE dialog was seen") +
                                            (killed.Count > 0 ? $" - the hidden IDE (pid {string.Join(", ", killed)}) was ended" : ""));
                    }
                    Say(tc.InstanceInfo);
                }
                sta.Run(() => { tc.RequireSysMan(); ShowAllMessages(tc); return 0; });
                // tcunit run from the TcUnit Tests window: the IDE is the user's own - report its dialogs, never close them
                guard = new DialogGuard(tc.AttachedPid, o.CloseDialogs && !(o.Command == "tcunit" && o.Attach != null));
            }
            catch (Exception ex) { return InfraError(o, "connect", "Could not open the TwinCAT solution: " + Msg(ex)); }

            Dialogs = guard;
            return o.Command switch
            {
                "check" => Check(o, tc),
                "analyze" => Analyze(o, tc),
                "library" => SaveLibrary(o, tc),
                "archive" => Archive(o, tc),
                "scope" => ScopeRecord(o, tc),
                "doc" => Doc(o, tc),
                "sbom" => Sbom(o, tc),
                "lock" => Lock(o, tc),
                "build" => Build(o, tc),
                "test" => Test(o, tc, ownIde),
                "sil" => TestRun(o, tc, ownIde, hil: false),
                "hil" => TestRun(o, tc, ownIde, hil: true),
                "tcunit" => TcUnitCli(o, tc),
                _ => Usage
            };
        }
        catch (Exception ex) { return InfraError(o, o.Command, Msg(ex)); }
        finally
        {
            guard?.Dispose();
            if (ownIde && !o.KeepOpen)
            {
                Say("Closing Visual Studio (nothing is saved)");
                try { sta.Run(() => { tc.CloseIde(); return 0; }, 60_000); } catch (Exception ex) { Say("Close failed: " + Msg(ex)); }
            }
            Say(System.FormattableString.Invariant($"Total {total.Elapsed.TotalSeconds:0.0} s"));
            sta.Dispose();
        }
    }

    // ------------------------------------------------------------------ analyze / library / archive
    static List<TCatSysManagerLib.ITcSmTreeItem> SelectedPlcs(Options o, TwinCatSession tc)
    {
        var all = tc.PlcProjects();
        var sel = o.Plc.Count == 0 ? all : all.Where(p => o.Plc.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                                                        p.PathName.Split('^').Skip(1).FirstOrDefault()?.Equals(n, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        if (sel.Count == 0) throw new Exception("No (matching) PLC project" + (o.Plc.Count > 0 ? " for --plc " + string.Join(",", o.Plc) : "") + ".");
        return sel;
    }

    /// <summary>Static analysis (TE1200): results like compiler messages, optionally SARIF (4026.20+).</summary>
    static int Analyze(Options o, TwinCatSession tc)
    {
        var sw = Stopwatch.StartNew();
        var (compile, names) = tc.Sta.Run(() =>
        {
            var plcs = SelectedPlcs(o, tc);
            foreach (var plc in plcs)
            {
                if (o.Settings != null) ComCall.Invoke(plc, "LoadStaticAnalysisSettings", Path.GetFullPath(o.Settings));
                if (o.Sarif != null)
                {
                    var f = plcs.Count == 1 ? Path.GetFullPath(o.Sarif)
                          : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(o.Sarif) ?? "", $"{Path.GetFileNameWithoutExtension(o.Sarif)}.{plc.Name}{Path.GetExtension(o.Sarif)}"));
                    Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                    ComCall.Invoke(plc, "ExportToSarif", f);
                    Say("SARIF: " + f);
                }
                else ComCall.Invoke(plc, "RunStaticAnalysis", false);
            }
            return (tc.ReadErrorList(5000).compile, plcs.Select(p => p.Name).ToList());
        }, o.TimeoutSec * 1000);
        return ReportCompile(o, "analyze", string.Join(", ", names), compile, sw.Elapsed.TotalSeconds);
    }

    /// <summary>"Solution not found" with the .sln files that exist in that folder (typical: folder MyMachine_2, file MyMachine2.sln).</summary>
    static string SolutionNotFound(string path)
    {
        var msg = "Solution not found: " + path;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir != null && Directory.Exists(dir))
            {
                var slns = Directory.GetFiles(dir, "*.sln").Select(Path.GetFileName).Take(5).ToList();
                if (slns.Count > 0) msg += " - in that folder: " + string.Join(", ", slns);
            }
        }
        catch { }
        return msg;
    }

    /// <summary>Saves the PLC project as .library (artifact), optionally installs it.</summary>
    static int SaveLibrary(Options o, TwinCatSession tc)
    {
        if (!o.NoBuild)
        {
            var (failed, compile) = tc.Sta.Run(() => BuildSolution(tc, o), o.TimeoutSec * 1000);
            if (failed > 0 || compile.Any(e => e.Level == "ERROR"))
                return ReportCompile(o, "library", "build", compile, 0);
        }
        var file = Path.GetFullPath(o.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        tc.Sta.Run(() =>
        {
            var plcs = SelectedPlcs(o, tc);
            if (plcs.Count > 1) throw new Exception("Several PLC projects - choose one with --plc.");
            ComCall.Invoke(plcs[0], "SaveAsLibrary", file, o.Install);
            return 0;
        }, o.TimeoutSec * 1000);
        if (!File.Exists(file)) { Say("LIBRARY FAILED: no file written"); return Failed; }
        Say($"LIBRARY OK: {file} ({new FileInfo(file).Length / 1024} KB){(o.Install ? ", installed" : "")}");
        return Ok;
    }

    /// <summary>Records with a Scope configuration of the solution for N seconds and exports the data (e.g. CSV as artifact).
    /// The PLC must be running (e.g. after "test" or an activation).</summary>
    static int ScopeRecord(Options o, TwinCatSession tc)
    {
        var tools = new ToolRegistry(tc);
        string Run(string action, JsonObject extra = null)
        {
            var args = new JsonObject { ["action"] = action };
            if (o.ScopePath != null) args["path"] = o.ScopePath;
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value?.DeepClone();
            var r = tools.Call("tc_scope", args);
            var text = r["content"]?[0]?["text"]?.ToString() ?? "";
            if (r["isError"]?.GetValue<bool>() == true) throw new Exception(text);
            return text;
        }
        Say(Run("record_start"));
        Say($"recording {o.Seconds} s ...");
        Thread.Sleep(TimeSpan.FromSeconds(o.Seconds));
        Say(Run("record_stop"));
        var file = Path.GetFullPath(o.Out);
        Say(Run("export", new JsonObject { ["file"] = file }));
        for (int i = 0; i < 60 && !File.Exists(file); i++) Thread.Sleep(500);   // the Scope may write the export asynchronously
        if (!File.Exists(file)) { Say("SCOPE FAILED: no file written"); return Failed; }
        Say($"SCOPE OK: {file}");
        return Ok;
    }

    static int Archive(Options o, TwinCatSession tc)
    {
        var file = Path.GetFullPath(o.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        tc.Sta.Run(() => { ComCall.Invoke(tc.RequireSysMan(), "SaveAsArchive", file); return 0; }, o.TimeoutSec * 1000);
        if (!File.Exists(file)) { Say("ARCHIVE FAILED: no file written"); return Failed; }
        Say($"ARCHIVE OK: {file} ({new FileInfo(file).Length / 1024} KB)");
        return Ok;
    }

    /// <summary>Program documentation (HTML) of each PLC project, with the explanations stored by the agent (tc_doc).</summary>
    static int Doc(Options o, TwinCatSession tc)
    {
        var sln = SolutionName(tc);
        var dir = Path.GetDirectoryName(sln);
        var mem = PlcDoc.Summaries.Load(dir);
        var docs = tc.Sta.Run(() => SelectedPlcs(o, tc).Select(plc =>
        {
            var d = PlcDoc.Collect(tc, plc, ToolRegistry.SfcForDoc);
            d.Analyze();
            return d;
        }).ToList(), o.TimeoutSec * 1000);
        var riskConfig = RiskConfig.Load(dir);
        foreach (var d in docs) d.RiskConfig = riskConfig;
        foreach (var e in riskConfig.Errors) Say("RISK RULES ERROR: " + e);
        foreach (var d in docs)
        {
            var file = o.Out == null ? Path.Combine(dir ?? ".", "doc", d.Project + ".html")
                     : docs.Count == 1 ? o.Out
                     : Path.Combine(Path.GetDirectoryName(o.Out) ?? "", $"{Path.GetFileNameWithoutExtension(o.Out)}.{d.Project}{Path.GetExtension(o.Out)}");
            file = Path.GetFullPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, d.Html(mem, "TcAgent " + Program.ServerVersion), new System.Text.UTF8Encoding(false));
            Say($"DOC OK: {file} ({d.Pous.Count} objects, {d.Findings().Count} findings, risks: {PlcDoc.RiskCounts(d.Risks())})");
        }
        // static risk rules (tc_doc action=risks): list and optionally fail the step
        var failed = false;
        if (o.Risks || o.FailOn != null)
            foreach (var d in docs)
            {
                var rk = d.Risks();
                Say($"RISKS {d.Project}: {PlcDoc.RiskCounts(rk)}");
                foreach (var x in rk) Console.WriteLine("  " + x);
                if (o.FailOn != null && rk.Any(x => x.Rank <= PlcDoc.SeverityRank(o.FailOn))) failed = true;
            }
        if (failed) { Say($"DOC FAILED: risks with severity {o.FailOn} or higher"); return Failed; }
        return Ok;
    }

    // ------------------------------------------------------------------ check / build
    static int Check(Options o, TwinCatSession tc)
    {
        var sw = Stopwatch.StartNew();
        var (compile, plcs) = tc.Sta.Run(() =>
        {
            var list = tc.PlcProjects();
            if (list.Count == 0) throw new Exception("No PLC project in the solution.");
            foreach (var plc in list)
                if (!tc.CheckAllObjects(plc)) throw new Exception($"'Check all objects' is not available for {plc.Name}.");
            return (tc.ReadErrorList(5000).compile, list.Select(p => p.Name).ToList());
        }, o.TimeoutSec * 1000);
        return ReportCompile(o, "check", string.Join(", ", plcs), compile, sw.Elapsed.TotalSeconds);
    }

    static int Build(Options o, TwinCatSession tc)
    {
        var sw = Stopwatch.StartNew();
        var (failedProjects, compile) = tc.Sta.Run(() => BuildSolution(tc, o), o.TimeoutSec * 1000);
        int rc = ReportCompile(o, "build", Path.GetFileNameWithoutExtension(SolutionName(tc)), compile, sw.Elapsed.TotalSeconds);
        if (rc == Ok && failedProjects > 0) { Say($"BUILD FAILED: {failedProjects} project(s) failed without compiler messages."); return Failed; }
        return rc;
    }

    static (int failedProjects, List<TwinCatSession.ErrorEntry> compile) BuildSolution(TwinCatSession tc, Options o)
    {
        var sb = tc.RequireDte().Solution.SolutionBuild;
        Say(o.Rebuild ? "Rebuild ..." : "Build ...");
        if (o.Rebuild) sb.Clean(true);
        sb.Build(true);
        int failed = sb.LastBuildInfo;
        if (o.CheckAll)
            foreach (var plc in tc.PlcProjects()) tc.CheckAllObjects(plc);
        return (failed, tc.ReadErrorList(5000).compile);
    }

    static int ReportCompile(Options o, string what, string name, List<TwinCatSession.ErrorEntry> compile, double seconds)
    {
        int errors = compile.Count(e => e.Level == "ERROR"), warnings = compile.Count(e => e.Level == "WARN");
        foreach (var e in compile.Where(e => e.Level is "ERROR" or "WARN"))
            Console.WriteLine(JUnit.Annotation(e, e.Level == "ERROR" || o.WarningsAsErrors));
        bool fail = errors > 0 || (o.WarningsAsErrors && warnings > 0);
        Say(System.FormattableString.Invariant($"{what.ToUpperInvariant()} {(fail ? "FAILED" : "OK")}: {errors} error(s), {warnings} warning(s) ({name}, {seconds:0.0} s)"));
        if (o.Junit != null)
        {
            WriteJunit(o.Junit, "TcAgent " + what, new[] { JUnit.CompileSuite($"{name} ({what})", compile, o.WarningsAsErrors, seconds) });
            Say("JUnit: " + Path.GetFullPath(o.Junit));
        }
        return fail ? Failed : Ok;
    }

    // ------------------------------------------------------------------ test
    static int Test(Options o, TwinCatSession tc, bool ownIde)
    {
        var policy = new Policy();
        var suites = new List<XElement>();
        var sw = Stopwatch.StartNew();

        // 1) target - only allowed targets (TCAGENT_ALLOW_TARGETS, default: this PC)
        string target = tc.Sta.Run(() =>
        {
            dynamic sm = tc.RequireSysMan();
            if (!string.IsNullOrEmpty(o.Target)) sm.SetTargetNetId(o.Target);
            return (string)sm.GetTargetNetId();
        });
        try { policy.Check("tc_activate", () => target); }
        catch (Exception ex) { return Refused(o, "target", ex.Message + " (pass --target or set TCAGENT_ALLOW_TARGETS for the CI machine)"); }   // like sil/hil: exit 2
        Say("Test target: " + target);

        // 2) disable I/O devices (no hardware on the CI machine) - by default only in a Visual Studio started by TcAgent
        bool disableIo = o.DisableIo ?? ownIde;
        if (disableIo) Say($"I/O devices disabled for the test: {DisableIoDevices(tc).Count}");

        // 3) build
        if (!o.NoBuild && BuildBeforeTest(o, tc, sw, "TcAgent test") is int buildRc) return buildRc;

        // 4) PLC projects start by themselves after activation: boot project with autostart
        var plcNames = PrepareBootProjects(o, tc).Select(b => b.Name).ToList();
        if (plcNames.Count == 0) return InfraError(o, "plc", "No matching PLC project" + (o.Plc.Count > 0 ? " for --plc " + string.Join(",", o.Plc) : "") + ".");
        Say("PLC: " + string.Join(", ", plcNames) + (o.NoBootProject ? "" : " (boot project with autostart)"));

        // 5) activate and restart in run mode
        var started = DateTime.Now;
        try
        {
            tc.Sta.Run(() =>
            {
                dynamic sm = tc.RequireSysMan();
                sm.ActivateConfiguration();
                sm.StartRestartTwinCAT();
                return 0;
            }, 300_000);
        }
        catch (Exception ex)
        {
            return InfraError(o, "activate", "Activation failed: " + Msg(ex) +
                " - are all licenses on the target activated (TwinCAT trial licenses must be activated by a person)?");
        }
        Say("Configuration activated, TwinCAT restarting in run mode - waiting for TcUnit ...");
        policy.Audit("cli test", new JsonObject { ["solution"] = SolutionName(tc), ["target"] = target }, true, "activated", (long)sw.Elapsed.TotalMilliseconds);

        // 6) + 7) wait for TcUnit, report
        return WaitForTcUnit(o, tc, started, suites, "TcAgent test");
    }

    /// <summary>Disables all enabled I/O devices (no hardware on the CI machine). Returns the tree paths of the disabled devices.</summary>
    static List<string> DisableIoDevices(TwinCatSession tc) => tc.Sta.Run(() =>
    {
        var paths = new List<string>();
        foreach (var dev in TwinCatSession.Children(tc.Item("TIID")))
            if (dev.Disabled == DISABLED_STATE.SMDS_NOT_DISABLED) { dev.Disabled = DISABLED_STATE.SMDS_DISABLED; paths.Add(dev.PathName); }
        return paths;
    });

    /// <summary>Builds before a test run. null = build OK, otherwise the exit code (compiler errors in the log and the JUnit file).</summary>
    static int? BuildBeforeTest(Options o, TwinCatSession tc, Stopwatch sw, string junitName)
    {
        var (failedProjects, compile) = tc.Sta.Run(() => BuildSolution(tc, o), o.TimeoutSec * 1000);
        int errors = compile.Count(e => e.Level == "ERROR");
        foreach (var e in compile.Where(e => e.Level == "ERROR")) Console.WriteLine(JUnit.Annotation(e, true));
        Say($"Build: {errors} error(s), {compile.Count(e => e.Level == "WARN")} warning(s)");
        if (errors == 0 && failedProjects == 0) return null;
        Say("TEST NOT RUN: build failed");
        if (o.Junit != null)
            WriteJunit(o.Junit, junitName, new[] { JUnit.CompileSuite(Path.GetFileNameWithoutExtension(SolutionName(tc)) + " (build)", compile, false, sw.Elapsed.TotalSeconds) });
        return Failed;
    }

    /// <summary>Selected PLC projects (outer TIPC items); unless --no-boot-project: autostart on and boot project generated.
    /// Returns name, tree path and the autostart value before (null = not changed / not readable) for restoring it.</summary>
    static List<(string Name, string Path, bool? AutostartBefore)> PrepareBootProjects(Options o, TwinCatSession tc) => tc.Sta.Run(() =>
    {
        var list = new List<(string Name, string Path, bool? AutostartBefore)>();
        foreach (var plc in TwinCatSession.Children(tc.Item("TIPC")))
        {
            if (o.Plc.Count > 0 && !o.Plc.Any(p => plc.Name.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            bool? before = null;
            if (!o.NoBootProject)
            {
                dynamic d = plc;
                try { before = (bool)d.BootProjectAutostart; } catch { }
                d.BootProjectAutostart = true;
                d.GenerateBootProject(true);
            }
            list.Add((plc.Name, plc.PathName, before));
        }
        return list;
    }, o.TimeoutSec * 1000);

    /// <summary>Waits for TcUnit ("TESTS FINISHED RUNNING", then until the messages stop changing), writes the JUnit file and
    /// returns the exit code. tick runs after every poll (e.g. to start the PLC if nothing happens), beforeReport once
    /// before the JUnit file is written (it may add suites).</summary>
    static int WaitForTcUnit(Options o, TwinCatSession tc, DateTime started, List<XElement> suites, string junitName,
                             Action<TcUnitResults> tick = null, Action beforeReport = null)
    {
        var deadline = DateTime.Now.AddSeconds(o.TimeoutSec);
        TcUnitResults r = null;
        int lastCount = -1, stable = 0;
        DateTime? errorDialogAt = null;
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(2000);
            List<string> lines;
            try
            {
                lines = tc.Sta.Run(() => TwinCatSession.Chronological(tc.ReadErrorList(20000).runtime
                    .Where(e => e.Stamp == null || e.Stamp >= started.AddSeconds(-10)).ToList())
                    .Select(e => e.Text).ToList(), 60_000);
            }
            catch (Exception ex) { Say("Reading the error list failed, retrying: " + Msg(ex)); continue; }
            r = TcUnitResults.Parse(lines);
            tick?.Invoke(r);
            if (!r.Finished)
            {
                // TwinCAT reported an error in a dialog (e.g. "Target system reports a fatal error") and TcUnit has not
                // started 30 s later: do not wait for the full timeout
                var err = Dialogs?.Snapshot().LastOrDefault(d => LooksLikeError(d));
                if (err != null && r.Lines.Count == 0)
                {
                    errorDialogAt ??= DateTime.Now;
                    if ((DateTime.Now - errorDialogAt.Value).TotalSeconds > 30)
                        return InfraError(o, "runtime", $"TwinCAT reported an error and TcUnit did not start: {err}");
                }
                continue;
            }
            stable = lines.Count == lastCount ? stable + 1 : 0;
            lastCount = lines.Count;
            if ((stable >= 2 && r.TestCount != null) || stable >= 5) break;
        }

        beforeReport?.Invoke();
        if (r == null || !r.Finished)
        {
            var hint = r != null && r.Lines.Count > 0 ? "TcUnit started but did not finish" : "no TcUnit output - is TcUnit running in the PLC (TcUnit.RUN())?";
            Say($"TIMEOUT after {o.TimeoutSec} s: {hint}");
            suites.Add(JUnit.ErrorSuite("TcUnit", "run", $"TcUnit did not finish within {o.TimeoutSec} s: {hint}", r != null ? string.Join("\n", r.Lines) : null));
            if (o.Junit != null) WriteJunit(o.Junit, junitName, suites);
            return Infra;
        }

        // report
        foreach (var s in r.Suites.Where(x => x.Tests != null && x.Cases.Count > 0 && x.Cases.Count != x.Tests))
            Say($"Note: suite {s.Name} reports {s.Tests} tests, {s.Cases.Count} were assigned to it - the error list order may be off.");
        foreach (var s in r.Suites)
            foreach (var c in s.Cases.Where(c => c.Failed))
                Console.WriteLine(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
                    ? $"::error title=TcUnit {s.Name}@{c.Name}::{(c.Failure ?? c.Status).Replace("\n", "%0A")}"
                    : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))
                        ? $"##vso[task.logissue type=error]TcUnit {s.Name}@{c.Name}: {c.Failure ?? c.Status}"
                        : $"FAILED {s.Name}@{c.Name}: {c.Failure ?? c.Status}");
        int failed = r.FailedCount;
        Say(System.FormattableString.Invariant(
            $"TEST {(failed == 0 ? "OK" : "FAILED")}: {r.TotalCount} test(s) in {r.SuiteCount ?? r.Suites.Count} suite(s), {failed} failed ({r.Duration ?? 0:0.000} s in the PLC)"));
        if (o.Junit != null)
        {
            suites.AddRange(JUnit.TestSuites(r, Path.GetFileNameWithoutExtension(SolutionName(tc))));
            WriteJunit(o.Junit, junitName, suites);
            Say("JUnit: " + Path.GetFullPath(o.Junit));
        }
        return failed == 0 ? Ok : Failed;
    }

    // ------------------------------------------------------------------ IDE dialogs
    static DialogGuard Dialogs;

    /// <summary>Watches the IDE for modal dialogs (they block every Automation Interface call): logs them and closes them
    /// like Cancel/X, so the pipeline does not hang. The dialogs are reported in the log and in the JUnit file.</summary>
    sealed class DialogGuard : IDisposable
    {
        public readonly List<IdeDialogs.Dialog> Seen = new();
        readonly Timer timer;
        readonly int pid; readonly bool close;
        readonly Dictionary<IntPtr, int> pending = new();

        public DialogGuard(int pid, bool close)
        {
            this.pid = pid; this.close = close;
            if (pid > 0) timer = new Timer(_ => Tick(), null, 1000, 1000);
            else Say("Note: process id of the IDE unknown - dialogs are not watched.");
        }

        void Tick()
        {
            List<IdeDialogs.Dialog> now;
            try { now = IdeDialogs.Find(pid); } catch { return; }
            lock (Seen)
            {
                foreach (var h in pending.Keys.Where(h => now.All(d => d.Handle != h)).ToList()) pending.Remove(h);
                foreach (var d in now)
                {
                    pending[d.Handle] = pending.TryGetValue(d.Handle, out var n) ? n + 1 : 1;
                    if (pending[d.Handle] != 2) continue;          // open for ~2 s: a real dialog, not a flicker
                    Seen.Add(d);
                    var action = close ? "closed (Cancel/X)" : "left open (--keep-dialogs)";
                    Say($"IDE DIALOG {d} -> {action}");
                    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                        Console.WriteLine($"::warning title=TwinCAT dialog: {d.Title.Replace(":", " ")}::{d.Text}");
                    else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
                        Console.WriteLine($"##vso[task.logissue type=warning]TwinCAT dialog '{d.Title}': {d.Text}");
                    if (close) IdeDialogs.Close(d);
                }
            }
        }

        public List<IdeDialogs.Dialog> Snapshot() { lock (Seen) return Seen.ToList(); }

        public void Dispose() => timer?.Dispose();
    }

    /// <summary>Watches the IDE processes started by this command while the solution loads - before the process id is known.
    /// A hidden Visual Studio that shows a dialog during Solution.Open would otherwise wait for ever (and the step only ends
    /// with a timeout). Dialogs are reported with their text and - unless --keep-dialogs - closed like Cancel/X.</summary>
    sealed class LoadGuard : IDisposable
    {
        readonly DateTime since = DateTime.Now.AddSeconds(-2);
        readonly bool close;
        readonly Timer timer;
        readonly Dictionary<IntPtr, int> pending = new();
        public readonly List<IdeDialogs.Dialog> Seen = new();
        public readonly HashSet<int> Pids = new();

        public LoadGuard(bool close) { this.close = close; timer = new Timer(_ => Tick(), null, 1000, 1000); }

        static readonly string[] Names = { "devenv", "TcXaeShell", "TcXaeShell64" };

        void Tick()
        {
            try
            {
                foreach (var n in Names)
                    foreach (var p in Process.GetProcessesByName(n))
                        using (p) { try { if (p.StartTime >= since) lock (Pids) Pids.Add(p.Id); } catch { } }
                List<IdeDialogs.Dialog> now = new();
                lock (Pids) foreach (var pid in Pids) { try { now.AddRange(IdeDialogs.Find(pid)); } catch { } }
                lock (Seen)
                {
                    foreach (var h in pending.Keys.Where(h => now.All(d => d.Handle != h)).ToList()) pending.Remove(h);
                    foreach (var d in now)
                    {
                        pending[d.Handle] = pending.TryGetValue(d.Handle, out var c) ? c + 1 : 1;
                        if (pending[d.Handle] != 2) continue;
                        Seen.Add(d);
                        Say($"IDE DIALOG while loading {d} -> {(close ? "closed (Cancel/X)" : "left open (--keep-dialogs)")}");
                        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                            Console.WriteLine($"::warning title=TwinCAT dialog: {d.Title.Replace(":", " ")}::{d.Text}");
                        if (close) IdeDialogs.Close(d);
                    }
                }
            }
            catch { }
        }

        /// <summary>Ends the IDE processes this command started (after a load timeout) - only those without a visible window.</summary>
        public List<int> KillStarted()
        {
            var killed = new List<int>();
            lock (Pids)
                foreach (var pid in Pids)
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle)) continue;
                        p.Kill(true); killed.Add(pid);
                    }
                    catch { }
            return killed;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        public string DialogText() { lock (Seen) return Seen.Count == 0 ? null : string.Join("; ", Seen.Select(d => d.ToString())); }

        public void Dispose() => timer.Dispose();
    }

    static bool LooksLikeError(IdeDialogs.Dialog d) =>
        new[] { "error", "fehler", "fatal", "failed", "fehlgeschlagen" }.Any(k =>
            d.Title.Contains(k, StringComparison.OrdinalIgnoreCase) || d.Text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>JUnit file plus a suite "TwinCAT IDE dialogs" (one errored case per dialog) if dialogs came up.</summary>
    static void WriteJunit(string path, string name, IEnumerable<XElement> suites)
    {
        var list = suites.ToList();
        var dialogs = Dialogs?.Snapshot() ?? new List<IdeDialogs.Dialog>();
        if (dialogs.Count > 0)
        {
            var suite = new XElement("testsuite",
                new XAttribute("name", "TwinCAT IDE dialogs"), new XAttribute("tests", dialogs.Count), new XAttribute("failures", 0),
                new XAttribute("errors", dialogs.Count), new XAttribute("skipped", 0), new XAttribute("time", "0"),
                new XAttribute("timestamp", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("hostname", Environment.MachineName));
            int i = 0;
            foreach (var d in dialogs)
                suite.Add(new XElement("testcase", new XAttribute("name", $"{++i}: {d.Title}"), new XAttribute("classname", "TwinCAT IDE dialogs"),
                    new XElement("error", new XAttribute("message", d.Text.Length > 0 ? d.Text : d.Title), new XAttribute("type", "IdeDialog"), d.ToString())));
            list.Add(suite);
        }
        JUnit.Write(path, name, list);
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>TcUnit messages are "messages" in the error list - make sure the list does not filter them.</summary>
    static void ShowAllMessages(TwinCatSession tc)
    {
        try
        {
            var el = tc.RequireDte().ToolWindows.ErrorList;
            el.ShowErrors = true; el.ShowWarnings = true; el.ShowMessages = true;
        }
        catch { }
    }

    static string SolutionName(TwinCatSession tc)
    {
        try { return tc.Sta.Run(() => (string)tc.RequireDte().Solution.FullName); } catch { return "solution"; }
    }

    static int InfraError(Options o, string step, string message)
    {
        Console.Error.WriteLine($"ERROR ({step}): {message}");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Console.WriteLine($"::error title=TcAgent {step}::{message.Replace("\n", "%0A")}");
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))) Console.WriteLine($"##vso[task.logissue type=error]TcAgent {step}: {message}");
        if (o.Junit != null)
            try { WriteJunit(o.Junit, "TcAgent " + o.Command, new[] { JUnit.ErrorSuite("TcAgent " + o.Command, step, message) }); } catch { }
        return Infra;
    }

    static string Msg(Exception ex) => ex.InnerException?.Message ?? ex.Message;

    static void Say(string s) => Console.WriteLine($"[tcagent {DateTime.Now:HH:mm:ss}] {s}");

    static Options ParseArgs(string[] args)
    {
        var o = new Options { Command = args[0].ToLowerInvariant().TrimStart('-', '/') };
        if (o.Command is "h" or "?") o.Command = "help";
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            string Val() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : throw new Exception($"{a} needs a value");
            switch (a.ToLowerInvariant())
            {
                case "--solution": case "-s": o.Solution = Val(); break;
                case "--attach": o.Attach = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : ""; break;
                case "--junit": case "-o": o.Junit = Val(); break;
                case "--out": case "--path": o.Out = Val(); break;
                case "--sarif": o.Sarif = Val(); break;
                case "--settings": o.Settings = Val(); break;
                case "--install": o.Install = true; break;
                case "--risks": o.Risks = true; break;
                case "--check": o.CheckLock = true; break;
                case "--fail-on": o.FailOn = Val().ToLowerInvariant(); break;
                case "--scope": o.ScopePath = Val(); break;
                case "--seconds": o.Seconds = int.Parse(Val()); break;
                case "--target": o.Target = Val(); break;
                case "--plc": o.Plc.AddRange(Val().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--timeout": o.TimeoutSec = int.Parse(Val()); o.TimeoutGiven = true; break;
                case "--load-timeout": o.LoadTimeoutSec = int.Parse(Val()); break;
                case "--progid": o.ProgId = Val(); break;
                case "--visible": o.Visible = true; break;
                case "--keep-open": o.KeepOpen = true; break;
                case "--rebuild": o.Rebuild = true; break;
                case "--check-all": o.CheckAll = true; break;
                case "--warnings-as-errors": o.WarningsAsErrors = true; break;
                case "--no-build": o.NoBuild = true; break;
                case "--no-boot-project": o.NoBootProject = true; break;
                case "--disable-io": o.DisableIo = true; break;
                case "--keep-io": o.DisableIo = false; break;
                case "--keep-dialogs": o.CloseDialogs = false; break;
                case "--allow": o.Allow.AddRange(Val().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--keep-running": o.KeepRunning = true; break;
                case "--config": o.Config = Val(); break;
                case "--only": o.Only = Val(); break;
                case "--from": o.From = Val(); break;
                case "--dry-run": o.DryRun = true; break;
                case "--init": o.Init = true; break;
                case "--clean": o.Clean = true; break;
                case "--force": o.Force = true; break;
                case "--tests": o.Tests.AddRange(Val().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--download": o.Download = true; break;
                case "--builtin": o.BuiltIn = true; break;
                case "--approve": o.Approve = Val().Trim().ToLowerInvariant(); break;
                case "--key": o.Key = Val().Trim(); break;
                case "--level": o.Level = Val().Trim().ToLowerInvariant(); break;
                case "--action": o.Action = Val().Trim().ToLowerInvariant(); break;
                case "--name": o.Name = Val().Trim(); break;
                case "--url": o.Url = Val().Trim(); break;
                case "--ref": o.Ref = Val().Trim(); break;
                case "--item": o.Item = Val().Trim(); break;
                case "--kind": o.Kind = Val().Trim().ToLowerInvariant(); break;
                case "--query": o.Query = Val(); break;
                case "--libraries": o.Libraries = Val(); break;
                case "--terms": o.Terms = Val(); break;
                case "--accept-license": o.AcceptLicense = true; break;
                case "--message": o.Message = Val(); break;
                default:
                    // st: the sub-command (export, check, import) is the first argument that is not an option
                    if (o.Command is "st" or "tcunit" or "skills" or "sources" or "knowledge" && o.Sub == null && !a.StartsWith("-")) { o.Sub = a.ToLowerInvariant(); break; }
                    throw new Exception("Unknown option " + a);
            }
        }
        if (o.Command != "ci" && (o.Config != null || o.Only != null || o.Init || (o.Command is not ("st" or "skills" or "sources") && (o.From != null || o.DryRun))))
            throw new Exception("--config, --only, --from, --dry-run and --init are only for ci (--from and --dry-run also for st import, --dry-run for skills mirror and sources sync)");
        if ((o.Clean || o.Force) && o.Command != "st") throw new Exception("--clean and --force are only for st");
        if (o.Command is "help" or "version") return o;
        if (o.Command is "st") { ValidateSt(o); return o; }
        if ((o.Tests.Count > 0 || o.Download) && o.Command != "tcunit") throw new Exception("--tests and --download are only for tcunit run");
        if (o.Command is "tcunit") { ValidateTcUnit(o); return o; }
        if (o.Command is "install") return o;
        if (o.Command is "ci") return o;                  // the steps are checked by ci itself (CliCi.cs)
        if ((o.Key != null || o.Level != null || o.Action != null || o.Message != null) && o.Command is not ("knowledge" or "sources")) throw new Exception("--key, --level, --action and --message are only for knowledge and sources");
        if ((o.Name != null || o.Url != null || o.Ref != null || o.Item != null || o.Kind != null || o.Query != null || o.Libraries != null || o.Terms != null || o.AcceptLicense)
            && o.Command is not ("knowledge" or "sources")) throw new Exception("--name, --url, --ref, --item, --kind, --query, --libraries, --terms and --accept-license are only for knowledge and sources");
        if (o.Command is "knowledge")                     // files only: the solution is optional (its platform and project level)
        {
            if (o.Sub is not ("json" or "disable" or "enable" or "new" or "move" or "delete" or "git" or "feedback" or "platform" or "settings" or "docs"))
                throw new Exception("knowledge needs json, disable, enable, new, move, delete, git, feedback, platform, settings or docs, e.g. TcAgent.exe knowledge json --solution M.sln");
            if (o.Sub is "disable" or "enable" && (o.Key == null || !o.Key.Contains('/'))) throw new Exception("knowledge " + o.Sub + " needs --key <kind>/<name>");
            if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
            return o;
        }
        if (o.Command is "sources")                       // files and git only: the solution is optional (its project level)
        {
            if (o.Sub is not ("status" or "sync" or "add" or "remove" or "search" or "install" or "pull" or "diff" or "changes" or "merge" or "propose" or "push"))
                throw new Exception("sources needs status, sync, add, remove, search, install, pull, diff, changes, merge, propose or push, e.g. TcAgent.exe sources sync --solution M.sln");
            if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
            return o;
        }
        if (o.Command is "skills")                        // files only: the solution is optional (its platform and project skills)
        {
            if (o.Sub is not ("list" or "mirror")) throw new Exception("skills needs list or mirror, e.g. TcAgent.exe skills mirror");
            if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
            return o;
        }
        if (o.Command is "doctor" or "diag")
        {
            if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
            return o;
        }
        if (o.Solution == null && o.Attach == null) throw new Exception("--solution <path to .sln> (or --attach) is required");
        if (o.Solution != null && o.Attach != null) throw new Exception("use either --solution or --attach");
        if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
        if (o.TimeoutSec < 10) throw new Exception("--timeout is in seconds (min. 10)");
        if (o.Seconds < 1) throw new Exception("--seconds must be at least 1");
        if (o.Command is "library" or "archive" or "scope" && o.Out == null) throw new Exception($"{o.Command} needs --out <file>");
        ValidateTestRunArgs(o);
        if (o.FailOn != null && o.FailOn is not ("high" or "medium" or "low")) throw new Exception("--fail-on: high, medium or low");
        if (o.CheckLock && o.Command != "lock") throw new Exception("--check is an option of lock");
        return o;
    }

    const string Help = @"TcAgent command line - TwinCAT 3 in CI pipelines

  TcAgent.exe check --solution <sln> [--junit <file>] [--warnings-as-errors]
      'Check all objects' of every PLC project: compiler errors and warnings, no code is generated.

  TcAgent.exe build --solution <sln> [--rebuild] [--check-all] [--junit <file>] [--warnings-as-errors]
      Builds the solution.

  TcAgent.exe analyze --solution <sln> [--plc <names>] [--settings <file.csa>] [--sarif <file>] [--junit <file>]
      Static analysis (TE1200 license): rule violations like compiler messages; SARIF for code scanning (4026.20+).

  TcAgent.exe library --solution <sln> --out <file.library> [--plc <name>] [--install] [--no-build]
      Builds and saves the PLC project as library - e.g. as pipeline artifact.

  TcAgent.exe archive --solution <sln> --out <file.tszip>
      Saves the whole solution as archive.

  TcAgent.exe scope --solution <sln> --out <file.csv|.svdx> [--scope <project/scope>] [--seconds <n>]
      Records with a Scope configuration of the solution (TE130x) for n seconds (default 10) and exports the data.

  TcAgent.exe doc --solution <sln> [--out <file.html>] [--plc <names>] [--risks] [--fail-on high|medium|low]
      Program documentation as HTML: call hierarchy, call graph, class diagram, step chains, risks, reference per object.
      --risks prints the static risk list (as tc_doc action=risks); --fail-on fails the step (exit 1) on risks of that
      severity or higher.
      Uses the explanations the agent stored with tc_doc (<solution>\.tcagent\doc-summaries.json).
      Default output: <solution folder>\doc\<PLC project>.html

  TcAgent.exe sbom --solution <sln> [--out <file.cdx.json>] [--plc <names>]
      Software bill of materials (CycloneDX 1.6 JSON) per PLC project: every library reference with resolved version,
      vendor, purl and the SHA-256 of the library file (when found in a library repository), project version, git
      commit/branch. Missing data is marked, not left out. Default output: <solution folder>\sbom\<PLC project>.cdx.json

  TcAgent.exe lock --solution <sln> [--plc <names>] [--check]
      Writes the resolved library versions to <solution folder>\.tcagent\libraries.lock.json (commit it to Git).
      --check compares the current resolution with the lock file: exit 0 = equal, 1 = differences (listed), 2 = no lock file.

  TcAgent.exe doctor [--solution <sln>] [--target <AmsNetId>] [--progid <ProgID>]
      Checks whether this machine and this user can run TcAgent pipelines: desktop session (not a service), Visual
      Studio / XAE Shell, TwinCAT XAE, interop, runtime via ADS, TcUnit, git - with a hint for every problem. With
      --solution it opens the solution hidden like a pipeline and closes it again. Exit 0 = ready, 3 = not ready.

  TcAgent.exe diag [--solution <sln>]
      Versions and environment for an issue report as a Markdown list: TcAgent, the registered extension, TwinCAT,
      installed IDEs, runtime state, Windows, configured knowledge levels - no names, paths or addresses.

  TcAgent.exe install --path <folder>
      Copies this TcAgent including the interop of this TwinCAT installation to a fixed folder (e.g. C:\Tools\TcAgent)
      for pipelines - the VSIX installs per user and per version.

  TcAgent.exe ci [--config <file>] [--only <steps>] [--from <n|step>] [--dry-run] [--init] [--solution <sln>]
      Runs the pipeline of the repository: the steps of .tcagent\pipeline.json (searched upwards from the current
      folder) in order, each as its own TcAgent.exe call with a fresh IDE, in the folder that contains .tcagent. Stops
      at the first failing step (unless continueOnError), prints a summary and writes <results>\ci-summary.md (and the
      GitHub job summary). --only/--from select steps by run or name (--from also by number), --dry-run prints the
      command lines, --init writes a starter file, --solution overrides the file's solution. Format: docs/PIPELINE.md.

  TcAgent.exe st export --solution <sln> [--plc <names>] [--out <folder>] [--clean]
  TcAgent.exe st check  --solution <sln> [--plc <names>] [--out <folder>]
  TcAgent.exe st import --solution <sln> [--plc <names>] [--from <folder>] [--dry-run] [--force]
      PLC code as plain-text ST files (one .st per POU/GVL/DUT/interface with its methods/properties/actions) and back -
      works on the project files, no IDE. Default folder: <solution folder>\st\<PLC project>\<folder>\<name>.st, plus
      <PLC project>\references.json with the library references of the .plcproj (export/check only, import ignores it).
      export writes only changed files (--clean also removes .st files without object, never references.json). check:
      exit 1 if the text folder (.st files and references.json) differs from the project (CI gate). import changes the existing objects (Ids kept), creates new methods/properties/
      actions/objects, never deletes; --dry-run lists the changes. Refused (exit 2) while the solution is open in an IDE.
      Graphical implementations (SFC, LD, FBD, CFC) stay as they are. Format: docs/PLAINTEXT.md.

  TcAgent.exe tcunit list --solution <sln> [--plc <name>]
      TcUnit tests read from the code (no IDE): PLC projects, suites, instances, tests with file, line, the /// description
      and the paths of the enable bools (bRunSuite, bRun_<TestMethod>) as JSON. Format: docs/TCUNIT_TESTS.md.

  TcAgent.exe tcunit run --solution <sln>|--attach [pid] [--tests <a,b>] [--plc <name>] [--target local|<AmsNetId>]
                         [--timeout <s>] [--junit <file>] [--no-build] [--download]
      Runs the selected tests: build + activation only if the code changed, reset of the PLC via ADS, enable bools
      (TRUE = selected, FALSE = the rest), start, live progress and results, reset again (TwinCAT stays in Run mode).
      --tests: instances (MAIN.fbMotor), suite types, MAIN.fbMotor@Test or test names; default all. Writes one JSON
      event per line (plan, step, suite, running, result, progress, done). Settings: .tcagent\tcunit.json (plcProject,
      target, timeout, progress). --download activates even if the code did not change. A remote target must be in
      TCAGENT_ALLOW_TARGETS. Exit 0 = passed, 1 = failed tests/build, 2 = refused/unknown tests, 3 = infrastructure/timeout.

  TcAgent.exe tcunit report --solution <sln> [--junit <file>] [--out <file.html>] [--builtin] [--approve <id>]
      HTML report of the last tcunit run (or of the JUnit file given): the command of ""report"" in .tcagent\tcunit.json
      (placeholders {junit} {html} {solution} {solutionDir}, run in the solution folder) or TcAgent's own report.
      The command runs only after you approved it once for this solution and command text: until then TcAgent's own
      report is made and the id to approve is shown; --approve <id> approves and runs it (only if you trust the solution).
      --builtin uses TcAgent's own report even if ""report"" is set. Default output:
      %LOCALAPPDATA%\TcAgent\tcunit\<solution>-<hash>\report.html. No IDE needed.

  TcAgent.exe feedback [--open] [--markdown <file.md>] [--summary] [--path <file|folder>]
      Report of the tc_feedback entries (TCAGENT_FEEDBACK or the default store), grouped by tool, as Markdown
      ready to paste into GitHub issues - to stdout or into the file. --open: only open entries; --summary: counts only.
      No IDE needed.

  TcAgent.exe sources status|sync|add|remove|search|install|pull|diff|changes|merge|propose|push [--solution <sln>] [--dry-run]
      The Git sources of skills, workflows and rules: sync restores every installed item at the commit of
      skills.lock.json (--dry-run only compares, exit 1 on differences). No IDE needed.

  TcAgent.exe skills list|mirror [--solution <sln>]
      The skills that apply / copy them into the folders of the agent CLIs. No IDE needed.

  TcAgent.exe knowledge json|new|move|delete|disable|enable|git|platform|feedback|settings|docs [--solution <sln>] ...
      The actions of the knowledge window (files of every level, Git, platforms, documentation sources, feedback
      reports) for scripts. knowledge docs [--action list|add|remove|copy|search] lists the sources of tc_docs.

  TcAgent.exe test --solution <sln> [--target <AmsNetId>] [--plc <names>] [--timeout <s>] [--junit <file>]
      Builds, activates the configuration on the test target, starts the PLC via boot project with autostart,
      waits for TcUnit and writes the results. Only targets allowed by TCAGENT_ALLOW_TARGETS (default: this PC).

  TcAgent.exe sil --solution <sln> [--target local|<AmsNetId>] [--plc <names>] [--timeout <s>] [--junit <file>]
                  [--scope <project/scope>] [--out <file>] [--keep-running]
      Software in the loop on the runtime of this PC (local XAR, or a usermode runtime allowed via TCAGENT_ALLOW_TARGETS):
      like test, plus: runtime reachable and in Run mode after the activation, PLC login + start if TcUnit does not start
      by itself, optional Scope recording, and a clean-up that always runs (Scope stopped, runtime to Config mode,
      target system / I/O devices / autostart restored in an attached IDE).

  TcAgent.exe hil --solution <sln> --target <AmsNetId> [--allow <AmsNetId>] [same options as sil]
      Hardware in the loop on a remote test PLC: like sil, I/O devices stay enabled. The target must be allowed
      explicitly (TCAGENT_ALLOW_TARGETS or --allow) - a pipeline cannot confirm, so it is refused otherwise (exit 2).
      The ADS route to the target must exist; if not, a broadcast search tells whether only the route is missing.

Options
  --solution, -s <sln>     solution to open in a new, hidden Visual Studio (closed afterwards, nothing is saved)
  --attach [pid|name]      use an already running Visual Studio instead (developer machine)
  --junit, -o <file>       write JUnit XML (GitHub Actions, Azure DevOps, Jenkins, GitLab)
  --target <AmsNetId>      test target (default: the target set in the project; sil: local); must be allowed
  --allow <a,b>            hil: allow these AmsNetIds for this run in addition to TCAGENT_ALLOW_TARGETS
  --keep-running           sil/hil: leave TwinCAT in Run mode at the end (default: Config mode, PLC stopped)
  --plc <a,b>              only these PLC projects (default: all)
  --timeout <s>            max. time for build and for the test run (default 1800)
  --load-timeout <s>       max. time to open the solution (default 300)
  --visible                show Visual Studio (debugging the pipeline)
  --keep-open              do not close the Visual Studio started by TcAgent
  --rebuild                clean before building
  --check-all              build: also 'check all objects' (finds errors in unused POUs)
  --warnings-as-errors     warnings fail the step
  --no-build               test/sil/hil: use the existing build
  --no-boot-project        test/sil/hil: do not create a boot project (test: PLC must start by itself)
  --disable-io / --keep-io test/sil/hil: disable all I/O devices (default: test/sil only in a Visual Studio
                           started by TcAgent, hil never)
  --out <file>             library/archive/scope/doc/sbom/tcunit report: output file; sil/hil: Scope export (default scope-<sil|hil>.csv);
                           st export/check: text folder (default <solution folder>\st)
  --sarif <file>           analyze: SARIF output
  --settings <file.csa>    analyze: static analysis settings to load first
  --install                library: also install into the library repository
  --risks                  doc: print the static risk list
  --check                  lock: compare with .tcagent\libraries.lock.json instead of writing it
  --fail-on <severity>     doc: exit code 1 when there are risks of this severity or higher (high, medium, low)
  --scope <path>           scope, sil/hil: '<measurement project>/<scope>' (default: the only one); sil/hil record
                           during the test run when --scope or --out is given
  --seconds <n>            scope: recording time (default 10)
  --keep-dialogs           do not close dialogs of the IDE (default: closed like Cancel/X and logged - they block the IDE)
  --progid <id>            COM ProgID of the IDE, e.g. TcXaeShell.DTE.17.0 (default: VS 2022, then TcXaeShell)
  --config <file>          ci: pipeline file (default: .tcagent\pipeline.json in the current folder or above)
  --only <a,b>             ci: run only these steps (run or name; also steps with ""enabled"": false)
  --from <n|step>          ci: start at this step (number in the file, run or name); st import: text folder
  --dry-run                ci: print the resolved command lines, run nothing; st import: list the changes, write nothing
  --clean                  st export: remove .st files whose object no longer exists (references.json is kept)
  --force                  st import: write even if it cannot be checked whether the solution is open in an IDE
  --init                   ci: write a starter .tcagent\pipeline.json (never overwrites)
  --tests <a,b>            tcunit run: instances, suite types, instance@test, test names or @<file> (saved test
                           selection *.tcunit.json or one entry per line; default: all)
  --download               tcunit run: build and activate even if the runtime runs the current code

Exit codes: 0 OK, 1 compiler errors, failed tests, lock or st check differences, st import errors, 2 wrong call, refused
            (test/sil/hil: target not allowed; st import: solution open in an IDE) or no lock file (lock --check),
            3 infrastructure problem (IDE, route, activation, Run mode, timeout).
Without arguments TcAgent.exe runs as MCP server for AI clients.";
}
