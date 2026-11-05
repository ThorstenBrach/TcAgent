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

    /// <summary>Is the entry only waiting for a plugin or a TcAgent version? It stays visible, the lists say what is missing.</summary>
    public static bool Waiting(KnowledgeLayers.Entry e) => e.Status is Status or TcAgentStatus;

    /// <summary>What is missing for the TcAgent versions, or null if this one fits.</summary>
    public static string MissingTcAgent(string versions, string current) =>
        string.IsNullOrWhiteSpace(versions) || Fits(current, versions) ? null : $"needs TcAgent {versions.Trim()}, this is {current}";

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
            else if (!Fits(p.Version, n.Versions)) problems.Add($"needs plugin {wanted}, installed is {p.Version ?? "no version"}");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
