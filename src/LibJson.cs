// Auswertung des Export-JSON einer Bibliothek: Gliederung und Suche nach Symbolen.
// Das Format ist undokumentiert; deshalb generisch: Objekte mit einem Namensfeld gelten als Symbole.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class LibJson
{
    static readonly string[] NameKeys = { "SignatureName", "Name", "name", "QualifiedName", "qualifiedName" };
    static readonly string[] KindKeys = { "POUType", "TreeItemType", "Kind", "kind", "Type", "type", "Returns" };

    static string NameOf(JsonObject o)
    {
        foreach (var k in NameKeys) if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        return null;
    }

    static string KindOf(JsonObject o)
    {
        foreach (var k in KindKeys) if (o[k] is JsonValue v) { var s = v.ToString(); if (s.Length > 0 && s.Length < 60) return k + "=" + s; }
        return null;
    }

    // Kompakte Gliederung: je Symbol eine Zeile (Name, Art, Anzahl Ein-/Ausgaenge) - Members nur ueber Find
    public static string Outline(string json, string filter = null, int maxLines = 600)
    {
        var root = JsonNode.Parse(json);
        var sb = new StringBuilder();
        int lines = 0, total = 0;
        var kinds = new SortedDictionary<string, int>();
        void Emit(JsonObject o, int depth)
        {
            var name = NameOf(o);
            total++;
            var kind = KindOf(o) ?? "";
            var k = kind.Contains('=') ? kind[(kind.IndexOf('=') + 1)..] : kind;
            kinds[k] = kinds.TryGetValue(k, out var c) ? c + 1 : 1;
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;
            if (lines >= maxLines) return;
            sb.Append(' ', depth * 2).Append(name);
            if (k.Length > 0) sb.Append("  [").Append(k).Append(']');
            var vars = (o["Variables"] ?? o["AllVariables"]) as JsonArray;
            if (vars != null)
            {
                var flags = vars.OfType<JsonObject>().GroupBy(v => v["Flag"]?.ToString() ?? v["Kind"]?.ToString() ?? "?")
                                .Where(g => g.Key is not ("Local" or "Temp")).Select(g => $"{g.Key}:{g.Count()}");
                var f = string.Join(" ", flags);
                if (f.Length > 0) sb.Append("  ").Append(f);
            }
            sb.AppendLine(); lines++;
        }
        void Walk(JsonNode n, int depth)
        {
            if (n is JsonArray a) { foreach (var x in a) Walk(x, depth); return; }
            if (n is not JsonObject o) return;
            if (NameOf(o) != null && depth > 0) { Emit(o, depth - 1); return; }   // Symbol: nicht weiter absteigen
            foreach (var kv in o)
                if (kv.Value is JsonObject || kv.Value is JsonArray) Walk(kv.Value, depth + 1);
        }
        Walk(root, 0);
        var head = $"{total} Symbole ({string.Join(", ", kinds.Select(kv => $"{kv.Key} {kv.Value}"))})" +
                   (string.IsNullOrEmpty(filter) ? "" : $", Filter '{filter}'") + "\n";
        if (lines >= maxLines) sb.AppendLine($"... (abgeschnitten nach {maxLines} Zeilen - mit filter oder symbol eingrenzen)");
        if (total == 0) sb.AppendLine("Keine Objekte mit Namensfeld erkannt. Anfang des JSON:\n" + (json.Length > 3000 ? json[..3000] + "..." : json));
        return head + sb;
    }

    public static string Find(string json, string symbol, int maxMatches = 5, int maxChars = 20000)
    {
        var root = JsonNode.Parse(json);
        var hits = new List<JsonObject>();
        void Walk(JsonNode n)
        {
            if (n == null || hits.Count >= maxMatches) return;
            if (n is JsonArray a) { foreach (var x in a) Walk(x); return; }
            if (n is not JsonObject o) return;
            var name = NameOf(o);
            if (name != null && (name.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                                 name.EndsWith("." + symbol, StringComparison.OrdinalIgnoreCase))) { hits.Add(o); return; }
            foreach (var kv in o) Walk(kv.Value);
        }
        Walk(root);
        if (hits.Count == 0) return $"Symbol '{symbol}' nicht gefunden.";
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sb = new StringBuilder($"{hits.Count} Treffer fuer '{symbol}':\n");
        foreach (var h in hits) sb.AppendLine(h.ToJsonString(opts));
        var s = sb.ToString();
        return s.Length > maxChars ? s[..maxChars] + "\n... (abgeschnitten)" : s;
    }
}
