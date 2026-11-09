// Conditions as logic: the tree of AND / OR / XOR / NOT over sub-conditions (atoms), its truth table, rewrites (De Morgan,
// simplify, write out in full, = TRUE uniform) and the proof that a rewrite means the same - every combination of the atoms is
// evaluated (up to 20 atoms). Used by the Conditions window of the IDE (TcAgent.exe condition) and tc_condition.
//
//   Atoms: a name, a comparison, a call ... - two atoms are the same when their tokens are the same. "x = TRUE" is the atom x,
//   "x = FALSE" and "x <> TRUE" are NOT x. A call may have side effects: a condition with a call is never simplified (that could
//   drop or repeat the call); AND_THEN / OR_ELSE fix the order: such a condition is not rewritten at all.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed class LNode
{
    public string Op;                         // AND, OR, XOR, NOT, atom, const
    public readonly List<LNode> Kids = new();
    public int Atom = -1;                     // index into the atoms
    public bool Value;                        // const
    public LNode(string op) { Op = op; }
    public static LNode Not(LNode k) { var n = new LNode("NOT"); n.Kids.Add(k); return n; }
    public static LNode Of(string op, IEnumerable<LNode> kids) { var n = new LNode(op); n.Kids.AddRange(kids); return n; }
    public int Prec => Op switch { "OR" => 1, "XOR" => 2, "AND" => 3, "NOT" => 4, _ => 5 };
}

public sealed class LExpr
{
    public LNode Root;
    public readonly List<string> Atoms = new();          // the text of each atom
    public readonly List<bool> Calls = new();            // the atom contains a call
    public bool ShortCircuit;                            // AND_THEN / OR_ELSE
    public bool HasCompare;                              // some atom is a comparison (not = TRUE / = FALSE)
    public string Keyword = "", Then = "";               // IF ... THEN around it, when given
    public string Source = "";                           // the condition as written (tokens, one space)

    public int AtomOf(string text, bool call)
    {
        int i = Atoms.IndexOf(text);
        if (i >= 0) return i;
        Atoms.Add(text); Calls.Add(call);
        return Atoms.Count - 1;
    }
}

public static class StLogic
{
    sealed class Fail : Exception { public Fail(string m) : base(m) { } }

