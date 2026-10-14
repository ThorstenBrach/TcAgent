// Editor navigation: open an object in the IDE editor and put the cursor on a line (like F12 / double-click),
// plus fallbacks for tc_goto_definition without editor context or without the VS extension.
// Preferably via the VS extension TcAgentBridge (actions "open"/"caret" in the IDE process), otherwise via DTE
// (Solution Explorer: select the node + DoDefaultAction).
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterEditorNav()
    {
        Add("tc_editor_open", "Opens a PLC object (POU, method, property accessor, action, DUT, GVL) in the IDE editor - like a double-click " +
                              "in the Solution Explorer - and optionally puts the cursor on a line (line numbers per part, as in tc_plc_read / " +
                              "tc_plc_search). Use it to show the user a place in the code. Changes nothing in the project. Cursor placement " +
                              "needs the VS extension TcAgentBridge; without it the object is only opened (via DTE).",
            Schema(Req(P("path", "string", "Tree path of the object, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Axis^Start")),
                   P("line", "integer", "Line in the part (1-based); empty = only open"),
                   P("part", "string", "declaration (default) or implementation"),
                   P("column", "integer", "Column (1-based, default 1)")),
            a => OpenInEditor(Str(a, "path"), Str(a, "part"), Int(a, "line", 0), Int(a, "column", 1)));
    }

    // ------------------------------------------------------------------ Open
    static string NormPart(string part) =>
        part != null && (part.StartsWith("impl", StringComparison.OrdinalIgnoreCase) || part.Equals("Implementierung", StringComparison.OrdinalIgnoreCase))
            ? "implementation" : "declaration";

    static bool IsTrue(JsonNode n) => n?.ToString() is "true" or "True";

    string OpenInEditor(string path, string part, int line, int column)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new Exception("path missing");
        part = NormPart(part);
        // Check the path (and normalize its spelling) before the IDE does anything
        var (treePath, pid) = tc.Sta.Run(() => { var it = tc.Item(path); return (it.PathName, tc.AttachedPid); });
        var where = line > 0 ? $" at {part} line {line}" : "";
        string bridgeNote;
        if (BridgeClient.Available(pid))
        {
            try
            {
                var args = new JsonObject { ["path"] = treePath, ["part"] = part, ["line"] = line, ["column"] = Math.Max(1, column) };
                var r = BridgeClient.Call(pid, "open", args, 60000);
                // The editor window only appears once the IDE has processed its messages -> set the cursor again if needed
                for (int i = 0; i < 8 && line > 0 && !IsTrue(r["lineSet"]); i++)
                {
                    System.Threading.Thread.Sleep(250);
                    r = BridgeClient.Call(pid, "caret", args, 30000);
                }
                if (line <= 0) return $"Opened {treePath} in the IDE editor.";
                return IsTrue(r["lineSet"])
                    ? $"Opened {treePath} in the IDE editor, cursor on {part} line {line}."
                    : $"Opened {treePath} in the IDE editor, but the cursor could not be placed{where}: {r["lineError"]?.ToString() ?? "unknown reason"}.";
            }
            catch (Exception ex)
            {
                bridgeNote = ex.Message.Contains("open", StringComparison.Ordinal) && (ex.Message.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Befehl", StringComparison.Ordinal))
                    ? "installed TcAgentBridge is too old for 'open' - update the VS extension"
                    : "TcAgentBridge: " + ex.Message;
            }
        }
        else bridgeNote = "TcAgentBridge not active";
        // DTE project model (works for the nested PLC project), then let the bridge put the cursor into the active editor
        string viaItems = null;
        try { viaItems = tc.Sta.Run(() => OpenViaProjectItems(treePath)); } catch (Exception ex) { Log.Info("open via ProjectItems: " + ex.Message); }
        if (viaItems != null)
        {
            if (line <= 0) return $"Opened {treePath} in the IDE editor.";
            if (BridgeClient.Available(pid))
            {
                var args = new JsonObject { ["path"] = treePath, ["part"] = part, ["line"] = line, ["column"] = Math.Max(1, column) };
                JsonObject r = null;
                for (int i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(250);
                    try { r = BridgeClient.Call(pid, "caret", args, 30000); } catch { }
                    if (r != null && IsTrue(r["lineSet"])) break;
                }
                if (r != null && IsTrue(r["lineSet"])) return $"Opened {treePath} in the IDE editor, cursor on {part} line {line}.";
                return $"Opened {treePath} in the IDE editor, but the cursor could not be placed{where}: {r?["lineError"]?.ToString() ?? "no answer from TcAgentBridge"}.";
            }
            return $"Opened {treePath} in the IDE editor; cursor not placed (needs TcAgentBridge).";
        }
        // DTE fallback: select and open the node in the Solution Explorer; line only if the editor offers DTE text
        return tc.Sta.Run(() => OpenViaDte(treePath, part, line)) + $" ({bridgeNote})";
    }

    // Solution Explorer via DTE: find the PLC node (2nd segment) in the upper levels, then descend segment by segment.
    // TwinCAT shows POUs with a type suffix ("MAIN (PRG)"). Collapse nodes that were expanded only for the search.
    string OpenViaDte(string treePath, string part, int line)
    {
        var dte = tc.RequireDte();
        var segs = treePath.Split('^');
        if (segs.Length < 2) return $"Not opened: {treePath} is not a PLC object.";
        var expanded = new List<EnvDTE.UIHierarchyItems>();
        bool Is(EnvDTE.UIHierarchyItem x, string seg)
        {
            string n;
            try { n = x.Name ?? ""; } catch { return false; }
            return string.Equals(n, seg, StringComparison.OrdinalIgnoreCase) || n.StartsWith(seg + " (", StringComparison.OrdinalIgnoreCase);
        }
        List<EnvDTE.UIHierarchyItem> Kids(EnvDTE.UIHierarchyItem x)
        {
            var list = new List<EnvDTE.UIHierarchyItem>();
            try
            {
                var k = x.UIHierarchyItems;
                if (k == null) return list;
                if (!k.Expanded) { k.Expanded = true; expanded.Add(k); }
                // nested hierarchies (TwinCAT project) fill their nodes only after the expansion was processed
                for (int t = 0; t < 4; t++)
                {
                    list.Clear();
                    foreach (EnvDTE.UIHierarchyItem c in k) list.Add(c);
                    if (list.Count > 0) break;
                    System.Threading.Thread.Sleep(150);
                }
            }
            catch { }
            return list;
        }
        var sol = dte.ToolWindows.SolutionExplorer;
        // Search by the ORDER of the path segments (TIPC^Plc^Plc Project^Folder^FB^Method): a node counts when its name
        // is the next segment; nodes in between (Solution, TwinCAT project, "PLC") are allowed in the upper levels.
        // Only nodes that matched a segment are expanded below level 3, so the search stays small.
        var dump = new List<string>();
        int bestIdx = 0; string bestWhere = null; List<string> bestKids = null;
        string Nm(EnvDTE.UIHierarchyItem x) { try { return x.Name ?? ""; } catch { return "?"; } }
        EnvDTE.UIHierarchyItem Find(List<EnvDTE.UIHierarchyItem> items, int idx, int depth)
        {
            foreach (var x in items)
            {
                if (depth <= 1 && dump.Count < 120) dump.Add(new string(' ', depth * 2) + Nm(x));
                int next = Is(x, segs[idx]) ? idx + 1 : idx;
                if (next == segs.Length) return x;
                if (next > idx || depth <= 3)
                {
                    if (depth > 9) continue;
                    var kids = Kids(x);
                    if (depth >= 2 && dump.Count < 120) dump.Add($"{new string(' ', depth * 2)}{Nm(x)} -> [{string.Join(", ", kids.Select(Nm).Take(12))}]");
                    if (next > bestIdx) { bestIdx = next; bestWhere = string.Join("^", segs.Take(next)); bestKids = kids.Select(Nm).Take(25).ToList(); }
                    var hit = Find(kids, next, depth + 1);
                    if (hit != null) return hit;
                }
            }
            return null;
        }
        var level = new List<EnvDTE.UIHierarchyItem>();
        foreach (EnvDTE.UIHierarchyItem x in sol.UIHierarchyItems) level.Add(x);
        var cur = Find(level, 1, 0);
        var keep = new HashSet<EnvDTE.UIHierarchyItems>();
        try { for (var p = cur?.Collection?.Parent as EnvDTE.UIHierarchyItem; p != null; p = p.Collection?.Parent as EnvDTE.UIHierarchyItem) keep.Add(p.UIHierarchyItems); }
        catch { }
        foreach (var e in expanded)
            if (!keep.Contains(e)) try { e.Expanded = false; } catch { }
        if (cur == null)
        {
            // fallback: open the object's file (.TcPOU/.TcDUT/.TcGVL) - the IDE opens the POU editor
            var file = ObjectFile(treePath);
            if (file != null)
                try
                {
                    dte.ItemOperations.OpenFile(file);
                    return $"Opened {Path.GetFileName(file)} (the file of {treePath}) - the node was not found in the Solution Explorer, " +
                           "so for a method/action/property the POU itself is open" + (line > 0 ? "; cursor not placed" : "") + Diag();
                }
                catch (Exception ex) { return $"Not opened: {treePath} not found in the Solution Explorer and opening {file} failed: {ex.Message}" + Diag(); }
            return $"Not opened: {treePath} not found in the Solution Explorer." + Diag();
        }
        string Diag() => (bestWhere == null ? "" : $" [Solution Explorer: matched up to '{bestWhere}', its children: {string.Join(", ", bestKids ?? new())}]") +
                         $"\nSolution Explorer (upper levels):\n{string.Join("\n", dump)}";
        cur.Select(EnvDTE.vsUISelectionType.vsUISelectionTypeSelect);
        sol.DoDefaultAction();
        if (line <= 0) return $"Opened {treePath} in the IDE editor.";
        // TwinCAT editors usually offer no DTE TextSelection; if they do, lines run across declaration + implementation
        try
        {
            if (dte.ActiveDocument?.Selection is EnvDTE.TextSelection ts)
            {
                int offset = 0;
                if (part == "implementation" && tc.Item(treePath) is ITcPlcDeclaration d)
                    offset = (d.DeclarationText ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
                ts.GotoLine(offset + line, false);
                return $"Opened {treePath} in the IDE editor, cursor on {part} line {line} (via DTE).";
            }
        }
        catch { }
        return $"Opened {treePath} in the IDE editor; cursor NOT placed on {part} line {line} (the TwinCAT editor offers no DTE text selection - needs TcAgentBridge).";
    }

    // file of a PLC object (FullPath VSProperty), for sub-objects (method, action ...) the file of the parent POU
    string ObjectFile(string treePath)
    {
        for (var p = treePath; p.Contains('^'); p = p[..p.LastIndexOf('^')])
        {
            try
            {
                string xml = ((dynamic)tc.Item(p)).ProduceXml(false);
                var m = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>FullPath</Name>\s*<Value>([^<]+)</Value>");
                if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
            }
            catch { }
        }
        return null;
    }

    // The TwinCAT PLC project is a nested hierarchy whose nodes are not exposed through the Solution Explorer's
    // UIHierarchy - but through the DTE project model: Solution > Project > ProjectItems (> SubProject) ...
    // Finds the item of the tree path by the order of its segments (names with or without extension / type suffix)
    // and opens it like a double-click. Returns null when nothing was found.
    string OpenViaProjectItems(string treePath)
    {
        var dte = tc.RequireDte();
        var segs = treePath.Split('^');
        bool Is(string n, string seg)
        {
            n ??= "";
            return string.Equals(n, seg, StringComparison.OrdinalIgnoreCase) || n.StartsWith(seg + " (", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetFileNameWithoutExtension(n), seg, StringComparison.OrdinalIgnoreCase);
        }
        IEnumerable<(string Name, EnvDTE.ProjectItems Items, EnvDTE.ProjectItem Item)> Children(EnvDTE.ProjectItems items)
        {
            var list = new List<(string, EnvDTE.ProjectItems, EnvDTE.ProjectItem)>();
            if (items == null) return list;
            try
            {
                foreach (EnvDTE.ProjectItem pi in items)
                {
                    string n = ""; try { n = pi.Name; } catch { }
                    EnvDTE.ProjectItems sub = null;
                    try { sub = pi.SubProject?.ProjectItems; } catch { }
                    try { sub ??= pi.ProjectItems; } catch { }
                    list.Add((n, sub, pi));
                }
            }
            catch { }
            return list;
        }
        EnvDTE.ProjectItem Find(EnvDTE.ProjectItems items, int idx, int depth)
        {
            foreach (var (name, sub, item) in Children(items))
            {
                // the "<Plc> Project" level may not exist as an item in the project model - allow skipping it
                int next = Is(name, segs[idx]) ? idx + 1
                         : idx + 1 < segs.Length && segs[idx].EndsWith(" Project", StringComparison.OrdinalIgnoreCase) && Is(name, segs[idx + 1]) ? idx + 2
                         : idx;
                if (next == segs.Length) return item;
                if ((next > idx || depth <= 3) && depth <= 12)
                {
                    var hit = Find(sub, next, depth + 1);
                    if (hit != null) return hit;
                }
            }
            return null;
        }
        EnvDTE.ProjectItem found = null;
        foreach (EnvDTE.Project prj in dte.Solution.Projects)
        {
            string pn = ""; try { pn = prj.Name; } catch { }
            int start = Is(pn, segs[1]) ? 2 : 1;
            try { found = Find(prj.ProjectItems, start, 0); } catch { }
            if (found != null) break;
        }
        if (found == null) return null;
        try
        {
            var w = found.Open(EnvDTE.Constants.vsViewKindPrimary);
            w.Visible = true;
            w.Activate();
        }
        catch
        {
            found.ExpandView();
            dte.ExecuteCommand("Project.Open");   // fallback: open the selected item
        }
        return "opened";
    }

    // Open a definition: find the declaration line in the declaration part (variable, or header line of the object)
    string OpenAtDeclaration(string treePath, string name, bool isVar)
    {
        int line = 1;
        try
        {
            var decl = tc.Sta.Run(() => (tc.Item(treePath) as ITcPlcDeclaration)?.DeclarationText);
            line = DeclLine(decl, name, isVar);
        }
        catch { }
        return OpenInEditor(treePath, "declaration", line, 1);
    }

    static int DeclLine(string decl, string name, bool isVar)
    {
        if (string.IsNullOrEmpty(decl) || string.IsNullOrEmpty(name)) return 1;
        var lines = decl.Replace("\r\n", "\n").Split('\n');
        var n = Regex.Escape(name);
        // Variable: "name : TYPE", "a, name : TYPE", "name AT %I* : TYPE" - comments are ignored
        var rxVar = new Regex($@"^\s*(?:[A-Za-z_]\w*\s*,\s*)*{n}\s*(?::|,|\bAT\b)", RegexOptions.IgnoreCase);
        var rxHead = new Regex($@"^\s*(?:FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE|METHOD|PROPERTY|ACTION|TYPE)\b.*\b{n}\b", RegexOptions.IgnoreCase);
        bool inBlockComment = false;
        int headLine = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i];
            if (inBlockComment) { var e = t.IndexOf("*)", StringComparison.Ordinal); if (e < 0) continue; t = t[(e + 2)..]; inBlockComment = false; }
            var c = t.IndexOf("//", StringComparison.Ordinal); if (c >= 0) t = t[..c];
            var b = t.IndexOf("(*", StringComparison.Ordinal);
            if (b >= 0) { var e = t.IndexOf("*)", b + 2, StringComparison.Ordinal); if (e < 0) inBlockComment = true; t = e < 0 ? t[..b] : t[..b] + t[(e + 2)..]; }
            if (isVar && rxVar.IsMatch(t)) return i + 1;
            if (headLine == 0 && rxHead.IsMatch(t)) headLine = i + 1;
        }
        if (headLine > 0) return headLine;
        if (!isVar)
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"^\s*(?:FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE|METHOD|PROPERTY|TYPE|VAR_GLOBAL)\b", RegexOptions.IgnoreCase)) return i + 1;
        return 1;
    }

    // ------------------------------------------------------------------ tc_goto_definition: fallbacks
    // Without the bridge: project objects with that name (tree index) and cached library signatures
    string GotoWithoutBridge(string name, bool open, string bridgeError)
    {
        var o = new StringBuilder($"'{name}': TcAgentBridge not available ({bridgeError}) - only project objects by name and cached library signatures, no scope resolution:\n");
        var paths = tc.Sta.Run(() => tc.ResolvePlcPathsPublic(name));
        o.Append(GotoFallbackText(name, paths));
        if (open)
            o.AppendLine(paths.Count > 0 ? OpenAtDeclaration(paths[0], name.Split('.').Last(), false) : "Not opened: no project object with this name.");
        return o.ToString();
    }

    string GotoFallbackText(string name, List<string> projectPaths = null)
    {
        var o = new StringBuilder();
        projectPaths ??= tc.Sta.Run(() => tc.ResolvePlcPathsPublic(name));
        foreach (var p in projectPaths) o.AppendLine($"Project object: {p}  (tc_plc_read)");
        var first = name.Split('.')[0];
        var libHits = LibCacheLookup(first);
        foreach (var h in libHits) o.AppendLine(h);
        if (projectPaths.Count == 0 && libHits.Count == 0)
        {
            List<string> libs;
            try { libs = tc.Sta.Run(ReferencedLibraryNames); } catch { libs = new List<string>(); }
            o.AppendLine("Not in the cached library signatures either." +
                         (libs.Count > 0 ? " Referenced libraries: " + string.Join(", ", libs) + "." : "") +
                         $" Load the likely library once with tc_lib_signatures library=<name> filter={first} (fills the cache), then retry.");
        }
        return o.ToString();
    }

    // Top-level symbol in the cached library APIs (*.api.json written by tc_lib_signatures)
    static List<string> LibCacheLookup(string name)
    {
        var res = new List<string>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "libcache");
        if (!Directory.Exists(dir)) return res;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(dir, "*.api.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var api = LibApi.Load(f);
                var s = api.Symbols.FirstOrDefault(x => x.ParentId < 0 && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (s == null || !seen.Add(api.Library ?? f)) continue;
                res.Add($"{s.Kind} {s.Name}{(string.IsNullOrEmpty(s.Extends) ? "" : " EXTENDS " + s.Extends)}: library {api.Library} " +
                        $"(from cache {Path.GetFileName(f)}) -> tc_lib_signatures library={api.Library} symbol={s.Name}");
            }
            catch { }
        }
        return res;
    }

    // Names of the referenced libraries of all PLC projects (resolved placeholders)
    List<string> ReferencedLibraryNames()
    {
        var names = new List<string>();
        foreach (var plc in tc.PlcProjects())
            foreach (var c in TwinCatSession.Children(plc))
            {
                if (c is not ITcPlcLibraryManager lm) continue;
                foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).References)
                {
                    var d = DescribeLib(r);
                    var lib = d.Contains(" -> ") ? d[(d.IndexOf(" -> ", StringComparison.Ordinal) + 4)..] : d;
                    var n = lib.Split(',')[0].Trim();
                    if (n.Length > 0 && n != "-" && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
                }
            }
        return names;
    }
}
