// Coding rules: Markdown files that TcAgent presents to the agent before it writes code for the first time.
// Levels and platforms (KnowledgeLayers.cs, docs/KNOWLEDGE_LAYERS.md), kind "coding-rules": on every level the folder
// coding-rules\ with one file per topic, and the former single file coding-rules.md:
//   company   TCAGENT_COMPANY\coding-rules\ (and TCAGENT_RULES - files/folders, ';'-separated)
//   user      %LOCALAPPDATA%\TcAgent\coding-rules\                    personal rules
//   platform  <company or user>\platforms\<name>\coding-rules\         rules of the automation framework the project uses
//   project   <Solution>\.tcagent\coding-rules\                       project rules (versioned with Git)
//   (<Solution>\.tcagent\rules\*.md - the former name of the project folder - is still read)
// A later level replaces a file with the same name; a locked file cannot be replaced. Later files appear further down and
// take precedence in case of conflicts.
using System.Text;

namespace TcAgent;

public static class CodingRules
{
    /// <summary>All rule files with their status (active, replaced, refused, disabled ...).</summary>
    public static List<KnowledgeLayers.Entry> Entries(string solutionDir, KnowledgeLayers.PlatformInfo pi = null)
    {
        pi ??= KnowledgeLayers.Platforms(solutionDir);
        var extraProject = string.IsNullOrEmpty(solutionDir) ? null : new[] { Path.Combine(solutionDir, ".tcagent", "rules") };     // former folder name
        return KnowledgeLayers.Resolve("coding-rules", solutionDir, pi.Active, replaceByName: true,
                                       (Environment.GetEnvironmentVariable("TCAGENT_RULES") ?? "").Split(';'), extraProject);
    }

    public static List<string> Files(string solutionDir) => Entries(solutionDir).Where(e => e.Active).Select(e => e.Path).ToList();

