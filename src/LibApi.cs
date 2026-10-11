// Kompaktes Schnittstellenmodell einer Bibliothek aus dem Projektmodell der IDE:
// Originalschreibweise, EXTENDS, geerbte Ein-/Ausgaenge, Methoden je FB, Struct-Felder, Enum-Werte, Doku-Kommentare.
// Ausgabe als ST-Deklarationen (wie Header-Dateien), damit Agenten keine APIs erfinden.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed class LibApi
{
    public sealed record Var(string Name, string Type, string Flags, string Doc);
    public sealed record Sym(int Id, int ParentId, string Name, string Kind, string Extends, string Doc, List<Var> Vars, string LibraryId);

    public string Library { get; init; }
    public List<Sym> Symbols { get; init; } = new();

    // ------------------------------------------------------------------ Aufbau aus dem Roh-Export der Bruecke
    public static LibApi FromLm(string rawJson, string library)
    {
        var root = JsonNode.Parse(rawJson)!.AsObject();
        var api = new LibApi { Library = library };
        foreach (var n in root["Signatures"]!.AsArray())
        {
            if (n is not JsonObject s) continue;
            string S(string k) => s[k] is JsonValue v ? v.ToString() : null;
            var vars = new List<Var>();
            if (s["AllVariables"] is JsonArray av)
                foreach (var x in av.OfType<JsonObject>())
                {
                    string V(string k) => x[k]?.ToString();
                    vars.Add(new Var(V("OrgName") ?? V("Name"), V("Type") ?? V("OriginalType"), V("Flags") ?? "", Clean(V("DocuComment"))));
                }
            api.Symbols.Add(new Sym(int.TryParse(S("PrecompileId"), out var id) ? id : -1,
                                    int.TryParse(S("PrecompileParentId"), out var pid) ? pid : -1,
                                    S("OrgName") ?? S("Name"), S("POUType") ?? "", S("BaseSignature"), Clean(S("DocuComment")), vars, S("LibraryId")));
        }
        return api;
    }

    static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));
    public static LibApi Load(string path) => JsonSerializer.Deserialize<LibApi>(File.ReadAllText(path));

    // ------------------------------------------------------------------ Abfragen
    Sym Parent(Sym s) => s.ParentId >= 0 ? Symbols.FirstOrDefault(x => x.Id == s.ParentId) : null;
    Sym ByName(string n) => Symbols.FirstOrDefault(x => x.ParentId < 0 && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
    IEnumerable<Sym> Methods(Sym s) => Symbols.Where(x => x.ParentId == s.Id && s.Id >= 0);

    static bool Is(Var v, string flag) => v.Flags.Split(',').Any(f => f.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase));

    public string Outline(string filter, int maxLines = 600)
    {
        var top = Symbols.Where(s => s.ParentId < 0).ToList();
        var kinds = string.Join(", ", top.GroupBy(s => s.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        var sb = new StringBuilder($"{top.Count} Symbole ({kinds}), dazu {Symbols.Count - top.Count} Methoden/Properties{(string.IsNullOrEmpty(filter) ? "" : $", Filter '{filter}'")}\n");
        int lines = 0;
        foreach (var s in top)
        {
            if (!string.IsNullOrEmpty(filter) && s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++lines > maxLines) { sb.AppendLine($"... (abgeschnitten nach {maxLines} Zeilen - mit filter eingrenzen)"); break; }
            sb.Append(s.Name).Append("  [").Append(s.Kind).Append(']');
            if (!string.IsNullOrEmpty(s.Extends)) sb.Append(" EXTENDS ").Append(s.Extends);
            int i = s.Vars.Count(v => Is(v, "Input")), o = s.Vars.Count(v => Is(v, "Output")), io = s.Vars.Count(v => Is(v, "Inout")), m = Methods(s).Count();
            if (s.Kind is "FunctionBlock" or "Function" or "Program") sb.Append($"  in:{i} out:{o}{(io > 0 ? $" inout:{io}" : "")}{(m > 0 ? $" methoden:{m}" : "")}");
            if (s.Doc != null) sb.Append("  // ").Append(s.Doc.Length > 100 ? s.Doc[..100] + "..." : s.Doc);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Schnittstelle eines Symbols als ST-Deklaration. "FB.Methode" waehlt eine Methode.</summary>
    public string Describe(string symbol)
    {
        Sym s;
        var dot = symbol.IndexOf('.');
        if (dot > 0)
        {
            var owner = ByName(symbol[..dot]);
            s = owner == null ? null : Methods(owner).FirstOrDefault(x => string.Equals(x.Name, symbol[(dot + 1)..], StringComparison.OrdinalIgnoreCase));
            if (s == null && owner != null && Methods(owner).Any(x => string.Equals(PropName(x), symbol[(dot + 1)..], StringComparison.OrdinalIgnoreCase)))
                return $"// Property von {owner.Name}\n" + PropMember(owner, symbol[(dot + 1)..]).Declaration;
        }
        else s = ByName(symbol) ?? Symbols.FirstOrDefault(x => string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));
        if (s == null)
        {
            var near = Symbols.Where(x => x.Name.Contains(symbol, StringComparison.OrdinalIgnoreCase)).Take(15).Select(x => (Parent(x) is { } p ? p.Name + "." : "") + x.Name);
            return $"Symbol '{symbol}' nicht gefunden. Aehnlich: {string.Join(", ", near)}";
        }
        var sb = new StringBuilder();
        if (Parent(s) is { } par) sb.AppendLine($"// Methode/Property von {par.Name}");
        if (s.Doc != null) sb.AppendLine("/// " + s.Doc);
        switch (s.Kind)
        {
            case "FunctionBlock":
            case "Program":
            case "Interface":
                sb.AppendLine($"{Keyword(s.Kind)} {s.Name}{(string.IsNullOrEmpty(s.Extends) ? "" : " EXTENDS " + s.Extends)}");
                Section(sb, "VAR_INPUT", s.Vars.Where(v => Is(v, "Input")));
                Section(sb, "VAR_OUTPUT", s.Vars.Where(v => Is(v, "Output")));
                Section(sb, "VAR_IN_OUT", s.Vars.Where(v => Is(v, "Inout")));
                // Geerbte Schnittstelle entlang der EXTENDS-Kette
                var b = s; var seen = new HashSet<string>();
                while (!string.IsNullOrEmpty(b.Extends) && seen.Add(b.Extends) && ByName(b.Extends) is { } bs)
                {
                    var inh = bs.Vars.Where(v => Is(v, "Input") || Is(v, "Output") || Is(v, "Inout")).ToList();
                    if (inh.Count > 0)
                    {
                        sb.AppendLine($"// geerbt von {bs.Name}:");
                        Section(sb, "VAR_INPUT", inh.Where(v => Is(v, "Input")));
                        Section(sb, "VAR_OUTPUT", inh.Where(v => Is(v, "Output")));
                        Section(sb, "VAR_IN_OUT", inh.Where(v => Is(v, "Inout")));
                    }
                    b = bs;
                }
                var locals = s.Vars.Count(v => Is(v, "Local"));
                if (locals > 0) sb.AppendLine($"// {locals} interne Variablen (nicht Teil der Schnittstelle)");
                var ms = Methods(s).ToList();
                b = s; seen.Clear();
                while (!string.IsNullOrEmpty(b.Extends) && seen.Add(b.Extends) && ByName(b.Extends) is { } bs2)
                {
                    foreach (var im in Methods(bs2)) if (!ms.Any(x => x.Name.Equals(im.Name, StringComparison.OrdinalIgnoreCase))) ms.Add(im);
                    b = bs2;
                }
                if (ms.Count > 0)
                {
                    sb.AppendLine("// Methoden/Properties:");
                    foreach (var m in ms) sb.AppendLine("//   " + MethodLine(m) + (Parent(m) != s ? $"   (von {Parent(m)?.Name})" : ""));
                }
                sb.AppendLine($"END_{Keyword(s.Kind)}");
                if (string.Equals(s.Kind, "Interface", StringComparison.OrdinalIgnoreCase) && InterfaceMembers(s.Name) is { Count: > 0 } im2)
                {
                    sb.AppendLine("\n// Vollstaendige Mitglieder (tc_plc_create kind=function_block implements=" + s.Name + " legt sie als Stubs an):");
                    foreach (var mm in im2) sb.AppendLine(mm.Declaration).AppendLine();
                }
                break;
            case "Function":
            case "Method":
                var ret = s.Vars.FirstOrDefault(v => Is(v, "Output") && v.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"{(s.Kind == "Method" ? "METHOD" : "FUNCTION")} {s.Name}{(ret != null ? " : " + ret.Type : "")}");
                Section(sb, "VAR_INPUT", s.Vars.Where(v => Is(v, "Input")));
                Section(sb, "VAR_OUTPUT", s.Vars.Where(v => Is(v, "Output") && v != ret));
                Section(sb, "VAR_IN_OUT", s.Vars.Where(v => Is(v, "Inout")));
                break;
            case string pk when pk.IndexOf("prop", StringComparison.OrdinalIgnoreCase) >= 0:
                sb.AppendLine(ToMember(s).Declaration);
                break;
            case "Type":
                var members = s.Vars.ToList();
                if (members.Any(v => Is(v, "Enum")))
                {
                    sb.AppendLine($"TYPE {s.Name} :\n(");
                    sb.AppendLine(string.Join(",\n", members.Where(v => Is(v, "Enum")).Select(v => "  " + v.Name + (v.Doc != null ? "  // " + v.Doc : ""))));
                    sb.AppendLine(");\nEND_TYPE");
                }
                else if (members.Any(v => Is(v, "Alias")) || members.Count == 1 && string.IsNullOrEmpty(members[0].Name))
                    sb.AppendLine($"TYPE {s.Name} : {members[0].Type};\nEND_TYPE");
                else
                {
                    sb.AppendLine($"TYPE {s.Name}{(string.IsNullOrEmpty(s.Extends) ? "" : " EXTENDS " + s.Extends)} :\nSTRUCT");
                    foreach (var v in members) sb.AppendLine(VarLine(v));
                    sb.AppendLine("END_STRUCT\nEND_TYPE");
                }
                break;
            default:   // VarGlobal u. a. - Enums erscheinen ebenfalls als VarGlobal
                if (s.Vars.Count > 0 && s.Vars.All(v => Is(v, "Enum")))
                {
                    sb.AppendLine($"TYPE {s.Name} :\n(");
                    sb.AppendLine(string.Join(",\n", s.Vars.Select(v => "  " + v.Name + (v.Doc != null ? "  // " + v.Doc : ""))));
                    sb.AppendLine(");\nEND_TYPE");
                    break;
                }
                sb.AppendLine($"// {s.Kind} {s.Name}");
                Section(sb, "VAR_GLOBAL", s.Vars);
                break;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Interface-Mitglieder (fuer tc_plc_create implements=...)
    public sealed record Member(string Name, bool IsProperty, string ReturnType, string Declaration);

    static bool IsProp(Sym m) => m.Kind != null && m.Kind.IndexOf("prop", StringComparison.OrdinalIgnoreCase) >= 0;
    static Var RetVar(Sym m) => m.Vars.FirstOrDefault(v => Is(v, "Output") && v.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Alle Methoden/Properties eines Interfaces inkl. EXTENDS-Kette; null, wenn es hier kein Interface dieses Namens gibt.</summary>
    public List<Member> InterfaceMembers(string iface)
    {
        var s = ByName(iface);
        if (s == null || !string.Equals(s.Kind, "Interface", StringComparison.OrdinalIgnoreCase)) return null;
        var list = new List<Member>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Sym>(); queue.Enqueue(s); var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            if (!done.Add(b.Name)) continue;
            foreach (var m in Methods(b))
            {
                // Properties erscheinen als Methoden __getX / __setX
                var pn = PropName(m);
                if (pn != null) { if (seen.Add(pn)) list.Add(PropMember(b, pn)); continue; }
                if (!m.Name.StartsWith("__") && seen.Add(m.Name)) list.Add(ToMember(m));
            }
            foreach (var e in (b.Extends ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                if (ByName(e) is { } es) queue.Enqueue(es);
        }
        return list;
    }

    static string PropName(Sym m) =>
        m.Name.Length > 5 && (m.Name.StartsWith("__get", StringComparison.OrdinalIgnoreCase) || m.Name.StartsWith("__set", StringComparison.OrdinalIgnoreCase))
            ? m.Name.Substring(5) : null;

    Member PropMember(Sym owner, string name)
    {
        var get = Methods(owner).FirstOrDefault(x => x.Name.Equals("__get" + name, StringComparison.OrdinalIgnoreCase));
        var set = Methods(owner).FirstOrDefault(x => x.Name.Equals("__set" + name, StringComparison.OrdinalIgnoreCase));
        var type = (get != null ? RetVar(get)?.Type : null)
                   ?? set?.Vars.FirstOrDefault(v => Is(v, "Input"))?.Type ?? "BOOL";
        var doc = get?.Doc ?? set?.Doc;
        var acc = get != null && set != null ? "Get/Set" : get != null ? "nur Get" : "nur Set";
        return new Member(name, true, type, (doc != null ? "/// " + doc + "\n" : "") + $"PROPERTY {name} : {type}   // {acc}");
    }

    Member ToMember(Sym m)
    {
        var ret = RetVar(m);
        if (IsProp(m)) return new Member(m.Name, true, ret?.Type ?? m.Vars.FirstOrDefault()?.Type ?? "BOOL", $"PROPERTY {m.Name} : {ret?.Type ?? m.Vars.FirstOrDefault()?.Type ?? "BOOL"}");
        var sb = new StringBuilder();
        if (m.Doc != null) sb.AppendLine("/// " + m.Doc);
        sb.AppendLine($"METHOD {m.Name}{(ret != null ? " : " + ret.Type : "")}");
        Section(sb, "VAR_INPUT", m.Vars.Where(v => Is(v, "Input")));
        Section(sb, "VAR_OUTPUT", m.Vars.Where(v => Is(v, "Output") && v != ret));
        Section(sb, "VAR_IN_OUT", m.Vars.Where(v => Is(v, "Inout")));
        return new Member(m.Name, false, ret?.Type, sb.ToString().TrimEnd());
    }

    static string Keyword(string kind) => kind switch { "Program" => "PROGRAM", "Interface" => "INTERFACE", _ => "FUNCTION_BLOCK" };
    static string VarLine(Var v) => $"  {v.Name} : {v.Type};{(v.Doc != null ? "  // " + v.Doc : "")}";

    static void Section(StringBuilder sb, string kw, IEnumerable<Var> vars)
    {
        var l = vars.ToList();
        if (l.Count == 0) return;
        sb.AppendLine(kw);
        foreach (var v in l) sb.AppendLine(VarLine(v));
        sb.AppendLine("END_VAR");
    }

    string MethodLine(Sym m)
    {
        var ret = m.Vars.FirstOrDefault(v => Is(v, "Output") && v.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
        var ins = m.Vars.Where(v => Is(v, "Input") || Is(v, "Inout")).Select(v => $"{v.Name} : {v.Type}");
        return $"{m.Name}({string.Join(", ", ins)}){(ret != null ? " : " + ret.Type : "")}";
    }
}
