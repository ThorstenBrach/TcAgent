// The ST formatter: indentation, case of keywords, spacing, aligned declarations, blank lines, long calls and brackets
// in conditions - deterministic, configurable (StFormatConfig.cs) and with a safety net (docs/FORMATTER.md).
//
//   Safety net: the tokens that carry meaning (StLexer.Significant) are the same before and after, otherwise the text stays
//   as it is and the result says why. Brackets added by paren.precedence are checked on the expression trees instead
//   (StExpr.cs). A second run must give the same text (idempotent), otherwise the text stays as well.
//   Switched off in the code: // tcagent-format: off ... on, next-line, file - or {attribute 'tcagent.format' := 'off'} ...
//   The markers of other formatters are understood too (STweep.Disable/Enable, fmt: off/on, stformat:off/on, tctidier-ignore).
//
// SPDX-License-Identifier: MIT
using System.Text;

namespace TcAgent;

public enum StPart { Auto, Declaration, Implementation }

public sealed class StFormatResult
{
    public string Text;
    public bool Changed;
    /// <summary>Why nothing was changed (a section that cannot be read, the safety net) - null when formatting worked.</summary>
    public string Error;
    public readonly List<string> Notes = new();
    public StPart Part;
}

public static class StFormatter
{
    public static StFormatResult Format(string text, StFormatConfig cfg, StPart part = StPart.Auto)
    {
        var r = new StFormatResult { Text = text ?? "" };
        if (string.IsNullOrEmpty(text)) return r;
        try
        {
            var before = StLexer.Lex(text);
            if (part == StPart.Auto) part = Detect(before);
            r.Part = part;
            var once = Core(before, cfg, part, r.Notes, out bool parens);
            // safety net 1: the meaning-carrying tokens are the same (brackets: checked on the trees in StExpr)
            var after = StLexer.Lex(once);
            var a = StLexer.Significant(before, parens);
            var b = StLexer.Significant(after, parens);
            if (!a.SequenceEqual(b))
            {
                int k = 0; while (k < a.Count && k < b.Count && a[k] == b[k]) k++;
                r.Error = $"safety net: the formatted text would differ at token {k + 1} ({(k < a.Count ? a[k] : "end")} → {(k < b.Count ? b[k] : "end")}) - nothing changed. Please report it (tc_feedback).";
                return r;
            }
            if (parens && !StExpr.SameTrees(before, after))
            {
                r.Error = "safety net: brackets would change the meaning of a condition - nothing changed. Please report it (tc_feedback).";
                return r;
            }
            // safety net 2: idempotent
            var twice = Core(after, cfg, part, new List<string>(), out _);
            if (twice != once)
            {
                r.Error = "safety net: a second run would format differently - nothing changed. Please report it (tc_feedback).";
                return r;
            }
            r.Text = once;
            r.Changed = once != text;
        }
        catch (StSyntaxException ex) { r.Error = "not formatted: " + ex.Message; }
        return r;
    }

    static readonly string[] DeclStarts = { "FUNCTION_BLOCK", "FUNCTION", "PROGRAM", "METHOD", "PROPERTY", "INTERFACE", "TYPE", "VAR", "VAR_INPUT",
        "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_TEMP", "VAR_STAT", "VAR_EXTERNAL", "VAR_INST", "VAR_CONFIG", "ACTION" };

    public static StPart Detect(List<StToken> t)
    {
        var first = t.FirstOrDefault(x => !x.Trivia && !x.Comment && x.Kind != StTok.Pragma);
        return first != null && first.Kind == StTok.Word && DeclStarts.Contains(first.Upper) ? StPart.Declaration : StPart.Implementation;
    }

    // ------------------------------------------------------------------ lines
    sealed class Piece { public StToken T; public string Gap = ""; }

    sealed class Line
    {
        public readonly List<Piece> P = new();
        public string Newline;
        public string OrigIndent = "";
        public bool Frozen, Blank, CommentOnly, Cont, Label, InBlock, Fixed;
        public int Depth, DepthHere;
        public Line ContBase;
        public string Original;
        public string Indent = "";
        public StToken First => P.FirstOrDefault(p => !p.T.Comment && p.T.Kind != StTok.Pragma)?.T;
        public StToken Last => P.LastOrDefault(p => !p.T.Comment && p.T.Kind != StTok.Pragma)?.T;
    }

