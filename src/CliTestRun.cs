// TcAgent command line: test runs with clean-up for CI pipelines (part of Cli, see Cli.cs).
//
//   TcAgent.exe sil --solution <sln> [--target local|<NetId>] ...    software in the loop: the runtime of this PC
//                                                                    (local XAR, or a usermode runtime allowed via TCAGENT_ALLOW_TARGETS)
//   TcAgent.exe hil --solution <sln> --target <NetId> [--allow <NetId>] [--scope <cfg> --out <file>] ...
//                                                                    hardware in the loop: a remote test PLC, I/O stays enabled
//
// Flow: whitelist check (Policy, like tc_activate/tc_plc_online/tc_runtime_mode) -> ADS route / runtime reachable ->
// target set (the original is restored) -> build -> boot project with autostart -> activate + restart in Run mode ->
// wait for Run mode -> optional Scope recording -> wait for TcUnit (if the PLC does not start: login + start like
// tc_plc_online) -> JUnit -> clean-up: stop the Scope, logout, runtime to Config mode (stops the PLC, not with
// --keep-running), restore target / I/O devices / autostart in an attached IDE.
// A pipeline runs unattended: there is no confirmation dialog, so a remote target needs an explicit allowance
// (TCAGENT_ALLOW_TARGETS or --allow in the pipeline definition) - otherwise the run is refused.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TCatSysManagerLib;
using TwinCAT.Ads;

namespace TcAgent;

public static partial class Cli
{
    const int SystemServicePort = 10000;
    const string LocalNetId = "127.0.0.1.1.1";

    /// <summary>Argument checks of sil/hil (called by ParseArgs; exceptions = exit code 2).</summary>
    static void ValidateTestRunArgs(Options o)
    {
        static bool IsNetId(string s) => Regex.IsMatch(s ?? "", @"^\d{1,3}(\.\d{1,3}){5}$");
        if (o.Allow.Count > 0 && o.Command != "hil")
            throw new Exception("--allow is only for hil (sil and test use TCAGENT_ALLOW_TARGETS)");
        if (o.KeepRunning && o.Command is not ("sil" or "hil"))
            throw new Exception("--keep-running is only for sil and hil");
        if (o.Command is not ("sil" or "hil")) return;
        if (o.Command == "hil" && string.IsNullOrEmpty(o.Target))
            throw new Exception("hil needs --target <AmsNetId of the test PLC>");
        if (o.Command == "hil" && o.Target.Equals("local", StringComparison.OrdinalIgnoreCase))
            throw new Exception("hil runs on a remote test PLC - for the runtime of this PC use sil");
        if (o.Target != null && !o.Target.Equals("local", StringComparison.OrdinalIgnoreCase) && !IsNetId(o.Target))
            throw new Exception($"--target: AmsNetId like 5.12.34.56.1.1 or 'local' expected, not '{o.Target}'");
        foreach (var bad in o.Allow.Where(x => !x.Equals("local", StringComparison.OrdinalIgnoreCase) && !IsNetId(x)))
            throw new Exception($"--allow: AmsNetId like 5.12.34.56.1.1 expected, not '{bad}'");
    }

