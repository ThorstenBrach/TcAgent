// Backup and restore of the knowledge levels (docs/KNOWLEDGE_LAYERS.md): coding rules, risk rules, skills, workflows,
// lessons, templates, platforms, documentation sources (knowledge.md), sources.json, skills.lock.json, disabled.json and
// config.json of the company, user, team, platform and project level (the built-in level comes with TcAgent).
//
//   when      automatically before every writing action - knowledge window, chat (the knowledge tools), command line -
//             and by hand. A level is only zipped when its content differs from its newest backup, so the newest backup
//             is always the state before the change. The last 20 backups per level are kept.
//   where     %LOCALAPPDATA%\TcAgent\backups\knowledge\<level>-<hash of the folder>\<date>-<reason>.zip with manifest.json
//             (TcAgent version, level, folder, reason, SHA-256 of every file)
//   Git       a level in a Git working copy (team, company, project) is backed up only with its uncommitted changes -
//             what is committed, Git keeps.
//   restore   the whole level, one file or one skill/workflow folder: first the difference, then (after asking) the
//             files - the current state is backed up before.
//
// Pure file logic (git only for the status of a working copy), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class KnowledgeBackup
{
    public const int Keep = 20;

    /// <summary>What belongs to the knowledge of a level (folders and files directly below its root).</summary>
    public static readonly string[] Parts =
    {
        "coding-rules", "coding-rules.md", "risk-rules", "risk-rules.md", "skills", "workflows", "lessons", "lessons.md", "doc-templates",
        "platforms", "platform.md", "knowledge.md", "sources.json", "skills.lock.json", "disabled.json", "config.json"
    };

    public sealed record Level(string Name, string Label, string Root);

    public sealed class Info
    {
        public string Zip, Created, Reason, Version;
        public bool Git;
        public List<(string Path, string Sha256)> Files = new();
        public List<string> Deleted = new();
    }

    public static string Version = "dev";
    /// <summary>Where problems of the automatic backup go (the log of the server); never an exception for the action itself.</summary>
    public static Action<string> Warn = _ => { };

    // ------------------------------------------------------------------ levels
    /// <summary>The levels with knowledge of their own (not built-in); platform folders inside another level are part of it.</summary>
    public static List<Level> Levels(string solutionDir)
    {
        var layers = KnowledgeLayers.Layers(solutionDir, KnowledgeLayers.Platforms(solutionDir).Active).Where(l => l.Level != "built-in").ToList();
        var list = new List<Level>();
        foreach (var l in layers)
        {
            var name = l.Platform != null ? (l.Level == "company" ? "company-platform:" : "platform:") + l.Platform : l.Team != null ? "team:" + l.Team : l.Level;
            var root = Path.GetFullPath(l.Root).TrimEnd('\\', '/');
            if (l.Platform != null && layers.Any(o => o.Platform == null && root.StartsWith(Path.GetFullPath(o.Root).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (list.Any(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new Level(name, l.Label, root));
        }
        return list;
    }

    public static Level Find(string solutionDir, string name)
    {
        var all = Levels(solutionDir);
        var n = (name ?? "").Trim().ToLowerInvariant() is "personal" ? "user" : (name ?? "").Trim();
        return all.FirstOrDefault(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(l => l.Name.StartsWith(n + ":", StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"level '{name}' - one of: {string.Join(", ", all.Select(l => l.Name))}");
    }

    public static string FolderOf(Level l)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(l.Root.ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(KnowledgeLayers.UserRoot, "backups", "knowledge", KnowledgeLayers.Slug(l.Name.Replace(':', '-')) + "-" + hash);
    }

    // ------------------------------------------------------------------ files
    static bool InParts(string rel) => Parts.Any(p => rel.Equals(p, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>The knowledge files of a level; for a Git working copy only the uncommitted ones (deleted ones separately).</summary>
    public static (List<string> Files, List<string> Deleted, bool Git) FilesOf(string root)
    {
        var files = new List<string>(); var deleted = new List<string>();
        if (!Directory.Exists(root)) return (files, deleted, false);
        string top = null;
        try { top = Sources.Git(root, "rev-parse --show-toplevel", 30_000).Replace('/', '\\'); } catch { }
        if (top != null)
        {
            // porcelain v2 (no leading blanks): "1 XY sub mH mI mW hH hI path" changed, "? path" untracked, "! path" ignored -
            // ignored files are not kept by Git either
            // run in the top folder: the paths are then relative to it (v2 writes them relative to the working folder)
            var spec = Path.GetRelativePath(top, root).Replace('\\', '/');
            foreach (var line in Sources.Git(top, $"-c core.quotepath=false status --porcelain=v2 -uall --ignored=matching --no-renames -- \"{spec}\"", 60_000).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string xy, p;
                if (line.StartsWith("1 ")) { var f = line.Split(' ', 9); if (f.Length < 9) continue; xy = f[1]; p = f[8]; }
                else if (line.StartsWith("? ") || line.StartsWith("! ")) { xy = ".."; p = line[2..]; }
                else continue;
                var full = Path.GetFullPath(Path.Combine(top, p.Trim().Trim('"')));
                if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Rel(root, full);
                if (!InParts(rel)) continue;
                if (xy.Contains('D')) deleted.Add(rel); else if (File.Exists(full)) files.Add(rel);
            }
            return (files.OrderBy(x => x, StringComparer.Ordinal).ToList(), deleted, true);
        }
        foreach (var p in Parts)
        {
            var full = Path.Combine(root, p);
            if (File.Exists(full)) files.Add(p);
            else if (Directory.Exists(full))
                files.AddRange(Directory.GetFiles(full, "*", SearchOption.AllDirectories).Select(f => Rel(root, f)));
        }
        return (files.OrderBy(x => x, StringComparer.Ordinal).ToList(), deleted, false);
    }

    static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    // ------------------------------------------------------------------ backup
    /// <summary>Zips the knowledge of a level. Returns the zip, or null when nothing changed since the newest backup
    /// (or a Git level has no uncommitted changes) - unless force.</summary>
    public static string Backup(Level l, string reason, bool force = false)
    {
        var (files, deleted, git) = FilesOf(l.Root);
        if (files.Count == 0 && deleted.Count == 0 && (git || !force)) return null;
        var hashes = files.Select(f => (Path: f, Sha256: Sha(Path.Combine(l.Root, f.Replace('/', '\\'))))).ToList();
        var last = List(l).FirstOrDefault();
        if (!force && last != null && last.Files.SequenceEqual(hashes) && last.Deleted.SequenceEqual(deleted)) return null;
        var folder = FolderOf(l);
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now;
        var zip = Path.Combine(folder, $"{stamp:yyyyMMdd-HHmmss-fff}-{KnowledgeLayers.Slug(reason ?? "backup")}.zip");
        if (zip.Length > 240) zip = Path.Combine(folder, $"{stamp:yyyyMMdd-HHmmss-fff}.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var f in files) z.CreateEntryFromFile(Path.Combine(l.Root, f.Replace('/', '\\')), "files/" + f, CompressionLevel.Optimal);
            var manifest = new JsonObject
            {
                ["tcagent"] = Version, ["created"] = stamp.ToString("s"), ["level"] = l.Name, ["label"] = l.Label, ["root"] = l.Root,
                ["reason"] = reason, ["git"] = git,
                ["files"] = new JsonArray(hashes.Select(h => (JsonNode)new JsonObject { ["path"] = h.Path, ["sha256"] = h.Sha256 }).ToArray()),
                ["deleted"] = new JsonArray(deleted.Select(d => (JsonNode)d).ToArray())
            };
            using var w = new StreamWriter(z.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            w.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var old in Directory.GetFiles(folder, "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Keep))
            try { File.Delete(old); } catch { }
        return zip;
    }

    /// <summary>Before a writing action: every level whose knowledge changed since its newest backup is zipped. Never throws.</summary>
    public static List<string> BeforeWrite(string solutionDir, string reason)
    {
        var made = new List<string>();
        try
        {
            foreach (var l in Levels(solutionDir))
                try { if (Backup(l, reason) is { } z) made.Add(z); }
                catch (Exception ex) { Warn($"Knowledge backup of {l.Name}: {ex.Message}"); }
        }
        catch (Exception ex) { Warn("Knowledge backup: " + ex.Message); }
        return made;
    }

    // ------------------------------------------------------------------ list
    public static Info Read(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        var e = z.GetEntry("manifest.json") ?? throw new InvalidOperationException("not a TcAgent knowledge backup (no manifest.json): " + zip);
        using var r = new StreamReader(e.Open());
        var m = JsonNode.Parse(r.ReadToEnd()) as JsonObject ?? throw new InvalidOperationException("broken manifest: " + zip);
        return new Info
        {
            Zip = zip, Created = m["created"]?.ToString(), Reason = m["reason"]?.ToString(), Version = m["tcagent"]?.ToString(),
            Git = m["git"]?.GetValue<bool>() == true,
            Files = (m["files"]?.AsArray() ?? new JsonArray()).Select(f => (f!["path"]!.ToString(), f["sha256"]!.ToString())).ToList(),
            Deleted = (m["deleted"]?.AsArray() ?? new JsonArray()).Select(d => d!.ToString()).ToList()
        };
    }

    /// <summary>The backups of a level, newest first.</summary>
    public static List<Info> List(Level l)
    {
        var folder = FolderOf(l);
        if (!Directory.Exists(folder)) return new();
        var list = new List<Info>();
        foreach (var f in Directory.GetFiles(folder, "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            try { list.Add(Read(f)); } catch { }
        return list;
    }

    // ------------------------------------------------------------------ restore
    public sealed record Change(string Path, string Kind, string Diff);   // Kind: changed, restored (missing now), removed (not in the backup)

    static bool Selected(string rel, string item) =>
        string.IsNullOrWhiteSpace(item) || rel.Equals(item.Trim('/'), StringComparison.OrdinalIgnoreCase) || rel.StartsWith(item.Trim('/') + "/", StringComparison.OrdinalIgnoreCase);

    static string Text(byte[] b) => Encoding.UTF8.GetString(b).Replace("\r\n", "\n");

    static bool IsText(string rel) => Path.GetExtension(rel).ToLowerInvariant() is ".md" or ".json" or ".txt" or ".yml" or ".yaml" or ".st" or "";

    /// <summary>What a restore would change: the whole level or only item (a file, or a folder such as skills/review).</summary>
    public static List<Change> Plan(Level l, string zip, string item)
    {
        var info = Read(zip);
        var changes = new List<Change>();
        using var z = ZipFile.OpenRead(zip);
        foreach (var (rel, sha) in info.Files.Where(f => Selected(f.Path, item)))
        {
            var full = Path.Combine(l.Root, rel.Replace('/', '\\'));
            if (File.Exists(full) && Sha(full) == sha) continue;
            byte[] data;
            using (var ms = new MemoryStream()) { z.GetEntry("files/" + rel)!.Open().CopyTo(ms); data = ms.ToArray(); }
            var diff = !IsText(rel) ? "(binary)" : LineDiff.Unified(File.Exists(full) ? Text(File.ReadAllBytes(full)) : "", Text(data), rel);
            changes.Add(new Change(rel, File.Exists(full) ? "changed" : "restored", diff));
        }
        // a backup of the whole level (not Git): files that came later go
        if (!info.Git)
            foreach (var rel in FilesOf(l.Root).Files.Where(f => Selected(f, item) && !info.Files.Any(x => x.Path.Equals(f, StringComparison.OrdinalIgnoreCase))))
                changes.Add(new Change(rel, "removed", IsText(rel) ? LineDiff.Unified(Text(File.ReadAllBytes(Path.Combine(l.Root, rel.Replace('/', '\\')))), "", rel) : "(binary)"));
        if (changes.Count == 0 && !string.IsNullOrWhiteSpace(item) && !info.Files.Any(f => Selected(f.Path, item)))
            throw new ArgumentException($"'{item}' is not in this backup");
        return changes;
    }

    /// <summary>Restores (the current state is backed up first) and returns the changes made.</summary>
    public static (List<Change> Changes, string SafetyBackup) Restore(Level l, string zip, string item)
    {
        var plan = Plan(l, zip, item);
        if (plan.Count == 0) return (plan, null);
        var safety = Backup(l, "before restore") ?? List(l).FirstOrDefault()?.Zip;
        using var z = ZipFile.OpenRead(zip);
        foreach (var c in plan)
        {
            var full = Path.Combine(l.Root, c.Path.Replace('/', '\\'));
            if (c.Kind == "removed") { File.Delete(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            z.GetEntry("files/" + c.Path)!.ExtractToFile(full, true);
        }
        return (plan, safety);
    }

    /// <summary>Resolves "latest" or a file name of the level's backups to the zip.</summary>
    public static string Resolve(Level l, string which)
    {
        var all = List(l);
        if (all.Count == 0) throw new ArgumentException($"no backups of the level {l.Name}");
        if (string.IsNullOrWhiteSpace(which) || which.Equals("latest", StringComparison.OrdinalIgnoreCase)) return all[0].Zip;
        if (File.Exists(which)) return Path.GetFullPath(which);
        return all.FirstOrDefault(i => Path.GetFileName(i.Zip).StartsWith(which, StringComparison.OrdinalIgnoreCase))?.Zip
               ?? throw new ArgumentException($"no backup '{which}' of the level {l.Name}");
    }
}
