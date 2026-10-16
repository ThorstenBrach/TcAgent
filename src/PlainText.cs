// PLC code as plain-text ST files and back (TcAgent.exe st export|check|import, tool tc_plaintext).
//
// Works on the project files only (.plcproj, .TcPOU, .TcGVL, .TcDUT, .TcIO) - no IDE, fast, CI friendly.
// One .st file per object: <out>\<PLC project>\<folder as in the plcproj>\<Name>.st with TwinCAT's own declaration and
// implementation text plus END_ markers; methods, properties and actions inside the POU file. Format: docs/PLAINTEXT.md.
// Next to the code folders: <out>\<PLC project>\references.json with the library references of the .plcproj (export/check
// only - the import ignores it).
//
// The XML is read and changed on the string level (never re-serialized): only the CDATA of changed declarations and
// implementations is replaced, new methods/properties/actions/accessors are inserted, the LineIds of changed blocks are
// removed. Everything else - Ids, attributes, order, graphical implementations (SFC, LD ...), transitions, folders,
// ObjectProperties, BOM, line ends - stays byte-identical, so importing an unchanged export changes nothing.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class PlainText
{
    public sealed class PlainTextException : Exception
    {
        public PlainTextException(string message) : base(message) { }
    }

    // ------------------------------------------------------------------ text helpers
    /// <summary>Line ends normalized to LF.</summary>
    static string N(string t) => t.Replace("\r\n", "\n").Replace("\r", "\n");
    /// <summary>Text as written to the .st file: empty, or ending with a line end.</summary>
    static string E(string t) => t.Length == 0 || t.EndsWith('\n') ? t : t + "\n";
    /// <summary>Inverse of E for a text without trailing line end: E(R(u)) == u for every u that is empty or ends with "\n".</summary>
    static string R(string u) => u.Length >= 2 && u[^1] == '\n' && u[^2] != '\n' ? u[..^1] : u;
    static List<string> Lines(string t) => t.Length == 0 ? new List<string>() : t[..^1].Split('\n').ToList();
    static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    static string Cdata(string t) => "<![CDATA[" + t.Replace("]]>", "]]]]><![CDATA[>") + "]]>";

    static readonly Regex EntityRx = new("&(lt|gt|amp|quot|apos|#[0-9]+|#x[0-9a-fA-F]+);", RegexOptions.CultureInvariant);

    static string Unescape(string t) => EntityRx.Replace(t, m =>
    {
        var e = m.Groups[1].Value;
        return e switch
        {
            "lt" => "<", "gt" => ">", "amp" => "&", "quot" => "\"", "apos" => "'",
            _ => e.StartsWith("#x", StringComparison.Ordinal) ? char.ConvertFromUtf32(Convert.ToInt32(e[2..], 16)) : char.ConvertFromUtf32(int.Parse(e[1..], System.Globalization.CultureInfo.InvariantCulture))
        };
    });

    static string Esc(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static bool At(string s, int i, string x) => i >= 0 && i + x.Length <= s.Length && string.CompareOrdinal(s, i, x, 0, x.Length) == 0;

    // ------------------------------------------------------------------ string-level XML scanner (positions, no re-serialization)
    sealed class Tag
    {
        public string Name;
        public int Start, OpenEnd, CloseStart, End;
        public bool SelfClosing;
        public readonly Dictionary<string, string> Attrs = new();
        public readonly List<Tag> Children = new();
        public Tag Parent;
        public Tag(string name, int start, int openEnd) { Name = name; Start = start; OpenEnd = openEnd; CloseStart = openEnd; End = openEnd; }
        public string Attr(string n) => Attrs.TryGetValue(n, out var v) ? v : null;
        public Tag Child(string n) => Children.FirstOrDefault(c => c.Name == n);
    }

    static readonly Regex AttrRx = new("([^\\s=]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.CultureInvariant);
    static readonly Regex TagNameRx = new("[^\\s/>]+", RegexOptions.CultureInvariant);

    static Tag ParseXml(string s)
    {
        var root = new Tag("#document", 0, 0);
        var stack = new List<Tag> { root };
        int i = 0, n = s.Length;
        while (true)
        {
            i = s.IndexOf('<', i);
            if (i < 0) break;
            int j;
            if (At(s, i, "<?"))
            {
                j = s.IndexOf("?>", i, StringComparison.Ordinal);
                if (j < 0) throw new PlainTextException("XML: unterminated <?");
                i = j + 2; continue;
            }
            if (At(s, i, "<!--"))
            {
                j = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (j < 0) throw new PlainTextException("XML: unterminated comment");
                i = j + 3; continue;
            }
            if (At(s, i, "<![CDATA["))
            {
                j = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                if (j < 0) throw new PlainTextException("XML: unterminated CDATA");
                i = j + 3; continue;
            }
            if (At(s, i, "<!"))
            {
                j = s.IndexOf('>', i);
                if (j < 0) throw new PlainTextException("XML: unterminated <!");
                i = j + 1; continue;
            }
            if (At(s, i, "</"))
            {
                j = s.IndexOf('>', i);
                if (j < 0) throw new PlainTextException("XML: unterminated end tag");
                var endName = s[(i + 2)..j].Trim();
                if (stack.Count == 1) throw new PlainTextException("XML: unexpected </" + endName + ">");
                var top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (top.Name != endName) throw new PlainTextException("XML: </" + endName + "> does not close <" + top.Name + ">");
                top.CloseStart = i; top.End = j + 1;
                i = j + 1; continue;
            }
            j = i + 1;
            char quote = '\0';
            while (j < n)
            {
                var c = s[j];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '>') break;
                j++;
            }
            if (j >= n) throw new PlainTextException("XML: unterminated start tag");
            var tag = s[(i + 1)..j];
            bool selfClosing = tag.EndsWith('/');
            if (selfClosing) tag = tag[..^1];
            var m = TagNameRx.Match(tag);
            if (!m.Success || m.Index != 0) throw new PlainTextException("XML: tag without name");
            var node = new Tag(m.Value, i, j + 1);
            foreach (Match a in AttrRx.Matches(tag[m.Value.Length..]))
                node.Attrs[a.Groups[1].Value] = Unescape(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value);
            node.Parent = stack[^1];
            stack[^1].Children.Add(node);
            if (selfClosing) node.SelfClosing = true;
            else stack.Add(node);
            i = j + 1;
        }
        if (stack.Count != 1) throw new PlainTextException("XML: <" + stack[^1].Name + "> is not closed");
        return root;
    }

    /// <summary>Text of an element: CDATA sections joined (a "]]>" in the code is split over two sections), other text unescaped.</summary>
    static string ElementText(string s, Tag node)
    {
        var content = s[node.OpenEnd..node.CloseStart];
        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            int j = content.IndexOf("<![CDATA[", i, StringComparison.Ordinal);
            if (j < 0) { sb.Append(Unescape(content[i..])); break; }
            sb.Append(Unescape(content[i..j]));
            int k = content.IndexOf("]]>", j + 9, StringComparison.Ordinal);
            if (k < 0) throw new PlainTextException("XML: unterminated CDATA");
            sb.Append(content, j + 9, k - j - 9);
            i = k + 3;
        }
        return sb.ToString();
    }

    /// <summary>Start of the line of pos if only blanks/tabs precede pos on that line, otherwise pos.</summary>
    static int LineStart(string s, int pos)
    {
        int k = pos;
        while (k > 0 && (s[k - 1] == ' ' || s[k - 1] == '\t')) k--;
        return k == 0 || s[k - 1] == '\n' ? k : pos;
    }

    static string LineIndent(string s, int pos) => s[LineStart(s, pos)..pos];

    sealed class Edit
    {
        public int Start, End; public string Text;
        public Edit(int start, int end, string text) { Start = start; End = end; Text = text; }
    }

    static string ApplyEdits(string s, List<Edit> edits)
    {
        // zero-length insertions at the same position keep the order in which they were added
        var merged = new List<Edit>();
        foreach (var e in edits)
        {
            var same = merged.FirstOrDefault(m => m.Start == e.Start && m.End == e.End && e.Start == e.End);
            if (same != null) same.Text += e.Text;
            else merged.Add(new Edit(e.Start, e.End, e.Text));
        }
        merged = merged.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
        for (int k = 1; k < merged.Count; k++)
            if (merged[k].Start < merged[k - 1].End || (merged[k].Start == merged[k - 1].Start && merged[k - 1].Start != merged[k - 1].End))
                throw new PlainTextException("internal: overlapping edits");
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var e in merged)
        {
            sb.Append(s, pos, e.Start - pos).Append(e.Text);
            pos = e.End;
        }
        sb.Append(s, pos, s.Length - pos);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ object model (from the TcPOU/TcGVL/TcDUT/TcIO XML)
    sealed class Block
    {
        public string Kind, Name, ImplLang, Decl, Impl, LineIdsName;
        public Tag Node, DeclNode, StNode;
        public readonly List<Block> Subs = new();
    }

    static readonly Dictionary<string, string> ObjectKinds = new() { ["POU"] = "POU", ["GVL"] = "GVL", ["DUT"] = "DUT", ["Itf"] = "ITF" };
    static readonly Dictionary<string, string> SubKinds = new() { ["Method"] = "METHOD", ["Property"] = "PROPERTY", ["Action"] = "ACTION" };
    static readonly Dictionary<string, string> AccKinds = new() { ["Get"] = "GET", ["Set"] = "SET" };

    static Block MakeBlock(string s, Tag node, string kind, string name, string lineIds)
    {
        var b = new Block { Kind = kind, Name = name, Node = node, LineIdsName = lineIds };
        b.DeclNode = node.Child("Declaration");
        b.Decl = b.DeclNode != null ? ElementText(s, b.DeclNode) : null;
        var impl = node.Child("Implementation");
        if (impl != null && impl.Children.Count > 0)
        {
            var first = impl.Children[0];
            b.ImplLang = first.Name;
            if (first.Name == "ST")
            {
                b.StNode = first;
                b.Impl = ElementText(s, first);
            }
        }
        return b;
    }

    static Block ObjectFromXml(string s)
    {
        var root = ParseXml(s);
        var tcp = root.Child("TcPlcObject") ?? throw new PlainTextException("not a TwinCAT PLC object file (no <TcPlcObject>)");
        var node = tcp.Children.FirstOrDefault(c => ObjectKinds.ContainsKey(c.Name)) ?? throw new PlainTextException("no POU, GVL, DUT or interface in the file");
        var kind = ObjectKinds[node.Name];
        var name = node.Attr("Name") ?? "";
        var obj = MakeBlock(s, node, kind, name, kind == "POU" ? name : null);
        if (kind is "POU" or "ITF")
            foreach (var c in node.Children)
            {
                if (!SubKinds.ContainsKey(c.Name)) continue;
                if (kind == "ITF" && c.Name == "Action") continue;
                var sn = c.Attr("Name") ?? "";
                var sub = MakeBlock(s, c, SubKinds[c.Name], sn, kind == "POU" ? name + "." + sn : null);
                if (c.Name == "Property")
                    foreach (var a in c.Children)
                    {
                        if (!AccKinds.ContainsKey(a.Name)) continue;
                        var an = a.Attr("Name") ?? a.Name;
                        sub.Subs.Add(MakeBlock(s, a, AccKinds[a.Name], an, kind == "POU" ? name + "." + sn + "." + an : null));
                    }
                obj.Subs.Add(sub);
            }
        return obj;
    }

    // ------------------------------------------------------------------ regular expressions (identical in the Python test port)
    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex SepRx = new(@"^\s*// @implementation(?:\s+(\S+).*?)?\s*$", RegexOptions.CultureInvariant);
    static readonly Regex EndVarRx = new(@"^\s*END_VAR\b", IC);
    static readonly Regex PouHeaderRx = new(@"^\s*(FUNCTION_BLOCK|PROGRAM|FUNCTION)\b", IC);
    static readonly Regex MethodHeaderRx = new(@"^\s*METHOD\b", IC);
    static readonly Regex MainEndRx = new(@"^END_(FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE)\s*$", IC);
    static readonly Regex SubEndRx = new(@"^END_(METHOD|PROPERTY|ACTION)\s*$", IC);
    static readonly Regex AccRx = new(@"^(GET|SET)\s*$", IC);
    static readonly Regex AccEndRx = new(@"^END_(GET|SET)\s*$", IC);
    static readonly Regex MethodNameRx = new(@"^\s*METHOD\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT|OVERRIDE)\s+)*([A-Za-z_]\w*)", IC);
    static readonly Regex PropNameRx = new(@"^\s*PROPERTY\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT|OVERRIDE)\s+)*([A-Za-z_]\w*)", IC);
    static readonly Regex ActionRx = new(@"^\s*ACTION\s+([A-Za-z_]\w*)\s*:?\s*$", IC);
    static readonly Regex HeaderNameRx = new(@"^\s*(?:FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE)\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*([A-Za-z_]\w*)", IC);
    static readonly Regex TypeNameRx = new(@"^\s*TYPE\s+([A-Za-z_]\w*)", IC);
    static readonly Regex KindRx = new(@"^\s*(FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE|TYPE|VAR_GLOBAL)\b", IC);

    static string FirstMatch(Regex rx, IEnumerable<string> lines)
    {
        foreach (var l in lines)
        {
            var m = rx.Match(l);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    static string PouKeyword(string decl) => FirstMatch(PouHeaderRx, Lines(E(N(decl ?? ""))))?.ToUpperInvariant() ?? "FUNCTION_BLOCK";

    static string NonStMarker(string lang) => "// @implementation " + lang + " - not plain text, kept as it is in the project";

    // ------------------------------------------------------------------ declaration / implementation split
    /// <summary>Splits a block body (POU, METHOD, GET, SET) into declaration and implementation: at an explicit
    /// "// @implementation" line, otherwise after the last END_VAR line, otherwise after the header line (GET/SET: all
    /// implementation). Lang != null: the separator names a graphical language (implementation not in the text).</summary>
    static (string D, string I, string Lang) Split(string body, string kind)
    {
        var lines = Lines(body);
        for (int k = 0; k < lines.Count; k++)
        {
            var m = SepRx.Match(lines[k]);
            if (m.Success) return (Join(lines.Take(k)), Join(lines.Skip(k + 1)), m.Groups[1].Success ? m.Groups[1].Value : null);
        }
        int last = -1;
        for (int k = 0; k < lines.Count; k++)
            if (EndVarRx.IsMatch(lines[k])) last = k;
        if (last >= 0) return (Join(lines.Take(last + 1)), Join(lines.Skip(last + 1)), null);
        if (kind is "GET" or "SET") return ("", body, null);
        var hrx = kind == "POU" ? PouHeaderRx : MethodHeaderRx;
        for (int k = 0; k < lines.Count; k++)
            if (hrx.IsMatch(lines[k])) return (Join(lines.Take(k + 1)), Join(lines.Skip(k + 1)), null);
        return (body, "", null);
    }

    // ------------------------------------------------------------------ export: object -> .st text (LF here, written with CRLF)
    static string RenderBody(Block b)
    {
        var d = E(N(b.Decl ?? ""));
        if (b.ImplLang != null && b.ImplLang != "ST") return d + NonStMarker(b.ImplLang) + "\n";
        var i = E(N(b.Impl ?? ""));
        var (d2, i2, lang) = Split(d + i, b.Kind);
        if (lang == null && d2 == d && i2 == i) return d + i;
        return d + "// @implementation\n" + i;
    }

    static string RenderAction(Block b) =>
        b.ImplLang != null && b.ImplLang != "ST" ? NonStMarker(b.ImplLang) + "\n" : E(N(b.Impl ?? ""));

    static string RenderFile(Block obj)
    {
        if (obj.Kind is "GVL" or "DUT") return E(N(obj.Decl ?? ""));
        bool itf = obj.Kind == "ITF";
        var sb = new StringBuilder();
        if (itf) sb.Append(E(N(obj.Decl ?? ""))).Append("END_INTERFACE\n");
        else sb.Append(RenderBody(obj)).Append("END_").Append(PouKeyword(obj.Decl)).Append('\n');
        foreach (var s in obj.Subs)
        {
            sb.Append('\n');
            if (s.Kind == "METHOD") sb.Append(itf ? E(N(s.Decl ?? "")) : RenderBody(s)).Append("END_METHOD\n");
            else if (s.Kind == "ACTION") sb.Append("ACTION ").Append(s.Name).Append(":\n").Append(RenderAction(s)).Append("END_ACTION\n");
            else
            {
                sb.Append(E(N(s.Decl ?? "")));
                foreach (var a in s.Subs)
                    sb.Append(a.Kind).Append('\n').Append(itf ? E(N(a.Decl ?? "")) : RenderBody(a)).Append("END_").Append(a.Kind).Append('\n');
                sb.Append("END_PROPERTY\n");
            }
        }
        return sb.ToString();
    }

    static int CountNonSt(Block b) => (b.ImplLang != null && b.ImplLang != "ST" ? 1 : 0) + b.Subs.Sum(CountNonSt);

    // ------------------------------------------------------------------ import: .st text -> parsed blocks
    sealed class PBlock
    {
        public string Kind, Name, D, I, Lang;
        public int Line;
        /// <summary>Main block written before its sub-blocks (methods inside FUNCTION_BLOCK..END_FUNCTION_BLOCK): trailing
        /// blank lines are not compared.</summary>
        public bool TrimTail;
        public readonly List<PBlock> Accessors = new();
        public PBlock(string kind, string name, int line) { Kind = kind; Name = name; Line = line; }
    }

    // a sub-block header in column 1 (strict, so that variables named method/action/get/set do not match)
    static readonly Regex SubStartRx = new(@"^(?:(?:METHOD|PROPERTY)\s+[A-Za-z_]|ACTION\s+[A-Za-z_]\w*\s*:?\s*$)", IC);
    static readonly Regex CommentLineRx = new(@"^\s*(?://|\(\*|\{)", RegexOptions.CultureInvariant);
    // safety net: block keywords that must never end up inside a declaration or implementation text
    static readonly Regex NetRx = new(@"^\s*(?:(METHOD|PROPERTY)\s+[A-Za-z_]|ACTION\s+[A-Za-z_]\w*\s*:?\s*$|(?:GET|SET)\s*$|END_(?:METHOD|PROPERTY|ACTION|FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE|GET|SET)\b)", IC);

    /// <summary>Refuses a text segment (lines starting at file line start+1) that contains a block keyword line - only the
    /// first METHOD/PROPERTY header of its own block (allowed) may appear.</summary>
    static void CheckSegment(List<string> lines, int start, string allowed)
    {
        bool used = false;
        for (int k = 0; k < lines.Count; k++)
        {
            var m = NetRx.Match(lines[k]);
            if (!m.Success) continue;
            if (allowed != null && !used && m.Groups[1].Success && m.Groups[1].Value.ToUpperInvariant() == allowed) { used = true; continue; }
            throw new PlainTextException($"line {start + k + 1}: '{lines[k].Trim()}' inside a block - every METHOD, PROPERTY and ACTION needs its own END_METHOD / END_PROPERTY / END_ACTION line (column 1)");
        }
    }

    static List<string> StripBlankTail(List<string> lines)
    {
        int n = lines.Count;
        while (n > 0 && string.IsNullOrWhiteSpace(lines[n - 1])) n--;
        return lines.GetRange(0, n);
    }

    static (PBlock Main, List<PBlock> Subs) ParseSt(string text, string kind)
    {
        var t = E(N(text));
        var main = new PBlock(kind, null, 1);
        var subs = new List<PBlock>();
        var lines = Lines(t);
        if (kind is "GVL" or "DUT")
        {
            CheckSegment(lines, 0, null);
            main.D = t;
            return (main, subs);
        }
        bool itf = kind == "ITF";
        var missingEnd = "line 1: " + (itf ? "END_INTERFACE" : "END_FUNCTION_BLOCK / END_PROGRAM / END_FUNCTION") + " (own line, column 1) is missing";
        int e = lines.FindIndex(l => MainEndRx.IsMatch(l));
        if (e < 0) throw new PlainTextException(missingEnd);
        // IEC 61131-3 3rd edition layout: methods/properties/actions before END_FUNCTION_BLOCK. The first sub-block header
        // ends the body; comment/attribute lines directly above it belong to the sub-block, blank lines to neither.
        int h = lines.FindIndex(0, e, l => SubStartRx.IsMatch(l));
        List<string> bodyLines;
        bool mainDone;
        int i;
        if (h >= 0)
        {
            int start = h;
            while (start > 0 && CommentLineRx.IsMatch(lines[start - 1]) && !SepRx.IsMatch(lines[start - 1])) start--;
            bodyLines = StripBlankTail(lines.GetRange(0, start));
            main.TrimTail = true;
            mainDone = false;
            i = start;
        }
        else
        {
            bodyLines = lines.GetRange(0, e);
            mainDone = true;
            i = e + 1;
        }
        CheckSegment(bodyLines, 0, null);
        var body = Join(bodyLines);
        if (itf) main.D = body;
        else (main.D, main.I, main.Lang) = Split(body, "POU");
        while (true)
        {
            while (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Count) break;
            if (MainEndRx.IsMatch(lines[i]))
            {
                if (mainDone) throw new PlainTextException($"line {i + 1}: second {lines[i].Trim()}");
                mainDone = true; i++; continue;
            }
            int j = i;
            while (j < lines.Count && !SubEndRx.IsMatch(lines[j]))
            {
                if (MainEndRx.IsMatch(lines[j]))
                    throw new PlainTextException($"line {j + 1}: {lines[j].Trim()} inside the block that starts at line {i + 1} - END_METHOD / END_PROPERTY / END_ACTION missing");
                j++;
            }
            if (j >= lines.Count)
                throw new PlainTextException($"line {i + 1}: a METHOD, PROPERTY or ACTION block ending with END_METHOD / END_PROPERTY / END_ACTION (own line, column 1) is expected");
            var k = SubEndRx.Match(lines[j]).Groups[1].Value.ToUpperInvariant();
            var region = lines.GetRange(i, j - i);
            PBlock p;
            if (k == "METHOD")
            {
                var name = FirstMatch(MethodNameRx, region) ?? throw new PlainTextException($"line {i + 1}: block ends with END_METHOD but has no 'METHOD <name>' line");
                CheckSegment(region, i, "METHOD");
                p = new PBlock("METHOD", name, i + 1);
                var mbody = Join(region);
                if (itf) p.D = mbody;
                else (p.D, p.I, p.Lang) = Split(mbody, "METHOD");
            }
            else if (k == "ACTION")
            {
                if (itf) throw new PlainTextException($"line {i + 1}: an interface has no actions");
                var m = region.Count > 0 ? ActionRx.Match(region[0]) : null;
                if (m == null || !m.Success) throw new PlainTextException($"line {i + 1}: block ends with END_ACTION but does not start with 'ACTION <name>:'");
                p = new PBlock("ACTION", m.Groups[1].Value, i + 1);
                var rest = region.Skip(1).ToList();
                CheckSegment(rest, i + 1, null);
                var sm = rest.Count == 1 ? SepRx.Match(rest[0]) : null;
                if (sm != null && sm.Success && sm.Groups[1].Success) { p.I = ""; p.Lang = sm.Groups[1].Value; }
                else p.I = Join(rest);
            }
            else
            {
                int a = region.FindIndex(l => AccRx.IsMatch(l));
                if (a < 0) a = region.Count;
                var name = FirstMatch(PropNameRx, region.Take(a)) ?? throw new PlainTextException($"line {i + 1}: block ends with END_PROPERTY but has no 'PROPERTY <name>' line");
                CheckSegment(region.GetRange(0, a), i, "PROPERTY");
                p = new PBlock("PROPERTY", name, i + 1) { D = Join(region.Take(a)) };
                int q = a;
                while (q < region.Count)
                {
                    if (string.IsNullOrWhiteSpace(region[q])) { q++; continue; }
                    var am = AccRx.Match(region[q]);
                    if (!am.Success) throw new PlainTextException($"line {i + q + 1}: GET or SET (own line) expected in PROPERTY {name}");
                    var acc = am.Groups[1].Value.ToUpperInvariant();
                    int r = q + 1;
                    while (r < region.Count && !AccEndRx.IsMatch(region[r])) r++;
                    if (r >= region.Count) throw new PlainTextException($"line {i + q + 1}: END_{acc} is missing");
                    if (AccEndRx.Match(region[r]).Groups[1].Value.ToUpperInvariant() != acc) throw new PlainTextException($"line {i + r + 1}: END_{acc} expected");
                    if (p.Accessors.Any(x => x.Kind == acc)) throw new PlainTextException($"line {i + q + 1}: PROPERTY {name} has two {acc} blocks");
                    CheckSegment(region.GetRange(q + 1, r - q - 1), i + q + 1, null);
                    var ab = new PBlock(acc, acc == "GET" ? "Get" : "Set", i + q + 1);
                    var accBody = Join(region.Skip(q + 1).Take(r - q - 1));
                    if (itf) ab.D = accBody;
                    else (ab.D, ab.I, ab.Lang) = Split(accBody, acc);
                    p.Accessors.Add(ab);
                    q = r + 1;
                }
            }
            if (subs.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                throw new PlainTextException($"line {i + 1}: {p.Name} is defined twice");
            subs.Add(p);
            i = j + 1;
        }
        if (!mainDone) throw new PlainTextException(missingEnd);
        return (main, subs);
    }

    // ------------------------------------------------------------------ import: merge parsed blocks into the XML (string level)
    static string NewGuid() => Guid.NewGuid().ToString("B");    // {xxxxxxxx-xxxx-...} lower case, like TwinCAT

    /// <summary>New XML text of a block: the trailing line end and LF-only line ends of the original are kept
    /// (TwinCAT: declarations end with a line end, implementations do not); otherwise CRLF.</summary>
    static string Fit(string u, string orig, bool defaultNl)
    {
        bool keepNl = string.IsNullOrEmpty(orig) ? defaultNl : N(orig).EndsWith('\n');
        var t = keepNl ? u : R(u);
        bool lfOnly = orig != null && orig.Contains('\n') && !orig.Contains("\r\n");
        return lfOnly ? t : t.Replace("\n", "\r\n");
    }

    /// <summary>A changed block of an existing object: Path = sub-object names below the object ([] = the object itself,
    /// [method], [property, Get]); Decl/Impl = new text or null (unchanged).</summary>
    public sealed class BlockEdit
    {
        public List<string> Path; public string Decl, Impl;
        public BlockEdit(List<string> path, string decl, string impl) { Path = path; Decl = decl; Impl = impl; }
    }

    /// <summary>A new method/property/action/accessor (Kind METHOD, PROPERTY, ACTION, GET, SET).</summary>
    public sealed class NewBlock
    {
        public List<string> Path; public string Kind, Decl, Impl;
        public NewBlock(List<string> path, string kind, string decl, string impl) { Path = path; Kind = kind; Decl = decl; Impl = impl; }
    }

    public sealed class UpdateResult
    {
        public string NewXml;
        public readonly List<string> Changed = new(), Added = new(), Kept = new(), Warnings = new(), Errors = new();
        public readonly List<BlockEdit> Edits = new();
        public readonly List<NewBlock> New = new();
    }

    static Edit ReplaceText(Tag node, string text) =>
        node.SelfClosing ? new Edit(node.Start, node.End, "<" + node.Name + ">" + Cdata(text) + "</" + node.Name + ">")
                         : new Edit(node.OpenEnd, node.CloseStart, Cdata(text));

    static readonly Dictionary<string, string> XmlTags = new() { ["METHOD"] = "Method", ["PROPERTY"] = "Property", ["ACTION"] = "Action", ["GET"] = "Get", ["SET"] = "Set" };

    static (List<string> Head, string Tail) ElementLines(string kind, string name, string decl, string impl, bool itf, string ind)
    {
        var tag = XmlTags[kind];
        var head = new List<string> { ind + "<" + tag + " Name=\"" + Esc(name) + "\" Id=\"" + NewGuid() + "\">" };
        if (kind != "ACTION") head.Add(ind + "  <Declaration>" + Cdata(decl) + "</Declaration>");
        if (!itf && kind != "PROPERTY")
        {
            head.Add(ind + "  <Implementation>");
            head.Add(ind + "    <ST>" + Cdata(impl) + "</ST>");
            head.Add(ind + "  </Implementation>");
        }
        return (head, ind + "</" + tag + ">");
    }

    static string NewSubText(PBlock p, bool itf, string ind, string nl, UpdateResult res, string label)
    {
        if (p.Lang != null) throw new PlainTextException($"{label}: a new {p.Kind} with a {p.Lang} implementation cannot be created from text");
        var decl = p.D != null ? Fit(p.D, null, true) : "";
        var impl = Fit(p.I ?? "", null, false);
        var (head, tail) = ElementLines(p.Kind, p.Name, decl, impl, itf, ind);
        var lines = new List<string>(head);
        res.New.Add(new NewBlock(new List<string> { p.Name }, p.Kind, p.Kind == "ACTION" ? null : decl, itf || p.Kind == "PROPERTY" ? null : impl));
        if (p.Kind == "PROPERTY")
            foreach (var a in p.Accessors)
            {
                if (a.Lang != null) throw new PlainTextException($"{label}.{a.Name}: a new accessor with a {a.Lang} implementation cannot be created from text");
                var ad = Fit(a.D, null, true);
                var ai = Fit(a.I ?? "", null, false);
                var (ah, at) = ElementLines(a.Kind, a.Name, ad, ai, itf, ind + "  ");
                lines.AddRange(ah);
                lines.Add(at);
                res.New.Add(new NewBlock(new List<string> { p.Name, a.Name }, a.Kind, ad, itf ? null : ai));
            }
        lines.Add(tail);
        return string.Join(nl, lines);
    }

    static UpdateResult UpdateObject(string s, Block obj, PBlock main, List<PBlock> subs)
    {
        var res = new UpdateResult();
        var nl = s.Contains("\r\n") ? "\r\n" : "\n";
        bool itf = obj.Kind == "ITF";
        var edits = new List<Edit>();
        var lineIds = new HashSet<string>();

        // trimTail: body written before the sub-blocks (methods inside FUNCTION_BLOCK..END_FUNCTION_BLOCK) - its trailing
        // blank lines cannot be told apart from the separation, so they do not count as a change
        static bool Same(string p, string orig, bool trimTail)
        {
            var o = E(N(orig));
            return p == o || (trimTail && Join(StripBlankTail(Lines(p))) == Join(StripBlankTail(Lines(o))));
        }

        void Cmp(Block xb, string pd, string pi, string plang, string label, List<string> path, bool trimTail = false)
        {
            string newDecl = null, newImpl = null;
            if (pd != null)
            {
                if (xb.DeclNode != null) { if (!Same(pd, xb.Decl, trimTail)) newDecl = Fit(pd, xb.Decl, true); }
                else if (pd != "") res.Warnings.Add($"{label}: has no declaration part in the project - the text is ignored");
            }
            if (pi != null)
            {
                if (xb.ImplLang == "ST")
                {
                    if (plang != null) res.Warnings.Add($"{label}: marked as {plang} in the text file but ST in the project - implementation kept");
                    else if (!Same(pi, xb.Impl, trimTail)) newImpl = Fit(pi, xb.Impl, false);
                }
                else if (xb.ImplLang != null)
                {
                    if (plang == null && pi != "") res.Warnings.Add($"{label}: the implementation is {xb.ImplLang} in the project - the text is ignored");
                }
                else if (plang == null && pi != "")
                    res.Warnings.Add($"{label}: has no implementation part in the project - the text is ignored");
            }
            if (newDecl != null) edits.Add(ReplaceText(xb.DeclNode, newDecl));
            if (newImpl != null) edits.Add(ReplaceText(xb.StNode, newImpl));
            if (newDecl != null || newImpl != null)
            {
                res.Changed.Add(label + (newDecl != null ? " (declaration" + (newImpl != null ? " + implementation)" : ")") : " (implementation)"));
                if (xb.LineIdsName != null) lineIds.Add(xb.LineIdsName.ToLowerInvariant());
                res.Edits.Add(new BlockEdit(path, newDecl, newImpl));
            }
        }

        // the object itself
        if (obj.Kind is "POU" or "ITF" or "DUT")
        {
            var hn = FirstMatch(obj.Kind == "DUT" ? TypeNameRx : HeaderNameRx, Lines(main.D));
            if (hn == null)
            {
                res.Errors.Add($"no '{(obj.Kind == "DUT" ? "TYPE" : itf ? "INTERFACE" : "FUNCTION_BLOCK / PROGRAM / FUNCTION")} <name>' line");
                return res;
            }
            if (!hn.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))
            {
                res.Errors.Add($"the declaration names {hn}, the object is {obj.Name} - renaming is not supported");
                return res;
            }
        }
        Cmp(obj, main.D, obj.Kind != "POU" ? null : main.I, main.Lang, obj.Name, new List<string>(), main.TrimTail);

        // methods, properties, actions
        var xsubs = new Dictionary<string, Block>();
        foreach (var x in obj.Subs) xsubs.TryAdd(x.Name.ToLowerInvariant(), x);
        var seen = new HashSet<string>();
        var newTexts = new List<string>();
        Tag anchor = null;
        foreach (var c in obj.Node.Children)
            if (c.Name is "Method" or "Property" or "Action" or "Transition" or "Folder") anchor = c;
        anchor ??= obj.Node.Child("Implementation") ?? obj.Node.Child("Declaration");
        var ind = LineIndent(s, (obj.Node.Child("Declaration") ?? anchor ?? obj.Node).Start);
        try
        {
            foreach (var p in subs)
            {
                var label = obj.Name + "." + p.Name;
                if (!xsubs.TryGetValue(p.Name.ToLowerInvariant(), out var x))
                {
                    if (anchor == null) throw new PlainTextException($"{label}: no place for a new {p.Kind} in the XML");
                    newTexts.Add(NewSubText(p, itf, ind, nl, res, label));
                    res.Added.Add(p.Kind + " " + label);
                    continue;
                }
                seen.Add(x.Name.ToLowerInvariant());
                if (x.Kind != p.Kind)
                {
                    res.Errors.Add($"{label}: {x.Kind} in the project, {p.Kind} in the text file - changing the kind is not supported");
                    continue;
                }
                if (p.Kind == "METHOD") Cmp(x, p.D, itf ? null : p.I, p.Lang, label, new List<string> { x.Name });
                else if (p.Kind == "ACTION") Cmp(x, null, p.I, p.Lang, label, new List<string> { x.Name });
                else
                {
                    Cmp(x, p.D, null, null, label, new List<string> { x.Name });
                    var xacc = new Dictionary<string, Block>();
                    foreach (var xa in x.Subs) xacc.TryAdd(xa.Kind, xa);
                    foreach (var pa in p.Accessors.OrderBy(q => q.Kind == "GET" ? 0 : 1))
                    {
                        if (xacc.TryGetValue(pa.Kind, out var xa))
                        {
                            Cmp(xa, pa.D, itf ? null : pa.I, pa.Lang, label + "." + xa.Name, new List<string> { x.Name, xa.Name });
                            continue;
                        }
                        if (pa.Lang != null) throw new PlainTextException($"{label}.{pa.Name}: a new accessor with a {pa.Lang} implementation cannot be created from text");
                        var pdecl = x.Node.Child("Declaration") ?? throw new PlainTextException($"{label}: the property has no declaration in the XML");
                        var at = pdecl;
                        if (pa.Kind == "SET")
                            foreach (var c in x.Node.Children)
                                if (c.Name is "Get" or "Set") at = c;
                        var ad = Fit(pa.D, null, true);
                        var ai = Fit(pa.I ?? "", null, false);
                        var (ah, atl) = ElementLines(pa.Kind, pa.Name, ad, ai, itf, LineIndent(s, pdecl.Start));
                        edits.Add(new Edit(at.End, at.End, nl + string.Join(nl, ah.Append(atl))));
                        res.Added.Add(pa.Kind + " " + label);
                        res.New.Add(new NewBlock(new List<string> { x.Name, pa.Name }, pa.Kind, ad, itf ? null : ai));
                    }
                    foreach (var xa in x.Subs)
                        if (!p.Accessors.Any(pa => pa.Kind == xa.Kind)) res.Kept.Add(label + "." + xa.Name);
                }
            }
        }
        catch (PlainTextException ex) { res.Errors.Add(ex.Message); }
        foreach (var x in obj.Subs)
            if (!seen.Contains(x.Name.ToLowerInvariant())) res.Kept.Add(obj.Name + "." + x.Name);
        if (res.Errors.Count > 0) return res;
        if (newTexts.Count > 0) edits.Add(new Edit(anchor.End, anchor.End, string.Concat(newTexts.Select(t => nl + t))));
        foreach (var c in obj.Node.Children)
        {
            if (c.Name != "LineIds" || !lineIds.Contains((c.Attr("Name") ?? "").ToLowerInvariant())) continue;
            int st = LineStart(s, c.Start);
            int en = c.End + (At(s, c.End, nl) ? nl.Length : 0);
            if (st == c.Start) en = c.End;
            edits.Add(new Edit(st, en, ""));
        }
        res.NewXml = ApplyEdits(s, edits);
        return res;
    }

    // ------------------------------------------------------------------ new object files
    static readonly Dictionary<string, string> Extensions = new() { ["POU"] = ".TcPOU", ["ITF"] = ".TcIO", ["GVL"] = ".TcGVL", ["DUT"] = ".TcDUT" };
    static readonly Dictionary<string, string> KindOfKeyword = new()
    { ["FUNCTION_BLOCK"] = "POU", ["PROGRAM"] = "POU", ["FUNCTION"] = "POU", ["INTERFACE"] = "ITF", ["TYPE"] = "DUT", ["VAR_GLOBAL"] = "GVL" };

    static string DetectKind(string text)
    {
        var k = FirstMatch(KindRx, Lines(E(N(text))));
        return k != null ? KindOfKeyword[k.ToUpperInvariant()] : null;
    }

    static (string Xml, UpdateResult Res) NewObjectXml(string kind, string name, PBlock main, List<PBlock> subs, string version, string productVersion)
    {
        if (main.Lang != null) throw new PlainTextException($"a new object with a {main.Lang} implementation cannot be created from text");
        const string nl = "\r\n";
        var decl = Fit(main.D, null, true);
        var tag = kind switch { "POU" => "POU", "ITF" => "Itf", "GVL" => "GVL", _ => "DUT" };
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<TcPlcObject Version=\"" + Esc(version) + "\" ProductVersion=\"" + Esc(productVersion) + "\">",
            "  <" + tag + " Name=\"" + Esc(name) + "\" Id=\"" + NewGuid() + "\"" + (kind == "POU" ? " SpecialFunc=\"None\">" : ">"),
            "    <Declaration>" + Cdata(decl) + "</Declaration>"
        };
        if (kind == "POU")
        {
            lines.Add("    <Implementation>");
            lines.Add("      <ST>" + Cdata(Fit(main.I ?? "", null, false)) + "</ST>");
            lines.Add("    </Implementation>");
        }
        lines.Add("  </" + tag + ">");
        lines.Add("</TcPlcObject>");
        var xml = string.Join(nl, lines);
        if (subs.Count == 0) return (xml, null);
        var res = UpdateObject(xml, ObjectFromXml(xml), main, subs);
        if (res.Errors.Count > 0) throw new PlainTextException(string.Join("; ", res.Errors));
        return (res.NewXml, res);
    }

    // ------------------------------------------------------------------ .plcproj
    public sealed class PlcObj
    {
        public string Include, Folder, Name, Ext, FullPath;
    }

    public sealed class PlcProj
    {
        public string Path, Dir, Stem, Name, Text;
        public bool Bom;
        public readonly List<PlcObj> Objects = new();
        public readonly List<LibraryRef> References = new();
        /// <summary>Problems with the library references (e.g. a placeholder without DefaultResolution) - export warnings.</summary>
        public readonly List<string> ReferenceIssues = new();
    }

    /// <summary>A library reference of the .plcproj as written to references.json (Namespace null = same as Name).</summary>
    public sealed class LibraryRef
    {
        public string Name, Version, DistributorName, Namespace;
    }

    static readonly string[] CodeExtensions = { ".tcpou", ".tcgvl", ".tcdut", ".tcio" };
    static readonly string[] SkipDirs = { "bin", "obj", "_boot", "_libraries", ".git", ".vs" };

    static string Combine(string dir, string rel) =>
        rel.Split('\\', StringSplitOptions.RemoveEmptyEntries).Aggregate(dir, (a, b) => Path.Combine(a, b));

    /// <summary>All *.plcproj below the solution folder (bin, obj, _Boot, _Libraries, .git, .vs skipped).</summary>
    public static List<string> FindPlcProjs(string slnDir)
    {
        var found = new List<string>();
        void Walk(string d)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(d); } catch { return; }
            foreach (var p in entries.OrderBy(x => Path.GetFileName(x).ToLowerInvariant(), StringComparer.Ordinal))
            {
                var e = Path.GetFileName(p);
                if (Directory.Exists(p))
                {
                    if (SkipDirs.Contains(e.ToLowerInvariant())) continue;
                    Walk(p);
                }
                else if (e.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase)) found.Add(p);
            }
        }
        Walk(slnDir);
        return found;
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>File as text (UTF-8, strict) and whether it starts with a BOM. Throws DecoderFallbackException if not UTF-8.</summary>
    static (string Text, bool Bom) ReadText(string path)
    {
        var data = File.ReadAllBytes(path);
        bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        int off = bom ? 3 : 0;
        return (StrictUtf8.GetString(data, off, data.Length - off), bom);
    }

    static byte[] Encode(string text, bool bom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        if (!bom) return body;
        var all = new byte[body.Length + 3];
        all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
        Buffer.BlockCopy(body, 0, all, 3, body.Length);
        return all;
    }

    static PlcProj ReadPlcProj(string path)
    {
        var p = new PlcProj { Path = path, Dir = System.IO.Path.GetDirectoryName(path), Stem = System.IO.Path.GetFileNameWithoutExtension(path) };
        (p.Text, p.Bom) = ReadText(path);
        var proj = ParseXml(p.Text).Child("Project") ?? throw new PlainTextException(path + ": no <Project>");
        foreach (var pg in proj.Children)
            if (pg.Name == "PropertyGroup" && p.Name == null && pg.Child("Name") is { } n) p.Name = ElementText(p.Text, n).Trim();
        foreach (var ig in proj.Children)
        {
            if (ig.Name != "ItemGroup") continue;
            foreach (var c in ig.Children)
            {
                if (c.Name is "PlaceholderReference" or "LibraryReference")
                {
                    var r = ParseLibraryRef(p.Text, c, out var issue);
                    if (r != null) p.References.Add(r);
                    if (issue != null) p.ReferenceIssues.Add(issue);
                    continue;
                }
                if (c.Name != "Compile") continue;
                var inc = (c.Attr("Include") ?? "").Replace('/', '\\');
                var ext = System.IO.Path.GetExtension(inc);
                if (!CodeExtensions.Contains(ext.ToLowerInvariant())) continue;
                int k = inc.LastIndexOf('\\');
                p.Objects.Add(new PlcObj
                {
                    Include = inc, Folder = k >= 0 ? inc[..k] : "", Name = System.IO.Path.GetFileNameWithoutExtension(inc[(k + 1)..]),
                    Ext = ext, FullPath = Combine(p.Dir, inc)
                });
            }
        }
        if (string.IsNullOrEmpty(p.Name)) p.Name = p.Stem;
        // sorted by name (ordinal, case-insensitive; stable) so that Git diffs of references.json stay small
        var sorted = p.References.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        p.References.Clear();
        p.References.AddRange(sorted);
        return p;
    }

    // ------------------------------------------------------------------ library references (references.json)
    public const string ReferencesFile = "references.json";

    /// <summary>"Tc2_Standard, * (Beckhoff Automation GmbH)" - name, version and distributor of a DefaultResolution.</summary>
    static readonly Regex ResolutionRx = new(@"^\s*(?<name>[^,]+?)\s*,\s*(?<version>[^\s(]+)\s*\((?<dist>.*)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>"newest" (LibraryReference) and an empty version mean "newest installed" - written as "*".</summary>
    static string NormVersion(string v) =>
        string.IsNullOrWhiteSpace(v) || v.Trim().Equals("newest", StringComparison.OrdinalIgnoreCase) ? "*" : v.Trim();

    /// <summary>One &lt;PlaceholderReference&gt; or &lt;LibraryReference&gt; of the .plcproj; parameters (e.g. GVL_PARAM_TCUNIT)
    /// are not taken over. null (with issue) when the entry cannot be read.</summary>
    static LibraryRef ParseLibraryRef(string text, Tag c, out string issue)
    {
        issue = null;
        var include = (c.Attr("Include") ?? "").Trim();
        string name, version, dist;
        if (c.Name == "PlaceholderReference")
        {
            var res = c.Child("DefaultResolution") is { } d ? ElementText(text, d).Trim() : "";
            var m = ResolutionRx.Match(res);
            if (!m.Success)
            {
                issue = $"placeholder {include}: DefaultResolution '{res}' is not 'Name, Version (Distributor)' - not in {ReferencesFile}";
                return null;
            }
            (name, version, dist) = (m.Groups["name"].Value.Trim(), m.Groups["version"].Value, m.Groups["dist"].Value.Trim());
        }
        else
        {
            var parts = include.Split(',');
            if (parts.Length != 3 || parts[0].Trim().Length == 0)
            {
                issue = $"library reference '{include}' is not 'Name,Version,Distributor' - not in {ReferencesFile}";
                return null;
            }
            (name, version, dist) = (parts[0].Trim(), parts[1], parts[2].Trim());
        }
        var ns = c.Child("Namespace") is { } n ? ElementText(text, n).Trim() : "";
        return new LibraryRef
        {
            Name = name, Version = NormVersion(version), DistributorName = dist.Length > 0 ? dist : null,
            Namespace = ns.Length == 0 || ns == name ? null : ns
        };
    }

    static string JsonString(string v)
    {
        if (v == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var ch in v)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>references.json in the format of the SRCI repository: an array of {Name, Version, DistributorName, Namespace},
    /// 2 blanks indentation, lines joined with "\n" (written with CRLF), no line end after the closing bracket.</summary>
    public static string ReferencesJson(IEnumerable<LibraryRef> refs)
    {
        var list = refs.ToList();
        if (list.Count == 0) return "[]";
        var sb = new StringBuilder("[\n");
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            sb.Append("  {\n")
              .Append("    \"Name\": ").Append(JsonString(r.Name)).Append(",\n")
              .Append("    \"Version\": ").Append(JsonString(r.Version)).Append(",\n")
              .Append("    \"DistributorName\": ").Append(JsonString(r.DistributorName)).Append(",\n")
              .Append("    \"Namespace\": ").Append(JsonString(r.Namespace)).Append('\n')
              .Append(i < list.Count - 1 ? "  },\n" : "  }\n");
        }
        return sb.Append(']').ToString();
    }

    /// <summary>The library references of a .plcproj file (sorted by name) - for tests and tools.</summary>
    public static List<LibraryRef> ReadReferences(string plcproj) => ReadPlcProj(plcproj).References;

    /// <summary>--plc: plcproj file name or PLC project name, also with " Project" (the name of the nested project in the tree).</summary>
    static bool PlcMatches(PlcProj p, IReadOnlyCollection<string> filters)
    {
        if (filters == null || filters.Count == 0) return true;
        var names = new[] { p.Stem, p.Stem + " Project", p.Name, p.Name + " Project" }.Select(x => x.ToLowerInvariant()).ToList();
        return filters.Any(f => names.Contains(f.Trim().ToLowerInvariant()));
    }

    /// <summary>Inserts an item sorted into the ItemGroup that holds such items (string level, formatting kept).</summary>
    static string InsertItem(string text, string element, string include, List<string> elementText)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var proj = ParseXml(text).Child("Project") ?? throw new PlainTextException("plcproj: no <Project>");
        var group = proj.Children.FirstOrDefault(ig => ig.Name == "ItemGroup" && ig.Children.Any(c => c.Name == element));
        if (group != null)
        {
            var items = group.Children.Where(c => c.Name == element).ToList();
            var ind = LineIndent(text, items[0].Start);
            var body = string.Join(nl, elementText.Select(l => ind + l));
            var after = items.FirstOrDefault(c => string.CompareOrdinal((c.Attr("Include") ?? "").ToLowerInvariant(), include.ToLowerInvariant()) > 0);
            if (after != null)
            {
                int pos = LineStart(text, after.Start);
                return text[..pos] + body + nl + text[pos..];
            }
            var last = items[^1];
            return text[..last.End] + nl + body + text[last.End..];
        }
        var groups = proj.Children.Where(ig => ig.Name == "ItemGroup").ToList();
        var anchorNode = groups.FirstOrDefault(ig => ig.Children.Any(c => c.Name == "Compile"));
        var first = proj.Children.FirstOrDefault();
        var gind = first != null ? LineIndent(text, first.Start) : "  ";
        var block = gind + "<ItemGroup>" + nl + string.Join(nl, elementText.Select(l => gind + "  " + l)) + nl + gind + "</ItemGroup>";
        if (anchorNode != null) return text[..anchorNode.End] + nl + block + text[anchorNode.End..];
        if (groups.Count > 0)
        {
            int pos = LineStart(text, groups[0].Start);
            return text[..pos] + block + nl + text[pos..];
        }
        int end = LineStart(text, proj.CloseStart);
        return text[..end] + block + nl + text[end..];
    }

    /// <summary>Adds &lt;Compile Include&gt; and the missing &lt;Folder Include&gt; entries of a new object file.</summary>
    static string AddToPlcProj(string text, string include)
    {
        var proj = ParseXml(text).Child("Project") ?? throw new PlainTextException("plcproj: no <Project>");
        var existing = new HashSet<string>();
        var folders = new HashSet<string>();
        foreach (var ig in proj.Children.Where(x => x.Name == "ItemGroup"))
            foreach (var c in ig.Children)
            {
                if (c.Name == "Compile") existing.Add((c.Attr("Include") ?? "").ToLowerInvariant());
                if (c.Name == "Folder") folders.Add((c.Attr("Include") ?? "").TrimEnd('\\').ToLowerInvariant());
            }
        if (!existing.Contains(include.ToLowerInvariant()))
            text = InsertItem(text, "Compile", include, new List<string> { "<Compile Include=\"" + Esc(include) + "\">", "  <SubType>Code</SubType>", "</Compile>" });
        int k = include.LastIndexOf('\\');
        if (k > 0)
        {
            var parts = include[..k].Split('\\');
            for (int n = 1; n <= parts.Length; n++)
            {
                var f = string.Join("\\", parts.Take(n));
                if (folders.Contains(f.ToLowerInvariant())) continue;
                text = InsertItem(text, "Folder", f, new List<string> { "<Folder Include=\"" + Esc(f) + "\" />" });
                folders.Add(f.ToLowerInvariant());
            }
        }
        return text;
    }

    // ------------------------------------------------------------------ export / check / import of a solution
    /// <summary>A changed or new object of an import - for the IDE path of tc_plaintext (NewKind != null: new file).</summary>
    public sealed class ObjectChange
    {
        public PlcProj Plc; public string Folder, Name, NewKind, File; public UpdateResult Result;
    }

    public sealed class Report
    {
        public readonly List<string> Lines = new(), Warnings = new(), Errors = new(), Differences = new();
        public int Objects, Written, Unchanged, Removed, NonSt;
        public readonly List<(string Path, byte[] Data)> Files = new();
        public readonly List<ObjectChange> Changes = new();
        public readonly List<PlcProj> Plcs = new();
    }

    static List<PlcProj> SelectPlcs(string slnDir, IReadOnlyCollection<string> filters, Report rep)
    {
        var plcs = new List<PlcProj>();
        foreach (var f in FindPlcProjs(slnDir))
        {
            PlcProj p;
            try { p = ReadPlcProj(f); }
            catch (Exception ex) when (ex is PlainTextException or DecoderFallbackException or IOException or UnauthorizedAccessException)
            { rep.Errors.Add(f + ": " + ex.Message); continue; }
            if (PlcMatches(p, filters)) plcs.Add(p);
        }
        var stems = new Dictionary<string, string>();
        foreach (var p in plcs)
        {
            if (stems.TryGetValue(p.Stem.ToLowerInvariant(), out var other)) rep.Errors.Add($"two PLC projects named {p.Stem}: {other} and {p.Path}");
            stems[p.Stem.ToLowerInvariant()] = p.Path;
        }
        if (plcs.Count == 0 && rep.Errors.Count == 0)
            rep.Errors.Add("no (matching) PLC project (*.plcproj) below " + slnDir + (filters != null && filters.Count > 0 ? " for --plc " + string.Join(",", filters) : ""));
        rep.Plcs.AddRange(plcs);
        return plcs;
    }

    static List<string> StFiles(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.st", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    static string RelDir(string root, string file)
    {
        var r = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        return r == "." ? "" : r.Replace(Path.DirectorySeparatorChar, '\\');
    }

    /// <summary>Export self check: importing the text must not change anything.</summary>
    static List<string> SelfCheck(string xml, Block obj, string text)
    {
        PBlock main;
        List<PBlock> subs;
        try { (main, subs) = ParseSt(text, obj.Kind); }
        catch (PlainTextException ex) { return new List<string> { "cannot be read back: " + ex.Message }; }
        var res = UpdateObject(xml, obj, main, subs);
        var issues = res.Errors.Concat(res.Warnings).Concat(res.Changed).Concat(res.Added).Concat(res.Kept).ToList();
        if (res.NewXml != null && res.NewXml != xml && issues.Count == 0) issues.Add("XML would change");
        return issues;
    }

    /// <summary>check=false: writes the .st files that differ (clean: removes .st files without object). check=true: writes
    /// nothing, lists the differences between the text folder and the project.</summary>
    public static Report Export(string slnDir, IReadOnlyCollection<string> filters, string outDir, bool check, bool clean)
    {
        var rep = new Report();
        foreach (var p in SelectPlcs(slnDir, filters, rep))
        {
            var root = Path.Combine(outDir, p.Stem);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in p.Objects)
            {
                var rel = Path.Combine(new[] { p.Stem }.Concat(o.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries)).Append(o.Name + ".st").ToArray());
                if (!File.Exists(o.FullPath)) { rep.Warnings.Add($"{p.Stem}: {o.Include} is in the plcproj but missing on disk"); continue; }
                string text;
                Block obj;
                List<string> issues;
                try
                {
                    var (xml, _) = ReadText(o.FullPath);
                    obj = ObjectFromXml(xml);
                    text = RenderFile(obj);
                    issues = SelfCheck(xml, obj, text);
                }
                catch (Exception ex) when (ex is PlainTextException or DecoderFallbackException or IOException)
                { rep.Warnings.Add($"{o.Include}: {ex.Message}"); continue; }
                if (issues.Count > 0)
                {
                    rep.Warnings.Add($"{rel}: the text does not reproduce the project exactly ({string.Join("; ", issues)})");
                    if (check) rep.Differences.Add("cannot be represented exactly: " + rel);
                }
                rep.Objects++;
                rep.NonSt += CountNonSt(obj);
                var target = Path.Combine(outDir, rel);
                expected.Add(Path.GetFullPath(target));
                var data = new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n"));
                var old = File.Exists(target) ? File.ReadAllBytes(target) : null;
                if (check)
                {
                    if (old == null) rep.Differences.Add("missing: " + rel);
                    else
                    {
                        bool same;
                        try { same = N(StrictUtf8.GetString(old).TrimStart('\uFEFF')) == text; } catch (DecoderFallbackException) { same = false; }
                        if (!same) rep.Differences.Add("differs: " + rel);
                    }
                }
                else if (old == null || !old.AsSpan().SequenceEqual(data))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                    rep.Written++;
                }
                else rep.Unchanged++;
            }
            ExportReferences(p, outDir, check, rep);
            foreach (var f in StFiles(root))
            {
                if (expected.Contains(Path.GetFullPath(f))) continue;
                var rel = Path.GetRelativePath(outDir, f);
                if (check) rep.Differences.Add("not in the project: " + rel);
                else if (clean) { File.Delete(f); rep.Removed++; }
                else rep.Warnings.Add($"{rel}: no such object in the project (--clean removes it)");
            }
        }
        return rep;
    }

    /// <summary>&lt;out&gt;\&lt;PLC project&gt;\references.json: written only when the content changes (counted like a .st file);
    /// check compares it with line ends ignored. --clean never removes it (it only removes .st files).</summary>
    static void ExportReferences(PlcProj p, string outDir, bool check, Report rep)
    {
        foreach (var issue in p.ReferenceIssues) rep.Warnings.Add($"{p.Stem}: {issue}");
        var rel = Path.Combine(p.Stem, ReferencesFile);
        var target = Path.Combine(outDir, rel);
        var text = ReferencesJson(p.References);
        var data = new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n"));
        var old = File.Exists(target) ? File.ReadAllBytes(target) : null;
        if (check)
        {
            if (old == null) rep.Differences.Add("missing: " + rel);
            else
            {
                bool same;
                // line ends ignored, also a final line end added by an editor or Git
                try { same = N(StrictUtf8.GetString(old).TrimStart('\uFEFF')).TrimEnd('\n') == text; } catch (DecoderFallbackException) { same = false; }
                if (!same) rep.Differences.Add("differs: " + rel);
            }
        }
        else if (old == null || !old.AsSpan().SequenceEqual(data))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            rep.Written++;
        }
        else rep.Unchanged++;
    }

    static int[] VersionKey(string v)
    {
        var parts = v.Split('.');
        if (parts.Length < 2 || parts.Length > 4 || !parts.All(x => x.Length > 0 && x.All(char.IsAsciiDigit))) return new[] { -1 };
        return parts.Select(x => int.TryParse(x, out var i) ? i : int.MaxValue).ToArray();
    }

    static int CompareKeys(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>TcPlcObject Version / ProductVersion of the newest object file of the project (new files look like the others).</summary>
    static (string Version, string ProductVersion) ProductVersionOf(PlcProj p)
    {
        (string, string)? best = null;
        foreach (var o in p.Objects)
        {
            if (!File.Exists(o.FullPath)) continue;
            Tag tcp;
            try { tcp = ParseXml(ReadText(o.FullPath).Text).Child("TcPlcObject"); }
            catch (Exception ex) when (ex is PlainTextException or DecoderFallbackException or IOException) { continue; }
            if (tcp == null || string.IsNullOrEmpty(tcp.Attr("Version")) || string.IsNullOrEmpty(tcp.Attr("ProductVersion"))) continue;
            if (best == null || CompareKeys(VersionKey(tcp.Attr("ProductVersion")), VersionKey(best.Value.Item2)) > 0)
                best = (tcp.Attr("Version"), tcp.Attr("ProductVersion"));
        }
        return best ?? ("1.1.0.1", "3.1.4024.0");
    }

    /// <summary>Plans the import of the text folder into the existing project. Nothing is written here: the files to write
    /// are in Report.Files (WriteFiles), the changes per object in Report.Changes. With errors nothing may be written.</summary>
    public static Report Import(string slnDir, IReadOnlyCollection<string> filters, string fromDir)
    {
        var rep = new Report();
        foreach (var p in SelectPlcs(slnDir, filters, rep))
        {
            var root = Path.Combine(fromDir, p.Stem);
            if (!Directory.Exists(root)) { rep.Warnings.Add($"{p.Stem}: no text folder {root} - skipped"); continue; }
            var byKey = new Dictionary<(string, string), PlcObj>();
            var byName = new Dictionary<string, PlcObj>();
            foreach (var o in p.Objects)
            {
                byKey[(o.Folder.ToLowerInvariant(), o.Name.ToLowerInvariant())] = o;
                byName.TryAdd(o.Name.ToLowerInvariant(), o);
            }
            var seen = new HashSet<(string, string)>();
            var plcText = p.Text;
            (string Version, string ProductVersion)? version = null;
            foreach (var f in StFiles(root))
            {
                var relDir = RelDir(root, f);
                var name = Path.GetFileNameWithoutExtension(f);
                var rel = Path.GetRelativePath(fromDir, f);
                string text;
                try { text = ReadText(f).Text; }
                catch (DecoderFallbackException) { rep.Errors.Add($"{rel}: not UTF-8 - save the file as UTF-8"); continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rep.Errors.Add($"{rel}: {ex.Message}"); continue; }
                var key = (relDir.ToLowerInvariant(), name.ToLowerInvariant());
                try
                {
                    if (byKey.TryGetValue(key, out var o))
                    {
                        seen.Add(key);
                        if (!File.Exists(o.FullPath)) { rep.Errors.Add($"{rel}: {o.Include} is in the plcproj but missing on disk"); continue; }
                        var (xml, bom) = ReadText(o.FullPath);
                        var obj = ObjectFromXml(xml);
                        var (main, subs) = ParseSt(text, obj.Kind);
                        var res = UpdateObject(xml, obj, main, subs);
                        rep.Warnings.AddRange(res.Warnings.Select(w => $"{rel}: {w}"));
                        if (res.Errors.Count > 0) { rep.Errors.AddRange(res.Errors.Select(e => $"{rel}: {e}")); continue; }
                        if (res.NewXml != xml)
                        {
                            rep.Files.Add((o.FullPath, Encode(res.NewXml, bom)));
                            var parts = new List<string>();
                            if (res.Changed.Count > 0) parts.Add("changed " + string.Join(", ", res.Changed));
                            if (res.Added.Count > 0) parts.Add("new " + string.Join(", ", res.Added));
                            rep.Lines.Add($"{rel}: " + string.Join("; ", parts));
                            rep.Changes.Add(new ObjectChange { Plc = p, Folder = o.Folder, Name = o.Name, Result = res });
                        }
                        foreach (var k in res.Kept) rep.Lines.Add($"{rel}: {k} kept (not in the text file)");
                        rep.Objects++;
                    }
                    else
                    {
                        if (byName.TryGetValue(name.ToLowerInvariant(), out var other))
                        { rep.Errors.Add($"{rel}: {name} exists in folder '{other.Folder}' of the project - moving objects is not supported"); continue; }
                        var kind = DetectKind(text);
                        if (kind == null) { rep.Errors.Add($"{rel}: new file without FUNCTION_BLOCK, PROGRAM, FUNCTION, INTERFACE, TYPE or VAR_GLOBAL"); continue; }
                        var (main, subs) = ParseSt(text, kind);
                        if (kind != "GVL")
                        {
                            var hn = FirstMatch(kind == "DUT" ? TypeNameRx : HeaderNameRx, Lines(main.D));
                            if (hn == null || !hn.Equals(name, StringComparison.OrdinalIgnoreCase))
                            { rep.Errors.Add($"{rel}: the declaration names {hn}, the file is {name}.st - they must be equal"); continue; }
                        }
                        version ??= ProductVersionOf(p);
                        var (xml, res) = NewObjectXml(kind, name, main, subs, version.Value.Version, version.Value.ProductVersion);
                        var include = (relDir != "" ? relDir + "\\" : "") + name + Extensions[kind];
                        var full = Combine(p.Dir, include);
                        if (File.Exists(full)) { rep.Errors.Add($"{rel}: {include} exists on disk but is not in the plcproj - not overwritten"); continue; }
                        rep.Files.Add((full, Encode(xml, true)));
                        plcText = AddToPlcProj(plcText, include);
                        byName[name.ToLowerInvariant()] = new PlcObj { Include = include, Folder = relDir, Name = name, Ext = Extensions[kind], FullPath = full };
                        rep.Lines.Add($"{rel}: new {kind} {include}");
                        rep.Changes.Add(new ObjectChange { Plc = p, Folder = relDir, Name = name, NewKind = kind, File = full, Result = res });
                        rep.Objects++;
                    }
                }
                catch (Exception ex) when (ex is PlainTextException or DecoderFallbackException or IOException)
                { rep.Errors.Add($"{rel}: {ex.Message}"); }
            }
            foreach (var o in p.Objects)
                if (!seen.Contains((o.Folder.ToLowerInvariant(), o.Name.ToLowerInvariant())))
                    rep.Lines.Add($"{p.Stem}: {o.Include} not in the text folder (kept)");
            if (plcText != p.Text) rep.Files.Add((p.Path, Encode(plcText, p.Bom)));
        }
        return rep;
    }

    public static void WriteFiles(Report rep)
    {
        foreach (var (path, data) in rep.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }

    /// <summary>Kind word of a new object for messages and tc_plc_create.</summary>
    public static string KindText(string kind) => kind switch
    {
        "POU" => "POU", "ITF" => "interface", "GVL" => "GVL", "DUT" => "DUT", _ => kind
    };
}
