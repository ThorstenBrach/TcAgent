// Line-based diff (LCS) in unified format for propose mode / dryRun.
using System.Text;

namespace TcAgent;

public static class LineDiff
{
    public static string Unified(string oldText, string newText, string label, int context = 2)
    {
        var a = Split(oldText); var b = Split(newText);
        if (a.SequenceEqual(b)) return $"--- {label}: no change\n";
        if ((long)a.Length * b.Length > 4_000_000)
            return $"--- {label}: too large for a diff ({a.Length} -> {b.Length} lines), complete new text:\n{newText}\n";

        // LCS table
        var l = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);

        // Operations: ' ' equal, '-' old, '+' new (with old/new line numbers)
        var ops = new List<(char op, string text, int ai, int bi)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { ops.Add((' ', a[x], x, y)); x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) { ops.Add(('-', a[x], x, y)); x++; }
            else { ops.Add(('+', b[y], x, y)); y++; }
        }
        while (x < a.Length) { ops.Add(('-', a[x], x, y)); x++; }
        while (y < b.Length) { ops.Add(('+', b[y], x, y)); y++; }

        // Hunks with context
        var sb = new StringBuilder($"--- {label} (old)\n+++ {label} (new)\n");
        int k = 0;
        while (k < ops.Count)
        {
            if (ops[k].op == ' ') { k++; continue; }
            int start = Math.Max(0, k - context), end = k;
            while (end < ops.Count)
            {
                if (ops[end].op != ' ') { end++; continue; }
                int run = 0;
                while (end + run < ops.Count && ops[end + run].op == ' ') run++;
                if (run > 2 * context || end + run >= ops.Count) { end = Math.Min(ops.Count, end + context); break; }
                end += run;
            }
            sb.AppendLine($"@@ old line {ops[start].ai + 1}, new line {ops[start].bi + 1} @@");
            for (int i = start; i < end; i++) sb.Append(ops[i].op).AppendLine(ops[i].text);
            k = end;
        }
        return sb.ToString();
    }

    static string[] Split(string s) => (s ?? "").Replace("\r\n", "\n").Split('\n');
}
