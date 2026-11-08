// tc_advisor (Advisor.cs): an Advisor run reads and advises, it changes nothing. action=start makes this session read only
// until action=finish - every tool that would change the project or the runtime is refused (CallTool); the advice itself is
// kept with record (Advisor.cs), rated by people, and its order is what a person later sends to an agent.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    string advisorRun;          // id of the running Advisor run of this session (null = none)
    int advisorRecorded;

    /// <summary>A started Advisor run, or a server started for one (TcAgent.exe advise sets TCAGENT_ADVISOR).</summary>
    bool AdvisorActive => advisorRun != null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TCAGENT_ADVISOR"));

    void RegisterAdvisor()
    {
        Add("tc_advisor", "Advisor: look, advise, hand over an order - never change anything. action=start (kind, trigger) makes this session " +
                          "read only until action=finish: tools that change the project or the runtime are refused. record keeps one piece of " +
                          "advice (finding, evidence, proposal, order = the prompt a person sends to an agent to act on it). list shows the advice " +
                          "of the project, rate records a person's rating, stats the hit rate per kind of task. Live, at the running plant: watch_list shows the " +
                          "fault signals of the project (.tcagent\\advisor-watch.json) and of the plugins, watch_add / watch_remove change them (after the user " +
                          "agreed), watch_check looks now: runtime and PLC state, cycle time, the signals that fire with their details.",
            Schema(Req(P("action", "string", "start, record, finish, list, rate, done (the finding is dealt with), stats; Live: watch_list, watch_add, watch_remove, watch_check")),
                   P("symbol", "string", "watch_add/remove: the variable that signals a fault, e.g. MAIN.fbStation.bError"),
                   P("when", "string", "watch_add: true, false, == n, != n, > n, >= n, < n, <= n, & n (bit mask) or unchanged <n>s (default true)"),
                   P("text", "string", "watch_add: what the fault means"),
                   P("details", "string", "watch_add: variables to read as well when it fires, comma-separated"),
                   P("port", "integer", "watch_add: ADS port of the PLC (default 851)"),
                   P("netId", "string", "watch_check: AmsNetId (default: the target of the project)"),
                   P("kind", "string", "start/record: review, diagnose, cause or code (default review)"),
                   P("trigger", "string", "start/record: chat, nightly, commit, pipeline (default chat)"),
                   P("finding", "string", "record: one sentence - what you found"),
                   P("evidence", "string", "record: quotes from tool outputs with their place (object, line, variable, value), one per line"),
                   P("proposal", "string", "record: what should be done"),
                   P("diff", "string", "record: optional code change as a diff - never applied"),
                   P("order", "string", "record: the prompt a person sends to an agent to act on the finding, self-contained"),
                   P("workflow", "string", "record: the workflow for the order, e.g. bug-fix or new-feature"),
                   P("confidence", "string", "record: high, medium or low"),
                   P("why", "string", "record: one sentence why this confidence"),
                   P("id", "string", "rate: id of the advice (AD-...)"),
                   P("rating", "string", "rate: right, partly, wrong or n/a"),
                   P("comment", "string", "rate: optional"),
                   P("open", "boolean", "list: only advice not rated yet"),
                   P("max", "integer", "list: how many (default 20)")),
            a =>
            {
                var dir = SolutionDir();
                switch ((Str(a, "action") ?? "").Trim().ToLowerInvariant())
                {
                    case "start":
                        advisorRun = "R-" + Guid.NewGuid().ToString("N")[..6];
                        advisorRecorded = 0;
                        Log.Info($"advisor run {advisorRun} started");
                        return $"Advisor run {advisorRun} started ({Str(a, "kind") ?? "review"}, {Str(a, "trigger") ?? "chat"}). This session is read only now: " +
                               "read, find, prove with tool outputs. For each finding call tc_advisor action=record with an order a person can send to an agent. " +
                               "End with tc_advisor action=finish.";
                    case "record":
                        try
                        {
                            var it = Advisor.Record(dir, new Advisor.Item
                            {
                                Kind = Str(a, "kind"), Trigger = Str(a, "trigger") ?? Environment.GetEnvironmentVariable("TCAGENT_ADVISOR"),
                                Finding = Str(a, "finding"), Proposal = Str(a, "proposal"), Diff = Str(a, "diff"), Order = Str(a, "order"),
                                Workflow = Str(a, "workflow"), Confidence = Str(a, "confidence"), Why = Str(a, "why"), Run = advisorRun,
                                Evidence = (Str(a, "evidence") ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
                            });
                            advisorRecorded++;
                            return $"recorded {it.Id} in {Path.GetRelativePath(dir, Advisor.Folder(dir))}";
                        }
                        catch (ArgumentException ex) { throw new Exception("NOT recorded: " + ex.Message); }
                    case "finish":
                    {
                        var r = advisorRun; var n = advisorRecorded;
                        advisorRun = null; advisorRecorded = 0;
                        return r == null ? "No Advisor run was started." : $"Advisor run {r} finished: {n} advice recorded. The session may change things again.";
                    }
                    case "list":
                        return Advisor.List(Advisor.Load(dir), Bool(a, "open", false), Int(a, "max", 20));
                    case "rate":
                        try { Advisor.Rate(dir, Str(a, "id"), Str(a, "rating"), Environment.UserName, Str(a, "comment")); }
                        catch (ArgumentException ex) { throw new Exception("NOT rated: " + ex.Message); }
                        return $"{Str(a, "id")}: {Str(a, "rating")}";
                    case "done":
                        try { Advisor.Mark(dir, Str(a, "id"), "done", Environment.UserName, Str(a, "comment")); }
                        catch (ArgumentException ex) { throw new Exception("NOT marked: " + ex.Message); }
                        return $"{Str(a, "id")}: done";
                    case "stats":
                    {
                        var q = Advisor.Quotas(Advisor.Load(dir), DateTime.Now);
                        return q.Count == 0 ? "No advice yet." : string.Join("\n", q.Select(Advisor.FormatQuota));
                    }
                    case "watch_list":
                    {
                        var (signals, hints) = AdvisorWatch.Load(dir, PluginFolders(dir), ActivePlatforms(dir));
                        var o = new System.Text.StringBuilder();
                        o.AppendLine(signals.Count == 0 ? "No fault signals declared (.tcagent\\advisor-watch.json)." : $"{signals.Count} fault signal(s):");
                        foreach (var s in signals) o.AppendLine("  " + AdvisorWatch.Describe(s));
                        foreach (var h in hints) o.AppendLine($"Plugin {h.Plugin}: load the skill {h.Skill} - it knows how this platform reports faults.");
                        o.Append("Always watched as well: the runtime and each PLC leaving Run, tasks exceeding their cycle time.");
                        return o.ToString();
                    }
                    case "watch_add":
                        try
                        {
                            AdvisorWatch.Add(dir, new AdvisorWatch.Signal(Str(a, "symbol"), Str(a, "when") ?? "true", Str(a, "text") ?? Str(a, "symbol"),
                                (Str(a, "details") ?? "").Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                                Int(a, "port", 851)));
                        }
                        catch (ArgumentException ex) { throw new Exception("NOT added: " + ex.Message); }
                        return $"fault signal {Str(a, "symbol")} {Str(a, "when") ?? "true"} added to .tcagent\\advisor-watch.json";
                    case "watch_remove":
                        return AdvisorWatch.Remove(dir, Str(a, "symbol") ?? throw new Exception("symbol is required"))
                            ? $"fault signal {Str(a, "symbol")} removed" : $"{Str(a, "symbol")} is not in .tcagent\\advisor-watch.json";
                    case "watch_check":
                    {
                        // one look at the plant now: what is wrong, with the details of the signals that fire
                        var (signals, _) = AdvisorWatch.Load(dir, PluginFolders(dir), ActivePlatforms(dir));
                        string net = Str(a, "netId");
                        if (string.IsNullOrEmpty(net)) try { net = tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId()); } catch { net = "local"; }
                        using var r = new AdvisorReader(net);
                        var ports = signals.Select(s => s.Port).Append(851);
                        var found = AdvisorLook.Look(r, ports, signals, new AdvisorWatch.State(), DateTime.Now);
                        bool dirty = false;
                        try { dirty = tc.Sta.Run(() => tc.RequireDte().Documents.Cast<EnvDTE.Document>().Any(d => !d.Saved)); } catch { }
                        var code = AdvisorCode.Check(r, 851, dir, dirty);
                        var head = $"{r.NetId}: runtime {r.State(10000)}, PLC 851 {r.State(851)}; {signals.Count} fault signal(s) checked.\nCode: {code}";
                        return found.Count == 0 ? head + "\nNothing is wrong now."
                            : head + "\n" + string.Join("\n", found.Select(f => "  " + f.Text + (f.Details.Count > 0 ? "\n    " + string.Join("\n    ", f.Details) : "")));
                    }
                    default: throw new Exception("action: start, record, finish, list, rate, stats, watch_list, watch_add, watch_remove or watch_check");
                }
            });
    }

    static IEnumerable<string> ActivePlatforms(string dir)
    {
        try { return KnowledgeLayers.Platforms(dir).Active; } catch { return Enumerable.Empty<string>(); }
    }

    static IEnumerable<(string id, string folder)> PluginFolders(string dir)
    {
        try { return Plugins.Usable(dir).Where(p => p.Ready).Select(p => (p.Id, p.Folder)).ToList(); }
        catch { return Enumerable.Empty<(string, string)>(); }
    }

    /// <summary>During an Advisor run nothing is changed: every tool that is not read only is refused.</summary>
    string AdvisorRefusal(string name, JsonObject args) =>
        AdvisorActive && name != "tc_advisor" && Policy.ClassOf(name, args) != ToolClass.Read
            ? $"NOT executed: an Advisor run reads and advises, it changes nothing ({name}). Record what should be done with tc_advisor action=record " +
              "and put it into the order - a person sends it to an agent later."
            : null;
}