    /// <summary>sil / hil: like test, plus route and Run mode checks, optional Scope recording and a clean-up that always runs.</summary>
    static int TestRun(Options o, TwinCatSession tc, bool ownIde, bool hil)
    {
        string kind = hil ? "hil" : "sil", junitName = "TcAgent " + kind;
        var policy = new Policy(hil ? o.Allow : Enumerable.Empty<string>());
        var suites = new List<XElement>();
        var sw = Stopwatch.StartNew();

        // 1) target and whitelist - the same checks the MCP tools tc_activate, tc_plc_online and tc_runtime_mode make
        string wanted = string.IsNullOrEmpty(o.Target) || o.Target.Equals("local", StringComparison.OrdinalIgnoreCase) ? LocalNetId : o.Target.Trim();
        if (hil && policy.IsLocal(wanted))
            return Refused(o, "target", $"{wanted} is this PC - hil runs on a remote test PLC, use sil for the local runtime.");
        try
        {
            foreach (var tool in new[] { "tc_activate", "tc_plc_online", "tc_runtime_mode" }) policy.Check(tool, () => wanted);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Refused(o, "target", ex.Message + (hil
                ? $" A pipeline cannot ask for confirmation: allow the test PLC explicitly with --allow {wanted} or TCAGENT_ALLOW_TARGETS."
                : " sil uses TCAGENT_ALLOW_TARGETS (default: local)."));
        }
        if (!hil && !policy.IsLocal(wanted))
            Say($"Note: {wanted} is not 127.0.0.1.1.1/the own AmsNetId - used as a separate runtime on this PC (e.g. usermode runtime).");

        // 2) runtime reachable via ADS (route) - before anything is changed
        var tools = new ToolRegistry(tc);
        var (stateBefore, probeError) = ReadSystemState(wanted);
        if (stateBefore == null)
            return InfraError(o, "route", RouteProblem(tools, wanted, policy.IsLocal(wanted), probeError));
        Say($"Target {wanted}: TwinCAT system service reachable, state {stateBefore}");

        // 3) set the target (restored at the end)
        string original = tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId());
        bool sameTarget = string.Equals(original, wanted, StringComparison.OrdinalIgnoreCase) || (policy.IsLocal(original) && policy.IsLocal(wanted));
        string target = sameTarget ? original : wanted;
        List<string> disabledIo = new(), loggedIn = new();
        List<(string Name, string Path, bool? AutostartBefore)> boot = new();
        bool activationTried = false, scopeRunning = false;
        string scopeFile = null;
        try
        {
            if (!sameTarget)
            {
                tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).SetTargetNetId(wanted); return 0; });
                Say($"Target system of the project: {original} -> {wanted} (restored at the end)");
            }
            Say($"{kind.ToUpperInvariant()} target: {target}");

            // 4) I/O devices: sil disables them in a Visual Studio started by TcAgent (no hardware), hil keeps them (that is the point)
            if (o.DisableIo ?? (!hil && ownIde))
            {
                disabledIo = DisableIoDevices(tc);
                Say($"I/O devices disabled for the test: {disabledIo.Count}");
            }

            // 5) build
            if (!o.NoBuild && BuildBeforeTest(o, tc, sw, junitName) is int buildRc) return buildRc;

            // 6) boot project with autostart
            boot = PrepareBootProjects(o, tc);
            if (boot.Count == 0) return InfraError(o, "plc", "No matching PLC project" + (o.Plc.Count > 0 ? " for --plc " + string.Join(",", o.Plc) : "") + ".");
            Say("PLC: " + string.Join(", ", boot.Select(b => b.Name)) + (o.NoBootProject ? "" : " (boot project with autostart)"));

            // 7) activate and restart in Run mode, then wait until the runtime really is in Run mode
            var started = DateTime.Now;
            activationTried = true;
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
            policy.Audit("cli " + kind, new JsonObject { ["solution"] = SolutionName(tc), ["target"] = target }, true, "activated", (long)sw.Elapsed.TotalMilliseconds);
            Say("Configuration activated, TwinCAT restarting in Run mode ...");
            var runState = WaitSystemState(target, AdsState.Run, 120);
            if (runState != AdsState.Run)
                return InfraError(o, "runmode", $"TwinCAT on {target} did not reach Run mode within 120 s (state {runState?.ToString() ?? "not readable"}) - " +
                    "missing licenses (trial licenses must be activated by a person), real-time blocked (Hyper-V/VBS, no isolated core) or a configuration error.");
            Say("TwinCAT in Run mode - waiting for TcUnit ...");

            // 8) optional Scope recording during the test run
            if (o.ScopePath != null || o.Out != null)
            {
                scopeFile = Path.GetFullPath(o.Out ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.Junit ?? "x")) ?? ".", $"scope-{kind}.csv"));
                try { Say(ScopeCall(tools, o, "record_start")); scopeRunning = true; }
                catch (Exception ex) { Say("SCOPE NOT STARTED: " + Msg(ex)); scopeFile = null; }
            }

            // 9) wait for TcUnit; if the PLC shows no sign of life 30 s after Run mode: login + start (as tc_plc_online)
            var runAt = DateTime.Now;
            bool startTried = false;
            void Tick(TcUnitResults r)
            {
                if (startTried || r.Lines.Count > 0 || (DateTime.Now - runAt).TotalSeconds < 30) return;
                startTried = true;
                Say("No TcUnit output 30 s after Run mode - starting the PLC with login + start (like tc_plc_online)");
                loggedIn = PlcLoginStart(tc, boot.Select(b => b.Name).ToList());
            }
            void BeforeReport()
            {
                if (!scopeRunning) return;
                scopeRunning = false;
                suites.Add(ScopeStopExport(tools, o, scopeFile));
            }
            return WaitForTcUnit(o, tc, started, suites, junitName, Tick, BeforeReport);
        }
        finally
        {
            CleanUp(tc, tools, o, ownIde, target, original, sameTarget, activationTried, scopeRunning, loggedIn, disabledIo, boot);
        }
    }

    /// <summary>Always runs at the end of sil/hil, every step on its own (one failing step does not skip the others).</summary>
    static void CleanUp(TwinCatSession tc, ToolRegistry tools, Options o, bool ownIde, string target, string original, bool sameTarget,
                        bool activationTried, bool scopeRunning, List<string> loggedIn, List<string> disabledIo,
                        List<(string Name, string Path, bool? AutostartBefore)> boot)
    {
        void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Say($"Clean-up: {what} failed: {Msg(ex)}"); }
        }
        if (scopeRunning) Step("stopping the Scope recording", () => Say(ScopeCall(tools, o, "record_stop")));
        foreach (var path in loggedIn)
            Step("logout " + path, () => PlcCommand(tc, path, "LogoutCmd"));
        if (activationTried && !o.KeepRunning)
            Step("switching the runtime to Config mode", () => Say($"Clean-up: {target} {ToConfigMode(target)} (PLC stopped)"));
        else if (activationTried) Say($"Clean-up: --keep-running - TwinCAT on {target} stays in Run mode with the test configuration.");
        if (!ownIde)
        {
            // an attached IDE keeps its project - undo what the run changed (a Visual Studio started by TcAgent is closed without saving)
            if (disabledIo.Count > 0)
                Step("re-enabling the I/O devices", () => tc.Sta.Run(() =>
                {
                    foreach (var p in disabledIo) tc.Item(p).Disabled = DISABLED_STATE.SMDS_NOT_DISABLED;
                    return 0;
                }));
            foreach (var b in boot.Where(x => x.AutostartBefore == false))
                Step("restoring boot project autostart of " + b.Name, () => tc.Sta.Run(() => { ((dynamic)tc.Item(b.Path)).BootProjectAutostart = false; return 0; }));
        }
        if (!sameTarget)
            Step("restoring the target system", () =>
            {
                tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).SetTargetNetId(original); return 0; });
                Say($"Clean-up: target system of the project restored to {original}");
            });
    }

    /// <summary>Exit code 2: the call is not allowed as given (whitelist, mode) - retrying does not help.</summary>
    static int Refused(Options o, string step, string message)
    {
        InfraError(o, step, "REFUSED: " + message);
        return Usage;
    }

    // ------------------------------------------------------------------ runtime state (ADS, system service port 10000)
    static AmsNetId Net(string netId) => netId == LocalNetId ? AmsNetId.Local : AmsNetId.Parse(netId);

    static (AdsState? State, string Error) ReadSystemState(string netId)
    {
        try
        {
            using var c = new AdsClient();
            c.Connect(Net(netId), SystemServicePort);
            return (c.ReadState().AdsState, null);
        }
        catch (Exception ex) { return (null, Msg(ex)); }
    }

    /// <summary>Waits for the system service state (after StartRestartTwinCAT the old state can be read for a moment).</summary>
    static AdsState? WaitSystemState(string netId, AdsState want, int seconds)
    {
        Thread.Sleep(3000);
        var end = DateTime.Now.AddSeconds(seconds);
        AdsState? s = null;
        while (DateTime.Now < end)
        {
            s = ReadSystemState(netId).State ?? s;
            if (s == want) return s;
            Thread.Sleep(1000);
        }
        return s;
    }

    /// <summary>Like tc_runtime_mode mode=config: stops the PLC and frees the runtime.</summary>
    static string ToConfigMode(string netId)
    {
        using var c = new AdsClient();
        c.Connect(Net(netId), SystemServicePort);
        var before = c.ReadState().AdsState;
        if (before == AdsState.Config) return "already in Config mode";
        c.WriteControl(new StateInfo(AdsState.Reconfig, 0));
        var end = DateTime.Now.AddSeconds(30);
        var now = before;
        while (DateTime.Now < end)
        {
            Thread.Sleep(500);
            try { now = c.ReadState().AdsState; if (now == AdsState.Config) break; } catch { /* service is restarting */ }
        }
        return $"{before} -> {now}";
    }

    /// <summary>Clear error text when the target does not answer: no runtime on this PC, or for a remote PLC whether a
    /// broadcast search (tc_route_search) finds it - then only the route is missing.</summary>
    static string RouteProblem(ToolRegistry tools, string netId, bool local, string adsError)
    {
        if (local)
            return $"No TwinCAT runtime answers on this PC ({netId}, ADS port {SystemServicePort}: {adsError}). XAR not installed, TwinCAT system " +
                   "service stopped, or the usermode runtime is not started (pass its AmsNetId with --target and allow it in TCAGENT_ALLOW_TARGETS).";
        string search;
        try
        {
            var r = tools.Call("tc_route_search", new JsonObject());
            search = r["content"]?[0]?["text"]?.ToString() ?? "";
        }
        catch (Exception ex) { search = "broadcast search failed: " + Msg(ex); }
        bool found = Regex.IsMatch(search, @"NetId=" + Regex.Escape(netId) + @"(\s|$)");
        return found
            ? $"Target {netId} answers the broadcast search but not via ADS ({adsError}): the ADS route from this PC is missing or broken. " +
              "Add it once on the build machine (TwinCAT router 'Add Route...', or tc_route_add from an agent session with the target's credentials) - the pipeline never adds routes.\n" + search
            : $"Target {netId} is not reachable via ADS ({adsError}) and was not found by a broadcast search: switched off, other subnet, " +
              "firewall (UDP 48899 / TCP 48898) or wrong AmsNetId. Check with tc_route_search host=<ip>, then add the route once.\n" + search;
    }

    // ------------------------------------------------------------------ PLC online commands (the XML tc_plc_online sends)
    static void PlcCommand(TwinCatSession tc, string plcProjectPath, string tag)
    {
        var xml = $"<TreeItem><IECProjectDef><OnlineSettings><Commands><{tag}>true</{tag}></Commands></OnlineSettings></IECProjectDef></TreeItem>";
        tc.Sta.Run(() => { ((dynamic)tc.Item(plcProjectPath)).ConsumeXml(xml); return 0; }, 120_000);
    }

    /// <summary>Login + start for the PLC projects (inner project items) of the given TIPC names. Returns the logged-in paths.</summary>
    static List<string> PlcLoginStart(TwinCatSession tc, List<string> plcNames)
    {
        var paths = tc.Sta.Run(() => tc.PlcProjects()
            .Where(p => plcNames.Contains(p.PathName.Split('^').Skip(1).FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase))
            .Select(p => p.PathName).ToList());
        var done = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                PlcCommand(tc, path, "LoginCmd");
                done.Add(path);
                Thread.Sleep(3000);
                PlcCommand(tc, path, "StartCmd");
                Thread.Sleep(1000);
                Say($"PLC {path}: login + start sent");
            }
            catch (Exception ex) { Say($"PLC {path}: login/start failed: {Msg(ex)}"); }
        }
        return done;
    }

    // ------------------------------------------------------------------ Scope during the run
    static string ScopeCall(ToolRegistry tools, Options o, string action, JsonObject extra = null)
    {
        var args = new JsonObject { ["action"] = action };
        if (o.ScopePath != null) args["path"] = o.ScopePath;
        if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value?.DeepClone();
        var r = tools.Call("tc_scope", args);
        var text = r["content"]?[0]?["text"]?.ToString() ?? "";
        if (r["isError"]?.GetValue<bool>() == true) throw new Exception(text);
        return text;
    }

    /// <summary>Stops the recording and exports it. Returns a JUnit suite "Scope recording": passed with the file as
    /// attachment ([[ATTACHMENT|file]], Jenkins/GitLab), or skipped with the reason - a Scope problem does not fail the tests.</summary>
    static XElement ScopeStopExport(ToolRegistry tools, Options o, string file)
    {
        string error = null;
        try
        {
            Say(ScopeCall(tools, o, "record_stop"));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Say(ScopeCall(tools, o, "export", new JsonObject { ["file"] = file }));
            for (int i = 0; i < 60 && !File.Exists(file); i++) Thread.Sleep(500);   // the Scope may write the export asynchronously
            if (!File.Exists(file)) error = "no file written: " + file;
        }
        catch (Exception ex) { error = Msg(ex); }
        Say(error == null ? $"SCOPE OK: {file}" : $"SCOPE FAILED: {error}");
        var tcase = new XElement("testcase", new XAttribute("name", o.ScopePath ?? "scope"), new XAttribute("classname", "Scope recording"));
        if (error == null) tcase.Add(new XElement("system-out", $"[[ATTACHMENT|{file}]]"));
        else tcase.Add(new XElement("skipped", new XAttribute("message", "Scope recording failed: " + error)));
        return new XElement("testsuite",
            new XAttribute("name", "Scope recording"), new XAttribute("tests", 1), new XAttribute("failures", 0), new XAttribute("errors", 0),
            new XAttribute("skipped", error == null ? 0 : 1), new XAttribute("time", "0"),
            new XAttribute("timestamp", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)),
            new XAttribute("hostname", Environment.MachineName),
            new XElement("properties", new XElement("property", new XAttribute("name", "scope.file"), new XAttribute("value", file ?? ""))),
            tcase);
    }
}
