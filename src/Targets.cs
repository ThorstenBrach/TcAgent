// Target systems a person released in the TcAgent settings window (or with TcAgent.exe targets): %LOCALAPPDATA%\TcAgent\targets.json.
// Each entry has a name, an AmsNetId, whether the agent may write to it (activate, PLC start/stop, ADS write ...) or only read,
// and optionally a time until which the release holds. Reading is free for every target (decision 09.11.); a release that ran out
// counts as "read only" again without a restart - the policy reads the file on every runtime call. The list applies to every
// project on this PC (global). TCAGENT_ALLOW_TARGETS of the environment still applies in addition (pipelines, MCP configurations).
//
// Only a person changes the list: there is no agent tool that writes it. Every change is written to the audit log.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed class TargetEntry
{
    public string Name = "", NetId = "";
    public bool Write;
    /// <summary>Local time until which the release holds; null = for good.</summary>
    public DateTime? Until;
    /// <summary>When and how it was released ("settings window", "command line").</summary>
    public DateTime Added = DateTime.Now;
    public string By = "";

    public bool Expired(DateTime now) => Until is { } u && u <= now;
    /// <summary>May the agent write to it now?</summary>
    public bool WritableAt(DateTime now) => Write && !Expired(now);

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["name"] = Name, ["netId"] = NetId, ["access"] = Write ? "write" : "read" };
        if (Until is { } u) o["until"] = u.ToString("s");
        o["added"] = Added.ToString("s");
        if (By.Length > 0) o["by"] = By;
        return o;
    }

    public static TargetEntry FromJson(JsonObject o) => new()
    {
        Name = o["name"]?.ToString() ?? "",
        NetId = (o["netId"]?.ToString() ?? "").Trim(),
        Write = !string.Equals(o["access"]?.ToString(), "read", StringComparison.OrdinalIgnoreCase),
        Until = DateTime.TryParse(o["until"]?.ToString(), out var u) ? u : null,
        Added = DateTime.TryParse(o["added"]?.ToString(), out var a) ? a : DateTime.MinValue,
        By = o["by"]?.ToString() ?? ""
    };
}

public static class Targets
{
    /// <summary>Test hook: another file instead of %LOCALAPPDATA%\TcAgent\targets.json.</summary>
    public static string FileOverride;
    public static string FilePath => FileOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "targets.json");

    /// <summary>The released targets; an invalid or missing file gives none (and never more rights).</summary>
    public static List<TargetEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<TargetEntry>();
            if (JsonNode.Parse(File.ReadAllText(FilePath))?["targets"] is not JsonArray arr) return new List<TargetEntry>();
            return arr.OfType<JsonObject>().Select(TargetEntry.FromJson).Where(t => IsNetId(t.NetId)).ToList();
        }
        catch (Exception ex) { Log.Error("targets.json: " + ex.Message); return new List<TargetEntry>(); }
    }

    public static void Save(IEnumerable<TargetEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var root = new JsonObject { ["targets"] = new JsonArray(list.Select(t => (JsonNode)t.ToJson()).ToArray()) };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Six numbers 0..255 separated by dots.</summary>
    public static bool IsNetId(string s) =>
        !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), @"^\d{1,3}(\.\d{1,3}){5}$") && s.Trim().Split('.').All(p => int.Parse(p) <= 255);

    /// <summary>Is writing to this target released in targets.json right now?</summary>
    public static bool WriteAllowed(string netId, DateTime? now = null) =>
        Load().Any(t => t.NetId.Equals((netId ?? "").Trim(), StringComparison.OrdinalIgnoreCase) && t.WritableAt(now ?? DateTime.Now));

    /// <summary>Adds or replaces the entry with this AmsNetId. Returns the entry.</summary>
    public static TargetEntry Set(string netId, string name, bool write, DateTime? until, string by)
    {
        if (!IsNetId(netId)) throw new ArgumentException($"'{netId}' is not an AMS Net ID - six numbers 0..255, e.g. 5.80.123.4.1.1");
        if (until is { } u && u <= DateTime.Now) throw new ArgumentException("the time 'until' is already over");
        var list = Load();
        list.RemoveAll(t => t.NetId.Equals(netId.Trim(), StringComparison.OrdinalIgnoreCase));
        var e = new TargetEntry { NetId = netId.Trim(), Name = (name ?? "").Trim(), Write = write, Until = until, Added = DateTime.Now, By = by ?? "" };
        list.Add(e);
        Save(list);
        return e;
    }

    /// <summary>Removes the entry; false if there was none.</summary>
    public static bool Remove(string netId)
    {
        var list = Load();
        var n = list.RemoveAll(t => t.NetId.Equals((netId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save(list);
        return n > 0;
    }

    /// <summary>"1h", "30m", "2d", "today", "2026-11-09T20:00", "" / "forever" → the end of the release.</summary>
    public static DateTime? ParseUntil(string s, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s is "" or "forever" or "dauerhaft" or "permanent") return null;
        if (s is "today" or "heute") return n.Date.AddDays(1).AddSeconds(-1);
        var m = Regex.Match(s, @"^(\d+)\s*(m|min|h|d)$");
        if (m.Success)
        {
            var v = int.Parse(m.Groups[1].Value);
            return m.Groups[2].Value switch { "h" => n.AddHours(v), "d" => n.AddDays(v), _ => n.AddMinutes(v) };
        }
        if (DateTime.TryParse(s, out var d)) return d;
        throw new ArgumentException($"until: '{s}' - use 30m, 1h, 2d, today, a date/time or forever");
    }

    /// <summary>The user mode runtimes installed on this PC (TwinCAT 3.1.4026+): each has its own AmsNetId in
    /// %ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\&lt;name&gt;\3.1\TcRegistry.xml.</summary>
    public static List<(string Name, string NetId)> LocalUserModeRuntimes(string runtimesDir = null)
    {
        var found = new List<(string, string)>();
        var dir = runtimesDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Beckhoff", "TwinCAT", "3.1", "Runtimes");
        try
        {
            if (!Directory.Exists(dir)) return found;
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var reg = Path.Combine(d, "3.1", "TcRegistry.xml");
                if (!File.Exists(reg)) continue;
                var m = Regex.Match(File.ReadAllText(reg), @"<Value\s+Name=""AmsNetId""\s+Type=""BIN"">\s*([0-9A-Fa-f]{12})\s*</Value>");
                if (!m.Success) continue;
                var hex = m.Groups[1].Value;
                found.Add((Path.GetFileName(d), string.Join(".", Enumerable.Range(0, 6).Select(i => Convert.ToInt32(hex.Substring(2 * i, 2), 16)))));
            }
        }
        catch (Exception ex) { Log.Error("user mode runtimes: " + ex.Message); }
        return found;
    }

    /// <summary>One line per entry for tc_policy / the command line.</summary>
    public static string Describe(TargetEntry t, DateTime now) =>
        $"{(string.IsNullOrEmpty(t.Name) ? "(no name)" : t.Name)} {t.NetId}: " +
        (t.Expired(now) ? $"expired {t.Until:dd.MM. HH:mm} - read only" : (t.Write ? "write" : "read only") + (t.Until is { } u ? $" until {u:dd.MM. HH:mm}" : ""));
}
