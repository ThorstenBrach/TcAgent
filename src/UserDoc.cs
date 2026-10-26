// User documentation (manual) of function blocks, functions, interfaces and their data types.
//
//   tc_userdoc action=templates             example documents the user provided (the agent copies their structure and style)
//   tc_userdoc action=template name=...     content of one example
//   tc_userdoc action=data objects=...      everything a manual needs, per FB/function: description, inputs/outputs/in-outs,
//                                           methods and properties with parameters, the STRUCTs/ENUMs/aliases they use (fields,
//                                           values) - with the existing comments, missing ones marked, plus the code so the agent
//                                           can explain from context what is not commented
//   tc_userdoc action=write file=... content=...   writes the finished document (<solution>\doc\user\...)
//
// Templates are example manuals, not fill-in forms: Markdown (.md), AsciiDoc (.adoc) or HTML (.html) - the format of the
// result follows the template (or 'format'). Folders: <solution>\.tcagent\doc-templates, %LOCALAPPDATA%\TcAgent\doc-templates,
// TCAGENT_DOC_TEMPLATES (';'-separated files/folders).
// Missing comments can be written back into the code with tc_refactor (always with the user's consent).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class UserDoc
{
    static readonly string[] TemplateExt = { ".md", ".markdown", ".adoc", ".asciidoc", ".html", ".htm", ".txt" };

    public static List<string> TemplateFiles(string solutionDir)
    {
        var list = new List<string>();
        void AddPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (Directory.Exists(p))
                list.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories)
                                       .Where(f => TemplateExt.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) list.Add(p);
        }
        // most specific first: project, platform (user, company), user, company - the agent picks the best fitting example
        foreach (var l in KnowledgeLayers.Layers(solutionDir, KnowledgeLayers.Platforms(solutionDir).Active).OrderByDescending(l => l.Rank))
            AddPath(Path.Combine(l.Root, "doc-templates"));
        foreach (var p in (Environment.GetEnvironmentVariable("TCAGENT_DOC_TEMPLATES") ?? "").Split(';')) AddPath(p);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string FormatOf(string file) => Path.GetExtension(file ?? "").ToLowerInvariant() switch
    {
        ".adoc" or ".asciidoc" => "asciidoc",
        ".html" or ".htm" => "html",
        _ => "markdown"
    };

    // ------------------------------------------------------------------ data types
    public sealed class Member { public string Name, Value, Type, Comment; }

    public sealed class TypeInfo
    {
        public string Name, Kind = "?", BaseType = "", Doc = "";     // Kind: STRUCT, UNION, ENUM, ALIAS
        public readonly List<Member> Members = new();
    }

    static string StripBlock(string s) => Regex.Replace(s ?? "", @"\(\*.*?\*\)", " ", RegexOptions.Singleline);
    static string StripComments(string s) => Regex.Replace(StripBlock(s), @"//[^\n]*", " ");

    /// <summary>Splits a line into code and trailing comment ('//' or '(* *)' outside string literals).</summary>
    internal static (string Code, string Comment) SplitComment(string line)
    {
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q != '\0') { if (ch == '$') i++; else if (ch == q) q = '\0'; continue; }
            if (ch is '\'' or '"') { q = ch; continue; }
            if (i + 1 < line.Length && (ch == '/' && line[i + 1] == '/' || ch == '(' && line[i + 1] == '*'))
            {
                var c = line[i..];
                c = c.StartsWith("//") ? c[2..] : Regex.Replace(c[2..], @"\*\)\s*$", "");
                return (line[..i], c.Trim());
            }
        }
        return (line, null);
    }

    public static TypeInfo ParseDut(string name, string decl)
    {
        var t = new TypeInfo { Name = name };
        decl = (decl ?? "").Replace("\r\n", "\n");
        var doc = new List<string>();
        foreach (var l in decl.Split('\n')) { var x = l.Trim(); if (x.StartsWith("///")) doc.Add(x.TrimStart('/').Trim()); else if (x.Length > 0 && !x.StartsWith("{")) break; }
        t.Doc = string.Join(" ", doc);
        var body = decl;
        var code = StripComments(body);
        if (Regex.IsMatch(code, @"\bSTRUCT\b", RegexOptions.IgnoreCase)) t.Kind = "STRUCT";
        else if (Regex.IsMatch(code, @"\bUNION\b", RegexOptions.IgnoreCase)) t.Kind = "UNION";
        else if (Regex.IsMatch(code, @"\bTYPE\b[^:]*:\s*\(", RegexOptions.IgnoreCase)) t.Kind = "ENUM";
        else
        {
            var al = Regex.Match(code, @"TYPE\s+\w+\s*:\s*([^;]+);", RegexOptions.IgnoreCase);
            if (al.Success) { t.Kind = "ALIAS"; t.BaseType = al.Groups[1].Value.Trim(); }
            return t;
        }
        var ext = Regex.Match(code, @"\bEXTENDS\s+([\w.]+)", RegexOptions.IgnoreCase);
        if (ext.Success) t.BaseType = "EXTENDS " + ext.Groups[1].Value;
        string pending = null;
        bool inside = false, typeSeen = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (t.Kind is "STRUCT" or "UNION")
            {
                if (!inside && Regex.IsMatch(line, @"^(?:TYPE\b[^:]*:\s*)?(STRUCT|UNION)\b", RegexOptions.IgnoreCase)) { inside = true; continue; }
                if (Regex.IsMatch(line, @"^END_(STRUCT|UNION)\b", RegexOptions.IgnoreCase)) break;
                if (!inside) continue;
                if (line.StartsWith("///")) { pending = (pending == null ? "" : pending + " ") + line.TrimStart('/').Trim(); continue; }
                var m = Regex.Match(line, @"^([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*(?:AT\s+%\S+\s*)?:\s*([^;]+?)\s*(?::=\s*([^;]*))?;\s*(?://\s*(.*)|\(\*\s*(.*?)\s*\*\))?$");
                if (!m.Success) continue;
                var c = pending ?? (m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Success ? m.Groups[5].Value : "");
                foreach (var n in m.Groups[1].Value.Split(','))
                    t.Members.Add(new Member { Name = n.Trim(), Type = m.Groups[2].Value.Trim(), Value = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "", Comment = c.Trim() });
                pending = null;
            }
            else
            {
                // ENUM: values one per line ('(Idle := 0,', 'Run := 1  // x', ') INT;') or several per line ('TYPE E : (A, B) ; END_TYPE')
                if (inside && line.StartsWith("///")) { pending = (pending == null ? "" : pending + " ") + line.TrimStart('/').Trim(); continue; }
                var (lc, cmt) = SplitComment(line);
                if (!inside)
                {
                    if (!typeSeen)
                    {
                        if (!Regex.IsMatch(lc, @"^TYPE\b", RegexOptions.IgnoreCase)) continue;
                        typeSeen = true;
                        var ci = lc.IndexOf(':');
                        lc = ci >= 0 ? lc[(ci + 1)..] : "";
                    }
                    var pi = lc.IndexOf('(');
                    if (pi < 0) continue;
                    inside = true;
                    lc = lc[(pi + 1)..];
                }
                var close = lc.IndexOf(')');
                var items = (close >= 0 ? lc[..close] : lc).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                for (int k = 0; k < items.Count; k++)
                {
                    var m = Regex.Match(items[k], @"^([A-Za-z_]\w*)\s*(?::=\s*(.+))?$");
                    if (!m.Success) continue;
                    var c = (k == 0 ? pending : null) ?? (k == items.Count - 1 ? cmt : null) ?? "";
                    t.Members.Add(new Member { Name = m.Groups[1].Value, Value = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "", Comment = c.Trim() });
                }
                if (items.Count > 0) pending = null;
                if (close >= 0)
                {
                    var bt = Regex.Match(lc[(close + 1)..], @"^\s*([A-Za-z_]\w*)");
                    if (bt.Success) t.BaseType = bt.Groups[1].Value;
                    break;
                }
            }
        }
        return t;
    }

    // ------------------------------------------------------------------ data for the manual
    static readonly HashSet<string> Elementary = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT", "REAL", "LREAL",
        "TIME", "LTIME", "DATE", "TOD", "TIME_OF_DAY", "DT", "DATE_AND_TIME", "STRING", "WSTRING", "PVOID", "XINT", "UXINT", "BIT"
    };

    static string BareType(string type) =>
        Regex.Replace(type ?? "", @"^\s*(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", RegexOptions.IgnoreCase).Split('(')[0].Split('[')[0].Trim().Split('.').Last();

    public static string Data(PlcDoc doc, IEnumerable<string> keys, PlcDoc.Summaries mem, bool withCode, int maxCode)
    {
        var o = new StringBuilder();
        var types = new SortedDictionary<string, TypeInfo>(StringComparer.OrdinalIgnoreCase);
        int missing = 0;
        string Desc(PlcDoc.Pou p) { var d = p.Doc.Length > 0 ? p.Doc : mem.Get(p.Key) ?? ""; if (d.Length == 0) missing++; return d.Length > 0 ? d : "(no description - explain it from the code)"; }
        string C(string c) { if (string.IsNullOrWhiteSpace(c)) { missing++; return "(no comment)"; } return c; }
        void UseType(string type, int depth)
        {
            var b = BareType(type);
            if (b.Length == 0 || Elementary.Contains(b) || types.ContainsKey(b) || depth > 4) return;
            if (!doc.Pous.TryGetValue(b, out var dut) || dut.Kind != "DUT") return;
            var t = ParseDut(dut.Name, dut.Decl);
            types[b] = t;
            foreach (var m in t.Members) if (m.Type != null) UseType(m.Type, depth + 1);
            if (t.Kind == "ALIAS") UseType(t.BaseType, depth + 1);
        }
        void Vars(PlcDoc.Pou p, string indent)
        {
            foreach (var g in p.Vars.Where(v => v.Section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "RETURN").GroupBy(v => v.Section))
            {
                o.AppendLine($"{indent}{g.Key switch { "VAR_INPUT" => "Inputs", "VAR_OUTPUT" => "Outputs", "VAR_IN_OUT" => "In/out", _ => "Return value" }}:");
                foreach (var v in g)
                {
                    o.AppendLine($"{indent}  {v.Name} : {v.Type}  -- {(g.Key == "RETURN" ? (v.Comment ?? "") : C(v.Comment))}");
                    UseType(v.Type, 0);
                }
            }
        }
        foreach (var key in keys)
        {
            if (!doc.Pous.TryGetValue(key, out var p)) continue;
            if (p.Kind == "DUT") { UseType(p.Name, 0); continue; }
            o.AppendLine($"\n# {p.Name}  [{p.Kind}]  {p.Path}" + (p.Extends != null ? $"  EXTENDS {p.Extends}" : "") +
                         (p.Implements.Count > 0 ? "  IMPLEMENTS " + string.Join(", ", p.Implements) : ""));
            o.AppendLine("Description: " + Desc(p));
            Vars(p, "");
            foreach (var mk in p.Members)
            {
                var m = doc.Pous[mk];
                if (m.Kind is "TRANSITION") continue;
                var access = Regex.Match(m.Decl, @"^\s*(?:METHOD|PROPERTY)\s+(PUBLIC|PRIVATE|PROTECTED|INTERNAL)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (access.Success && access.Groups[1].Value.ToUpperInvariant() is "PRIVATE" or "PROTECTED") continue;   // not part of the user's API
                o.AppendLine($"\n## {m.Kind} {p.Name}.{m.Name}");
                o.AppendLine("Description: " + Desc(m));
                Vars(m, "");
                if (withCode && m.Impl.Trim().Length > 0) o.AppendLine("Code:\n" + Shorten(m.Impl, maxCode / 2));
            }
            foreach (var v in p.Vars.Where(v => v.Section is "VAR" or "VAR_STAT")) UseType(v.Type, 0);
            if (withCode && (p.Impl.Trim().Length > 0 || p.SfcText != null)) o.AppendLine("\nCode:\n" + Shorten(p.SfcText ?? p.Impl, maxCode));
        }
        if (types.Count > 0)
        {
            o.AppendLine("\n# Data types used");
            foreach (var t in types.Values)
            {
                var dut = doc.Pous[t.Name];
                o.AppendLine($"\n## {t.Kind} {t.Name}" + (t.BaseType.Length > 0 ? $"  ({t.BaseType})" : "") + $"  {dut.Path}");
                o.AppendLine("Description: " + (t.Doc.Length > 0 ? t.Doc : mem.Get(t.Name) ?? C("")));
                foreach (var m in t.Members)
                    o.AppendLine(t.Kind == "ENUM" ? $"  {m.Name}{(m.Value.Length > 0 ? " = " + m.Value : "")}  -- {C(m.Comment)}"
                                                  : $"  {m.Name} : {m.Type}{(m.Value.Length > 0 ? " := " + m.Value : "")}  -- {C(m.Comment)}");
            }
        }
        var head = $"Manual data for {keys.Count()} object(s); {missing} descriptions/comments missing" +
                   (missing > 0 ? " - explain them from the code and context (mark guesses), and offer to write them back with tc_refactor." : ".") + "\n";
        return head + o;
    }

    static string Shorten(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s[..n] + "\n... (shortened)"; }
}

