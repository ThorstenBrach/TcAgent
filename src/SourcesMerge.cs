// Local changes of items installed from sources (Sources.cs): see them, merge a newer version of the source into them,
// and give them back to the source as a pull request.
//
//   local diff  the installed item against the item as it was installed (a fresh install of the same commit)
//   merge       three-way per file (git merge-file): base = the installed commit, ours = the local item, theirs = the
//               newer commit of the source. Conflicts stay as conflict markers in the file; the local item is copied to
//               %LOCALAPPDATA%\TcAgent\backups\sources\ first. The lock then points to the newer commit - the item still
//               counts as "changed locally" as long as it differs from the source.
//   propose     the local changes on top of the installed commit as a commit on a new branch in a working copy of the
//               source (%LOCALAPPDATA%\TcAgent\contrib\<source>) plus a patch file. TcAgent's own metadata
//               (tcagent-source/-commit/-license, the copied license notice) is left out. Pushed only when the user
//               clicks Push - to the source or to a fork of it.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class SourcesMerge
{
    public sealed class Result
    {
        public readonly List<string> Merged = new(), Conflicts = new(), Taken = new(), Deleted = new(), Kept = new();
        public string Backup, Branch, Folder, Patch, CompareUrl;
        public List<string> Specifics = new();
        /// <summary>Files with conflicts and their three versions - for a merge tool (Visual Studio: vsDiffMerge /m).</summary>
        public readonly List<(string Rel, string Base, string Theirs, string Yours, string Result)> ConflictFiles = new();
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>The files of an item: a folder (relative paths) or a single file (its name).</summary>
    static Dictionary<string, string> FilesIn(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(path))
            foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) d[Path.GetRelativePath(path, f).Replace('\\', '/')] = f;
        else if (File.Exists(path)) d[Path.GetFileName(path)] = path;
        return d;
    }

    static string Text(string f) => f != null && File.Exists(f) ? File.ReadAllText(f).Replace("\r\n", "\n") : null;

    static (int Code, string Out) Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                                                StandardOutputEncoding = new UTF8Encoding(false) };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (dir != null) psi.WorkingDirectory = dir;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git not found - install Git for Windows");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("git " + args.FirstOrDefault() + ": no answer"); }
        // merge-file -p writes the merged text to stdout - warnings on stderr must not end up in the file
        return (p.ExitCode, args[0] == "merge-file" && p.ExitCode is >= 0 and <= 127 ? o : o + err.Result);
    }

    /// <summary>The item as TcAgent installs it at a commit (with its metadata), in a temporary folder.</summary>
    static string Pristine(string key, Sources.LockEntry e, Sources.Source src, string commit, bool acceptLicense = true)
    {
        var repo = Sources.Checkout(src.Name, e.Url, commit);
        var it = Sources.Scan(repo).FirstOrDefault(i => i.RelPath == e.Path) ?? throw new InvalidOperationException($"{e.Path} is not in {src.Name} @{commit[..8]} any more");
        if (Sources.LicenseRefusal(it, acceptLicense) is { } lic) throw new InvalidOperationException(lic);
        var tmp = Path.Combine(Path.GetTempPath(), "tcagent-merge", Guid.NewGuid().ToString("N")[..8]);
        Sources.Install(tmp, repo, it, src, commit);
        return Sources.InstalledPath(tmp, key, e);
    }

    /// <summary>Three-way merge of an item per file into output: what other changed against base is applied to current.</summary>
    static void ThreeWay(string current, string @base, string other, string output, Result r)
    {
        var cur = FilesIn(current); var bas = FilesIn(@base); var oth = FilesIn(other);
        bool single = File.Exists(output) || !Directory.Exists(output) && !Directory.Exists(current) && File.Exists(current);
        string Out(string rel) => single ? output : Path.Combine(output, rel.Replace('/', '\\'));
        foreach (var rel in cur.Keys.Concat(bas.Keys).Concat(oth.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
        {
            cur.TryGetValue(rel, out var c); bas.TryGetValue(rel, out var b); oth.TryGetValue(rel, out var o);
            string tc = Text(c), tb = Text(b), to = Text(o);
            if (to == tb) { r.Kept.Add(rel); if (c != null && !string.Equals(Path.GetFullPath(c), Path.GetFullPath(Out(rel)), StringComparison.OrdinalIgnoreCase)) Write(Out(rel), File.ReadAllText(c)); continue; }
            if (tc == tb || tc == to)
            {
                // only the other side changed (or both the same way): take it
                if (o == null) { if (File.Exists(Out(rel))) File.Delete(Out(rel)); r.Deleted.Add(rel); }
                else { Write(Out(rel), File.ReadAllText(o)); r.Taken.Add(rel); }
                continue;
            }
            if (c == null || o == null || b == null)
            {
                // added on both sides differently, or deleted on one side and changed on the other: keep current, report
                if (c != null) Write(Out(rel), File.ReadAllText(c));
                r.Conflicts.Add(rel + (c == null ? " (deleted here, changed there - not taken)" : o == null ? " (changed here, deleted there - kept)" : " (added on both sides - kept here)"));
                continue;
            }
            var (code, text) = Run(null, "merge-file", "-p", "-L", "yours", "-L", "installed", "-L", "source", c, b, o);
            if (code < 0 || code > 127) { r.Conflicts.Add(rel + " (could not be merged: " + text + ")"); continue; }
            if (code > 0)
            {
                // your version before the merge, for the merge tool (the file itself gets the conflict markers)
                var yours = Path.Combine(Path.GetTempPath(), "tcagent-merge", Guid.NewGuid().ToString("N")[..8], Path.GetFileName(c));
                Directory.CreateDirectory(Path.GetDirectoryName(yours)!);
                File.Copy(c, yours, true);
                r.ConflictFiles.Add((rel, b, o, yours, Out(rel)));
            }
            Write(Out(rel), text);
            if (code > 0) r.Conflicts.Add($"{rel} ({code} conflict(s) marked with <<<<<<<)"); else r.Merged.Add(rel);
        }
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    static void CopyItem(string from, string to)
    {
        if (File.Exists(from)) { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, true); return; }
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }

    // ------------------------------------------------------------------ local diff
    /// <summary>What was changed in the installed item since the install (unified diff).</summary>
    public static string LocalDiff(string root, string key, Sources.LockEntry e, Sources.Source src)
    {
        var local = Sources.InstalledPath(root, key, e);
        var orig = Pristine(key, e, src, e.Commit);
        var (code, text) = Run(null, "diff", "--no-index", "--no-color", "-U3", orig, local);
        text = text.Replace(orig.Replace('\\', '/'), "installed").Replace(local.Replace('\\', '/'), "yours").Replace(orig, "installed").Replace(local, "yours");
        return code == 0 ? "No local changes." : text.Trim();
    }

    // ------------------------------------------------------------------ merge
    /// <summary>Merges the newest version of the source into the locally changed item (three-way, with a backup first).</summary>
    public static Result Merge(string root, string key, Sources.LockEntry e, Sources.Source src, string upstream, bool acceptLicense, bool dryRun)
    {
        var r = new Result();
        var local = Sources.InstalledPath(root, key, e);
        if (!File.Exists(local) && !Directory.Exists(local)) throw new InvalidOperationException($"{key} is missing - restore it with sources sync");
        var @base = Pristine(key, e, src, e.Commit);
        var theirs = Pristine(key, e, src, upstream, acceptLicense);
        var target = local;
        if (dryRun)
        {
            target = Path.Combine(Path.GetTempPath(), "tcagent-merge", Guid.NewGuid().ToString("N")[..8], Path.GetFileName(local));
            CopyItem(local, target);
        }
        else
        {
            r.Backup = Path.Combine(KnowledgeLayers.UserRoot, "backups", "sources", key.Replace('/', '-') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Path.GetFileName(local));
            CopyItem(local, r.Backup);
        }
        ThreeWay(local, @base, theirs, target, r);
        if (!dryRun)
        {
            // the lock points to the newer commit; its checksum is that of the pristine item, so the local changes stay visible
            e.Commit = upstream; e.Sha256 = Sources.Checksum(theirs); e.Installed = DateTime.Now;
            Sources.SetLock(root, key, e);
        }
        return r;
    }

    // ------------------------------------------------------------------ changed files, revert, keep as own version
    /// <summary>The files you changed, added or deleted - with the installed version next to them (for a compare window).</summary>
    public static List<(string Rel, string Installed, string Yours, string Kind)> ChangedFiles(string root, string key, Sources.LockEntry e, Sources.Source src)
    {
        var local = Sources.InstalledPath(root, key, e);
        var orig = Pristine(key, e, src, e.Commit);
        var a = FilesIn(orig); var b = FilesIn(local);
        var list = new List<(string, string, string, string)>();
        foreach (var rel in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
        {
            a.TryGetValue(rel, out var x); b.TryGetValue(rel, out var y);
            if (x != null && y != null && Text(x) == Text(y)) continue;
            list.Add((rel, x, y, x == null ? "added" : y == null ? "deleted" : "changed"));
        }
        return list;
    }

    /// <summary>Back to the installed version (your changes go to the backups first).</summary>
    public static string Revert(string root, string key, Sources.LockEntry e, Sources.Source src)
    {
        var local = Sources.InstalledPath(root, key, e);
        string backup = null;
        if (File.Exists(local) || Directory.Exists(local))
        {
            backup = Path.Combine(KnowledgeLayers.UserRoot, "backups", "sources", key.Replace('/', '-') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Path.GetFileName(local));
            CopyItem(local, backup);
        }
        var repo = Sources.Checkout(src.Name, e.Url, e.Commit);
        var it = Sources.Scan(repo).FirstOrDefault(i => i.RelPath == e.Path) ?? throw new InvalidOperationException($"{e.Path} is not in {src.Name} @{e.Commit[..8]}");
        Sources.Install(root, repo, it, src, e.Commit);
        return $"{key}: back to the installed version @{e.Commit[..8]}" + (backup != null ? $" - your version is in {backup}" : "");
    }

    /// <summary>Keeps the item as your own version: no longer tied to the source (no updates, no "changed" state); where it
    /// came from stays in its header (tcagent-source, -commit, -license and tcagent-detached).</summary>
    public static string Detach(string root, string key, Sources.LockEntry e)
    {
        var local = Sources.InstalledPath(root, key, e);
        var main = Directory.Exists(local) && Sources.FolderKinds.TryGetValue(Sources.KindDir(key), out var mf) ? Path.Combine(local, mf) : local;
        if (File.Exists(main))
            File.WriteAllText(main, Sources.AddMeta(File.ReadAllText(main), new Dictionary<string, string> { ["tcagent-detached"] = $"{e.Source}@{e.Commit[..Math.Min(12, e.Commit.Length)]}" }));
        Sources.Unlock(root, key);
        return $"{key} is your own version now - it is no longer updated from {e.Source}; its origin stays in the header.";
    }

    /// <summary>Installs into a shared level kept in Git as a commit on a new branch (pull request) - never pushed.</summary>
    public static string InstallViaPullRequest(string root, Sources.Item it, Sources.Source s, string commit, string checkout)
    {
        var repo = KnowledgeOps.RepoOf(root) ?? throw new InvalidOperationException("the level is not in a Git working copy");
        if (Sources.Git(repo, "status --porcelain").Length > 0) throw new InvalidOperationException("the shared repository has uncommitted changes - commit or stash them first");
        var previous = Sources.Git(repo, "rev-parse --abbrev-ref HEAD");
        var branch = "tcagent/install-" + KnowledgeLayers.Slug(it.Name) + "-" + DateTime.Now.ToString("yyyyMMddHHmm");
        Sources.Git(repo, $"switch -q -c {branch}");
        try
        {
            Sources.Install(root, checkout, it, s, commit);
            Sources.Git(repo, $"add -- \"{Path.GetRelativePath(repo, Sources.TargetOf(root, it))}\" \"{Path.GetRelativePath(repo, Path.Combine(root, Sources.LockFile))}\"");
            Sources.Git(repo, $"-c core.safecrlf=false commit -q -m \"Install {it.Key} from {s.Name}\"");
        }
        catch
        {
            try { Sources.Git(repo, "reset -q --hard"); Sources.Git(repo, "clean -fdq"); } catch { }
            throw;
        }
        finally { try { Sources.Git(repo, $"switch -q {previous}"); } catch { } }
        return $"Committed on branch {branch} in {repo} (not pushed) - push it and open a pull request; the owners of the level decide.";
    }

    // ------------------------------------------------------------------ uninstall
    /// <summary>Removes an installed item (its files and the lock entry) after copying it to the backups - the source stays.</summary>
    public static string Uninstall(string root, string key, Sources.LockEntry e)
    {
        var local = Sources.InstalledPath(root, key, e);
        string backup = null;
        if (File.Exists(local) || Directory.Exists(local))
        {
            backup = Path.Combine(KnowledgeLayers.UserRoot, "backups", "sources", key.Replace('/', '-') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Path.GetFileName(local));
            CopyItem(local, backup);
            if (Directory.Exists(local)) Directory.Delete(local, true);
            else
            {
                File.Delete(local);
                var notice = Path.ChangeExtension(local, ".LICENSE.txt");     // the notice copied along with a single-file item
                if (File.Exists(notice)) File.Delete(notice);
            }
        }
        Sources.Unlock(root, key);
        return $"Uninstalled {key} from {root}" + (backup != null ? $" - a copy is in {backup}" : " (its files were already gone)");
    }

    // ------------------------------------------------------------------ propose (pull request)
    /// <summary>The local changes as a commit on a new branch of a working copy of the source, plus a patch file.</summary>
    public static Result Propose(string root, string key, Sources.LockEntry e, Sources.Source src, string message, IEnumerable<string> privateTerms)
    {
        var r = new Result();
        var local = Sources.InstalledPath(root, key, e);
        var cache = Sources.CacheOf(src.Name);
        var orig = Pristine(key, e, src, e.Commit);
        var name = key.Split('/')[1];
        var user = Regex.Replace(Environment.UserName.ToLowerInvariant(), "[^a-z0-9]+", "-");
        r.Branch = $"tcagent/{user}-{name}-{DateTime.Now:yyyyMMdd-HHmm}";
        r.Folder = Path.Combine(KnowledgeLayers.UserRoot, "contrib", src.Name);
        if (Directory.Exists(r.Folder)) { Run(cache, "worktree", "remove", "--force", r.Folder); if (Directory.Exists(r.Folder)) Directory.Delete(r.Folder, true); }
        Run(cache, "worktree", "prune");
        var (wc, wo) = Run(cache, "worktree", "add", "-f", "-B", r.Branch, r.Folder, e.Commit);
        if (wc != 0) throw new InvalidOperationException("git worktree add: " + wo.Trim());
        var item = Path.Combine(r.Folder, e.Path.Replace('/', '\\'));
        // base = installed (with TcAgent's metadata), other = yours: only your changes reach the source's own files
        ThreeWay(item, orig, local, item, r);
        if (r.Conflicts.Count > 0)
            throw new InvalidOperationException("Your changes do not fit the installed version cleanly (" + string.Join(", ", r.Conflicts) +
                                                ") - edit the item so that the conflicts are gone, or send the patch by hand.");
        r.Specifics = Improver.Specifics(string.Join("\n", FilesIn(item).Values.Select(f => Text(f) ?? "")), privateTerms);
        Run(r.Folder, "add", "-A", "--", e.Path);
        if (Run(r.Folder, "diff", "--cached", "--quiet").Code == 0) throw new InvalidOperationException("No local changes to propose.");
        var (cc, co) = Run(r.Folder, "commit", "-q", "-m", string.IsNullOrWhiteSpace(message) ? $"{name}: improvements" : message.Trim());
        if (cc != 0) throw new InvalidOperationException("git commit: " + co.Trim() + " (set your name and e-mail: git config --global user.name / user.email)");
        r.Patch = Path.Combine(KnowledgeLayers.UserRoot, "contrib", $"{src.Name}-{name}.patch");
        var (_, patch) = Run(r.Folder, "format-patch", "-1", "--stdout");
        File.WriteAllText(r.Patch, patch, new UTF8Encoding(false));
        r.CompareUrl = CompareUrl(src.Url, src.Ref, r.Branch, null);
        return r;
    }

    /// <summary>Pushes the proposal branch - to the source or to a fork (its URL) - and returns the page to open the pull request.</summary>
    public static (string Output, string CompareUrl) Push(Sources.Source src, string branch, string remoteUrl)
    {
        var folder = Path.Combine(KnowledgeLayers.UserRoot, "contrib", src.Name);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("No proposal prepared for " + src.Name);
        var remote = string.IsNullOrWhiteSpace(remoteUrl) ? src.Url : remoteUrl.Trim();
        var (code, o) = Run(folder, "push", remote, $"HEAD:refs/heads/{branch}");
        if (code != 0) throw new InvalidOperationException("git push: " + o.Trim() +
                                                           "\nNo write access? Fork the repository on GitHub and push to the URL of your fork.");
        return (o.Trim(), CompareUrl(src.Url, src.Ref, branch, remote.Equals(src.Url, StringComparison.OrdinalIgnoreCase) ? null : remote));
    }

    /// <summary>GitHub: the page that opens a pull request of the branch (from a fork: owner:branch).</summary>
    static string CompareUrl(string url, string baseRef, string branch, string fork)
    {
        var m = Regex.Match(url ?? "", @"github\.com[/:]([^/]+)/([^/]+?)(\.git)?/?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var head = branch;
        if (fork != null && Regex.Match(fork, @"github\.com[/:]([^/]+)/", RegexOptions.IgnoreCase) is { Success: true } f) head = f.Groups[1].Value + ":" + branch;
        var b = string.IsNullOrEmpty(baseRef) || baseRef == "HEAD" ? "" : Uri.EscapeDataString(baseRef) + "...";
        return $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}/compare/{b}{Uri.EscapeDataString(head).Replace("%2F", "/").Replace("%3A", ":")}?expand=1";
    }
}
