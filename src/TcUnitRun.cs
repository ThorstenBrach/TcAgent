// TcUnit run with test selection and live progress (TcAgent.exe tcunit run, tool tc_test_run, TcUnit Tests window).
//
// Flow (docs/TCUNIT_TESTS.md):
//   target (local by default; another one only if TCAGENT_ALLOW_TARGETS allows it) -> save all + compare the code with
//   what the runtime runs: if it changed (or TwinCAT is in Config mode / runs another project) build, boot project,
//   activate and restart in Run mode - TwinCAT stays in Run mode -> reset of the PLC via ADS (Stop, all variables back to
//   their initial values: every enable bool TRUE again) -> write ALL enable bools (TRUE = selected) -> start via ADS ->
//   follow TcUnit's own test table (<suite>.Tests[i]: name, finished, failed, skipped, message, duration) and
//   GVL_TcUnit.CurrentTest*BeingCalled every 300 ms -> after TcUnit printed the final results: failure texts (EXP/ACT/MSG)
//   from the runtime messages of the IDE -> reset again, so the next run can start right away.
// No IDE login: online commands of the IDE may ask questions in dialogs (download, online change); ADS does not.
// Cancel/timeout = reset; tests that did not finish stay "notrun".
//
// Events: one JSON object per call of emit (the CLI writes them as lines for the window):
//   plan, step, suite, running, result, progress (optional progress block), done
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TwinCAT.Ads;

namespace TcAgent;

/// <summary>Optional settings of the solution: &lt;solution folder&gt;\.tcagent\tcunit.json.</summary>
public sealed class TcUnitConfig
{
    public string PlcProject, Target, Progress, Report, ReportGroupBy, File;
    public TimeSpan Timeout = TimeSpan.FromMinutes(5);
    public readonly List<string> Errors = new();

    public static TcUnitConfig Load(string slnDir)
    {
        var c = new TcUnitConfig { File = Path.Combine(slnDir ?? ".", ".tcagent", "tcunit.json") };
        if (!System.IO.File.Exists(c.File)) return c;
        try
        {
            var j = JsonNode.Parse(System.IO.File.ReadAllText(c.File), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            string S(string k) => j?[k] is JsonValue v && v.ToString().Trim() is { Length: > 0 } s ? s : null;
            c.PlcProject = S("plcProject"); c.Target = S("target"); c.Progress = S("progress"); c.Report = S("report"); c.ReportGroupBy = S("reportGroupBy");
            if (j?["timeout"] is JsonValue t)
            {
                if (t.TryGetValue<double>(out var sec)) c.Timeout = TimeSpan.FromSeconds(sec);
                else if (TimeSpan.TryParse(t.ToString(), CultureInfo.InvariantCulture, out var ts)) c.Timeout = ts;
                else c.Errors.Add($"timeout: '{t}' is neither seconds nor hh:mm:ss");
            }
        }
        catch (Exception ex) { c.Errors.Add(ex.Message); }
        return c;
    }
}

public sealed class TcUnitRun
{
    public sealed class Options
    {
        public string Solution, Plc, Target, Junit;
        public readonly List<string> Tests = new();
        public TimeSpan? Timeout;
        public bool NoBuild, Download;
    }

    public sealed class TestResult
    {
        public string Instance, Name, Status = "notrun", Message;
        public double? Duration;
        public bool Selected;
        public JsonObject Json() => new()
        {
            ["instance"] = Instance, ["test"] = Name, ["status"] = Status, ["message"] = Message,
            ["duration"] = Duration is double d ? Math.Round(d, 4) : null
        };
    }

    const string LocalNetId = "127.0.0.1.1.1";
    readonly TwinCatSession tc;
    readonly Action<JsonObject> emit;
    readonly Func<bool> cancelled;
    public readonly List<TestResult> Results = new();
    public string Summary;

    public TcUnitRun(TwinCatSession tc, Action<JsonObject> emit, Func<bool> cancelled = null)
    {
        this.tc = tc; this.emit = emit ?? (_ => { }); this.cancelled = cancelled ?? (() => false);
    }

    void Step(string text) => emit(new JsonObject { ["event"] = "step", ["text"] = text });

