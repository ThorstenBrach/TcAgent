// Knowledge that cannot work without a plugin (skills, workflows, prompts, rules, lessons): tcagent-requires-plugin in the
// metadata of the front matter names the plugin and the versions it must have.
//
//   tcagent-requires-plugin: line-check >=1.2 <2.0, label-printer
//
// Several plugins with a comma, the versions after the id (>=, >, <=, <, =; a bare number means >=). Without versions any
// version fits. Installing such an item from a source is refused while the requirement is not met; an item already there
// has the status "needs plugin" and does not take effect, until the plugin fits again (KnowledgeLayers.ApplyRules).
//
//   tcagent-requires-tcagent: >=0.53
//
// does the same for the version of TcAgent: for an item that uses a tool or function that older versions do not have.
//
//   tcagent-requires-module: scope, hmi
//
// does the same for modules (Modules.cs): while one of them is switched off, the item has the status "needs module". An item
// that only names tools of a switched-off module stays active and gets a note - a review still works without the static analysis.
//
// Pure logic, so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace TcAgent;

public static class PluginRequirements
{
    /// <summary>The key in the metadata, without the prefix tcagent-.</summary>
    public const string Key = "requires-plugin";

    /// <summary>The status of an entry whose plugin does not fit.</summary>
    public const string Status = "needs plugin";

    /// <summary>The key of the TcAgent versions an item needs, because it uses something newer.</summary>
    public const string TcAgentKey = "requires-tcagent";

    /// <summary>The status of an entry that needs another version of TcAgent.</summary>
    public const string TcAgentStatus = "needs TcAgent";

    /// <summary>The key of the modules an item cannot work without (Modules.cs).</summary>
    public const string ModuleKey = "requires-module";

    /// <summary>The status of an entry whose module is switched off.</summary>
    public const string ModuleStatus = "needs module";

    /// <summary>Is the entry only waiting for a plugin, a module or a TcAgent version? It stays visible, the lists say what is missing.</summary>
    public static bool Waiting(KnowledgeLayers.Entry e) => e.Status is Status or TcAgentStatus or ModuleStatus;

    /// <summary>What is missing of the modules ("scope, hmi"), or null. off: the modules switched off; known: all module ids.</summary>
    public static string MissingModules(string spec, ICollection<string> off, ICollection<string> known)
    {
        var problems = new List<string>();
        foreach (var id in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Contains(id, StringComparer.OrdinalIgnoreCase)) problems.Add($"module {id} does not exist");
            else if (off.Contains(id, StringComparer.OrdinalIgnoreCase)) problems.Add($"module {id} is switched off");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    static readonly Regex ToolName = new(@"\b(?:tc|ads|x)_[a-z0-9_]+\b");

    /// <summary>A note for an item that names tools of switched-off modules - it stays active, only some of its steps are missing.</summary>
    public static string OffModuleNote(string text, ICollection<string> off, Func<string, string> moduleOf)
    {
        var hits = ToolName.Matches(text ?? "").Select(m => m.Value).Distinct()
                           .Select(t => (tool: t, module: moduleOf(t))).Where(x => x.module != null && off.Contains(x.module, StringComparer.OrdinalIgnoreCase))
                           .GroupBy(x => x.module).Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.tool))})").ToList();
        return hits.Count == 0 ? null : "uses tools of switched-off modules: " + string.Join("; ", hits);
    }

    /// <summary>What is missing for the TcAgent versions, or null if this one fits.</summary>
    public static string MissingTcAgent(string versions, string current) =>
        string.IsNullOrWhiteSpace(versions) || Fits(current, versions) ? null : $"TcAgent {versions.Trim()} required, this is {current}";

    public sealed record Need(string Id, string Versions);

    static readonly Regex Constraint = new(@"^(>=|<=|>|<|=)?\s*(\d+(?:\.\d+)*)$");

    public static List<Need> Parse(string spec)
    {
        var list = new List<Need>();
        foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list.Add(new Need(words[0], words.Length > 1 ? words[1] : ""));
        }
        return list;
    }

    /// <summary>Numbers compared part by part; what follows "-" or "+" does not count, a missing part is 0.</summary>
    public static int Compare(string a, string b)
    {
        static int[] Parts(string v) => Regex.Match(v ?? "", @"^\d+(\.\d+)*").Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        int[] x = Parts(a), y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var d = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (d != 0) return d;
        }
        return 0;
    }

    /// <summary>Does the version fit all constraints ("&gt;=1.2 &lt;2.0")? A constraint that cannot be read does not fit.</summary>
    public static bool Fits(string version, string versions)
    {
        var words = Regex.Replace(versions ?? "", @"(>=|<=|>|<|=)\s+", "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        if (!Regex.IsMatch(version ?? "", @"^\d")) return false;
        foreach (var w in words)
        {
            var m = Constraint.Match(w);
            if (!m.Success) return false;
            var c = Compare(version, m.Groups[2].Value);
            var ok = m.Groups[1].Value switch { ">" => c > 0, "<" => c < 0, "<=" => c <= 0, "=" => c == 0, _ => c >= 0 };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>What is missing for the requirement, or null if it is met.</summary>
    public static string Missing(string spec, IEnumerable<Plugins.Plugin> found)
    {
        var problems = new List<string>();
        foreach (var n in Parse(spec))
        {
            var wanted = n.Versions.Length > 0 ? $"{n.Id} {n.Versions}" : n.Id;
            var p = found.FirstOrDefault(x => x.Id.Equals(n.Id, StringComparison.OrdinalIgnoreCase));
            if (p == null) problems.Add($"plugin {wanted} is not installed");
            else if (!p.Ready) problems.Add($"plugin {n.Id} is not ready ({p.Status})");
            else if (!Fits(p.Version, n.Versions)) problems.Add($"plugin {wanted} required, installed is {p.Version ?? "no version"}");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