public sealed partial class ToolRegistry
{
    void RegisterUserDoc()
    {
        Add("tc_userdoc", "User documentation (manual) of function blocks, functions, interfaces and their data types - how to USE them " +
                          "(for the program's inner workings use tc_doc). action=templates lists the example documents the user provided; " +
                          "action=template name=... returns one: interpret it as the model for structure, headings, tables, tone and language " +
                          "and produce the same format (Markdown, AsciiDoc or HTML) - it is an example, not a form. action=data objects=... returns " +
                          "per object: description, inputs/outputs/in-outs, public methods and properties with parameters, and all STRUCTs, " +
                          "ENUMs (values) and aliases they use - with existing comments, missing ones marked, plus the code to explain them from " +
                          "context (mark such explanations as derived). action=write file=... content=... saves the finished document " +
                          "(default folder <solution>\\doc\\user). Without a template use a clear manual structure: purpose, interface " +
                          "table, methods, data types, example call. Offer afterwards to write missing comments back with tc_refactor.",
            Schema(Req(P("action", "string", "templates, template, data or write")),
                   P("name", "string", "template: file name of the template (as listed)"),
                   P("objects", "string", "data: comma-separated names (FB_Axis, F_Scale, E_Mode), tree paths or a folder path (all objects " +
                                         "below it), or 'selection' = the object open/selected in the editor"),
                   P("code", "boolean", "data: include the implementation code (default true)"),
                   P("plcProjectPath", "string", "PLC project (empty = the only one)"),
                   P("file", "string", "write: file name or path; relative = <solution>\\doc\\user\\<file>; extension decides the format (.md, .adoc, .html)"),
                   P("content", "string", "write: the complete document")),
            a => UserDocCall(a));
    }