    /// <summary>A condition - "IF a AND b THEN", "a AND b" or "x := a AND b;" - as a logic tree.</summary>
    public static LExpr Parse(string text)
    {
        var all = StLexer.Lex(text ?? "").Where(t => !t.Trivia && !t.Comment && t.Kind != StTok.Pragma).ToList();
        var e = new LExpr();
        int from = 0, to = all.Count;
        if (all.Count > 0 && all[0].Kind == StTok.Word && all[0].Upper is "IF" or "ELSIF" or "WHILE" or "UNTIL") { e.Keyword = all[0].Upper; from = 1; }
        for (int i = from; i < all.Count; i++)
            if (all[i].Kind == StTok.Word && all[i].Upper is "THEN" or "DO" || all[i].Text == ";") { e.Then = all[i].Upper; to = i; break; }
        for (int i = from, d = 0; i < to && e.Keyword == ""; i++)
        {
            if (all[i].Text is "(" or "[") d++; else if (all[i].Text is ")" or "]") d--;
            else if (d == 0 && all[i].Kind == StTok.Op && all[i].Text == ":=") { e.Keyword = StLexer.Join(all.Take(i + 1).Select((t, k) => k == 0 ? t : t with { Text = " " + t.Text })); from = i + 1; break; }
        }
        var s = all.Skip(from).Take(to - from).ToList();
        if (s.Count == 0) throw new ArgumentException("no condition found");
        e.Source = Text(s);
        int pos = 0;
        StToken Peek(int k = 0) => pos + k < s.Count ? s[pos + k] : null;
        static int Prec(StToken t) => t == null ? 0 : t.Kind == StTok.Word ? t.Upper switch { "OR" or "OR_ELSE" => 1, "XOR" => 2, "AND" or "AND_THEN" => 3, _ => 0 } : t.Kind == StTok.Op && t.Text == "&" ? 3 : 0;
        LNode Level(int prec)
        {
            if (prec > 3) return Unary();
            var left = Level(prec + 1);
            while (Prec(Peek()) == prec)
            {
                var op = s[pos++];
                if (op.Upper is "AND_THEN" or "OR_ELSE") e.ShortCircuit = true;
                var right = Level(prec + 1);
                string key = prec == 1 ? "OR" : prec == 2 ? "XOR" : "AND";
                if (left.Op == key) left.Kids.Add(right); else left = LNode.Of(key, new[] { left, right });
            }
            return left;
        }
        int Match(int open)
        {
            for (int i = open, d = 0; i < s.Count; i++)
            {
                if (s[i].Text is "(" or "[") d++;
                else if (s[i].Text is ")" or "]" && --d == 0) return i;
            }
            return -1;
        }
        LNode Unary()
        {
            var t = Peek() ?? throw new Fail("the condition ends too early");
            if (t.Kind == StTok.Word && t.Upper == "NOT")
            {
                int start = pos; pos++;
                // NOT a = b is (NOT a) = b: the whole is one atom
                if (Peek() is { } nx && !(nx.Kind == StTok.Punct && nx.Text == "(") && !(nx.Kind == StTok.Word && nx.Upper == "NOT"))
                {
                    int end = pos, dd = 0;
                    while (end < s.Count)
                    {
                        var x = s[end];
                        if (x.Text is "(" or "[") dd++;
                        else if (x.Text is ")" or "]") { if (dd == 0) break; dd--; }
                        else if (dd == 0 && Prec(x) > 0) break;
                        end++;
                    }
                    if (Binary(s, pos, end)) { pos = end; return Atom(start, end); }
                }
                var k = Unary();
                return LNode.Not(k);
            }
            if (t.Kind == StTok.Punct && t.Text == "(")
            {
                int close = Match(pos);
                var after = close >= 0 && close + 1 < s.Count ? s[close + 1] : null;
                if (close > 0 && (after == null || Prec(after) > 0 || after.Text == ")"))
                {
                    int save = pos; pos++;
                    try { var inner = Level(1); if (pos == close) { pos++; return inner; } } catch (Fail) { }
                    pos = save;
                }
            }
            int st = pos, d = 0;
            while (pos < s.Count)
            {
                var x = s[pos];
                if (x.Text is "(" or "[") d++;
                else if (x.Text is ")" or "]") { if (d == 0) break; d--; }
                else if (d == 0 && Prec(x) > 0) break;
                pos++;
            }
            if (pos == st) throw new Fail($"cannot read the condition at '{s[st].Text}'");
            return Atom(st, pos);
        }
        LNode Atom(int a, int b)
        {
            var toks = s.GetRange(a, b - a);
            if (toks.Count == 1 && toks[0].Kind == StTok.Word && toks[0].Upper is "TRUE" or "FALSE") return new LNode("const") { Value = toks[0].Upper == "TRUE" };
            // x = TRUE / x = FALSE / x <> TRUE / x <> FALSE: the atom x, maybe negated
            if (toks.Count >= 3 && toks[^2].Kind == StTok.Op && toks[^2].Text is "=" or "<>" && toks[^1].Kind == StTok.Word && toks[^1].Upper is "TRUE" or "FALSE"
                && !Binary(toks, 0, toks.Count - 2))
            {
                var inner = toks.Take(toks.Count - 2).ToList();
                bool neg = (toks[^2].Text == "<>") ^ (toks[^1].Upper == "FALSE");
                var at = new LNode("atom") { Atom = e.AtomOf(Text(inner), IsCall(inner)) };
                return neg ? LNode.Not(at) : at;
            }
            if (Binary(toks, 0, toks.Count) && toks.Any(x => x.Kind == StTok.Op && x.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")) e.HasCompare = true;
            return new LNode("atom") { Atom = e.AtomOf(Text(toks), IsCall(toks)) };
        }
        e.Root = Level(1);
        if (pos != s.Count) throw new Fail($"cannot read the condition at '{s[pos].Text}'");
        return e;
    }

    static bool Binary(List<StToken> t, int from, int to)
    {
        for (int i = from, d = 0; i < to; i++)
        {
            if (t[i].Text is "(" or "[") d++; else if (t[i].Text is ")" or "]") d--;
            else if (d == 0 && (t[i].Kind == StTok.Op && t[i].Text is not ("." or "^") || t[i].Kind == StTok.Word && t[i].Upper == "MOD")) return true;
        }
        return false;
    }

    static bool IsCall(List<StToken> t)
    {
        for (int i = 1; i < t.Count; i++)
            if (t[i].Text == "(" && t[i].Kind == StTok.Punct && (t[i - 1].Kind == StTok.Word && !StKeywords.IsKeyword(t[i - 1].Text) || t[i - 1].Text == ")")) return true;
        return false;
    }

    /// <summary>The tokens of an atom as text: one space between words and around operators, none around . ( ) [ ] ,.</summary>
    static string Text(List<StToken> t)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (i > 0)
            {
                var p = t[i - 1];
                bool tight = p.Text is "(" or "[" or "." or "^" || x.Text is ")" or "]" or "." or "," or "^" || x.Text is "(" or "[" && p.Kind == StTok.Word && !StKeywords.IsWordOperator(p.Text) && p.Upper != "NOT";
                if (!tight) sb.Append(' ');
            }
            sb.Append(x.Kind == StTok.Word && StKeywords.IsCaseFree(x.Text) ? x.Upper : x.Text);
        }
        return sb.ToString().Replace(" ,", ",");
    }