    int Done(string status, int code, string message)
    {
        Summary = message;
        var o = new JsonObject
        {
            ["event"] = "done", ["status"] = status, ["message"] = message,
            ["passed"] = Results.Count(r => r.Status == "passed"), ["failed"] = Results.Count(r => r.Status == "failed"),
            ["skipped"] = Results.Count(r => r.Status == "skipped"), ["notRun"] = Results.Count(r => r.Status == "notrun" && r.Selected),
            ["exitCode"] = code
        };
        emit(o);
        return code;
    }

    /// <summary>Runs the tests. Returns the exit code of the command line (0 ok, 1 failed tests/build, 2 refused/wrong call, 3 infrastructure).</summary>
    public int Run(Options o)
    {
        var sw = Stopwatch.StartNew();
        // ---- tests from the code
        var sln = o.Solution ?? (tc != null ? tc.Sta.Run(() => (string)tc.RequireDte().Solution.FullName) : null);
        if (string.IsNullOrEmpty(sln) || !File.Exists(sln)) return Done("error", Cli.Usage, "Solution not found: " + sln);
        var cfg = TcUnitConfig.Load(Path.GetDirectoryName(sln));
        foreach (var e in cfg.Errors) Step($"{cfg.File}: {e}");
        var disc = TcUnitDiscovery.Discover(sln);
        var project = ChooseProject(disc, o.Plc ?? cfg.PlcProject, out var why);
        if (project == null) return Done("error", Cli.Usage, why);
        List<string> tests;
        try { tests = TcUnitDiscovery.ExpandSelectionFiles(o.Tests, Path.GetDirectoryName(sln)); }
        catch (Exception ex) { return Done("error", Cli.Usage, Msg(ex)); }
        var sel = TcUnitDiscovery.Select(project, tests);
        if (sel.Errors.Count > 0) return Done("error", Cli.Usage, string.Join("; ", sel.Errors));
        foreach (var i in project.Instances)
        {
            sel.Selected.TryGetValue(i.Path, out var set);
            bool on = sel.Selected.ContainsKey(i.Path);
            foreach (var t in i.Suite.Tests)
            {
                bool s = on && (set == null || set.Contains(t.Name));
                Results.Add(new TestResult { Instance = i.Path, Name = t.Name, Selected = s, Status = s ? "notrun" : "skipped" });
            }
        }
        var timeout = o.Timeout ?? cfg.Timeout;

        // ---- target and whitelist (the checks of tc_activate / ads_write)
        var policy = new Policy();
        var wantedRaw = o.Target ?? cfg.Target;
        string target = string.IsNullOrWhiteSpace(wantedRaw) || wantedRaw.Trim().Equals("local", StringComparison.OrdinalIgnoreCase) ? LocalNetId : wantedRaw.Trim();
        try { foreach (var tool in new[] { "tc_activate", "tc_plc_online", "ads_write" }) policy.Check(tool, () => target); }
        catch (UnauthorizedAccessException ex) { return Done("refused", Cli.Usage, ex.Message); }

        emit(new JsonObject
        {
            ["event"] = "plan", ["solution"] = sln, ["project"] = project.Name, ["treePath"] = project.TreePath, ["target"] = target,
            ["runMode"] = project.RunMode, ["expected"] = sel.ExpectedCount, ["timeoutSec"] = (int)timeout.TotalSeconds,
            ["tests"] = new JsonArray(Results.Select(r => (JsonNode)r.Json()).ToArray()),
            ["alsoRun"] = new JsonArray(sel.AlsoRun.Select(a => (JsonNode)a).ToArray()),
            ["instances"] = new JsonArray(sel.Selected.Keys.Select(k => (JsonNode)k).ToArray())
        });
        if (project.RunMode == "parallel") Step("TcUnit.RUN(): all suites run at the same time - only the overall progress is shown while running.");
        foreach (var a in sel.AlsoRun) Step("Runs as well: " + a);

        string originalTarget = null;
        bool? autostartBefore = null;
        bool started = false;
        AdsVars ads = null;
        try
        {
            // ---- is the code in the runtime the current one?
            if (tc != null) { Step("Saving all files ..."); try { tc.Sta.Run(() => { tc.RequireDte().ExecuteCommand("File.SaveAll"); return 0; }); } catch (Exception ex) { Step("Save all failed: " + Msg(ex)); } }
            var hash = SourceHash(project);
            var key = target + "|" + project.PlcProj.ToLowerInvariant();
            var (upToDate, reason) = RuntimeUpToDate(target, project, key, hash);
            if (!upToDate || o.Download)
            {
                if (tc == null) return Done("error", Cli.Infra, $"The runtime has to be updated ({reason}) - that needs the IDE (tc_attach / --attach / --solution).");
                Step($"Download needed: {(o.Download ? "requested" : reason)}");
                if (!o.NoBuild)
                {
                    Step("Building ...");
                    var (failedProjects, errors) = Build();
                    foreach (var e in errors) emit(new JsonObject { ["event"] = "compile", ["level"] = e.Level, ["text"] = e.Text, ["file"] = e.File, ["line"] = e.Line });
                    if (failedProjects > 0 || errors.Any(e => e.Level == "ERROR"))
                        return Done("build", Cli.Failed, $"Build failed: {errors.Count(e => e.Level == "ERROR")} error(s) - tests not run.");
                }
                originalTarget = SetTarget(target);
                autostartBefore = PrepareBoot(project);
                Step($"Activating the configuration on {target} and restarting TwinCAT in Run mode ...");
                try
                {
                    tc.Sta.Run(() => { dynamic sm = tc.RequireSysMan(); sm.ActivateConfiguration(); sm.StartRestartTwinCAT(); return 0; }, 300_000);
                }
                catch (Exception ex) { return Done("error", Cli.Infra, "Activation failed: " + Msg(ex) + " - are all licenses on the target activated (trial licenses are activated by a person)?"); }
                policy.Audit("tc_test_run", new JsonObject { ["solution"] = sln, ["target"] = target }, true, "activated", sw.ElapsedMilliseconds);
                if (WaitState(target, 10000, AdsState.Run, 120) != AdsState.Run)
                    return Done("error", Cli.Infra, $"TwinCAT on {target} did not reach Run mode within 120 s - missing licenses, real-time blocked (Hyper-V/VBS) or a configuration error.");
                var plc = WaitState(target, project.AmsPort, null, 60);
                if (plc is not (AdsState.Run or AdsState.Stop))
                    return Done("error", Cli.Infra, $"PLC port {project.AmsPort} on {target} does not answer (state {plc?.ToString() ?? "none"}).");
                SaveState(key, hash, target, project.AmsPort);
            }
            else Step("The runtime runs the current code - no build, no download.");

            // ---- reset, selection, start
            ads = new AdsVars(target, project.AmsPort);
            Step("Reset of the PLC (all variables to their initial values) ...");
            ads.Control(AdsState.Reset);
            if (ads.WaitState(AdsState.Stop, 15) != AdsState.Stop) return Done("error", Cli.Infra, $"PLC did not stop after the reset (state {ads.State()}).");
            Step($"Writing {sel.Writes.Count} enable bool(s) ...");
            foreach (var (path, value) in sel.Writes)
                try { ads.WriteBool(path, value); }
                catch (Exception ex) { return Done("error", Cli.Infra, $"Writing {path} := {value} failed: {Msg(ex)} - is the code in the runtime older than the files? Run with download."); }
            policy.Audit("tc_test_run", new JsonObject { ["target"] = target, ["writes"] = sel.Writes.Count, ["tests"] = string.Join(",", o.Tests) }, true, "selection written", sw.ElapsedMilliseconds);
            var runStart = DateTime.Now;
            ads.Control(AdsState.Run);
            started = true;
            Step("PLC started - TcUnit is running ...");
            var outcome = Monitor(ads, project, sel, cfg.Progress, timeout);

            // failure texts with EXP/ACT from the runtime messages (the PLC knows only MSG)
            if (outcome == "finished" && tc != null) MergeLog(runStart);
            ads.Control(AdsState.Reset);
            started = false;

            foreach (var r in Results.Where(r => r.Selected && r.Status is "running")) r.Status = "notrun";
            int failed = Results.Count(r => r.Status == "failed"), passed = Results.Count(r => r.Status == "passed");
            // every run also leaves its results for tcunit report (TcUnitReport.cs)
            try
            {
                WriteJunit(TcUnitReport.LastJunit(sln), project, outcome, quiet: true);
                TcUnitReport.SaveRun(sln, new JsonObject
                {
                    ["project"] = project.Name, ["target"] = target, ["started"] = runStart.ToString("o"), ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    ["outcome"] = outcome, ["filter"] = string.Join(",", tests), ["runMode"] = project.RunMode, ["tcagent"] = Program.ServerVersion,
                    ["results"] = new JsonArray(Results.Select(r => { var j = r.Json(); j["selected"] = r.Selected; return (JsonNode)j; }).ToArray())
                });
            }
            catch (Exception ex) { Step("Results for the report not written: " + Msg(ex)); }
            if (o.Junit != null) WriteJunit(o.Junit, project, outcome);
            var tail = string.Create(CultureInfo.InvariantCulture, $"{passed} passed, {failed} failed, {Results.Count(r => r.Status == "skipped")} skipped, {Results.Count(r => r.Selected && r.Status == "notrun")} not run ({sw.Elapsed.TotalSeconds:0.0} s)");
            return outcome switch
            {
                "finished" => Done(failed == 0 ? "passed" : "failed", failed == 0 ? Cli.Ok : Cli.Failed, (failed == 0 ? "TESTS OK: " : "TESTS FAILED: ") + tail),
                "cancelled" => Done("cancelled", Cli.Failed, "Cancelled: " + tail),
                _ => Done("timeout", Cli.Infra, $"Timeout after {timeout}: " + tail)
            };
        }
        catch (Exception ex) { return Done("error", Cli.Infra, Msg(ex)); }
        finally
        {
            if (started) try { ads.Control(AdsState.Reset); } catch { }
            ads?.Dispose();
            if (autostartBefore == false) try { tc.Sta.Run(() => { ((dynamic)tc.Item("TIPC^" + project.Name)).BootProjectAutostart = false; return 0; }); } catch { }
            if (originalTarget != null) try { tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).SetTargetNetId(originalTarget); return 0; }); } catch { }
        }
    }

    // ------------------------------------------------------------------ live progress
    sealed class Live
    {
        public string Instance;
        public int Known;                     // tests TcUnit registered so far
        public readonly Dictionary<int, TestResult> ByIndex = new();
        public readonly HashSet<int> Final = new();
        public bool Seen, Closed;
    }

    /// <summary>Follows the run until TcUnit printed its final results. Returns finished, cancelled or timeout.</summary>
    string Monitor(AdsVars ads, TcUnitDiscovery.Project project, TcUnitDiscovery.Selection sel, string progressPath, TimeSpan timeout)
    {
        var lives = sel.Selected.Keys.Select(k => new Live { Instance = k }).ToList();
        bool sequence = project.RunMode != "parallel";
        var end = DateTime.Now + timeout;
        var sw = Stopwatch.StartNew();
        string lastRunning = null, lastProgress = null;
        int idle = 0;
        while (true)
        {
            if (cancelled()) return "cancelled";
            if (DateTime.Now > end) return "timeout";
            Thread.Sleep(300);
            string current = ads.Str("GVL_TcUnit.CurrentTestSuiteBeingCalled^.InstancePath");
            var cur = current == null ? null : lives.FirstOrDefault(l => current.EndsWith("." + l.Instance, StringComparison.OrdinalIgnoreCase) || current.Equals(l.Instance, StringComparison.OrdinalIgnoreCase));
            // a short suite can start and end between two polls: every suite before the current one has run
            foreach (var l in sequence && cur != null ? lives.TakeWhile(l => l != cur).Append(cur) : cur != null ? new[] { cur } : Enumerable.Empty<Live>())
                if (!l.Seen)
                {
                    l.Seen = true;
                    emit(new JsonObject { ["event"] = "suite", ["instance"] = l.Instance });
                }
            // in sequence mode only the current suite and the ones before it change
            foreach (var l in lives.Where(l => !l.Closed && (!sequence || l.Seen)))
                Poll(ads, l);
            if (sequence && cur != null)
                foreach (var l in lives.TakeWhile(l => l != cur).Where(l => l.Seen && !l.Closed)) { Poll(ads, l); l.Closed = l.Final.Count >= l.Known; }

            bool finished = ads.Bool("GVL_TcUnit.TcUnitRunner.AdsTestResultLogger.PrintedFinalTestResults") ?? ads.Bool("GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished") ?? false;
            int known = lives.Sum(l => l.Seen || !sequence ? Math.Max(l.Known, ExpectedOf(sel, l.Instance, project)) : ExpectedOf(sel, l.Instance, project));
            int done = Results.Count(r => r.Selected && r.Status is "passed" or "failed" or "skipped" && Final(lives, r));
            if (sequence && cur != null)
            {
                var name = ads.Str("GVL_TcUnit.CurrentTestNameBeingCalled");
                var key = cur.Instance + "@" + name;
                if (!finished && name != null && key != lastRunning)
                {
                    lastRunning = key;
                    var r = Results.FirstOrDefault(x => x.Instance == cur.Instance && x.Name == name);
                    if (r != null && r.Status == "notrun") r.Status = "running";
                    double? remaining = done > 0 ? sw.Elapsed.TotalSeconds / done * Math.Max(0, known - done) : null;
                    emit(new JsonObject
                    {
                        ["event"] = "running", ["instance"] = cur.Instance, ["test"] = name, ["index"] = Math.Min(done + 1, Math.Max(known, 1)), ["total"] = known,
                        ["remainingSec"] = remaining is double rs ? Math.Round(rs) : null
                    });
                }
            }
            else if (!sequence)
            {
                var key = $"{done}/{known}";
                if (key != lastRunning)
                {
                    lastRunning = key;
                    emit(new JsonObject { ["event"] = "running", ["index"] = done, ["total"] = known, ["remainingSec"] = done > 0 ? Math.Round(sw.Elapsed.TotalSeconds / done * Math.Max(0, known - done)) : null });
                }
            }
            if (progressPath != null)
            {
                var pj = ProgressBlock(ads, progressPath);
                var text = pj?.ToJsonString();
                if (text != null && text != lastProgress) { lastProgress = text; emit(pj); }
            }
            if (finished)
            {
                foreach (var l in lives) Poll(ads, l);
                // let the last messages of the ADS log queue out (TcUnit sends them with a pause in between)
                if (++idle >= 3) return "finished";
            }
        }
    }

    static int ExpectedOf(TcUnitDiscovery.Selection sel, string instance, TcUnitDiscovery.Project p)
    {
        if (sel.Selected.TryGetValue(instance, out var set) && set != null) return set.Count;
        return p.Instances.FirstOrDefault(i => i.Path == instance)?.Suite.Tests.Count ?? 0;
    }

    bool Final(List<Live> lives, TestResult r) => lives.Any(l => l.Instance == r.Instance && l.ByIndex.Any(kv => kv.Value == r && l.Final.Contains(kv.Key)));

    /// <summary>Reads TcUnit's test table of a suite instance and reports every change of a test.</summary>
    void Poll(AdsVars ads, Live l)
    {
        var n = ads.UInt(l.Instance + ".NumberOfTests");
        if (n == null) return;
        if (n > l.Known) l.Known = n.Value;
        for (int i = 1; i <= l.Known; i++)
        {
            if (l.Final.Contains(i)) continue;
            var p = $"{l.Instance}.Tests[{i}]";
            if (!l.ByIndex.TryGetValue(i, out var r))
            {
                var name = ads.Str(p + ".TestName");
                if (string.IsNullOrEmpty(name)) continue;
                r = Results.FirstOrDefault(x => x.Instance == l.Instance && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (r == null) { r = new TestResult { Instance = l.Instance, Name = name, Selected = true }; Results.Add(r); }   // known only at run time
                r.Selected = true;
                l.ByIndex[i] = r;
            }
            bool failedNow = ads.Bool(p + ".TestIsFailed") == true;
            bool finished = ads.Bool(p + ".TestIsFinished") == true;
            bool skipped = ads.Bool(p + ".TestIsSkipped") == true;
            if (!finished && !skipped)
            {
                if (failedNow && r.Status != "failed") { r.Status = "failed"; r.Message = ads.Str(p + ".AssertionMessage"); emit(Event(r)); }
                continue;
            }
            r.Duration = ads.LReal(p + ".Duration");
            r.Status = skipped ? "skipped" : failedNow ? "failed" : "passed";
            if (failedNow) r.Message ??= ads.Str(p + ".AssertionMessage");
            l.Final.Add(i);
            emit(Event(r));
        }
    }

    static JsonObject Event(TestResult r) { var j = r.Json(); j["event"] = "result"; return j; }

    /// <summary>Values of the progress block (config "progress"), e.g. GVL_Progress.Progress with Total, Current, Done, Test, Remaining, Finished.</summary>
    static JsonObject ProgressBlock(AdsVars ads, string path)
    {
        var o = new JsonObject { ["event"] = "progress", ["path"] = path };
        bool any = false;
        foreach (var f in new[] { "Total", "Current", "Done", "Test", "Remaining", "Finished", "Percent" })
        {
            var v = ads.Any(path + "." + f);
            if (v == null) continue;
            o[f.ToLowerInvariant()] = v; any = true;
        }
        return any ? o : null;
    }

    // ------------------------------------------------------------------ results from the runtime messages
    void MergeLog(DateTime since)
    {
        TcUnitResults log = null;
        for (int i = 0; i < 15; i++)
        {
            try
            {
                var lines = tc.Sta.Run(() => TwinCatSession.Chronological(tc.ReadErrorList(20000).runtime
                    .Where(e => e.Stamp == null || e.Stamp >= since.AddSeconds(-2)).ToList()).Select(e => e.Text).ToList(), 60_000);
                log = TcUnitResults.Parse(lines);
                if (log.Finished && Results.Where(r => r.Status == "failed").All(r => Failure(log, r) != null)) break;
            }
            catch { }
            Thread.Sleep(1000);
        }
        if (log == null) return;
        foreach (var r in Results.Where(r => r.Status == "failed"))
            if (Failure(log, r) is string f && f != r.Message) { r.Message = f; emit(Event(r)); }
    }

    // the error list may still hold the same message of an earlier run (entries without time stamp): each line once
    static string Failure(TcUnitResults log, TestResult r) =>
        log.Suites.Where(s => s.Name.Equals(r.Instance, StringComparison.OrdinalIgnoreCase) || s.Name.EndsWith("." + r.Instance, StringComparison.OrdinalIgnoreCase))
           .SelectMany(s => s.Cases).FirstOrDefault(c => c.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && c.Failure != null)?.Failure is string f
            ? string.Join("\n", f.Split('\n').Distinct()) : null;

    void WriteJunit(string path, TcUnitDiscovery.Project p, string outcome, bool quiet = false)
    {
        var r = new TcUnitResults { Finished = outcome == "finished" };
        foreach (var g in Results.Where(x => x.Selected || x.Status == "skipped").GroupBy(x => x.Instance))
        {
            var s = new TcUnitResults.Suite { Name = g.Key };
            foreach (var x in g)
                s.Cases.Add(new TcUnitResults.Case
                {
                    Suite = g.Key, Name = x.Name, Duration = x.Duration,
                    Status = !x.Selected ? "SKIPPED (switched off for this run)"
                           : x.Status switch { "passed" => "PASS", "failed" => "FAIL", "skipped" => "SKIPPED", _ => "SKIPPED (not run)" },
                    Failure = x.Status == "failed" ? x.Message ?? "failed" : null
                });
            r.Suites.Add(s);
        }
        var suites = JUnit.TestSuites(r, p.Name).ToList();
        if (outcome != "finished") suites.Add(JUnit.ErrorSuite("TcUnit", "run", outcome == "cancelled" ? "The run was cancelled." : "TcUnit did not finish in time."));
        JUnit.Write(path, "TcAgent tcunit", suites);
        if (!quiet) Step("JUnit: " + Path.GetFullPath(path));
    }

    // ------------------------------------------------------------------ IDE: build, boot project, target
    (int failedProjects, List<TwinCatSession.ErrorEntry> errors) Build() => tc.Sta.Run(() =>
    {
        var sb = tc.RequireDte().Solution.SolutionBuild;
        sb.Build(true);
        return (sb.LastBuildInfo, tc.ReadErrorList(5000).compile.Where(e => e.Level is "ERROR" or "WARN").ToList());
    }, 1_800_000);

    /// <summary>Boot project with autostart for the PLC project (activation loads it). Returns the autostart value before.</summary>
    bool? PrepareBoot(TcUnitDiscovery.Project p) => tc.Sta.Run(() =>
    {
        dynamic d = tc.Item("TIPC^" + p.Name);
        bool? before = null;
        try { before = (bool)d.BootProjectAutostart; } catch { }
        d.BootProjectAutostart = true;
        d.GenerateBootProject(true);
        return before;
    }, 300_000);

    /// <summary>Sets the target system of the project if it differs. Returns the original one (to restore) or null.</summary>
    string SetTarget(string target)
    {
        var original = tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId());
        var policy = new Policy();
        if (string.Equals(original, target, StringComparison.OrdinalIgnoreCase) || (policy.IsLocal(original) && policy.IsLocal(target))) return null;
        tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).SetTargetNetId(target); return 0; });
        Step($"Target system of the project: {original} -> {target} (restored at the end)");
        return original;
    }

    static TcUnitDiscovery.Project ChooseProject(TcUnitDiscovery.Result d, string wanted, out string why)
    {
        why = null;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var w = wanted.Trim();
            var p = d.Projects.FirstOrDefault(x => x.Name.Equals(w, StringComparison.OrdinalIgnoreCase) || x.TreePath.Equals(w, StringComparison.OrdinalIgnoreCase)
                                                   || ("TIPC^" + x.Name).Equals(w, StringComparison.OrdinalIgnoreCase));
            if (p == null) why = $"PLC project '{w}' not found - the solution has: {string.Join(", ", d.Projects.Select(x => x.Name))}";
            return p;
        }
        var withTests = d.Projects.Where(x => x.Instances.Count > 0).ToList();
        if (withTests.Count == 1) return withTests[0];
        why = withTests.Count == 0
            ? "No PLC project with TcUnit test suite instances (FB EXTENDS TcUnit.FB_TestSuite, instance in a PROGRAM or GVL)."
            : $"Several PLC projects with tests ({string.Join(", ", withTests.Select(x => x.Name))}) - choose one with --plc or \"plcProject\" in .tcagent\\tcunit.json.";
        return null;
    }

    // ------------------------------------------------------------------ is the runtime up to date?
    static readonly string StateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "tcunit-state.json");

    /// <summary>Hash of the .plcproj and all its code files - what a build of the project depends on.</summary>
    static string SourceHash(TcUnitDiscovery.Project p)
    {
        using var sha = SHA256.Create();
        var dir = Path.GetDirectoryName(p.PlcProj)!;
        var files = new List<string> { p.PlcProj };
        files.AddRange(Directory.GetFiles(dir, "*.Tc*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "_") && Path.GetExtension(f).ToLowerInvariant() is ".tcpou" or ".tcgvl" or ".tcdut" or ".tcio" or ".tctto")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        foreach (var f in files)
        {
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, f).ToLowerInvariant());
            sha.TransformBlock(name, 0, name.Length, null, 0);
            var data = File.ReadAllBytes(f);
            sha.TransformBlock(data, 0, data.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    static (bool, string) RuntimeUpToDate(string target, TcUnitDiscovery.Project p, string key, string hash)
    {
        var sys = ReadState(target, 10000);
        if (sys == null) return (false, $"no TwinCAT runtime answers on {target}");
        if (sys != AdsState.Run) return (false, $"TwinCAT is in {sys} mode");
        var plc = ReadState(target, p.AmsPort);
        if (plc is not (AdsState.Run or AdsState.Stop)) return (false, $"no PLC program on port {p.AmsPort} ({plc?.ToString() ?? "no answer"})");
        using var ads = new AdsVars(target, p.AmsPort);
        var name = ads.Str("TwinCAT_SystemInfoVarList._AppInfo.ProjectName");
        if (!string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, Path.GetFileNameWithoutExtension(p.PlcProj), StringComparison.OrdinalIgnoreCase))
            return (false, $"the runtime runs '{name}'");
        var s = LoadState()[key] as JsonObject;
        if (s == null) return (false, "TcAgent has not downloaded this project yet");
        if ((string)s["hash"] != hash) return (false, "the code changed since the last download");
        if ((uint?)s["appTimestamp"] != ads.UDInt("TwinCAT_SystemInfoVarList._AppInfo.AppTimestamp") || (uint?)s["onlineChanges"] != ads.UDInt("TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt"))
            return (false, "another program version was downloaded in the meantime");
        return (true, null);
    }

    static JsonObject LoadState()
    {
        try { return JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? new JsonObject(); } catch { return new JsonObject(); }
    }

    static void SaveState(string key, string hash, string target, int port)
    {
        try
        {
            using var ads = new AdsVars(target, port);
            var s = LoadState();
            s[key] = new JsonObject
            {
                ["hash"] = hash, ["appTimestamp"] = ads.UDInt("TwinCAT_SystemInfoVarList._AppInfo.AppTimestamp"),
                ["onlineChanges"] = ads.UDInt("TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt"), ["time"] = DateTime.Now.ToString("o")
            };
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ------------------------------------------------------------------ ADS
    static AmsNetId Net(string netId) => netId == LocalNetId ? AmsNetId.Local : AmsNetId.Parse(netId);

    static AdsState? ReadState(string netId, int port)
    {
        try { using var c = new AdsClient(); c.Connect(Net(netId), port); return c.ReadState().AdsState; } catch { return null; }
    }

    /// <summary>Waits for a state (want == null: any state); after a restart the old state can be read for a moment.</summary>
    static AdsState? WaitState(string netId, int port, AdsState? want, int seconds)
    {
        Thread.Sleep(2000);
        var end = DateTime.Now.AddSeconds(seconds);
        AdsState? s = null;
        while (DateTime.Now < end)
        {
            s = ReadState(netId, port) ?? s;
            if (want == null ? s is not (null or AdsState.Invalid or AdsState.Init) : s == want) return s;
            Thread.Sleep(1000);
        }
        return s;
    }

    /// <summary>Variables by name (ADS handles, cached); a missing symbol reads as null.</summary>
    sealed class AdsVars : IDisposable
    {
        readonly AdsClient c = new();
        readonly Dictionary<string, uint?> handles = new(StringComparer.OrdinalIgnoreCase);

        public AdsVars(string netId, int port) => c.Connect(Net(netId), port);

        uint? Handle(string path)
        {
            if (handles.TryGetValue(path, out var h)) return h;
            try { h = c.CreateVariableHandle(path); } catch { h = null; }
            return handles[path] = h;
        }

        T? Read<T>(string path) where T : struct
        {
            var h = Handle(path);
            if (h == null) return null;
            try { return (T)c.ReadAny(h.Value, typeof(T)); } catch { return null; }
        }

        public bool? Bool(string path) => Read<bool>(path);
        public int? UInt(string path) => Read<ushort>(path) is ushort u ? u : null;
        public uint? UDInt(string path) => Read<uint>(path);
        public double? LReal(string path) => Read<double>(path);

        public string Str(string path)
        {
            var h = Handle(path);
            if (h == null) return null;
            try { return c.ReadAnyString(h.Value, 255, Encoding.Latin1); } catch { return null; }
        }

        /// <summary>Value of unknown type as JSON (progress block): via the symbol information.</summary>
        public JsonNode Any(string path)
        {
            try
            {
                var info = c.ReadSymbol(path);
                if (info == null) return null;
                var v = c.ReadValue(info);
                return v switch { bool b => b, string s => s, IConvertible cv when v is not char => JsonValue.Create(Convert.ToDouble(cv, CultureInfo.InvariantCulture)), _ => v?.ToString() };
            }
            catch { return null; }
        }

        public void WriteBool(string path, bool value)
        {
            var h = Handle(path) ?? throw new Exception("symbol not found");
            c.WriteAny(h, value);
        }

        public AdsState? State() { try { return c.ReadState().AdsState; } catch { return null; } }

        public void Control(AdsState s) => c.WriteControl(new StateInfo(s, c.ReadState().DeviceState));

        public AdsState? WaitState(AdsState want, int seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            AdsState? s = null;
            while (DateTime.Now < end) { s = State(); if (s == want) return s; Thread.Sleep(200); }
            return s;
        }

        public void Dispose()
        {
            foreach (var h in handles.Values.Where(x => x != null)) try { c.DeleteVariableHandle(h.Value); } catch { }
            c.Dispose();
        }
    }

    static string Msg(Exception ex) => ex.InnerException?.Message ?? ex.Message;
}
