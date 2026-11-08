// The comments of enumeration values in the DUTs of a project - for the formatter rule comment.case-labels, which writes
// them above the CASE labels (StFormatter.cs). Files only.
//
//   TYPE E_State :
//   (
//     Idle := 0,     // waiting for start      -> "E_State.Idle" = "waiting for start"
//     /// cycle runs                            -> "E_State.Run"  = "cycle runs"
//     Run
//   );
//   END_TYPE
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static class StEnums
{
    static readonly System.Text.RegularExpressions.Regex Declarations =
        new(@"<Declaration><!\[CDATA\[(.*?)\]\]></Declaration>", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>"E_Type.Value" → comment, for every enumeration of the DUT files under the folder (and plain-text .st files).</summary>
    public static Dictionary<string, string> Read(string folder)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return r;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".TcDUT", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\_Boot\") && !f.Contains(@"\_CompileInfo\"))
                .Take(5000).ToList();
        }
        catch { return r; }
        foreach (var f in files)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match m in Declarations.Matches(File.ReadAllText(f)))
                    foreach (var kv in FromText(m.Groups[1].Value)) r[kv.Key] = kv.Value;
            }
            catch { }
        }
        return r;
    }

    /// <summary>The enumerations of one declaration text.</summary>
    public static Dictionary<string, string> FromText(string text)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<StToken> t;
        try { t = StLexer.Lex(text); } catch (StSyntaxException) { return r; }
        // TYPE <name> : ( members ) ...
        for (int i = 0; i < t.Count; i++)
        {
            if (!t[i].Is("TYPE")) continue;
            int k = Next(t, i);
            if (k < 0 || t[k].Kind != StTok.Word) continue;
            string type = t[k].Text;
            k = Next(t, k);
            if (k < 0 || t[k].Text != ":") continue;
            k = Next(t, k);
            if (k < 0 || t[k].Text != "(") continue;
            string pending = null;           // comment lines above the next member
            string member = null;            // the member waiting for a comment at the end of its line
            for (int j = k + 1; j < t.Count && t[j].Text != ")"; j++)
            {
                var x = t[j];
                if (x.Kind == StTok.Newline) { member = null; continue; }
                if (x.Comment)
                {
                    var c = Clean(x.Text);
                    if (c.Length == 0) continue;
                    if (member != null) { r[type + "." + member] = c; member = null; }
                    else pending = pending == null ? c : pending + " " + c;
                    continue;
                }
                if (x.Kind == StTok.Word && (j == k + 1 || PrevSig(t, j) is { } p && (p.Text == "," || p.Text == "(")))
                {
                    member = x.Text;
                    if (pending != null) { r[type + "." + member] = pending; pending = null; }
                }
            }
        }
        return r;
    }

    static int Next(List<StToken> t, int i)
    {
        for (int k = i + 1; k < t.Count; k++) if (!t[k].Trivia && !t[k].Comment) return k;
        return -1;
    }

    static StToken PrevSig(List<StToken> t, int i)
    {
        for (int k = i - 1; k >= 0; k--) if (!t[k].Trivia && !t[k].Comment) return t[k];
        return null;
    }

    static string Clean(string c)
    {
        c = c.Trim();
        if (c.StartsWith("(*") && c.EndsWith("*)")) c = c[2..^2];
        else if (c.StartsWith("/*") && c.EndsWith("*/")) c = c[2..^2];
        else c = c.TrimStart('/');
        return string.Join(" ", c.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimStart('*').Trim())).Trim();
    }
}
