// TwinSAFE - READ ONLY: tc_safety lists the safety projects of the solution with their groups, alias devices, connections
// (FSoE address, watchdog), safety parameters and CRCs/checksums as far as the Automation Interface exposes them.
//
// Writing safety configuration is intentionally NOT supported: a TwinSAFE project must be changed, verified and downloaded
// by a person in the TwinSAFE editor (user/password, CRC check, validation per machinery directive). This tool never calls
// ConsumeXml, CreateChild, DeleteChild or any download/activation on safety items - only LookupTreeItem, the tree
// enumeration and ProduceXml (plus reading the project files on disk).
//
// Where the safety configuration lives is not documented as precisely as TIPC/TIRT: the tree shortcut tried first is TISC
// (the "SAFETY" node of the TwinCAT project); if that fails, the project files on disk (*.splcproj and the files in its
// folder, e.g. *.sal = safety application of a group, *.sds = alias device) are read. Because the XML layout differs between
// versions, values are found by name (FSoE address, watchdog, CRC, connection ID, safe parameters ...) and the shortened raw
// XML is returned with what could be parsed.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    // tree shortcut(s) of the safety configuration - tried in this order
    internal static readonly string[] SafetyRoots = { "TISC" };

    // names of values that matter for a safety review
    static readonly Regex SafetyKey = new(@"fsoe|watchdog|wdg|crc|checksum|safe_?addr|safety_?addr|conn(?:ection)?_?(?:id|no|number)\b|" +
                                          @"safe_?param|safety_?param|module_?fault|com_?error|err_?ack|\bsil\b|\bpl\b|safety_?group|group_?(?:id|name)|" +
                                          @"alias|target_?system|serial|logic_?type|project_?(?:crc|version)|online_?crc|distribution",
                                          RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // safety I/O and logic terminals in the I/O configuration (FSoE participants)
    static readonly Regex SafetyTerminal = new(@"\b(?:EL69\d\d|EJ69\d\d|EK1960|EL19\d\d|EL29\d\d|EJ19\d\d|EJ29\d\d|EP19\d\d|EP29\d\d|EL1918|EL2911|EL6930|KL6904|AX58\d\d|EL72\d\d-\d{4}.*TwinSAFE)",
                                               RegexOptions.IgnoreCase | RegexOptions.Compiled);

    void RegisterSafety()
    {
        Add("tc_safety", "TwinSAFE, READ ONLY: lists the safety projects of the solution (tree node TISC = SAFETY, otherwise the " +
                         "*.splcproj files on disk) with groups, alias devices, connections (FSoE address, watchdog), safety " +
                         "parameters and project CRC/checksums as far as they can be read, plus the safety terminals in the I/O " +
                         "configuration. action=list (default) overview; action=show path=<tree path> one item with its values and " +
                         "the shortened raw XML; action=files the safety project files on disk. Writing safety configuration is " +
                         "intentionally not supported - changes, verification and download belong to a person in the TwinSAFE editor.",
            Schema(P("action", "string", "list (default), show or files"),
                   P("path", "string", "show: tree path of a safety item (from action=list)"),
                   P("depth", "integer", "list: tree depth below the safety node (default 4)"),
                   P("maxChars", "integer", "max. characters of raw XML per item (default 3000)")),
            a => Safety(a));
    }

    string Safety(JsonObject a)
    {
        var action = (Str(a, "action") ?? "list").ToLowerInvariant();
        var maxChars = Math.Clamp(Int(a, "maxChars", 3000), 200, 200_000);
        const string note = "\n(read only - writing TwinSAFE configuration is intentionally not supported: change, verify and download in the TwinSAFE editor)";
        switch (action)
        {
            case "files":
                return SafetyFiles(SolutionDir(), maxChars) + note;
            case "show":
                var path = Str(a, "path") ?? throw new Exception("show needs path (a tree path from tc_safety action=list)");
                return tc.Sta.Run(() => SafetyShow(tc.Item(path), maxChars)) + note;
            case "list":
                var tree = tc.Sta.Run(() => SafetyList(Math.Clamp(Int(a, "depth", 4), 1, 10), maxChars));
                // the tree shows the structure; groups, alias devices and parameters live in the project files (*.sal, *.sds ...)
                return tree.Text + (tree.Found ? "\nSafety project files on disk:\n" : "") + SafetyFiles(SolutionDir(), maxChars) + note;
            default:
                throw new Exception("action: list, show or files (read only - writing TwinSAFE configuration is not supported)");
        }
    }

    // ------------------------------------------------------------------ tree
    (bool Found, string Text) SafetyList(int depth, int maxChars)
    {
        var o = new StringBuilder();
        var roots = new List<ITcSmTreeItem>();
        foreach (var s in SafetyRoots) try { roots.Add(tc.Item(s)); } catch { }
        var groups = new List<string>(); var aliases = new List<string>(); var conns = new List<string>(); var crcs = new List<string>(); var parms = new List<string>();
        var anyValue = false;
        foreach (var root in roots)
        {
            o.AppendLine($"Safety configuration {SafeName(root)}  [{root.PathName}]");
            void Walk(ITcSmTreeItem it, int level)
            {
                string name = SafeName(it), path = "";
                int type = 0, sub = 0;
                try { path = it.PathName; type = it.ItemType; sub = it.ItemSubType; } catch { }
                var ind = new string(' ', level * 2);
                o.AppendLine($"{ind}{name}  [type={type} sub={sub}]  {path}");
                var values = SafetyValues(Xml(it));
                foreach (var (k, v) in values.Take(12)) o.AppendLine($"{ind}    {k} = {v}");
                if (values.Count > 12) o.AppendLine($"{ind}    ... {values.Count - 12} more (tc_safety action=show path=...)");
                anyValue |= values.Count > 0;
                Classify(path, name, values, groups, aliases, conns, crcs, parms);
                if (level >= depth) return;
                List<ITcSmTreeItem> kids;
                try { kids = TwinCatSession.Children(it).ToList(); } catch { return; }
                foreach (var c in kids) Walk(c, level + 1);
            }
            List<ITcSmTreeItem> top;
            try { top = TwinCatSession.Children(root).ToList(); } catch { top = new(); }
            if (top.Count == 0) o.AppendLine("  (no safety project)");
            foreach (var c in top) Walk(c, 1);
            if (!anyValue && top.Count == 0)
            {
                var raw = Xml(root);
                o.AppendLine("\nNo values recognized - raw XML of the safety node (shortened):");
                o.AppendLine(SafetyShorten(raw, maxChars));
            }
        }
        if (roots.Count == 0)
        {
            o.AppendLine($"No safety node in the tree (tried {string.Join(", ", SafetyRoots)}).");
            Terminals(o);
            o.AppendLine("\nSafety project files on disk:");
            return (false, o.ToString());
        }
        Summary(o, groups, aliases, conns, crcs, parms);
        Terminals(o);
        return (true, o.ToString());
    }

    string SafetyShow(ITcSmTreeItem it, int maxChars)
    {
        var o = new StringBuilder($"{SafeName(it)}  [type={it.ItemType} sub={it.ItemSubType}]  {it.PathName}\n");
        var xml = Xml(it);
        var values = SafetyValues(xml);
        if (values.Count > 0) { o.AppendLine("Values:"); foreach (var (k, v) in values) o.AppendLine($"  {k} = {v}"); }
        else o.AppendLine("No values recognized.");
        try { var kids = TwinCatSession.Children(it).Select(SafeName).ToList(); if (kids.Count > 0) o.AppendLine("Children: " + string.Join(", ", kids)); } catch { }
        o.AppendLine("\nXML (ProduceXml, shortened):");
        o.AppendLine(xml.Length == 0 ? "(empty - ProduceXml not supported for this item)" : SafetyShorten(xml, maxChars));
        return o.ToString();
    }

    static string SafeName(ITcSmTreeItem it) { try { return it.Name; } catch { return "?"; } }

    static string Xml(ITcSmTreeItem it)
    {
        try { return (string)((dynamic)it).ProduceXml(false) ?? ""; } catch { return ""; }
    }

    static string SafetyShorten(string s, int max)
    {
        s = Regex.Replace(s ?? "", @"<TreeImageData\w*>[^<]*</TreeImageData\w*>", "");   // icon bitmaps (hex) are noise
        return s.Length <= max ? s : s[..max] + $"\n... (shortened, {s.Length} characters - maxChars)";
    }

    static void Classify(string path, string name, List<(string Key, string Val)> values, List<string> groups, List<string> aliases,
                         List<string> conns, List<string> crcs, List<string> parms)
    {
        string Brief(Func<string, bool> sel) => string.Join(", ", values.Where(v => sel(v.Key)).Take(6).Select(v => $"{ShortKey(v.Key)}={v.Val}"));
        var label = string.IsNullOrEmpty(path) ? name : path;
        if (Regex.IsMatch(name, @"group|\.sal$", RegexOptions.IgnoreCase)) groups.Add(label);
        if (Regex.IsMatch(label, @"alias|\.sds$", RegexOptions.IgnoreCase) && !Regex.IsMatch(name, @"^alias\s*devices?$", RegexOptions.IgnoreCase))
            aliases.Add(label + (Brief(k => Regex.IsMatch(k, "fsoe|addr|watchdog|wdg|conn", RegexOptions.IgnoreCase)) is { Length: > 0 } b ? "  " + b : ""));
        var c = Brief(k => Regex.IsMatch(k, "fsoe|watchdog|wdg|conn", RegexOptions.IgnoreCase));
        if (c.Length > 0) conns.Add($"{label}: {c}");
        var crc = Brief(k => Regex.IsMatch(k, "crc|checksum", RegexOptions.IgnoreCase));
        if (crc.Length > 0) crcs.Add($"{label}: {crc}");
        var pa = Brief(k => Regex.IsMatch(k, "safe_?param|safety_?param|module_?fault|com_?error|err_?ack", RegexOptions.IgnoreCase));
        if (pa.Length > 0) parms.Add($"{label}: {pa}");
    }

    static string ShortKey(string key) => key.Split('/').Last();

    static void Summary(StringBuilder o, List<string> groups, List<string> aliases, List<string> conns, List<string> crcs, List<string> parms)
    {
        void Section(string title, List<string> items)
        {
            o.AppendLine($"\n{title} ({items.Count}):");
            if (items.Count == 0) o.AppendLine("  (none recognized)");
            foreach (var x in items.Distinct().Take(100)) o.AppendLine("  " + x);
        }
        Section("TwinSAFE groups", groups);
        Section("Alias devices", aliases);
        Section("Connections (FSoE address / watchdog / connection ID)", conns);
        Section("Safety parameters", parms);
        Section("CRC / checksums", crcs);
    }

    // safety terminals in the I/O configuration (by product name) - their FSoE address is usually set on the device (DIP switch)
    void Terminals(StringBuilder o)
    {
        var hits = new List<string>();
        void Walk(ITcSmTreeItem it, int level)
        {
            if (level > 6 || hits.Count >= 100) return;
            List<ITcSmTreeItem> kids;
            try { kids = TwinCatSession.Children(it).ToList(); } catch { return; }
            foreach (var c in kids)
            {
                var n = SafeName(c);
                if (SafetyTerminal.IsMatch(n))
                {
                    var v = SafetyValues(Xml(c));
                    string path; try { path = c.PathName; } catch { path = n; }
                    hits.Add(path + (v.Count > 0 ? "  " + string.Join(", ", v.Take(6).Select(x => $"{ShortKey(x.Key)}={x.Val}")) : ""));
                }
                Walk(c, level + 1);
            }
        }
        try { Walk(tc.Item("TIID"), 0); } catch { }
        o.AppendLine($"\nSafety terminals in the I/O configuration ({hits.Count}):");
        if (hits.Count == 0) o.AppendLine("  (none found by product name)");
        foreach (var h in hits) o.AppendLine("  " + h);
    }

    // values with a safety-relevant name: attributes, leaf elements and <X Name="..." Value="..."/> / <Name>..</Name><Value>..</Value> pairs
    internal static List<(string Key, string Val)> SafetyValues(string xml)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(xml)) return list;
        XDocument x;
        try { x = XDocument.Parse(xml); } catch { return list; }
        var seen = new HashSet<string>();
        void Put(string k, string v)
        {
            v = Regex.Replace(v ?? "", @"\s+", " ").Trim();
            if (v.Length == 0 || v.Length > 200 || list.Count >= 200 || !seen.Add(k + "=" + v)) return;
            list.Add((k, v));
        }
        static string PathOf(XElement e) => string.Join("/", e.AncestorsAndSelf().Take(3).Reverse().Select(a => a.Name.LocalName));
        foreach (var e in x.Descendants())
        {
            foreach (var at in e.Attributes())
                if (SafetyKey.IsMatch(at.Name.LocalName)) Put(PathOf(e) + "@" + at.Name.LocalName, at.Value);
            if (!e.HasElements && SafetyKey.IsMatch(e.Name.LocalName)) Put(PathOf(e), e.Value);
            // name/value pairs: <Parameter Name="FSoE Address" Value="5"/>, <Param><Name>Watchdog</Name><Value>100</Value></Param>
            var nm = (string)e.Attribute("Name") ?? (e.HasElements ? (string)e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name") : null);
            if (nm != null && SafetyKey.IsMatch(nm))
            {
                var val = (string)e.Attribute("Value") ?? (string)e.Elements().FirstOrDefault(c => c.Name.LocalName is "Value" or "Default" or "DefaultValue");
                if (val != null) Put(PathOf(e) + "[" + nm + "]", val);
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ files on disk
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "_Boot", "_CompileInfo", "_Libraries", ".git", ".vs", "bin", "obj", "doc", ".tcagent" };

    internal static string SafetyFiles(string dir, int maxChars)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "No solution folder - open a solution first.";
        var projects = new List<string>();
        void Find(string d, int level)
        {
            if (level > 6 || projects.Count >= 50) return;
            try
            {
                projects.AddRange(Directory.GetFiles(d, "*.splcproj"));
                foreach (var sub in Directory.GetDirectories(d))
                    if (!SkipDirs.Contains(Path.GetFileName(sub))) Find(sub, level + 1);
            }
            catch { }
        }
        Find(dir, 0);
        var o = new StringBuilder();
        if (projects.Count == 0) return $"No TwinSAFE project files (*.splcproj) below {dir}.";
        foreach (var proj in projects)
        {
            var folder = Path.GetDirectoryName(proj)!;
            o.AppendLine($"Safety project {Path.GetFileNameWithoutExtension(proj)}  ({proj})");
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                                 .Where(f => !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Any(SkipDirs.Contains) && !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                                 .Take(300).ToList();
            }
            catch (Exception ex) { o.AppendLine("  (folder not readable: " + ex.Message + ")"); continue; }
            var groups = files.Where(f => f.EndsWith(".sal", StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetRelativePath(folder, f)).ToList();
            var aliases = files.Where(f => f.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetRelativePath(folder, f)).ToList();
            o.AppendLine($"  TwinSAFE groups (*.sal): {(groups.Count == 0 ? "-" : string.Join(", ", groups))}");
            o.AppendLine($"  Alias devices (*.sds): {(aliases.Count == 0 ? "-" : string.Join(", ", aliases))}");
            var nothing = true;
            foreach (var f in files)
            {
                string text;
                try { if (new FileInfo(f).Length > 4_000_000) continue; text = File.ReadAllText(f); } catch { continue; }
                if (!text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("<")) continue;           // XML files only
                var values = SafetyValues(text);
                if (values.Count == 0) continue;
                nothing = false;
                o.AppendLine($"  {Path.GetRelativePath(folder, f)}:");
                foreach (var (k, v) in values.Take(25)) o.AppendLine($"    {k} = {v}");
                if (values.Count > 25) o.AppendLine($"    ... {values.Count - 25} more");
            }
            if (nothing)
            {
                o.AppendLine("  No values recognized - project file (shortened):");
                try { o.AppendLine(SafetyShorten(File.ReadAllText(proj), maxChars)); } catch (Exception ex) { o.AppendLine("  (not readable: " + ex.Message + ")"); }
            }
        }
        return o.ToString();
    }
}
