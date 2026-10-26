// Run logs of the engineering workflows (docs/KNOWLEDGE_LAYERS.md, "Self-improvement") - LOCAL only, never in Git:
//
//   %LOCALAPPDATA%\TcAgent\runs\<project>\<yyyyMMdd-HHmmss>-<workflow>.json   one file per run, the last 100 are kept
//   ...\runs\<project>\summary.json      totals per workflow of the deleted runs - before/after comparisons stay possible
//   ...\runs\<project>\changes.json      improvements adopted with tc_improve: what, when, the values before; evaluated
//                                        over the next 10 runs of the workflow (helped / no effect / worse -> revert proposal)
//   ...\runs\<project>\backup\<id>\      the file before the change (for the revert)
//   ...\runs\<project>\project.json      the solution folder (to compare lessons across projects)
//
// A run log holds only counts and names: workflow, duration, steps with failed checks / checkpoint stops / compiler error
// codes, activate stops, user corrections, skills used. No task text, no code, no customer data.
//
// Pure file logic (no IDE), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class RunLogs
{
    public const int Keep = 100;
    public const int Window = 10;    // runs per comparison window (trend, evaluation of a change)

    public sealed class StepLog
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public int FailedChecks { get; set; }
        public int GateStops { get; set; }
        public List<string> ErrorCodes { get; set; } = new();
    }

    public sealed class Record
    {
        public string Workflow { get; set; } = "";
        public string Level { get; set; } = "";
        public DateTime Started { get; set; }
        public double Minutes { get; set; }
        public bool Aborted { get; set; }
        public int FailedChecks { get; set; }
        public int GateStops { get; set; }
        public int ActivateStops { get; set; }
        public int Corrections { get; set; }
        public List<string> Skills { get; set; } = new();
        public List<StepLog> Steps { get; set; } = new();
        /// <summary>A run without a failed check and without a user correction.</summary>
        public bool FirstTimeRight => !Aborted && FailedChecks == 0 && Corrections == 0;
    }

    public sealed class Totals
    {
        public int Runs { get; set; }
        public int FailedChecks { get; set; }
        public int Corrections { get; set; }
        public int FirstTimeRight { get; set; }
        public double Minutes { get; set; }
    }

    public sealed class Change
    {
        public string Id { get; set; } = "";
        public DateTime Date { get; set; }
        public string Kind { get; set; } = "";
        public string Workflow { get; set; }
        public string Step { get; set; }
        public string Target { get; set; } = "";       // the file changed
        public string Description { get; set; } = "";
        public string Backup { get; set; }             // copy of the file before the change (null = the file was new)
        public double BeforeFailedChecks { get; set; } // averages over the last 10 runs before the change
        public double BeforeCorrections { get; set; }
        public string Status { get; set; } = "adopted"; // adopted, reverted
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Root(string project) =>
        Path.Combine(KnowledgeLayers.UserRoot, "runs", Regex.Replace(string.IsNullOrWhiteSpace(project) ? "no-solution" : project.Trim(), @"[^\w.-]+", "_"));

    /// <summary>The key of a project: the name of the solution file (TwinCAT solutions often sit in a folder named "TwinCAT").</summary>
    public static string ProjectOf(string solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir)) return null;
        try { if (Directory.GetFiles(solutionDir, "*.sln") is { Length: 1 } s) return Path.GetFileNameWithoutExtension(s[0]); } catch { }
        return Path.GetFileName(solutionDir.TrimEnd('\\', '/'));
    }

    public static Record ToRecord(Workflows.Run run, bool aborted) => new()
    {
        Workflow = run.Workflow, Level = run.Level, Started = run.Started, Aborted = aborted,
        Minutes = Math.Round(((run.Finished ?? DateTime.Now) - run.Started).TotalMinutes, 1),
        FailedChecks = run.Steps.Sum(s => s.FailedChecks), GateStops = run.Steps.Sum(s => s.GateStops),
        ActivateStops = run.ActivateStops, Corrections = run.Corrections, Skills = run.SkillsUsed.Distinct().ToList(),
        Steps = run.Steps.Select(s => new StepLog { Id = s.Id, Status = s.Status, FailedChecks = s.FailedChecks, GateStops = s.GateStops,
                                                    ErrorCodes = s.ErrorCodes.Distinct().ToList() }).ToList()
    };

    /// <summary>Writes the run log, keeps the newest 100 (the others are added to summary.json). Returns the file.</summary>
    public static string Save(string project, string solutionDir, Record r)
    {
        var root = Root(project);
        Directory.CreateDirectory(root);
        if (!string.IsNullOrEmpty(solutionDir))
            File.WriteAllText(Path.Combine(root, "project.json"), new JsonObject { ["solutionDir"] = solutionDir }.ToJsonString(Json));
        var file = Path.Combine(root, r.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Regex.Replace(r.Workflow, @"[^\w-]+", "_") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(r, Json));
        Prune(root);
        return file;
    }

    static string[] RunFiles(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*.json").Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{8}-\d{6}-")).OrderBy(f => Path.GetFileName(f)).ToArray() : Array.Empty<string>();

    static void Prune(string root)
    {
        var files = RunFiles(root);
        if (files.Length <= Keep) return;
        var totals = LoadTotals(root);
        foreach (var f in files.Take(files.Length - Keep))
        {
            if (Read(f) is { } r)
            {
                var t = totals.TryGetValue(r.Workflow, out var x) ? x : totals[r.Workflow] = new Totals();
                t.Runs++; t.FailedChecks += r.FailedChecks; t.Corrections += r.Corrections; t.Minutes += r.Minutes;
                if (r.FirstTimeRight) t.FirstTimeRight++;
            }
            File.Delete(f);
        }
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(totals, Json));
    }

    static Record Read(string f)
    {
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(f), Json); } catch { return null; }
    }

    public static List<Record> Load(string project) => RunFiles(Root(project)).Select(Read).Where(r => r != null).OrderBy(r => r.Started).ToList();

    public static Dictionary<string, Totals> LoadTotals(string root)
    {
        try
        {
            var f = Path.Combine(root, "summary.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<Dictionary<string, Totals>>(File.ReadAllText(f), Json) ?? new();
        }
        catch { }
        return new();
    }

    // ------------------------------------------------------------------ KPIs
    public static double Avg(IEnumerable<Record> rs, Func<Record, double> f) { var l = rs.ToList(); return l.Count == 0 ? 0 : l.Average(f); }

    /// <summary>Per workflow: runs, first-time-right, failed checks / corrections / minutes per run, trend of the last 10 against the 10 before.</summary>
    public static string Kpis(string project, string workflow = null)
    {
        var all = Load(project).Where(r => workflow == null || r.Workflow.Equals(workflow, StringComparison.OrdinalIgnoreCase)).ToList();
        var totals = LoadTotals(Root(project));
        if (all.Count == 0 && totals.Count == 0) return "No workflow runs recorded yet for this project.";
        var sb = new StringBuilder($"Workflow runs of {project} (last {Keep} kept locally in {Root(project)}):\n");
        sb.AppendLine("workflow               runs  first-time-right  failed checks/run  corrections/run  min/run  trend (failed checks, last 10 vs 10 before)");
        foreach (var g in all.GroupBy(r => r.Workflow).OrderBy(g => g.Key))
        {
            var l = g.ToList();
            var last = l.TakeLast(Window).ToList();
            var prev = l.SkipLast(Window).TakeLast(Window).ToList();
            var trend = prev.Count == 0 ? "-" : Fmt(Avg(prev, r => r.FailedChecks)) + " → " + Fmt(Avg(last, r => r.FailedChecks));
            int old = totals.TryGetValue(g.Key, out var t) ? t.Runs : 0;
            sb.AppendLine($"{g.Key,-22} {l.Count + old,5}  {Pct(l.Count(r => r.FirstTimeRight), l.Count),16}  {Fmt(Avg(l, r => r.FailedChecks)),17}  " +
                          $"{Fmt(Avg(l, r => r.Corrections)),15}  {Fmt(Avg(l, r => r.Minutes)),7}  {trend}");
        }
        var codes = all.SelectMany(r => r.Steps.SelectMany(s => s.ErrorCodes)).GroupBy(c => c).OrderByDescending(g => g.Count()).Take(5).ToList();
        if (codes.Count > 0) sb.AppendLine("Most frequent compiler errors in failed checks: " + string.Join(", ", codes.Select(c => $"{c.Key} ({c.Count()}×)")));
        return sb.ToString();
    }

    static string Fmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string Pct(int n, int of) => of == 0 ? "-" : (100.0 * n / of).ToString("0", CultureInfo.InvariantCulture) + " %";

    // ------------------------------------------------------------------ changes
    public static List<Change> Changes(string project)
    {
        try
        {
            var f = Path.Combine(Root(project), "changes.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(f), Json) ?? new();
        }
        catch { }
        return new();
    }

    public static void SaveChanges(string project, List<Change> changes)
    {
        Directory.CreateDirectory(Root(project));
        File.WriteAllText(Path.Combine(Root(project), "changes.json"), JsonSerializer.Serialize(changes, Json));
    }

    /// <summary>Records an adopted change with the values of the last 10 runs before it; keeps a copy of the file for the revert.</summary>
    public static Change Adopt(string project, string kind, string workflow, string step, string target, string description, string before)
    {
        var runs = Load(project).Where(r => workflow == null || r.Workflow == workflow).TakeLast(Window).ToList();
        var c = new Change
        {
            Id = "C" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), Date = DateTime.Now, Kind = kind,
            Workflow = workflow, Step = step, Target = target, Description = description,
            BeforeFailedChecks = Math.Round(Avg(runs, r => r.FailedChecks), 2), BeforeCorrections = Math.Round(Avg(runs, r => r.Corrections), 2)
        };
        if (before != null)
        {
            var dir = Path.Combine(Root(project), "backup", c.Id);
            Directory.CreateDirectory(dir);
            c.Backup = Path.Combine(dir, Path.GetFileName(target));
            File.WriteAllText(c.Backup, before);
        }
        var all = Changes(project);
        all.Add(c);
        SaveChanges(project, all);
        return c;
    }

    /// <summary>The effect of a change: values of the runs after it against the values before. Null verdict while fewer than 10 runs.</summary>
    public static (int RunsAfter, double FailedAfter, double CorrectionsAfter, string Verdict) Evaluate(Change c, IReadOnlyList<Record> runs)
    {
        var after = runs.Where(r => r.Started > c.Date && (c.Workflow == null || r.Workflow == c.Workflow)).Take(Window).ToList();
        double fa = Avg(after, r => r.FailedChecks), ca = Avg(after, r => r.Corrections);
        if (after.Count < Window || c.Status != "adopted") return (after.Count, fa, ca, null);
        double before = c.BeforeFailedChecks + c.BeforeCorrections, now = fa + ca;
        var verdict = now < before * 0.8 ? "helped" : now > before * 1.2 + 0.1 ? "worse" : "no effect";
        return (after.Count, fa, ca, verdict);
    }

    public static string History(string project)
    {
        var changes = Changes(project);
        if (changes.Count == 0) return "No improvements adopted yet (tc_improve action=analyze shows proposals).";
        var runs = Load(project);
        var sb = new StringBuilder("Adopted improvements (values per run: failed checks + corrections, before → after the next 10 runs):\n");
        foreach (var c in changes)
        {
            var (n, fa, ca, verdict) = Evaluate(c, runs);
            sb.AppendLine($"[{c.Id}] {c.Date:yyyy-MM-dd} {c.Kind} {c.Workflow}{(c.Step != null ? "/" + c.Step : "")} – {c.Description}");
            sb.AppendLine($"    {c.Status}; before {Fmt(c.BeforeFailedChecks)} + {Fmt(c.BeforeCorrections)}, after {n} run(s) {Fmt(fa)} + {Fmt(ca)}" +
                          (verdict != null ? $" → {verdict}" : $" (evaluated after {Window} runs)") + $"; file {c.Target}");
        }
        return sb.ToString();
    }

    /// <summary>Restores the file of a change. Returns what was done.</summary>
    public static string Revert(string project, string id)
    {
        var all = Changes(project);
        var c = all.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"change {id} not found");
        if (c.Status != "adopted") throw new ArgumentException($"change {id} is already {c.Status}");
        if (c.Backup != null && File.Exists(c.Backup)) File.Copy(c.Backup, c.Target, true);
        else if (c.Backup == null && File.Exists(c.Target)) File.Delete(c.Target);   // the change created the file
        c.Status = "reverted";
        SaveChanges(project, all);
        return $"Change {c.Id} reverted: {c.Target}";
    }
}
