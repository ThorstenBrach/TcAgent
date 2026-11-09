// Where a variable of a condition really comes from - for reading it online (Conditions window, tc_condition online).
// The name in the code is often not the ADS path: a variable can be inherited (EXTENDS), reached through a reference, a pointer
// or an interface, be THIS^ / SUPER^, a local of a method (on the stack - not readable), a global of a GVL, or a property that
// is only readable with {attribute 'monitoring' := 'call'}. From the declarations in the project files (no IDE) each operand of
// a sub-condition gets: the ADS path, what must be checked first (a pointer not 0, the value of an array index), where it comes
// from in words - or why it cannot be read. Nothing is guessed: an operand that cannot be resolved stays "not readable".
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed class VarDecl
{
    public string Name, Type, Section;     // Section: VAR, VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR_TEMP, VAR_INST, VAR_STAT, VAR_GLOBAL
}

public sealed class PouDecl
{
    public string Name, Kind, Extends;                 // Kind: FUNCTION_BLOCK, PROGRAM, FUNCTION, INTERFACE, STRUCT, ENUM, GVL
    public readonly List<string> Implements = new();
    public readonly Dictionary<string, VarDecl> Vars = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, PouDecl> Methods = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> Properties = new(StringComparer.OrdinalIgnoreCase);   // name → readable online (monitoring attribute)
    public bool QualifiedOnly;
}

public sealed class ProjectIndex
{
    public readonly Dictionary<string, PouDecl> Types = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<PouDecl> Gvls = new();
}

public sealed class ResolvedOperand
{
    public string Text;                        // as written: pSensor^.rValue
    public string Path;                        // ADS path, null when it cannot be read
    public string From = "", FromDe = "";      // in words: "inherited from FB_Base", "through pointer pSensor"
    public string Reason, ReasonDe;            // why it cannot be read
    public bool Constant;                      // a value of a type (enum value, constant): not read, used as written
    public readonly List<string> PointerChecks = new();          // paths of pointers that must not be 0
    public readonly List<(string Var, string Path)> Indexes = new();   // array indexes read first: {0} in Path
}

