// Source control bindings of a solution (TFS / VSS: "SAK" entries, GlobalSection(TeamFoundationVersionControl), .vspscc).
// A TwinCAT solution still bound to a TFS server that cannot be reached made the XAE Shell crash or refuse "Cannot insert
// project" while TcAgent changed it - even with Git chosen as the source control plug-in. TcAgent finds the bindings and
// warns (tc_attach, doctor --solution); it never changes these files. Pure file logic, so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace TcAgent;

public static class SourceControlBindings
{
    static readonly Regex SlnSection = new(@"GlobalSection\((TeamFoundationVersionControl|SourceCodeControl)\)", RegexOptions.IgnoreCase);
    static readonly Regex SccEntry = new(@"<Scc(ProjectName|LocalPath|AuxPath|Provider)>\s*[^<\s]", RegexOptions.IgnoreCase);
    static readonly string[] ProjectFiles = { "*.tsproj", "*.tspproj", "*.plcproj", "*.csproj", "*.hmiproj", "*.tcscopeproj", "*.tmcproj" };
    static readonly string[] SkipDirs = { "_Boot", "_CompileInfo", "_Libraries", "bin", "obj", ".git", ".vs" };

    /// <summary>The files with source control bindings (relative to the solution folder) and what was found.</summary>
    public static List<string> Find(string slnPath, int maxFiles = 300)
    {
        var hits = new List<string>();
        if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath)) return hits;
        var dir = Path.GetDirectoryName(Path.GetFullPath(slnPath)) ?? ".";
        try
        {
            var sln = File.ReadAllText(slnPath);
            if (SlnSection.IsMatch(sln) || sln.Contains("SccProjectName", StringComparison.OrdinalIgnoreCase))
                hits.Add(Path.GetFileName(slnPath) + ": source control section (TFS/VSS)");
        }
        catch (IOException) { }
        int n = 0;
        foreach (var f in Files(dir))
        {
            if (++n > maxFiles) break;
            var rel = Path.GetRelativePath(dir, f);
            if (f.EndsWith(".vspscc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vssscc", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(rel + ": source control file");
                continue;
            }
            try { if (SccEntry.IsMatch(File.ReadAllText(f))) hits.Add(rel + ": Scc entries (e.g. SAK)"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return hits;
    }

    static IEnumerable<string> Files(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try
            {
                files = ProjectFiles.Concat(new[] { "*.vspscc", "*.vssscc" }).SelectMany(p => Directory.GetFiles(d, p)).ToArray();
                dirs = Directory.GetDirectories(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var f in files) yield return f;
            foreach (var sub in dirs)
                if (!SkipDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
        }
    }

    /// <summary>The warning for tc_attach and doctor, or null when the solution has no bindings.</summary>
    public static string Warning(string slnPath)
    {
        var hits = Find(slnPath);
        if (hits.Count == 0) return null;
        return "WARNING: this solution is still bound to a source control server (TFS/VSS) - " + string.Join("; ", hits.Take(5)) +
               (hits.Count > 5 ? $"; and {hits.Count - 5} more" : "") + ". With a server that cannot be reached the IDE may crash or refuse " +
               "changes (\"Cannot insert project\") while TcAgent edits the solution, even with Git as the source control plug-in. " +
               "Remove the bindings before larger changes (File > Source Control > Change Source Control > Unbind, or delete the Scc* " +
               "entries and the GlobalSection(TeamFoundationVersionControl) of the .sln) - TcAgent does not change these files.";
    }
}
