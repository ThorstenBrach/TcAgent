// Program documentation of a PLC project: what calls what, what each object does, how the program works.
//
//   tc_doc action=outline   compact text for the agent: task -> program -> FB/method call hierarchy, objects with their
//                           description (or "no description"), step chains, findings
//   tc_doc action=html      one self-contained HTML page: call hierarchy (clickable tree), call graph, class diagram
//                           (EXTENDS/IMPLEMENTS, methods, properties), step chains (SFC and CASE state machines),
//                           reference per object (description, inputs/outputs, calls, called by, code) and findings.
//                           The agent adds an overview and a short explanation per object (summaries); they are kept in
//                           <solution>\.tcagent\doc-summaries.json as project memory and reused by later runs and the CLI.
//   tc_doc action=memory    the stored overview and explanations (project memory) - read this before explaining the project
//
// The analysis is done on the source text (declarations and implementations through the Automation Interface,
// SFC through PLCopen XML): calls are resolved through the declared types of instances, EXTENDS chains and GVLs.
// Diagrams are Mermaid (rendered in the browser; the hierarchy tree works without it).
//
// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public sealed class PlcDoc
{
    public sealed class Var { public string Section, Name, Type, Comment; }

    public sealed class Pou
    {
        public string Key, Name, Kind, Owner, Path, Decl = "", Impl = "", Doc = "", Extends, Lang = "ST";
        public readonly List<string> Implements = new();
        public readonly List<Var> Vars = new();
        public readonly SortedSet<string> Calls = new(StringComparer.OrdinalIgnoreCase);     // keys of project objects
        public readonly SortedSet<string> External = new(StringComparer.OrdinalIgnoreCase);  // library / unknown
        public readonly List<string> Members = new();                                         // method/property/action keys
        public string SfcText;
        public bool SelfCall;
        public readonly List<(string From, string To, string Cond)> SfcEdges = new();
        public readonly List<(string Var, List<(string From, string To, string Cond)> Edges)> CaseCharts = new();
        public int Lines => (Decl + "\n" + Impl).Split('\n').Length;
    }

    public string Project = "";
    public readonly Dictionary<string, Pou> Pous = new(StringComparer.OrdinalIgnoreCase);
    public readonly SortedDictionary<string, List<string>> Tasks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string Gvl, string Type)> globals = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] PouKinds = { "PROGRAM", "FUNCTION_BLOCK", "FUNCTION", "INTERFACE" };

    // ================================================================== collect (STA thread)
    public static PlcDoc Collect(TwinCatSession tc, ITcSmTreeItem plcProject, Func<ITcSmTreeItem, string, (string Text, List<(string, string, string)> Edges)> sfc)
    {
        var d = new PlcDoc { Project = plcProject.Name };
        var items = tc.PlcObjects(plcProject).ToList();
        var byPath = new Dictionary<string, Pou>(StringComparer.OrdinalIgnoreCase);     // tree path -> object

        foreach (var it in items)
        {
            string path, name; int type;
            try { path = it.PathName; name = it.Name; type = it.ItemType; } catch { continue; }
            if (type == 601) continue;                                         // folder
            string decl = null, impl = null;
            try { if (it is ITcPlcDeclaration dd) decl = dd.DeclarationText; } catch { }
            try { if (it is ITcPlcImplementation ii) impl = ii.ImplementationText; } catch { }
            var parentPath = path.Contains('^') ? path[..path.LastIndexOf('^')] : "";
            var kind = KindOf(decl, type);
            if (kind == null)
            {
                // property accessors: their code belongs to the property
                if (type is 613 or 614 && byPath.TryGetValue(parentPath, out var prop)) prop.Impl += "\n" + impl;
                continue;
            }
            var pou = new Pou { Name = name, Kind = kind, Path = path, Decl = decl ?? "", Impl = impl ?? "" };
            if (kind is "METHOD" or "PROPERTY" or "ACTION" or "TRANSITION")
            {
                if (!byPath.TryGetValue(parentPath, out var owner)) continue;
                pou.Owner = owner.Name;
                pou.Key = owner.Name + "." + name;
                if (d.Pous.ContainsKey(pou.Key)) continue;
                owner.Members.Add(pou.Key);
            }
            else
            {
                pou.Key = name;
                if (d.Pous.ContainsKey(name)) continue;                      // first one wins (e.g. a program call below a task)
            }
            if (string.IsNullOrWhiteSpace(impl) && kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "METHOD" or "ACTION")
            {
                pou.Lang = "GRAPHIC";        // no text implementation: SFC, FBD, LD or CFC
                try
                {
                    var lang = ComCall.Get(it, "Language")?.ToString() ?? "";
                    lang = Regex.Replace(lang, @"^.*_", "").ToUpperInvariant();
                    if (Regex.IsMatch(lang, @"^[A-Z]+$")) pou.Lang = lang;      // ignore numeric enum values
                }
                catch { }
            }
            d.Pous[pou.Key] = pou;
            byPath[path] = pou;
        }
        // tasks of the PLC project: a node without declaration whose children are program calls (e.g. ...^PlcTask^MAIN);
        // folders are skipped and a child must not be the program object itself (a folder "POUs" holding MAIN is no task)
        foreach (var it in items)
        {
            string path; try { path = it.PathName; if (it.ItemType == 601) continue; } catch { continue; }
            if (byPath.ContainsKey(path)) continue;
            var progs = new List<string>();
            List<ITcSmTreeItem> kids;
            try { kids = TwinCatSession.Children(it).ToList(); } catch { continue; }
            foreach (var c in kids)
            {
                try { if (d.Pous.TryGetValue(c.Name, out var p) && p.Kind == "PROGRAM" && !p.Path.Equals(c.PathName, StringComparison.OrdinalIgnoreCase)) progs.Add(p.Key); } catch { }
            }
            if (progs.Count > 0) d.Tasks[it.Name] = progs;
        }
        // SFC charts (graphical - through PLCopen XML); other graphical languages are only listed
        foreach (var p in d.Pous.Values.Where(p => p.Owner == null && (p.Lang is "SFC" or "GRAPHIC")).ToList())
        {
            try
            {
                var (text, edges) = sfc(plcProject, p.Path);
                p.SfcText = text;
                p.SfcEdges.AddRange(edges);
                p.Lang = "SFC";
            }
            catch (Exception ex)
            {
                var body = Regex.Match(ex.Message, @"^body:(\w+)");
                if (body.Success) p.Lang = body.Groups[1].Value.ToUpperInvariant();
                else if (p.Lang == "SFC") p.SfcText = "(chart not readable: " + ex.Message + ")";
            }
        }
        return d;
    }

    static string KindOf(string decl, int itemType)
    {
        if (itemType == 608) return "ACTION";
        if (itemType == 616) return "TRANSITION";
        if (string.IsNullOrWhiteSpace(decl)) return null;
        var body = StripComments(decl);
        body = Regex.Replace(body, @"\{[^}]*\}", " ");                    // pragmas {attribute ...}
        var m = Regex.Match(body, @"^\s*(PROGRAM|FUNCTION_BLOCK|FUNCTION|METHOD|PROPERTY|INTERFACE|TYPE|VAR_GLOBAL)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var k = m.Groups[1].Value.ToUpperInvariant();
        return k switch { "TYPE" => "DUT", "VAR_GLOBAL" => "GVL", _ => k };
    }

    // ================================================================== analyze
    public void Analyze()
    {
        foreach (var p in Pous.Values)
        {
            ParseDeclaration(p);
            p.Doc = DocOf(p.Decl, p.Impl);
            if (p.Kind == "GVL")
                foreach (var v in p.Vars) { globals[p.Name + "." + v.Name] = (p.Name, v.Type); globals.TryAdd(v.Name, (p.Name, v.Type)); }
        }
        foreach (var p in Pous.Values)
        {
            var code = StripStrings(StripComments(p.Impl));
            if (code.Trim().Length > 0)
            {
                FindCalls(p, code);
                FindCaseCharts(p, code);
            }
            if (p.SfcText != null)
                foreach (Match m in Regex.Matches(p.SfcText, @"^\s*(?:ACTION\s+\S+(?:\s+T#\S+)?|TRANSITION)\s+([A-Za-z_]\w*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                    if (Pous.TryGetValue(p.Name + "." + m.Groups[1].Value, out var act)) p.Calls.Add(act.Key);
            p.SelfCall = p.Calls.Remove(p.Key);
        }
    }

    static string StripComments(string s)
    {
        s ??= "";
        s = Regex.Replace(s, @"\(\*.*?\*\)", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        return Regex.Replace(s, @"//[^\n]*", "");
    }

    static string StripStrings(string s) => Regex.Replace(s, @"'(?:\$.|[^'])*'|""(?:\$.|[^""])*""", "''");

    void ParseDeclaration(Pou p)
    {
        var decl = p.Decl.Replace("\r\n", "\n");
        var head = Regex.Replace(StripComments(decl), @"\{[^}]*\}", " ");
        var ext = Regex.Match(head, @"\bEXTENDS\s+([A-Za-z_][\w.]*)", RegexOptions.IgnoreCase);
        if (ext.Success) p.Extends = ext.Groups[1].Value.Split('.').Last();
        var imp = Regex.Match(head, @"\bIMPLEMENTS\s+([A-Za-z_][\w.,\s]*?)(?=\s*(\n|VAR|$))", RegexOptions.IgnoreCase);
        if (imp.Success) p.Implements.AddRange(imp.Groups[1].Value.Split(',').Select(x => x.Trim().Split('.').Last()).Where(x => x.Length > 0));
        if (p.Kind == "FUNCTION" || p.Kind == "METHOD" || p.Kind == "PROPERTY")
        {
            var rt = Regex.Match(head, @"^\s*(?:FUNCTION|METHOD|PROPERTY)\b[^:\n]*:\s*([^\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (rt.Success) p.Vars.Add(new Var { Section = "RETURN", Name = p.Name, Type = rt.Groups[1].Value.Trim() });
        }
        string section = null, pendingDoc = null;
        foreach (var raw in decl.Split('\n'))
        {
            var line = raw.Trim();
            var sec = Regex.Match(line, @"^(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_GLOBAL|VAR_STAT|VAR_INST|VAR_TEMP|VAR_EXTERNAL|VAR)\b", RegexOptions.IgnoreCase);
            if (sec.Success) { section = sec.Groups[1].Value.ToUpperInvariant(); pendingDoc = null; continue; }
            if (Regex.IsMatch(line, @"^END_VAR\b", RegexOptions.IgnoreCase)) { section = null; continue; }
            if (line.StartsWith("///")) { pendingDoc = (pendingDoc == null ? "" : pendingDoc + " ") + line.TrimStart('/').Trim(); continue; }
            if (section == null) continue;
            var m = Regex.Match(line, @"^([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*(?:AT\s+%\S+\s*)?:\s*([^;]+?)\s*(?::=[^;]*)?;\s*(?://\s*(.*)|\(\*\s*(.*?)\s*\*\))?$");
            if (!m.Success) continue;
            var comment = pendingDoc ?? (m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : "");
            var type = Regex.Replace(m.Groups[2].Value, @"\s*:=.*$", "").Trim();
            foreach (var n in m.Groups[1].Value.Split(','))
                p.Vars.Add(new Var { Section = section, Name = n.Trim(), Type = type, Comment = comment?.Trim() });
            pendingDoc = null;
        }
    }

    // description: /// lines before the keyword, else a "Description:" block of the header comment, else the first comment
    static string DocOf(string decl, string impl)
    {
        decl = (decl ?? "").Replace("\r\n", "\n");
        var lines = decl.Split('\n');
        var doc = new List<string>();
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.StartsWith("///")) { doc.Add(t.TrimStart('/').Trim()); continue; }
            if (t.StartsWith("{") || t.Length == 0) continue;
            break;
        }
        if (doc.Count > 0) return string.Join(" ", doc).Trim();
        var m = Regex.Match(decl, @"Description\s*:\s*\n?(?<d>(?:[ \t]*//[^\n]*\n?)+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var block = new List<string>();
            foreach (var l in m.Groups["d"].Value.Split('\n'))
            {
                var t = Regex.Replace(l.Trim(), @"^//\s?", "").Trim();
                if (t.Length == 0 && block.Count > 0) break;
                if (Regex.IsMatch(t, @"^[A-Z][\w ]{1,20}:\s*$") && block.Count > 0) break;    // next header field
                if (t.Length > 0) block.Add(t);
            }
            if (block.Count > 0) return string.Join(" ", block);
        }
        var first = Regex.Match(decl, @"\(\*\s*(.+?)\s*\*\)", RegexOptions.Singleline);
        if (first.Success && first.Index < 200) return Regex.Replace(first.Groups[1].Value, @"\s+", " ");
        return "";
    }

    // ------------------------------------------------------------------ calls
    static readonly HashSet<string> NotCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "ELSIF", "WHILE", "UNTIL", "FOR", "CASE", "RETURN", "NOT", "AND", "OR", "XOR", "MOD", "THEN", "DO", "OF",
        "SIZEOF", "ADR", "ADR2", "REF", "SEL", "MUX", "LIMIT", "MIN", "MAX", "ABS", "SQRT", "EXPT", "TRUNC", "LEN", "CONCAT",
        "LEFT", "RIGHT", "MID", "FIND", "INSERT", "DELETE", "REPLACE", "SHL", "SHR", "ROL", "ROR", "LN", "LOG", "EXP",
        "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "MOVE", "MEMCPY", "MEMSET", "MEMCMP", "__NEW", "__DELETE", "__QUERYINTERFACE",
        "__ISVALIDREF", "TO_STRING", "TO_INT", "TO_REAL", "TO_LREAL", "TO_DINT", "TO_UDINT", "TO_BOOL", "TO_BYTE", "TO_WORD", "TO_DWORD"
    };

    static readonly Regex CallRx = new(@"(?<![\w.^])((?:THIS|SUPER)\^\s*\.\s*)?([A-Za-z_]\w*(?:\s*\[[^\]]*\])?(?:\s*\^?\s*\.\s*[A-Za-z_]\w*(?:\s*\[[^\]]*\])?)*)\s*\(", RegexOptions.Compiled);

    Pou TypeOf(string type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        var bare = Regex.Replace(type, @"^\s*(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", RegexOptions.IgnoreCase).Split('(')[0].Split('[')[0].Trim();
        bare = bare.Split('.').Last();
        return Pous.TryGetValue(bare, out var p) && p.Owner == null ? p : null;
    }

    static string BareType(string type) =>
        Regex.Replace(type ?? "", @"^\s*(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", RegexOptions.IgnoreCase).Split('(')[0].Split('[')[0].Trim().Split('.').Last();

    IEnumerable<Pou> Chain(Pou t)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (t != null && seen.Add(t.Name)) { yield return t; t = t.Extends != null && Pous.TryGetValue(t.Extends, out var b) ? b : null; }
    }

    Var VarOf(Pou t, string name) => Chain(t).SelectMany(x => x.Vars).FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    string MemberOf(Pou t, string name) => Chain(t).Select(x => Pous.TryGetValue(x.Name + "." + name, out var m) ? m.Key : null).FirstOrDefault(k => k != null);

    void FindCalls(Pou p, string code)
    {
        var self = p.Owner != null && Pous.TryGetValue(p.Owner, out var o) ? o : p;
        foreach (Match m in CallRx.Matches(code))
        {
            var chain = Regex.Replace(m.Groups[2].Value, @"\s*\[[^\]]*\]", "").Replace("^", "").Replace(" ", "");
            var parts = chain.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0 || NotCalls.Contains(parts[0]) || Regex.IsMatch(parts[0], @"^\w+_TO_\w+$", RegexOptions.IgnoreCase)) continue;
            Pou cur = null; string ext = null;
            var prefix = m.Groups[1].Value.ToUpperInvariant();
            int i = 0;
            if (prefix.StartsWith("THIS")) cur = self;
            else if (prefix.StartsWith("SUPER")) cur = self.Extends != null && Pous.TryGetValue(self.Extends, out var b) ? b : null;
            else
            {
                var first = parts[0];
                var v = p.Vars.FirstOrDefault(x => x.Name.Equals(first, StringComparison.OrdinalIgnoreCase)) ?? (p != self ? VarOf(self, first) : VarOf(p, first));
                if (v != null) { cur = TypeOf(v.Type); if (cur == null) ext = BareType(v.Type); i = 1; }
                else if (Pous.TryGetValue(first, out var gvl) && gvl.Kind == "GVL" && parts.Count > 1)
                {
                    var gv = gvl.Vars.FirstOrDefault(x => x.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                    if (gv == null) continue;
                    cur = TypeOf(gv.Type); if (cur == null) ext = BareType(gv.Type); i = 2;
                }
                else if (globals.TryGetValue(first, out var g)) { cur = TypeOf(g.Type); if (cur == null) ext = BareType(g.Type); i = 1; }
                else if (Pous.TryGetValue(first, out var direct) && direct.Owner == null)
                {
                    if (parts.Count == 1) { p.Calls.Add(direct.Key); continue; }
                    cur = direct; i = 1;      // Program.Action() / static access
                }
                else if (MemberOf(self, first) is { } own && parts.Count == 1) { p.Calls.Add(own); continue; }
                else { if (parts.Count == 1 && !Pous.ContainsKey(first)) p.External.Add(first); continue; }
            }
            if (ext != null)
            {
                p.External.Add(parts.Count > i ? ext + "." + parts[i] : ext);
                continue;
            }
            if (cur == null) continue;
            string target = cur.Key;
            for (; i < parts.Count; i++)
            {
                var mem = MemberOf(cur, parts[i]);
                if (mem != null) { target = mem; break; }
                var v = VarOf(cur, parts[i]);
                if (v == null) { target = null; break; }
                var next = TypeOf(v.Type);
                if (next == null) { p.External.Add(BareType(v.Type) + (i + 1 < parts.Count ? "." + parts[i + 1] : "")); target = null; break; }
                cur = next; target = cur.Key;
            }
            if (target != null) p.Calls.Add(target);
        }
    }

    // CASE <var> OF ... <label>: ... <var> := <label2> ... END_CASE  ->  state machine
    void FindCaseCharts(Pou p, string code)
    {
        var tok = new Regex(@"\bCASE\b|\bEND_CASE\b", RegexOptions.IgnoreCase);
        foreach (Match cm in Regex.Matches(code, @"\bCASE\s+([A-Za-z_][\w.]*)\s+OF\b", RegexOptions.IgnoreCase))
        {
            var stateVar = cm.Groups[1].Value;
            // body up to the matching END_CASE; nested CASE blocks are masked for the label search (their labels are no states)
            int depth = 1, pos = cm.Index + cm.Length, nestStart = -1;
            int end = code.Length;
            var mask = new StringBuilder(code.Length - pos);
            int copied = pos;
            for (var t = tok.Match(code, pos); t.Success; t = t.NextMatch())
            {
                var isEnd = t.Value.Equals("END_CASE", StringComparison.OrdinalIgnoreCase);
                if (!isEnd && depth == 1) { mask.Append(code, copied, t.Index - copied); copied = t.Index; nestStart = t.Index; }
                depth += isEnd ? -1 : 1;
                if (isEnd && depth == 1 && nestStart >= 0)
                {
                    var stop = t.Index + t.Length;
                    mask.Append(Regex.Replace(code[nestStart..stop], @"[^\n]", " "));
                    copied = stop; nestStart = -1;
                }
                if (depth == 0) { end = t.Index; break; }
            }
            if (copied < end) mask.Append(code, copied, end - copied);
            var body = code[pos..end];
            var masked = mask.ToString();
            if (masked.Length != body.Length) masked = body;
            // labels at line start: "Label:", "Label1, Label2:", "10:", "E_State.Idle:"
            var labelRx = new Regex(@"^[ \t]*((?:[A-Za-z_][\w.#]*|\d+(?:\.\.\d+)?)(?:\s*,\s*(?:[A-Za-z_][\w.#]*|\d+(?:\.\.\d+)?))*)\s*:(?!=)", RegexOptions.Multiline);
            var labels = labelRx.Matches(masked).Cast<Match>().Where(l => !Regex.IsMatch(l.Groups[1].Value, @"^(ELSE|THEN|DO)$", RegexOptions.IgnoreCase)).ToList();
            if (labels.Count < 2) continue;
            var edges = new List<(string, string, string)>();
            var assign = new Regex($@"\b{Regex.Escape(stateVar)}\s*:=\s*([A-Za-z_][\w.#]*|\d+)", RegexOptions.IgnoreCase);
            for (int k = 0; k < labels.Count; k++)
            {
                var from = labels[k].Groups[1].Value.Split(',')[0].Trim();
                var segStart = labels[k].Index + labels[k].Length;
                var segEnd = k + 1 < labels.Count ? labels[k + 1].Index : body.Length;
                var seg = body[segStart..segEnd];
                if (k == labels.Count - 1) seg = seg[..CaseElse(seg)];
                foreach (Match a in assign.Matches(seg))
                    edges.Add((Short(from), Short(a.Groups[1].Value), ConditionBefore(seg, a.Index)));
            }
            if (edges.Count > 0) p.CaseCharts.Add((stateVar, edges.Distinct().ToList()));
        }
    }

    // position of the ELSE of the CASE itself in the last label segment (an ELSE inside IF ... END_IF does not count)
    static int CaseElse(string seg)
    {
        int ifDepth = 0;                                                       // open IF / nested CASE blocks
        foreach (Match m in Regex.Matches(seg, @"\b(?:END_IF|ELSIF|IF|END_CASE|CASE|ELSE)\b", RegexOptions.IgnoreCase))
        {
            var v = m.Value.ToUpperInvariant();
            if (v is "IF" or "CASE") ifDepth++;
            else if (v is "END_IF" or "END_CASE") ifDepth = Math.Max(0, ifDepth - 1);
            else if (v == "ELSE" && ifDepth == 0) return m.Index;
        }
        return seg.Length;
    }

    // E_State.Idle / E_State#Idle -> Idle; numbers and ranges (10..20) stay
    static string Short(string label) => char.IsDigit(label[0]) ? label : label[(label.LastIndexOfAny(new[] { '.', '#' }) + 1)..];

    // innermost IF ... THEN that is still open at position idx
    static string ConditionBefore(string seg, int idx)
    {
        var text = seg[..idx];
        var stack = new Stack<string>();
        foreach (Match m in Regex.Matches(text, @"\b(?:ELSIF\s+(?<c>.+?)\s+THEN|IF\s+(?<c>.+?)\s+THEN|END_IF|ELSE)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var v = m.Value.ToUpperInvariant();
            if (v.StartsWith("END_IF")) { if (stack.Count > 0) stack.Pop(); }
            else if (v.StartsWith("ELSIF")) { if (stack.Count > 0) stack.Pop(); stack.Push(m.Groups["c"].Value); }
            else if (v == "ELSE") { if (stack.Count > 0) { var c = stack.Pop(); stack.Push("NOT (" + c + ")"); } }
            else stack.Push(m.Groups["c"].Value);
        }
        var cond = stack.Count > 0 ? Regex.Replace(stack.Peek(), @"\s+", " ").Trim() : "";
        return cond.Length > 60 ? cond[..57] + "..." : cond;
    }

    // ================================================================== hierarchy
    public List<string> Roots()
    {
        var roots = Tasks.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var called = new HashSet<string>(Pous.Values.SelectMany(p => p.Calls).Select(c => c.Split('.')[0]), StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values.Where(p => p.Kind == "PROGRAM" && !roots.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !called.Contains(p.Key)))
            roots.Add(p.Key);
        return roots;
    }

    // callees of an object: its own calls plus the calls of its members (for an FB: what its body and methods call)
    // callees of an object: what its code calls (for an FB body also its own methods)
    IEnumerable<string> Callees(string key) =>
        Pous.TryGetValue(key, out var p) ? p.Calls : Enumerable.Empty<string>();

    // FB instances declared in a program/FB that its code does not call directly - e.g. TcUnit test suites run by
    // TcUnit.RUN(), or instances used only through their outputs/methods called elsewhere
    List<string> Instances(string key)
    {
        if (!Pous.TryGetValue(key, out var p) || p.Owner != null || p.Kind is not ("PROGRAM" or "FUNCTION_BLOCK")) return new();
        var called = new HashSet<string>(p.Calls.Select(c => c.Split('.')[0]), StringComparer.OrdinalIgnoreCase);
        return p.Vars.Where(v => v.Section is "VAR" or "VAR_STAT" or "VAR_INST")
                     .Select(v => TypeOf(v.Type)).Where(t => t != null && t.Kind == "FUNCTION_BLOCK" && !called.Contains(t.Key))
                     .Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> Unused()
    {
        var reach = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(Roots());
        foreach (var g in Pous.Values.Where(x => x.Kind == "GVL"))           // global instances are in use as well
            foreach (var v in g.Vars) if (TypeOf(v.Type) is { } t) stack.Push(t.Key);
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            if (!Pous.TryGetValue(k, out var p) || !reach.Add(p.Key)) continue;
            foreach (var c in p.Calls) stack.Push(c);
            foreach (var m in p.Members) stack.Push(m);                  // an FB in use keeps its methods
            if (p.Owner != null) stack.Push(p.Owner);
            if (p.Extends != null) stack.Push(p.Extends);
            foreach (var i in p.Implements) stack.Push(i);
            foreach (var v in p.Vars) if (TypeOf(v.Type) is { } t) stack.Push(t.Key);     // instances
        }
        return Pous.Values.Where(p => p.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" && !reach.Contains(p.Key)).Select(p => p.Key).OrderBy(x => x).ToList();
    }

    // ================================================================== outline (text for the agent)
    public string Outline(Summaries s)
    {
        var o = new StringBuilder($"PLC project {Project}: {Pous.Values.Count(p => p.Owner == null && p.Kind != "GVL" && p.Kind != "DUT")} POUs, " +
                                  $"{Pous.Values.Count(p => p.Owner != null)} methods/properties/actions, {Pous.Values.Count(p => p.Kind == "DUT")} DUTs, {Pous.Values.Count(p => p.Kind == "GVL")} GVLs\n");
        o.AppendLine("\nCall hierarchy:");
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tasks) { o.AppendLine($"Task {t.Key}"); foreach (var r in t.Value) Tree(o, r, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase), done); }
        foreach (var r in Roots().Where(r => !Tasks.Values.Any(v => v.Contains(r, StringComparer.OrdinalIgnoreCase)))) Tree(o, r, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), done);

        o.AppendLine("\nObjects (description from comments | stored explanation):");
        foreach (var p in Pous.Values.Where(p => p.Kind is not ("DUT" or "GVL")).OrderBy(p => p.Key))
        {
            var mine = s.Get(p.Key);
            var desc = mine ?? (p.Doc.Length > 0 ? p.Doc : "(no description)");
            o.AppendLine($"  {p.Kind,-15} {p.Key,-40} {Trim(desc, 140)}{(mine != null ? "  [explained]" : "")}");
        }
        var charts = Pous.Values.Where(p => p.SfcText != null || p.CaseCharts.Count > 0).ToList();
        if (charts.Count > 0)
        {
            o.AppendLine("\nStep chains:");
            foreach (var p in charts)
            {
                if (p.SfcText != null) o.AppendLine($"  {p.Key} (SFC): {p.SfcEdges.Count} transitions");
                foreach (var (v, e) in p.CaseCharts) o.AppendLine($"  {p.Key}: CASE {v} - {e.Select(x => x.From).Distinct().Count()} states, {e.Count} transitions");
            }
        }
        var f = Findings();
        if (f.Count > 0) { o.AppendLine("\nFindings:"); foreach (var x in f) o.AppendLine("  - " + x); }
        o.AppendLine("\nNext: explain the objects that have no description (read them with tc_plc_read if needed) and call " +
                     "tc_doc action=html with overview (how the program works) and summaries {object: explanation}.");
        return o.ToString();
    }

    // each subtree is expanded only once (done): repeated objects are listed without children, so the output stays
    // linear in the number of calls instead of growing with the number of call paths
    void Tree(StringBuilder o, string key, int depth, HashSet<string> path, HashSet<string> done)
    {
        if (depth > 12) return;
        var ind = new string(' ', depth * 2 + 2);
        if (!path.Add(key)) { o.AppendLine($"{ind}{key} (recursion)"); return; }
        var kind = Pous.TryGetValue(key, out var p) ? p.Kind.ToLowerInvariant().Replace("function_block", "FB") : "?";
        var kids = Callees(key).ToList();
        var inst = Instances(key);
        if (kids.Count + inst.Count > 0 && !done.Add(key)) { o.AppendLine($"{ind}{key}  [{kind}] (see above)"); return; }
        o.AppendLine($"{ind}{key}  [{kind}]");
        foreach (var c in kids) Tree(o, c, depth + 1, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase), done);
        if (inst.Count > 0) o.AppendLine($"{ind}  instances (not called directly, e.g. TcUnit suites): {string.Join(", ", inst)}");
    }

    public List<string> Findings()
    {
        var f = new List<string>();
        var unused = Unused();
        if (unused.Count > 0) f.Add($"Not called from any task (unused or only used dynamically): {string.Join(", ", unused.Take(30))}{(unused.Count > 30 ? " ..." : "")}");
        foreach (var p in Pous.Values.Where(p => p.Lines > 400).OrderByDescending(p => p.Lines).Take(10))
            f.Add($"{p.Key}: {p.Lines} lines - candidate for splitting");
        foreach (var p in Pous.Values.Where(p => p.SelfCall))
            f.Add($"{p.Key} calls itself (recursion)");
        var noDoc = Pous.Values.Count(p => p.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "INTERFACE" && p.Doc.Length == 0);
        if (noDoc > 0) f.Add($"{noDoc} POUs/interfaces without a description comment (///)");
        foreach (var g in Pous.Values.Where(p => p.Kind == "GVL" && p.Vars.Count > 0))
        {
            var writers = Pous.Values.Count(p => p.Kind != "GVL" && Regex.IsMatch(StripComments(p.Impl), $@"\b{Regex.Escape(g.Name)}\.\w+\s*:=", RegexOptions.IgnoreCase));
            if (writers > 5) f.Add($"{g.Name} is written from {writers} objects - global coupling");
        }
        return f;
    }

    static string Trim(string s, int n) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length <= n ? s : s[..(n - 3)] + "..."; }

    // ================================================================== project memory
    public sealed class Summaries
    {
        public string Overview = "";
        public readonly Dictionary<string, string> Pous = new(StringComparer.OrdinalIgnoreCase);
        public string Get(string k) => Pous.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public static string FileFor(string solutionDir) => Path.Combine(solutionDir ?? ".", ".tcagent", "doc-summaries.json");

        public static Summaries Load(string solutionDir)
        {
            var s = new Summaries();
            try
            {
                var f = FileFor(solutionDir);
                if (!File.Exists(f)) return s;
                var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                s.Overview = j["overview"]?.ToString() ?? "";
                if (j["pous"] is JsonObject po) foreach (var kv in po) s.Pous[kv.Key] = kv.Value?.ToString() ?? "";
            }
            catch { }
            return s;
        }

        public void Save(string solutionDir)
        {
            var f = FileFor(solutionDir);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            var po = new JsonObject();
            foreach (var kv in Pous.OrderBy(k => k.Key)) po[kv.Key] = kv.Value;
            File.WriteAllText(f, new JsonObject { ["overview"] = Overview, ["pous"] = po }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    // ================================================================== HTML
    static string H(string s) => WebUtility.HtmlEncode(s ?? "");
    static string Id(string key) => "o-" + Regex.Replace(key ?? "", @"[^\w]", "_");
    static string Mid(string key) => "n" + Regex.Replace(key ?? "", @"[^\w]", "_");
    static string MLabel(string s) => (s ?? "").Replace("\"", "'").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "/").Replace("[", "(").Replace("]", ")").Replace("{", "(").Replace("}", ")");

    public string Html(Summaries s, string generatedBy)
    {
        var o = new StringBuilder();
        o.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        o.Append($"<title>{H(Project)} – program documentation</title><style>{Css}</style></head><body>");
        o.Append($"<header><h1>{H(Project)}</h1><p class=\"meta\">Program documentation · generated {DateTime.Now:yyyy-MM-dd HH:mm} by {H(generatedBy)} · ");
        o.Append($"{Pous.Values.Count(p => p.Owner == null && p.Kind is not ("DUT" or "GVL"))} POUs, {Pous.Values.Count(p => p.Owner != null)} methods/properties/actions, ");
        o.Append($"{Pous.Values.Count(p => p.Kind == "DUT")} DUTs, {Pous.Values.Count(p => p.Kind == "GVL")} GVLs</p>");
        o.Append("<nav><a href=\"#overview\">Overview</a><a href=\"#hierarchy\">Call hierarchy</a><a href=\"#graph\">Call graph</a><a href=\"#classes\">Classes</a>" +
                 "<a href=\"#chains\">Step chains</a><a href=\"#findings\">Findings</a><a href=\"#reference\">Reference</a>" +
                 "<input id=\"q\" placeholder=\"Filter objects…\" oninput=\"filter(this.value)\"></nav></header><main>");

        // overview
        o.Append("<section id=\"overview\"><h2>How the program works</h2>");
        o.Append(s.Overview.Length > 0 ? Para(s.Overview) : "<p class=\"muted\">No overview yet – ask the agent: \"document the program\" (tc_doc).</p>");
        if (Tasks.Count > 0)
        {
            o.Append("<h3>Tasks</h3><ul>");
            foreach (var t in Tasks) o.Append($"<li><b>{H(t.Key)}</b> calls {string.Join(", ", t.Value.Select(Link))}</li>");
            o.Append("</ul>");
        }
        o.Append("</section>");

        // hierarchy tree (works without JavaScript)
        o.Append("<section id=\"hierarchy\"><h2>Call hierarchy</h2><div class=\"tree\">");
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tasks)
        {
            o.Append($"<details open><summary><span class=\"k task\">task</span> {H(t.Key)}</summary>");
            foreach (var r in t.Value) TreeHtml(o, r, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, done);
            o.Append("</details>");
        }
        foreach (var r in Roots().Where(r => !Tasks.Values.Any(v => v.Contains(r, StringComparer.OrdinalIgnoreCase))))
            TreeHtml(o, r, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, done);
        o.Append("</div></section>");

        // call graph (POU level)
        o.Append("<section id=\"graph\"><h2>Call graph</h2>");
        var edges = new SortedSet<string>();
        var nodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values)
        {
            var from = p.Owner != null && Pous.TryGetValue(p.Owner, out var ow) ? ow.Key : p.Key;
            foreach (var c in p.Calls)
            {
                if (!Pous.TryGetValue(c, out var callee)) continue;
                var to = callee.Owner != null && Pous.TryGetValue(callee.Owner, out var co) ? co.Key : callee.Key;
                if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
                edges.Add($"{Mid(from)} --> {Mid(to)}");
                nodes.Add(from); nodes.Add(to);
            }
        }
        foreach (var t in Tasks) foreach (var r in t.Value) { edges.Add($"{Mid("task " + t.Key)}([\"{MLabel(t.Key)}\"]) --> {Mid(r)}"); nodes.Add(r); }
        if (edges.Count == 0) o.Append("<p class=\"muted\">No calls found.</p>");
        else if (edges.Count > 250) o.Append($"<p class=\"muted\">{edges.Count} connections – too many for one graph; use the hierarchy above.</p>");
        else
        {
            var g = new StringBuilder("flowchart LR\n");
            foreach (var n in nodes)
            {
                var p = Pous[n];
                g.AppendLine(p.Kind switch
                {
                    "PROGRAM" => $"  {Mid(n)}[[\"{MLabel(n)}\"]]",
                    "FUNCTION" => $"  {Mid(n)}([\"{MLabel(n)}\"])",
                    "INTERFACE" => $"  {Mid(n)}{{{{\"{MLabel(n)}\"}}}}",
                    _ => $"  {Mid(n)}[\"{MLabel(n)}\"]"
                });
            }
            foreach (var e in edges) g.AppendLine("  " + e);
            foreach (var n in nodes) g.AppendLine($"  click {Mid(n)} href \"#{Id(n)}\"");
            o.Append(Mermaid(g.ToString()));
        }
        o.Append("</section>");

        // class diagram
        o.Append("<section id=\"classes\"><h2>Classes and interfaces</h2>");
        var bases = new HashSet<string>(Pous.Values.SelectMany(x => x.Implements.Append(x.Extends)).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
        var classes = Pous.Values.Where(p => p.Owner == null && (p.Kind is "FUNCTION_BLOCK" or "INTERFACE") &&
                                             (p.Extends != null || p.Implements.Count > 0 || p.Members.Count > 0 || bases.Contains(p.Name))).ToList();
        if (classes.Count == 0) o.Append("<p class=\"muted\">No function blocks with methods, inheritance or interfaces.</p>");
        else if (classes.Count > 80) o.Append($"<p class=\"muted\">{classes.Count} classes – too many for one diagram; see the reference.</p>");
        else
        {
            var c = new StringBuilder("classDiagram\n");
            foreach (var p in classes)
            {
                c.AppendLine($"  class {Mid(p.Key)}[\"{MLabel(p.Name)}\"] {{");
                if (p.Kind == "INTERFACE") c.AppendLine("    <<interface>>");
                foreach (var v in p.Vars.Where(v => v.Section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT").Take(12))
                    c.AppendLine($"    +{MLabel(v.Name)} {MLabel(v.Type)}");
                foreach (var mk in p.Members.Take(25))
                {
                    var m = Pous[mk];
                    var rt = m.Vars.FirstOrDefault(v => v.Section == "RETURN")?.Type;
                    c.AppendLine(m.Kind == "PROPERTY" ? $"    +{MLabel(m.Name)} {MLabel(rt ?? "")}" : m.Kind == "METHOD" ? $"    +{MLabel(m.Name)}() {MLabel(rt ?? "")}" : $"    ~{MLabel(m.Name)}()");
                }
                c.AppendLine("  }");
            }
            var byName = classes.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var p in classes)
            {
                if (p.Extends != null && byName.TryGetValue(p.Extends, out var b)) c.AppendLine($"  {Mid(b.Key)} <|-- {Mid(p.Key)}");
                foreach (var i in p.Implements) if (byName.TryGetValue(i, out var itf)) c.AppendLine($"  {Mid(itf.Key)} <|.. {Mid(p.Key)}");
            }
            o.Append(Mermaid(c.ToString()));
        }
        o.Append("</section>");

        // step chains
        o.Append("<section id=\"chains\"><h2>Step chains</h2>");
        var any = false;
        foreach (var p in Pous.Values.Where(p => p.SfcText != null || p.CaseCharts.Count > 0).OrderBy(p => p.Key))
        {
            any = true;
            if (p.SfcText != null)
            {
                o.Append($"<h3>{Link(p.Key)} <span class=\"k\">SFC</span></h3>");
                if (p.SfcEdges.Count > 0) o.Append(Mermaid(StateDiagram(p.SfcEdges, FirstStep(p.SfcText))));
                o.Append($"<details><summary>Chain as text</summary><pre>{H(p.SfcText)}</pre></details>");
            }
            foreach (var (v, e) in p.CaseCharts)
            {
                o.Append($"<h3>{Link(p.Key)} <span class=\"k\">CASE {H(v)}</span></h3>");
                o.Append(Mermaid(StateDiagram(e, e[0].From)));
            }
        }
        if (!any) o.Append("<p class=\"muted\">No SFC charts or CASE state machines found.</p>");
        o.Append("</section>");

        // findings
        o.Append("<section id=\"findings\"><h2>Findings</h2>");
        var f = Findings();
        o.Append(f.Count == 0 ? "<p class=\"muted\">Nothing noticeable.</p>" : "<ul>" + string.Join("", f.Select(x => $"<li>{H(x)}</li>")) + "</ul>");
        o.Append("</section>");

        // reference
        o.Append("<section id=\"reference\"><h2>Reference</h2>");
        var calledBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values)
            foreach (var c in p.Calls)
            {
                if (!calledBy.TryGetValue(c, out var set)) calledBy[c] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(p.Key);
            }
        // objects that declare an instance/variable of a type (computed once - per object it would be O(n²))
        var instUsers = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values)
            foreach (var v in p.Vars)
            {
                var bt = BareType(v.Type);
                if (bt.Length == 0 || !Pous.ContainsKey(bt)) continue;
                if (!instUsers.TryGetValue(bt, out var set)) instUsers[bt] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(p.Key);
            }
        foreach (var kind in new[] { "PROGRAM", "FUNCTION_BLOCK", "FUNCTION", "INTERFACE", "DUT", "GVL" })
        {
            var list = Pous.Values.Where(p => p.Kind == kind && p.Owner == null).OrderBy(p => p.Key).ToList();
            if (list.Count == 0) continue;
            o.Append($"<h3>{KindTitle(kind)}</h3>");
            foreach (var p in list) PouHtml(o, p, s, calledBy, instUsers);
        }
        o.Append("</section></main>");
        o.Append($"<footer>Generated from the source code by TcAgent · explanations in <code>.tcagent/doc-summaries.json</code></footer>");
        o.Append($"<script src=\"https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js\"></script><script>{Js}</script></body></html>");
        return o.ToString();
    }

    static string KindTitle(string k) => k switch
    {
        "PROGRAM" => "Programs", "FUNCTION_BLOCK" => "Function blocks", "FUNCTION" => "Functions", "INTERFACE" => "Interfaces",
        "DUT" => "Data types", "GVL" => "Global variable lists", _ => k
    };

    string Link(string key) => Pous.TryGetValue(key, out var p) ? $"<a href=\"#{Id(p.Key)}\">{H(p.Key)}</a>" : H(key);

    static string Para(string text) => string.Join("", (text ?? "").Replace("\r\n", "\n").Split("\n\n").Select(p => $"<p>{H(p).Replace("\n", "<br>")}</p>"));

    static string Mermaid(string src) =>
        "<div class=\"dwrap\"><div class=\"dtools\"><button data-z=\"in\" title=\"Zoom in (Ctrl+wheel)\">+</button>" +
        "<button data-z=\"out\" title=\"Zoom out (Ctrl+wheel)\">&minus;</button><button data-z=\"fit\" title=\"Fit to width\">Fit</button>" +
        "<button data-z=\"one\" title=\"Original size\">1:1</button><button data-z=\"full\" title=\"Full screen\">&#x26F6;</button>" +
        "<span class=\"zl\"></span></div>" +
        $"<div class=\"diagram\"><pre class=\"mermaid\">{H(src)}</pre></div></div>";

    static string FirstStep(string sfcText) => Regex.Match(sfcText ?? "", @"STEP\s+(\w+)\s+INITIAL").Groups[1].Value;

    static string StateDiagram(List<(string From, string To, string Cond)> edges, string start)
    {
        var sd = new StringBuilder("stateDiagram-v2\n");
        // state ids: only word characters, not starting with a digit and no Mermaid keyword (state, note, end, ...)
        string S(string n)
        {
            var id = Regex.Replace(string.IsNullOrEmpty(n) ? "x" : n, @"[^\w]", "_");
            return char.IsDigit(id[0]) || Regex.IsMatch(id, @"^(state|note|end|direction|class|classDef|click|style|as|fork|join|choice)$", RegexOptions.IgnoreCase) ? "s_" + id : id;
        }
        if (!string.IsNullOrEmpty(start)) sd.AppendLine($"  [*] --> {S(start)}");
        foreach (var (f, t, c) in edges.Distinct())
            sd.AppendLine($"  {S(f)} --> {S(t)}" + (string.IsNullOrWhiteSpace(c) ? "" : " : " + MLabel(c.Replace(";", ",")).Replace(":", " ")));
        return sd.ToString();
    }

    void TreeHtml(StringBuilder o, string key, HashSet<string> path, int depth, HashSet<string> done, bool instance = false)
    {
        var kind = Pous.TryGetValue(key, out var p) ? p.Kind : "?";
        var recursion = !path.Add(key);
        var kids = depth < 12 && !recursion ? Callees(key).ToList() : new List<string>();
        var inst = depth < 12 && !recursion ? Instances(key) : new List<string>();
        var label = $"<span class=\"k {kind.ToLowerInvariant()}\">{H(KindShort(kind))}</span> {Link(key)}" +
                    (instance ? " <span class=\"muted\">(instance, not called directly)</span>" : "");
        if (recursion) label += " <span class=\"muted\">(recursion)</span>";
        else if (kids.Count + inst.Count > 0 && !done.Add(key)) { kids.Clear(); inst.Clear(); label += " <span class=\"muted\">(expanded above)</span>"; }
        if (kids.Count + inst.Count == 0) { o.Append($"<div class=\"leaf\">{label}</div>"); return; }
        o.Append($"<details{(depth < 2 ? " open" : "")}><summary>{label}</summary>");
        foreach (var c in kids) TreeHtml(o, c, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase), depth + 1, done);
        foreach (var c in inst) TreeHtml(o, c, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase), depth + 1, done, true);
        o.Append("</details>");
    }

    static string KindShort(string k) => k switch
    {
        "PROGRAM" => "PRG", "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FUN", "METHOD" => "M", "PROPERTY" => "P", "ACTION" => "ACT",
        "TRANSITION" => "TR", "INTERFACE" => "I", _ => k
    };

    void PouHtml(StringBuilder o, Pou p, Summaries s, Dictionary<string, SortedSet<string>> calledBy, Dictionary<string, SortedSet<string>> instUsers)
    {
        o.Append($"<article class=\"pou\" id=\"{Id(p.Key)}\" data-name=\"{H(p.Key.ToLowerInvariant())}\"><h4><span class=\"k {p.Kind.ToLowerInvariant()}\">{H(KindShort(p.Kind))}</span> {H(p.Name)}");
        if (p.Extends != null) o.Append($" <small>EXTENDS {Link(p.Extends)}</small>");
        if (p.Implements.Count > 0) o.Append($" <small>IMPLEMENTS {string.Join(", ", p.Implements.Select(Link))}</small>");
        o.Append($"</h4><p class=\"path\">{H(p.Path)}{(p.Lang != "ST" && p.Lang != "?" ? " · " + H(p.Lang) : "")}</p>");
        var mine = s.Get(p.Key);
        if (mine != null) o.Append($"<div class=\"explain\">{Para(mine)}</div>");
        if (p.Doc.Length > 0) o.Append($"<p class=\"doc\">{H(p.Doc)}</p>");
        if (mine == null && p.Doc.Length == 0 && p.Kind is not ("DUT" or "GVL")) o.Append("<p class=\"muted\">No description.</p>");
        var io = p.Vars.Where(v => v.Section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL" or "RETURN" || p.Kind == "DUT").ToList();
        if (p.Kind == "DUT" && io.Count == 0) io = p.Vars;
        if (io.Count > 0)
        {
            o.Append("<table><tr><th></th><th>Name</th><th>Type</th><th>Description</th></tr>");
            foreach (var v in io.Take(200))
                o.Append($"<tr><td class=\"sec\">{H(SecShort(v.Section))}</td><td>{H(v.Name)}</td><td>{TypeLink(v.Type)}</td><td>{H(v.Comment)}</td></tr>");
            o.Append("</table>");
        }
        if (p.Kind == "DUT" && io.Count == 0) o.Append($"<pre>{H(Trim(p.Decl, 2000))}</pre>");
        if (p.Members.Count > 0)
        {
            o.Append("<ul class=\"members\">");
            foreach (var mk in p.Members)
            {
                var m = Pous[mk];
                var desc = s.Get(mk) ?? m.Doc;
                o.Append($"<li id=\"{Id(mk)}\"><span class=\"k {m.Kind.ToLowerInvariant()}\">{KindShort(m.Kind)}</span> <b>{H(m.Name)}</b>");
                var args = m.Vars.Where(v => v.Section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT").Select(v => v.Name + " : " + v.Type).ToList();
                var rt = m.Vars.FirstOrDefault(v => v.Section == "RETURN")?.Type;
                if (m.Kind != "ACTION" && m.Kind != "TRANSITION") o.Append($"<code>({H(string.Join("; ", args))}){(rt != null ? " : " + H(rt) : "")}</code>");
                if (!string.IsNullOrWhiteSpace(desc)) o.Append($" – {H(desc)}");
                if (m.Calls.Count > 0) o.Append($"<div class=\"calls\">calls {string.Join(", ", m.Calls.Select(Link))}</div>");
                o.Append("</li>");
            }
            o.Append("</ul>");
        }
        var calls = p.Calls.Where(c => !c.StartsWith(p.Name + ".", StringComparison.OrdinalIgnoreCase)).ToList();
        if (calls.Count > 0) o.Append($"<p class=\"calls\"><b>Calls</b> {string.Join(", ", calls.Select(Link))}</p>");
        if (p.External.Count > 0) o.Append($"<p class=\"calls\"><b>Uses (libraries)</b> {H(string.Join(", ", p.External.Take(40)))}</p>");
        var by = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (calledBy.TryGetValue(p.Key, out var b1)) by.UnionWith(b1);
        foreach (var mk in p.Members) if (calledBy.TryGetValue(mk, out var b2)) by.UnionWith(b2.Where(x => !x.StartsWith(p.Name + ".", StringComparison.OrdinalIgnoreCase)));
        if (instUsers.TryGetValue(p.Name, out var users)) by.UnionWith(users);
        by.Remove(p.Key);
        if (by.Count > 0) o.Append($"<p class=\"calls\"><b>Used by</b> {string.Join(", ", by.Take(60).Select(Link))}</p>");
        if (p.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" && (p.Impl.Trim().Length > 0 || p.SfcText != null))
            o.Append($"<details><summary>Code ({p.Lines} lines)</summary><pre>{H(Trim2(p.Decl, 6000))}\n\n{H(Trim2(p.SfcText ?? p.Impl, 12000))}</pre></details>");
        o.Append("</article>");
    }

    static string Trim2(string s, int n) => (s ?? "").Length <= n ? s : s[..n] + "\n... (shortened)";

    static string SecShort(string s) => s switch
    {
        "VAR_INPUT" => "in", "VAR_OUTPUT" => "out", "VAR_IN_OUT" => "in/out", "RETURN" => "return", "VAR_GLOBAL" => "global", _ => ""
    };

    string TypeLink(string type)
    {
        var t = BareType(type);
        return Pous.TryGetValue(t, out var p) && p.Owner == null ? H(type).Replace(H(t), $"<a href=\"#{Id(p.Key)}\">{H(t)}</a>") : H(type);
    }

    const string Css = @"
:root{--bg:#fff;--fg:#1d2330;--muted:#6b7280;--line:#e5e7eb;--accent:#0b63ce;--chip:#eef2f7;--code:#f6f8fa}
@media(prefers-color-scheme:dark){:root{--bg:#14171c;--fg:#e6e8eb;--muted:#9aa3ad;--line:#2b313a;--accent:#5aa2ff;--chip:#232933;--code:#1b2027}}
*{box-sizing:border-box}body{margin:0;font:14px/1.5 'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--fg)}
header{position:sticky;top:0;background:var(--bg);border-bottom:1px solid var(--line);padding:12px 24px;z-index:2}
h1{margin:0;font-size:22px}.meta{margin:2px 0 8px;color:var(--muted)}nav{display:flex;flex-wrap:wrap;gap:14px;align-items:center}
nav a{color:var(--accent);text-decoration:none}nav input{margin-left:auto;padding:4px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg)}
main{max-width:1200px;margin:0 auto;padding:8px 24px 48px}section{padding-top:12px;border-bottom:1px solid var(--line)}h2{font-size:18px}
a{color:var(--accent)}.muted{color:var(--muted)}.k{display:inline-block;font:11px/1.6 Consolas,monospace;padding:0 5px;border-radius:4px;background:var(--chip);color:var(--muted);min-width:28px;text-align:center}
.k.program{color:#b45309}.k.function_block{color:#0b63ce}.k.function{color:#047857}.k.interface{color:#7c3aed}.k.task{color:#be123c}
.tree details{margin-left:14px}.tree summary{cursor:pointer}.tree .leaf{margin-left:30px}.dwrap{border:1px solid var(--line);border-radius:8px;margin:8px 0;background:var(--bg)}
.dtools{display:flex;gap:4px;align-items:center;padding:4px 6px;border-bottom:1px solid var(--line)}
.dtools button{font:12px 'Segoe UI',sans-serif;min-width:30px;padding:2px 8px;border:1px solid var(--line);border-radius:5px;background:var(--chip);color:var(--fg);cursor:pointer}
.dtools button:hover{border-color:var(--accent)}.dtools .zl{margin-left:auto;color:var(--muted);font-size:12px}
.diagram{overflow:auto;padding:8px;max-height:75vh;cursor:grab;resize:vertical}.diagram.drag{cursor:grabbing;user-select:none}
.diagram svg{max-width:none!important;display:block}.diagram pre.mermaid{overflow:visible;background:none;padding:0;margin:0;width:max-content}.dwrap:fullscreen{display:flex;flex-direction:column}.dwrap:fullscreen .diagram{max-height:none;flex:1}
pre{background:var(--code);padding:10px;border-radius:6px;overflow:auto;font:12px/1.45 Consolas,monospace}code{font:12px Consolas,monospace}
.pou{border:1px solid var(--line);border-radius:8px;padding:10px 14px;margin:10px 0}.pou h4{margin:0;font-size:15px}.path{margin:2px 0;color:var(--muted);font-size:12px}
.explain{border-left:3px solid var(--accent);padding-left:10px}.doc{font-style:italic}table{border-collapse:collapse;margin:6px 0;width:100%}
td,th{border-bottom:1px solid var(--line);padding:3px 8px;text-align:left;vertical-align:top}td.sec{color:var(--muted);width:60px}.members{padding-left:18px}.calls{color:var(--muted);font-size:13px}
footer{color:var(--muted);text-align:center;padding:16px;font-size:12px}";

    const string Js = @"
function filter(q){q=q.toLowerCase();document.querySelectorAll('article.pou').forEach(a=>{a.style.display=!q||a.dataset.name.includes(q)?'':'none'})}
if(window.mermaid){mermaid.initialize({startOnLoad:false,securityLevel:'loose',theme:matchMedia('(prefers-color-scheme: dark)').matches?'dark':'default',maxTextSize:200000});
 mermaid.run({querySelector:'pre.mermaid'}).then(zoomAll).catch(e=>{console.error(e);zoomAll()})}
else document.querySelectorAll('pre.mermaid').forEach(p=>p.insertAdjacentHTML('beforebegin','<p class=muted>Diagram needs internet access (Mermaid) - source:</p>'));
// zoom: buttons, Ctrl+wheel (around the cursor), double click; pan: drag with the mouse or the scroll bars
function zoomAll(){document.querySelectorAll('.dwrap').forEach(w=>{const d=w.querySelector('.diagram'),svg=d.querySelector('svg');if(!svg)return;
 const vb=svg.viewBox&&svg.viewBox.baseVal;const W=vb&&vb.width?vb.width:svg.getBBox().width,H=vb&&vb.height?vb.height:svg.getBBox().height;
 svg.removeAttribute('style');let z=1;const lab=w.querySelector('.zl');
 function set(nz,cx,cy){nz=Math.max(.1,Math.min(8,nz));const r=d.getBoundingClientRect();cx=cx??r.width/2;cy=cy??r.height/2;
  const px=(d.scrollLeft+cx)/z,py=(d.scrollTop+cy)/z;z=nz;svg.setAttribute('width',W*z);svg.setAttribute('height',H*z);
  d.scrollLeft=px*z-cx;d.scrollTop=py*z-cy;lab.textContent=Math.round(z*100)+' %'}
 function fit(){const avail=d.clientWidth-16;set(Math.min(1.5,avail/W),0,0);d.scrollLeft=0;d.scrollTop=0}
 fit();
 w.querySelector('.dtools').addEventListener('click',e=>{const b=e.target.closest('button');if(!b)return;const k=b.dataset.z;
  if(k=='in')set(z*1.25);else if(k=='out')set(z/1.25);else if(k=='fit')fit();else if(k=='one')set(1);
  else if(k=='full'){if(document.fullscreenElement)document.exitFullscreen();else w.requestFullscreen&&w.requestFullscreen().then(()=>setTimeout(fit,100))}});
 d.addEventListener('wheel',e=>{if(!e.ctrlKey)return;e.preventDefault();const r=d.getBoundingClientRect();set(z*(e.deltaY<0?1.15:1/1.15),e.clientX-r.left,e.clientY-r.top)},{passive:false});
 d.addEventListener('dblclick',e=>{if(e.target.closest('a'))return;const r=d.getBoundingClientRect();set(z*(e.shiftKey?1/1.5:1.5),e.clientX-r.left,e.clientY-r.top)});
 let drag=null;d.addEventListener('mousedown',e=>{if(e.button!==0||e.target.closest('a'))return;drag={x:e.clientX,y:e.clientY,l:d.scrollLeft,t:d.scrollTop,m:false}});
 window.addEventListener('mousemove',e=>{if(!drag)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;if(Math.abs(dx)+Math.abs(dy)>3){drag.m=true;d.classList.add('drag')}
  d.scrollLeft=drag.l-dx;d.scrollTop=drag.t-dy});
 window.addEventListener('mouseup',()=>{if(drag&&drag.m)setTimeout(()=>d.classList.remove('drag'),0);else d.classList.remove('drag');drag=null});
 d.addEventListener('click',e=>{if(d.classList.contains('drag')){e.preventDefault();e.stopPropagation()}},true)})}";
}

public sealed partial class ToolRegistry
{
    void RegisterDoc()
    {
        Add("tc_doc", "Program documentation of a PLC project (like a project memory): action=outline returns the call hierarchy " +
                      "(task -> program -> FBs/methods/functions), every object with its description, step chains (SFC, CASE state machines) " +
                      "and findings; action=html writes one HTML page with clickable call hierarchy, call graph, class diagram, step chain " +
                      "diagrams, findings and a reference per object (description, inputs/outputs, calls, used by, code); action=memory returns " +
                      "the stored overview and explanations. Workflow: outline -> read important objects (tc_plc_read) -> html with overview " +
                      "(how the program works, for a new colleague) and summaries {object: 1-3 sentences what it does}. Explanations are kept in " +
                      "<solution>\\.tcagent\\doc-summaries.json and reused (pass only new or changed ones).",
            Schema(Req(P("action", "string", "outline, html or memory")),
                   P("plcProjectPath", "string", "PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = the only one)"),
                   P("file", "string", "html: target file (default <solution>\\doc\\<PLC project>.html)"),
                   P("overview", "string", "html: how the program works - tasks, main sequence, important blocks, interfaces to hardware/other systems"),
                   P("summaries", "object", "html: {\"FB_Axis\": \"what it does\", \"FB_Axis.MoveAbs\": \"...\"} - explanations per object (keys as in outline)")),
            a => Doc(a));
    }

    string Doc(JsonObject a)
    {
        var action = (Str(a, "action") ?? "outline").ToLowerInvariant();
        var dir = SolutionDir();
        var mem = PlcDoc.Summaries.Load(dir);
        if (action == "memory")
        {
            if (mem.Overview.Length == 0 && mem.Pous.Count == 0) return "No project memory yet - create it with tc_doc action=outline, then action=html with overview and summaries.";
            return $"Overview:\n{mem.Overview}\n\nExplanations ({mem.Pous.Count}):\n" + string.Join("\n", mem.Pous.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));
        }
        var doc = BuildDoc(Str(a, "plcProjectPath"));
        if (action == "outline") return doc.Outline(mem);
        if (action != "html") throw new Exception("action: outline, html or memory");
        if (Str(a, "overview") is { } ov && ov.Trim().Length > 0) mem.Overview = ov.Trim();
        if (a["summaries"] is JsonObject sums) foreach (var kv in sums) if (kv.Value != null) mem.Pous[kv.Key] = kv.Value.ToString().Trim();
        if (dir != null) mem.Save(dir);
        var file = Str(a, "file") ?? Path.Combine(dir ?? Path.GetTempPath(), "doc", doc.Project + ".html");
        file = dir != null ? Path.GetFullPath(file, dir) : Path.GetFullPath(file);     // relative to the solution, not to the server's working directory
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, doc.Html(mem, "TcAgent " + Program.ServerVersion), new UTF8Encoding(false));
        var missing = doc.Pous.Values.Count(p => p.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "INTERFACE" && p.Doc.Length == 0 && mem.Get(p.Key) == null);
        return $"Documentation written: {file} ({new FileInfo(file).Length / 1024} KB)" +
               (missing > 0 ? $"\n{missing} POUs/interfaces still without description or explanation - add summaries and call again." : "") +
               $"\nProject memory: {PlcDoc.Summaries.FileFor(dir)}";
    }

    internal PlcDoc BuildDoc(string plcProjectPath) => tc.Sta.Run(() =>
    {
        var plc = PlcProject(plcProjectPath);
        var d = PlcDoc.Collect(tc, plc, SfcForDoc);
        d.Analyze();
        return d;
    }, 600_000);

    // SFC of one POU for the documentation: chain text and step-to-step edges
    internal static (string Text, List<(string, string, string)> Edges) SfcForDoc(ITcSmTreeItem plc, string pouPath)
    {
        var plcPath = plc.PathName;
        var selection = string.Join(".", pouPath[(plcPath.Length + 1)..].Split('^'));
        var pouName = pouPath.Split('^').Last();
        var tmp = Path.Combine(Path.GetTempPath(), $"tcagent-doc-{Guid.NewGuid():N}.xml");
        try
        {
            ComCall.Invoke(plc, "PlcOpenExport", tmp, selection);
            var x = XDocument.Load(tmp);
            var pou = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "pou" && (string)e.Attribute("name") == pouName) ?? throw new Exception("not in the export");
            var body = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
            var sfc = body?.Elements().FirstOrDefault(e => e.Name.LocalName == "SFC");
            if (sfc == null)
            {
                var b = body?.Elements().FirstOrDefault();
                throw new Exception("body:" + (b?.Name.LocalName ?? "none"));
            }
            return (SfcToText(sfc), SfcEdges(sfc));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // transitions as step -> step edges (through divergences/convergences; jumps to their target)
    internal static List<(string, string, string)> SfcEdges(XElement sfc)
    {
        var g = Graph(sfc);
        var preds = new Dictionary<string, List<string>>();
        foreach (var (id, e) in g.Nodes) preds[id] = Inputs(e).Where(g.Nodes.ContainsKey).ToList();
        string StepName(XElement e) => e.Name.LocalName == "step" ? (string)e.Attribute("name") : e.Name.LocalName == "jumpStep" ? (string)e.Attribute("targetName") : null;
        IEnumerable<string> Back(string id, int d)
        {
            if (d > 50 || !g.Nodes.TryGetValue(id, out var e)) yield break;
            if (e.Name.LocalName == "step") { yield return StepName(e); yield break; }
            if (e.Name.LocalName == "transition") yield break;
            foreach (var p in preds[id]) foreach (var s in Back(p, d + 1)) yield return s;
        }
        IEnumerable<string> Fwd(string id, int d)
        {
            if (d > 50 || !g.Nodes.TryGetValue(id, out var e)) yield break;
            if (e.Name.LocalName is "step" or "jumpStep") { yield return StepName(e); yield break; }
            if (e.Name.LocalName == "transition" && d > 0) yield break;
            if (g.Succ.TryGetValue(id, out var s)) foreach (var n in s) foreach (var x in Fwd(n, d + 1)) yield return x;
        }
        var edges = new List<(string, string, string)>();
        foreach (var (id, e) in g.Nodes.Where(n => n.Value.Name.LocalName == "transition"))
        {
            var cond = Condition(e);
            foreach (var f in preds[id].SelectMany(p => Back(p, 0)).Distinct())
                foreach (var t in Fwd(id, 0).Distinct())
                    edges.Add((f, t, cond));
        }
        return edges;
    }
}
