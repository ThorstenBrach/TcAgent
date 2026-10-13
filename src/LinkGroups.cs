// tc_link with structures and groups ("Change Multi Link" in one step).
//
// A PLC STRUCT/ARRAY variable (e.g. MAIN.SerialCom.ComIn : KL6inData22B) cannot be linked with LinkVariables to a
// PDO group of a terminal (e.g. EL6001 'COM Inputs') - TwinCAT answers "Item ... is not a variable". When both sides
// of a pair have sub-items, the pair is expanded element-wise into single variable links:
//   1. members with the same name are paired (Status <=> Status, Ctrl <=> Ctrl); two matched groups recurse,
//   2. the rest is paired in tree order (D[0..21] <=> Data In 0..21), flattening arrays/sub-structures to their leaves
//      when that makes the counts equal.
// If source elements remain unmatched nothing is linked (all or nothing) - the proposal shows every pair so the agent
// can pass corrected pairs explicitly.
// Sub-items are read from the tree (ITcSmTreeItem children). Some views list struct members flat as siblings named
// '<var>.<member>' - this form is recognised as well.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    sealed class LinkNode
    {
        public string Name;
        public string Path;
        public List<LinkNode> Kids = new();
    }

    sealed record LinkPair(string Source, string Target, string How);

    const int LinkNodeBudget = 5000, LinkMaxDepth = 8;

    string Link(JsonObject a)
    {
        var list = new List<(string S, string T)>();
        if (!string.IsNullOrEmpty(Str(a, "source")) && !string.IsNullOrEmpty(Str(a, "target"))) list.Add((Str(a, "source").Trim(), Str(a, "target").Trim()));
        foreach (var line in (Str(a, "pairs") ?? "").Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            var parts = l.Split(new[] { "<=>", "=>", "->" }, StringSplitOptions.None);
            if (parts.Length != 2) throw new Exception("Line not in the format '<path> => <path>': " + l);
            list.Add((parts[0].Trim(), parts[1].Trim()));
        }
        if (list.Count == 0) throw new Exception("specify source+target or pairs");
        bool dry = DryRun(a);
        bool elementwise = Bool(a, "elementwise", true);
        var sm = (dynamic)tc.RequireSysMan();
        var o = new StringBuilder();
        int ok = 0, total = 0;
        foreach (var (src, dst) in list)
        {
            List<LinkPair> leafPairs;
            try { leafPairs = ExpandLinkPair(src, dst, elementwise, o); }
            catch (Exception ex) { total++; o.AppendLine($"ERROR: {src} <=> {dst}: {ex.InnerException?.Message ?? ex.Message}"); continue; }
            foreach (var p in leafPairs)
            {
                total++;
                var how = p.How == "direct" ? "" : $" (by {p.How})";
                try
                {
                    if (!dry) sm.LinkVariables(p.Source, p.Target);
                    ok++;
                    o.AppendLine($"{(dry ? "OK (dryRun)" : "linked")}{how}: {p.Source} => {p.Target}");
                }
                catch (Exception ex) { o.AppendLine($"ERROR{how}: {p.Source} => {p.Target}: {ex.InnerException?.Message ?? ex.Message}"); }
            }
        }
        return $"{ok}/{total} {(dry ? "checkable (nothing linked)" : "linked")}\n" + o;
    }

    /// <summary>One requested pair -> the single variable links to create. Both sides with sub-items (struct/array/PDO group)
    /// are expanded element-wise; otherwise the pair is linked as given. Notes (unlinked extra entries) go to <paramref name="notes"/>.</summary>
    List<LinkPair> ExpandLinkPair(string src, string dst, bool elementwise, StringBuilder notes)
    {
        var s = TreeLinkNode(src, elementwise);
        var t = TreeLinkNode(dst, elementwise);
        // flat members ('<var>.<member>' siblings) are only searched when needed: path missing or the other side is a group
        if (elementwise && (s == null || s.Kids.Count == 0) && (s == null || t?.Kids.Count > 0)) s = FlatLinkNode(src) ?? s;
        if (elementwise && (t == null || t.Kids.Count == 0) && (t == null || s?.Kids.Count > 0)) t = FlatLinkNode(dst) ?? t;
        if (s == null) throw new Exception("Path not found: " + src);
        if (t == null) throw new Exception("Path not found: " + dst);
        if (!elementwise || s.Kids.Count == 0 || t.Kids.Count == 0)
            return new List<LinkPair> { new(s.Path, t.Path, "direct") };

        var pairs = new List<LinkPair>();
        var problems = new List<string>();
        var leftOver = new List<string>();
        MatchLinkNodes(s.Kids, t.Kids, pairs, problems, leftOver);
        if (problems.Count > 0)
        {
            var sb = new StringBuilder($"element-wise mapping of {src} => {dst} is incomplete, nothing linked for this pair:\n");
            foreach (var p in problems) sb.AppendLine("  " + p);
            if (pairs.Count > 0)
            {
                sb.AppendLine("  pairs found so far (pass the corrected list as 'pairs'):");
                foreach (var p in pairs) sb.AppendLine($"    {p.Source} => {p.Target}");
            }
            sb.Append($"  source elements: {string.Join(", ", LinkLeaves(s.Kids).Select(x => x.Name))}\n" +
                      $"  target elements: {string.Join(", ", LinkLeaves(t.Kids).Select(x => x.Name))}");
            throw new Exception(sb.ToString());
        }
        notes.AppendLine($"{src} => {dst}: element-wise, {pairs.Count} single links " +
                         $"({pairs.Count(p => p.How == "name")} by name, {pairs.Count(p => p.How == "order")} by order)");
        if (leftOver.Count > 0) notes.AppendLine($"  target entries left unlinked: {string.Join(", ", leftOver)}");
        return pairs;
    }

    /// <summary>Tree item with its sub-items (tree children); null if the path does not exist.</summary>
    LinkNode TreeLinkNode(string path, bool expand = true)
    {
        ITcSmTreeItem it;
        try { it = tc.Item(path); } catch { return null; }
        int budget = LinkNodeBudget;
        var node = BuildLinkNode(it, it.Name, expand ? 0 : LinkMaxDepth, ref budget);   // elementwise=false: existence check only, no sub-items
        node.Path = path;          // keep the spelling the caller used for the top item
        return node;
    }

    /// <summary>Group of flat siblings named '&lt;name&gt;.member' / '&lt;name&gt;[i]' below the parent of path; null if there are none.</summary>
    LinkNode FlatLinkNode(string path)
    {
        var cut = path.LastIndexOf('^');
        if (cut <= 0) return null;
        ITcSmTreeItem parent;
        try { parent = tc.Item(path[..cut]); } catch { return null; }
        var baseName = path[(cut + 1)..];
        var flat = TwinCatSession.Children(parent)
            .Where(c => c.Name.Length > baseName.Length &&
                        c.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                        (c.Name[baseName.Length] == '.' || c.Name[baseName.Length] == '['))
            .ToList();
        if (flat.Count == 0) return null;
        int budget = LinkNodeBudget;
        var g = new LinkNode { Name = baseName, Path = path };
        foreach (var c in flat) g.Kids.Add(BuildLinkNode(c, c.Name[baseName.Length..].TrimStart('.'), 1, ref budget));
        return g;
    }

    static LinkNode BuildLinkNode(ITcSmTreeItem it, string name, int depth, ref int budget)
    {
        if (budget-- <= 0) throw new Exception($"more than {LinkNodeBudget} sub-items - link a smaller group or pass explicit pairs");
        var n = new LinkNode { Name = name, Path = it.PathName };
        if (depth >= LinkMaxDepth) return n;
        List<ITcSmTreeItem> kids;
        try { kids = TwinCatSession.Children(it).ToList(); } catch { return n; }
        foreach (var c in kids) n.Kids.Add(BuildLinkNode(c, c.Name, depth + 1, ref budget));
        return n;
    }

    /// <summary>Name used for matching: last member name, case-insensitive ('MAIN.x.ComIn.Status' -> 'status').</summary>
    static string LinkKey(string name)
    {
        var k = (name ?? "").Trim();
        int dot = k.LastIndexOf('.');
        if (dot >= 0 && k.IndexOf(']', dot) < 0) k = k[(dot + 1)..];
        return k.ToLowerInvariant();
    }

    static List<LinkNode> LinkLeaves(IEnumerable<LinkNode> nodes) =>
        nodes.SelectMany(n => n.Kids.Count == 0 ? new List<LinkNode> { n } : LinkLeaves(n.Kids)).ToList();

    static void MatchLinkNodes(List<LinkNode> src, List<LinkNode> tgt, List<LinkPair> pairs, List<string> problems, List<string> leftOver)
    {
        // 1. by name
        var restS = new List<LinkNode>();
        var restT = new List<LinkNode>(tgt);
        foreach (var s in src)
        {
            var t = restT.FirstOrDefault(x => LinkKey(x.Name) == LinkKey(s.Name));
            if (t == null) { restS.Add(s); continue; }
            restT.Remove(t);
            if (s.Kids.Count > 0 && t.Kids.Count > 0) MatchLinkNodes(s.Kids, t.Kids, pairs, problems, leftOver);
            else pairs.Add(new LinkPair(s.Path, t.Path, "name"));
        }
        if (restS.Count == 0)
        {
            leftOver.AddRange(restT.Select(x => x.Path));
            return;
        }
        if (restT.Count == 0)
        {
            problems.Add("no counterpart for: " + string.Join(", ", restS.Select(x => x.Name)));
            return;
        }
        // 2. by order - as they are, or flattened to leaves where that makes the counts equal
        var candidates = new[]
        {
            (S: restS, T: restT), (S: LinkLeaves(restS), T: LinkLeaves(restT)),
            (S: LinkLeaves(restS), T: restT), (S: restS, T: LinkLeaves(restT)),
        };
        foreach (var (cs, ct) in candidates)
        {
            if (cs.Count != ct.Count) continue;
            for (int i = 0; i < cs.Count; i++)
            {
                if (cs[i].Kids.Count > 0 && ct[i].Kids.Count > 0) MatchLinkNodes(cs[i].Kids, ct[i].Kids, pairs, problems, leftOver);
                else pairs.Add(new LinkPair(cs[i].Path, ct[i].Path, "order"));
            }
            return;
        }
        problems.Add($"{restS.Count} unmatched source element(s) ({LinkLeaves(restS).Count} leaves: {string.Join(", ", restS.Select(x => x.Name))}) " +
                     $"vs {restT.Count} target element(s) ({LinkLeaves(restT).Count} leaves: {string.Join(", ", restT.Select(x => x.Name))}) - counts differ");
    }
}
