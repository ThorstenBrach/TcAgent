// Expressions of ST as trees - for the brackets of the formatter (paren.precedence) and its safety net.
//
//   Order (IEC 61131-3): brackets, calls; NOT and the sign; **; * / MOD; + -; < > <= >=; = <>; AND & AND_THEN; XOR; OR OR_ELSE.
//   AddPrecedenceParens puts brackets around every AND (or XOR) group that stands directly in an OR (or an AND group in an
//   XOR) - the order that already applies becomes visible, the meaning stays. SameTrees compares the trees of two texts
//   with the brackets left out: the safety net for exactly this rule.
//
// SPDX-License-Identifier: MIT
using System.Text;

namespace TcAgent;

public static class StExpr
{
    sealed class Node
    {
        public string Op;              // "OR", "AND", "+", "NOT", "call", "leaf" ...
        public int Start, End;         // indices into the significant token list, brackets included when Paren
        public bool Paren;
        public readonly List<Node> Kids = new();
        public string Leaf;
    }

    sealed class ParseFail : Exception { }

    sealed class Parser
    {
        readonly List<StToken> s;
        public int Pos;
        public Parser(List<StToken> sig, int pos) { s = sig; Pos = pos; }
        StToken Peek(int k = 0) => Pos + k < s.Count ? s[Pos + k] : null;

        static int BinPrec(StToken t)
        {
            if (t == null) return 0;
            if (t.Kind == StTok.Word)
                return t.Upper switch { "OR" or "OR_ELSE" => 1, "XOR" => 2, "AND" or "AND_THEN" => 3, "MOD" => 7, _ => 0 };
            if (t.Kind == StTok.Op)
                return t.Text switch { "&" => 3, "=" or "<>" => 4, "<" or ">" or "<=" or ">=" => 5, "+" or "-" => 6, "*" or "/" => 7, "**" => 8, _ => 0 };
            return 0;
        }

        static string OpName(StToken t) => t.Kind == StTok.Word ? (t.Upper == "AND_THEN" ? "AND_THEN" : t.Upper == "OR_ELSE" ? "OR_ELSE" : t.Upper) : t.Text == "&" ? "AND" : t.Text;

        public Node Expr(int min = 1)
        {
            var left = Unary();
            while (true)
            {
                var op = Peek();
                int p = BinPrec(op);
                if (p == 0 || p < min) break;
                Pos++;
                var right = Expr(op.Text == "**" ? p : p + 1);
                var n = new Node { Op = OpName(op), Start = left.Start, End = right.End };
                // a chain of the same operator stays one node (a AND b AND c)
                if (left.Op == n.Op && !left.Paren) n.Kids.AddRange(left.Kids); else n.Kids.Add(left);
                n.Kids.Add(right);
                left = n;
            }
            return left;
        }

        Node Unary()
        {
            var t = Peek() ?? throw new ParseFail();
            if (t.Kind == StTok.Word && t.Upper == "NOT" || t.Kind == StTok.Op && t.Text is "-" or "+")
            {
                int st = Pos; Pos++;
                var k = Unary();
                var n = new Node { Op = "u" + (t.Kind == StTok.Word ? "NOT" : t.Text), Start = st, End = k.End };
                n.Kids.Add(k);
                return n;
            }
            return Postfix(Primary());
        }

        Node Primary()
        {
            var t = Peek() ?? throw new ParseFail();
            int st = Pos;
            if (t.Kind == StTok.Punct && t.Text == "(")
            {
                Pos++;
                var e = Expr();
                if (Peek()?.Text != ")") throw new ParseFail();
                Pos++;
                e.Start = st; e.End = Pos - 1; e.Paren = true;
                return e;
            }
            if (t.Kind is StTok.Number or StTok.Literal or StTok.String) { Pos++; return new Node { Op = "leaf", Leaf = t.Upper, Start = st, End = st }; }
            if (t.Kind == StTok.Word)
            {
                if (StKeywords.IsKeyword(t.Text) && !(t.Upper is "THIS" or "SUPER" or "ADR" or "SIZEOF" or "BITADR" or "__NEW" or "__QUERYINTERFACE" or "__QUERYPOINTER" or "__ISVALIDREF"))
                    throw new ParseFail();
                Pos++;
                return new Node { Op = "leaf", Leaf = StKeywords.IsCaseFree(t.Text) ? t.Upper : t.Text, Start = st, End = st };
            }
            throw new ParseFail();
        }

