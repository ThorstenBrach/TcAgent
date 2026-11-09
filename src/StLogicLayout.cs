// A rewrite of a condition as the code that replaces it in the editor: the same statement head, laid out by the formatter
// with the rules of the solution, and read again to check that it means the same (StLogic.cs). Used by TcAgent.exe condition --layout.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static class StLogicLayout
{
    /// <summary>The code that replaces the condition: the same kind of statement head as the input (IF … THEN, ELSIF … THEN,
    /// WHILE … DO, UNTIL …, x := …; or the bare expression) with the new expression, laid out by the formatter at column 0
    /// (the window indents the next lines like the line it stands in). Checked: the code read again means the same as the
    /// condition (null: too many sub-conditions to prove). When the formatter cannot lay it out: the expression on one line.</summary>
    public static (string Code, bool? Checked) Layout(LExpr e, string expr, StFormatConfig cfg)
    {
        string kw = e.Keyword ?? "", then = e.Then;
        string flat = kw switch
        {
            "" => expr,
            "IF" or "ELSIF" or "WHILE" or "UNTIL" => kw + " " + expr + (then != null && then != ";" ? " " + then : then == ";" ? ";" : ""),
            _ => kw + " " + expr + (then == ";" ? ";" : "")
        };
        string snippet = kw switch
        {
            "IF" => $"IF {expr} THEN\n    ;\nEND_IF\n",
            "ELSIF" => $"IF TRUE THEN\n    ;\nELSIF {expr} THEN\n    ;\nEND_IF\n",
            "WHILE" => $"WHILE {expr} DO\n    ;\nEND_WHILE\n",
            "UNTIL" => $"REPEAT\n    ;\nUNTIL {expr}\nEND_REPEAT\n",
            "" => null,
            _ => $"{kw} {expr};\n"
        };
        string code = flat;
        if (snippet != null)
            try
            {
                var res = StFormatter.Format(snippet, cfg, StPart.Implementation);
                if (res.Error == null && Cut(res.Text ?? snippet, kw, then) is { } cut) code = cut;
            }
            catch { }
        bool? ok;
        try
        {
            var back = StLogic.Parse(code);
            ok = Same(e, back);
        }
        catch { ok = false; }
        if (ok == false && code != flat) { code = flat; try { ok = Same(e, StLogic.Parse(flat)); } catch { ok = false; } }
        return (code, ok);
    }

    /// <summary>The statement head out of the formatted snippet.</summary>
    static string Cut(string formatted, string kw, string then)
    {
        var toks = StLexer.Lex(formatted);
        int pos = 0, start = -1, end = -1;
        string startWord = kw is "IF" or "ELSIF" or "WHILE" or "UNTIL" ? kw : null;
        foreach (var t in toks)
        {
            int at = pos; pos += t.Text.Length;
            if (t.Trivia || t.Comment) continue;
            if (start < 0)
            {
                if (startWord == null || t.Kind == StTok.Word && t.Upper == startWord) start = at;
                continue;
            }
            if (kw is "IF" or "ELSIF" && t.Kind == StTok.Word && t.Upper == "THEN") { end = then == "THEN" ? pos : at; break; }
            if (kw == "WHILE" && t.Kind == StTok.Word && t.Upper == "DO") { end = then == "DO" ? pos : at; break; }
            if (kw == "UNTIL" && t.Kind == StTok.Word && t.Upper == "END_REPEAT") { end = at; break; }
            if (startWord == null && t.Text == ";") { end = then == ";" ? pos : at; break; }
        }
        if (start < 0 || end < 0) return null;
        var head = formatted.Substring(start, end - start).TrimEnd();
        if (kw == "UNTIL" && then == ";") head += ";";
        // at column 0: the snippet put the head there (ELSIF, UNTIL too); the next lines keep their indent relative to it
        int col = start - (formatted.LastIndexOf('\n', Math.Max(0, start - 1)) + 1);
        if (col > 0) head = string.Join("\n", head.Split('\n').Select((l, i) => i == 0 ? l : l.Length >= col && l.Take(col).All(char.IsWhiteSpace) ? l.Substring(col) : l.TrimStart()));
        return head.Replace("\r\n", "\n");
    }

    /// <summary>Does b (read again from code) mean the same as a? Atoms are matched by their text; a turned comparison
    /// (a &gt;= b for NOT a &lt; b) counts as the negated atom.</summary>
    static bool? Same(LExpr a, LExpr b)
    {
        var index = a.Atoms.Select((x, i) => (x, i)).ToDictionary(p => p.x, p => p.i, StringComparer.OrdinalIgnoreCase);
        LNode Map(LNode n)
        {
            if (n.Op == "atom")
            {
                var s = b.Atoms[n.Atom];
                if (index.TryGetValue(s, out var i)) return new LNode("atom") { Atom = i };
                foreach (var kv in index)
                    if (StLogic.Turn(kv.Key) is { } tk && string.Equals(tk, s, StringComparison.OrdinalIgnoreCase)) return LNode.Not(new LNode("atom") { Atom = kv.Value });
                throw new KeyNotFoundException(s);
            }
            var r = new LNode(n.Op) { Atom = n.Atom, Value = n.Value };
            r.Kids.AddRange(n.Kids.Select(Map));
            return r;
        }
        try { return StLogic.Same(a.Root, Map(b.Root), a.Atoms.Count); }
        catch (KeyNotFoundException) { return false; }
    }
}