public static class SymbolResolve
{
    // ------------------------------------------------------------------ the project
    public static ProjectIndex Index(string solutionDir)
    {
        var idx = new ProjectIndex();
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return idx;
        foreach (var f in Directory.EnumerateFiles(solutionDir, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                     .Where(x => x.EndsWith(".TcPOU", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".TcGVL", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".TcDUT", StringComparison.OrdinalIgnoreCase))
                     .Where(x => !x.Contains(@"\_Boot\") && !x.Contains(@"\_CompileInfo\")))
        {
            try { AddFile(idx, File.ReadAllText(f)); } catch { }
        }
        return idx;
    }

    static readonly Regex Cdata = new(@"<Declaration><!\[CDATA\[(.*?)\]\]></Declaration>", RegexOptions.Singleline);

    /// <summary>One TcPOU / TcGVL / TcDUT: the POU with its methods and properties, a GVL or a type.</summary>
    public static void AddFile(ProjectIndex idx, string xml)
    {
        var top = Cdata.Match(xml);
        if (!top.Success) return;
        var main = ParseDecl(top.Groups[1].Value);
        if (main == null) return;
        if (xml.Contains("<GVL ")) { main.Kind = "GVL"; main.Name = Regex.Match(xml, @"<GVL\s+Name=""([^""]+)""").Groups[1].Value; idx.Gvls.Add(main); return; }
        // methods and properties of a POU
        foreach (Match m in Regex.Matches(xml, @"<Method\s+Name=""([^""]+)""[^>]*>\s*<Declaration><!\[CDATA\[(.*?)\]\]></Declaration>", RegexOptions.Singleline))
            if (ParseDecl(m.Groups[2].Value) is { } md) main.Methods[m.Groups[1].Value] = md;
        foreach (Match p in Regex.Matches(xml, @"<Property\s+Name=""([^""]+)""[^>]*>\s*<Declaration><!\[CDATA\[(.*?)\]\]></Declaration>", RegexOptions.Singleline))
            main.Properties[p.Groups[1].Value] = Regex.IsMatch(p.Groups[2].Value, @"\{attribute\s+'monitoring'\s*:=\s*'(call|variable)'\s*\}", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(main.Name)) idx.Types[main.Name] = main;
    }

    /// <summary>A declaration: its header (kind, name, EXTENDS, IMPLEMENTS) and its variables per section.</summary>
    public static PouDecl ParseDecl(string text)
    {
        var t = StLexer.Lex(text).Where(x => !x.Trivia && !x.Comment).ToList();
        var d = new PouDecl();
        if (Regex.IsMatch(text, @"\{attribute\s+'qualified_only'\s*\}", RegexOptions.IgnoreCase)) d.QualifiedOnly = true;
        int i = 0;
        while (i < t.Count && t[i].Kind == StTok.Pragma) i++;
        // header
        for (; i < t.Count; i++)
        {
            var w = t[i];
            if (w.Kind != StTok.Word) continue;
            if (w.Upper is "FUNCTION_BLOCK" or "PROGRAM" or "FUNCTION" or "INTERFACE" or "METHOD" or "PROPERTY")
            {
                d.Kind = w.Upper;
                int k = i + 1;
                while (k < t.Count && t[k].Kind == StTok.Word && t[k].Upper is "PUBLIC" or "PRIVATE" or "PROTECTED" or "INTERNAL" or "ABSTRACT" or "FINAL") k++;
                if (k < t.Count) d.Name = t[k].Text;
                for (int j = k + 1; j < t.Count && !(t[j].Kind == StTok.Word && t[j].Upper.StartsWith("VAR")); j++)
                {
                    if (t[j].Upper == "EXTENDS" && j + 1 < t.Count) d.Extends = Dotted(t, j + 1);
                    if (t[j].Upper == "IMPLEMENTS") for (int q = j + 1; q < t.Count && (t[q].Kind == StTok.Word || t[q].Text is "," or "."); q++) if (t[q].Kind == StTok.Word && !(q > 0 && t[q - 1].Text == ".")) d.Implements.Add(Dotted(t, q));
                }
                break;
            }
            if (w.Upper == "TYPE")
            {
                d.Name = i + 1 < t.Count ? t[i + 1].Text : null;
                d.Kind = t.Skip(i).Any(x => x.Upper is "STRUCT" or "UNION") ? "STRUCT" : "ENUM";
                int ext = t.FindIndex(i, x => x.Upper == "EXTENDS");
                if (ext > 0 && ext + 1 < t.Count) d.Extends = Dotted(t, ext + 1);
                break;
            }
            if (w.Upper.StartsWith("VAR")) break;
        }
        // variables
        string section = null;
        for (int k = 0; k < t.Count; k++)
        {
            var w = t[k];
            if (w.Kind == StTok.Word && w.Upper.StartsWith("VAR") && w.Upper != "VAR_CONFIG") { section = w.Upper; continue; }
            if (w.Kind == StTok.Word && w.Upper is "END_VAR" or "END_STRUCT" or "END_UNION") { section = w.Upper == "END_VAR" ? null : section; continue; }
            if (w.Kind == StTok.Word && w.Upper is "STRUCT" or "UNION") { section = "VAR"; continue; }
            if (section == null || w.Kind != StTok.Word || StKeywords.IsKeyword(w.Text)) continue;
            // names : type [:= init] ;
            var names = new List<string> { w.Text };
            int j = k + 1;
            while (j + 1 < t.Count && t[j].Text == "," && t[j + 1].Kind == StTok.Word) { names.Add(t[j + 1].Text); j += 2; }
            while (j < t.Count && t[j].Kind == StTok.Word && t[j].Upper is "AT") { j++; while (j < t.Count && t[j].Text != ":") j++; }
            if (j >= t.Count || t[j].Text != ":") continue;
            int end = j + 1, depth = 0;
            var type = new List<string>();
            for (; end < t.Count; end++)
            {
                var x = t[end];
                if (x.Text is "(" or "[") depth++; else if (x.Text is ")" or "]") depth--;
                if (depth == 0 && (x.Text == ";" || x.Kind == StTok.Op && x.Text == ":=")) break;
                type.Add(x.Kind == StTok.Word && StKeywords.IsCaseFree(x.Text) ? x.Upper : x.Text);
            }
            var ty = string.Join(" ", type).Replace(" .", ".").Replace(". ", ".").Replace("[ ", "[").Replace(" ]", "]").Replace(" ..", "..").Replace(".. ", "..");
            foreach (var n in names) d.Vars[n] = new VarDecl { Name = n, Type = ty, Section = section };
            while (end < t.Count && t[end].Text != ";") end++;
            k = end;
        }
        return d;
    }

    static string Dotted(List<StToken> t, int i)
    {
        var s = t[i].Text;
        while (i + 2 < t.Count && t[i + 1].Text == "." && t[i + 2].Kind == StTok.Word) { s += "." + t[i + 2].Text; i += 2; }
        return s;
    }

    static string Bare(string type) => type?.Split('.').Last();

    static PouDecl Type(ProjectIndex idx, string name) => name != null && idx.Types.TryGetValue(Bare(name), out var p) ? p : null;

    /// <summary>The variable along the EXTENDS chain; where = the type that declares it.</summary>
    static VarDecl Member(ProjectIndex idx, PouDecl pou, string name, out PouDecl where)
    {
        for (var p = pou; p != null; p = Type(idx, p.Extends))
            if (p.Vars.TryGetValue(name, out var v)) { where = p; return v; }
        where = null;
        return null;
    }

    /// <summary>The first type of the EXTENDS chain that is not in the project (a library), or null.</summary>
    static string LibraryBase(ProjectIndex idx, PouDecl pou)
    {
        for (var p = pou; p != null; p = Type(idx, p.Extends))
            if (!string.IsNullOrEmpty(p.Extends) && Type(idx, p.Extends) == null) return p.Extends;
        return null;
    }

    static bool? Property(ProjectIndex idx, PouDecl pou, string name)
    {
        for (var p = pou; p != null; p = Type(idx, p.Extends))
            if (p.Properties.TryGetValue(name, out var mon)) return mon;
        return null;
    }

    // ------------------------------------------------------------------ the instances of a POU
    /// <summary>Where the POU is used: the instance paths from the PROGRAMs and GVLs down through FB members (also arrays: [first]).
    /// A PROGRAM is its own instance.</summary>
    public static List<string> Instances(ProjectIndex idx, string pou, int max = 50)
    {
        var found = new List<string>();
        var target = Type(idx, pou);
        if (target?.Kind == "PROGRAM") { found.Add(target.Name); return found; }
        bool Is(string type) { for (var t = Type(idx, type); t != null; t = Type(idx, t.Extends)) if (t.Name.Equals(pou, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        void Walk(PouDecl owner, string path, int depth)
        {
            if (depth > 6 || found.Count >= max) return;
            foreach (var v in owner.Vars.Values.Where(v => v.Section is not ("VAR_TEMP" or "VAR_IN_OUT" or "VAR_INPUT" or null) || owner.Kind is "GVL" or "PROGRAM"))
            {
                var ty = v.Type ?? "";
                string sub = path + "." + v.Name;
                var arr = Regex.Match(ty, @"^ARRAY\s*\[\s*(-?\d+)\s*\.\..*?\]\s*OF\s+(.+)$", RegexOptions.IgnoreCase);
                if (arr.Success) { sub += "[" + arr.Groups[1].Value + "]"; ty = arr.Groups[2].Value.Trim(); }
                if (ty.StartsWith("POINTER", StringComparison.OrdinalIgnoreCase) || ty.StartsWith("REFERENCE", StringComparison.OrdinalIgnoreCase)) continue;
                if (Is(ty)) { found.Add(sub); continue; }
                if (Type(idx, ty) is { Kind: "FUNCTION_BLOCK" or "STRUCT" } inner) Walk(inner, sub, depth + 1);
            }
        }
        foreach (var p in idx.Types.Values.Where(t => t.Kind == "PROGRAM").OrderBy(t => t.Name)) Walk(p, p.Name, 0);
        foreach (var g in idx.Gvls.OrderBy(g => g.Name)) Walk(g, g.Name, 0);
        return found.Distinct().Take(max).ToList();
    }

    // ------------------------------------------------------------------ the operands of a sub-condition
    /// <summary>The variables a sub-condition reads, each resolved to its ADS path. pou: the POU the condition is in; method: the
    /// method (or null); instance: the instance path of the POU (MAIN.fbStation1, or the PROGRAM name).</summary>
    public static List<ResolvedOperand> Operands(ProjectIndex idx, string pou, string method, string instance, string atom)
    {
        var t = StLexer.Lex(atom ?? "").Where(x => !x.Trivia && !x.Comment).ToList();
        var r = new List<ResolvedOperand>();
        for (int i = 0; i < t.Count; i++)
        {
            var w = t[i];
            if (w.Kind != StTok.Word || StKeywords.IsKeyword(w.Text) && w.Upper is not ("THIS" or "SUPER")) continue;
            if (i > 0 && t[i - 1].Kind == StTok.Op && t[i - 1].Text == ".") continue;
            // the chain: name (.name | ^ | [ ... ])*
            int j = i + 1;
            while (j < t.Count)
            {
                if (t[j].Kind == StTok.Op && t[j].Text == "." && j + 1 < t.Count && t[j + 1].Kind is StTok.Word or StTok.Number) { j += 2; continue; }
                if (t[j].Kind == StTok.Op && t[j].Text == "^") { j++; continue; }
                if (t[j].Text == "[") { int d = 0; for (; j < t.Count; j++) { if (t[j].Text == "[") d++; else if (t[j].Text == "]" && --d == 0) { j++; break; } } continue; }
                break;
            }
            var chain = t.GetRange(i, j - i);
            bool call = j < t.Count && t[j].Text == "(";
            r.Add(Resolve(idx, pou, method, instance, chain, call));
            i = j - 1;
        }
        // enum values and constants of a type (E_State.Run) are no operands
        return r.Where(x => x != null).ToList();
    }

    static string Join(List<StToken> c) => string.Concat(c.Select(x => x.Text));

    static ResolvedOperand Resolve(ProjectIndex idx, string pouName, string method, string instance, List<StToken> chain, bool call)
    {
        var o = new ResolvedOperand { Text = Join(chain) };
        ResolvedOperand No(string en, string de) { o.Path = null; o.Reason = en; o.ReasonDe = de; return o; }
        if (call) return No("a call - never executed to show a value (it may have side effects)", "ein Aufruf - wird nie ausgeführt, um einen Wert zu zeigen (er kann Nebenwirkungen haben)");
        var pou = Type(idx, pouName);
        var first = chain[0];
        string path;
        string typeName;
        int k = 1;
        // enum value or constant of a type: E_State.Run
        if (idx.Types.TryGetValue(first.Text, out var asType) && asType.Kind == "ENUM") return null;
        if (first.Upper is "THIS" or "SUPER")
        {
            // THIS^.x / SUPER^.x: a member of the instance
            if (instance == null || pou == null) return No("the instance is not known", "die Instanz ist nicht bekannt");
            if (chain.Count < 4 || chain[1].Text != "^" || chain[2].Text != "." ) return No("THIS^ / SUPER^ alone", "THIS^ / SUPER^ allein");
            var name = chain[3].Text;
            var v = Member(idx, first.Upper == "SUPER" ? Type(idx, pou.Extends) ?? pou : pou, name, out var where);
            if (v == null) return Property(idx, pou, name) is { } mon ? PropertyResult(o, instance + "." + name, mon, name) : No($"{name} is not declared in {pouName} or its base types", $"{name} ist in {pouName} und seinen Basistypen nicht deklariert");
            path = instance + "." + name; typeName = v.Type;
            o.From = first.Upper == "SUPER" || where != pou ? $"inherited from {where.Name} ({first.Upper}^)" : "own variable (THIS^)";
            o.FromDe = first.Upper == "SUPER" || where != pou ? $"geerbt von {where.Name} ({first.Upper}^)" : "eigene Variable (THIS^)";
            k = 4;
        }
        else
        {
            var name = first.Text;
            VarDecl v = null; PouDecl where = null;
            // a local of the method: on the stack, or VAR_INST
            if (method != null && pou != null && pou.Methods.TryGetValue(method, out var md) && md.Vars.TryGetValue(name, out var mv))
                return mv.Section == "VAR_INST"
                    ? No($"VAR_INST of method {method} - not read here", $"VAR_INST der Methode {method} - wird hier nicht gelesen")
                    : No($"{SectionWord(mv.Section)} of method {method} - lives on the stack only while the method runs", $"{SectionWordDe(mv.Section)} der Methode {method} - liegt nur auf dem Stack, solange die Methode läuft");
            if (pou != null) v = Member(idx, pou, name, out where);
            if (v != null)
            {
                if (pou.Kind == "FUNCTION") return No("a variable of a function - lives on the stack only during the call", "eine Variable einer Funktion - liegt nur während des Aufrufs auf dem Stack");
                if (v.Section == "VAR_TEMP") return No("VAR_TEMP - lives on the stack only during the cycle", "VAR_TEMP - liegt nur während des Zyklus auf dem Stack");
                if (instance == null) return No("the instance is not known - choose it", "die Instanz ist nicht bekannt - bitte wählen");
                path = instance + "." + name; typeName = v.Type;
                o.From = where != pou ? $"inherited from {where.Name}" : v.Section == "VAR_INPUT" ? "VAR_INPUT" : v.Section == "VAR_OUTPUT" ? "VAR_OUTPUT" : v.Section == "VAR_IN_OUT" ? "VAR_IN_OUT (reference to the caller's variable)" : "own variable";
                o.FromDe = where != pou ? $"geerbt von {where.Name}" : v.Section == "VAR_INPUT" ? "VAR_INPUT" : v.Section == "VAR_OUTPUT" ? "VAR_OUTPUT" : v.Section == "VAR_IN_OUT" ? "VAR_IN_OUT (Referenz auf die Variable des Aufrufers)" : "eigene Variable";
            }
            else if (pou != null && Property(idx, pou, name) is { } mon)
                return PropertyResult(o, (instance ?? "?") + "." + name, mon, name);
            else
            {
                // a global: GVL.name, or name of a GVL without qualified_only
                var gvl = idx.Gvls.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (gvl != null && chain.Count >= 3 && chain[1].Text == "." && gvl.Vars.TryGetValue(chain[2].Text, out var gv))
                { path = gvl.Name + "." + gv.Name; typeName = gv.Type; k = 3; o.From = "global (" + gvl.Name + ")"; o.FromDe = "global (" + gvl.Name + ")"; }
                else if (idx.Gvls.FirstOrDefault(g => !g.QualifiedOnly && g.Vars.ContainsKey(name)) is { } g2)
                { var gv2 = g2.Vars[name]; path = g2.Name + "." + gv2.Name; typeName = gv2.Type; o.From = "global (" + g2.Name + ")"; o.FromDe = "global (" + g2.Name + ")"; }
                else if (idx.Types.TryGetValue(name, out var prog) && prog.Kind == "PROGRAM" && chain.Count >= 3 && chain[1].Text == ".")
                { path = prog.Name; typeName = prog.Name; k = 1; o.From = "program " + prog.Name; o.FromDe = "Programm " + prog.Name; }
                else if (LibraryBase(idx, pou) is { } lib && !(chain.Count >= 3 && chain[1].Text == "." && char.IsUpper(chain[0].Text[0]) && chain[2].Text.ToUpperInvariant() == chain[2].Text))
                {
                    // the POU extends a type of a library: the variable may be declared there - read it, the read tells
                    if (instance == null) return No("the instance is not known - choose it", "die Instanz ist nicht bekannt - bitte wählen");
                    path = instance + "." + name; typeName = null;
                    o.From = $"inherited from {lib} (a library - not checked)"; o.FromDe = $"geerbt von {lib} (Bibliothek - nicht geprüft)";
                }
                else if (chain.Count >= 3 && chain[1].Text == "." && chain.Count == 3)
                {
                    // Type.VALUE: an enum value or constant of a library - not read, compared as written
                    o.Constant = true; o.From = "a value of a library type (enum or constant)"; o.FromDe = "ein Wert eines Bibliothekstyps (Enum oder Konstante)";
                    return o;
                }
                else return No($"{name} is not declared where the condition is (or in a GVL of the project)", $"{name} ist dort, wo die Bedingung steht, nicht deklariert (auch in keiner GVL des Projekts)");
            }
        }
        // the rest of the chain, along the types
        while (k < chain.Count)
        {
            var x = chain[k];
            var ty = (typeName ?? "").Trim();
            bool isPtr = ty.StartsWith("POINTER TO ", StringComparison.OrdinalIgnoreCase);
            bool isRef = ty.StartsWith("REFERENCE TO ", StringComparison.OrdinalIgnoreCase);
            var inner = isPtr ? ty[11..].Trim() : isRef ? ty[13..].Trim() : ty;
            if (x.Kind == StTok.Op && x.Text == "^")
            {
                if (!isPtr) return No($"^ on {Join(chain.Take(k).ToList())}, which is not a pointer", $"^ auf {Join(chain.Take(k).ToList())}, das kein Zeiger ist");
                o.PointerChecks.Add(path);
                path += "^"; typeName = inner;
                Note(o, $"through pointer {Join(chain.Take(k).ToList())}", $"über Zeiger {Join(chain.Take(k).ToList())}");
                k++; continue;
            }
            if (x.Text == "[")
            {
                int close = k, d = 0;
                for (; close < chain.Count; close++) { if (chain[close].Text == "[") d++; else if (chain[close].Text == "]" && --d == 0) break; }
                var idxToks = chain.GetRange(k + 1, close - k - 1);
                var idxText = Join(idxToks);
                if (idxToks.All(z => z.Kind == StTok.Number || z.Text == ","))
                    path += "[" + idxText + "]";
                else if (idxToks.Count == 1 && idxToks[0].Kind == StTok.Word)
                {
                    var iv = Resolve(idx, pouName, method, instance, idxToks, false);
                    if (iv?.Path == null) return No($"the index {idxText} cannot be read", $"der Index {idxText} kann nicht gelesen werden");
                    o.Indexes.Add((idxText, iv.Path));
                    path += "[{" + (o.Indexes.Count - 1) + "}]";
                }
                else return No($"the index {idxText} is an expression - not evaluated", $"der Index {idxText} ist ein Ausdruck - wird nicht berechnet");
                var m = Regex.Match(inner, @"^ARRAY\s*\[.*?\]\s*OF\s+(.+)$", RegexOptions.IgnoreCase);
                typeName = m.Success ? m.Groups[1].Value.Trim() : null;
                k = close + 1; continue;
            }
            if (x.Kind == StTok.Op && x.Text == ".")
            {
                var name = chain[k + 1].Text;
                if (isRef) Note(o, $"through reference {Join(chain.Take(k).ToList())}", $"über Referenz {Join(chain.Take(k).ToList())}");
                if (isPtr) return No($"{Join(chain.Take(k).ToList())} is a pointer - use ^ to reach {name}", $"{Join(chain.Take(k).ToList())} ist ein Zeiger - {name} erreicht man nur mit ^");
                if (chain[k + 1].Kind == StTok.Number) { path += "." + name; typeName = "BOOL"; k += 2; continue; }   // bit access
                var t2 = Type(idx, inner);
                if (t2?.Kind == "INTERFACE")
                    return No($"{name} through the interface {Join(chain.Take(k).ToList())} - which FB is behind it is only known at runtime", $"{name} über die Schnittstelle {Join(chain.Take(k).ToList())} - welcher FB dahinter steckt, ist erst zur Laufzeit bekannt");
                if (t2 != null)
                {
                    var mv = Member(idx, t2, name, out var where2);
                    if (mv == null && Property(idx, t2, name) is { } mon)
                    {
                        if (!mon) return No($"property {name} of {t2.Name} - readable online only with {{attribute 'monitoring' := 'call'}}", $"Property {name} von {t2.Name} - online nur lesbar mit {{attribute 'monitoring' := 'call'}}");
                        path += "." + name; typeName = null; k += 2; continue;
                    }
                    if (mv == null && t2.Kind is "FUNCTION_BLOCK" or "STRUCT") return No($"{name} is not declared in {t2.Name}", $"{name} ist in {t2.Name} nicht deklariert");
                    if (mv != null && mv.Section == "VAR_TEMP") return No($"{name} is VAR_TEMP of {t2.Name}", $"{name} ist VAR_TEMP von {t2.Name}");
                    if (mv != null && where2 != t2) Note(o, $"{name} inherited from {where2.Name}", $"{name} geerbt von {where2.Name}");
                    typeName = mv?.Type;
                }
                else typeName = null;            // a library type (TON.Q ...): read as it is
                path += "." + name;
                k += 2; continue;
            }
            return No($"cannot follow {Join(chain)}", $"{Join(chain)} kann nicht verfolgt werden");
        }
        if ((typeName ?? "").StartsWith("REFERENCE TO ", StringComparison.OrdinalIgnoreCase)) Note(o, "a reference - read through it", "eine Referenz - es wird über sie gelesen");
        if (string.IsNullOrEmpty(o.From)) { o.From = "own variable"; o.FromDe = "eigene Variable"; }
        o.Path = path;
        return o;
    }

    static ResolvedOperand PropertyResult(ResolvedOperand o, string path, bool monitoring, string name)
    {
        if (!monitoring) { o.Reason = $"property {name} - readable online only with {{attribute 'monitoring' := 'call'}}"; o.ReasonDe = $"Property {name} - online nur lesbar mit {{attribute 'monitoring' := 'call'}}"; return o; }
        o.Path = path; o.From = "property (monitoring)"; o.FromDe = "Property (monitoring)";
        return o;
    }

    static void Note(ResolvedOperand o, string en, string de)
    {
        o.From = string.IsNullOrEmpty(o.From) ? en : o.From + ", " + en;
        o.FromDe = string.IsNullOrEmpty(o.FromDe) ? de : o.FromDe + ", " + de;
    }

    static string SectionWord(string s) => s switch { "VAR_INPUT" => "an input", "VAR_OUTPUT" => "an output", "VAR_IN_OUT" => "an in-out", "VAR_TEMP" => "a temporary variable", _ => "a local variable" };
    static string SectionWordDe(string s) => s switch { "VAR_INPUT" => "ein Eingang", "VAR_OUTPUT" => "ein Ausgang", "VAR_IN_OUT" => "ein IN_OUT", "VAR_TEMP" => "eine temporäre Variable", _ => "eine lokale Variable" };
}
