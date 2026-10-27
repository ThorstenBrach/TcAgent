// Runtime data backup and restore: the values that make a machine "this machine" - persistent and retain variables,
// recipes and parameters - read via ADS into a JSON file and written back later (after a PLC exchange, a new software
// version, a second machine of the same kind).
//
//   what      every symbol flagged persistent (ADS), the RETAIN/PERSISTENT variables found in the code of the solution
//             (GVLs and programs by path, function blocks in every instance) and the include patterns of the call or of
//             .tcagent\backup.json {"include": ["GVL_Recipe.*"], "exclude": ["*.aTrace"]}; structures are stored per
//             element, arrays of simple types as one array. Pointers, references and interfaces are skipped.
//   file      {"tcagent", "created", "target", "port", "project", "values": {"<path>": {"type": "<IEC type>", "value": ...}}}
//             default %LOCALAPPDATA%\TcAgent\backups\runtime\<solution>\<target>_<port>-<date>.json
//   restore   only symbols that still exist with the same type; only values that differ are written; read-only symbols,
//             missing ones and type changes are reported, never forced. A restore is a runtime write: allowed targets
//             only (TCAGENT_ALLOW_TARGETS), confirmed by the user, and only with the PLC stopped unless whileRunning.
//
// The ADS part needs a running PLC; the selection, the file and the conversion are pure functions (unit tests).
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class RuntimeBackup
{
    // ------------------------------------------------------------------ selection
    /// <summary>RETAIN and PERSISTENT variables declared in the code: by path (GVL, program) and per function block type.</summary>
    public sealed class Declared
    {
        public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HashSet<string>> FbVars = new(StringComparer.OrdinalIgnoreCase);
        public int Count => Paths.Count + FbVars.Values.Sum(v => v.Count);
    }

    static readonly Regex Header = new(@"^\s*(?:\{[^}]*\}\s*)*(FUNCTION_BLOCK|PROGRAM)\s+(?:(?:PUBLIC|INTERNAL|ABSTRACT|FINAL)\s+)*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex Block = new(@"^\s*VAR(?:_GLOBAL)?\b([^\r\n]*)\r?\n(.*?)^\s*END_VAR", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
    static readonly Regex VarLine = new(@"^\s*([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*(?:AT\s+%\S+\s*)?:", RegexOptions.Multiline);

    /// <summary>Scans the declarations of a POU or GVL (the text of its declaration part). name: the GVL name for a GVL.</summary>
    public static void Scan(string declaration, string gvlName, Declared d)
    {
        var text = Regex.Replace(declaration, @"\(\*.*?\*\)", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", "");
        var h = Header.Match(text);
        string kind = h.Success ? h.Groups[1].Value.ToUpperInvariant() : "GVL", owner = h.Success ? h.Groups[2].Value : gvlName;
        if (owner == null) return;
        foreach (Match b in Block.Matches(text))
        {
            if (!Regex.IsMatch(b.Groups[1].Value, @"\b(RETAIN|PERSISTENT)\b", RegexOptions.IgnoreCase)) continue;
            foreach (Match v in VarLine.Matches(b.Groups[2].Value))
                foreach (var name in v.Groups[1].Value.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
                    if (kind == "FUNCTION_BLOCK") { if (!d.FbVars.TryGetValue(owner, out var set)) d.FbVars[owner] = set = new(StringComparer.OrdinalIgnoreCase); set.Add(name); }
                    else d.Paths.Add(owner + "." + name);
        }
    }

    /// <summary>All RETAIN/PERSISTENT declarations of the PLC projects below a solution folder (.TcPOU, .TcGVL files).</summary>
    public static Declared ScanSolution(string solutionDir)
    {
        var d = new Declared();
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return d;
        foreach (var f in Directory.EnumerateFiles(solutionDir, "*.*", SearchOption.AllDirectories)
                                   .Where(f => f.EndsWith(".TcPOU", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".TcGVL", StringComparison.OrdinalIgnoreCase))
                                   .Where(f => !f.Contains(@"\_Boot\") && !f.Contains(@"\.git\")))
            try
            {
                var x = File.ReadAllText(f);
                var decl = Regex.Match(x, @"<Declaration><!\[CDATA\[(.*?)\]\]></Declaration>", RegexOptions.Singleline);
                if (!decl.Success) continue;
                var gvl = f.EndsWith(".TcGVL", StringComparison.OrdinalIgnoreCase)
                          ? Regex.Match(x, @"<GVL\s+Name=""([^""]+)""").Groups[1].Value is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(f) : null;
                Scan(decl.Groups[1].Value, gvl, d);
            }
            catch { }
        return d;
    }

    /// <summary>A pattern: a path (with its elements) or a wildcard pattern with * and ?.</summary>
    public static bool Matches(string path, string pattern)
    {
        pattern = (pattern ?? "").Trim();
        if (pattern.Length == 0) return false;
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            return path.Equals(pattern, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(pattern + "[", StringComparison.OrdinalIgnoreCase);
        return Regex.IsMatch(path, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>include/exclude of .tcagent\backup.json of the solution.</summary>
    public static (List<string> Include, List<string> Exclude) Config(string solutionDir)
    {
        var inc = new List<string>(); var exc = new List<string>();
        try
        {
            var f = solutionDir == null ? null : Path.Combine(solutionDir, ".tcagent", "backup.json");
            if (f != null && File.Exists(f) && JsonNode.Parse(File.ReadAllText(f)) is JsonObject o)
            {
                foreach (var v in o["include"]?.AsArray() ?? new JsonArray()) if (v != null) inc.Add(v.ToString());
                foreach (var v in o["exclude"]?.AsArray() ?? new JsonArray()) if (v != null) exc.Add(v.ToString());
            }
        }
        catch (Exception ex) { throw new InvalidOperationException(".tcagent\\backup.json: " + ex.Message); }
        return (inc, exc);
    }

    // ------------------------------------------------------------------ values
    /// <summary>A value read via ADS as JSON: bool and numbers as they are, everything else (time, date, enum, string) as text.</summary>
    public static JsonNode ToJson(object v) => v switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        sbyte or byte or short or ushort or int or uint or long or ulong => JsonValue.Create(Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
        float f => JsonValue.Create((double)f),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        Array a => new JsonArray(a.Cast<object>().Select(ToJson).ToArray()),
        IFormattable fm => JsonValue.Create(fm.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(v.ToString())
    };

    /// <summary>The stored value converted to the .NET type of the current value (from the symbol) - for writing.</summary>
    public static object FromJson(JsonNode n, object current)
    {
        if (current is Array cur)
        {
            var arr = n as JsonArray ?? throw new FormatException("an array was expected");
            if (arr.Count != cur.Length) throw new FormatException($"the array has {cur.Length} elements now, {arr.Count} in the backup");
            var res = Array.CreateInstance(cur.GetType().GetElementType()!, cur.Length);
            for (int i = 0; i < cur.Length; i++) res.SetValue(FromJson(arr[i], cur.GetValue(i)), i);
            return res;
        }
        var text = n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
        return current switch
        {
            bool => text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1",
            string => text,
            TimeSpan => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
            DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Enum e => Enum.Parse(e.GetType(), text, true),
            null => throw new FormatException("the current value is unknown"),
            float or double => Convert.ChangeType(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), current.GetType(), CultureInfo.InvariantCulture),
            _ when current.GetType().IsPrimitive || current is decimal =>
                Convert.ChangeType(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), current.GetType(), CultureInfo.InvariantCulture),
            _ => Convert.ChangeType(text, current.GetType(), CultureInfo.InvariantCulture)
        };
    }

    public static bool SameValue(object a, object b) => JsonNode.DeepEquals(ToJson(a), ToJson(b));

    // ------------------------------------------------------------------ file
    public static string DefaultFolder(string solutionDir) =>
        Path.Combine(KnowledgeLayers.UserRoot, "backups", "runtime", solutionDir == null ? "no-solution" : Path.GetFileName(solutionDir.TrimEnd('\\', '/')));

    public static string FileName(string target, int port) => $"{(string.IsNullOrEmpty(target) ? "local" : target)}_{port}-{DateTime.Now:yyyyMMdd-HHmmss}.json";

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Save(string file, JsonObject backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, backup.ToJsonString(Pretty), new UTF8Encoding(false));
    }

    public static JsonObject Load(string file)
    {
        var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidOperationException("not a TcAgent runtime backup: " + file);
        if (o["values"] is not JsonObject) throw new InvalidOperationException("not a TcAgent runtime backup (no values): " + file);
        return o;
    }

    /// <summary>The backups in a folder, newest first: file, target, date, number of values.</summary>
    public static List<(string File, string Target, string Created, int Values)> List(string folder)
    {
        var l = new List<(string, string, string, int)>();
        if (!Directory.Exists(folder)) return l;
        foreach (var f in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime))
            try { var o = Load(f); l.Add((f, o["target"]?.ToString(), o["created"]?.ToString(), (o["values"] as JsonObject)!.Count)); } catch { }
        return l;
    }
}