    // ------------------------------------------------------------------ evaluation and proof
    public static bool Eval(LNode n, ulong v) => n.Op switch
    {
        "atom" => (v >> n.Atom & 1) == 1,
        "const" => n.Value,
        "NOT" => !Eval(n.Kids[0], v),
        "AND" => n.Kids.All(k => Eval(k, v)),
        "OR" => n.Kids.Any(k => Eval(k, v)),
        "XOR" => n.Kids.Aggregate(false, (a, k) => a ^ Eval(k, v)),
        _ => throw new InvalidOperationException(n.Op)
    };

    public const int MaxAtoms = 20;

    /// <summary>Do both trees (over the same atoms) give the same result for every combination? null: too many atoms to prove.</summary>
    public static bool? Same(LNode a, LNode b, int atoms)
    {
        if (atoms > MaxAtoms) return null;
        for (ulong v = 0; v < 1UL << atoms; v++) if (Eval(a, v) != Eval(b, v)) return false;
        return true;
    }

    /// <summary>The combinations that make the condition true, as prime implicants: one row per implicant, '1', '0' or '-' per atom.</summary>
    public static List<string> TrueRows(LNode n, int atoms, int maxRows = 64)
    {
        if (atoms > 12) return null;
        var minterms = new List<ulong>();
        for (ulong v = 0; v < 1UL << atoms; v++) if (Eval(n, v)) minterms.Add(v);
        return Cover(minterms, atoms).Take(maxRows).ToList();
    }

