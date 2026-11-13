// What TcAgent keeps under %LOCALAPPDATA%\TcAgent and how long (PLAN 15.14, docs/USER_GUIDE.md "Storage").
//
// Areas with a rule (settings.json "retention" changes or switches off each one):
//   chats        conversations per solution          90 days, the newest 50 per solution
//   attachments  files given to the chat             30 days
//   tcunit       the last run per project            projects without a run for 90 days
//   logs         logs\*.log, bridge\*.log            30 days; a log over 5 MB is set aside as .old (the previous .old goes)
//   caches       libcache, doccache                  not written for 90 days
//   app          app\<version> (the server)          the newest 2 and the one registered for the AI clients stay
//   updates      downloaded and unpacked packages    14 days
//   bak          *.bak-<time> of the config files    the newest 3 per file
//   stale        busy.<pid>.txt, reload.<pid>.json   when that process no longer runs
// Never touched: backups, the knowledge files of the user level, settings, the audit log, sources, plugins, workflow runs
// (they keep themselves). Deleting goes to the recycle bin unless --permanent (a pipeline).
//
// Once a day the extension cleans the safe areas in the background (Auto); chats and TcUnit runs only on request.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Storage
{
    public static string RootOverride;      // tests
    public static string Root => RootOverride ?? KnowledgeLayers.UserRoot;

    public sealed record AreaDef(string Key, string Title, string Rule, bool Auto, int Days, int Keep);
    public sealed record Item(string Area, string Path, long Bytes, DateTime Time, string Why, bool Rotate = false);

    static readonly AreaDef[] Defaults =
    {
        new("chats", "Chat conversations", "{days} days, the newest {keep} per solution", false, 90, 50),
        new("attachments", "Chat attachments", "{days} days", true, 30, 0),
        new("tcunit", "TcUnit runs", "projects without a run for {days} days", false, 90, 0),
        new("logs", "Logs", "{days} days; over {keep} MB set aside as .old", true, 30, 5),
        new("caches", "Library and document caches", "not written for {days} days", true, 90, 0),
        new("app", "Server versions (app)", "the newest {keep} and the registered one stay", true, 0, 2),
        new("updates", "Downloaded updates", "{days} days", true, 14, 0),
        new("bak", "Backups of config files (*.bak-*)", "the newest {keep} per file", true, 0, 3),
        new("stale", "Files of ended processes", "busy/reload files without their process", true, 0, 0),
    };

    // ------------------------------------------------------------------ settings.json "retention"
    /// <summary>The rules with the values of settings.json "retention": { "chats": { "days": 60, "keep": 30 }, "logs": false, "auto": false }.</summary>
    public static List<(AreaDef Def, bool On)> Rules()
    {
        JsonObject r = null;
        try
        {
            var f = Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            if (File.Exists(f)) r = JsonNode.Parse(File.ReadAllText(f))?["retention"] as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        var list = new List<(AreaDef, bool)>();
        foreach (var d in Defaults)
        {
            var on = true; var def = d;
            switch (r?[d.Key])
            {
                case JsonValue v when v.TryGetValue<bool>(out var b): on = b; break;
                case JsonObject o:
                    def = d with { Days = o["days"] is JsonValue dv && dv.TryGetValue<int>(out var days) ? Math.Max(1, days) : d.Days,
                                   Keep = o["keep"] is JsonValue kv && kv.TryGetValue<int>(out var keep) ? Math.Max(1, keep) : d.Keep };
                    if (o["on"] is JsonValue ov && ov.TryGetValue<bool>(out var ob)) on = ob;
                    break;
            }
            list.Add((def, on));
        }
        return list;
    }

    /// <summary>The daily background run is on unless settings.json "retention": { "auto": false }.</summary>
    public static bool AutoOn()
    {
        try
        {
            var f = Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            return !(File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["retention"]?["auto"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return true; }
    }

    public static string RuleText(AreaDef d) => d.Rule.Replace("{days}", d.Days.ToString()).Replace("{keep}", d.Keep.ToString());

    // ------------------------------------------------------------------ what is there
    static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>The files and folders of an area (each folder counts as one entry with its size).</summary>
    public static List<Item> Contents(string area)
    {
        var list = new List<Item>();
        void Files(string dir, string pattern, SearchOption o = SearchOption.TopDirectoryOnly)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern, o)) list.Add(new Item(area, f, Size(f), Time(f), ""));
        }
        void Dirs(string dir, string pattern = "*")
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir, pattern)) list.Add(new Item(area, d, Size(d), Time(d), ""));
        }
        switch (area)
        {
            case "chats": if (Directory.Exists(P("chats"))) foreach (var d in Directory.GetDirectories(P("chats"))) Files(d, "*.jsonl"); break;
            case "attachments": Dirs(P("attachments")); Files(P("attachments"), "*"); break;
            case "tcunit": Dirs(P("tcunit")); break;
            case "logs": Files(P("logs"), "*.log"); Files(P("bridge"), "*.log*"); Files(P("updates"), "*.log"); break;
            case "caches": Files(P("libcache"), "*", SearchOption.AllDirectories); Files(P("doccache"), "*", SearchOption.AllDirectories); break;
            case "app": Dirs(P("app")); break;
            case "updates":
                Dirs(P("updates"));
                if (Directory.Exists(P("updates")))
                    foreach (var f in Directory.GetFiles(P("updates")).Where(f => f.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
                        list.Add(new Item(area, f, Size(f), Time(f), ""));
                break;
            case "bak": if (Directory.Exists(Root)) foreach (var f in Directory.GetFiles(Root).Where(f => BakRx.IsMatch(Path.GetFileName(f)))) list.Add(new Item(area, f, Size(f), Time(f), "")); break;
            case "stale": if (Directory.Exists(Root)) foreach (var f in Directory.GetFiles(Root).Where(f => PidRx.IsMatch(Path.GetFileName(f)))) list.Add(new Item(area, f, Size(f), Time(f), "")); break;
        }
        return list;
    }

    static readonly Regex BakRx = new(@"^(?<base>.+)\.bak-\d{8}-\d{6}$", RegexOptions.IgnoreCase);
    static readonly Regex PidRx = new(@"^(busy|reload)\.(?<pid>\d+)\.(txt|json)$", RegexOptions.IgnoreCase);

    static long Size(string p)
    {
        try { return File.Exists(p) ? new FileInfo(p).Length : Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>A file: its time. A folder: the time of its newest file.</summary>
    static DateTime Time(string p)
    {
        try
        {
            if (File.Exists(p)) return File.GetLastWriteTime(p);
            // the newest file counts (a folder gets a new time when a file in it is created or renamed); an empty folder: its own
            DateTime? t = null;
            foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)) { var ft = File.GetLastWriteTime(f); if (t == null || ft > t) t = ft; }
            return t ?? Directory.GetLastWriteTime(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    // ------------------------------------------------------------------ what the rules remove
    /// <summary>What the rules would remove now (area: one of them; auto: only the areas of the daily run).</summary>
    public static List<Item> Plan(string area = null, bool auto = false, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var plan = new List<Item>();
        foreach (var (d, on) in Rules())
        {
            if (!on || (area != null && !d.Key.Equals(area, StringComparison.OrdinalIgnoreCase)) || (auto && !d.Auto)) continue;
            var c = Contents(d.Key);
            var limit = t - TimeSpan.FromDays(Math.Max(1, d.Days));
            switch (d.Key)
            {
                case "chats":
                    foreach (var g in c.GroupBy(i => Path.GetDirectoryName(i.Path)))
                    {
                        int n = 0;
                        foreach (var i in g.OrderByDescending(i => Path.GetFileName(i.Path), StringComparer.Ordinal))
                        {
                            n++;
                            if (n > d.Keep) plan.Add(i with { Why = $"more than {d.Keep} in this solution" });
                            else if (i.Time < limit) plan.Add(i with { Why = $"older than {d.Days} days" });
                        }
                    }
                    break;
                case "logs":
                    foreach (var i in c)
                        if (i.Time < limit) plan.Add(i with { Why = $"older than {d.Days} days" });
                        else if (i.Bytes > d.Keep * 1024L * 1024L && !i.Path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) plan.Add(i with { Why = $"over {d.Keep} MB - set aside as .old", Rotate = true });
                    break;
                case "app":
                {
                    var keep = AppKeep(c.Select(i => Path.GetFileName(i.Path)), d.Keep);
                    foreach (var i in c.Where(i => !keep.Contains(Path.GetFileName(i.Path)))) plan.Add(i with { Why = "an older server version" });
                    break;
                }
                case "bak":
                    foreach (var g in c.GroupBy(i => BakRx.Match(Path.GetFileName(i.Path)).Groups["base"].Value, StringComparer.OrdinalIgnoreCase))
                        foreach (var i in g.OrderByDescending(i => Path.GetFileName(i.Path), StringComparer.Ordinal).Skip(d.Keep)) plan.Add(i with { Why = $"more than {d.Keep} backups of {g.Key}" });
                    break;
                case "stale":
                    foreach (var i in c.Where(i => !Running(int.Parse(PidRx.Match(Path.GetFileName(i.Path)).Groups["pid"].Value)))) plan.Add(i with { Why = "its process has ended" });
                    break;
                default:        // attachments, tcunit, caches, updates
                    foreach (var i in c.Where(i => i.Time < limit)) plan.Add(i with { Why = d.Key == "tcunit" ? $"no run for {d.Days} days" : $"older than {d.Days} days" });
                    break;
            }
        }
        return plan;
    }

    /// <summary>The server versions that stay: the newest keep, the one registered for the AI clients, the one that runs.</summary>
    static HashSet<string> AppKeep(IEnumerable<string> versions, int keep)
    {
        var all = versions.ToList();
        var stay = all.Where(v => Updates.ParseVersion(v) != null).OrderByDescending(v => Updates.ParseVersion(v)).Take(keep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        stay.UnionWith(all.Where(v => Updates.ParseVersion(v) == null));           // something else in app\: not ours to judge
        try
        {
            var f = Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["server"]?["version"]?.ToString() is { } reg) stay.Add(reg);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        var running = Path.GetFileName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        if (all.Contains(running, StringComparer.OrdinalIgnoreCase)) stay.Add(running);
        return stay;
    }

    static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ------------------------------------------------------------------ clean
    public sealed record Result(int Removed, long Bytes, List<string> Errors);

    /// <summary>Removes the items (recycle bin unless permanent); a log over the limit is renamed to .old. A file in use is
    /// left and named in Errors.</summary>
    public static Result Clean(IEnumerable<Item> items, bool permanent)
    {
        int n = 0; long bytes = 0; var errors = new List<string>();
        foreach (var i in items)
            try
            {
                if (i.Rotate)
                {
                    var old = i.Path + ".old";
                    if (File.Exists(old)) Remove(old, permanent);
                    File.Move(i.Path, old);
                }
                else Remove(i.Path, permanent);
                n++; bytes += i.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { errors.Add($"{i.Path}: {ex.Message}"); }
        return new Result(n, bytes, errors);
    }

    static void Remove(string path, bool permanent)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (!permanent && OperatingSystem.IsWindows() && ToRecycleBin(path)) return;
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
    }

    // SHFileOperation with FOF_ALLOWUNDO: the recycle bin, no dialogs
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp { public IntPtr Hwnd; public uint Func; public string From; public string To; public ushort Flags; public bool Aborted; public IntPtr Mappings; public string Title; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref ShFileOp op);

    static bool ToRecycleBin(string path)
    {
        const uint FO_DELETE = 3; const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new ShFileOp { Func = FO_DELETE, From = Path.GetFullPath(path) + "\0\0", Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
        return SHFileOperation(ref op) == 0 && !op.Aborted && !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>The daily background run (extension): the safe areas, recycle bin, at most once a day.</summary>
    public static JsonObject Auto(bool force)
    {
        var st = Updates.LoadState();
        var o = new JsonObject { ["ran"] = false };
        if (!AutoOn()) { o["reason"] = "switched off (settings.json retention.auto)"; return o; }
        if (!force && !Updates.Due(st["storageClean"]?.ToString(), DateTime.UtcNow)) { o["reason"] = "already today"; return o; }
        st["storageClean"] = DateTime.UtcNow.ToString("o");
        Updates.SaveState(st);
        var r = Clean(Plan(auto: true), permanent: false);
        o["ran"] = true; o["removed"] = r.Removed; o["bytes"] = r.Bytes; o["errors"] = new JsonArray(r.Errors.Select(e => (JsonNode)e).ToArray());
        return o;
    }

    // ------------------------------------------------------------------ per project
    public sealed record ProjectPart(string Area, string Path, long Bytes, DateTime Time);
    public sealed record Project(string Name, string SolutionDir, bool Exists, List<ProjectPart> Parts)
    {
        public long Bytes => Parts.Sum(p => p.Bytes);
        public DateTime Last => Parts.Count == 0 ? DateTime.MinValue : Parts.Max(p => p.Time);
    }

    /// <summary>What TcAgent keeps per project: chats\&lt;name&gt;-&lt;hash&gt;, tcunit\&lt;name&gt;-&lt;hash&gt;, runs\&lt;name&gt;; grouped by the
    /// project name. The solution folder comes from solution.json of the chats, where there is one.</summary>
    public static List<Project> Projects()
    {
        var parts = new Dictionary<string, List<ProjectPart>>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string area, string path)
        {
            if (!parts.TryGetValue(name, out var l)) parts[name] = l = new List<ProjectPart>();
            l.Add(new ProjectPart(area, path, Size(path), Time(path)));
        }
        static string Name(string folder) => Regex.Replace(Path.GetFileName(folder), @"-[0-9a-fA-F]{8}$", "");
        foreach (var (area, sub) in new[] { ("chats", "chats"), ("tcunit", "tcunit"), ("runs", "runs") })
        {
            if (!Directory.Exists(P(sub))) continue;
            foreach (var d in Directory.GetDirectories(P(sub)))
            {
                var n = Name(d);
                if (n.StartsWith("_")) continue;            // chats\_no-solution
                Add(n, area, d);
                var info = Path.Combine(d, "solution.json");
                if (area == "chats" && File.Exists(info))
                    try { if (JsonNode.Parse(File.ReadAllText(info))?["solutionDir"]?.ToString() is { Length: > 0 } sd) dirs[n] = sd; } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            }
        }
        return parts.Select(kv =>
        {
            dirs.TryGetValue(kv.Key, out var sd);
            var exists = sd == null || (Directory.Exists(sd) && Directory.GetFiles(sd, "*.sln").Length > 0);
            return new Project(kv.Key, sd, exists, kv.Value);
        }).OrderByDescending(p => p.Last).ToList();
    }

    public static Result DeleteProject(string name, bool permanent) =>
        Clean((Projects().FirstOrDefault(p => p.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"no stored files of a project named '{name}'"))
              .Parts.Select(p => new Item(p.Area, p.Path, p.Bytes, p.Time, "project")), permanent);
}
