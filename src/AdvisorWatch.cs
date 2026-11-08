// Advisor Live: what signals a fault in a project (docs/ADVISOR.md).
//
//   <solution dir>\.tcagent\advisor-watch.json      edited by hand, or in the chat (tc_advisor action=watch_add)
//   <plugin folder>\advisor.json                    a plugin brings what it knows about its platform
//
//   {
//     "skill": "my-platform-diagnose",                       (plugin only: the skill that knows how the platform reports faults)
//     "signals": [
//       { "symbol": "MAIN.fbStation.bError", "when": "true", "text": "Station reports a fault" },
//       { "symbol": "MAIN.fbStation.nFault", "when": "!= 0", "text": "Fault number", "details": ["MAIN.fbStation.nStep"] },
//       { "symbol": "MAIN.fbStation.nState", "when": "& 128", "text": "Error bit of the state" },
//       { "symbol": "MAIN.fbConveyor.nStep", "when": "unchanged 30s", "text": "Conveyor hangs in a step", "port": 851 }
//     ]
//   }
//
//   when   true | false | == n | != n | > n | >= n | < n | <= n | & n (a bit of the mask is set) | unchanged <n>s
//
// Pure logic, so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class AdvisorWatch
{
    public sealed record Signal(string Symbol, string When, string Text, List<string> Details, int Port = 851, string Source = "project");

    public sealed record Hint(string Plugin, string Skill);

    public static string FileOf(string solutionDir) => Path.Combine(solutionDir, ".tcagent", "advisor-watch.json");

    static readonly Regex Compare = new(@"^(==|!=|>=|<=|>|<|&)\s*(-?\d+(?:\.\d+)?)$");
    static readonly Regex Unchanged = new(@"^unchanged\s+(\d+)\s*s$", RegexOptions.IgnoreCase);

    /// <summary>Checks a condition; null = fine, otherwise what is wrong with it.</summary>
    public static string CheckWhen(string when)
    {
        var w = (when ?? "").Trim().ToLowerInvariant();
        return w is "true" or "false" || Compare.IsMatch(w) || Unchanged.IsMatch(w) ? null
             : "when: true, false, == n, != n, > n, >= n, < n, <= n, & n (bit mask) or unchanged <n>s";
    }

    public static List<Signal> Parse(string json, string source)
    {
        var list = new List<Signal>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        var o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        foreach (var n in o?["signals"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject s) continue;
            var symbol = s["symbol"]?.ToString();
            var when = s["when"]?.ToString() ?? "true";
            if (string.IsNullOrWhiteSpace(symbol) || CheckWhen(when) != null) continue;
            var details = (s["details"] as JsonArray)?.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
            int port = s["port"] is JsonValue pv && int.TryParse(pv.ToString(), out var p) ? p : 851;
            list.Add(new Signal(symbol.Trim(), when.Trim(), s["text"]?.ToString() ?? symbol.Trim(), details, port, source));
        }
        return list;
    }

    /// <summary>The signals of the project and of the plugins that are ready, and the skills the plugins name.</summary>
    public static (List<Signal> signals, List<Hint> hints) Load(string solutionDir, IEnumerable<(string id, string folder)> plugins)
    {
        var signals = new List<Signal>();
        var hints = new List<Hint>();
        if (!string.IsNullOrEmpty(solutionDir) && File.Exists(FileOf(solutionDir)))
            signals.AddRange(Parse(File.ReadAllText(FileOf(solutionDir)), "project"));
        foreach (var (id, folder) in plugins ?? Enumerable.Empty<(string, string)>())
        {
            var f = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "advisor.json");
            if (f == null || !File.Exists(f)) continue;
            var text = File.ReadAllText(f);
            signals.AddRange(Parse(text, "plugin " + id));
            try { if ((JsonNode.Parse(text) as JsonObject)?["skill"]?.ToString() is { Length: > 0 } skill) hints.Add(new Hint(id, skill)); } catch { }
        }
        return (signals, hints);
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Adds or replaces (same symbol and condition) a signal of the project file.</summary>
    public static void Add(string solutionDir, Signal s)
    {
        if (CheckWhen(s.When) is { } bad) throw new ArgumentException(bad);
        if (string.IsNullOrWhiteSpace(s.Symbol)) throw new ArgumentException("symbol: the variable, e.g. MAIN.fbStation.bError");
        var list = Project(solutionDir).Where(x => !(x.Symbol.Equals(s.Symbol, StringComparison.OrdinalIgnoreCase) && x.When == s.When)).ToList();
        list.Add(s with { Source = "project" });
        Save(solutionDir, list);
    }

    public static bool Remove(string solutionDir, string symbol)
    {
        var list = Project(solutionDir);
        int n = list.RemoveAll(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save(solutionDir, list);
        return n > 0;
    }

    static List<Signal> Project(string solutionDir) =>
        File.Exists(FileOf(solutionDir)) ? Parse(File.ReadAllText(FileOf(solutionDir)), "project") : new List<Signal>();

    static void Save(string solutionDir, List<Signal> list)
    {
        var arr = new JsonArray(list.Select(s =>
        {
            var o = new JsonObject { ["symbol"] = s.Symbol, ["when"] = s.When, ["text"] = s.Text };
            if (s.Details.Count > 0) o["details"] = new JsonArray(s.Details.Select(d => (JsonNode)d).ToArray());
            if (s.Port != 851) o["port"] = s.Port;
            return (JsonNode)o;
        }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(solutionDir))!);
        File.WriteAllText(FileOf(solutionDir), new JsonObject { ["signals"] = arr }.ToJsonString(Pretty) + "\n");
    }

    // ------------------------------------------------------------------ evaluation
    /// <summary>The last value and since when it has not changed, per signal - for "unchanged".</summary>
    public sealed class State
    {
        readonly Dictionary<string, (string value, DateTime since)> last = new();

        public bool Fires(Signal s, string value, DateTime now)
        {
            var key = $"{s.Port}:{s.Symbol}";
            if (!last.TryGetValue(key, out var l) || l.value != value) last[key] = l = (value, now);
            return Evaluate(s.When, value, now - l.since);
        }
    }

    /// <summary>Does the value meet the condition? unchanged uses how long the value has stayed the same.</summary>
    public static bool Evaluate(string when, string value, TimeSpan unchangedFor)
    {
        var w = (when ?? "").Trim().ToLowerInvariant();
        var v = (value ?? "").Trim();
        if (w == "true") return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        if (w == "false") return v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0";
        if (Unchanged.Match(w) is { Success: true } u) return unchangedFor.TotalSeconds >= int.Parse(u.Groups[1].Value, CultureInfo.InvariantCulture);
        var m = Compare.Match(w);
        if (!m.Success) return false;
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) v = "1"; else if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) v = "0";
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value switch
        {
            "==" => x == y, "!=" => x != y, ">" => x > y, ">=" => x >= y, "<" => x < y, "<=" => x <= y,
            "&" => ((long)x & (long)y) != 0, _ => false
        };
    }

    public static string Describe(Signal s) =>
        $"{s.Symbol} {s.When} - {s.Text}" + (s.Details.Count > 0 ? $" (details: {string.Join(", ", s.Details)})" : "") + (s.Port != 851 ? $" port {s.Port}" : "") +
        (s.Source != "project" ? $" [{s.Source}]" : "");
}