    /// <summary>Quine-McCluskey: prime implicants, then a cover (essential ones first, then the one covering most).</summary>
    static List<string> Cover(List<ulong> minterms, int atoms)
    {
        if (minterms.Count == 0) return new List<string>();
        string Bits(ulong v) => new string(Enumerable.Range(0, atoms).Select(i => (v >> i & 1) == 1 ? '1' : '0').ToArray());
        var current = minterms.Select(Bits).Distinct().ToHashSet();
        var primes = new HashSet<string>();
        while (current.Count > 0)
        {
            var next = new HashSet<string>();
            var used = new HashSet<string>();
            var list = current.ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    int diff = -1, count = 0;
                    for (int k = 0; k < atoms && count < 2; k++)
                        if (list[i][k] != list[j][k]) { if (list[i][k] == '-' || list[j][k] == '-') { count = 2; break; } diff = k; count++; }
                    if (count != 1) continue;
                    var m = list[i].ToCharArray(); m[diff] = '-';
                    next.Add(new string(m)); used.Add(list[i]); used.Add(list[j]);
                }
            foreach (var x in list) if (!used.Contains(x)) primes.Add(x);
            current = next;
        }
        bool Covers(string p, ulong v) { for (int k = 0; k < atoms; k++) if (p[k] != '-' && (p[k] == '1') != ((v >> k & 1) == 1)) return false; return true; }
        var open = minterms.ToHashSet();
        var chosen = new List<string>();
        foreach (var v in minterms)
        {
            var by = primes.Where(p => Covers(p, v)).ToList();
            if (by.Count == 1 && !chosen.Contains(by[0])) { chosen.Add(by[0]); open.RemoveWhere(x => Covers(by[0], x)); }
        }
        while (open.Count > 0)
        {
            var best = primes.Except(chosen).OrderByDescending(p => open.Count(x => Covers(p, x))).ThenBy(p => p.Count(c => c != '-')).First();
            chosen.Add(best); open.RemoveWhere(x => Covers(best, x));
        }
        // in the order of the sub-conditions: the group that starts with the earlier one first
        return chosen.OrderBy(p => p.IndexOf(p.FirstOrDefault(c => c != '-'))).ThenBy(p => new string(p.Select(c => c == '-' ? '2' : c == '1' ? '0' : '1').ToArray()), StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------ rewrites
    static LNode Copy(LNode n) { var c = new LNode(n.Op) { Atom = n.Atom, Value = n.Value }; c.Kids.AddRange(n.Kids.Select(Copy)); return c; }

    static LNode Flatten(LNode n)
    {
        var kids = n.Kids.Select(Flatten).ToList();
        var r = new LNode(n.Op) { Atom = n.Atom, Value = n.Value };
        foreach (var k in kids)
            if (k.Op == n.Op && n.Op is "AND" or "OR") r.Kids.AddRange(k.Kids); else r.Kids.Add(k);
        return r;
    }

    /// <summary>NOT moved inward (De Morgan) as far as it goes; NOT NOT x is x.</summary>
    public static LNode NotInward(LNode n, bool neg = false)
    {
        switch (n.Op)
        {
            case "NOT": return NotInward(n.Kids[0], !neg);
            case "AND": case "OR":
                var op = neg ? (n.Op == "AND" ? "OR" : "AND") : n.Op;
                return Flatten(LNode.Of(op, n.Kids.Select(k => NotInward(k, neg))));
            case "const": return new LNode("const") { Value = n.Value ^ neg };
            default:
                var c = n.Op == "XOR" ? LNode.Of("XOR", n.Kids.Select(k => NotInward(k))) : Copy(n);
                return neg ? LNode.Not(c) : c;
        }
    }

    /// <summary>NOT moved outward (De Morgan): an AND or OR whose parts are all negated becomes one NOT around the other group.</summary>
    public static LNode NotOutward(LNode n)
    {
        var kids = n.Kids.Select(NotOutward).ToList();
        if (n.Op is "AND" or "OR" && kids.Count > 1 && kids.All(k => k.Op == "NOT"))
            return LNode.Not(Flatten(LNode.Of(n.Op == "AND" ? "OR" : "AND", kids.Select(k => k.Kids[0]))));
        var r = new LNode(n.Op) { Atom = n.Atom, Value = n.Value };
        if (n.Op is "AND" or "OR")
        {
            // a run of negated parts next to each other becomes one NOT: a AND NOT b AND NOT c → a AND NOT (b OR c)
            for (int i = 0; i < kids.Count;)
            {
                int j = i;
                while (j < kids.Count && kids[j].Op == "NOT") j++;
                if (j - i >= 2) { r.Kids.Add(LNode.Not(Flatten(LNode.Of(n.Op == "AND" ? "OR" : "AND", kids.Skip(i).Take(j - i).Select(k => k.Kids[0]))))); i = j; }
                else { r.Kids.Add(kids[i]); i++; }
            }
            return r;
        }
        r.Kids.AddRange(kids);
        return r;
    }

    /// <summary>The shortest OR of AND groups with the same truth table (Quine-McCluskey).</summary>
    public static LNode Simplify(LNode n, int atoms) => FromRows(TrueRows(n, atoms, 1000), atoms);

    /// <summary>Every combination that makes it true written out: one AND group per combination over all atoms.</summary>
    public static LNode WriteOut(LNode n, int atoms)
    {
        if (atoms > 6) return null;
        var rows = new List<string>();
        for (ulong v = 0; v < 1UL << atoms; v++)
            if (Eval(n, v)) rows.Add(new string(Enumerable.Range(0, atoms).Select(i => (v >> i & 1) == 1 ? '1' : '0').ToArray()));
        if (rows.Count > 8) return null;                 // more than 8 groups helps nobody read it
        return FromRows(rows, atoms);
    }

    static LNode FromRows(List<string> rows, int atoms)
    {
        if (rows == null) return null;
        if (rows.Count == 0) return new LNode("const") { Value = false };
        LNode Group(string r)
        {
            var parts = Enumerable.Range(0, atoms).Where(i => r[i] != '-').Select(i => { var a = new LNode("atom") { Atom = i }; return r[i] == '1' ? a : LNode.Not(a); }).ToList();
            return parts.Count == 0 ? new LNode("const") { Value = true } : parts.Count == 1 ? parts[0] : LNode.Of("AND", parts);
        }
        var groups = rows.Select(Group).ToList();
        return groups.Count == 1 ? groups[0] : LNode.Of("OR", groups);
    }

    /// <summary>The atoms that are used, in the order they appear.</summary>
    public static IEnumerable<int> Used(LNode n) => n.Op == "atom" ? new[] { n.Atom } : n.Kids.SelectMany(Used);

    // ------------------------------------------------------------------ text
    /// <summary>ST text of a tree. boolCompare: "keep" (x, NOT x), "always" (x = TRUE, x = FALSE).</summary>
    public static string ToText(LExpr e, LNode n, string boolCompare = "keep")
    {
        string W(LNode x, int parentPrec)
        {
            string s;
            switch (x.Op)
            {
                case "atom": s = e.Atoms[x.Atom] + (boolCompare == "always" && IsBoolAtom(e, x.Atom) ? " = TRUE" : ""); return s;
                case "const": return x.Value ? "TRUE" : "FALSE";
                case "NOT":
                    if (boolCompare == "always" && x.Kids[0].Op == "atom" && IsBoolAtom(e, x.Kids[0].Atom)) return e.Atoms[x.Kids[0].Atom] + " = FALSE";
                    var k = x.Kids[0];
                    s = "NOT " + (k.Op is "atom" or "const" && !Binaryish(e, k) ? W(k, 4) : "(" + W(k, 0) + ")");
                    return s;
                default:
                    s = string.Join(" " + x.Op + " ", x.Kids.Select(c => W(c, x.Prec + (c.Op == x.Op ? 0 : 0))));
                    return x.Prec < parentPrec || x.Prec == parentPrec && parentPrec != 0 ? "(" + s + ")" : s;
            }
        }
        return W(n, 0);
    }

    static bool Binaryish(LExpr e, LNode atom) => atom.Op == "atom" && Binary(StLexer.Lex(e.Atoms[atom.Atom]).Where(t => !t.Trivia).ToList(), 0, StLexer.Lex(e.Atoms[atom.Atom]).Count(t => !t.Trivia));

    /// <summary>An atom that is a BOOL by its form: a name, member, bit or array element - not a comparison or arithmetic.</summary>
    static bool IsBoolAtom(LExpr e, int i)
    {
        var t = StLexer.Lex(e.Atoms[i]).Where(x => !x.Trivia).ToList();
        return !Binary(t, 0, t.Count);
    }

    // ------------------------------------------------------------------ online: values, three-valued logic, what is missing
    /// <summary>The value of a sub-condition from the values of its operands: a BOOL operand, or a comparison of an operand with
    /// another operand, a number, TRUE/FALSE, a string or an enum value (Type.VALUE). null = unknown (not readable, arithmetic ...).</summary>
    public static bool? EvalAtom(string atom, Func<string, object> value)
    {
        var t = StLexer.Lex(atom ?? "").Where(x => !x.Trivia && !x.Comment).ToList();
        if (t.Count > 0 && t[0].Kind == StTok.Word && t[0].Upper == "NOT")
        {
            // NOT a = b: (NOT a) = b - only the plain NOT x here
            if (Binary(t, 1, t.Count)) return null;
            return !EvalAtom(Text(t.Skip(1).ToList()), value);
        }
        int at = -1;
        for (int i = 0, d = 0; i < t.Count; i++)
        {
            if (t[i].Text is "(" or "[") d++; else if (t[i].Text is ")" or "]") d--;
            else if (d == 0 && t[i].Kind == StTok.Op && t[i].Text is "=" or "<>" or "<" or ">" or "<=" or ">=") { if (at >= 0) return null; at = i; }
            else if (d == 0 && t[i].Kind == StTok.Op && t[i].Text is not ("." or "^")) return null;      // arithmetic: not evaluated
        }
        if (at < 0) return value(Text(t)) is bool b ? b : null;   // Unknown or a number: unknown
        object Side(List<StToken> s)
        {
            if (s.Count == 1 && s[0].Kind == StTok.Number && double.TryParse(s[0].Text.Replace("_", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return n;
            if (s.Count == 2 && s[0].Text == "-" && s[1].Kind == StTok.Number && double.TryParse(s[1].Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)) return -m;
            if (s.Count == 1 && s[0].Kind == StTok.Word && s[0].Upper is "TRUE" or "FALSE") return s[0].Upper == "TRUE";
            if (s.Count == 1 && s[0].Kind == StTok.String) return s[0].Text.Trim('\'', '"');
            var v = value(Text(s));
            if (ReferenceEquals(v, Unknown)) return null;
            if (v != null) return v;
            if (s.Count == 3 && s[1].Text == "." && s[2].Kind == StTok.Word) return new EnumName(s[2].Text);       // Type.VALUE
            return null;
        }
        var l = Side(t.Take(at).ToList());
        var r = Side(t.Skip(at + 1).ToList());
        if (l == null || r == null) return null;
        int? cmp = Compare(l, r);
        if (cmp == null) return null;
        return t[at].Text switch { "=" => cmp == 0, "<>" => cmp != 0, "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, _ => cmp >= 0 };
    }

    sealed record EnumName(string Name);

    /// <summary>The value of an operand that could not be read: it stays unknown (never taken for an enum value).</summary>
    public static readonly object Unknown = new();

    static int? Compare(object a, object b)
    {
        static double? Num(object x) => x switch { bool => null, IConvertible c when x is not string && x is not EnumName => Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture), _ => null };
        if (a is EnumName || b is EnumName)
        {
            // an enum read online comes as its name ("READY") or "Type.READY"; a number cannot be compared with a name here
            string N(object x) => x is EnumName e ? e.Name : x?.ToString()?.Split('.').Last();
            if (Num(a) != null && b is EnumName || Num(b) != null && a is EnumName) return null;
            return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        if (a is bool ba && b is bool bb) return ba == bb ? 0 : 1;
        if (Num(a) is { } x && Num(b) is { } y) return x.CompareTo(y);
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        return null;
    }

    /// <summary>Three-valued: true, false, or unknown when it depends on an unknown sub-condition.</summary>
    public static bool? Eval3(LNode n, Func<int, bool?> atom)
    {
        switch (n.Op)
        {
            case "atom": return atom(n.Atom);
            case "const": return n.Value;
            case "NOT": return !Eval3(n.Kids[0], atom);
            case "AND":
            {
                var v = n.Kids.Select(k => Eval3(k, atom)).ToList();
                return v.Any(x => x == false) ? false : v.All(x => x == true) ? true : null;
            }
            case "OR":
            {
                var v = n.Kids.Select(k => Eval3(k, atom)).ToList();
                return v.Any(x => x == true) ? true : v.All(x => x == false) ? false : null;
            }
            default:
            {
                var v = n.Kids.Select(k => Eval3(k, atom)).ToList();
                return v.Any(x => x == null) ? null : v.Aggregate(false, (a, x) => a ^ x.Value);
            }
        }
    }

    /// <summary>What keeps the condition from being TRUE: the sub-conditions to change, for each way to get there (an OR gives
    /// several ways; the shortest first). Empty when it is TRUE.</summary>
    public static List<List<(int Atom, bool Want)>> Missing(LNode n, Func<int, bool?> atom, bool want = true)
    {
        var r = new List<List<(int, bool)>>();
        var v = Eval3(n, atom);
        if (v == want) { r.Add(new List<(int, bool)>()); return r; }
        switch (n.Op)
        {
            case "atom": r.Add(new List<(int, bool)> { (n.Atom, want) }); return r;
            case "const": return r;                                 // never
            case "NOT": return Missing(n.Kids[0], atom, !want);
            case "AND" when want:
            case "OR" when !want:
            {
                // every part must give want: combine the shortest way of each part that does not yet
                var all = new List<(int, bool)>();
                foreach (var k in n.Kids)
                {
                    var m = Missing(k, atom, want);
                    if (m.Count == 0) return r;
                    all.AddRange(m.OrderBy(x => x.Count).First());
                }
                r.Add(all.Distinct().ToList()); return r;
            }
            case "AND":
            case "OR":
                // one part is enough: each part is a way
                foreach (var k in n.Kids) r.AddRange(Missing(k, atom, want));
                return r.Where(x => x.Count > 0).OrderBy(x => x.Count).Take(4).ToList();
            default: return r;                                      // XOR: not explained
        }
    }

    // ------------------------------------------------------------------ everything for the window and the agent
    public static JsonObject Analyze(string text)
    {
        var e = Parse(text);
        int n = e.Atoms.Count;
        JsonObject Tree(LNode x) => new JsonObject
        {
            ["op"] = x.Op,
            ["atom"] = x.Op == "atom" ? x.Atom : null,
            ["value"] = x.Op == "const" ? x.Value : null,
            ["kids"] = new JsonArray(x.Kids.Select(k => (JsonNode)Tree(k)).ToArray())
        };
        var o = new JsonObject
        {
            ["keyword"] = e.Keyword, ["then"] = e.Then,
            ["text"] = ToText(e, e.Root),
            ["atoms"] = new JsonArray(e.Atoms.Select((a, i) => (JsonNode)new JsonObject { ["text"] = a, ["call"] = e.Calls[i], ["bool"] = IsBoolAtom(e, i) }).ToArray()),
            ["tree"] = Tree(e.Root),
            ["shortCircuit"] = e.ShortCircuit
        };
        var rows = TrueRows(e.Root, n);
        o["trueRows"] = rows == null ? null : new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
        o["combinations"] = n <= 62 ? (long)(1UL << n) : -1;
        // rewrites: only those that change something; each one proven over all combinations
        var list = new JsonArray();
        void Add(string id, string title, string titleDe, LNode r, string why = null, string boolCompare = "keep")
        {
            if (r == null) return;
            var txt = ToText(e, r, boolCompare);
            if (txt == e.Source || id is not ("bool-uniform" or "bool-remove") && txt == ToText(e, e.Root)) return;
            var same = Same(e.Root, r, n);
            if (same == false) return;          // never offered: it would change the meaning
            if (list.Any(x => x!["text"]!.ToString() == txt)) return;      // the same text as a rewrite already offered
            list.Add(new JsonObject { ["id"] = id, ["title"] = title, ["titleDe"] = titleDe, ["text"] = txt, ["proven"] = same == true, ["combinations"] = o["combinations"]?.DeepClone(), ["note"] = why });
        }
        if (e.ShortCircuit)
            o["rewriteNote"] = "AND_THEN / OR_ELSE fix the order of evaluation - the condition is not rewritten.";
        else
        {
            Add("not-inward", "NOT inward (De Morgan)", "NOT nach innen (De Morgan)", NotInward(e.Root));
            Add("not-outward", "NOT outward (De Morgan)", "NOT nach außen (De Morgan)", NotOutward(e.Root));
            if (e.Calls.Any(c => c)) o["simplifyNote"] = "the condition calls a function or method - simplifying could drop or repeat the call, so it is not offered";
            else
            {
                Add("simplify", "Simplify", "Vereinfachen", n <= 12 ? Simplify(e.Root, n) : null);
                Add("write-out", "Write out in full", "Ausführlich ausschreiben", WriteOut(e.Root, n));
            }
            if (e.HasCompare) Add("bool-uniform", "= TRUE / = FALSE uniform", "= TRUE / = FALSE einheitlich", e.Root, null, "always");
            if (System.Text.RegularExpressions.Regex.IsMatch(e.Source, @"(=|<>) (TRUE|FALSE)\b")) Add("bool-remove", "Without = TRUE / = FALSE", "Ohne = TRUE / = FALSE", Copy(e.Root));
        }
        o["rewrites"] = list;
        return o;
    }
}
