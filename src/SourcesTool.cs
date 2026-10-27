// tc_sources (Sources.cs): Git repositories as sources of skills, workflows, coding rules, risk rules and lessons -
// add/remove a source, search it, install an item (license check, files and scripts shown, the user is asked), status,
// pull updates, diff, and promote an own item to a higher level (company: commit on a new branch for a pull request).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterSources()
    {
        Add("tc_sources", "Git repositories as sources of skills, workflows, coding rules, risk rules and lessons (an external skill collection, " +
                          "a team, platform or company repository). action=list (sources and installed items), add name url [ref] [scope], remove name, " +
                          "search [query] [source], install item=<source>/<name> [kind] scope=personal|project|platform:<name> (license check, shows files " +
                          "and scripts, asks the user; MIT/Apache/BSD/ISC accepted, source-available and unlicensed refused, others only with acceptLicense), " +
                          "status (ok / changed locally / newer upstream), pull [item] (updates after asking; skips locally changed items unless force), " +
                          "diff item, changes item (what the user changed in an installed item), merge item (a newer version of the source into the locally changed " +
                          "item, three-way; dryRun first, then only after the user agreed - conflicts are marked in the file), propose item message (the local " +
                          "changes as a commit on a new branch of the source plus a patch - never pushed; the user pushes in the knowledge window), promote item=<kind>/<name> scope=personal|team|platform:<name>|company (own item one level up; checked for specific names; " +
                          "company and a team in Git as a commit on a new branch for a pull request). Installed items are pinned in skills.lock.json of their level.",
            Schema(Req(P("action", "string", "list, add, remove, search, install, status, pull, diff, changes, merge, propose or promote")),
                   P("name", "string", "add/remove: source name (lowercase, hyphens)"),
                   P("url", "string", "add: Git URL or path of the repository"),
                   P("ref", "string", "add: branch or tag (default HEAD)"),
                   P("item", "string", "install/pull/diff: <source>/<name> or <name>; promote: <kind>/<name>, e.g. skills/driver-fb"),
                   P("kind", "string", "install: skills, workflows, coding-rules, risk-rules or lessons (when a name exists in several kinds)"),
                   P("query", "string", "search: words in name or description"),
                   P("scope", "string", "add: personal (default) or project; install: personal (default), project, team or platform:<name>; promote: personal, team, platform:<name> or company"),
                   P("acceptLicense", "boolean", "install/pull: take over an item whose license is neither accepted nor refused (e.g. copyleft) - after the user checked it"),
                   P("force", "boolean", "install: overwrite an own file of the same name; pull: also update locally changed items"),
                   P("message", "string", "propose: what the change improves (the commit message)"),
                   P("dryRun", "boolean", "install/pull/promote/merge: only show what would be done")),
            a =>
            {
                var dir = SolutionDir();
                try
                {
                    return (Str(a, "action") ?? "list").ToLowerInvariant() switch
                    {
                        "list" => SourcesList(dir),
                        "add" => "Source saved in " + Sources.AddSource(Sources.RootOf(dir, Str(a, "scope") ?? "personal"), Str(a, "name"), Str(a, "url"), Str(a, "ref")) +
                                 " - tc_sources action=search shows what it offers.",
                        "remove" => Sources.RemoveSource(Sources.RootOf(dir, Str(a, "scope") ?? "personal"), Str(a, "name") ?? "") ? "Source removed (installed items stay)." : "Source not found.",
                        "search" => SourcesSearch(dir, Str(a, "query"), Str(a, "name") ?? SourceOfItem(Str(a, "item"))),
                        "install" => SourcesInstall(a, dir),
                        "status" => SourcesStatus(dir),
                        "pull" => SourcesPull(a, dir),
                        "diff" => SourcesDiff(dir, Str(a, "item") ?? throw new ArgumentException("item is required")),
                        "promote" => SourcesPromote(a, dir),
                        "changes" or "merge" or "propose" => SourcesLocal(a, dir),
                        _ => throw new ArgumentException("action: list, add, remove, search, install, status, pull, diff, changes, merge, propose or promote")
                    };
                }
                catch (ArgumentException ex) { throw new Exception("NOT done: " + ex.Message); }
                catch (InvalidOperationException ex) { throw new Exception("NOT done: " + ex.Message); }
            });
    }

    // local changes of an installed item (SourcesMerge.cs)
    static string SourcesLocal(JsonObject a, string dir)
    {
        var name = (Str(a, "item") ?? throw new ArgumentException("item is required")).Split('/').Last();
        var hits = InstallRoots(dir).SelectMany(r => Sources.LoadLock(r.Root).Where(x => x.Key.Split('/')[1].Equals(name, StringComparison.OrdinalIgnoreCase))
                                                                            .Select(x => (r.Label, r.Root, x.Key, x.Value)))
                                    .Where(h => Str(a, "scope") is not { } sc || h.Label.Equals(sc, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1) throw new ArgumentException(hits.Count == 0 ? $"'{name}' is not installed from a source" : $"'{name}' is installed on several levels - give scope");
        var (label, root, key, e) = hits[0];
        var src = Sources.All(dir).FirstOrDefault(x => x.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
        switch (Str(a, "action")!.ToLowerInvariant())
        {
            case "changes": return $"{key} ({label}) - the user's changes since the install @{e.Commit[..8]}:\n\n{SourcesMerge.LocalDiff(root, key, e, src)}";
            case "merge":
            {
                var up = Sources.Fetch(src);
                if (up == e.Commit) return $"{key}: the source has no newer version.";
                var from = e.Commit[..8];
                var r = SourcesMerge.Merge(root, key, e, src, up, Bool(a, "acceptLicense", false), Bool(a, "dryRun", false));
                return $"{key} ({label}): {(Bool(a, "dryRun", false) ? "merge preview (nothing changed) - ask the user before merging" : "merged")} {from} -> {up[..8]}. " +
                       $"Conflicts: {(r.Conflicts.Count == 0 ? "none" : string.Join(", ", r.Conflicts) + " - marked with <<<<<<< in the file; let the user decide")}. " +
                       $"Merged: {r.Merged.Count}, taken from the source: {r.Taken.Count}, deleted: {r.Deleted.Count}." + (r.Backup != null ? $" Backup: {r.Backup}" : "");
            }
            default:
            {
                var r = SourcesMerge.Propose(root, key, e, src, Str(a, "message"), Improver.PrivateTerms(dir));
                return $"Prepared (not pushed): branch {r.Branch} in {r.Folder}, patch {r.Patch}." +
                       (r.Specifics.Count > 0 ? $" WARNING - private terms in the proposal: {string.Join(", ", r.Specifics)} - remove them before anything is shared." : "") +
                       " The user pushes it in the knowledge window (Sources > Propose) or sends the patch.";
            }
        }
    }

    static string SourceOfItem(string item) => item != null && item.Contains('/') ? item.Split('/')[0] : null;

    /// <summary>The level roots that can hold installed items: project, user and the user's platform folders.</summary>
    static List<(string Label, string Root)> InstallRoots(string dir)
    {
        var list = new List<(string, string)>();
        if (KnowledgeLayers.ProjectRoot(dir) is { } p) list.Add(("project", p));
        list.Add(("user", KnowledgeLayers.UserRoot));
        foreach (var t in KnowledgeLayers.TeamRoots) list.Add(("team", t));
        var plat = Path.Combine(KnowledgeLayers.UserRoot, "platforms");
        if (Directory.Exists(plat)) foreach (var d in Directory.GetDirectories(plat)) list.Add(("user · " + Path.GetFileName(d), d));
        return list;
    }

    string SourcesList(string dir)
    {
        var all = Sources.All(dir);
        var sb = new StringBuilder(all.Count == 0 ? "No sources (tc_sources action=add name=<name> url=<git url>).\n" : "Sources:\n");
        foreach (var s in all) sb.AppendLine($"- {s.Name} ({s.Level}): {s.Url}{(s.Ref != "HEAD" ? " @" + s.Ref : "")}");
        return sb.AppendLine().Append(SourcesStatus(dir)).ToString();
    }

    string SourcesStatus(string dir)
    {
        var sb = new StringBuilder();
        foreach (var (label, root) in InstallRoots(dir))
            foreach (var (key, state, e) in Sources.Status(root))
                sb.AppendLine($"- {key} ({label}) from {e.Source} @{e.Commit[..Math.Min(8, e.Commit.Length)]}, {e.License}: {state}");
        return sb.Length == 0 ? "No items installed from sources." : "Installed from sources (newer upstream as of the last search/pull):\n" + sb;
    }

    Sources.Source SourceByName(string dir, string name) =>
        Sources.All(dir).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"source '{name}' not defined (tc_sources action=list)");

    string SourcesSearch(string dir, string query, string only)
    {
        var sb = new StringBuilder();
        foreach (var s in Sources.All(dir).Where(s => only == null || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)))
        {
            string repo, commit;
            try { commit = Sources.Fetch(s); repo = Sources.Checkout(s.Name, s.Url, commit); }
            catch (Exception ex) { sb.AppendLine($"{s.Name}: not reachable - {ex.Message}"); continue; }
            var items = Sources.Scan(repo).Where(i => string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                                     .All(w => (i.Name + " " + i.Description).Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            sb.AppendLine($"{s.Name} @{commit[..8]}: {items.Count} item(s)");
            foreach (var i in items.Take(60))
                sb.AppendLine($"  {s.Name}/{i.Name}  [{i.Kind}, {i.License}{(i.LicenseClass == "accepted" ? "" : " - " + i.LicenseClass)}]  {Trunc(i.Description, 140)}");
        }
        return sb.Length == 0 ? "No sources (tc_sources action=add)." : sb.ToString();
    }

    static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + " …";

    static Sources.Item FindItem(List<Sources.Item> items, string name, string kind)
    {
        var m = items.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (kind == null || i.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))).ToList();
        if (m.Count == 0) throw new ArgumentException($"'{name}' not found in the source (tc_sources action=search)");
        if (m.Count > 1) throw new ArgumentException($"'{name}' exists as {string.Join(" and ", m.Select(x => x.Kind))} - give kind");
        return m[0];
    }

    string SourcesInstall(JsonObject a, string dir)
    {
        var spec = Str(a, "item") ?? throw new ArgumentException("item is required: <source>/<name>");
        var sources = Sources.All(dir);
        var s = spec.Contains('/') ? SourceByName(dir, spec.Split('/')[0]) : sources.Count == 1 ? sources[0] : throw new ArgumentException("item: <source>/<name>");
        var name = spec.Split('/').Last();
        var commit = Sources.Fetch(s);
        var repo = Sources.Checkout(s.Name, s.Url, commit);
        var it = FindItem(Sources.Scan(repo), name, Str(a, "kind"));
        if (Sources.LicenseRefusal(it, Bool(a, "acceptLicense", false)) is { } lic) throw new InvalidOperationException(lic);
        var root = Sources.RootOf(dir, Str(a, "scope") ?? "personal");
        var target = Sources.TargetOf(root, it);
        bool exists = Directory.Exists(target) || File.Exists(target);
        if (exists && !Sources.LoadLock(root).ContainsKey(it.Key) && !Bool(a, "force", false))
            throw new InvalidOperationException($"{target} exists and was not installed from a source - an own file; force=true overwrites it");
        var files = Sources.FilesOf(repo, it);
        var scripts = Sources.Scripts(files);
        var what = $"Install {it.Kind[..^1]} '{it.Name}' from {s.Name} @{commit[..8]} ({s.Url}), license {it.License}, into {target}\n" +
                   $"Files: {string.Join(", ", files.Take(30))}{(files.Count > 30 ? $" … ({files.Count})" : "")}" +
                   (scripts.Count > 0 ? $"\nContains scripts ({string.Join(", ", scripts)}) - TcAgent never runs them; read them before using the skill." : "");
        if (DryRun(a)) return "PROPOSAL (nothing installed):\n" + what;
        AskImprove(what);
        var e = Sources.Install(root, repo, it, s, commit);
        return $"Installed: {target}\nPinned in {Path.Combine(root, Sources.LockFile)} (commit {e.Commit[..8]}).";
    }

    string SourcesPull(JsonObject a, string dir)
    {
        var only = Str(a, "item")?.Split('/').Last();
        var plan = new List<(string Root, string Key, Sources.LockEntry E, Sources.Item It, string Repo, string Commit, string Note)>();
        var skipped = new List<string>();
        var fetched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, root) in InstallRoots(dir))
            foreach (var (key, e) in Sources.LoadLock(root))
            {
                if (only != null && !key.Split('/')[1].Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                var src = Sources.All(dir).FirstOrDefault(x => x.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
                if (!fetched.TryGetValue(src.Name, out var up))
                    try { fetched[src.Name] = up = Sources.Fetch(src); }
                    catch (Exception ex) { skipped.Add($"{key}: source {src.Name} not reachable - {ex.Message}"); continue; }
                if (up == e.Commit || Sources.Git(Sources.CacheOf(src.Name), $"log --format=%h {e.Commit}..{up} -- \"{e.Path}\"").Length == 0) continue;
                var installed = Sources.InstalledPath(root, key, e);
                if ((File.Exists(installed) || Directory.Exists(installed)) && Sources.Checksum(installed) != e.Sha256 && !Bool(a, "force", false))
                { skipped.Add($"{key} ({label}): changed locally - not updated (diff first; force=true overwrites)"); continue; }
                var repo = Sources.Checkout(src.Name, src.Url, up);
                var it = Sources.Scan(repo).FirstOrDefault(i => i.RelPath == e.Path);
                if (it == null) { skipped.Add($"{key}: no longer in the source ({e.Path})"); continue; }
                if (Sources.LicenseRefusal(it, Bool(a, "acceptLicense", false)) is { } lic) { skipped.Add(lic + " (license changed?)"); continue; }
                var stat = Sources.Git(Sources.CacheOf(src.Name), $"diff --stat {e.Commit} {up} -- \"{e.Path}\"").Split('\n').LastOrDefault()?.Trim();
                plan.Add((root, key, e, it, repo, up, $"{key} ({label}): {e.Commit[..8]} → {up[..8]}, {stat}, license {it.License}"));
            }
        var sb = new StringBuilder();
        if (plan.Count == 0) sb.AppendLine("Everything is up to date.");
        else
        {
            var what = "Update from the sources:\n" + string.Join("\n", plan.Select(p => p.Note)) + "\n(tc_sources action=diff item=<name> shows the changes)";
            if (DryRun(a)) return "PROPOSAL (nothing updated):\n" + what + (skipped.Count > 0 ? "\n\nSkipped:\n" + string.Join("\n", skipped) : "");
            AskImprove(what);
            foreach (var p in plan)
            {
                var src = Sources.All(dir).FirstOrDefault(x => x.Name == p.E.Source) ?? new Sources.Source { Name = p.E.Source, Url = p.E.Url };
                Sources.Checkout(src.Name, src.Url, p.Commit);
                Sources.Install(p.Root, p.Repo, p.It, src, p.Commit);
                sb.AppendLine("Updated: " + p.Note);
            }
        }
        if (skipped.Count > 0) sb.AppendLine().AppendLine("Skipped:").AppendLine(string.Join("\n", skipped));
        return sb.ToString();
    }

    string SourcesDiff(string dir, string item)
    {
        var name = item.Split('/').Last();
        foreach (var (label, root) in InstallRoots(dir))
            foreach (var (key, e) in Sources.LoadLock(root).Where(x => x.Key.Split('/')[1].Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var src = Sources.All(dir).FirstOrDefault(x => x.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
                var up = Sources.Fetch(src);
                var installed = Sources.InstalledPath(root, key, e);
                var local = Sources.Checksum(installed) == e.Sha256 ? "not changed locally" : "CHANGED LOCALLY since the install";
                var patch = up == e.Commit ? "" : Sources.Git(Sources.CacheOf(src.Name), $"diff {e.Commit} {up} -- \"{e.Path}\"");
                var lines = patch.Split('\n');
                return $"{key} ({label}) from {src.Name}: installed @{e.Commit[..8]}, upstream @{up[..8]}; {local}\n\n" +
                       (patch.Length == 0 ? "No changes upstream." : string.Join("\n", lines.Take(200)) + (lines.Length > 200 ? $"\n… {lines.Length - 200} more lines" : ""));
            }
        throw new ArgumentException($"'{item}' is not installed from a source");
    }

    string SourcesPromote(JsonObject a, string dir)
    {
        var spec = Str(a, "item") ?? throw new ArgumentException("item is required: <kind>/<name>, e.g. skills/driver-fb");
        var parts = spec.Split('/');
        if (parts.Length != 2 || !(Sources.FolderKinds.ContainsKey(parts[0]) || Sources.FileKinds.Contains(parts[0])))
            throw new ArgumentException("item: <kind>/<name> with kind skills, workflows, coding-rules, risk-rules or lessons");
        var (kind, name) = (parts[0], parts[1]);
        // the own item: project first, then the user level
        string source = null;
        foreach (var root in new[] { KnowledgeLayers.ProjectRoot(dir), KnowledgeLayers.UserRoot }.Where(r => r != null))
        {
            var p = Sources.FolderKinds.ContainsKey(kind) ? Path.Combine(root, kind, name) : Directory.Exists(Path.Combine(root, kind)) ? Directory.GetFiles(Path.Combine(root, kind), "*.md").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase) || Improver.LessonSlug(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
            if (p != null && (Directory.Exists(p) || File.Exists(p))) { source = p; break; }
        }
        if (source == null) throw new ArgumentException($"{spec} not found on the project or user level");
        var texts = Directory.Exists(source) ? Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => Path.GetExtension(f) is ".md" or ".txt" or ".json").Select(File.ReadAllText) : new[] { File.ReadAllText(source) };
        var found = Improver.Specifics(string.Join("\n", texts), Improver.PrivateTerms(dir));
        if (found.Count > 0) throw new InvalidOperationException($"{spec} names {string.Join(", ", found)} - make it general first, then promote again");
        var scope = (Str(a, "scope") ?? "personal").Trim();
        // company, and a team kept in Git: as a commit on a new branch for a pull request; a plain team folder: copied
        bool team = scope.Equals("team", StringComparison.OrdinalIgnoreCase);
        bool company = scope.Equals("company", StringComparison.OrdinalIgnoreCase) || team && KnowledgeLayers.IsGitRepo(KnowledgeLayers.TeamRoot);
        var targetRoot = scope.Equals("company", StringComparison.OrdinalIgnoreCase) ? KnowledgeLayers.CompanyRoot ?? throw new ArgumentException("no company level (TCAGENT_COMPANY not set)")
                       : team ? KnowledgeLayers.TeamRoot ?? throw new ArgumentException("no team level (TCAGENT_TEAM not set)") : Sources.RootOf(dir, scope);
        var target = Path.Combine(targetRoot, kind, Path.GetFileName(source));
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{spec} is already on that level");
        var what = $"Promote {spec}: copy {source} to {target}" + (company ? " (shared repository: commit on a new branch for a pull request, not pushed)" : "");
        if (DryRun(a)) return "PROPOSAL (nothing changed): " + what;
        AskImprove(what);
        if (company) return Sources.CommitForPullRequest(targetRoot, source, target, $"Promote {spec}");
        if (Directory.Exists(target) || File.Exists(target)) throw new InvalidOperationException($"{target} exists already");
        if (Directory.Exists(source))
            foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(source, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
        return $"Promoted: {target}. The original stays in {source} - remove it when the user agrees (a later level replaces the same name anyway).";
    }
}