    string UserDocCall(JsonObject a)
    {
        var dir = SolutionDir();
        switch ((Str(a, "action") ?? "data").ToLowerInvariant())
        {
            case "templates":
            {
                var files = UserDoc.TemplateFiles(dir);
                if (files.Count == 0)
                    return "No templates yet. Put example manuals (.md, .adoc or .html) into " +
                           (dir != null ? Path.Combine(dir, ".tcagent", "doc-templates") + " (project) or " : "") +
                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "doc-templates") +
                           " (personal). Without a template, use a clear standard structure.";
                return "Templates (examples to imitate):\n" + string.Join("\n", files.Select(f => $"  {Path.GetFileName(f)}  [{UserDoc.FormatOf(f)}, {new FileInfo(f).Length / 1024.0:0.#} KB]  {f}"));
            }
            case "template":
            {
                var name = Str(a, "name") ?? throw new Exception("name is required");
                var f = UserDoc.TemplateFiles(dir).FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase) || x.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception($"Template {name} not found - action=templates lists them");
                var text = File.ReadAllText(f);
                if (text.Length > 60_000) text = text[..60_000] + "\n... (shortened)";
                return $"Template {Path.GetFileName(f)} - format {UserDoc.FormatOf(f)}. Imitate structure, headings, tables, tone and language; " +
                       "replace the example's content with the real objects:\n\n" + text;
            }
            case "data":
            {
                var spec = Str(a, "objects") ?? "selection";
                var doc = BuildDoc(Str(a, "plcProjectPath"));
                var keys = ResolveDocObjects(doc, spec);
                if (keys.Count == 0) throw new Exception($"No objects found for '{spec}' - names, tree paths, a folder or 'selection'");
                return UserDoc.Data(doc, keys, PlcDoc.Summaries.Load(dir), Bool(a, "code", true), 6000);
            }
            case "write":
            {
                var file = Str(a, "file") ?? throw new Exception("file is required");
                var content = Str(a, "content") ?? throw new Exception("content is required");
                var root = Path.Combine(dir ?? Path.GetTempPath(), "doc", "user");
                var full = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
                if (Path.GetExtension(full).Length == 0) full += ".md";
                full = Path.GetFullPath(full);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content, new UTF8Encoding(false));
                return $"Written: {full} ({UserDoc.FormatOf(full)}, {content.Length / 1024.0:0.#} KB)";
            }
            default: throw new Exception("action: templates, template, data or write");
        }
    }

    // names, tree paths, folders or the editor selection -> object keys of the documentation model
    internal List<string> ResolveDocObjects(PlcDoc doc, string spec)
    {
        var keys = new List<string>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("selection", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = tc.Sta.Run(() => tc.EditorContext());
                var path = ctx.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("TIPC^"))
                           ?? throw new Exception("No PLC object open/selected in the editor");
                var p = doc.Pous.Values.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (p != null) keys.Add(p.Owner ?? p.Key);
                continue;
            }
            if (doc.Pous.TryGetValue(part, out var direct)) { keys.Add(direct.Owner ?? direct.Key); continue; }
            var byPath = doc.Pous.Values.FirstOrDefault(x => x.Path.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (byPath != null) { keys.Add(byPath.Owner ?? byPath.Key); continue; }
            // folder: everything below it (FBs, functions, interfaces, DUTs; no programs/GVLs)
            keys.AddRange(doc.Pous.Values.Where(x => x.Owner == null && x.Path.StartsWith(part + "^", StringComparison.OrdinalIgnoreCase) &&
                                                      x.Kind is "FUNCTION_BLOCK" or "FUNCTION" or "INTERFACE" or "DUT")
                                         .OrderBy(x => x.Kind == "DUT" ? 1 : 0).ThenBy(x => x.Key).Select(x => x.Key));
        }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
