// Newer built-in knowledge without a new TcAgent (PLAN 15.4, docs/KNOWLEDGE_LAYERS.md "Updating the built-in level").
//
// The skills, workflows, prompts and risk rules of the built-in level come with the package (knowledge\ next to TcAgent.exe,
// the Git submodule TcAgent_Knowledge at the commit of the release). Between two releases TcAgent offers a newer version of
// that repository - its tags vX.Y.Z - and, once the user takes it, uses it as the built-in level instead of the package:
//
//   %LOCALAPPDATA%\TcAgent\builtin\repo\        a bare clone of the knowledge repository (tags only)
//   %LOCALAPPDATA%\TcAgent\builtin\<version>\   the files of a tag; the one in use and the one before it are kept
//   update-state.json "builtin"                 { active, previous, skipped, lastCheck, error, offer }
//
// Nothing changes by itself: skills are instructions to the agent, so a new version is shown with its changelog and taken over
// by the user (TcAgent Chat). Rules:
//   - the package stays the fallback: without a chosen version, offline, or when the package is as new or newer;
//   - a version is used only if this TcAgent is new enough for it ("Needs TcAgent x.y.z" in its CHANGELOG.md section);
//   - pipelines stay on the package (CI variables, TCAGENT_BUILTIN=package) - --fail-on must not change by itself;
//   - updates.json of the company or a team: "builtinUpdates": false switches it off (package only), "builtinVersion": "1.1.0"
//     pins a version ("package" = the one in the package).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class BuiltInKnowledge
{
    public const string RepoUrl = "https://github.com/ThorstenBrach/TcAgent_Knowledge.git";
    public const string SwitchKey = "builtinUpdates", PinKey = "builtinVersion", StateKey = "builtin", EnvKey = "TCAGENT_BUILTIN";

    /// <summary>Test hooks: the repository and the package folder.</summary>
    public static string RepoUrlOverride, PackageRootOverride;
    public static bool? PipelineOverride;

    static string Url => RepoUrlOverride ?? RepoUrl;
    static string Base => Path.Combine(KnowledgeLayers.UserRoot, "builtin");
    static string Repo => Path.Combine(Base, "repo");
    public static string Folder(string version) => Path.Combine(Base, version);
    public static string PackageRoot => PackageRootOverride ?? Path.Combine(AppContext.BaseDirectory, "knowledge");

    // ------------------------------------------------------------------ versions
    public sealed record Info(string Version, string NeedsTcAgent);

    /// <summary>The version of a knowledge folder and the TcAgent it needs: the newest section of its CHANGELOG.md.</summary>
    public static Info VersionOf(string root)
    {
        try
        {
            var f = Path.Combine(root ?? "", "CHANGELOG.md");
            return File.Exists(f) ? Parse(File.ReadAllText(f)) : new Info(null, null);
        }
        catch (IOException) { return new Info(null, null); }
    }

    public static Info Parse(string changelog)
    {
        var s = Updates.ChangelogSections(changelog);
        var newest = s.Keys.Select(k => (V: Updates.ParseVersion(k), K: k)).Where(x => x.V != null).OrderByDescending(x => x.V).FirstOrDefault();
        return newest.K == null ? new Info(null, null) : new Info(newest.K, NeedsOf(s[newest.K]));
    }

    static string NeedsOf(string section) => Regex.Match(section ?? "", @"Needs TcAgent\s+v?(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Is this TcAgent new enough for a knowledge version that needs needs (none named: yes)?</summary>
    public static bool Fits(string needs, string tcagent) =>
        Updates.ParseVersion(needs) is not { } n || (Updates.ParseVersion(tcagent) ?? new Version(0, 0)) >= n;

    static bool Newer(string a, string b) => Updates.ParseVersion(a) is { } va && (Updates.ParseVersion(b) is not { } vb || va > vb);

    // ------------------------------------------------------------------ which folder is the built-in level
    /// <summary>A pipeline keeps the package: the usual CI variables, or TCAGENT_BUILTIN=package.</summary>
    public static bool InPipeline => PipelineOverride ??
        (Environment.GetEnvironmentVariable(EnvKey)?.Trim().Equals("package", StringComparison.OrdinalIgnoreCase) == true ||
        new[] { "CI", "TF_BUILD", "GITHUB_ACTIONS", "GITLAB_CI", "JENKINS_URL", "BUILD_BUILDID", "TEAMCITY_VERSION" }
            .Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)) && Environment.GetEnvironmentVariable(v) != "false"));

    static (DateTime Stamp, string Root, bool Have) cached;
    static readonly object Gate = new();

    /// <summary>The root of the built-in level: the chosen version from the cache when it may be used, otherwise the package.</summary>
    public static string ActiveRoot()
    {
        if (InPipeline) return PackageRoot;
        var file = Path.Combine(KnowledgeLayers.UserRoot, "update-state.json");
        DateTime stamp;
        try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; } catch (IOException) { stamp = DateTime.MinValue; }
        lock (Gate)
        {
            if (cached.Have && cached.Stamp == stamp) return cached.Root;
            var root = Choose(Updates.LoadState()).Root;
            cached = (stamp, root, true);
            return root;
        }
    }

    /// <summary>Forget the cached choice (after apply / back / tests).</summary>
    public static void Reset() { lock (Gate) cached = default; }

    /// <summary>The root and why: "package", "cache", "pinned", "package (…)".</summary>
    public static (string Root, string Version, string Why) Choose(JsonObject st)
    {
        var pkg = VersionOf(PackageRoot);
        if (InPipeline) return (PackageRoot, pkg.Version, "package (pipeline)");
        var sw = Updates.Setting(SwitchKey, st);
        if (sw.LockedBy != null && !sw.On) return (PackageRoot, pkg.Version, $"package (switched off on the {sw.LockedBy} level)");
        string want = Pin().Version ?? st[StateKey]?["active"]?.ToString();
        if (string.IsNullOrEmpty(want) || want.Equals("package", StringComparison.OrdinalIgnoreCase)) return (PackageRoot, pkg.Version, Pin().Version != null ? "package (pinned)" : "package");
        var dir = Folder(want);
        var info = VersionOf(dir);
        if (info.Version == null) return (PackageRoot, pkg.Version, $"package ({want} is not in the cache)");
        if (!Fits(info.NeedsTcAgent, KnowledgeLayers.TcAgentVersion)) return (PackageRoot, pkg.Version, $"package ({want} needs TcAgent {info.NeedsTcAgent})");
        if (Pin().Version == null && !Newer(info.Version, pkg.Version)) return (PackageRoot, pkg.Version, "package (as new as " + want + " or newer)");
        return (dir, info.Version, Pin().Version != null ? "pinned" : "cache");
    }

    /// <summary>A version fixed in updates.json of the company or a team ("builtinVersion").</summary>
    public static (string Version, string By) Pin()
    {
        foreach (var (label, root) in new[] { ("company", KnowledgeLayers.CompanyRoot) }.Concat(KnowledgeLayers.TeamRoots.Select(t => ("team", t))))
        {
            var f = root == null ? null : Path.Combine(root, "updates.json");
            if (f == null || !File.Exists(f)) continue;
            try { if (JsonNode.Parse(File.ReadAllText(f))?[PinKey] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return (s.Trim().TrimStart('v', 'V'), label); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return (null, null);
    }

    // ------------------------------------------------------------------ the repository
    static string Git(string args) => Sources.Git(null, args);

    /// <summary>Clones (bare) or fetches the tags of the knowledge repository; returns the release versions, newest first.</summary>
    static List<string> FetchTags()
    {
        Directory.CreateDirectory(Base);
        if (!Directory.Exists(Repo)) Git($"clone -q --bare --no-tags \"{Url}\" \"{Repo}\"");
        Git($"--git-dir=\"{Repo}\" fetch -q --prune --prune-tags \"{Url}\" \"+refs/tags/*:refs/tags/*\"");
        return Git($"--git-dir=\"{Repo}\" tag --list").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => Regex.IsMatch(t, @"^v?\d+(\.\d+){1,3}$")).OrderByDescending(t => Updates.ParseVersion(t)).ToList();
    }

    static string TagOf(string version) =>
        Git($"--git-dir=\"{Repo}\" tag --list").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(t => Updates.ParseVersion(t)?.ToString() == Updates.ParseVersion(version)?.ToString())
        ?? throw new ArgumentException($"knowledge {version} is not a tag of {Url}");

    static string ChangelogAt(string tag)
    {
        try { return Git($"--git-dir=\"{Repo}\" show \"{tag}:CHANGELOG.md\""); }
        catch (InvalidOperationException) { return ""; }
    }

    // ------------------------------------------------------------------ check, apply, back, skip
    /// <summary>Looks for a newer knowledge version (once a day, or forced) and describes the state. Never in a pipeline.</summary>
    public static JsonObject Check(bool force)
    {
        var st = Updates.LoadState();
        var b = st[StateKey] as JsonObject ?? new JsonObject();
        var sw = Updates.Setting(SwitchKey, st);
        var pin = Pin();
        if (!InPipeline && pin.Version == null && (sw.LockedBy == null || sw.On) && (force || (sw.On && Updates.Due(b["lastCheck"]?.ToString(), DateTime.UtcNow))))
        {
            b["lastCheck"] = DateTime.UtcNow.ToString("o");
            try
            {
                Sources.NoSignInDialog = !force;
                var tags = FetchTags();
                b.Remove("error");
                b["offer"] = Offer(tags, Choose(st).Version);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
            {
                b["error"] = ex.Message.Split('\n')[0];
            }
            st[StateKey] = b;
            Updates.SaveState(st);
        }
        return Describe(Updates.LoadState());
    }

    /// <summary>The newest tag newer than current that this TcAgent can use, with the changes since current; a newer one that
    /// needs a newer TcAgent is named too.</summary>
    static JsonNode Offer(List<string> tags, string current)
    {
        var newer = tags.Where(t => Newer(t.TrimStart('v', 'V'), current)).ToList();
        if (newer.Count == 0) return null;
        JsonObject blocked = null;
        foreach (var tag in newer)
        {
            var log = ChangelogAt(tag);
            var info = Parse(log);
            var version = Updates.ParseVersion(tag)!.ToString();
            if (!Fits(info.NeedsTcAgent, KnowledgeLayers.TcAgentVersion))
            {
                blocked ??= new JsonObject { ["version"] = version, ["needs"] = info.NeedsTcAgent };
                continue;
            }
            var notes = Updates.ChangelogSections(log).Where(kv => Newer(kv.Key, current) && !Newer(kv.Key, version))
                                .OrderByDescending(kv => Updates.ParseVersion(kv.Key))
                                .Select(kv => (JsonNode)new JsonObject { ["version"] = kv.Key, ["notes"] = kv.Value }).ToArray();
            return new JsonObject { ["version"] = version, ["tag"] = tag, ["needs"] = info.NeedsTcAgent, ["releases"] = new JsonArray(notes), ["blocked"] = blocked };
        }
        return new JsonObject { ["version"] = null, ["blocked"] = blocked };
    }

    public static JsonObject Describe(JsonObject st)
    {
        var b = st[StateKey] as JsonObject ?? new JsonObject();
        var (root, version, why) = Choose(st);
        var pkg = VersionOf(PackageRoot);
        var sw = Updates.Setting(SwitchKey, st);
        var pin = Pin();
        var offer = b["offer"]?.DeepClone() as JsonObject;
        // an offer that is no longer newer than what is used (applied meanwhile, a newer package) is no offer
        if (offer != null && !Newer(offer["version"]?.ToString(), version)) offer["version"] = null;
        var offered = offer?["version"]?.ToString();
        return new JsonObject
        {
            ["version"] = version, ["root"] = root, ["why"] = why, ["package"] = pkg.Version,
            ["active"] = b["active"]?.ToString(), ["previous"] = b["previous"]?.ToString(),
            ["available"] = offered != null, ["latest"] = offered,
            ["notify"] = offered != null && offered != b["skipped"]?.ToString() && !InPipeline,
            ["offer"] = offer,
            [SwitchKey] = sw.On, ["lockedBy"] = sw.LockedBy, ["pinned"] = pin.Version, ["pinnedBy"] = pin.By,
            ["pipeline"] = InPipeline, ["lastCheck"] = b["lastCheck"]?.ToString(), ["error"] = b["error"]?.ToString()
        };
    }

    /// <summary>Takes a version over: its files from the tag into the cache, then it is the built-in level. The version before
    /// stays for "back"; older ones are removed.</summary>
    public static JsonObject Apply(string version)
    {
        if (InPipeline) throw new InvalidOperationException("a pipeline keeps the built-in knowledge of the package");
        if (Pin() is { Version: not null } pin) throw new InvalidOperationException($"the built-in knowledge is fixed to {pin.Version} on the {pin.By} level (updates.json)");
        var st = Updates.LoadState();
        if (Updates.Setting(SwitchKey, st) is { LockedBy: not null, On: false } sw) throw new InvalidOperationException($"switched off on the {sw.LockedBy} level (updates.json)");
        if (Updates.ParseVersion(version) is null) throw new ArgumentException("update builtin apply <version>, e.g. 1.2.0");
        string tag;
        try { tag = Directory.Exists(Repo) ? TagOf(version) : null; } catch (ArgumentException) { tag = null; }
        if (tag == null) { FetchTags(); tag = TagOf(version); }
        var v = Updates.ParseVersion(tag)!.ToString();
        var target = Folder(v);
        if (VersionOf(target).Version != v)
        {
            var tmp = target + ".part";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var tar = Path.Combine(Base, v + ".tar");
            try
            {
                Git($"--git-dir=\"{Repo}\" archive --format=tar -o \"{tar}\" \"{tag}\"");
                System.Formats.Tar.TarFile.ExtractToDirectory(tar, tmp, true);
            }
            finally { try { File.Delete(tar); } catch (IOException) { } }
            var got = VersionOf(tmp);
            if (got.Version != v) { Directory.Delete(tmp, true); throw new InvalidOperationException($"the tag {tag} holds knowledge {got.Version ?? "without a version"} - not taken over"); }
            if (!Fits(got.NeedsTcAgent, KnowledgeLayers.TcAgentVersion)) { Directory.Delete(tmp, true); throw new InvalidOperationException($"knowledge {v} needs TcAgent {got.NeedsTcAgent} - update TcAgent first"); }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);
        }
        var b = st[StateKey] as JsonObject ?? new JsonObject();
        var before = Choose(st);
        if (before.Version != v) b["previous"] = before.Why.StartsWith("package") ? "package" : before.Version;
        b["active"] = v;
        st[StateKey] = b;
        Updates.SaveState(st);
        Reset();
        Tidy(b);
        return Describe(Updates.LoadState());
    }

    /// <summary>Back to the version before (or the package).</summary>
    public static JsonObject Back()
    {
        var st = Updates.LoadState();
        var b = st[StateKey] as JsonObject ?? new JsonObject();
        var prev = b["previous"]?.ToString();
        if (string.IsNullOrEmpty(prev)) prev = "package";
        if (prev != "package" && VersionOf(Folder(prev)).Version == null) prev = "package";
        b["previous"] = null;
        b["active"] = prev == "package" ? null : prev;
        st[StateKey] = b;
        Updates.SaveState(st);
        Reset();
        return Describe(Updates.LoadState());
    }

    public static void Skip(string version)
    {
        var st = Updates.LoadState();
        var b = st[StateKey] as JsonObject ?? new JsonObject();
        b["skipped"] = Updates.ParseVersion(version)?.ToString() ?? throw new ArgumentException("update builtin skip <version>");
        st[StateKey] = b;
        Updates.SaveState(st);
    }

    // only the version in use and the one before it stay in the cache
    static void Tidy(JsonObject b)
    {
        var keep = new[] { b["active"]?.ToString(), b["previous"]?.ToString() }.Where(x => x != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.GetDirectories(Base))
        {
            var n = Path.GetFileName(d);
            if (n == "repo" || keep.Contains(n) || Updates.ParseVersion(n) == null) continue;
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
