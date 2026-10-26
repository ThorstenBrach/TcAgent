// TcAgent command line: sources (part of Cli, see Cli.cs; the logic is in Sources.cs). Files and git only, no IDE.
//
//   TcAgent.exe sources status [--solution <sln>]             what was installed from sources: ok, changed locally,
//                                                             missing, newer upstream (as of the last fetch)
//   TcAgent.exe sources sync   [--solution <sln>] [--dry-run] restore every item of skills.lock.json exactly at its
//                                                             commit - the pipeline gets the same skills as the team;
//                                                             --dry-run only checks (exit 1 if something differs)
//
// sync never updates to a newer commit (that is tc_sources action=pull, with the user's agreement) and never runs scripts.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static partial class Cli
{
    static int SourcesCli(Options o)
    {
        var dir = o.Solution != null ? Path.GetDirectoryName(Path.GetFullPath(o.Solution)) : null;
        if (o.Sub is "add" or "remove" or "search" or "install" or "pull" or "diff")
            try { return SourcesEdit(o, dir); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException) { Console.Error.WriteLine(ex.Message); return Failed; }
        var roots = new List<(string Label, string Root)>();
        if (KnowledgeLayers.ProjectRoot(dir) is { } p) roots.Add(("project", p));
        roots.Add(("user", KnowledgeLayers.UserRoot));
        roots.AddRange(KnowledgeLayers.TeamRoots.Select(t => ("team", t)));
        int differs = 0;
        foreach (var (label, root) in roots)
            foreach (var (key, state, e) in Sources.Status(root))
            {
                bool bad = state.StartsWith("missing") || state.StartsWith("changed locally");
                if (o.Sub == "status" || !bad) { Console.WriteLine($"  {key} ({label}) {e.Source} @{e.Commit[..Math.Min(8, e.Commit.Length)]}: {state}"); continue; }
                if (o.DryRun) { Console.WriteLine($"  {key} ({label}): {state} - differs from {Sources.LockFile}"); differs++; continue; }
                try
                {
                    var src = Sources.All(dir).FirstOrDefault(s => s.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
                    var repo = Sources.Checkout(src.Name, e.Url, e.Commit);
                    var it = Sources.Scan(repo).FirstOrDefault(i => i.RelPath == e.Path) ?? throw new InvalidOperationException($"{e.Path} not in {e.Source} @{e.Commit[..8]}");
                    var installed = Sources.Install(root, repo, it, src, e.Commit);
                    if (installed.Sha256 != e.Sha256) Console.WriteLine($"  {key} ({label}): restored @{e.Commit[..8]} (the checksum of the lock was written by another TcAgent version)");
                    else Console.WriteLine($"  {key} ({label}): restored @{e.Commit[..8]}");
                }
                catch (Exception ex) { Console.Error.WriteLine($"  {key} ({label}): NOT restored - {ex.Message}"); differs++; }
            }
        if (roots.All(r => Sources.LoadLock(r.Root).Count == 0)) Console.WriteLine("No items installed from sources.");
        return differs == 0 ? Ok : Failed;
    }

    // add / remove / search / install / pull / diff - for the knowledge manager window, which shows license, files and
    // scripts and asks the user before it calls install or pull (the same checks as tc_sources)
    static int SourcesEdit(Options o, string dir)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Sources.Source Find(string name) => Sources.All(dir).FirstOrDefault(s => s.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase))
                                            ?? throw new ArgumentException($"source '{name}' not defined");
        var roots = new List<(string Label, string Root)>();
        if (KnowledgeLayers.ProjectRoot(dir) is { } pr) roots.Add(("project", pr));
        roots.Add(("user", KnowledgeLayers.UserRoot));
        roots.AddRange(KnowledgeLayers.TeamRoots.Select(t => ("team", t)));
        switch (o.Sub)
        {
            case "add":
                Console.WriteLine("Source saved in " + Sources.AddSource(Sources.RootOf(dir, o.Level ?? "personal"), o.Name, o.Url, o.Ref));
                return Ok;
            case "remove":
                Console.WriteLine(Sources.RemoveSource(Sources.RootOf(dir, o.Level ?? "personal"), o.Name ?? "") ? "Source removed (installed items stay)." : "Source not found.");
                return Ok;
            case "search":
            {
                var items = new System.Text.Json.Nodes.JsonArray();
                foreach (var s in Sources.All(dir).Where(s => o.Name == null || s.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var commit = Sources.Fetch(s);
                    var repo = Sources.Checkout(s.Name, s.Url, commit);
                    foreach (var i in Sources.Scan(repo).Where(i => string.IsNullOrWhiteSpace(o.Query) || (i.Name + " " + i.Description).Contains(o.Query, StringComparison.OrdinalIgnoreCase)))
                    {
                        var files = Sources.FilesOf(repo, i);
                        items.Add(new System.Text.Json.Nodes.JsonObject
                        {
                            ["source"] = s.Name, ["commit"] = commit, ["name"] = i.Name, ["kind"] = i.Kind, ["path"] = i.RelPath, ["description"] = i.Description,
                            ["license"] = i.License, ["licenseClass"] = i.LicenseClass, ["refusal"] = Sources.LicenseRefusal(i, false),
                            ["files"] = new System.Text.Json.Nodes.JsonArray(files.Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray()),
                            ["scripts"] = new System.Text.Json.Nodes.JsonArray(Sources.Scripts(files).Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray())
                        });
                    }
                }
                Console.WriteLine(items.ToJsonString());
                return Ok;
            }
            case "install":
            {
                var s = Find(o.Name);
                var commit = Sources.Fetch(s);
                var repo = Sources.Checkout(s.Name, s.Url, commit);
                var matches = Sources.Scan(repo).Where(i => i.Name.Equals(o.Item ?? "", StringComparison.OrdinalIgnoreCase) && (o.Kind == null || i.Kind == o.Kind)).ToList();
                if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? $"'{o.Item}' not found in {s.Name}" : $"'{o.Item}' exists in several kinds - give --kind");
                var it = matches[0];
                if (Sources.LicenseRefusal(it, o.AcceptLicense) is { } lic) throw new InvalidOperationException(lic);
                var root = Sources.RootOf(dir, o.Level ?? "personal");
                var target = Sources.TargetOf(root, it);
                if ((Directory.Exists(target) || File.Exists(target)) && !Sources.LoadLock(root).ContainsKey(it.Key))
                    throw new InvalidOperationException($"{target} exists and was not installed from a source - an own file");
                var e = Sources.Install(root, repo, it, s, commit);
                Console.WriteLine($"Installed: {target} (commit {e.Commit[..8]}, license {e.License})");
                return Ok;
            }
            case "pull":
            {
                int n = 0;
                foreach (var (label, root) in roots)
                    foreach (var (key, e) in Sources.LoadLock(root))
                    {
                        if (o.Item != null && !key.Split('/')[1].Equals(o.Item, StringComparison.OrdinalIgnoreCase)) continue;
                        var src = Sources.All(dir).FirstOrDefault(x => x.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
                        var up = Sources.Fetch(src);
                        if (up == e.Commit || Sources.Git(Sources.CacheOf(src.Name), $"log --format=%h {e.Commit}..{up} -- \"{e.Path}\"").Length == 0) continue;
                        var installed = Sources.InstalledPath(root, key, e);
                        if ((File.Exists(installed) || Directory.Exists(installed)) && Sources.Checksum(installed) != e.Sha256) { Console.WriteLine($"  {key} ({label}): changed locally - not updated"); continue; }
                        var repo = Sources.Checkout(src.Name, src.Url, up);
                        var it = Sources.Scan(repo).FirstOrDefault(i => i.RelPath == e.Path);
                        if (it == null || Sources.LicenseRefusal(it, o.AcceptLicense) is { }) { Console.WriteLine($"  {key} ({label}): not updated (removed from the source or license changed)"); continue; }
                        if (o.DryRun) { Console.WriteLine($"  {key} ({label}): {e.Commit[..8]} -> {up[..8]}"); n++; continue; }
                        Sources.Install(root, repo, it, src, up);
                        Console.WriteLine($"  {key} ({label}): updated {e.Commit[..8]} -> {up[..8]}");
                        n++;
                    }
                if (n == 0) Console.WriteLine("Everything is up to date.");
                return Ok;
            }
            default:   // diff
            {
                foreach (var (label, root) in roots)
                    foreach (var (key, e) in Sources.LoadLock(root).Where(x => x.Key.Split('/')[1].Equals(o.Item ?? "", StringComparison.OrdinalIgnoreCase)))
                    {
                        var src = Sources.All(dir).FirstOrDefault(x => x.Name == e.Source) ?? new Sources.Source { Name = e.Source, Url = e.Url };
                        var up = Sources.Fetch(src);
                        var local = Sources.Checksum(Sources.InstalledPath(root, key, e)) == e.Sha256 ? "not changed locally" : "CHANGED LOCALLY since the install";
                        var patch = up == e.Commit ? "No changes in the source." : Sources.Git(Sources.CacheOf(src.Name), $"diff {e.Commit} {up} -- \"{e.Path}\"");
                        Console.WriteLine($"{key} ({label}) from {src.Name}: installed @{e.Commit[..8]}, source @{up[..8]}; {local}\n\n{patch}");
                        return Ok;
                    }
                throw new ArgumentException($"'{o.Item}' is not installed from a source");
            }
        }
    }
}
