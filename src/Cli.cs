// TcAgent command line for CI pipelines - the same TcAgent.exe, without an AI:
//
//   TcAgent.exe check --solution <sln>             "Check all objects": compiler errors, no code generated
//   TcAgent.exe build --solution <sln>             build the solution
//   TcAgent.exe test  --solution <sln>             build, activate on the test target, run TcUnit, collect the results
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

public static class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2, Infra = 3;

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "check" or "build" or "test" or "analyze" or "library" or "archive" or "scope" or "help" or "--help" or "-h" or "/?" or "version" or "--version";

    sealed class Options
    {
        public string Command, Solution, Attach, Junit, Target, ProgId, Out, Sarif, Settings, ScopePath;
        public int Seconds = 10;
        public bool Install;
        public readonly List<string> Plc = new();
        public bool Visible, KeepOpen, Rebuild, CheckAll, WarningsAsErrors, NoBuild, NoBootProject;
        public bool? DisableIo;
        public bool CloseDialogs = true;
        public int TimeoutSec = 1800, LoadTimeoutSec = 300;
    }

    public static int Run(string[] args)
    {
        Options o;
        try { o = ParseArgs(args); }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Usage; }
        if (o.Command is "help") { Console.WriteLine(Help); return Ok; }
        if (o.Command is "version") { Console.WriteLine($"TcAgent {Program.ServerVersion}"); return Ok; }

        Program.ClientName = "cli:" + o.Command;
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
                    sta.Run(() => tc.Open(sln, o.Visible, o.ProgId), o.LoadTimeoutSec * 1000);
                    ownIde = true;
                    sta.Run(() => { tc.WaitForTwinCat(o.LoadTimeoutSec); return 0; }, (o.LoadTimeoutSec + 30) * 1000);
                    Say(tc.InstanceInfo);
                }
                sta.Run(() => { tc.RequireSysMan(); ShowAllMessages(tc); return 0; });
                guard = new DialogGuard(tc.AttachedPid, o.CloseDialogs);
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
                "build" => Build(o, tc),
                "test" => Test(o, tc, ownIde),
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
        catch (Exception ex) { return InfraError(o, "target", ex.Message + " (pass --target or set TCAGENT_ALLOW_TARGETS for the CI machine)"); }
        Say("Test target: " + target);

        // 2) disable I/O devices (no hardware on the CI machine) - by default only in a Visual Studio started by TcAgent
        bool disableIo = o.DisableIo ?? ownIde;
        if (disableIo)
        {
            var n = tc.Sta.Run(() =>
            {
                int count = 0;
                foreach (var dev in TwinCatSession.Children(tc.Item("TIID")))
                    if (dev.Disabled == DISABLED_STATE.SMDS_NOT_DISABLED) { dev.Disabled = DISABLED_STATE.SMDS_DISABLED; count++; }
                return count;
            });
            Say($"I/O devices disabled for the test: {n}");
        }

        // 3) build
        if (!o.NoBuild)
        {
            var (failedProjects, compile) = tc.Sta.Run(() => BuildSolution(tc, o), o.TimeoutSec * 1000);
            int errors = compile.Count(e => e.Level == "ERROR");
            foreach (var e in compile.Where(e => e.Level == "ERROR")) Console.WriteLine(JUnit.Annotation(e, true));
            Say($"Build: {errors} error(s), {compile.Count(e => e.Level == "WARN")} warning(s)");
            if (errors > 0 || failedProjects > 0)
            {
                Say("TEST NOT RUN: build failed");
                if (o.Junit != null)
                    WriteJunit(o.Junit, "TcAgent test", new[] { JUnit.CompileSuite(Path.GetFileNameWithoutExtension(SolutionName(tc)) + " (build)", compile, false, sw.Elapsed.TotalSeconds) });
                return Failed;
            }
        }

        // 4) PLC projects start by themselves after activation: boot project with autostart
        var plcNames = tc.Sta.Run(() =>
        {
            var names = new List<string>();
            foreach (var plc in TwinCatSession.Children(tc.Item("TIPC")))
            {
                if (o.Plc.Count > 0 && !o.Plc.Any(p => plc.Name.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (!o.NoBootProject)
                {
                    dynamic d = plc;
                    d.BootProjectAutostart = true;
                    d.GenerateBootProject(true);
                }
                names.Add(plc.Name);
            }
            return names;
        }, o.TimeoutSec * 1000);
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

        // 6) wait for TcUnit: "TESTS FINISHED RUNNING", then until the messages stop changing (TcUnit logs a few lines per cycle)
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

        if (r == null || !r.Finished)
        {
            var hint = r != null && r.Lines.Count > 0 ? "TcUnit started but did not finish" : "no TcUnit output - is TcUnit running in the PLC (TcUnit.RUN())?";
            Say($"TIMEOUT after {o.TimeoutSec} s: {hint}");
            suites.Add(JUnit.ErrorSuite("TcUnit", "run", $"TcUnit did not finish within {o.TimeoutSec} s: {hint}", r != null ? string.Join("\n", r.Lines) : null));
            if (o.Junit != null) WriteJunit(o.Junit, "TcAgent test", suites);
            return Infra;
        }

        // 7) report
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
            WriteJunit(o.Junit, "TcAgent test", suites);
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
                case "--out": o.Out = Val(); break;
                case "--sarif": o.Sarif = Val(); break;
                case "--settings": o.Settings = Val(); break;
                case "--install": o.Install = true; break;
                case "--scope": o.ScopePath = Val(); break;
                case "--seconds": o.Seconds = int.Parse(Val()); break;
                case "--target": o.Target = Val(); break;
                case "--plc": o.Plc.AddRange(Val().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--timeout": o.TimeoutSec = int.Parse(Val()); break;
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
                default: throw new Exception("Unknown option " + a);
            }
        }
        if (o.Command is "help" or "version") return o;
        if (o.Solution == null && o.Attach == null) throw new Exception("--solution <path to .sln> (or --attach) is required");
        if (o.Solution != null && o.Attach != null) throw new Exception("use either --solution or --attach");
        if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception("Solution not found: " + o.Solution);
        if (o.TimeoutSec < 10) throw new Exception("--timeout is in seconds (min. 10)");
        if (o.Seconds < 1) throw new Exception("--seconds must be at least 1");
        if (o.Command is "library" or "archive" or "scope" && o.Out == null) throw new Exception($"{o.Command} needs --out <file>");
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

  TcAgent.exe test --solution <sln> [--target <AmsNetId>] [--plc <names>] [--timeout <s>] [--junit <file>]
      Builds, activates the configuration on the test target, starts the PLC via boot project with autostart,
      waits for TcUnit and writes the results. Only targets allowed by TCAGENT_ALLOW_TARGETS (default: this PC).

Options
  --solution, -s <sln>     solution to open in a new, hidden Visual Studio (closed afterwards, nothing is saved)
  --attach [pid|name]      use an already running Visual Studio instead (developer machine)
  --junit, -o <file>       write JUnit XML (GitHub Actions, Azure DevOps, Jenkins, GitLab)
  --target <AmsNetId>      test target (default: the target set in the project); must be allowed
  --plc <a,b>              only these PLC projects (default: all)
  --timeout <s>            max. time for build and for the test run (default 1800)
  --load-timeout <s>       max. time to open the solution (default 300)
  --visible                show Visual Studio (debugging the pipeline)
  --keep-open              do not close the Visual Studio started by TcAgent
  --rebuild                clean before building
  --check-all              build: also 'check all objects' (finds errors in unused POUs)
  --warnings-as-errors     warnings fail the step
  --no-build               test: use the existing build
  --no-boot-project        test: do not create a boot project (PLC must start by itself)
  --disable-io / --keep-io test: disable all I/O devices (default: only in a Visual Studio started by TcAgent)
  --out <file>             library/archive: output file
  --sarif <file>           analyze: SARIF output
  --settings <file.csa>    analyze: static analysis settings to load first
  --install                library: also install into the library repository
  --scope <path>           scope: '<measurement project>/<scope>' (default: the only one)
  --seconds <n>            scope: recording time (default 10)
  --keep-dialogs           do not close dialogs of the IDE (default: closed like Cancel/X and logged - they block the IDE)
  --progid <id>            COM ProgID of the IDE, e.g. TcXaeShell.DTE.17.0 (default: VS 2022, then TcXaeShell)

Exit codes: 0 OK, 1 compiler errors or failed tests, 2 wrong call, 3 infrastructure problem (IDE, activation, timeout).
Without arguments TcAgent.exe runs as MCP server for AI clients.";
}
