// Knowledge sources (docs/KNOWLEDGE_LAYERS.md, "Skill sources"): Git repositories that skills, workflows, coding rules,
// risk rules and lessons are installed from - an external collection, a team, platform or company repository.
//
//   <level>\sources.json         the sources of a level: [{ "name", "url", "ref" }] (user level and project level)
//   %LOCALAPPDATA%\TcAgent\sources\<name>\   a clone per source (TcAgent's cache - checked out at the commit it installs)
//   <level>\skills.lock.json     what was installed on that level: source, url, path, commit, checksum, license -
//                                the team and the pipeline get exactly the same files; updates only by pull
//
// Repository layout: the common one of the Agent Skills ecosystem - folders with SKILL.md (skills) or WORKFLOW.md
// (workflows) anywhere in the repository, Markdown files in folders named coding-rules, risk-rules or lessons.
//
// Licenses are detected on install and pull (LICENSE file of the item, the "license" field, the LICENSE of the repository):
// MIT, Apache 2.0, BSD, ISC and public-domain style are accepted and their notice travels with the item; source-available
// licenses and items without a license are refused; other licenses (e.g. copyleft) only with acceptLicense. Scripts in a
// skill are listed before the install and never run by TcAgent.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Sources
{
    public const string LockFile = "skills.lock.json";
    public const string ConfigName = "sources.json";
    public static readonly Dictionary<string, string> FolderKinds = new() { ["skills"] = Skills.MainFile, ["workflows"] = Workflows.MainFile };
    public static readonly string[] FileKinds = { "coding-rules", "risk-rules", "lessons" };
    static readonly string[] ScriptExt = { ".ps1", ".psm1", ".py", ".sh", ".bash", ".js", ".mjs", ".ts", ".bat", ".cmd", ".exe", ".dll", ".vbs" };

    public sealed class Source
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Ref { get; set; } = "HEAD";
        [System.Text.Json.Serialization.JsonIgnore] public string Level { get; set; } = "";
    }

    public sealed class Item
    {
        public string Kind, Name, RelPath, Description, License, LicenseClass, LicenseFile;
        public bool IsFolder => FolderKinds.ContainsKey(Kind);
        public string Key => Kind + "/" + Name;
    }

    public sealed class LockEntry
    {
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public string Path { get; set; } = "";
        public string Commit { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string License { get; set; } = "";
        public DateTime Installed { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ------------------------------------------------------------------ git
    public static string Git(string dir, string args, int timeoutMs = 300_000)
    {
        // own stdin, closed at once: under MCP the inherited stdin is the client's pipe - git (or a credential helper) would wait on it
        var psi = new ProcessStartInfo("git", args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (dir != null) psi.WorkingDirectory = dir;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";     // never wait for credentials in the background
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git not found - install Git for Windows");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException($"git {args}: no answer"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args}: {(err.Result + o).Trim()}");
        return o.Trim();
    }

    // ------------------------------------------------------------------ sources.json
    static string ConfigOf(string root) => Path.Combine(root, ConfigName);

    public static string RootOf(string solutionDir, string scope)
    {
        var s = (scope ?? "personal").Trim().ToLowerInvariant();
        if (s is "project") return KnowledgeLayers.ProjectRoot(solutionDir) ?? throw new ArgumentException("scope=project needs an open solution");
        if (s is "personal" or "user") return KnowledgeLayers.UserRoot;
        if (s is "team") return KnowledgeLayers.TeamWriteRoot();
        if (s.StartsWith("platform:")) return Path.Combine(KnowledgeLayers.UserRoot, "platforms", scope!.Trim()[9..].Trim());
        throw new ArgumentException("scope: personal, project, team or platform:<name> (company and teams in Git change by pull request: promote)");
    }

    public static List<Source> Load(string root)
    {
        try
        {
            var f = ConfigOf(root);
            if (File.Exists(f)) return JsonSerializer.Deserialize<List<Source>>(File.ReadAllText(f), Json) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>The sources of the project and of the user level (project first: its definition wins).</summary>
    public static List<Source> All(string solutionDir)
    {
        var list = new List<Source>();
        if (KnowledgeLayers.ProjectRoot(solutionDir) is { } p) list.AddRange(Load(p).Select(s => { s.Level = "project"; return s; }));
        foreach (var s in Load(KnowledgeLayers.UserRoot)) if (list.All(x => !x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))) { s.Level = "user"; list.Add(s); }
        return list;
    }

    public static string AddSource(string root, string name, string url, string gitRef)
    {
        name = Skills.ValidateName(name);
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url: the Git repository (https://..., a path or a file:// URL)");
        var list = Load(root);
        list.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        list.Add(new Source { Name = name, Url = url.Trim(), Ref = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim() });
        Directory.CreateDirectory(root);
        File.WriteAllText(ConfigOf(root), JsonSerializer.Serialize(list, Json));
        return ConfigOf(root);
    }

    public static bool RemoveSource(string root, string name)
    {
        var list = Load(root);
        if (list.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        File.WriteAllText(ConfigOf(root), JsonSerializer.Serialize(list, Json));
        return true;
    }

    // ------------------------------------------------------------------ clone cache
    public static string CacheOf(string name) => Path.Combine(KnowledgeLayers.UserRoot, "sources", name);
    static string UpstreamFile(string name) => Path.Combine(CacheOf(name), ".git", "tcagent-upstream");

    /// <summary>Clones or fetches the source and returns the newest commit of its ref (network).</summary>
    public static string Fetch(Source s)
    {
        var dir = CacheOf(s.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            Git(null, $"clone -q --no-checkout \"{s.Url}\" \"{dir}\"");
        }
        Git(dir, $"fetch -q \"{s.Url}\" {s.Ref}");
        var commit = Git(dir, "rev-parse FETCH_HEAD");
        File.WriteAllText(UpstreamFile(s.Name), commit);
        return commit;
    }

    /// <summary>The newest commit seen at the last fetch (no network), null if never fetched.</summary>
    public static string KnownUpstream(string name) => File.Exists(UpstreamFile(name)) ? File.ReadAllText(UpstreamFile(name)).Trim() : null;

    /// <summary>Checks the cache out at a commit (fetches it by URL if missing) and returns the folder.</summary>
    public static string Checkout(string name, string url, string commit)
    {
        var dir = CacheOf(name);
        if (!Directory.Exists(Path.Combine(dir, ".git"))) Fetch(new Source { Name = name, Url = url });
        try { Git(dir, $"cat-file -e {commit}^{{commit}}"); }
        catch { Git(dir, $"fetch -q \"{url}\" {commit}"); }
        Git(dir, $"checkout -q --force --detach {commit}");
        return dir;
    }

    // ------------------------------------------------------------------ items of a repository
    public static List<Item> Scan(string repo)
    {
        var items = new List<Item>();
        void Walk(string dir, int depth)
        {
            if (depth > 6) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith(".") || n is "node_modules") continue;
                var kind = FolderKinds.FirstOrDefault(k => File.Exists(Path.Combine(d, k.Value))).Key;
                if (kind != null)
                {
                    var fm = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(Path.Combine(d, FolderKinds[kind])));
                    items.Add(Make(repo, kind, n, d, fm));
                    continue;     // a skill's own subfolders are not searched
                }
                if (FileKinds.Contains(n, StringComparer.OrdinalIgnoreCase))
                    foreach (var f in Directory.GetFiles(d, "*.md"))
                        items.Add(Make(repo, n.ToLowerInvariant(), Path.GetFileNameWithoutExtension(f), f, KnowledgeLayers.ParseFrontMatter(File.ReadAllText(f))));
                Walk(d, depth + 1);
            }
        }
        Walk(repo, 0);
        return items;
    }

    static Item Make(string repo, string kind, string name, string path, KnowledgeLayers.FrontMatter fm)
    {
        var it = new Item { Kind = kind, Name = name, RelPath = Path.GetRelativePath(repo, path).Replace('\\', '/'),
                            Description = fm.Top.TryGetValue("description", out var d) ? d : "" };
        // the item's own license file, its "license" field, the license of the repository
        var own = Directory.Exists(path) ? LicenseFiles(path).FirstOrDefault() : null;
        var root = LicenseFiles(repo).FirstOrDefault();
        var text = own != null ? File.ReadAllText(own) : fm.Top.TryGetValue("license", out var l) && l.Length > 0 ? l : root != null ? File.ReadAllText(root) : null;
        (it.License, it.LicenseClass) = ClassifyLicense(text);
        it.LicenseFile = own ?? root;
        return it;
    }

    static IEnumerable<string> LicenseFiles(string dir) =>
        Directory.GetFiles(dir).Where(f => Regex.IsMatch(Path.GetFileName(f), @"^(LICEN[CS]E|COPYING)(\.(md|txt))?$", RegexOptions.IgnoreCase));

    /// <summary>License id and class: accepted, copyleft, unknown, source-available, none.</summary>
    public static (string Id, string Class) ClassifyLicense(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("none", "none");
        var t = text.Trim();
        if (Regex.IsMatch(t, @"Business Source License|Elastic License|Commons Clause|PolyForm|Server Side Public License|Functional Source License|BUSL-|SSPL|Elastic-2\.0|FSL-", RegexOptions.IgnoreCase)) return ("source-available", "source-available");
        if (Regex.IsMatch(t, @"^MIT$|Permission is hereby granted, free of charge", RegexOptions.IgnoreCase)) return ("MIT", "accepted");
        if (Regex.IsMatch(t, @"^Apache-2\.0$|Apache License,?\s+Version 2\.0", RegexOptions.IgnoreCase)) return ("Apache-2.0", "accepted");
        if (Regex.IsMatch(t, @"^BSD-[23]-Clause$|Redistribution and use in source and binary forms", RegexOptions.IgnoreCase)) return ("BSD", "accepted");
        if (Regex.IsMatch(t, @"^ISC$|Permission to use, copy, modify, and/or distribute this software for any purpose", RegexOptions.IgnoreCase)) return ("ISC", "accepted");
        if (Regex.IsMatch(t, @"^(CC0-1\.0|Unlicense|0BSD)$|free and unencumbered software released into the public domain|CC0 1\.0 Universal", RegexOptions.IgnoreCase)) return ("public-domain", "accepted");
        if (Regex.IsMatch(t, @"GNU (Affero |Lesser )?General Public License|^(A|L)?GPL-|Mozilla Public License|^MPL-", RegexOptions.IgnoreCase)) return ("copyleft", "copyleft");
        return (t.Length <= 40 && !t.Contains('\n') ? t : "unknown", "unknown");
    }

    public static string LicenseRefusal(Item it, bool accept) => it.LicenseClass switch
    {
        "accepted" => null,
        "source-available" => $"{it.Key}: source-available license - not taken over (it limits the use)",
        "none" => $"{it.Key}: no license - all rights reserved, not taken over",
        _ when accept => null,
        _ => $"{it.Key}: license '{it.License}' ({it.LicenseClass}) - not taken over by default; check it and install with acceptLicense=true"
    };

    public static List<string> FilesOf(string repo, Item it) =>
        it.IsFolder ? Directory.GetFiles(Path.Combine(repo, it.RelPath), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.Combine(repo, it.RelPath), f).Replace('\\', '/')).OrderBy(x => x).ToList()
                    : new List<string> { Path.GetFileName(it.RelPath) };

    public static List<string> Scripts(IEnumerable<string> files) => files.Where(f => ScriptExt.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();

    // ------------------------------------------------------------------ install
    public static string TargetOf(string root, Item it) =>
        it.IsFolder ? Path.Combine(root, it.Kind, it.Name) : Path.Combine(root, it.Kind, Path.GetFileName(it.RelPath));

    /// <summary>Copies the item (checked out in repo) to the level root, records source and license in its front matter and the lock file.</summary>
    public static LockEntry Install(string root, string repo, Item it, Source s, string commit)
    {
        var target = TargetOf(root, it);
        var src = Path.Combine(repo, it.RelPath);
        var meta = new Dictionary<string, string> { ["tcagent-source"] = s.Name + "/" + it.RelPath, ["tcagent-commit"] = commit[..Math.Min(12, commit.Length)], ["tcagent-license"] = it.License };
        if (it.IsFolder)
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, true);
            }
            var main = Path.Combine(target, FolderKinds[it.Kind]);
            File.WriteAllText(main, AddMeta(File.ReadAllText(main), meta));
            // the notice travels with the item
            if (it.LicenseFile != null && !LicenseFiles(target).Any()) File.Copy(it.LicenseFile, Path.Combine(target, "LICENSE.txt"));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, AddMeta(File.ReadAllText(src), meta));
            if (it.LicenseFile != null) File.Copy(it.LicenseFile, Path.ChangeExtension(target, ".LICENSE.txt"), true);
        }
        var e = new LockEntry { Source = s.Name, Url = s.Url, Path = it.RelPath, Commit = commit, Sha256 = Checksum(target), License = it.License, Installed = DateTime.Now };
        var lk = LoadLock(root);
        lk[it.Key] = e;
        SaveLock(root, lk);
        return e;
    }

    /// <summary>Adds keys to the metadata block of the front matter (creates the front matter or the block if missing).</summary>
    public static string AddMeta(string text, IDictionary<string, string> meta)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var t = text.Replace("\r\n", "\n").TrimStart('﻿');
        string block(string indent) => string.Concat(meta.Select(kv => $"{indent}{kv.Key}: {kv.Value}\n"));
        int end = t.StartsWith("---\n") ? t.IndexOf("\n---", 4, StringComparison.Ordinal) : -1;
        if (end < 0)
            return ("---\nmetadata:\n" + block("  ") + "---\n" + t).Replace("\n", nl);
        var head = t[4..(end + 1)];
        // drop earlier values of the same keys (re-install)
        head = string.Concat(head.Split('\n').Where(l => l.Length > 0 && !meta.Keys.Any(k => Regex.IsMatch(l, @"^\s+" + Regex.Escape(k) + @"\s*:"))).Select(l => l + "\n"));
        var m = Regex.Match(head, @"(?m)^metadata:\s*\n((?:[ \t]+.*\n)*)");
        head = m.Success ? head.Insert(m.Index + m.Length, block("  ")) : head + "metadata:\n" + block("  ");
        return ("---\n" + head + t[(end + 1)..]).Replace("\n", nl);
    }

    public static string Checksum(string path)
    {
        using var sha = SHA256.Create();
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
                  : File.Exists(path) ? new[] { path } : Array.Empty<string>();
        if (files.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append(Path.GetRelativePath(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, f).Replace('\\', '/')).Append('\n')
              .Append(Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("\r\n", "\n"))))).Append('\n');
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ lock file
    public static Dictionary<string, LockEntry> LoadLock(string root)
    {
        try
        {
            var f = Path.Combine(root, LockFile);
            if (File.Exists(f)) return JsonSerializer.Deserialize<Dictionary<string, LockEntry>>(File.ReadAllText(f), Json) ?? new();
        }
        catch { }
        return new();
    }

    static void SaveLock(string root, Dictionary<string, LockEntry> lk)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, LockFile), JsonSerializer.Serialize(new SortedDictionary<string, LockEntry>(lk, StringComparer.Ordinal), Json));
    }

    public static void Unlock(string root, string key)
    {
        var lk = LoadLock(root);
        if (lk.Remove(key)) SaveLock(root, lk);
    }

    public static string KindDir(string key) => key.Split('/')[0];

    /// <summary>The installed path of a lock entry.</summary>
    public static string InstalledPath(string root, string key, LockEntry e) =>
        FolderKinds.ContainsKey(KindDir(key)) ? Path.Combine(root, KindDir(key), key.Split('/')[1]) : Path.Combine(root, KindDir(key), Path.GetFileName(e.Path));

    /// <summary>Status of the installed items of a level: ok, changed locally, missing, newer upstream (as of the last fetch).</summary>
    public static List<(string Key, string State, LockEntry E)> Status(string root)
    {
        var list = new List<(string, string, LockEntry)>();
        foreach (var (key, e) in LoadLock(root).OrderBy(x => x.Key))
        {
            var p = InstalledPath(root, key, e);
            string state = !Directory.Exists(p) && !File.Exists(p) ? "missing" : Checksum(p) != e.Sha256 ? "changed locally" : "ok";
            var up = KnownUpstream(e.Source);
            if (up != null && up != e.Commit)
                try { if (Git(CacheOf(e.Source), $"log --format=%h {e.Commit}..{up} -- \"{e.Path}\"").Length > 0) state += ", newer upstream"; }
                catch { state += ", upstream unknown"; }
            list.Add((key, state, e));
        }
        return list;
    }

    // ------------------------------------------------------------------ shared levels: pull request only
    /// <summary>Copies a file or folder into a Git repository as a commit on a new branch and switches back. Never pushed.</summary>
    public static string CommitForPullRequest(string repo, string source, string target, string message)
    {
        if (string.IsNullOrEmpty(repo) || !Directory.Exists(repo)) throw new InvalidOperationException("the shared level does not exist");
        try { Git(repo, "rev-parse --is-inside-work-tree"); }
        catch { throw new InvalidOperationException("the shared level is not a Git repository - ask its owner"); }
        if (Git(repo, "status --porcelain").Length > 0) throw new InvalidOperationException("the shared repository has uncommitted changes - commit or stash them first");
        if (File.Exists(target) || Directory.Exists(target)) throw new InvalidOperationException($"{target} exists already");
        var previous = Git(repo, "rev-parse --abbrev-ref HEAD");
        var branch = "tcagent/" + KnowledgeLayers.Slug(Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/'))) + "-" + DateTime.Now.ToString("yyyyMMddHHmm");
        Git(repo, $"switch -q -c {branch}");
        try
        {
            if (Directory.Exists(source))
                foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(target, Path.GetRelativePath(source, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to);
                }
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
            Git(repo, $"add -- \"{Path.GetRelativePath(repo, target)}\"");
            Git(repo, $"commit -q -m \"{message.Replace("\"", "'")}\"");
        }
        catch
        {
            try { Git(repo, "reset -q --hard"); Git(repo, "clean -fdq -- \"" + Path.GetRelativePath(repo, target) + "\""); } catch { }
            throw;
        }
        finally { try { Git(repo, $"switch -q {previous}"); } catch { } }
        return $"Committed on branch {branch} in {repo} (not pushed). Push the branch and open a pull request - the owners of that level decide.";
    }
}
