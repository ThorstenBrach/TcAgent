// The /// text of a TcUnit test or suite as HTML, for the test report (TcUnitReport.cs): "Keyword:" lines as headings,
// "1." and "-" lines as lists, "| a | b |" lines as a table (first line the head, a |---| separator is left out, number
// columns right-aligned), http(s) links clickable, everything else HTML-encoded. The TcUnit Tests window lays out the
// same text with the same rules (TableCells).
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Net;
using System.Text;

namespace TcAgent;

public static class TcUnitDoc
{
    static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    static readonly System.Text.RegularExpressions.Regex Heading = new(@"^([A-Za-z][\w /()-]{0,30}):\s*(.*)$"), Numbered = new(@"^\d+[.)]\s+(.*)$"), Bullet = new(@"^[-*]\s+(.*)$");

    /// <summary>The details of a /// text as HTML: "Keyword:" lines as headings, "1." and "-" lines as lists, "| a | b |" lines
    /// as a table, links clickable.</summary>
    public static string Html(string details)
    {
        var sb = new StringBuilder();
        string list = null;
        void Close() { if (list != null) { sb.Append("</").Append(list).Append('>'); list = null; } }
        void Open(string kind) { if (list == kind) return; Close(); sb.Append('<').Append(kind).Append('>'); list = kind; }
        // links: only absolute http(s) URLs, found in the raw text and encoded for the attribute and the text separately
        static string Text(string s)
        {
            var sb = new StringBuilder();
            int at = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s ?? "", @"https?://[^\s<>""']+"))
            {
                sb.Append(E(s[at..m.Index]));
                var href = Guards.SafeHref(m.Value);
                sb.Append(href != null ? "<a href=\"" + E(href) + "\" rel=\"noopener noreferrer\">" + E(m.Value) + "</a>" : E(m.Value));
                at = m.Index + m.Length;
            }
            return sb.Append(E((s ?? "")[at..])).ToString();
        }
        // "| a | b |" lines: a table, the first line its head, a separator line (|---|:--:|) is left out; every other line ends it
        var table = new List<List<string>>();
        void CloseTable()
        {
            if (table.Count == 0) return;
            var cols = table.Max(r => r.Count);
            // a column whose cells (below the head) are all numbers is right-aligned
            var right = Enumerable.Range(0, cols).Select(c => table.Count > 1 && table.Skip(1).All(r => c >= r.Count || r[c].Length == 0 ||
                            double.TryParse(r[c], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) && table.Skip(1).Any(r => c < r.Count && r[c].Length > 0)).ToArray();
            sb.Append("<table class='steps'>");
            for (int i = 0; i < table.Count; i++)
            {
                var tag = i == 0 ? "th" : "td";
                sb.Append("<tr>");
                for (int c = 0; c < cols; c++)
                    sb.Append('<').Append(tag).Append(right[c] ? " class='n'>" : ">").Append(Text(c < table[i].Count ? table[i][c] : "")).Append("</").Append(tag).Append('>');
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            table.Clear();
        }
        foreach (var raw in details.Split('\n'))
        {
            var l = raw.Trim();
            if (TableCells(l) is { } cells)
            {
                Close();
                if (!(table.Count > 0 && cells.All(c => TableSeparator.IsMatch(c)))) table.Add(cells);
                continue;
            }
            CloseTable();
            if (l.Length == 0) { Close(); continue; }
            System.Text.RegularExpressions.Match m;
            if ((m = Numbered.Match(l)).Success) { Open("ol"); sb.Append("<li>").Append(Text(m.Groups[1].Value)).Append("</li>"); continue; }
            if ((m = Bullet.Match(l)).Success) { Open("ul"); sb.Append("<li>").Append(Text(m.Groups[1].Value)).Append("</li>"); continue; }
            Close();
            if ((m = Heading.Match(l)).Success && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<h4>").Append(E(m.Groups[1].Value)).Append("</h4>");
                if (m.Groups[2].Value.Length > 0) sb.Append("<div>").Append(Text(m.Groups[2].Value)).Append("</div>");
            }
            else sb.Append("<div>").Append(Text(l)).Append("</div>");
        }
        CloseTable();
        Close();
        return sb.ToString();
    }

    static readonly System.Text.RegularExpressions.Regex TableSeparator = new(@"^:?-{1,}:?$");

    /// <summary>The cells of a table line "| a | b |" (trimmed; a line has to start and end with | and have one cell at
    /// least), or null. Shared with the TcUnit Tests window (same rule).</summary>
    public static List<string> TableCells(string line)
    {
        var l = (line ?? "").Trim();
        if (l.Length < 2 || l[0] != '|' || l[^1] != '|') return null;
        return l[1..^1].Split('|').Select(c => c.Trim()).ToList();
    }
}
