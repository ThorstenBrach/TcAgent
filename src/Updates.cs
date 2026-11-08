// New versions of TcAgent and of the repo sources (TcAgent.exe update, the notice in the TcAgent Chat).
//
// Once a day at most the extension asks through TcAgent.exe - never the agent, never a pipeline. The request is an anonymous
// GET of the release list of the TcAgent repository; nothing about the solution or the user goes with it. A download happens only
// on a click, its checksum must match SHA256SUMS.txt of the release, and installing stays a step of the user.
// The checksum shows a damaged download; it lies in the same release, so it does not protect against a taken-over repository.
//
// %LOCALAPPDATA%\TcAgent\update-state.json   the switches of the user, the last answer, a skipped version
// <level>\updates.json (company, team)       { "check": false, "sources": false, "knowledgeSource": false } - a value here locks the switch
//
// What is new comes from CHANGELOG.md of the newest release (its tag): every version between the installed and the newest one,
// not only the text of the release. Without it (not reachable, no section) the text of the release stays.
//
// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Updates
{
    public const string Repo = "ThorstenBrach/TcAgent", KnowledgeSource = "knowledgeSource";
    const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
    const string DownloadRoot = "https://github.com/" + Repo + "/releases/download/";
    const string RawRoot = "https://raw.githubusercontent.com/" + Repo + "/";
    static readonly TimeSpan Interval = TimeSpan.FromHours(20);     // once a day, also when the IDE starts a little earlier

    static string StateFile => Path.Combine(KnowledgeLayers.UserRoot, "update-state.json");
    public static string DownloadDir => Path.Combine(KnowledgeLayers.UserRoot, "updates");

    public sealed record Release(string Version, string Tag, string Published, string Notes, string Url);
    public sealed record Switch(bool On, string LockedBy);

    // ------------------------------------------------------------------ versions
    /// <summary>"v0.51.0" -> 0.51.0; null if it is no plain version (a tag like v1.0-rc1 is no release to offer).</summary>
    public static Version ParseVersion(string text)
    {
        var t = (text ?? "").Trim().TrimStart('v', 'V');
        return Regex.IsMatch(t, @"^\d+(\.\d+){1,3}$") && Version.TryParse(t, out var v) ? v : null;
    }

    /// <summary>The published releases newer than current, newest first. Drafts, pre-releases and odd tags do not count.</summary>
    public static List<Release> Newer(JsonNode releases, string current)
    {
        var cur = ParseVersion(current) ?? new Version(0, 0);
        var list = new List<(Version V, Release R)>();
        foreach (var r in releases as JsonArray ?? new JsonArray())
        {
            if (r?["draft"]?.GetValue<bool>() == true || r?["prerelease"]?.GetValue<bool>() == true) continue;
            var tag = r?["tag_name"]?.ToString();
            if (ParseVersion(tag) is not { } v || v <= cur) continue;
            list.Add((v, new Release(v.ToString(), tag, r?["published_at"]?.ToString(), r?["body"]?.ToString() ?? "", r?["html_url"]?.ToString())));
        }
        return list.OrderByDescending(x => x.V).Select(x => x.R).ToList();
    }

    /// <summary>The sections of CHANGELOG.md per version ("## [0.52.1] – 2026-11-08" up to the next "## ["), without the heading.</summary>
    public static Dictionary<string, string> ChangelogSections(string changelog)
    {
        var d = new Dictionary<string, string>();
        var ms = Regex.Matches(changelog ?? "", @"^##\s*\[(?<v>[^\]]+)\][^\n]*$", RegexOptions.Multiline);
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index + ms[i].Length, end = i + 1 < ms.Count ? ms[i + 1].Index : (changelog ?? "").Length;
            if (ParseVersion(ms[i].Groups["v"].Value) is { } v) d[v.ToString()] = changelog[start..end].Trim();
        }
        return d;
    }

    /// <summary>The releases with the text of their CHANGELOG.md section where there is one.</summary>
    public static List<Release> WithChangelog(List<Release> releases, string changelog, string current = null)
    {
        var s = ChangelogSections(changelog);
        var list = releases.Select(r => s.TryGetValue(r.Version, out var text) && text.Length > 0 ? r with { Notes = text } : r).ToList();
        // versions in between that were never released on their own (combined into a later one) are new as well
        if (list.Count > 0 && ParseVersion(list[0].Version) is { } newest)
        {
            var oldest = ParseVersion(current) ?? list.Select(r => ParseVersion(r.Version)).Min();
            foreach (var (v, text) in s)
                if (ParseVersion(v) is { } pv && pv < newest && pv > oldest && list.All(r => r.Version != v)) list.Add(new Release(v, null, null, text, null));
        }
        return list.OrderByDescending(r => ParseVersion(r.Version)).ToList();
    }

    // ------------------------------------------------------------------ switches
    /// <summary>The switch "check", "sources" or "knowledgeSource": on unless the user switched it off; a value in updates.json of the company
    /// or a team level wins and locks it.</summary>
    public static Switch Setting(string key, JsonObject state, IEnumerable<(string Label, string Root)> levels = null)
    {
        foreach (var (label, root) in levels ?? Levels())
        {
            var f = root == null ? null : Path.Combine(root, "updates.json");
            if (f == null || !File.Exists(f)) continue;
            try { if (JsonNode.Parse(File.ReadAllText(f))?[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return new Switch(b, label); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return new Switch(!(state[key] is JsonValue u && u.TryGetValue<bool>(out var on) && !on), null);
    }

    static IEnumerable<(string, string)> Levels()
    {
        yield return ("company", KnowledgeLayers.CompanyRoot);
        foreach (var t in KnowledgeLayers.TeamRoots) yield return ("team", t);
    }

    public static JsonObject LoadState()
    {
        try { if (File.Exists(StateFile) && JsonNode.Parse(File.ReadAllText(StateFile)) is JsonObject o) return o; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        return new JsonObject();
    }

    public static void SaveState(JsonObject state)
    {
        Directory.CreateDirectory(KnowledgeLayers.UserRoot);
        File.WriteAllText(StateFile, state.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool Due(string lastCheck, DateTime now) =>
        !DateTime.TryParse(lastCheck, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) || now.ToUniversalTime() - t.ToUniversalTime() >= Interval;

    // ------------------------------------------------------------------ check
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TcAgent/" + Program.ServerVersion);
        return c;
    }

    /// <summary>What is known about new versions; asks GitHub when a day has passed or when forced by the user. A failed request
    /// is reported in "error" and tried again the next day - no notice, nothing the user has to deal with.</summary>
    public static JsonObject Check(string current, bool force)
    {
        var st = LoadState();
        var sw = Setting("check", st);
        string error = null;
        if (sw.LockedBy != null && !sw.On) error = $"switched off on the {sw.LockedBy} level";
        else if (force || (sw.On && Due(st["lastCheck"]?.ToString(), DateTime.UtcNow)))
        {
            st["lastCheck"] = DateTime.UtcNow.ToString("o");
            try
            {
                st["releases"] = JsonNode.Parse(Http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult());
                st.Remove("error");
                // what is new in every newer version: the changelog of the newest release
                if (Newer(st["releases"], current).FirstOrDefault() is { } newest && Regex.IsMatch(newest.Tag, @"^v?\d+(\.\d+){1,3}$"))
                    try { st["changelog"] = Http.GetStringAsync(RawRoot + newest.Tag + "/CHANGELOG.md").GetAwaiter().GetResult(); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { st.Remove("changelog"); }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                st["error"] = ex.Message;
            }
            SaveState(st);
            error = st["error"]?.ToString();
        }
        return Describe(st, sw, current, error);
    }

    public static JsonObject Describe(JsonObject st, Switch sw, string current, string error)
    {
        var newer = WithChangelog(Newer(st["releases"], current), st["changelog"]?.ToString(), current);
        var skipped = st["skipped"]?.ToString();
        var src = Setting("sources", st);
        var known = Setting(KnowledgeSource, st);
        return new JsonObject
        {
            ["current"] = current, ["latest"] = newer.FirstOrDefault()?.Version, ["available"] = newer.Count > 0,
            ["notify"] = newer.Count > 0 && newer[0].Version != skipped,
            ["check"] = sw.On, ["checkLockedBy"] = sw.LockedBy, ["sources"] = src.On, ["sourcesLockedBy"] = src.LockedBy,
            [KnowledgeSource] = known.On, ["knowledgeSourceLockedBy"] = known.LockedBy,
            ["lastCheck"] = st["lastCheck"]?.ToString(), ["error"] = error,
            ["releases"] = new JsonArray(newer.Select(r => (JsonNode)new JsonObject
            {
                ["version"] = r.Version, ["published"] = r.Published, ["notes"] = r.Notes, ["url"] = r.Url
            }).ToArray())
        };
    }

    // ------------------------------------------------------------------ download
    /// <summary>The checksum of a file in SHA256SUMS.txt ("hash  name" or "hash *name"), lower case; null if not listed.</summary>
    public static string ExpectedHash(string sums, string file)
    {
        foreach (var line in (sums ?? "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim().Equals(file, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Downloads the .vsix of the newest release into the update folder and checks it. The address is built from the
    /// repository and the tag, not taken from the answer. Returns the file; nothing stays behind when the checksum differs.</summary>
    public static string Download(string current)
    {
        var r = Newer(LoadState()["releases"], current).FirstOrDefault() ?? throw new Exception("No newer version is known - check for updates first.");
        if (!Regex.IsMatch(r.Tag, @"^v?\d+(\.\d+){1,3}$")) throw new Exception("Unexpected release tag: " + r.Tag);
        var name = $"TcAgent-{r.Tag.TrimStart('v', 'V')}.vsix";
        var baseUrl = DownloadRoot + r.Tag + "/";
        var want = ExpectedHash(Http.GetStringAsync(baseUrl + "SHA256SUMS.txt").GetAwaiter().GetResult(), name)
                   ?? throw new Exception($"{name} is not listed in SHA256SUMS.txt of {r.Tag} - not downloaded.");
        Directory.CreateDirectory(DownloadDir);
        var target = Path.Combine(DownloadDir, name);
        if (File.Exists(target) && Sha256(target) == want) return target;
        var part = target + ".part";
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
        using (var res = Http.GetAsync(baseUrl + name, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
        {
            res.EnsureSuccessStatusCode();
            using var from = res.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult();
            using var to = File.Create(part);
            from.CopyToAsync(to, cts.Token).GetAwaiter().GetResult();
        }
        if (Sha256(part) != want)
        {
            File.Delete(part);
            throw new Exception($"The checksum of {name} does not match SHA256SUMS.txt - the file was removed, nothing is installed.");
        }
        File.Move(part, target, true);
        foreach (var old in Directory.GetFiles(DownloadDir, "TcAgent-*.vsix"))
            if (!old.Equals(target, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (IOException) { }
        return target;
    }

    // ------------------------------------------------------------------ repo sources
    /// <summary>Installed items with a newer version in their source. Fetches the sources in use once a day (never pulls, no
    /// sign-in dialog); a source that cannot be reached is named in "errors" and shown in the Sources tab only.</summary>
    public static JsonObject CheckSources(string solutionDir, bool force)
    {
        var st = LoadState();
        var sw = Setting("sources", st);
        var roots = Sources.InstallRoots(solutionDir);
        var errors = new JsonArray();
        var key = "sourcesCheck:" + (solutionDir ?? "").ToLowerInvariant();
        if ((sw.LockedBy == null || sw.On) && (force || (sw.On && Due(st[key]?.ToString(), DateTime.UtcNow))))
        {
            st[key] = DateTime.UtcNow.ToString("o");
            SaveState(st);
            var used = roots.SelectMany(r => Sources.LoadLock(r.Root).Values.Select(e => e.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Sources.NoSignInDialog = true;
            foreach (var s in Sources.All(solutionDir).Where(s => used.Contains(s.Name)))
                try { Sources.Fetch(s); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception) { errors.Add(s.Name + ": " + ex.Message); }
        }
        var items = new JsonArray();
        foreach (var (label, root) in roots)
            foreach (var (k, state, e) in Sources.Status(root))
                if (state.Contains("newer upstream")) items.Add(new JsonObject { ["item"] = k, ["level"] = label, ["source"] = e.Source });
        return new JsonObject { ["enabled"] = sw.On, ["items"] = items, ["errors"] = errors };
    }
}
