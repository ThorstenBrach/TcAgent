// Self-improvement proposals (docs/KNOWLEDGE_LAYERS.md) - deterministic first: TcAgent finds the candidates in the run
// logs, the lessons and the knowledge files by fixed rules; the agent only writes the text where one is needed (a step
// hint) and the user decides (tc_improve apply asks with the diff). Nothing changes silently.
//
//   step-hint        a workflow step needed 1.5 or more failed checks per run over the last runs -> a hint in the step
//   revert           an adopted change did not help over the next 10 runs -> restore the file
//   promote-lesson   the same lesson exists in two or more projects -> move it up (personal, platform, company)
//   frequent-error   a compiler error comes back in several runs and no lesson knows it -> record one
//   duplicate-lesson two lesson files with the same title in one folder -> merge
//   stale-tool       a skill or workflow names a tool TcAgent does not have (renamed or removed) -> fix the text
//
// Moving knowledge up checks the text for names of this machine, user, solution and projects plus the "privateTerms"
// of settings.json / .tcagent\config.json - nothing specific travels upwards.
//
// Pure logic (no IDE), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Improver
{
    public const double StepHintThreshold = 1.5;
    public const int MinRuns = 5;

    public sealed class Proposal
    {
        public string Id, Kind, Title, Evidence, Apply, Workflow, Step, Target, Source;
        public bool NeedsText => Kind == "step-hint";
        public bool Applicable => Kind is "step-hint" or "revert" or "promote-lesson";
    }

    public sealed class Input
    {
        public List<RunLogs.Record> Runs = new();
        public List<RunLogs.Change> Changes = new();
        public List<KnowledgeLayers.Entry> Workflows = new(), Skills = new();
        public HashSet<string> ToolNames = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> LessonFilesByProject = new(StringComparer.OrdinalIgnoreCase);  // project -> lesson files
        public List<string> LessonFolders = new();      // folders of this project's levels (duplicates)
        public string LessonText = "";                  // all lessons of this project (frequent-error)
        public string Project;
    }

    static string Key(string s) { unchecked { int h = 17; foreach (var c in s) h = h * 31 + c; return ((uint)h % 0xFFFFF).ToString("X5"); } }

    /// <summary>Lesson file name without the date: 2026-10-13-sfc-import-rejects.md -> sfc-import-rejects.</summary>
    public static string LessonSlug(string file) => Regex.Replace(Path.GetFileNameWithoutExtension(file), @"^\d{4}-\d{2}-\d{2}-", "");

    public static List<Proposal> Analyze(Input i)
    {
        var list = new List<Proposal>();
        void P(string kind, string key, string title, string evidence, string apply, string wf = null, string step = null, string target = null, string source = null) =>
            list.Add(new Proposal { Id = kind[0..2].ToUpperInvariant() + Key(kind + "|" + key), Kind = kind, Title = title, Evidence = evidence,
                                    Apply = apply, Workflow = wf, Step = step, Target = target, Source = source });

        // step-hint
        foreach (var g in i.Runs.Where(r => !r.Aborted).GroupBy(r => r.Workflow))
        {
            var last = g.TakeLast(RunLogs.Window).ToList();
            var entry = i.Workflows.FirstOrDefault(e => e.Active && e.Name == g.Key);
            if (entry == null) continue;
            foreach (var stepId in last.SelectMany(r => r.Steps).Select(s => s.Id).Distinct())
            {
                var steps = last.SelectMany(r => r.Steps.Where(s => s.Id == stepId && s.Status == "done")).ToList();
                if (steps.Count < MinRuns) continue;
                var avg = steps.Average(s => s.FailedChecks);
                if (avg < StepHintThreshold) continue;
                // an earlier change of this step is still being measured
                if (i.Changes.Any(c => c.Status == "adopted" && c.Workflow == g.Key && c.Step == stepId && RunLogs.Evaluate(c, i.Runs).Verdict == null)) continue;
                var codes = steps.SelectMany(s => s.ErrorCodes).GroupBy(c => c).OrderByDescending(x => x.Count()).Take(3).Select(x => $"{x.Key} ({x.Count()}×)").ToList();
                P("step-hint", g.Key + "/" + stepId, $"Workflow {g.Key}, step {stepId}: add a hint",
                  $"{avg:0.0} failed checks per run over the last {steps.Count} runs" + (codes.Count > 0 ? "; frequent errors: " + string.Join(", ", codes) : ""),
                  "write a short, general hint that prevents the failed checks (look at the lessons and the errors), then tc_improve action=apply id=<id> text=<hint>",
                  g.Key, stepId, entry.Path);
            }
        }

        // revert
        foreach (var c in i.Changes.Where(c => c.Status == "adopted"))
        {
            var (n, fa, ca, verdict) = RunLogs.Evaluate(c, i.Runs);
            if (verdict is "worse" or "no effect")
                P("revert", c.Id, $"Revert change {c.Id} ({c.Description})",
                  $"{verdict}: failed checks + corrections per run {c.BeforeFailedChecks + c.BeforeCorrections:0.0} before, {fa + ca:0.0} over the {n} runs after",
                  "tc_improve action=apply id=<id> restores the file", c.Workflow, c.Step, c.Target, c.Id);
        }

        // promote-lesson: same lesson title in two or more projects
        foreach (var g in i.LessonFilesByProject.SelectMany(kv => kv.Value.Select(f => (Project: kv.Key, File: f))).GroupBy(x => LessonSlug(x.File)))
        {
            var projects = g.Select(x => x.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (projects.Count < 2) continue;
            var src = g.FirstOrDefault(x => x.Project.Equals(i.Project, StringComparison.OrdinalIgnoreCase)).File ?? g.First().File;
            P("promote-lesson", g.Key, $"Move lesson '{g.Key}' up",
              $"the same lesson exists in {projects.Count} projects",
              "tc_improve action=apply id=<id> scope=personal|platform:<name>|company (the text is checked for specific names first)",
              source: src);
        }

        // frequent-error: a compiler error code in 3 or more of the last runs, no lesson mentions it
        foreach (var g in i.Runs.TakeLast(RunLogs.Window).SelectMany(r => r.Steps.SelectMany(s => s.ErrorCodes).Distinct().Select(c => c)).GroupBy(c => c))
            if (g.Count() >= 3 && !i.LessonText.Contains(g.Key, StringComparison.OrdinalIgnoreCase))
                P("frequent-error", g.Key, $"Record a lesson for compiler error {g.Key}",
                  $"{g.Key} came back in {g.Count()} of the last {Math.Min(RunLogs.Window, i.Runs.Count)} runs and no lesson mentions it",
                  "when the cause is known, ask the user and record it with tc_lessons action=add");

        // duplicate-lesson
        foreach (var folder in i.LessonFolders.Where(Directory.Exists))
            foreach (var g in Directory.GetFiles(folder, "*.md").GroupBy(LessonSlug).Where(g => g.Count() > 1))
                P("duplicate-lesson", folder + "|" + g.Key, $"Merge {g.Count()} lessons '{g.Key}'",
                  string.Join(", ", g.Select(Path.GetFileName)), "merge them into one file (after asking the user) and remove the others with tc_lessons action=remove");

        // stale-tool
        foreach (var e in i.Workflows.Concat(i.Skills).Where(e => e.Active))
        {
            var unknown = Regex.Matches(e.Meta.Body, @"\b(?:tc|ads)_[a-z_]+\b").Select(m => m.Value).Distinct()
                               .Where(t => i.ToolNames.Count > 0 && !i.ToolNames.Contains(t)).ToList();
            if (unknown.Count > 0)
                P("stale-tool", e.Path, $"{e.Kind} {e.Name} names unknown tools",
                  string.Join(", ", unknown) + $" ({e.Layer.Label})", "correct the text in " + e.Path + (e.Locked ? " (locked: through its owner)" : ""), target: e.Path);
        }
        return list;
    }

    public static string Format(IReadOnlyList<Proposal> ps)
    {
        if (ps.Count == 0) return "No improvement proposals - the run logs, lessons and knowledge files show nothing to change.";
        var sb = new StringBuilder($"{ps.Count} improvement proposal(s). Ask the user before applying any of them:\n");
        foreach (var p in ps)
            sb.AppendLine().AppendLine($"[{p.Id}] {p.Title}").AppendLine("    Why: " + p.Evidence).AppendLine("    How: " + p.Apply.Replace("<id>", p.Id));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ step hint
    /// <summary>Inserts "Hint: ..." as the last line of a step in the WORKFLOW.md text.</summary>
    public static string InsertHint(string text, string stepId, string hint)
    {
        hint = Regex.Replace(hint ?? "", @"\s+", " ").Trim();
        if (hint.Length is 0 or > 400) throw new ArgumentException("text: one short hint (1-400 characters)");
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => Regex.IsMatch(l, @"^\d+\.\s+\*\*" + Regex.Escape(stepId) + @"\*\*", RegexOptions.IgnoreCase));
        if (start < 0) throw new ArgumentException($"step '{stepId}' not found in the workflow");
        int end = start;
        while (end + 1 < lines.Count && lines[end + 1].Length > 0 && char.IsWhiteSpace(lines[end + 1][0])) end++;
        var indent = new string(' ', Regex.Match(lines[start], @"^\d+\.\s+").Length);
        lines.Insert(end + 1, indent + "Hint: " + hint);
        return string.Join(nl, lines);
    }

    // ------------------------------------------------------------------ names that must not travel upwards
    /// <summary>The names of this machine, user, solution and its projects plus the configured privateTerms.</summary>
    public static List<string> PrivateTerms(string solutionDir)
    {
        var t = new List<string> { Environment.MachineName, Environment.UserName };
        if (!string.IsNullOrEmpty(solutionDir))
        {
            t.Add(Path.GetFileName(solutionDir.TrimEnd('\\', '/')));
            try
            {
                foreach (var f in Directory.GetFiles(solutionDir, "*.sln").Concat(Directory.GetFiles(solutionDir, "*.tsproj", SearchOption.AllDirectories))
                                           .Concat(Directory.GetFiles(solutionDir, "*.plcproj", SearchOption.AllDirectories)).Take(50))
                    t.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            if (KnowledgeLayers.ReadConfig(solutionDir)?["privateTerms"] is JsonArray pa) t.AddRange(pa.Select(x => x?.ToString()));
        }
        try
        {
            var f = Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["privateTerms"] is JsonArray sa) t.AddRange(sa.Select(x => x?.ToString()));
        }
        catch { }
        return t.Where(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length >= 3).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The terms that occur in the text as a whole word (case-insensitive).</summary>
    public static List<string> Specifics(string text, IEnumerable<string> terms) =>
        terms.Where(t => Regex.IsMatch(text ?? "", @"(?<![\w])" + Regex.Escape(t) + @"(?![\w])", RegexOptions.IgnoreCase)).ToList();

    /// <summary>The folder a promoted lesson goes to.</summary>
    public static string PromoteFolder(string scope)
    {
        var s = (scope ?? "personal").Trim();
        if (s.Equals("company", StringComparison.OrdinalIgnoreCase))
            return KnowledgeLayers.CompanyRoot is { Length: > 0 } c ? Path.Combine(c, "lessons") : throw new ArgumentException("no company level (TCAGENT_COMPANY not set)");
        if (s.StartsWith("platform:", StringComparison.OrdinalIgnoreCase)) return Path.Combine(KnowledgeLayers.UserRoot, "platforms", s[9..].Trim(), "lessons");
        if (s is "personal" or "user") return Path.Combine(KnowledgeLayers.UserRoot, "lessons");
        throw new ArgumentException("scope: personal, platform:<name> or company");
    }
}