    /// <summary>All rules as one text, or null if none are defined. Every file with its level; a note on the platform first.</summary>
    public static string Text(string solutionDir, int maxChars = 40000)
    {
        var pi = KnowledgeLayers.Platforms(solutionDir);
        // a forced user file comes after the platform files (it wins against them) but before the project
        var entries = Entries(solutionDir, pi).Where(e => e.Active)
                                              .OrderBy(e => e.Force && e.Layer.Level == "user" && e.Layer.Platform == null ? 999 : e.Layer.Rank).ToList();
        var note = KnowledgeLayers.PlatformNote(pi);
        if (entries.Count == 0 && pi.Ambiguous.Count == 0) return null;
        var sb = new StringBuilder();
        if (note != null) sb.AppendLine(note).AppendLine();
        foreach (var e in entries)
        {
            string t;
            try { t = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(e.Path)).Body.Trim(); } catch (Exception ex) { t = "(not readable: " + ex.Message + ")"; }
            if (t.Length == 0) continue;
            var tags = e.Layer.Label + (e.Locked ? ", locked" : "") + (e.Status == "forced" ? ", kept with force" : "");
            sb.AppendLine($"===== {e.Path} ({tags}) =====").AppendLine(t).AppendLine();
        }
        if (sb.Length == 0) return null;
        return sb.Length <= maxChars ? sb.ToString() : sb.ToString(0, maxChars) + "\n... (truncated - please shorten the rules)";
    }

    /// <summary>File that a new rule is written to. One file per topic in the folder coding-rules\ of the level (project =
    /// Solution\.tcagent, personal = %LOCALAPPDATA%\TcAgent, platform:&lt;name&gt; = %LOCALAPPDATA%\TcAgent\platforms\&lt;name&gt;);
    /// a level that still has only the former single file coding-rules.md keeps using it.</summary>
    public static string TargetFile(string solutionDir, string scope, string section = null)
    {
        var s = (scope ?? "").Trim();
        var root = s.StartsWith("platform:", StringComparison.OrdinalIgnoreCase) ? Path.Combine(KnowledgeLayers.UserRoot, "platforms", s[9..].Trim())
                 : s.Equals("personal", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(solutionDir) ? KnowledgeLayers.UserRoot
                 : KnowledgeLayers.ProjectRoot(solutionDir);
        var single = Path.Combine(root, "coding-rules.md");
        var folder = Path.Combine(root, "coding-rules");
        if (File.Exists(single) && !Directory.Exists(folder)) return single;
        return Path.Combine(folder, KnowledgeLayers.Slug(string.IsNullOrWhiteSpace(section) ? "additions" : section.Trim().TrimStart('#').Trim()) + ".md");
    }

    /// <summary>Inserts the rule as a bullet point under a heading (default "Additions"; an existing German "Ergaenzungen"
    /// section of older rule files is reused). Returns (file, result).</summary>
    public static (string file, string result) Add(string solutionDir, string scope, string rule, string section, bool dryRun, bool force = false)
    {
        var file = TargetFile(solutionDir, scope, section);
        rule = string.Join(" ", (rule ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())).Trim();
        if (rule.StartsWith("- ")) rule = rule[2..];
        if (rule.Length == 0) throw new Exception("rule is empty");
        // one file per topic (coding-rules\<topic>.md): the file is the section - the rule goes to its end
        bool topicFile = Path.GetFileName(Path.GetDirectoryName(file)!).Equals("coding-rules", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(section) ? "Additions" : section.Trim().TrimStart('#').Trim();
        var text = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : topicFile ? $"# {title}\n" : "# Coding rules\n";
        if (string.IsNullOrWhiteSpace(section))
            section = text.Split('\n').Any(l => l.StartsWith("## ") && l[3..].Trim().Equals("Ergaenzungen", StringComparison.OrdinalIgnoreCase)) ? "Ergaenzungen" : "Additions";
        else section = section.Trim().TrimStart('#').Trim();
        if (text.Contains("- " + rule)) return (file, "already present - nothing changed");
        var lines = text.TrimEnd('\n').Split('\n').ToList();
        int head = lines.FindIndex(l => l.StartsWith("## ") && l[3..].Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        int insertAt;
        if (topicFile)
        {
            if (lines.Count == 1) lines.Add("");
            insertAt = lines.Count;
        }
        else if (head < 0)
        {
            lines.Add(""); lines.Add("## " + section);
            insertAt = lines.Count;
        }
        else
        {
            insertAt = head + 1;
            for (int i = head + 1; i < lines.Count && !lines[i].StartsWith("## ") && !lines[i].StartsWith("# "); i++)
                if (lines[i].Trim().Length > 0) insertAt = i + 1;
        }
        lines.Insert(insertAt, "- " + rule);
        // force: the user file stays against a platform file with the same name (front matter, Agent Skills format)
        if (force && topicFile && !(lines.Count > 0 && lines[0] == "---"))
            lines.InsertRange(0, new[] { "---", $"name: {Path.GetFileNameWithoutExtension(file)}", $"description: {title}", "metadata:", "  tcagent-force: \"true\"", "---" });
        var result = string.Join("\r\n", lines) + "\r\n";
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, result, new UTF8Encoding(false));
        }
        return (file, (dryRun ? "PROPOSAL (not written): " : "added: ") + $"## {section}\n- {rule}");
    }

    public static string Where(string solutionDir) =>
        "Location: " + (string.IsNullOrEmpty(solutionDir) ? "<solution folder>" : solutionDir) + "\\.tcagent\\coding-rules\\*.md (project, in Git), " +
        "%LOCALAPPDATA%\\TcAgent\\coding-rules\\*.md (personal), <company or user>\\platforms\\<name>\\coding-rules\\*.md (platform), " +
        "TCAGENT_COMPANY\\coding-rules\\*.md or TCAGENT_RULES (company); the former single files coding-rules.md are read too. " +
        "tc_knowledge action=list kind=coding-rules shows which file applies and why.";
}