        Node Postfix(Node n)
        {
            while (true)
            {
                var t = Peek();
                if (t == null) return n;
                if (t.Kind == StTok.Op && t.Text == "." && Peek(1)?.Kind == StTok.Word)
                {
                    Pos += 2;
                    var m = new Node { Op = ".", Start = n.Start, End = Pos - 1, Leaf = s[Pos - 1].Text };
                    m.Kids.Add(n); n = m; continue;
                }
                if (t.Kind == StTok.Op && t.Text == "^") { Pos++; var m = new Node { Op = "^", Start = n.Start, End = Pos - 1 }; m.Kids.Add(n); n = m; continue; }
                if (t.Kind == StTok.Punct && t.Text == "[")
                {
                    Pos++;
                    var m = new Node { Op = "[]", Start = n.Start }; m.Kids.Add(n);
                    while (true) { m.Kids.Add(Expr()); if (Peek()?.Text == ",") { Pos++; continue; } break; }
                    if (Peek()?.Text != "]") throw new ParseFail();
                    Pos++; m.End = Pos - 1; n = m; continue;
                }
                if (t.Kind == StTok.Punct && t.Text == "(")
                {
                    Pos++;
                    var m = new Node { Op = "call", Start = n.Start }; m.Kids.Add(n);
                    if (Peek()?.Text == ")") { Pos++; m.End = Pos - 1; n = m; continue; }
                    while (true)
                    {
                        // a named parameter: name := value, name => target, NOT name => target
                        if (Peek()?.Kind == StTok.Word && Peek(1)?.Kind == StTok.Op && Peek(1).Text is ":=" or "=>")
                        { m.Kids.Add(new Node { Op = "name", Leaf = Peek().Text + Peek(1).Text, Start = Pos, End = Pos + 1 }); Pos += 2; }
                        else if (Peek()?.Upper == "NOT" && Peek(1)?.Kind == StTok.Word && Peek(2)?.Text == "=>")
                        { m.Kids.Add(new Node { Op = "name", Leaf = "NOT " + Peek(1).Text + "=>", Start = Pos, End = Pos + 2 }); Pos += 3; }
                        m.Kids.Add(Expr());
                        if (Peek()?.Text == ",") { Pos++; continue; }
                        break;
                    }
                    if (Peek()?.Text != ")") throw new ParseFail();
                    Pos++; m.End = Pos - 1; n = m; continue;
                }
                return n;
            }
        }
    }

    static List<(StToken T, int Index)> Sig(List<StToken> tokens)
    {
        var r = new List<(StToken, int)>();
        for (int i = 0; i < tokens.Count; i++)
            if (!tokens[i].Trivia && !tokens[i].Comment && tokens[i].Kind != StTok.Pragma) r.Add((tokens[i], i));
        return r;
    }

