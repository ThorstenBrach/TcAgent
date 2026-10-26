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
        var roots = new List<(string Label, string Root)>();
        if (KnowledgeLayers.ProjectRoot(dir) is { } p) roots.Add(("project", p));
        roots.Add(("user", KnowledgeLayers.UserRoot));
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
}
