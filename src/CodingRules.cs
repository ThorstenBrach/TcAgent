// Coding rules: Markdown files that TcAgent presents to the agent before it writes code for the first time.
//   1. TCAGENT_RULES                         files/folders, ';'-separated (e.g. company rules on a network drive)
//   2. %LOCALAPPDATA%\TcAgent\coding-rules.md  personal rules
//   3. <Solution>\.tcagent\coding-rules.md      project rules (versioned with Git)
//   4. <Solution>\.tcagent\rules\*.md           further project files
// Later sources appear further down and take precedence in case of conflicts.
using System.Text;

namespace TcAgent;

public static class CodingRules
{
    public static List<string> Files(string solutionDir)
    {
        var list = new List<string>();
        void AddPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (Directory.Exists(p)) list.AddRange(Directory.GetFiles(p, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) list.Add(p);
        }
        foreach (var p in (Environment.GetEnvironmentVariable("TCAGENT_RULES") ?? "").Split(';')) AddPath(p);
        AddPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "coding-rules.md"));
        if (!string.IsNullOrEmpty(solutionDir))
        {
            AddPath(Path.Combine(solutionDir, ".tcagent", "coding-rules.md"));
            AddPath(Path.Combine(solutionDir, ".tcagent", "rules"));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All rules as one text, or null if none are defined.</summary>
    public static string Text(string solutionDir, int maxChars = 40000)
    {
        var files = Files(solutionDir);
        if (files.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            string t;
            try { t = File.ReadAllText(f).Trim(); } catch (Exception ex) { t = "(not readable: " + ex.Message + ")"; }
            if (t.Length == 0) continue;
            sb.AppendLine($"===== {f} =====").AppendLine(t).AppendLine();
        }
        if (sb.Length == 0) return null;
        return sb.Length <= maxChars ? sb.ToString() : sb.ToString(0, maxChars) + "\n... (truncated - please shorten the rules)";
    }

    /// <summary>File that new rules are written to: project = Solution\.tcagent\coding-rules.md, personal = %LOCALAPPDATA%.</summary>
    public static string TargetFile(string solutionDir, string scope) =>
        string.Equals(scope, "personal", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(solutionDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "coding-rules.md")
            : Path.Combine(solutionDir, ".tcagent", "coding-rules.md");

    /// <summary>Inserts the rule as a bullet point under a heading (default "Additions"; an existing German "Ergaenzungen"
    /// section of older rule files is reused). Returns (file, result).</summary>
    public static (string file, string result) Add(string solutionDir, string scope, string rule, string section, bool dryRun)
    {
        var file = TargetFile(solutionDir, scope);
        rule = string.Join(" ", (rule ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())).Trim();
        if (rule.StartsWith("- ")) rule = rule[2..];
        if (rule.Length == 0) throw new Exception("rule is empty");
        var text = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : "# Coding rules\n";
        if (string.IsNullOrWhiteSpace(section))
            section = text.Split('\n').Any(l => l.StartsWith("## ") && l[3..].Trim().Equals("Ergaenzungen", StringComparison.OrdinalIgnoreCase)) ? "Ergaenzungen" : "Additions";
        else section = section.Trim().TrimStart('#').Trim();
        if (text.Contains("- " + rule)) return (file, "already present - nothing changed");
        var lines = text.TrimEnd('\n').Split('\n').ToList();
        int head = lines.FindIndex(l => l.StartsWith("## ") && l[3..].Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        int insertAt;
        if (head < 0)
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
        var result = string.Join("\r\n", lines) + "\r\n";
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, result, new UTF8Encoding(false));
        }
        return (file, (dryRun ? "PROPOSAL (not written): " : "added: ") + $"## {section}\n- {rule}");
    }

    public static string Where(string solutionDir) =>
        "Location: " + (string.IsNullOrEmpty(solutionDir) ? "<solution folder>" : solutionDir) + "\\.tcagent\\coding-rules.md (project, in Git), " +
        "%LOCALAPPDATA%\\TcAgent\\coding-rules.md (personal) or TCAGENT_RULES (files/folders, ';'-separated).";
}