    /// <summary>All expressions of an implementation: conditions, right sides, call arguments, FOR bounds.</summary>
    static List<Node> Expressions(List<StToken> sig)
    {
        var r = new List<Node>();
        int i = 0;
        Node Try(int at)
        {
            try { var p = new Parser(sig, at); var n = p.Expr(); return n; }
            catch (ParseFail) { return null; }
        }
        while (i < sig.Count)
        {
            var t = sig[i];
            bool trigger = t.Kind == StTok.Word && t.Upper is "IF" or "ELSIF" or "WHILE" or "UNTIL" or "CASE" or "TO" or "BY" or "RETURN"
                        || t.Kind == StTok.Op && t.Text is ":=" or "S=" or "R=" or "REF=";
            bool statement = t.Kind == StTok.Word && !StKeywords.IsKeyword(t.Text) && (i == 0 || sig[i - 1].Text is ";" or ":" || sig[i - 1].Kind == StTok.Word && sig[i - 1].Upper is "THEN" or "ELSE" or "DO" or "REPEAT" or "OF");
            if (trigger && i + 1 < sig.Count)
            {
                var n = Try(i + 1);
                if (n != null) { r.Add(n); i = n.End + 1; continue; }
            }
            else if (statement)
            {
                // a call statement or the left side of an assignment, with its arguments
                var n = Try(i);
                if (n != null) { r.Add(n); i = n.End + 1; continue; }
            }
            i++;
        }
        return r;
    }

    static void Walk(Node n, Action<Node> f) { f(n); foreach (var k in n.Kids) Walk(k, f); }

    static bool Logical(string op) => op is "OR" or "OR_ELSE" or "XOR" or "AND" or "AND_THEN";

    /// <summary>Brackets around AND groups in an OR and around AND groups in an XOR. Returns the new tokens, the frozen marks
    /// moved with them, and how many pairs were added.</summary>
    public static (List<StToken> Tokens, bool[] Frozen, int Added) AddPrecedenceParens(List<StToken> tokens, bool[] frozen)
    {
        var sig = Sig(tokens);
        var sigTok = sig.Select(x => x.T).ToList();
        var inserts = new List<(int Before, int After)>();   // token indices: '(' before, ')' after
        foreach (var e in Expressions(sigTok))
            Walk(e, n =>
            {
                if (!(n.Op is "OR" or "OR_ELSE" or "XOR")) return;
                foreach (var k in n.Kids)
                {
                    bool needs = !k.Paren && Logical(k.Op) && k.Op != n.Op && !(n.Op is "OR" or "OR_ELSE" && k.Op is "OR" or "OR_ELSE");
                    if (!needs) continue;
                    int a = sig[k.Start].Index, b = sig[k.End].Index;
                    bool anyFrozen = false;
                    for (int x = a; x <= b; x++) if (frozen[x]) anyFrozen = true;
                    if (!anyFrozen) inserts.Add((a, b));
                }
            });
        if (inserts.Count == 0) return (tokens, frozen, 0);
        var opens = inserts.GroupBy(x => x.Before).ToDictionary(g => g.Key, g => g.Count());
        var closes = inserts.GroupBy(x => x.After).ToDictionary(g => g.Key, g => g.Count());
        var t2 = new List<StToken>();
        var f2 = new List<bool>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (opens.TryGetValue(i, out var o)) for (int k = 0; k < o; k++) { t2.Add(new StToken(StTok.Punct, "(")); f2.Add(false); }
            t2.Add(tokens[i]); f2.Add(frozen[i]);
            if (closes.TryGetValue(i, out var c)) for (int k = 0; k < c; k++) { t2.Add(new StToken(StTok.Punct, ")")); f2.Add(false); }
        }
        return (t2, f2.ToArray(), inserts.Count);
    }

    static string Canon(Node n)
    {
        var sb = new StringBuilder();
        void W(Node x)
        {
            sb.Append(x.Op);
            if (x.Leaf != null) sb.Append('[').Append(x.Leaf).Append(']');
            if (x.Kids.Count > 0) { sb.Append('('); for (int i = 0; i < x.Kids.Count; i++) { if (i > 0) sb.Append(','); W(x.Kids[i]); } sb.Append(')'); }
        }
        W(n);
        return sb.ToString();
    }

    /// <summary>Do both texts have the same expression trees (brackets left out)?</summary>
    public static bool SameTrees(List<StToken> a, List<StToken> b)
    {
        var x = Expressions(Sig(a).Select(s => s.T).ToList()).Select(Canon).ToList();
        var y = Expressions(Sig(b).Select(s => s.T).ToList()).Select(Canon).ToList();
        return x.SequenceEqual(y);
    }
}
