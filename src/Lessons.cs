// Lessons learned: solutions to hard problems the agent found while coding or configuring TwinCAT, kept across sessions.
//
//   <Solution>\.tcagent\lessons.md        project lessons (this machine, this hardware, this project) - versioned with Git
//   %LOCALAPPDATA%\TcAgent\lessons.md     personal lessons (general TwinCAT knowledge, all projects)
//   TCAGENT_LESSONS                       team file, e.g. on a network drive (scope=team)
//
// One Markdown section per lesson, readable and editable by hand:
//   ## SFC import rejects a transition without condition
//   - id: L20261013-091512 · date: 2026-10-13 · tags: sfc, plcopen · tool: tc_sfc
//   - **Symptom:** ...
//   - **Cause:** ...
//   - **Solution:** ...
//
// The agent saves a lesson with tc_lessons action=add after it solved something non-obvious; TcAgent also recalls matching
// lessons by itself when a tool fails or a build/check reports errors, and reminds the agent to save a lesson when a call
// succeeds after several failed attempts.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Lessons
{
    public sealed class Lesson
    {
        public string Id, Title, Date, Tags = "", Tool = "", Symptom = "", Cause = "", Solution = "", Avoid = "", File, Scope;
        public string AllText => $"{Title} {Tags} {Tool} {Symptom} {Cause} {Solution} {Avoid}";

        public string Format(bool full = true)
        {
            var sb = new StringBuilder($"[{Id}] {Title}  ({Scope}, {Date}{(Tags.Length > 0 ? ", " + Tags : "")}{(Tool.Length > 0 ? ", " + Tool : "")})\n");
            if (Symptom.Length > 0) sb.AppendLine("  Symptom: " + Symptom);
            if (Cause.Length > 0) sb.AppendLine("  Why: " + Cause);
            sb.AppendLine("  Solution: " + Solution);
            if (full && Avoid.Length > 0) sb.AppendLine("  Avoid: " + Avoid);
            return sb.ToString();
        }
    }

    static string Team => Environment.GetEnvironmentVariable("TCAGENT_LESSONS") is { Length: > 0 } t ? Environment.ExpandEnvironmentVariables(t.Trim().Trim('"')) : null;

    // Levels and platforms (KnowledgeLayers.cs): every level has the folder lessons\ (one file per lesson - no merge conflicts
    // when several people or agents add lessons) and the former single file lessons.md. TCAGENT_LESSONS: a team file or folder.
    //   scope project → <Solution>\.tcagent\lessons\     personal → %LOCALAPPDATA%\TcAgent\lessons\
    //   platform:<name> → %LOCALAPPDATA%\TcAgent\platforms\<name>\lessons\     team → TCAGENT_LESSONS
    // Company and company platform levels are shared: lessons go there by promoting them (pull request), not directly.
    static string ScopeOf(KnowledgeLayers.Layer l) =>
        l.Platform != null ? "platform:" + l.Platform + (l.Level == "user" ? "" : " (company)") : l.Level == "user" ? "personal" : l.Level;

    /// <summary>The folder (one file per lesson) or - for a team file - the file a new lesson of this scope goes to.</summary>
    public static string FileFor(string solutionDir, string scope)
    {
        var s = (scope ?? "").Trim();
        switch (s.ToLowerInvariant())
        {
            case "personal": case "general": return Path.Combine(KnowledgeLayers.UserRoot, "lessons");
            case "team": return Team ?? throw new Exception("scope=team needs the environment variable TCAGENT_LESSONS (team file or folder)");
            case "company": throw new Exception("The company level is shared: save the lesson with scope=personal or project and promote it (tc_knowledge).");
        }
        if (s.StartsWith("platform:", StringComparison.OrdinalIgnoreCase)) return Path.Combine(KnowledgeLayers.UserRoot, "platforms", s[9..].Trim(), "lessons");
        return KnowledgeLayers.ProjectRoot(solutionDir) is { } p ? Path.Combine(p, "lessons") : Path.Combine(KnowledgeLayers.UserRoot, "lessons");
    }

    // ------------------------------------------------------------------ read
    static readonly object gate = new();
    static readonly Dictionary<string, (DateTime Stamp, List<Lesson> Items)> cache = new(StringComparer.OrdinalIgnoreCase);

    public static List<Lesson> All(string solutionDir)
    {
        var list = new List<Lesson>();
        // project first: its lessons are the most specific (search ranks equal scores in this order)
        var entries = KnowledgeLayers.Resolve("lessons", solutionDir, KnowledgeLayers.Platforms(solutionDir).Active, replaceByName: false)
                                     .Where(e => e.Active).OrderByDescending(e => e.Layer.Rank);
        foreach (var e in entries) list.AddRange(Load(e.Path, ScopeOf(e.Layer)));
        if (Team is { } t)
        {
            if (Directory.Exists(t)) foreach (var f in Directory.GetFiles(t, "*.md")) list.AddRange(Load(f, "team"));
            else list.AddRange(Load(t, "team"));
        }
        return list;
    }

    static List<Lesson> Load(string file, string scope)
    {
        try
        {
            if (!File.Exists(file)) return new();
            var stamp = File.GetLastWriteTimeUtc(file);
            lock (gate)
            {
                if (cache.TryGetValue(file, out var c) && c.Stamp == stamp) return c.Items;
                var items = Parse(File.ReadAllText(file), file, scope);
                cache[file] = (stamp, items);
                return items;
            }
        }
        catch (Exception ex) { Log.Info($"Lessons not readable ({file}): {ex.Message}"); return new(); }
    }

    static List<Lesson> Parse(string text, string file, string scope)
    {
        var list = new List<Lesson>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n"), @"^## ", RegexOptions.Multiline).Skip(1))
        {
            var lines = block.Split('\n');
            var l = new Lesson { Title = lines[0].Trim(), File = file, Scope = scope };
            string field = null;
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.TrimEnd();
                var meta = Regex.Match(line, @"^-\s*id:\s*(\S+)(.*)$");
                if (meta.Success)
                {
                    l.Id = meta.Groups[1].Value;
                    foreach (var part in meta.Groups[2].Value.Split('·'))
                    {
                        var kv = Regex.Match(part, @"^\s*(\w+):\s*(.*?)\s*$");
                        if (!kv.Success) continue;
                        switch (kv.Groups[1].Value.ToLowerInvariant())
                        {
                            case "date": l.Date = kv.Groups[2].Value; break;
                            case "tags": l.Tags = kv.Groups[2].Value; break;
                            case "tool": l.Tool = kv.Groups[2].Value; break;
                        }
                    }
                    field = null;
                    continue;
                }
                var f = Regex.Match(line, @"^-\s*\*\*(Symptom|Cause|Solution|Avoid):\*\*\s*(.*)$", RegexOptions.IgnoreCase);
                if (f.Success) { field = f.Groups[1].Value.ToLowerInvariant(); Append(l, field, f.Groups[2].Value); continue; }
                if (field != null && line.Trim().Length > 0) Append(l, field, line.StartsWith("  ") ? line[2..] : line.Trim());     // continuation lines (keep code indentation)
            }
            l.Id ??= "L-" + StableHash(l.Title);     // string.GetHashCode is randomized per process
            l.Date ??= "";
            if (l.Title.Length > 0) list.Add(l);
        }
        return list;
    }

    static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s.ToLowerInvariant()) h = (h ^ ch) * 16777619;
        return (h % 100000).ToString("00000");
    }

    static void Append(Lesson l, string field, string text)
    {
        string Join(string a) => a.Length == 0 ? text.Trim() : a + "\n" + text.TrimEnd();
        switch (field)
        {
            case "symptom": l.Symptom = Join(l.Symptom); break;
            case "cause": l.Cause = Join(l.Cause); break;
            case "solution": l.Solution = Join(l.Solution); break;
            case "avoid": l.Avoid = Join(l.Avoid); break;
        }
    }

    // ------------------------------------------------------------------ search
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "from", "this", "that", "into", "when", "after", "error", "errors", "warning", "is", "are",
        "was", "der", "die", "das", "und", "mit", "nicht", "ein", "eine", "beim", "bei", "von", "auf", "ist", "wird", "fehler",
        "true", "false", "line", "zeile", "object", "objekt", "tool", "call", "failed"
    };

    static IEnumerable<string> Words(string s) =>
        Regex.Matches(s ?? "", @"[A-Za-z_ÄÖÜäöüß][\w.#]*|0x[0-9A-Fa-f]+").Select(m => m.Value.Trim('.'))
             .Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase);

    // codes that identify an error precisely: C0032, 0x0706, ADS 1861, SA0033, ... - plain numbers only right after ADS/error/code,
    // never on their own (line numbers, years, durations, ports would cause false recalls)
    static IEnumerable<string> Codes(string s) =>
        Regex.Matches(s ?? "", @"\b[A-Z]{1,3}\d{3,5}\b|\b0x[0-9A-Fa-f]{3,8}\b|(?<=\b(?:ADS|[Ee]rror|ERROR|Fehler|[Cc]ode)(?:\s+[Cc]ode)?\s*[:#=]?\s*)\d{3,5}\b")
             .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase);

    // words too common in TcAgent/IDE messages to count as evidence for an automatic recall
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "project", "projects", "solution", "plc", "twincat", "tcagent", "ide", "build", "check", "file", "open", "could", "cannot", "can",
        "has", "have", "been", "use", "used", "value", "type", "variable", "instance", "main", "name", "found", "unknown", "invalid", "see",
        "you", "your", "please", "only", "then", "does", "which", "will", "all", "any", "also", "must", "should", "there", "other", "more",
        "some", "its", "compiler", "warnings", "entries", "messages", "runtime", "succeeded", "duration", "objects", "kann", "keine", "kein"
    };

    public static List<(Lesson L, double Score)> Search(string solutionDir, string query, string tool = null, int max = 5)
    {
        var words = Words(query).ToList();
        var codes = Codes(query).ToList();
        if (words.Count == 0 && codes.Count == 0 && string.IsNullOrEmpty(tool)) return new();
        var res = new List<(Lesson, double)>();
        foreach (var l in All(solutionDir))
        {
            double score = 0;
            var title = l.Title + " " + l.Tags;
            var all = l.AllText;
            foreach (var w in words)
            {
                if (title.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 3;
                else if (l.Symptom.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 2;
                else if (all.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 1;
            }
            foreach (var c in codes) if (Regex.IsMatch(all, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase)) score += 8;
            if (!string.IsNullOrEmpty(tool) && l.Tool.Split(',', ' ').Any(t => t.Equals(tool, StringComparison.OrdinalIgnoreCase))) score += 2;
            if (score > 0) res.Add((l, score / Math.Sqrt(Math.Max(1, words.Count + codes.Count))));
        }
        return res.OrderByDescending(x => x.Item2).Take(max).ToList();
    }

    /// <summary>Lessons that very probably match an error (for automatic recall) - strict, to avoid noise.</summary>
    public static string Recall(string solutionDir, string tool, string errorText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(errorText) || All(solutionDir).Count == 0) return null;
            var text = errorText.Length > 3000 ? errorText[..3000] : errorText;
            var codes = Codes(text).ToList();
            var words = Words(text).Where(w => !Generic.Contains(w) && !w.StartsWith("tc_", StringComparison.OrdinalIgnoreCase)).ToList();
            bool Strong(Lesson l)
            {
                if (codes.Any(c => Regex.IsMatch(l.AllText, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase))) return true;
                var key = Words(l.Title + " " + l.Tags + " " + l.Symptom).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return words.Count(key.Contains) >= 3;
            }
            var hits = Search(solutionDir, text, tool, 20).Where(h => Strong(h.L)).Take(2).ToList();
            if (hits.Count == 0) return null;
            return "\n\nKnown solutions from earlier sessions (tc_lessons) - check whether they apply:\n" + string.Join("", hits.Select(h => h.L.Format(false)));
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ write
    public static string Add(string solutionDir, JsonObject a, bool dryRun)
    {
        string S(string k) => a[k] is JsonValue v ? v.ToString().Trim() : "";
        var title = S("title");
        var solution = S("solution");
        var missing = new[] { "title", "symptom", "cause", "solution" }.Where(k => S(k).Length == 0).ToList();
        if (missing.Count > 0)
            throw new Exception("A lesson needs the problem, its explanation and the solution - missing: " + string.Join(", ", missing) +
                                ". symptom = what exactly happened (error text/code, behaviour), cause = why it happens (explanation, background; " +
                                "'unclear' if not known), solution = exactly what fixed it (steps, values, code).");
        var scope = S("scope").Length > 0 ? S("scope").ToLowerInvariant() : "project";
        var target = FileFor(solutionDir, scope);
        // a folder (lessons\): one file per lesson; a team file of an older setup: one section per lesson as before
        bool folder = !target.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var all = All(solutionDir);
        var replace = S("id");
        Lesson old = replace.Length > 0 ? all.FirstOrDefault(l => l.Id.Equals(replace, StringComparison.OrdinalIgnoreCase))
                                         : all.FirstOrDefault(l => l.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (replace.Length > 0 && old == null) throw new Exception($"Lesson {replace} not found");
        bool sameLevel(Lesson l) => folder ? Path.GetDirectoryName(l.File)!.Equals(target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) : l.File == target;
        if (old != null && !sameLevel(old) && replace.Length == 0) old = null;   // same title in another scope: a new lesson here (own id)
        var id = old?.Id ?? $"L{DateTime.Now:yyyyMMdd-HHmmss}";
        for (int n = 2; old == null && all.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); n++) id = $"L{DateTime.Now:yyyyMMdd-HHmmss}-{n}";
        // the file: an updated lesson keeps its file; a new one gets lessons\<date>-<title>.md
        string file = old != null ? old.File
                    : folder ? Path.Combine(target, $"{DateTime.Now:yyyy-MM-dd}-{KnowledgeLayers.Slug(title)}.md")
                    : target;
        for (int n = 2; old == null && folder && File.Exists(file); n++) file = Path.Combine(target, $"{DateTime.Now:yyyy-MM-dd}-{KnowledgeLayers.Slug(title)}-{n}.md");
        bool single = old != null ? IsSingle(old.File) : folder;
        var sb = new StringBuilder($"## {OneLine(title)}\n");
        var meta = $"- id: {id} · date: {DateTime.Now:yyyy-MM-dd}";
        if (S("tags").Length > 0) meta += " · tags: " + OneLine(S("tags"));
        if (S("tool").Length > 0) meta += " · tool: " + OneLine(S("tool"));
        sb.AppendLine(meta);
        void F(string name, string key) { var v = S(key); if (v.Length > 0) sb.AppendLine($"- **{name}:** " + v.Replace("\r\n", "\n").Replace("\n", "\n  ")); }
        F("Symptom", "symptom"); F("Cause", "cause"); F("Solution", "solution"); F("Avoid", "avoid");
        var entry = sb.ToString().Replace("\r\n", "\n");     // AppendLine writes \r\n on Windows; the file is normalized below
        if (dryRun) return $"Would {(old != null ? "update" : "add")} in {file}:\n\n{entry}";

        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string text;
            if (single) text = entry;                       // one lesson per file: the file is the lesson
            else
            {
                text = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : Header;
                if (old != null && old.File == file) text = RemoveSection(text, old.Title);
                if (!text.EndsWith("\n")) text += "\n";
                text += "\n" + entry;
            }
            File.WriteAllText(file, text.Replace("\n", "\r\n"), new UTF8Encoding(false));
            cache.Remove(file);
        }
        var similar = old == null ? Search(solutionDir, title, null, 10).Where(h => h.Score >= 2.5 && h.L.Id != id).Select(h => h.L).Take(3).ToList() : new();
        return $"Lesson {(old != null ? "updated" : "saved")}: [{id}] {title}\nFile: {file}" +
               (similar.Count > 0 ? "\nSimilar lessons exist - merge them if they describe the same thing (tc_lessons action=add id=... / remove):\n" +
                                    string.Join("", similar.Select(l => "  " + l.Format(false))) : "");
    }

    public static string Remove(string solutionDir, string id)
    {
        var l = All(solutionDir).FirstOrDefault(x => x.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception($"Lesson {id} not found");
        lock (gate)
        {
            if (IsSingle(l.File)) File.Delete(l.File);     // one lesson per file
            else
            {
                var text = File.ReadAllText(l.File).Replace("\r\n", "\n");
                File.WriteAllText(l.File, RemoveSection(text, l.Title).Replace("\n", "\r\n"), new UTF8Encoding(false));
            }
            cache.Remove(l.File);
        }
        return $"Removed: [{l.Id}] {l.Title} ({l.File})";
    }

    /// <summary>A file in a lessons\ folder holds exactly one lesson.</summary>
    static bool IsSingle(string file) => Path.GetFileName(Path.GetDirectoryName(file) ?? "").Equals("lessons", StringComparison.OrdinalIgnoreCase);

    static string RemoveSection(string text, string title)
    {
        var m = Regex.Match(text, $@"^## {Regex.Escape(title)}[ \t]*\n.*?(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        return m.Success ? text.Remove(m.Index, m.Length).TrimEnd() + "\n" : text;
    }

    static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Replace("·", "-").Trim();

    const string Header = "# TcAgent lessons learned\n\nSolutions to problems that were hard to find. Written by the agent (tc_lessons), may be edited by hand.\n";

    // ------------------------------------------------------------------ session tracking (reminder to save a lesson)
    static readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> BuildTools = new(StringComparer.OrdinalIgnoreCase) { "tc_check", "tc_build", "tc_errors" };

    /// <summary>Counts failed attempts per tool (also builds with compile errors) and returns a reminder when a tool succeeds
    /// after at least two failures in a row.</summary>
    public static string Track(string tool, bool isError, string text)
    {
        if (tool is "tc_lessons" or "tc_feedback") return null;
        var failed = isError || BuildTools.Contains(tool) && HasCompileErrors(text);
        lock (failures)
        {
            failures.TryGetValue(tool, out var n);
            if (failed) { failures[tool] = n + 1; return null; }
            failures.Remove(tool);
            if (n < 2) return null;
            return $"\n\n(This worked after {n} failed attempts. If the solution was not obvious - a TwinCAT peculiarity, a parameter, " +
                   "an order of steps, a workaround - save it with tc_lessons action=add so it is known next time.)";
        }
    }

    public static bool HasCompileErrors(string text) =>
        text != null && Regex.IsMatch(text, @"(?m)^(?:ERROR\t|Build FAILED:|Check ERRORS:)|(?i)\b[1-9]\d*\s+(?:compiler\s+)?(?:errors?|fehler)\b");
}

public sealed partial class ToolRegistry
{
    void RegisterLessons()
    {
        Add("tc_lessons", "Lessons learned across sessions: solutions to hard problems while coding or configuring TwinCAT. " +
                          "action=search query=... (before a non-trivial task and when an error is not obvious - TcAgent also recalls matching " +
                          "lessons by itself when a tool fails), action=list, action=add after you solved something that took several attempts, " +
                          "a workaround, a non-obvious parameter or setting, or the user's help. Record exactly WHAT the problem was (symptom: error text/code, " +
                          "behaviour), WHY (cause: the explanation/background) and HOW it was solved (solution: concrete steps, values, code), " +
                          "plus what did not work (avoid), tags, tool; scope project = only this project/" +
                          "hardware, personal = general TwinCAT knowledge, platform:<name> = knowledge about the automation framework the project uses, team = shared). Save only what is verified to work, generally " +
                          "phrased, no passwords or customer secrets. Tell the user in one sentence what you saved. action=remove id=... " +
                          "deletes a wrong or outdated lesson; add with id=... updates one.",
            Schema(Req(P("action", "string", "search, list, add or remove")),
                   P("query", "string", "search: error text, error code, topic (e.g. 'SFC transition PLCopen import', 'C0032')"),
                   P("tool", "string", "search: affected tool (ranks its lessons higher); add: tool(s) involved, e.g. tc_sfc"),
                   P("title", "string", "add: short, searchable, e.g. 'EL7031: motor does not turn - current limit in CoE 8010:01'"),
                   P("symptom", "string", "add (required): the problem exactly - error text/code, observed behaviour, in which situation"),
                   P("cause", "string", "add (required): the explanation - why it happens, the background (TwinCAT behaviour, data type, order, setting); 'unclear' if not known"),
                   P("solution", "string", "add (required): exactly how it was solved - concrete steps, parameters, values, code before/after"),
                   P("avoid", "string", "add: what did NOT work / what to avoid (optional)"),
                   P("tags", "string", "add: comma-separated, e.g. 'sfc, plcopen' or 'ethercat, el7031, coe'"),
                   P("scope", "string", "add: project (default; this project/hardware), personal (general TwinCAT knowledge), platform:<name> (knowledge about that automation framework) or team"),
                   P("id", "string", "add: update this lesson; remove: lesson to delete"),
                   P("max", "integer", "search: max. results (default 5)"),
                   P("dryRun", "boolean", "add: only show the entry")),
            a =>
            {
                var dir = SolutionDir();
                switch ((Str(a, "action") ?? "search").ToLowerInvariant())
                {
                    case "search":
                        var hits = Lessons.Search(dir, Str(a, "query") ?? "", Str(a, "tool"), Int(a, "max", 5));
                        return hits.Count == 0 ? "No matching lessons." : string.Join("\n", hits.Select(h => h.L.Format()));
                    case "list":
                        var all = Lessons.All(dir);
                        return all.Count == 0
                            ? "No lessons yet. Files: " + Lessons.FileFor(dir, "project") + ", " + Lessons.FileFor(dir, "personal")
                            : $"{all.Count} lessons:\n" + string.Join("\n", all.Select(l => $"  [{l.Id}] {l.Title}  ({l.Scope}{(l.Tags.Length > 0 ? ", " + l.Tags : "")})"));
                    case "add": return Lessons.Add(dir, a, DryRun(a));
                    case "remove": return Lessons.Remove(dir, Str(a, "id"));
                    default: throw new Exception("action: search, list, add or remove");
                }
            });
    }
}
