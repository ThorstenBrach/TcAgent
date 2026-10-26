// Skills: how a kind of task is done (KnowledgeLayers.cs, docs/KNOWLEDGE_LAYERS.md) - in the open Agent Skills format:
// a folder <level>\skills\<name>\ with SKILL.md (front matter name, description; TcAgent's fields under metadata) and
// optional further files (scripts\, references\, assets\).
//
//   levels     built-in (shipped with TcAgent: knowledge\skills next to TcAgent.exe), company, user, platform, project
//   tc_skills  list (name + description - the agent loads a skill only when the task fits), show, add, mirror
//   mirror     copies the skills that apply everywhere (built-in, company, user) into the skill folders of the agent CLIs
//              (default %USERPROFILE%\.claude\skills and %USERPROFILE%\.agents\skills, or "skillMirrors" in settings.json),
//              so a client also finds them without a tool call. Only folders with the marker file .tcagent-mirror are
//              updated or removed - skills the user put there by hand are never touched.
//
// Pure file logic (no IDE), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Skills
{
    public const string MainFile = "SKILL.md";
    const string Marker = ".tcagent-mirror";

    /// <summary>Name rules of the Agent Skills standard: 1-64 characters, lowercase letters, digits and single hyphens.</summary>
    public static string ValidateName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 64 || !Regex.IsMatch(n, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new ArgumentException($"name '{name}': 1-64 lowercase letters, digits and single hyphens (e.g. plc-code-review)");
        return n;
    }

    public static List<KnowledgeLayers.Entry> Entries(string solutionDir, IEnumerable<string> platforms) =>
        KnowledgeLayers.Resolve("skills", solutionDir, platforms, replaceByName: true, folderFile: MainFile);

    public static string Description(KnowledgeLayers.Entry e) =>
        e.Meta.Top.TryGetValue("description", out var d) && d.Length > 0 ? d : "(no description)";

    /// <summary>The compact list for the agent (name + description only).</summary>
    public static string Catalog(IEnumerable<KnowledgeLayers.Entry> entries)
    {
        var active = entries.Where(e => e.Active).ToList();
        if (active.Count == 0) return null;
        var sb = new StringBuilder("Skills (how this team does these tasks - load one with tc_skills action=show name=<name> when the task fits):\n");
        foreach (var e in active) sb.AppendLine($"- {e.Name} ({e.Layer.Label}): {Description(e)}");
        return sb.ToString();
    }

    /// <summary>The skill text for the agent: body plus the list of its further files (read them with the file tools when needed).</summary>
    public static string Show(KnowledgeLayers.Entry e)
    {
        var dir = Path.GetDirectoryName(e.Path)!;
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                             .Where(f => !f.Equals(e.Path, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f) != Marker)
                             .Select(f => Path.GetRelativePath(dir, f)).ToList();
        var sb = new StringBuilder($"Skill {e.Name} ({e.Layer.Label}{(e.Locked ? ", locked" : "")}) – {Description(e)}\n");
        if (e.Meta.Get("inspired-by") is { } src) sb.AppendLine("Inspired by: " + src);
        sb.AppendLine("Folder: " + dir).AppendLine().AppendLine(e.Meta.Body.Trim());
        if (files.Count > 0) sb.AppendLine().AppendLine("Further files of the skill: " + string.Join(", ", files));
        return sb.ToString();
    }

    /// <summary>Root folder of a scope: project (default), personal, platform:&lt;name&gt;.</summary>
    public static string RootOf(string solutionDir, string scope)
    {
        var s = (scope ?? "project").Trim();
        if (s.StartsWith("platform:", StringComparison.OrdinalIgnoreCase)) return Path.Combine(KnowledgeLayers.UserRoot, "platforms", s[9..].Trim());
        if (s.Equals("personal", StringComparison.OrdinalIgnoreCase) || s.Equals("user", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.UserRoot;
        if (s.Equals("company", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The company level is shared: create the skill with scope=personal or project and promote it.");
        return KnowledgeLayers.ProjectRoot(solutionDir) ?? KnowledgeLayers.UserRoot;
    }

    /// <summary>Writes a new skill (or replaces the text of an own one). Returns the SKILL.md path.</summary>
    public static string Add(string solutionDir, string scope, string name, string description, string body, string inspiredBy, bool write)
    {
        name = ValidateName(name);
        description = Regex.Replace(description ?? "", @"\s+", " ").Trim();
        if (description.Length is 0 or > 1024) throw new ArgumentException("description: 1-1024 characters - what the skill does and when to use it");
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("body: the instructions of the skill (Markdown)");
        var file = Path.Combine(RootOf(solutionDir, scope), "skills", name, MainFile);
        var sb = new StringBuilder("---\n").Append("name: ").Append(name).Append('\n')
                                          .Append("description: ").Append(description).Append('\n');
        if (!string.IsNullOrWhiteSpace(inspiredBy)) sb.Append("metadata:\n  tcagent-inspired-by: ").Append(inspiredBy.Trim()).Append('\n');
        sb.Append("---\n").Append(body.Trim()).Append('\n');
        if (write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, sb.ToString().Replace("\n", "\r\n"), new UTF8Encoding(false));
        }
        return file;
    }

    // ------------------------------------------------------------------ mirror
    public static List<string> DefaultMirrors()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string> { Path.Combine(home, ".claude", "skills"), Path.Combine(home, ".agents", "skills") };
        // settings.json "skillMirrors": [...] replaces the default list
        try
        {
            var f = Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["skillMirrors"] is JsonArray arr)
                list = arr.Select(x => Environment.ExpandEnvironmentVariables(x?.ToString() ?? "")).Where(x => x.Length > 0).ToList();
        }
        catch { }
        return list;
    }

    /// <summary>Copies the skills that apply everywhere (built-in, company, user - not platform or project) into the target
    /// folders; removes skills TcAgent mirrored earlier that no longer apply. Returns one line per action.</summary>
    public static List<string> Mirror(IEnumerable<KnowledgeLayers.Entry> entries, IEnumerable<string> targets, bool write = true)
    {
        var report = new List<string>();
        var general = entries.Where(e => e.Active && e.Layer.Platform == null && e.Layer.Level is "built-in" or "company" or "user").ToList();
        foreach (var target in targets)
        {
            // only mirror into a client that exists (its parent folder, e.g. .claude, is there)
            if (!Directory.Exists(Path.GetDirectoryName(target.TrimEnd('\\', '/')))) { report.Add($"skipped {target} (client not set up)"); continue; }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in general)
            {
                var src = Path.GetDirectoryName(e.Path)!;
                var dst = Path.Combine(target, e.Name);
                names.Add(e.Name);
                if (Directory.Exists(dst) && !File.Exists(Path.Combine(dst, Marker))) { report.Add($"kept {dst} (not created by TcAgent)"); continue; }
                if (write)
                {
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    CopyDir(src, dst);
                    File.WriteAllText(Path.Combine(dst, Marker), $"Mirrored by TcAgent from {src} - changes here are overwritten. Edit the source instead.\r\n");
                }
                report.Add($"{e.Name} → {dst}");
            }
            if (Directory.Exists(target))
                foreach (var d in Directory.GetDirectories(target))
                    if (File.Exists(Path.Combine(d, Marker)) && !names.Contains(Path.GetFileName(d)))
                    {
                        if (write) Directory.Delete(d, true);
                        report.Add($"removed {d} (no longer applies)");
                    }
        }
        return report;
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
        }
    }
}
