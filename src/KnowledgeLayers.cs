// Knowledge layers: where the agent's knowledge comes from (docs/KNOWLEDGE_LAYERS.md) - one scheme for coding rules, risk
// rules, lessons, knowledge sources and documentation templates (later skills and workflows).
//
//   levels      built-in · company (TCAGENT_COMPANY) · user (%LOCALAPPDATA%\TcAgent) · project (<solution>\.tcagent)
//   platforms   an automation framework a project is built on: <company>\platforms\<name>\, <user>\platforms\<name>\ and the
//               roots in TCAGENT_PLATFORMS (a repository of its own). Which platform: .tcagent\config.json {"platforms": [...]},
//               otherwise detected from the referenced libraries / terms listed in platform.md; ambiguous = ask the user.
//   order       built-in → company → user → company·platform → user·platform → project (the later one wins)
//   files       one topic / lesson per file: <root>\<kind>\<name>.md; the former single file <root>\<kind>.md is read too
//   front matter (Agent Skills format): name, description at the top, TcAgent's own fields under metadata:
//               tcagent-locked (later levels may not replace or disable the file), tcagent-force (a user file stays against
//               a platform file), tcagent-scope, tcagent-owner, tcagent-version
//   disabled    <root>\disabled.json {"disabled": ["coding-rules/naming", ...]} - switches files off without changing them
//
// Pure file logic (no IDE), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class KnowledgeLayers
{
    // ------------------------------------------------------------------ roots
    /// <summary>Test hooks: replace the user root and the built-in root.</summary>
    public static string UserRootOverride, BuiltInRootOverride;

    public static string UserRoot => UserRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
    public static string BuiltInRoot => BuiltInRootOverride ?? Path.Combine(AppContext.BaseDirectory, "knowledge");
    public static string CompanyRoot => Expand(Environment.GetEnvironmentVariable("TCAGENT_COMPANY"));
    public static string ProjectRoot(string solutionDir) => string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(solutionDir, ".tcagent");

    static string Expand(string p) => string.IsNullOrWhiteSpace(p) ? null : Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
    static IEnumerable<string> ExpandList(string v) => (v ?? "").Split(';').Select(Expand).Where(x => x != null);

    public sealed record Layer(string Level, string Platform, string Root, int Rank)
    {
        public string Label => Platform == null ? Level : $"{Level} · {Platform}";
    }

    // ------------------------------------------------------------------ front matter
    public sealed class FrontMatter
    {
        public readonly Dictionary<string, string> Top = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Meta = new(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        public string Get(string key) => Meta.TryGetValue("tcagent-" + key, out var v) ? v : Top.TryGetValue(key, out var t) ? t : null;
        public bool Flag(string key) => (Get(key) ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>YAML front matter (subset: key: value, and the metadata block with one level of key: value).</summary>
    public static FrontMatter ParseFrontMatter(string text)
    {
        var fm = new FrontMatter();
        text = (text ?? "").Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n")) { fm.Body = text; return fm; }
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) { fm.Body = text; return fm; }
        bool inMeta = false;
        string lastKey = null;
        foreach (var raw in text[4..end].Split('\n'))
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#")) continue;
            var indented = raw.StartsWith(" ") || raw.StartsWith("\t");
            var m = Regex.Match(raw.Trim(), @"^([\w.-]+)\s*:\s*(.*)$");
            if (!m.Success)
            {
                // continuation of a folded value (e.g. a long description)
                if (lastKey != null && indented) { var d = inMeta ? fm.Meta : fm.Top; d[lastKey] = (d[lastKey] + " " + raw.Trim()).Trim(); }
                continue;
            }
            var key = m.Groups[1].Value;
            var val = Unquote(m.Groups[2].Value.Trim());
            if (!indented) { inMeta = key.Equals("metadata", StringComparison.OrdinalIgnoreCase) && val.Length == 0; if (!inMeta) fm.Top[key] = val; lastKey = inMeta ? null : key; }
            else if (inMeta) { fm.Meta[key] = val; lastKey = key; }
            else if (lastKey != null) fm.Top[lastKey] = (fm.Top[lastKey] + " " + raw.Trim()).Trim();
        }
        var bodyStart = text.IndexOf('\n', end + 4);
        fm.Body = bodyStart < 0 ? "" : text[(bodyStart + 1)..];
        return fm;
    }

    static string Unquote(string v) => v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    // ------------------------------------------------------------------ platforms
    public sealed class PlatformDef
    {
        public string Name, Description;
        public readonly List<string> Roots = new();       // company / external roots of the platform (user roots are separate)
        public readonly List<string> Libraries = new();   // library name patterns (* and ?)
        public readonly List<string> Terms = new();       // words that identify the framework in the project files
    }

    public sealed class PlatformInfo
    {
        public readonly List<string> Active = new();
        public readonly List<string> Ambiguous = new();
        public readonly List<PlatformDef> Known = new();
        public string Reason = "";
        public bool Configured;
    }

    public static List<PlatformDef> KnownPlatforms()
    {
        var defs = new List<PlatformDef>();
        void AddDir(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return;
            var file = Path.Combine(dir, "platform.md");
            var fm = File.Exists(file) ? ParseFrontMatter(File.ReadAllText(file)) : new FrontMatter();
            var name = (fm.Top.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileName(dir.TrimEnd('\\', '/'))).Trim();
            var def = defs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (def == null) defs.Add(def = new PlatformDef { Name = name });
            def.Roots.Add(dir);
            if (def.Description == null && fm.Top.TryGetValue("description", out var desc)) def.Description = desc;
            foreach (var (key, list) in new[] { ("libraries", def.Libraries), ("terms", def.Terms) })
            {
                var v = fm.Get("detect-" + key) ?? (fm.Top.TryGetValue(key, out var x) ? x : null);
                if (v != null) list.AddRange(SplitList(v).Where(s => !list.Contains(s, StringComparer.OrdinalIgnoreCase)));
            }
        }
        void AddPlatformsOf(string root)
        {
            var p = root == null ? null : Path.Combine(root, "platforms");
            if (p != null && Directory.Exists(p)) foreach (var d in Directory.GetDirectories(p).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) AddDir(d);
        }
        AddPlatformsOf(CompanyRoot);
        foreach (var r in ExpandList(Environment.GetEnvironmentVariable("TCAGENT_PLATFORMS")))
            if (File.Exists(Path.Combine(r, "platform.md"))) AddDir(r); else AddPlatformsOf(r);
        // user platform folders: they count as a platform even without a company definition
        foreach (var d in Directory.Exists(Path.Combine(UserRoot, "platforms")) ? Directory.GetDirectories(Path.Combine(UserRoot, "platforms")) : Array.Empty<string>())
        {
            var name = Path.GetFileName(d);
            if (!defs.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var file = Path.Combine(d, "platform.md");
                if (File.Exists(file)) { AddDir(d); defs.Last().Roots.Remove(d); }
                else defs.Add(new PlatformDef { Name = name });
            }
        }
        return defs;
    }

    /// <summary>"[A, B*]", "A, B" or "A B" → items.</summary>
    static IEnumerable<string> SplitList(string v) =>
        v.Trim().TrimStart('[').TrimEnd(']').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => Unquote(s.Trim())).Where(s => s.Length > 0);

    static bool Wildcard(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    public static PlatformInfo Platforms(string solutionDir)
    {
        var pi = new PlatformInfo();
        pi.Known.AddRange(KnownPlatforms());
        var cfg = ReadConfig(solutionDir);
        if (cfg?["platforms"] is JsonArray arr)
        {
            pi.Configured = true;
            pi.Active.AddRange(arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            pi.Reason = "configured in .tcagent\\config.json";
            return pi;
        }
        if (pi.Known.Count == 0 || string.IsNullOrEmpty(solutionDir)) { pi.Reason = "no platforms defined"; return pi; }
        var libs = ReferencedLibraries(solutionDir);
        var projectText = new Lazy<string>(() => ProjectText(solutionDir));
        var hits = new List<(PlatformDef Def, string Why)>();
        foreach (var d in pi.Known)
        {
            var lib = libs.FirstOrDefault(l => d.Libraries.Any(p => Wildcard(p, l)));
            if (lib != null) { hits.Add((d, "library " + lib)); continue; }
            var term = d.Terms.FirstOrDefault(t => projectText.Value.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (term != null) hits.Add((d, "term " + term));
        }
        if (hits.Count == 1) { pi.Active.Add(hits[0].Def.Name); pi.Reason = "detected: " + hits[0].Why; }
        else if (hits.Count > 1)
        {
            pi.Ambiguous.AddRange(hits.Select(h => h.Def.Name));
            pi.Reason = "several platforms match (" + string.Join(", ", hits.Select(h => $"{h.Def.Name}: {h.Why}")) + ") - ask the user which one";
        }
        else pi.Reason = "no platform detected";
        return pi;
    }

    public static JsonObject ReadConfig(string solutionDir)
    {
        var f = ProjectRoot(solutionDir) is { } r ? Path.Combine(r, "config.json") : null;
        try { return f != null && File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null; }
        catch { return null; }
    }

    /// <summary>Writes the platforms of the project into .tcagent\config.json (other keys stay).</summary>
    public static string SetPlatforms(string solutionDir, IEnumerable<string> names)
    {
        var root = ProjectRoot(solutionDir) ?? throw new Exception("No solution open - the platform is stored in <solution>\\.tcagent\\config.json");
        var f = Path.Combine(root, "config.json");
        var cfg = ReadConfig(solutionDir) ?? new JsonObject();
        var list = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) cfg.Remove("platforms");
        else cfg["platforms"] = new JsonArray(list.Select(n => (JsonNode)n).ToArray());
        Directory.CreateDirectory(root);
        File.WriteAllText(f, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return f;
    }

    /// <summary>Library names referenced by the PLC projects below the solution folder (placeholders and libraries).</summary>
    public static List<string> ReferencedLibraries(string solutionDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in PlcProjFiles(solutionDir))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            foreach (Match m in Regex.Matches(text, @"<(?:PlaceholderReference|LibraryReference)\s+Include=""([^"",]+)")) set.Add(m.Groups[1].Value.Trim());
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static IEnumerable<string> PlcProjFiles(string solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return Enumerable.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(solutionDir, "*.plcproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                            .Where(f => !f.Contains(@"\_Boot\") && !f.Contains(@"\bin\")).Take(50).ToList();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    static string ProjectText(string solutionDir)
    {
        var sb = new StringBuilder();
        foreach (var f in PlcProjFiles(solutionDir)) try { sb.Append(File.ReadAllText(f)); } catch { }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ layers
    // fixed ranks: the order of the levels (later wins); platforms between user and project
    const int RankBuiltIn = 0, RankCompany = 10, RankUser = 20, RankPlatform = 30, RankProject = 1000;

    public static List<Layer> Layers(string solutionDir, IEnumerable<string> platforms)
    {
        var list = new List<Layer>();
        void Add(string level, string platform, string root, int rank) { if (root != null) list.Add(new Layer(level, platform, root, rank)); }
        Add("built-in", null, BuiltInRoot, RankBuiltIn);
        Add("company", null, CompanyRoot, RankCompany);
        Add("user", null, UserRoot, RankUser);
        var known = KnownPlatforms();
        int i = 0;
        foreach (var p in platforms ?? Enumerable.Empty<string>())
        {
            var def = known.FirstOrDefault(d => d.Name.Equals(p, StringComparison.OrdinalIgnoreCase));
            foreach (var r in def?.Roots ?? new List<string>()) Add("company", p, r, RankPlatform + 2 * i);
            Add("user", p, Path.Combine(UserRoot, "platforms", def?.Name ?? p), RankPlatform + 2 * i + 1);
            i++;
        }
        Add("project", null, ProjectRoot(solutionDir), RankProject);
        return list;
    }

    // ------------------------------------------------------------------ entries
    public sealed class Entry
    {
        public string Kind, Name, Path, Status = "active", Reason;
        public Layer Layer;
        public FrontMatter Meta;
        public bool Locked => Meta?.Flag("locked") == true;
        public bool Force => Meta?.Flag("force") == true;
        public bool Active => Status is "active" or "forced";
        public string Key => Kind + "/" + Name;
    }

    /// <summary>All files of a kind on all layers with their status. replaceByName: rules (a later file with the same name
    /// replaces the earlier one); otherwise all are collected (lessons, knowledge). extraCompany / extraProject: the paths of
    /// the former environment variables and folder names (files or folders).</summary>
    public static List<Entry> Resolve(string kind, string solutionDir, IEnumerable<string> platforms, bool replaceByName,
                                      IEnumerable<string> extraCompany = null, IEnumerable<string> extraProject = null, string pattern = "*.md",
                                      string folderFile = null)
    {
        // folder kinds (skills, workflows): one folder per entry with its main file (SKILL.md / WORKFLOW.md), name = folder name
        if (folderFile != null)
        {
            var found = new List<Entry>();
            foreach (var layer in Layers(solutionDir, platforms))
            {
                var root = Path.Combine(layer.Root, kind);
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var main = Path.Combine(d, folderFile);
                    if (!File.Exists(main)) continue;
                    FrontMatter fm;
                    try { fm = ParseFrontMatter(File.ReadAllText(main)); } catch { fm = new FrontMatter(); }
                    found.Add(new Entry { Kind = kind, Name = Path.GetFileName(d), Path = main, Layer = layer, Meta = fm });
                }
            }
            return ApplyRules(found.OrderBy(e => e.Layer.Rank).ToList(), solutionDir, replaceByName);
        }
        var layers = Layers(solutionDir, platforms);
        var all = new List<Entry>();
        void AddFile(Layer layer, string file, string name)
        {
            if (all.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) return;
            FrontMatter fm;
            try { fm = ParseFrontMatter(File.ReadAllText(file)); } catch { fm = new FrontMatter(); }
            all.Add(new Entry { Kind = kind, Name = name, Path = file, Layer = layer, Meta = fm });
        }
        void AddPath(Layer layer, string p)
        {
            if (p == null) return;
            if (Directory.Exists(p))
                foreach (var f in Directory.GetFiles(p, pattern).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) AddFile(layer, f, Path.GetFileNameWithoutExtension(f));
            else if (File.Exists(p)) AddFile(layer, p, Path.GetFileNameWithoutExtension(p));
        }
        // the former environment variables (TCAGENT_RULES ...) count as company level
        foreach (var p in extraCompany ?? Enumerable.Empty<string>())
            if (Expand(p) is { } x) AddPath(new Layer("company", null, x, RankCompany), x);
        foreach (var layer in layers)
        {
            AddPath(layer, Path.Combine(layer.Root, kind + ".md"));
            AddPath(layer, Path.Combine(layer.Root, kind));
            if (layer.Level == "project") foreach (var p in extraProject ?? Enumerable.Empty<string>()) AddPath(layer, p);
        }
        all = all.OrderBy(e => e.Layer.Rank).ToList();     // stable: the order within a level stays
        return ApplyRules(all, solutionDir, replaceByName);
    }

    /// <summary>Replace / locked / force (replaceByName) and disabled.json on entries sorted by rank.</summary>
    static List<Entry> ApplyRules(List<Entry> all, string solutionDir, bool replaceByName)
    {
        if (replaceByName)
        {
            var current = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in all)
            {
                if (!current.TryGetValue(e.Name, out var cur)) { current[e.Name] = e; continue; }
                if (cur.Locked && e.Layer.Rank > cur.Layer.Rank)
                { e.Status = "refused"; e.Reason = $"the file of {cur.Layer.Label} is locked"; continue; }
                if (cur.Force && cur.Layer.Level == "user" && e.Layer.Platform != null)
                { e.Status = "replaced"; e.Reason = $"kept against it: forced file of {cur.Layer.Label}"; cur.Status = "forced"; continue; }
                cur.Status = "replaced"; cur.Reason = $"replaced by {e.Layer.Label} (same name)";
                current[e.Name] = e;
            }
        }
        // switched off in a disabled.json - a locked file only by its owner (the company level), not by a user or a project
        var disabled = DisabledKeys(solutionDir);
        foreach (var e in all.Where(x => x.Active))
            if (disabled.TryGetValue(e.Key, out var by))
            {
                if (e.Locked && by.Level != "company") e.Reason = "locked - cannot be switched off";
                else { e.Status = "disabled"; e.Reason = "switched off in " + by.Label; }
            }
        return all;
    }

    // ------------------------------------------------------------------ disabled.json
    static string DisabledFile(string root) => root == null ? null : Path.Combine(root, "disabled.json");

    /// <summary>key ("coding-rules/naming") → the level that switched it off (company, user, project).</summary>
    public static Dictionary<string, Layer> DisabledKeys(string solutionDir)
    {
        var d = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        foreach (var (level, root, rank) in new[] { ("company", CompanyRoot, RankCompany), ("user", UserRoot, RankUser), ("project", ProjectRoot(solutionDir), RankProject) })
        {
            var f = DisabledFile(root);
            if (f == null || !File.Exists(f)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(f))?["disabled"] is JsonArray arr)
                    foreach (var k in arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x))) d[k!] = new Layer(level, null, root, rank);
            }
            catch { }
        }
        return d;
    }

    /// <summary>Switches an entry off or on at a level (user or project). Returns the file.</summary>
    public static string SetDisabled(string solutionDir, string level, string key, bool disabled)
    {
        var root = level.Equals("project", StringComparison.OrdinalIgnoreCase) ? ProjectRoot(solutionDir) ?? throw new Exception("No solution open")
                 : level.Equals("user", StringComparison.OrdinalIgnoreCase) ? UserRoot
                 : throw new Exception("scope: user or project");
        var f = DisabledFile(root);
        var list = new List<string>();
        try { if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["disabled"] is JsonArray arr) list.AddRange(arr.Select(x => x?.ToString()).Where(x => x != null)!); } catch { }
        list.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (disabled) list.Add(key);
        Directory.CreateDirectory(root);
        File.WriteAllText(f, new JsonObject { ["disabled"] = new JsonArray(list.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()) }
                                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return f;
    }

    // ------------------------------------------------------------------ splitting a former single file
    /// <summary>Splits a single Markdown file (e.g. coding-rules.md) at its "## " headings into one file per topic in the
    /// folder of the same name. Returns (file, content) - written only if write is true; the single file is then renamed to
    /// *.md.split-&lt;time&gt; (kept as a backup, no longer read).</summary>
    public static List<(string File, string Content)> Split(string singleFile, bool write)
    {
        var text = File.ReadAllText(singleFile).Replace("\r\n", "\n");
        var folder = Path.Combine(Path.GetDirectoryName(singleFile)!, Path.GetFileNameWithoutExtension(singleFile));
        var parts = Regex.Split(text, @"^(?=## )", RegexOptions.Multiline);
        var result = new List<(string, string)>();
        var intro = parts[0].Trim();
        if (intro.Length > 0 && !Regex.IsMatch(intro, @"^#\s+[^\n]*$")) result.Add((Path.Combine(folder, "general.md"), intro + "\n"));
        foreach (var part in parts.Skip(1))
        {
            var title = part.Split('\n')[0][3..].Trim();
            var slug = Slug(title);
            var file = Path.Combine(folder, slug + ".md");
            for (int n = 2; result.Any(r => r.Item1.Equals(file, StringComparison.OrdinalIgnoreCase)) || write && File.Exists(file); n++) file = Path.Combine(folder, $"{slug}-{n}.md");
            result.Add((file, "# " + title + "\n" + part[(part.IndexOf('\n') + 1)..].TrimEnd() + "\n"));
        }
        if (write && result.Count > 0)
        {
            Directory.CreateDirectory(folder);
            foreach (var (f, c) in result) File.WriteAllText(f, c.Replace("\n", "\r\n"), new UTF8Encoding(false));
            File.Move(singleFile, singleFile + ".split-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        }
        return result;
    }

    public static string Slug(string title)
    {
        var s = Regex.Replace((title ?? "").ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss"), @"[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 60) s = s[..60].TrimEnd('-');
        return s.Length == 0 ? "topic" : s;
    }

    /// <summary>Text for the agent: the active platform and, if ambiguous, what to do.</summary>
    public static string PlatformNote(PlatformInfo pi)
    {
        if (pi.Ambiguous.Count > 0)
            return $"PLATFORM UNCLEAR: {pi.Reason}. Ask the user with tc_ask_user which platform this project is built on " +
                   $"(options: {string.Join(", ", pi.Ambiguous)}, or none), then store it with tc_knowledge action=set_platform platforms=<name>.";
        if (pi.Active.Count > 0) return $"Platform: {string.Join(", ", pi.Active)} ({pi.Reason}).";
        return null;
    }
}