    static string Core(List<StToken> tokens, StFormatConfig cfg, StPart part, List<string> notes, out bool parens)
    {
        parens = false;
        var toks = new List<StToken>(tokens);
        var frozenTok = Freeze(toks, out bool wholeFile);
        if (wholeFile) { notes.Add("switched off for the whole text (tcagent-format: file)"); return StLexer.Join(tokens); }
        if (part == StPart.Implementation && cfg.Flag("paren.precedence"))
        {
            var (t2, f2, added) = StExpr.AddPrecedenceParens(toks, frozenTok);
            if (added > 0) { toks = t2; frozenTok = f2; parens = true; notes.Add($"paren.precedence: {added} bracket pair(s)"); }
        }
        var lines = Split(toks, frozenTok, out bool finalNewline);
        CaseWords(lines, cfg);
        Structure(lines, cfg, part);
        foreach (var l in lines) if (!l.Frozen && !l.Blank) Spacing(l, cfg, part);
        Indentation(lines, cfg);
        if (part == StPart.Declaration) AlignDeclarations(lines, cfg);
        if (part == StPart.Implementation) lines = WrapCalls(lines, cfg);
        lines = BlankLines(lines, cfg);
        return Render(lines, cfg, finalNewline);
    }

    // ------------------------------------------------------------------ switched off in the code
    static string Marker(StToken t)
    {
        string s = t.Text.ToLowerInvariant();
        if (t.Comment)
        {
            int i = s.IndexOf("tcagent-format:", StringComparison.Ordinal);
            if (i >= 0)
            {
                var v = s[(i + 15)..].Trim().TrimEnd('*', ')', '/').Trim();
                if (v.StartsWith("off")) return "off"; if (v.StartsWith("on")) return "on";
                if (v.StartsWith("next-line")) return "next"; if (v.StartsWith("file")) return "file";
            }
            if (s.Contains("stweep.disable")) return "off"; if (s.Contains("stweep.enable")) return "on";
            if (s.Contains("fmt: off") || s.Contains("fmt:off")) return "off"; if (s.Contains("fmt: on") || s.Contains("fmt:on")) return "on";
            if (s.Contains("stformat:off") || s.Contains("stformat: off")) return "off"; if (s.Contains("stformat:on") || s.Contains("stformat: on")) return "on";
            if (s.Contains("tctidier-ignore")) return "next";
        }
        if (t.Kind == StTok.Pragma && s.Contains("tcagent.format"))
        {
            if (s.Contains("'off'")) return "off"; if (s.Contains("'on'")) return "on";
            if (s.Contains("'next-line'")) return "next"; if (s.Contains("'file'")) return "file";
        }
        return null;
    }

    /// <summary>Per token: does it lie in a section that is switched off? (The marker lines themselves stay as they are.)</summary>
    static bool[] Freeze(List<StToken> t, out bool wholeFile)
    {
        wholeFile = false;
        var f = new bool[t.Count];
        bool off = false, next = false;
        int lineStart = 0;
        for (int i = 0; i <= t.Count; i++)
        {
            if (i < t.Count && t[i].Kind != StTok.Newline) continue;
            // a line from lineStart to i (exclusive)
            string m = null;
            bool code = false;
            for (int k = lineStart; k < i; k++) { m = Marker(t[k]) ?? m; if (!t[k].Trivia) code = true; }
            if (m == "file") { wholeFile = true; return f; }
            bool freezeLine = off || next || m != null;
            if (m == "off") off = true;
            if (m == "on") off = false;
            if (next && code && m == null) next = false;
            if (m == "next") next = true;
            if (freezeLine) for (int k = lineStart; k < i; k++) f[k] = true;
            lineStart = i + 1;
        }
        return f;
    }

