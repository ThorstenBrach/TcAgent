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
            var inserted = new List<string>();
            var once = Core(before, cfg, part, r.Notes, out bool parens, inserted);
            // safety net 1: the meaning-carrying tokens are the same (brackets: checked on the trees in StExpr)
            var after = StLexer.Lex(once);
            var a = StLexer.Significant(before, parens);
            var b = StLexer.Significant(after, parens);
            // comments the formatter wrote itself (comment.case-labels) are the only allowed addition
            if (inserted.Count > 0)
            {
                // walk both: an extra comment in the new text must be the next one the formatter wrote, at its place
                var kept = new List<string>();
                int ia = 0, ins = 0;
                foreach (var x in b)
                {
                    if (ia < a.Count && a[ia] == x) { kept.Add(x); ia++; continue; }
                    if (ins < inserted.Count && x == "C:" + inserted[ins].TrimEnd()) { ins++; continue; }
                    kept.Add(x);
                }
                b = kept;
            }
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
            var twice = Core(after, cfg, part, new List<string>(), out _, new List<string>());
            if (twice != once)
            {
                if (Environment.GetEnvironmentVariable("TCAGENT_FORMAT_DEBUG") is { Length: > 0 } dbg)
                { File.WriteAllText(Path.Combine(dbg, "once.st"), once); File.WriteAllText(Path.Combine(dbg, "twice.st"), twice); }
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
        public bool Frozen, Blank, CommentOnly, Cont, Label, InBlock, Fixed, CaseElse;
        public int Depth, DepthHere;
        public Line ContBase;
        public string Original;
        public string Indent = "";
        public StToken First => P.FirstOrDefault(p => !p.T.Comment && p.T.Kind != StTok.Pragma)?.T;
        public StToken Last => P.LastOrDefault(p => !p.T.Comment && p.T.Kind != StTok.Pragma)?.T;
    }

    static string Core(List<StToken> tokens, StFormatConfig cfg, StPart part, List<string> notes, out bool parens, List<string> inserted)
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
        DefaultNewline = toks.FirstOrDefault(t => t.Kind == StTok.Newline)?.Text ?? "\r\n";
        var lines = Split(toks, frozenTok, out bool finalNewline);
        CaseWords(lines, cfg);
        if (part == StPart.Implementation) lines = Restructure(lines, cfg);
        Structure(lines, cfg, part);
        foreach (var l in lines) if (!l.Frozen && !l.Blank) Spacing(l, cfg, part);
        Indentation(lines, cfg);
        ExpandTabs(lines, cfg);
        if (part == StPart.Implementation) lines = CaseBranches(lines, cfg, inserted);
        if (part == StPart.Declaration) AlignDeclarations(lines, cfg);
        if (part == StPart.Implementation)
        {
            lines = JoinShortIfs(lines, cfg);
            lines = ExpandLongIfs(lines, cfg);
            lines = WrapConditions(lines, cfg);
            lines = WrapCalls(lines, cfg);
            AlignCode(lines, cfg);
            lines = BlankAroundBlocks(lines, cfg);
        }
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
                else if (f == "ELSE" && top?.Kind == "CASE") { d = top.LabelDepth; l.CaseElse = true; }
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
            // a name that starts with _ one column further left, so its letters stand under those of the other names
            var under = cfg["indent.underscore"].ToLowerInvariant();
            if (under != "off" && l.Depth > 0 && l.P.Count > 0 && l.P[0].T.Kind == StTok.Word && l.P[0].T.Text.StartsWith("_") && !l.P[0].T.Text.StartsWith("__")
                && (under == "everywhere" || under == "declarations" && l.InBlock))
                l.Indent = Spaces(Width(l.Indent, tab) - 1, cfg);
        }
    }

    /// <summary>space.tabs = replace: a tab between tokens becomes spaces up to the next tab stop (indent.tab-width), so the
    /// text stays in its column. Tabs in comments and strings stay.</summary>
    static void ExpandTabs(List<Line> lines, StFormatConfig cfg)
    {
        if (!cfg["space.tabs"].Equals("replace", StringComparison.OrdinalIgnoreCase)) return;
        int tab = cfg.TabWidth;
        foreach (var l in lines)
        {
            if (l.Frozen || l.Blank) continue;
            int col = Width(l.Indent, tab);
            foreach (var p in l.P)
            {
                if (p.Gap.IndexOf('\t') >= 0)
                {
                    var sb = new StringBuilder();
                    foreach (var c in p.Gap)
                    {
                        if (c == '\t') { int n = tab - (col % tab); sb.Append(' ', n); col += n; }
                        else { sb.Append(c); col++; }
                    }
                    p.Gap = sb.ToString();
                }
                else col += p.Gap.Length;
                col = p.T.Text.Contains('\n') ? 0 : Width(new string('x', col) + p.T.Text, tab);
            }
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

    // ------------------------------------------------------------------ layout: one statement per line, THEN/DO/OF
    static bool Kw(StToken t, params string[] words) => t != null && t.Kind == StTok.Word && words.Contains(t.Upper);
    static readonly string[] Openers = { "THEN", "DO", "OF" };
    static readonly string[] Closers = { "END_IF", "END_CASE", "END_FOR", "END_WHILE", "END_REPEAT", "ELSE", "ELSIF", "UNTIL" };

    [ThreadStatic] static string DefaultNewline;

    static Line Segment(Line l, int from, int to, bool first, bool last = false)
    {
        var x = new Line { Newline = last ? l.Newline : l.Newline ?? DefaultNewline, OrigIndent = first ? l.OrigIndent : "", Original = l.Original };
        for (int i = from; i < to; i++) x.P.Add(new Piece { T = l.P[i].T, Gap = i == from ? "" : l.P[i].Gap });
        x.Blank = x.P.Count == 0;
        return x;
    }

    /// <summary>layout.blocks = expand: one statement per line, the body of IF/CASE/FOR/WHILE/REPEAT on its own lines.
    /// layout.then: THEN, DO and OF at the end of the condition (same-line) or on their own line (own-line).</summary>
    static List<Line> Restructure(List<Line> lines, StFormatConfig cfg)
    {
        bool expand = cfg["layout.blocks"].Equals("expand", StringComparison.OrdinalIgnoreCase);
        string then = cfg["layout.then"].ToLowerInvariant();
        if (!expand && then == "preserve") return lines;
        var r = new List<Line>();
        foreach (var l in lines)
        {
            if (l.Frozen || l.Blank) { r.Add(l); continue; }
            // where the line is cut: before index i
            var cuts = new SortedSet<int>();
            int paren = 0;
            StToken prev = null;
            for (int i = 0; i < l.P.Count; i++)
            {
                var t = l.P[i].T;
                if (t.Comment || t.Kind == StTok.Pragma) continue;
                bool member = prev != null && prev.Kind == StTok.Op && prev.Text == ".";
                bool rest = l.P.Skip(i + 1).Any(p => !p.T.Comment);       // code follows on this line
                if (t.Text is "(" or "[") paren++;
                else if (t.Text is ")" or "]") paren = Math.Max(0, paren - 1);
                else if (!member && paren == 0)
                {
                    if (expand && t.Text == ";" && t.Kind == StTok.Punct && rest) cuts.Add(i + 1);
                    if (expand && Kw(t, "THEN", "DO", "OF", "ELSE", "REPEAT") && rest) cuts.Add(i + 1);
                    if (expand && Kw(t, Closers) && i > 0) cuts.Add(i);
                    if (then == "own-line" && Kw(t, Openers) && i > 0) { cuts.Add(i); if (rest) cuts.Add(i + 1); }
                }
                prev = t;
            }
            // a THEN/DO/OF that starts the line joins the condition above (same-line)
            var target = l;
            if (then == "same-line" && Kw(l.First, Openers) && l.P[0].T == l.First && r.Count > 0)
            {
                var above = r[^1];
                if (!above.Frozen && !above.Blank && !above.CommentOnly && !(above.P.Count > 0 && above.P[^1].T.Comment) && !Kw(above.Last, Openers)
                    && above.Last?.Text != ";")
                {
                    l.P[0].Gap = " ";
                    // the whole line moves up, cut points shift by the length of the line above
                    int shift = above.P.Count;
                    above.P.AddRange(l.P);
                    above.Newline = l.Newline;
                    var moved = new SortedSet<int>(cuts.Select(c => c + shift));
                    r.RemoveAt(r.Count - 1);
                    target = above; cuts = moved;
                }
            }
            if (cuts.Count == 0) { r.Add(target); continue; }
            int start = 0;
            bool firstSeg = true;
            foreach (var c in cuts.Where(c => c > 0 && c < target.P.Count))
            {
                if (c <= start) continue;
                r.Add(Segment(target, start, c, firstSeg)); firstSeg = false; start = c;
            }
            r.Add(Segment(target, start, target.P.Count, firstSeg, true));
        }
        return r;
    }

    // ------------------------------------------------------------------ CASE branches: comments above the labels, blank lines
    /// <summary>comment.case-labels = enum: a comment above each CASE label that has none - the comment of the enum value
    /// (StEnums, from the DUTs of the project), otherwise its name; numbers alone get none. blank.case-branches: a blank line
    /// between the branches (between), after the label line (after-label) or both.</summary>
    static List<Line> CaseBranches(List<Line> lines, StFormatConfig cfg, List<string> inserted)
    {
        bool comments = cfg["comment.case-labels"].Equals("enum", StringComparison.OrdinalIgnoreCase);
        string blank = cfg["blank.case-branches"].ToLowerInvariant();
        if (!comments && blank is not ("between" or "after-label" or "both")) return lines;
        var r = new List<Line>();
        Line Empty(Line like) => new Line { Blank = true, Newline = like.Newline ?? DefaultNewline, Original = "" };
        bool first = true;                     // the first branch after OF gets no blank line above
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!l.Frozen && Kw(l.Last, "OF") && l.P.Any(p => Kw(p.T, "OF"))) first = true;
            bool branch = !l.Frozen && !l.Cont && (l.Label || l.CaseElse);   // a label list over several lines: only its end has the colon
            if (branch)
            {
                // the comment lines right above belong to the label
                int k = r.Count;
                while (k > 0 && r[k - 1].CommentOnly && !r[k - 1].Frozen) k--;
                bool hasComment = k < r.Count;
                if (comments && !hasComment && l.Label && LabelComment(l, cfg) is { } text)
                {
                    var c = new Line { CommentOnly = true, Indent = l.Indent, Newline = DefaultNewline, Original = "", Depth = l.Depth };
                    c.P.Add(new Piece { T = new StToken(StTok.LineComment, text) });
                    r.Add(c); inserted.Add(text);
                    k = r.Count - 1;
                }
                if (blank is "between" or "both" && !first && k > 0 && !r[k - 1].Blank) r.Insert(k, Empty(l));
                first = false;
                r.Add(l);
                // a blank line after the label line, when the statements follow on the next lines
                bool alone = l.P.Count > 0 && (l.P[^1].T.Text == ":" || Kw(l.Last, "ELSE"));
                if (blank is "after-label" or "both" && alone && i + 1 < lines.Count && !lines[i + 1].Blank) r.Add(Empty(l));
                continue;
            }
            r.Add(l);
        }
        return r;
    }

    /// <summary>"// <comment of the enum value>" for each value of the label, or its name; null for numbers only.</summary>
    static string LabelComment(Line l, StFormatConfig cfg)
    {
        var items = new List<string>();
        var cur = new StringBuilder();
        int paren = 0;
        foreach (var p in l.P)
        {
            var t = p.T;
            if (t.Comment || t.Kind == StTok.Pragma) continue;
            if (t.Text is "(" or "[") paren++; else if (t.Text is ")" or "]") paren--;
            if (paren == 0 && t.Kind == StTok.Punct && t.Text is "," or ":") { items.Add(cur.ToString()); cur.Clear(); if (t.Text == ":") break; continue; }
            cur.Append(t.Text);
        }
        var parts = new List<string>();
        bool named = false;
        foreach (var it in items.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (it.Contains("..") || char.IsDigit(it[0]) || it[0] == '-') { parts.Add(it); continue; }
            named = true;
            var key = it.Replace("#", ".");
            if (cfg.EnumComments.TryGetValue(key, out var c)) { parts.Add(c); continue; }
            // an unqualified value: unique among the enums of the project
            var hits = cfg.EnumComments.Where(kv => kv.Key.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).Distinct().ToList();
            parts.Add(hits.Count == 1 ? hits[0] : key.Split('.').Last());
        }
        return named && parts.Count > 0 ? "// " + string.Join(" / ", parts) : null;
    }

    // ------------------------------------------------------------------ short IF on one line
    /// <summary>layout.short-if = N: IF cond THEN / one statement / END_IF becomes one line when it stays within N characters.
    /// Only without ELSE/ELSIF, comments or a statement over several lines.</summary>
    static List<Line> JoinShortIfs(List<Line> lines, StFormatConfig cfg)
    {
        if (!int.TryParse(cfg["layout.short-if"], out int max) || max <= 0) return lines;
        int tab = cfg.TabWidth;
        bool Plain(Line x) => !x.Frozen && !x.Blank && !x.CommentOnly && !x.Cont && !x.Fixed && x.P.All(p => !p.T.Comment && p.T.Kind != StTok.Pragma);
        var r = new List<Line>();
        for (int i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            if (i + 2 < lines.Count && Plain(a) && Kw(a.P[0].T, "IF") && Kw(a.Last, "THEN") && !a.P.Skip(1).Take(a.P.Count - 2).Any(p => Kw(p.T, "THEN", "END_IF")))
            {
                var b = lines[i + 1]; var c = lines[i + 2];
                bool body = Plain(b) && b.Last?.Text == ";" && !Kw(b.First, "IF", "CASE", "FOR", "WHILE", "REPEAT", "ELSE", "ELSIF", "END_IF")
                            && b.P.Count(p => p.T.Text == ";") == 1;
                bool end = Plain(c) && Kw(c.P[0].T, "END_IF") && (c.P.Count == 1 || c.P.Count == 2 && c.P[1].T.Text == ";");
                if (body && end)
                {
                    var j = new Line { Newline = c.Newline, Indent = a.Indent, OrigIndent = a.OrigIndent, Depth = a.Depth, Original = a.Original, Fixed = true };
                    foreach (var x in new[] { a, b, c })
                        for (int k = 0; k < x.P.Count; k++) j.P.Add(new Piece { T = x.P[k].T, Gap = k == 0 ? (x == a ? "" : " ") : x.P[k].Gap });
                    if (Width(j.Indent, tab) + TextWidth(j, 0, j.P.Count, tab) <= max) { r.Add(j); i += 2; continue; }
                }
            }
            r.Add(a);
        }
        return r;
    }

    static bool EndsWithEndIf(Line l)
    {
        var sig = l.P.Where(p => !p.T.Comment).Select(p => p.T).ToList();
        return sig.Count > 0 && (Kw(sig[^1], "END_IF") || sig.Count > 1 && sig[^1].Text == ";" && Kw(sig[^2], "END_IF"));
    }

    /// <summary>The limit of a one-line IF: layout.short-if when it is a number, otherwise wrap.max-length.</summary>
    static int OneLineIfLimit(StFormatConfig cfg) => int.TryParse(cfg["layout.short-if"], out int s) && s > 0 ? s : cfg.Int("wrap.max-length", 120);

    /// <summary>layout.long-if = expand: an IF ... END_IF written on one line that is longer than the limit gets its lines:
    /// IF ... THEN, each statement one level in, ELSIF/ELSE, END_IF. Nested IFs on the line stay as they are.</summary>
    static List<Line> ExpandLongIfs(List<Line> lines, StFormatConfig cfg)
    {
        if (!cfg["layout.long-if"].Equals("expand", StringComparison.OrdinalIgnoreCase)) return lines;
        int limit = OneLineIfLimit(cfg), tab = cfg.TabWidth;
        if (limit <= 0) return lines;
        bool own = cfg["layout.then"].Equals("own-line", StringComparison.OrdinalIgnoreCase);
        string unit = cfg.IndentUnit;
        var r = new List<Line>();
        foreach (var l in lines)
        {
            bool one = !l.Frozen && !l.Blank && !l.CommentOnly && !l.Cont && l.P.Count > 3 && Kw(l.P[0].T, "IF") && EndsWithEndIf(l)
                       && l.P.Count(p => Kw(p.T, "IF")) == 1 && l.P.Count(p => Kw(p.T, "END_IF")) == 1 && l.P.All(p => p.T.Kind != StTok.Pragma)
                       && !l.P.Take(l.P.Count - 1).Any(p => p.T.Comment);
            if (!one || Width(l.Indent, tab) + TextWidth(l, 0, l.P.Count, tab) <= limit) { r.Add(l); continue; }
            var cuts = new SortedSet<int>();
            int depth = 0;
            for (int i = 0; i < l.P.Count; i++)
            {
                var t = l.P[i].T;
                if (t.Text is "(" or "[") depth++; else if (t.Text is ")" or "]") depth--;
                if (depth != 0 || t.Comment) continue;
                bool rest = l.P.Skip(i + 1).Any(p => !p.T.Comment);
                if (Kw(t, "THEN", "ELSE") && rest) cuts.Add(i + 1);
                if (t.Text == ";" && t.Kind == StTok.Punct && rest) cuts.Add(i + 1);
                if (Kw(t, "ELSIF", "ELSE", "END_IF") && i > 0) cuts.Add(i);
                if (own && Kw(t, "THEN") && i > 0) cuts.Add(i);
            }
            int start = 0;
            var segs = new List<(int From, int To)>();
            foreach (var c in cuts) { if (c > start && c < l.P.Count) { segs.Add((start, c)); start = c; } }
            segs.Add((start, l.P.Count));
            for (int s = 0; s < segs.Count; s++)
            {
                var seg = Segment(l, segs[s].From, segs[s].To, s == 0, s == segs.Count - 1);
                var f = seg.P.Count > 0 ? seg.P[0].T : null;
                bool head = s == 0 || Kw(f, "ELSIF", "ELSE", "END_IF", "THEN");
                seg.Indent = head ? l.Indent : l.Indent + unit;
                seg.Fixed = true; seg.Depth = l.Depth + (head ? 0 : 1);
                r.Add(seg);
            }
        }
        return r;
    }

    // ------------------------------------------------------------------ long conditions: one term per line
    static List<Line> WrapConditions(List<Line> lines, StFormatConfig cfg)
    {
        int max = cfg.Int("wrap.max-length", 120);
        if (max <= 0 || !cfg["wrap.conditions"].Equals("when-long", StringComparison.OrdinalIgnoreCase)) return lines;
        bool opFirst = !cfg["wrap.operator"].Equals("last", StringComparison.OrdinalIgnoreCase);
        int tab = cfg.TabWidth;
        string unit = cfg.IndentUnit;
        var r = new List<Line>();
        foreach (var l in lines)
        {
            if (l.Frozen || l.Blank || l.CommentOnly || l.Cont || Width(l.Indent, tab) + TextWidth(l, 0, l.P.Count, tab) <= max) { r.Add(l); continue; }
            // the condition: after IF/ELSIF/WHILE/UNTIL up to THEN/DO (or the end), or the right side of an assignment
            int start = -1, end = l.P.Count;
            var f = l.P[0].T;
            if (Kw(f, "IF", "ELSIF", "WHILE", "UNTIL")) start = 1;
            else for (int i = 0, d0 = 0; i < l.P.Count; i++)
                {
                    var t0 = l.P[i].T;
                    if (t0.Text is "(" or "[") d0++; else if (t0.Text is ")" or "]") d0--;
                    else if (d0 == 0 && t0.Kind == StTok.Op && t0.Text == ":=") { start = i + 1; break; }
                }
            if (start < 0) { r.Add(l); continue; }
            for (int i = start; i < l.P.Count; i++)
                if (Kw(l.P[i].T, "THEN", "DO") || l.P[i].T.Text == ";" || l.P[i].T.Comment) { end = i; break; }
            // the operators at bracket depth 0 of the weakest kind present (OR before XOR before AND)
            var ops = new List<(int I, int Rank)>();
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                var t = l.P[i].T;
                if (t.Text is "(" or "[") depth++;
                else if (t.Text is ")" or "]") depth--;
                else if (depth == 0 && t.Kind == StTok.Word && t.Upper is "OR" or "OR_ELSE") ops.Add((i, 1));
                else if (depth == 0 && t.Kind == StTok.Word && t.Upper == "XOR") ops.Add((i, 2));
                else if (depth == 0 && (t.Kind == StTok.Word && t.Upper is "AND" or "AND_THEN" || t.Kind == StTok.Op && t.Text == "&")) ops.Add((i, 3));
            }
            if (ops.Count == 0 || l.P.Skip(start).Take(end - start).Any(p => p.T.Comment || p.T.Kind == StTok.Pragma)) { r.Add(l); continue; }
            int rank = ops.Min(o => o.Rank);
            var at = ops.Where(o => o.Rank == rank).Select(o => o.I).Where(i => opFirst ? i > start : i + 1 < end).ToList();
            if (at.Count == 0) { r.Add(l); continue; }   // already wrapped: the operator ends the line
            // continuation: operator first - one level in; operator last - under the first term
            int firstCol = Width(l.Indent, tab) + TextWidth(l, 0, start, tab) + (start > 0 ? l.P[start].Gap.Length : 0);
            string contIndent = opFirst ? l.Indent + unit : Spaces(firstCol, cfg);
            int from = 0;
            foreach (var i in at)
            {
                int cut = opFirst ? i : i + 1;
                var seg = Segment(l, from, cut, from == 0);
                seg.Indent = from == 0 ? l.Indent : contIndent; seg.Fixed = true; seg.Depth = l.Depth;
                r.Add(seg);
                from = cut;
            }
            var last = Segment(l, from, l.P.Count, false, true);
            last.Indent = contIndent; last.Fixed = true; last.Depth = l.Depth;
            r.Add(last);
        }
        return r;
    }

    // ------------------------------------------------------------------ alignment in the code
    static void AlignCode(List<Line> lines, StFormatConfig cfg)
    {
        int tab = cfg.TabWidth, maxPad = cfg.Int("align.max-pad", 40);
        void Align(List<(Line L, int I)> g)
        {
            if (g.Count < 2) return;
            int col = g.Max(x => Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.I, tab)) + 1;
            if (g.Any(x => col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.I, tab)) > maxPad)) return;
            foreach (var x in g) x.L.P[x.I].Gap = new string(' ', col - (Width(x.L.Indent, tab) + TextWidth(x.L, 0, x.I, tab)));
        }
        // named parameters, one per line: name := value, / name => target,
        if (cfg.Flag("align.named-args"))
        {
            var g = new List<(Line, int)>();
            string ind = null;
            foreach (var l in lines)
            {
                bool named = !l.Frozen && !l.Blank && (l.Cont || l.Fixed) && l.P.Count >= 3 && l.P[0].T.Kind == StTok.Word
                             && l.P[1].T.Kind == StTok.Op && l.P[1].T.Text is ":=" or "=>";
                if (named && (ind == null || ind == l.Indent)) { g.Add((l, 1)); ind = l.Indent; continue; }
                Align(g); g.Clear(); ind = null;
                if (named) { g.Add((l, 1)); ind = l.Indent; }
            }
            Align(g);
        }
        // consecutive assignments: x := value;
        if (cfg.Flag("align.assignments"))
        {
            var g = new List<(Line, int)>();
            string ind = null;
            foreach (var l in lines)
            {
                int a = -1;
                bool simple = !l.Frozen && !l.Blank && !l.CommentOnly && !l.Cont && !l.Fixed && l.Last?.Text == ";";
                if (simple)
                {
                    int paren = 0;
                    for (int i = 0; i < l.P.Count; i++)
                    {
                        var t = l.P[i].T;
                        if (t.Text is "(" or "[") paren++; else if (t.Text is ")" or "]") paren--;
                        else if (paren == 0 && t.Kind == StTok.Op && t.Text is ":=" or "S=" or "R=" or "REF=") { a = i; break; }
                        else if (t.Kind == StTok.Word && StKeywords.IsKeyword(t.Text) && !(t.Upper is "THIS" or "SUPER")) break;
                    }
                }
                if (a > 0 && (ind == null || ind == l.Indent)) { g.Add((l, a)); ind = l.Indent; continue; }
                Align(g); g.Clear(); ind = null;
                if (a > 0) { g.Add((l, a)); ind = l.Indent; }
            }
            Align(g);
        }
    }

    // ------------------------------------------------------------------ blank lines around blocks
    static List<Line> BlankAroundBlocks(List<Line> lines, StFormatConfig cfg)
    {
        if (!cfg["blank.around-blocks"].Equals("add", StringComparison.OrdinalIgnoreCase)) return lines;
        var r = new List<Line>();
        Line Blank(Line like) => new Line { Blank = true, Newline = like.Newline ?? "\r\n", Original = "" };
        bool OpensBody(Line x) => x == null || Kw(x.Last, "THEN", "DO", "OF", "ELSE", "REPEAT") || x.Label || x.Last?.Text == "(";
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            bool block = !l.Frozen && !l.Blank && !l.CommentOnly && !l.Cont && Kw(l.First, "IF", "CASE", "FOR", "WHILE", "REPEAT") && l.P[0].T == l.First;
            if (block)
            {
                // the comment lines right above belong to the block: the blank line goes above them
                int k = r.Count;
                while (k > 0 && r[k - 1].CommentOnly && !r[k - 1].Frozen) k--;
                var before = k > 0 ? r[k - 1] : null;
                if (before != null && !before.Blank && !OpensBody(before)) r.Insert(k, Blank(before));
            }
            r.Add(l);
            bool closes = !l.Frozen && !l.Blank && Kw(l.First, "END_IF", "END_CASE", "END_FOR", "END_WHILE", "END_REPEAT") && l.Last?.Text is not null;
            if (closes && i + 1 < lines.Count)
            {
                var next = lines[i + 1];
                bool closing = Kw(next.First, Closers) || next.Last?.Text == ")" && next.Cont;
                if (!next.Blank && !closing && !(next.Label)) r.Add(Blank(l));
            }
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
