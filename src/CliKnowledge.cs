// TcAgent command line: knowledge (part of Cli, see Cli.cs) - the data of the knowledge manager window in the IDE
// (vsbridge/KnowledgeWindow.cs). Files only, no IDE, no network.
//
//   TcAgent.exe knowledge json [--solution <sln>]       everything the window shows, as one JSON object: platform,
//                                                       KPIs of the workflow runs, entries of every kind and level with
//                                                       their status, sources and installed items, proposals, changes
//   TcAgent.exe knowledge disable|enable --key <kind/name> [--level user|project] [--solution <sln>]
//                                                       switch an entry off/on (disabled.json - the file stays unchanged)
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static partial class Cli
{
    static int KnowledgeCli(Options o)
    {
        var dir = o.Solution != null ? Path.GetDirectoryName(Path.GetFullPath(o.Solution)) : null;
        if (o.Sub is "disable" or "enable")
        {
            var level = o.Level is "user" or "personal" ? "user" : dir != null ? "project" : "user";
            var kind = o.Key![..o.Key.IndexOf('/')];
            var target = ToolRegistry.EntriesOf(kind, dir, KnowledgeLayers.Platforms(dir)).FirstOrDefault(e => e.Key.Equals(o.Key, StringComparison.OrdinalIgnoreCase));
            if (o.Sub == "disable" && target?.Locked == true) { Console.Error.WriteLine($"{o.Key} is locked by {target.Layer.Label} - only its owner can change it."); return Failed; }
            Console.WriteLine($"{(o.Sub == "disable" ? "Switched off" : "Switched on")}: {o.Key} ({KnowledgeLayers.SetDisabled(dir, level, o.Key, o.Sub == "disable")})");
            return Ok;
        }
        if (o.Sub is "feedback")
        {
            try
            {
                var items = Feedback.Load();
                switch (o.Action ?? "issue")
                {
                    case "issue":
                    {
                        // the selected reports as issue text, anonymized - the window shows it for editing before anything is shared
                        var ids = (o.Key ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var sel = items.Where(i => ids.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (sel.Count == 0) throw new ArgumentException("--key <id[,id...]> of the reports");
                        var terms = Improver.PrivateTerms(dir);
                        var texts = new List<string>(); var replaced = new List<string>();
                        foreach (var i in sel) { var (t, r) = Feedback.Anonymize(Feedback.Issue(i), terms, i); texts.Add(t); replaced.AddRange(r); }
                        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                        Console.WriteLine(new JsonObject
                        {
                            ["text"] = string.Join("\n---\n\n", texts), ["url"] = Feedback.ShareUrl(),
                            ["replaced"] = new JsonArray(replaced.Distinct().Select(x => (JsonNode)x).ToArray())
                        }.ToJsonString());
                        return Ok;
                    }
                    case "resolve":
                        Console.WriteLine(Feedback.Resolve(o.Key, o.Message ?? "resolved", null, null));
                        return Ok;
                    default: throw new ArgumentException("feedback --action issue|resolve");
                }
            }
            catch (Exception ex) when (ex is ArgumentException) { Console.Error.WriteLine(ex.Message); return Failed; }
        }
        if (o.Sub is "platform")
        {
            try
            {
                Console.WriteLine((o.Action ?? "") switch
                {
                    "new" => KnowledgeOps.NewPlatform(o.Name, o.Message, o.Libraries, o.Terms),
                    "set" => "Stored in " + KnowledgeLayers.SetPlatforms(dir ?? throw new ArgumentException("no solution open"),
                                 (o.Name ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => !n.Equals("none", StringComparison.OrdinalIgnoreCase))),
                    "auto" => KnowledgeOps.AutoPlatform(dir ?? throw new ArgumentException("no solution open")),
                    _ => throw new ArgumentException("platform --action new|set|auto")
                });
                return Ok;
            }
            catch (Exception ex) when (ex is ArgumentException) { Console.Error.WriteLine(ex.Message); return Failed; }
        }
        if (o.Sub is "new" or "move" or "delete" or "git")
        {
            try
            {
                var pi = KnowledgeLayers.Platforms(dir).Active;
                Console.WriteLine(o.Sub switch
                {
                    "new" => KnowledgeOps.New(dir, o.Key?.Split('/')[0], o.Key?.Split('/').Last(), o.Level ?? "user"),
                    "move" => KnowledgeOps.Move(dir, o.Out ?? throw new ArgumentException("--path is required"), o.Level ?? throw new ArgumentException("--level is required"), pi),
                    "delete" => KnowledgeOps.Delete(dir, o.Out ?? throw new ArgumentException("--path is required"), pi),
                    _ => KnowledgeOps.GitAction(dir, o.Action ?? "fetch", o.Level ?? throw new ArgumentException("--level is required"), o.Message)
                });
                return Ok;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Console.Error.WriteLine(ex.Message); return Failed; }
        }
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.WriteLine(KnowledgeJson(dir).ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return Ok;
    }

    static double R(double v) => Math.Round(v, 2);

    internal static JsonObject KnowledgeJson(string dir)
    {
        var pi = KnowledgeLayers.Platforms(dir);
        var project = RunLogs.ProjectOf(dir);
        var o = new JsonObject
        {
            ["version"] = Program.ServerVersion, ["solutionDir"] = dir, ["project"] = project,
            ["platform"] = new JsonObject { ["active"] = new JsonArray(pi.Active.Select(x => (JsonNode)x).ToArray()), ["reason"] = pi.Reason,
                                            ["ambiguous"] = new JsonArray(pi.Ambiguous.Select(x => (JsonNode)x).ToArray()) }
        };

        // entries of every kind and level
        var entries = new JsonArray();
        var counts = new JsonObject();
        foreach (var kind in ToolRegistry.KnowledgeKinds)
        {
            List<KnowledgeLayers.Entry> list;
            try { list = ToolRegistry.EntriesOf(kind, dir, pi); } catch { continue; }
            counts[kind] = list.Count(e => e.Active);
            foreach (var e in list)
                entries.Add(new JsonObject
                {
                    ["kind"] = kind, ["name"] = e.Name, ["key"] = e.Key, ["level"] = e.Layer.Level, ["label"] = e.Layer.Label, ["platform"] = e.Layer.Platform,
                    ["status"] = e.Status, ["active"] = e.Active, ["reason"] = e.Reason, ["path"] = e.Path, ["locked"] = e.Locked, ["force"] = e.Force,
                    ["description"] = e.Meta?.Top.TryGetValue("description", out var d) == true ? d : null,
                    ["source"] = e.Meta?.Get("source"), ["license"] = e.Meta?.Get("license")
                });
        }
        // the levels with their Git state (behind/ahead as of the last fetch, local changes) and what the window may do there
        var layers = new JsonArray();
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in KnowledgeLayers.Layers(dir, pi.Active).Where(l => Directory.Exists(l.Root)))
        {
            var git = l.Level == "built-in" ? null : KnowledgeOps.GitState(l.Root);
            if (git?["changes"] is JsonObject ch) foreach (var kv in ch) changes[kv.Key] = kv.Value!.ToString();
            var levelName = l.Platform != null ? (l.Level == "user" ? "platform:" + l.Platform : null) : l.Team != null ? "team:" + l.Team : l.Level;
            layers.Add(new JsonObject
            {
                ["label"] = l.Label, ["level"] = levelName, ["root"] = l.Root, ["deletable"] = KnowledgeOps.Deletable(l),
                ["viaPullRequest"] = levelName != null && l.Level is "company" or "team" && KnowledgeOps.ViaPullRequest(levelName, l.Root),
                ["git"] = git != null && seenRepos.Add((string)git["repo"]!) ? git : git != null ? new JsonObject { ["repo"] = (string)git["repo"]!, ["same"] = true } : null
            });
        }
        foreach (var e in entries.OfType<JsonObject>())
        {
            var p = (string)e["path"]!;
            var item = KnowledgeOps.ItemOf(p);
            var st = changes.TryGetValue(p, out var s1) ? s1 : changes.Where(kv => kv.Key.StartsWith(item + "\\", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).FirstOrDefault();
            if (st != null) e["git"] = st;
        }
        o["layers"] = layers;

        // platforms: the known ones with their detection, which apply to the project and why
        o["platforms"] = new JsonArray(pi.Known.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name, ["description"] = d.Description, ["active"] = pi.Active.Contains(d.Name, StringComparer.OrdinalIgnoreCase),
            ["libraries"] = new JsonArray(d.Libraries.Select(x => (JsonNode)x).ToArray()), ["terms"] = new JsonArray(d.Terms.Select(x => (JsonNode)x).ToArray()),
            ["roots"] = new JsonArray(d.Roots.Select(x => (JsonNode)x).ToArray()),
            ["file"] = d.Roots.Select(r => Path.Combine(r, "platform.md")).Concat(new[] { Path.Combine(KnowledgeLayers.UserRoot, "platforms", d.Name, "platform.md") }).FirstOrDefault(File.Exists)
        }).ToArray());
        o["platformConfigured"] = pi.Configured;
        o["libraries"] = new JsonArray(KnowledgeLayers.ReferencedLibraries(dir).Select(x => (JsonNode)x).ToArray());

        // feedback reports (tc_feedback) - the window shows them, anonymizes and shares a report on request
        try
        {
            o["feedback"] = new JsonArray(Feedback.Load().OrderByDescending(i => i.Time).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id, ["time"] = i.Time, ["status"] = i.Status, ["type"] = i.Type, ["severity"] = i.Severity, ["tool"] = i.Tool,
                ["summary"] = i.Summary, ["version"] = i.Version, ["client"] = i.Client
            }).ToArray());
        }
        catch { o["feedback"] = new JsonArray(); }
        o["entries"] = entries;
        o["counts"] = counts;

        // sources and installed items
        o["sources"] = new JsonArray(Sources.All(dir).Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["url"] = s.Url, ["ref"] = s.Ref, ["level"] = s.Level }).ToArray());
        var installed = new JsonArray();
        var roots = new List<(string, string)>();
        if (KnowledgeLayers.ProjectRoot(dir) is { } pr) roots.Add(("project", pr));
        roots.Add(("user", KnowledgeLayers.UserRoot));
        roots.AddRange(KnowledgeLayers.TeamRoots.Select(t => ("team", t)));
        foreach (var (label, root) in roots)
            foreach (var (key, state, e) in Sources.Status(root))
                installed.Add(new JsonObject { ["key"] = key, ["level"] = label, ["state"] = state, ["source"] = e.Source, ["url"] = e.Url,
                                               ["commit"] = e.Commit, ["license"] = e.License, ["installed"] = e.Installed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        o["installed"] = installed;

        // run logs: KPIs (last 10 runs against the 10 before) and the series per workflow
        var runs = project != null ? RunLogs.Load(project) : new List<RunLogs.Record>();
        JsonObject Kpi(IReadOnlyList<RunLogs.Record> rs)
        {
            var last = rs.TakeLast(RunLogs.Window).ToList();
            var prev = rs.SkipLast(RunLogs.Window).TakeLast(RunLogs.Window).ToList();
            JsonNode P(Func<RunLogs.Record, double> f) => prev.Count == 0 ? null : R(RunLogs.Avg(prev, f));
            return new JsonObject
            {
                ["runs"] = rs.Count,
                ["firstTimeRight"] = last.Count == 0 ? 0 : R(100.0 * last.Count(r => r.FirstTimeRight) / last.Count),
                ["firstTimeRightPrev"] = prev.Count == 0 ? null : R(100.0 * prev.Count(r => r.FirstTimeRight) / prev.Count),
                ["failedChecks"] = R(RunLogs.Avg(last, r => r.FailedChecks)), ["failedChecksPrev"] = P(r => r.FailedChecks),
                ["corrections"] = R(RunLogs.Avg(last, r => r.Corrections)), ["correctionsPrev"] = P(r => r.Corrections),
                ["minutes"] = R(RunLogs.Avg(last, r => r.Minutes)), ["minutesPrev"] = P(r => r.Minutes),
                ["gateStops"] = last.Sum(r => r.GateStops + r.ActivateStops), ["gateStopsPrev"] = prev.Count == 0 ? null : prev.Sum(r => r.GateStops + r.ActivateStops)
            };
        }
        JsonArray Series(IEnumerable<RunLogs.Record> rs) => new(rs.Select(r => (JsonNode)new JsonObject
        {
            ["t"] = r.Started.ToString("s", CultureInfo.InvariantCulture), ["failedChecks"] = r.FailedChecks, ["corrections"] = r.Corrections,
            ["minutes"] = r.Minutes, ["aborted"] = r.Aborted, ["firstTimeRight"] = r.FirstTimeRight
        }).ToArray());
        var proposals = new List<Improver.Proposal>();
        if (project != null) try { proposals = ToolRegistry.CollectProposals(dir, project, Array.Empty<string>()); } catch { }
        var kpis = Kpi(runs);
        kpis["series"] = Series(runs);
        o["overview"] = kpis;
        o["workflows"] = new JsonArray(runs.GroupBy(r => r.Workflow).OrderBy(g => g.Key).Select(g =>
        {
            var k = Kpi(g.ToList());
            k["name"] = g.Key;
            k["series"] = Series(g);
            k["proposals"] = proposals.Count(p => p.Workflow == g.Key);
            return (JsonNode)k;
        }).ToArray());
        o["topErrors"] = new JsonArray(runs.SelectMany(r => r.Steps.SelectMany(s => s.ErrorCodes)).GroupBy(c => c).OrderByDescending(g => g.Count()).Take(5)
                                           .Select(g => (JsonNode)new JsonObject { ["code"] = g.Key, ["count"] = g.Count() }).ToArray());
        o["proposals"] = new JsonArray(proposals.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["kind"] = p.Kind, ["title"] = p.Title, ["evidence"] = p.Evidence, ["workflow"] = p.Workflow, ["step"] = p.Step, ["target"] = p.Target
        }).ToArray());
        o["changes"] = new JsonArray((project != null ? RunLogs.Changes(project) : new List<RunLogs.Change>()).Select(c =>
        {
            var (n, fa, ca, verdict) = RunLogs.Evaluate(c, runs);
            return (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["date"] = c.Date.ToString("s", CultureInfo.InvariantCulture), ["kind"] = c.Kind, ["workflow"] = c.Workflow, ["step"] = c.Step,
                ["description"] = c.Description, ["target"] = c.Target, ["status"] = c.Status,
                ["beforeFailedChecks"] = c.BeforeFailedChecks, ["beforeCorrections"] = c.BeforeCorrections,
                ["runsAfter"] = n, ["failedChecksAfter"] = R(fa), ["correctionsAfter"] = R(ca), ["verdict"] = verdict
            };
        }).ToArray());
        return o;
    }
}