    static List<Line> Split(List<StToken> t, bool[] frozen, out bool finalNewline)
    {
        var lines = new List<Line>();
        var cur = new Line();
        var orig = new StringBuilder();
        string gap = "";
        bool lineFrozen = false, leading = true;
        void Close(string nl)
        {
            cur.Newline = nl; cur.Original = orig.ToString(); cur.Frozen = lineFrozen;
            cur.Blank = cur.P.Count == 0;
            lines.Add(cur);
            cur = new Line(); orig.Clear(); gap = ""; lineFrozen = false; leading = true;
        }
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == StTok.Newline) { Close(x.Text); continue; }
            orig.Append(x.Text);
            if (frozen[i]) lineFrozen = true;
            if (x.Kind == StTok.Space)
            {
                if (leading) cur.OrigIndent += x.Text; else gap += x.Text;
                continue;
            }
            leading = false;
            cur.P.Add(new Piece { T = x, Gap = gap });
            gap = "";
        }
        finalNewline = t.Count > 0 && t[^1].Kind == StTok.Newline;
        if (!finalNewline || cur.P.Count > 0 || orig.Length > 0) { Close(null); }
        return lines;
    }

    // ------------------------------------------------------------------ case of keywords
    static void CaseWords(List<Line> lines, StFormatConfig cfg)
    {
        StToken prev = null;
        foreach (var l in lines)
            foreach (var p in l.P)
            {
                var t = p.T;
                if (!l.Frozen && !(prev != null && prev.Kind == StTok.Op && prev.Text == "."))
                {
                    if (t.Kind == StTok.Word)
                    {
                        string rule = StKeywords.Keywords.Contains(t.Text) ? "case.keywords" : StKeywords.Types.Contains(t.Text) ? "case.types"
                                    : StKeywords.Literals.Contains(t.Text) ? "case.literals" : null;
                        if (rule != null) p.T = t with { Text = Cased(t.Text, cfg[rule]) };
                    }
                    else if (t.Kind == StTok.Literal && t.Text.IndexOf('#') is > 0 and var h)
                    {
                        var prefix = t.Text[..h];
                        if (StKeywords.Types.Contains(prefix) || StKeywords.LiteralPrefixes.Contains(prefix))
                            p.T = t with { Text = Cased(prefix, cfg["case.literals"]) + t.Text[h..] };
                    }
                }
                if (!t.Comment && t.Kind != StTok.Pragma) prev = p.T;
            }
    }

    static string Cased(string s, string mode) => mode.ToLowerInvariant() switch { "upper" => s.ToUpperInvariant(), "lower" => s.ToLowerInvariant(), _ => s };

    // ------------------------------------------------------------------ structure: depth of every line
    sealed class Frame { public string Kind; public int Base, LabelDepth; public bool Open; }

    static readonly HashSet<string> VarWords = new(StringComparer.OrdinalIgnoreCase)
        { "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_TEMP", "VAR_STAT", "VAR_EXTERNAL", "VAR_INST", "VAR_CONFIG" };

    static bool Structural(StToken t) => t != null && t.Kind == StTok.Word &&
        (t.Upper.StartsWith("END_") || VarWords.Contains(t.Text) || t.Upper is "THEN" or "DO" or "OF" or "ELSE" or "ELSIF" or "UNTIL" or "STRUCT" or "UNION"
         or "TYPE" or "FUNCTION" or "FUNCTION_BLOCK" or "PROGRAM" or "METHOD" or "PROPERTY" or "INTERFACE" or "ACTION" or "IF" or "CASE" or "FOR" or "WHILE" or "REPEAT");

    static void Structure(List<Line> lines, StFormatConfig cfg, StPart part)
    {
        var st = new Stack<Frame>();
        int depth = 0, paren = 0;
        bool inStatement = false;
        Line stmtStart = null;
        bool caseIndent = cfg.Flag("indent.case-labels");
        Frame Top() => st.Count > 0 ? st.Peek() : null;
        int lineNo = 0;
        foreach (var l in lines)
        {
            lineNo++;
            l.DepthHere = depth;
            if (l.Blank) continue;
            var first = l.First;
            if (first == null) { l.CommentOnly = true; continue; }
            var top = Top();
            bool label = part == StPart.Implementation && top?.Kind == "CASE" && top.Open && paren == 0 && IsLabelLine(l);
            l.Label = label;
            l.InBlock = top?.Kind is "VAR" or "STRUCT" && !first.Upper.StartsWith("END_");
            if (inStatement && !(first.Upper is "THEN" or "DO" or "OF") && !(Structural(first) && paren == 0))
            {
                l.Cont = true; l.ContBase = stmtStart; l.Depth = stmtStart?.Depth ?? depth;
            }
            else
            {
                int d = depth;
                string f = first.Kind == StTok.Word ? first.Upper : "";
                if (f is "END_IF" or "END_FOR" or "END_WHILE" or "END_REPEAT" or "END_VAR" or "END_STRUCT" or "END_UNION") d = Math.Max(0, depth - 1);
                else if (f == "UNTIL" && top?.Kind == "REPEAT") d = top.Base;
                else if (f == "END_CASE" && top?.Kind == "CASE") d = top.Base;
                else if (f == "ELSE" && top?.Kind == "CASE") d = top.LabelDepth;
                else if (f is "ELSE" or "ELSIF" && top?.Kind == "IF") d = top.Base;
                else if (label) d = top.LabelDepth;
                l.Depth = d;
                stmtStart = l;
            }
            // walk the tokens
            StToken prev = null;
            bool labelDone = false;
            foreach (var p in l.P)
            {
                var t = p.T;
                if (t.Comment || t.Kind == StTok.Pragma) continue;
                bool member = prev != null && prev.Kind == StTok.Op && prev.Text == ".";
                prev = t;
                if (t.Kind == StTok.Punct)
                {
                    if (t.Text is "(" or "[") paren++;
                    else if (t.Text is ")" or "]") paren = Math.Max(0, paren - 1);
                    else if (t.Text == ":" && label && !labelDone && paren == 0) { depth = Top().LabelDepth + 1; labelDone = true; }
                    continue;
                }
                if (t.Kind != StTok.Word || member) continue;
                var w = t.Upper;
                var f0 = Top();
                switch (w)
                {
                    case "IF": st.Push(new Frame { Kind = "IF", Base = depth }); break;
                    case "THEN": if (f0?.Kind == "IF" && !f0.Open) { depth = f0.Base + 1; f0.Open = true; } break;
                    case "ELSIF": if (f0?.Kind == "IF") { depth = f0.Base; f0.Open = false; } break;
                    case "ELSE":
                        if (f0?.Kind == "IF") { depth = f0.Base + 1; f0.Open = true; }
                        else if (f0?.Kind == "CASE") depth = f0.LabelDepth + 1;
                        break;
                    case "END_IF": depth = Pop(st, "IF", w, lineNo).Base; break;
                    case "FOR": st.Push(new Frame { Kind = "FOR", Base = depth }); break;
                    case "WHILE": st.Push(new Frame { Kind = "WHILE", Base = depth }); break;
                    case "DO": if (f0?.Kind is "FOR" or "WHILE" && !f0.Open) { depth = f0.Base + 1; f0.Open = true; } break;
                    case "END_FOR": depth = Pop(st, "FOR", w, lineNo).Base; break;
                    case "END_WHILE": depth = Pop(st, "WHILE", w, lineNo).Base; break;
                    case "REPEAT": st.Push(new Frame { Kind = "REPEAT", Base = depth, Open = true }); depth++; break;
                    case "UNTIL": if (f0?.Kind == "REPEAT") depth = f0.Base; break;
                    case "END_REPEAT": depth = Pop(st, "REPEAT", w, lineNo).Base; break;
                    case "CASE": st.Push(new Frame { Kind = "CASE", Base = depth }); break;
                    case "OF": if (f0?.Kind == "CASE" && !f0.Open) { f0.Open = true; f0.LabelDepth = depth + (caseIndent ? 1 : 0); depth = f0.LabelDepth; } break;
                    case "END_CASE": depth = Pop(st, "CASE", w, lineNo).Base; break;
                    case "STRUCT": case "UNION": st.Push(new Frame { Kind = "STRUCT", Base = depth, Open = true }); depth++; break;
                    case "END_STRUCT": case "END_UNION": depth = Pop(st, "STRUCT", w, lineNo).Base; break;
                    case "END_VAR": depth = Pop(st, "VAR", w, lineNo).Base; break;
                    default:
                        if (VarWords.Contains(w)) { st.Push(new Frame { Kind = "VAR", Base = depth, Open = true }); depth++; }
                        break;
                }
            }
            var last = l.Last;
            bool ends = last != null && (last.Text == ";" || last.Kind == StTok.Word && (Structural(last) && !(last.Upper is "IF" or "CASE" or "FOR" or "WHILE" or "ELSIF" or "UNTIL")
                        || last.Upper is "CONSTANT" or "RETAIN" or "PERSISTENT" or "ELSE" or "REPEAT") || last.Text == ":" && (label || part == StPart.Declaration));
            if (part == StPart.Declaration && last != null && st.Count == 0 && paren == 0 && !(last.Text is "," or "(")) ends = true;   // header lines
            inStatement = paren > 0 || !ends;
        }
        if (st.Count > 0) throw new StSyntaxException($"{st.Peek().Kind} is not closed (END_{st.Peek().Kind} missing)");
    }

    static Frame Pop(Stack<Frame> st, string kind, string word, int line)
    {
        if (st.Count == 0 || st.Peek().Kind != kind) throw new StSyntaxException($"line {line}: {word} without {kind}");
        return st.Pop();
    }

    /// <summary>A CASE label: values (numbers, enum values, ranges, lists) and a colon, before any :=.</summary>
    static bool IsLabelLine(Line l)
    {
        int paren = 0;
        foreach (var p in l.P)
        {
            var t = p.T;
            if (t.Comment || t.Kind == StTok.Pragma) continue;
            if (t.Text is "(" or "[") paren++;
            else if (t.Text is ")" or "]") paren--;
            else if (paren == 0 && t.Text == ":" && t.Kind == StTok.Punct) return true;
            else if (t.Kind == StTok.Op && t.Text is ":=" or "=>" or "S=" or "R=" or "REF=") return false;
            else if (t.Kind == StTok.Word && StKeywords.IsKeyword(t.Text) && t.Upper != "ELSE") return false;
            else if (t.Text == ";") return false;
        }
        return false;
    }

    // ------------------------------------------------------------------ spacing inside a line
    static bool Wordish(StToken t) => t.Kind is StTok.Word or StTok.Number or StTok.Literal or StTok.String;

    static readonly HashSet<string> BinOps = new() { ":=", "=>", "S=", "R=", "REF=", "=", "<>", "<", ">", "<=", ">=", "+", "-", "*", "/", "**", "&" };

    static void Spacing(Line l, StFormatConfig cfg, StPart part)
    {
        StToken prev = null, prevSig = null;
        bool prevUnary = false;
        for (int i = 0; i < l.P.Count; i++)
        {
            var piece = l.P[i];
            var t = piece.T;
            bool unary = Unary(t, prevSig) && !(i == 0 && l.Cont);
            if (i == 0) { piece.Gap = ""; prev = t; prevUnary = unary; if (!t.Comment && t.Kind != StTok.Pragma) prevSig = t; continue; }
            string orig = piece.Gap;
            string g = prevUnary && !t.Comment && t.Kind != StTok.Pragma ? "" : Gap(prev, t, prevSig, orig, cfg, part, i == l.P.Count - 1, l.Label);
            piece.Gap = g;
            prev = t;
            prevUnary = unary;
            if (!t.Comment && t.Kind != StTok.Pragma) prevSig = t;
        }
        if (cfg.Flag("eol.trim-trailing") && l.P.Count > 0 && l.P[^1].T.Kind == StTok.LineComment)
            l.P[^1].T = l.P[^1].T with { Text = l.P[^1].T.Text.TrimEnd() };
    }

    static string One(string orig, string mode, string want) => mode.Equals("preserve", StringComparison.OrdinalIgnoreCase) ? orig : want;

    static bool Unary(StToken t, StToken before) =>
        t.Kind == StTok.Op && t.Text is "-" or "+" &&
        (before == null || before.Kind == StTok.Op && before.Text != ")" || before.Kind == StTok.Punct && before.Text is "(" or "," or ":" or "[" or ";"
         || before.Kind == StTok.Word && StKeywords.IsKeyword(before.Text) && !(before.Upper is "THIS" or "SUPER"));

    static string Gap(StToken a, StToken b, StToken aSig, string orig, StFormatConfig cfg, StPart part, bool lastOnLine, bool label)
    {
        // comments and pragmas: the comment at the end of a code line, the rest as it was
        if (b.Comment && lastOnLine)
        {
            if (part == StPart.Declaration) return orig.Length == 0 ? " " : " ";
            var mode = cfg["comment.eol-spaces"];
            return int.TryParse(mode, out var n) ? new string(' ', Math.Max(1, n)) : orig;
        }
        if (a.Comment || b.Comment || a.Kind == StTok.Pragma || b.Kind == StTok.Pragma || a.Kind == StTok.Other || b.Kind == StTok.Other) return orig;
        string ops = cfg["space.operators"];
        // punctuation that never takes a space before it
        if (b.Kind == StTok.Punct && b.Text == ";") return "";
        if (b.Kind == StTok.Punct && b.Text == ",") return One(orig, cfg["space.comma"], "");
        if (a.Kind == StTok.Punct && a.Text == ",") return One(orig, cfg["space.comma"], " ");
        // . ^ and ranges
        if (a.Kind == StTok.Op && a.Text == "." || b.Kind == StTok.Op && b.Text == ".") return "";
        if (b.Kind == StTok.Op && b.Text == "^") return "";
        if (a.Kind == StTok.Op && a.Text == ".." || b.Kind == StTok.Op && b.Text == "..") return "";
        // brackets
        bool aOpen = a.Kind == StTok.Punct && a.Text is "(" or "[", bClose = b.Kind == StTok.Punct && b.Text is ")" or "]";
        if (aOpen && bClose) return "";
        if (aOpen || bClose)
        {
            var mode = cfg["space.parens"].ToLowerInvariant();
            return mode == "none" ? "" : mode == "one" ? " " : orig;
        }
        if (b.Kind == StTok.Punct && b.Text == "(")
        {
            if (a.Kind == StTok.Word && StKeywords.IsKeyword(a.Text) && !(a.Upper is "THIS" or "SUPER" or "ADR" or "SIZEOF" or "BITADR" or "__NEW" or "__DELETE"
                or "__QUERYINTERFACE" or "__QUERYPOINTER" or "__ISVALIDREF"))
                return One(orig, cfg["space.keyword-paren"], " ");
            if (a.Kind is StTok.Word or StTok.Literal || a.Text is ")" or "]" or "^")
                return cfg["space.call-paren"].Equals("preserve", StringComparison.OrdinalIgnoreCase) ? orig : "";
        }
        if (b.Kind == StTok.Punct && b.Text == "[")
        {
            if (a.Kind == StTok.Word && !StKeywords.IsKeyword(a.Text) || a.Text is ")" or "]" or "^") return "";
            return orig;
        }
        // colons: declaration or CASE label
        if (b.Kind == StTok.Punct && b.Text == ":")
            return part == StPart.Declaration ? One(orig, cfg["space.decl-colon"], " ") : (label ? "" : orig);
        if (a.Kind == StTok.Punct && a.Text == ":")
            return part == StPart.Declaration ? One(orig, cfg["space.decl-colon"], " ") : (orig.Length == 0 ? " " : " ");
        // operators
        if (b.Kind == StTok.Op && BinOps.Contains(b.Text))
        {
            if (Unary(b, aSig)) return a.Kind == StTok.Punct && a.Text is "(" or "[" ? "" : One(orig, ops, " ");
            return One(orig, ops, " ");
        }
        if (a.Kind == StTok.Op && BinOps.Contains(a.Text))
        {
            return One(orig, ops, " ");
        }
        if (a.Kind == StTok.Op && a.Text == "^") return Wordish(b) ? " " : orig;
        if (Wordish(a) && Wordish(b)) return " ";
        if (a.Kind == StTok.Punct && a.Text is ")" or "]" && Wordish(b)) return " ";
        if (Wordish(a) && b.Kind == StTok.Punct && b.Text is "(") return orig;
        return orig.Length > 0 ? " " : "";
    }

    // ------------------------------------------------------------------ indentation
    static int Width(string s, int tab)
    {
        int w = 0;
        foreach (var c in s) w = c == '\t' ? (w / tab + 1) * tab : w + 1;
        return w;
    }

    static string Spaces(int width, StFormatConfig cfg)
    {
        if (width <= 0) return "";
        if (cfg.IndentUnit == "\t") return new string('\t', width / cfg.TabWidth) + new string(' ', width % cfg.TabWidth);
        return new string(' ', width);
    }

    static void Indentation(List<Line> lines, StFormatConfig cfg)
    {
        string unit = cfg.IndentUnit;
        int tab = cfg.TabWidth;
        int unitW = Width(unit, tab);
        string Levels(int d) => string.Concat(Enumerable.Repeat(unit, Math.Max(0, d)));
        bool preserveCont = !cfg["indent.continuation"].Equals("indent", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Frozen || l.Blank) continue;
            if (l.CommentOnly)
            {
                // a comment or attribute line takes the indent of the code that follows it
                var next = lines.Skip(i + 1).FirstOrDefault(x => !x.Blank && !x.CommentOnly);
                int d = l.DepthHere;
                if (next != null && !next.Cont)
                {
                    var f = next.First;
                    bool closing = f != null && f.Kind == StTok.Word && (f.Upper.StartsWith("END_") || f.Upper is "ELSE" or "ELSIF" or "UNTIL");
                    d = closing ? l.DepthHere : next.Depth;
                }
                l.Indent = Levels(d);
                continue;
            }
            if (l.Cont && l.ContBase != null)
            {
                string baseNew = l.ContBase.Frozen ? l.ContBase.OrigIndent : l.ContBase.Indent;
                int off = Width(l.OrigIndent, tab) - Width(l.ContBase.OrigIndent, tab);
                var f = l.First;
                if (f != null && f.Kind == StTok.Punct && f.Text is ")" or "]" && off <= 0) { l.Indent = baseNew; continue; }
                if (!preserveCont || off <= 0) off = unitW;
                l.Indent = Spaces(Width(baseNew, tab) + off, cfg);
                if (unit == "\t" && off % tab == 0) l.Indent = baseNew + new string('\t', off / tab);
                continue;
            }
            l.Indent = Levels(l.Depth);
        }
    }

    // ------------------------------------------------------------------ aligned declarations
    static void AlignDeclarations(List<Line> lines, StFormatConfig cfg)
    {
        bool colon = cfg.Flag("align.decl"), init = cfg.Flag("align.decl-init"), comments = cfg.Flag("align.eol-comments");
        if (!colon && !init && !comments) return;
        int maxPad = cfg.Int("align.max-pad", 40), tab = cfg.TabWidth;
        var group = new List<(Line L, int Colon, int Assign, int Semi, int Comment)>();
        void Flush()
        {
            if (group.Count > 1) AlignGroup(group, colon, init, comments, maxPad, tab);
            group.Clear();
        }
        foreach (var l in lines)
        {
            if (l.Blank || l.CommentOnly && l.P.All(p => p.T.Comment)) { Flush(); continue; }
            if (l.CommentOnly) continue;   // attribute lines do not break a group
            if (l.Frozen || l.Cont || !l.InBlock) { Flush(); continue; }
            var d = DeclParts(l);
            if (d == null) { Flush(); continue; }
            group.Add((l, d.Value.Colon, d.Value.Assign, d.Value.Semi, d.Value.Comment));
        }
        Flush();
    }

    static (int Colon, int Assign, int Semi, int Comment)? DeclParts(Line l)
    {
        int colon = -1, assign = -1, semi = -1, comment = -1, paren = 0;
        for (int i = 0; i < l.P.Count; i++)
        {
            var t = l.P[i].T;
            if (t.Kind == StTok.Pragma) return null;
            if (t.Comment) { if (i == l.P.Count - 1 && semi >= 0) comment = i; else if (semi < 0) return null; continue; }
            if (semi >= 0) return null;
            if (t.Text is "(" or "[") paren++;
            else if (t.Text is ")" or "]") paren--;
            else if (paren == 0 && t.Kind == StTok.Punct && t.Text == ":" && colon < 0) colon = i;
            else if (paren == 0 && t.Kind == StTok.Op && t.Text == ":=" && colon >= 0 && assign < 0) assign = i;
            else if (paren == 0 && t.Text == ";") semi = i;
        }
        if (colon <= 0 || semi < 0) return null;
        return (colon, assign, semi, comment);
    }

    static int TextWidth(Line l, int from, int to, int tab)
    {
        // width of the pieces from..to-1, with their gaps (the gap of 'from' excluded)
        int w = 0;
        for (int i = from; i < to; i++) w += (i == from ? 0 : l.P[i].Gap.Length) + l.P[i].T.Text.Length;
        return w;
    }

    static void AlignGroup(List<(Line L, int Colon, int Assign, int Semi, int Comment)> g, bool colon, bool init, bool comments, int maxPad, int tab)
    {
        // column of the colon: the longest name part + one space
        if (colon)
        {
            int col = g.Max(x => Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Colon, tab)) + 1;
            if (g.All(x => col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Colon, tab)) <= maxPad))
                foreach (var x in g) x.L.P[x.Colon].Gap = new string(' ', col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Colon, tab)));
        }
        if (init)
        {
            var with = g.Where(x => x.Assign > 0).ToList();
            if (with.Count > 1)
            {
                int col = with.Max(x => Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Assign, tab)) + 1;
                if (with.All(x => col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Assign, tab)) <= maxPad))
                    foreach (var x in with) x.L.P[x.Assign].Gap = new string(' ', col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Assign, tab)));
            }
        }
        if (comments)
        {
            var with = g.Where(x => x.Comment > 0).ToList();
            if (with.Count > 1)
            {
                int col = with.Max(x => Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Comment, tab)) + 1;
                if (with.All(x => col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Comment, tab)) <= maxPad))
                    foreach (var x in with) x.L.P[x.Comment].Gap = new string(' ', col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.Comment, tab)));
            }
        }
    }

    // ------------------------------------------------------------------ long calls: one parameter per line
    static List<Line> WrapCalls(List<Line> lines, StFormatConfig cfg)
    {
        int max = cfg.Int("wrap.max-length", 120);
        if (max <= 0 || !cfg["wrap.call-args"].Equals("when-long", StringComparison.OrdinalIgnoreCase)) return lines;
        var r = new List<Line>();
        int tab = cfg.TabWidth;
        string unit = cfg.IndentUnit;
        foreach (var l in lines)
        {
            if (l.Frozen || l.Blank || l.CommentOnly || l.Cont || Width(l.Indent, tab) + TextWidth(l, 0, l.P.Count, tab) <= max) { r.Add(l); continue; }
            // the first call bracket after a name, its closing bracket followed by ; and nothing but a comment
            int open = -1;
            for (int i = 1; i < l.P.Count; i++)
                if (l.P[i].T.Text == "(" && l.P[i].T.Kind == StTok.Punct && l.P[i - 1].T.Kind == StTok.Word && !StKeywords.IsKeyword(l.P[i - 1].T.Text)) { open = i; break; }
            if (open < 0) { r.Add(l); continue; }
            int depth = 0, close = -1;
            var commas = new List<int>();
            for (int i = open; i < l.P.Count; i++)
            {
                var t = l.P[i].T;
                if (t.Comment || t.Kind == StTok.Pragma) { if (close < 0) { open = -1; break; } continue; }
                if (t.Text is "(" or "[") depth++;
                else if (t.Text is ")" or "]") { depth--; if (depth == 0) { close = i; break; } }
                else if (depth == 1 && t.Text == ",") commas.Add(i);
            }
            bool named = close > 0 && l.P.Skip(open).Take(close - open).Any(p => p.T.Kind == StTok.Op && p.T.Text is ":=" or "=>");
            if (open < 0 || close < 0 || commas.Count == 0 || !named) { r.Add(l); continue; }
            Line Part(int from, int to, string indent, string nl)
            {
                var x = new Line { Newline = nl, Indent = indent, Fixed = true, OrigIndent = indent, Depth = l.Depth };
                for (int i = from; i < to; i++) x.P.Add(new Piece { T = l.P[i].T, Gap = i == from ? "" : l.P[i].Gap });
                return x;
            }
            string nlx = l.Newline ?? "\r\n";
            string inner = l.Indent + unit;
            r.Add(Part(0, open + 1, l.Indent, nlx));
            int start = open + 1;
            foreach (var c in commas) { r.Add(Part(start, c + 1, inner, nlx)); start = c + 1; }
            r.Add(Part(start, close, inner, nlx));
            var lastPart = Part(close, l.P.Count, l.Indent, l.Newline);
            r.Add(lastPart);
        }
        return r;
    }

    // ------------------------------------------------------------------ blank lines
    static List<Line> BlankLines(List<Line> lines, StFormatConfig cfg)
    {
        int max = cfg.Preserve("blank.max") ? int.MaxValue : Math.Max(0, cfg.Int("blank.max", 1));
        bool edges = cfg["blank.body-edges"].Equals("remove", StringComparison.OrdinalIgnoreCase);
        var r = new List<Line>();
        int run = 0;
        foreach (var l in lines)
        {
            bool blank = l.Blank && !l.Frozen;
            if (blank) { run++; if (run > max) continue; if (edges && r.All(x => x.Blank)) continue; }
            else run = 0;
            r.Add(l);
        }
        if (edges) while (r.Count > 1 && r[^1].Blank && !r[^1].Frozen) { r[^2].Newline = r[^1].Newline; r.RemoveAt(r.Count - 1); }
        return r;
    }

    // ------------------------------------------------------------------ output
    static string Render(List<Line> lines, StFormatConfig cfg, bool finalNewline)
    {
        var sb = new StringBuilder();
        string style = cfg["eol.style"].ToLowerInvariant();
        bool trim = cfg.Flag("eol.trim-trailing");
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Frozen) sb.Append(l.Original);
            else if (!l.Blank)
            {
                sb.Append(l.Indent);
                foreach (var p in l.P) sb.Append(p.Gap).Append(p.T.Text);
            }
            else if (!trim) sb.Append(l.Original);
            string nl = l.Newline;
            if (nl == null) continue;
            sb.Append(style == "crlf" ? "\r\n" : style == "lf" ? "\n" : nl);
        }
        return sb.ToString();
    }
}
