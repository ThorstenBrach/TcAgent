// TcUnit tests read from the code of a solution (TcAgent.exe tcunit list, tc_test_run action=list, TcUnit Tests window).
//
// Works on the project files only (.sln, .tsproj/.xti, .plcproj, .TcPOU, .TcGVL) - no IDE, no runtime:
//   suite     = FUNCTION_BLOCK that EXTENDS (TcUnit.)FB_TestSuite, directly or through other suites
//   instance  = variable of a suite type in a PROGRAM or GVL (also ARRAY[..] OF suite), e.g. MAIN.fbMotor
//   test      = TEST('Name') / TEST_ORDERED('Name') in a method or in the body of the suite (or of a base suite)
//   enable    = bRunSuite / bRun_<TestMethod> (bRun_<TestName> for inline tests) declared in the suite: they switch
//               suites and tests off for a run (docs/TCUNIT_TESTS.md). Without them a suite runs as a whole.
//   text      = the /// comment above the suite, the test method and the instance
// Tests whose name is not a literal (TEST(sName)) are only known at run time: the suite is marked "dynamic".
//
// Select() turns a filter (--tests) into the values of the enable bools: TRUE for the selected tests, FALSE for the rest.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TcAgent;

public static class TcUnitDiscovery
{
    public sealed class Test
    {
        public string Name, Method, Description, Details, EnableVar, File, EditorPath;
        public int Line, PartLine;
        public bool Ordered;
    }

    public sealed class Suite
    {
        public string Type, Description, Details, File, EditorPath, RunSuiteVar;
        public int Line;
        public bool Dynamic;
        public readonly List<Test> Tests = new();
        /// <summary>All tests can be switched off one by one (bRun_ for every test).</summary>
        public bool TestsSelectable => Tests.Count > 0 && !Dynamic && Tests.All(t => t.EnableVar != null);
    }

    public sealed class Instance
    {
        public string Path, Description, File;
        public int Line;
        public Suite Suite;
    }

    public sealed class Project
    {
        public string Name, TreePath, PlcProj, RunMode;
        public int AmsPort = 851;
        public readonly List<Suite> Suites = new();
        public readonly List<Instance> Instances = new();
        public readonly List<string> Notes = new();
        public int TestCount => Instances.Sum(i => i.Suite.Tests.Count);
    }

    public sealed class Result
    {
        public string Solution;
        public readonly List<Project> Projects = new();
    }

    // ------------------------------------------------------------------ solution -> PLC projects
    static readonly Regex SlnProject = new(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""(?<p>[^""]+\.tsproj)""", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>All PLC projects of the solution with their suites, instances and tests.</summary>
    public static Result Discover(string slnPath)
    {
        var sln = Path.GetFullPath(slnPath);
        var dir = Path.GetDirectoryName(sln)!;
        var r = new Result { Solution = sln };
        var tsprojs = File.Exists(sln)
            ? SlnProject.Matches(File.ReadAllText(sln)).Select(m => Path.GetFullPath(Path.Combine(dir, m.Groups["p"].Value.Replace('\\', Path.DirectorySeparatorChar)))).Where(File.Exists).ToList()
            : new List<string>();
        if (tsprojs.Count == 0) tsprojs = Directory.GetFiles(dir, "*.tsproj", SearchOption.AllDirectories).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ts in tsprojs)
            foreach (var (name, plcproj, port) in PlcProjects(ts))
                if (seen.Add(plcproj)) r.Projects.Add(ReadProject(name, plcproj, port));
        return r;
    }

    /// <summary>PLC projects of a .tsproj: &lt;Plc&gt;&lt;Project Name=.. PrjFilePath=..&gt;, also in separate .xti files (File=..).</summary>
    static IEnumerable<(string Name, string PlcProj, int Port)> PlcProjects(string tsproj)
    {
        var tsDir = Path.GetDirectoryName(tsproj)!;
        XDocument doc;
        try { doc = XDocument.Load(tsproj); } catch { yield break; }
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Project" && e.Parent?.Name.LocalName == "Plc"))
        {
            var name = (string)p.Attribute("Name");
            var prj = (string)p.Attribute("PrjFilePath");
            var port = (int?)p.Attribute("AmsPort") ?? 851;
            if (prj == null && (string)p.Attribute("File") is string xti)
            {
                // independent project file: <tsproj dir>\_Config\PLC\<name>.xti
                var xf = Directory.GetFiles(tsDir, Path.GetFileName(xti), SearchOption.AllDirectories).FirstOrDefault();
                if (xf != null)
                    try
                    {
                        var xp = XDocument.Load(xf).Descendants().FirstOrDefault(e => e.Name.LocalName == "Project" && e.Attribute("PrjFilePath") != null);
                        if (xp != null) { prj = (string)xp.Attribute("PrjFilePath"); name ??= (string)xp.Attribute("Name"); port = (int?)xp.Attribute("AmsPort") ?? port; }
                    }
                    catch { }
            }
            if (prj == null) continue;
            var full = Path.GetFullPath(Path.Combine(tsDir, prj.Replace('\\', Path.DirectorySeparatorChar)));
            if (File.Exists(full) && full.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase))
                yield return (name ?? Path.GetFileNameWithoutExtension(full), full, port);
        }
    }

    // ------------------------------------------------------------------ PLC project
    sealed class Pou
    {
        public string Name, Kind, Extends, Description, Details, File, EditorPath, Decl, Impl;
        /// <summary>File lines: first line of the declaration text, the FUNCTION_BLOCK/METHOD line, first line of the implementation.</summary>
        public int DeclLine, HeaderLine, ImplLine;
        public readonly List<Pou> Methods = new();
        public readonly List<(string Name, string Type, string Description, int Line)> Vars = new();
    }

    static Project ReadProject(string name, string plcproj, int port)
    {
        var p = new Project { Name = name, PlcProj = plcproj, AmsPort = port, TreePath = $"TIPC^{name}^{name} Project" };
        var pdir = Path.GetDirectoryName(plcproj)!;
        var pous = new List<Pou>();
        XDocument proj;
        try { proj = XDocument.Load(plcproj); }
        catch (Exception ex) { p.Notes.Add($"{plcproj}: {ex.Message}"); return p; }
        foreach (var c in proj.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            var inc = ((string)c.Attribute("Include") ?? "").Replace('/', '\\');
            if (!inc.EndsWith(".TcPOU", StringComparison.OrdinalIgnoreCase) && !inc.EndsWith(".TcGVL", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(pdir, inc.Replace('\\', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(inc)?.Replace('/', '\\') ?? "";
            var treeDir = p.TreePath + string.Concat(folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(f => "^" + f));
            try { pous.AddRange(ReadObjects(file, treeDir)); }
            catch (Exception ex) { p.Notes.Add($"{inc}: {ex.Message}"); }
        }

        var byName = pous.Where(x => x.Kind != "GVL").GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // suite chain: the type and its bases up to (TcUnit.)FB_TestSuite, null = no suite
        List<Pou> Chain(string type)
        {
            var list = new List<Pou>();
            for (var t = Bare(type); list.Count < 16;)
            {
                if (IsTestSuiteBase(t)) return list;
                if (!byName.TryGetValue(t, out var pou) || pou.Kind != "FUNCTION_BLOCK" || pou.Extends == null) return null;
                list.Add(pou);
                t = Bare(pou.Extends);
            }
            return null;
        }

        var suites = new Dictionary<string, Suite>(StringComparer.OrdinalIgnoreCase);
        Suite SuiteOf(string type)
        {
            if (suites.TryGetValue(type, out var s)) return s;
            var chain = Chain(type);
            if (chain == null) return null;
            s = BuildSuite(chain);
            suites[type] = s;
            p.Suites.Add(s);
            return s;
        }
        foreach (var fb in pous.Where(x => x.Kind == "FUNCTION_BLOCK")) SuiteOf(fb.Name);

        // instances: declaration order of the programs and GVLs, as TcUnit registers them
        foreach (var owner in pous.Where(x => x.Kind is "PROGRAM" or "GVL"))
            foreach (var v in owner.Vars)
            {
                var (type, lo, hi) = ArrayOf(v.Type);
                var s = SuiteOf(type);
                if (s == null) continue;
                if (lo == null) p.Instances.Add(new Instance { Path = owner.Name + "." + v.Name, Description = v.Description, File = owner.File, Line = v.Line, Suite = s });
                else if (hi - lo > 999) p.Notes.Add($"{owner.Name}.{v.Name}: array of {hi - lo + 1} suites not listed");
                else
                    for (var i = lo.Value; i <= hi; i++)
                        p.Instances.Add(new Instance { Path = $"{owner.Name}.{v.Name}[{i}]", Description = v.Description, File = owner.File, Line = v.Line, Suite = s });
            }

        foreach (var prg in pous.Where(x => x.Kind == "PROGRAM"))
        {
            var code = StripComments(prg.Impl ?? "");
            if (Regex.IsMatch(code, @"\bRUN_IN_SEQUENCE\s*\(", RegexOptions.IgnoreCase)) p.RunMode = "sequence";
            else if (p.RunMode == null && Regex.IsMatch(code, @"\bTcUnit\s*\.\s*RUN\s*\(", RegexOptions.IgnoreCase)) p.RunMode = "parallel";
        }
        return p;
    }

    static Suite BuildSuite(List<Pou> chain)
    {
        var fb = chain[0];
        var s = new Suite { Type = fb.Name, Description = fb.Description, Details = fb.Details, File = fb.File, Line = fb.HeaderLine, EditorPath = fb.EditorPath };
        var vars = new HashSet<string>(chain.SelectMany(c => c.Vars.Select(v => v.Name)), StringComparer.OrdinalIgnoreCase);
        string Enable(string n) => vars.FirstOrDefault(v => v.Equals("bRun_" + n, StringComparison.OrdinalIgnoreCase));
        s.RunSuiteVar = vars.FirstOrDefault(v => v.Equals("bRunSuite", StringComparison.OrdinalIgnoreCase));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pou in chain)
        {
            foreach (var part in new[] { pou }.Concat(pou.Methods))
            {
                var impl = part.Impl ?? "";
                var code = StripComments(impl);
                foreach (Match m in LiteralTest.Matches(code))
                {
                    var name = m.Groups["name"].Value;
                    if (!names.Add(name)) continue;
                    var lineInPart = code[..m.Index].Count(ch => ch == '\n') + 1;
                    bool inMethod = part != pou;
                    s.Tests.Add(new Test
                    {
                        Name = name, Method = inMethod ? part.Name : null, Ordered = m.Groups["ord"].Success,
                        Description = inMethod ? part.Description : null, Details = inMethod ? part.Details : null,
                        EnableVar = Enable(inMethod ? part.Name : name) ?? (inMethod ? Enable(name) : null),
                        File = part.File, Line = part.ImplLine + lineInPart - 1, PartLine = lineInPart, EditorPath = part.EditorPath
                    });
                }
                // a call with a non-literal name: TEST(sName) / TEST_ORDERED(Name)
                foreach (Match m in TestCall.Matches(code))
                    if (!m.Groups["arg"].Value.TrimStart().StartsWith("'")) s.Dynamic = true;
            }
        }
        return s;
    }

    static readonly Regex LiteralTest = new(@"\bTEST(?<ord>_ORDERED)?\s*\(\s*'(?<name>[^']*)'", RegexOptions.IgnoreCase);
    static readonly Regex TestCall = new(@"\bTEST(?:_ORDERED)?\s*\((?<arg>[^)]*)\)", RegexOptions.IgnoreCase);

    static bool IsTestSuiteBase(string t) => t.Equals("FB_TestSuite", StringComparison.OrdinalIgnoreCase);

    /// <summary>Type name without namespace (TcUnit.FB_TestSuite -> FB_TestSuite) and without string length/range.</summary>
    static string Bare(string t)
    {
        t = (t ?? "").Trim();
        var i = t.IndexOfAny(new[] { '(', '[' });
        if (i > 0) t = t[..i].Trim();
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    static readonly Regex ArrayRx = new(@"^ARRAY\s*\[\s*(?<lo>-?\d+)\s*\.\.\s*(?<hi>-?\d+)\s*\]\s*OF\s+(?<t>.+)$", RegexOptions.IgnoreCase);

    static (string Type, int? Lo, int? Hi) ArrayOf(string type)
    {
        var m = ArrayRx.Match(type.Trim());
        return m.Success ? (Bare(m.Groups["t"].Value), int.Parse(m.Groups["lo"].Value), int.Parse(m.Groups["hi"].Value)) : (Bare(type), null, null);
    }

    // ------------------------------------------------------------------ .TcPOU / .TcGVL
    static readonly Regex Header = new(@"^\s*(?<kind>FUNCTION_BLOCK|PROGRAM|FUNCTION|METHOD|INTERFACE)\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|ABSTRACT|FINAL)\s+)*(?<name>\w+)(?:\s+EXTENDS\s+(?<ext>[\w.]+))?", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    static IEnumerable<Pou> ReadObjects(string file, string treeDir)
    {
        var doc = XDocument.Load(file, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var root = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName is "POU" or "GVL");
        if (root == null) yield break;
        var pou = ReadPart(root, file, treeDir + "^" + (string)root.Attribute("Name"));
        if (root.Name.LocalName == "GVL") { pou.Kind = "GVL"; pou.Name = (string)root.Attribute("Name"); }
        foreach (var m in root.Elements().Where(e => e.Name.LocalName == "Method"))
            pou.Methods.Add(ReadPart(m, file, pou.EditorPath + "^" + (string)m.Attribute("Name")));
        yield return pou;
    }

    static Pou ReadPart(XElement e, string file, string editorPath)
    {
        var p = new Pou { Name = (string)e.Attribute("Name"), File = file, EditorPath = editorPath };
        (p.Decl, p.DeclLine) = Cdata(e.Elements().FirstOrDefault(x => x.Name.LocalName == "Declaration"));
        (p.Impl, p.ImplLine) = Cdata(e.Elements().FirstOrDefault(x => x.Name.LocalName == "Implementation")?.Elements().FirstOrDefault(x => x.Name.LocalName == "ST"));
        var decl = p.Decl ?? "";
        var lines = decl.Replace("\r\n", "\n").Split('\n');
        var code = StripComments(decl);     // same lines as decl
        var h = Header.Match(code);
        p.HeaderLine = p.DeclLine;
        if (h.Success)
        {
            p.Kind = h.Groups["kind"].Value.ToUpperInvariant();
            p.Name = h.Groups["name"].Value;
            p.Extends = h.Groups["ext"].Success ? h.Groups["ext"].Value : null;
            var hl = LineOf(code, h.Groups["kind"].Index);
            (p.Description, p.Details) = SplitDoc(DocAbove(lines, hl));
            p.HeaderLine += hl;
        }
        ReadVars(p, lines);
        return p;
    }

    static int LineOf(string text, int index) => text[..index].Count(c => c == '\n');

    /// <summary>Text of a CDATA element and the file line its first text line is on.</summary>
    static (string Text, int Line) Cdata(XElement e)
    {
        if (e == null) return (null, 0);
        var node = e.Nodes().OfType<XCData>().FirstOrDefault();
        var text = node?.Value ?? e.Value;
        var info = (System.Xml.IXmlLineInfo)(node ?? (XObject)e);
        return (text, info.HasLineInfo() ? info.LineNumber : 0);
    }

    /// <summary>The /// lines directly above line index i (attribute pragmas in between are skipped), one per line.</summary>
    static string DocAbove(string[] lines, int i)
    {
        var doc = new List<string>();
        for (int k = i - 1; k >= 0; k--)
        {
            var t = lines[k].Trim();
            if (t.StartsWith("///")) { doc.Insert(0, t[3..].Trim()); continue; }
            if (t.StartsWith("{") && t.EndsWith("}")) continue;
            break;
        }
        while (doc.Count > 0 && doc[0].Length == 0) doc.RemoveAt(0);
        while (doc.Count > 0 && doc[^1].Length == 0) doc.RemoveAt(doc.Count - 1);
        return doc.Count == 0 ? null : string.Join("\n", doc);
    }

    static readonly Regex Section = new(@"^(?:[A-Za-z][\w /()-]{0,30}:(?:\s|$)|(?:\d+[.)]|[-*])\s)", RegexOptions.CultureInvariant);

    /// <summary>A /// text split into the description (the first sentence or lines up to an empty line, a "Keyword:" line or a
    /// list) and the details below it (Setup:, Steps:, Expected: ... - shown by the window and the report), null if none.</summary>
    public static (string Description, string Details) SplitDoc(string doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return (null, null);
        var lines = doc.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        int n = 1;
        while (n < lines.Count && lines[n].Length > 0 && !Section.IsMatch(lines[n])) n++;
        var details = string.Join("\n", lines.Skip(n)).Trim('\n');
        return (string.Join(" ", lines.Take(n)), details.Length == 0 ? null : details);
    }

    static readonly Regex VarLine = new(@"^\s*(?<names>\w+(?:\s*,\s*\w+)*)\s*(?:AT\s+%\S+\s*)?:\s*(?<type>[^;]+?)\s*(?::=.*)?;", RegexOptions.IgnoreCase);
    static readonly Regex VarStart = new(@"^\s*VAR(?:_\w+)?\b", RegexOptions.IgnoreCase);
    static readonly Regex VarEnd = new(@"^\s*END_VAR\b", RegexOptions.IgnoreCase);

    static void ReadVars(Pou p, string[] rawLines)
    {
        var code = StripComments(string.Join("\n", rawLines)).Split('\n');
        bool inVar = false;
        for (int i = 0; i < code.Length; i++)
        {
            var l = code[i];
            if (VarEnd.IsMatch(l)) { inVar = false; continue; }
            if (VarStart.IsMatch(l)) { inVar = true; continue; }
            if (!inVar) continue;
            var m = VarLine.Match(l);
            if (!m.Success) continue;
            var type = m.Groups["type"].Value.Trim();
            foreach (var n in m.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                p.Vars.Add((n, type, SplitDoc(DocAbove(rawLines, i)).Description, p.DeclLine + i));
        }
    }

    /// <summary>Code without comments ((* *), /* */, //); strings stay as they are, so TEST('a') names survive.
    /// Comments keep their line ends, so line numbers stay the same.</summary>
    public static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != c) { if (s[j] == '$' && j + 1 < s.Length) j++; j++; }
                sb.Append(s, i, Math.Min(j + 1, s.Length) - i);
                i = j;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n');
                continue;
            }
            if ((c == '(' || c == '/') && i + 1 < s.Length && s[i + 1] == '*')
            {
                var close = c == '(' ? "*)" : "*/";
                var end = s.IndexOf(close, i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                foreach (var ch in s.AsSpan(i, end - i)) if (ch == '\n') sb.Append('\n');
                i = end - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ selection
    public sealed class Selection
    {
        /// <summary>Symbol path -> value of every enable bool of the project.</summary>
        public readonly List<(string Path, bool Value)> Writes = new();
        /// <summary>Instance path -> names of the tests expected to run (null = all, also the ones known only at run time).</summary>
        public readonly Dictionary<string, HashSet<string>> Selected = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tests that run although they were not selected (no enable bool), e.g. "MAIN.fbLegacy (no bRunSuite)".</summary>
        public readonly List<string> AlsoRun = new();
        public readonly List<string> Errors = new();
        public bool Everything;
        /// <summary>Number of tests expected to run, known from the code (dynamic suites count with their literal tests).</summary>
        public int ExpectedCount;
    }

    /// <summary>Enable bools for a filter. Each entry selects an instance (MAIN.fbMotor), a suite type (FB_Motor_Test), one test
    /// (MAIN.fbMotor@WhenStarted, FB_Motor_Test.WhenStarted or only WhenStarted) - case-insensitive. Empty filter = all tests.</summary>
    public static Selection Select(Project p, IEnumerable<string> filter)
    {
        var sel = new Selection();
        var items = (filter ?? Enumerable.Empty<string>()).SelectMany(f => f.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        sel.Everything = items.Count == 0;
        var want = new Dictionary<Instance, HashSet<string>>();   // null set = the whole suite
        void Add(Instance i, string test)
        {
            if (test == null) { want[i] = null; return; }
            if (want.TryGetValue(i, out var set) && set == null) return;
            if (set == null) want[i] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(test);
        }
        foreach (var it in items)
        {
            bool hit = false;
            string inst = it, test = null;
            var at = it.IndexOf('@');
            if (at > 0) { inst = it[..at]; test = it[(at + 1)..]; }
            foreach (var i in p.Instances)
            {
                bool instMatch = i.Path.Equals(inst, StringComparison.OrdinalIgnoreCase) || i.Suite.Type.Equals(inst, StringComparison.OrdinalIgnoreCase);
                if (at > 0)
                {
                    if (!instMatch) continue;
                    var t = i.Suite.Tests.FirstOrDefault(x => x.Name.Equals(test, StringComparison.OrdinalIgnoreCase) || (x.Method?.Equals(test, StringComparison.OrdinalIgnoreCase) ?? false));
                    if (t == null && !i.Suite.Dynamic) continue;
                    Add(i, t?.Name ?? test); hit = true;
                    continue;
                }
                if (instMatch) { Add(i, null); hit = true; continue; }
                // Type.Test or Test alone
                var dot = it.LastIndexOf('.');
                foreach (var t in i.Suite.Tests)
                {
                    bool m = t.Name.Equals(it, StringComparison.OrdinalIgnoreCase) || (t.Method?.Equals(it, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (dot > 0 && i.Suite.Type.Equals(it[..dot], StringComparison.OrdinalIgnoreCase) && t.Name.Equals(it[(dot + 1)..], StringComparison.OrdinalIgnoreCase));
                    if (m) { Add(i, t.Name); hit = true; }
                }
            }
            if (!hit) sel.Errors.Add($"'{it}' matches no suite instance or test of {p.Name}");
        }

        foreach (var i in p.Instances)
        {
            var s = i.Suite;
            HashSet<string> tests = null;
            bool on = sel.Everything || want.ContainsKey(i);
            if (!sel.Everything && want.TryGetValue(i, out var w)) tests = w;
            if (s.RunSuiteVar != null) sel.Writes.Add((i.Path + "." + s.RunSuiteVar, on));
            foreach (var t in s.Tests.Where(t => t.EnableVar != null))
                sel.Writes.Add((i.Path + "." + t.EnableVar, on && (tests == null || tests.Contains(t.Name))));

            if (on)
            {
                // tests that cannot be switched off run together with the selected ones
                var running = tests == null ? null : new HashSet<string>(tests, StringComparer.OrdinalIgnoreCase);
                if (tests != null)
                {
                    foreach (var t in s.Tests.Where(t => t.EnableVar == null && !tests.Contains(t.Name)))
                    {
                        running.Add(t.Name);
                        sel.AlsoRun.Add($"{i.Path}@{t.Name} (no {(t.Method != null ? "bRun_" + t.Method : "bRun_" + t.Name)})");
                    }
                    if (s.Dynamic) { running = null; sel.AlsoRun.Add($"{i.Path}: all tests - test names known only at run time"); }
                }
                sel.Selected[i.Path] = running;
                sel.ExpectedCount += running?.Count ?? s.Tests.Count;
            }
            else if (s.RunSuiteVar == null)
            {
                // without bRunSuite: switched-off test bools leave an empty suite; tests without bools still run
                var left = s.Tests.Where(t => t.EnableVar == null).Select(t => t.Name).ToList();
                if (left.Count > 0 || s.Dynamic || s.Tests.Count == 0)
                {
                    sel.Selected[i.Path] = s.Dynamic || s.Tests.Count == 0 ? null : new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
                    sel.ExpectedCount += left.Count;
                    sel.AlsoRun.Add($"{i.Path} (no bRunSuite)");
                }
            }
        }
        return sel;
    }

    /// <summary>Filter entries "@file" replaced by the tests of a saved test selection (TcUnit Tests window, *.tcunit.json):
    /// {"tests": [...]}, a JSON array or one entry per line (# comments). Relative paths are relative to baseDir.</summary>
    public static List<string> ExpandSelectionFiles(IEnumerable<string> filter, string baseDir)
    {
        var list = new List<string>();
        foreach (var f in filter ?? Enumerable.Empty<string>())
        {
            var it = f?.Trim() ?? "";
            if (!it.StartsWith("@")) { if (it.Length > 0) list.Add(it); continue; }
            var path = it[1..].Trim().Trim('"');
            if (!Path.IsPathRooted(path) && baseDir != null) path = Path.Combine(baseDir, path);
            if (!File.Exists(path)) throw new FileNotFoundException("Test selection not found: " + path);
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("{") || text.StartsWith("["))
            {
                var node = JsonNode.Parse(text);
                var arr = node as JsonArray ?? node?["tests"] as JsonArray ?? throw new FormatException(path + ": no \"tests\" list");
                list.AddRange(arr.Select(x => x?.ToString().Trim()).Where(x => !string.IsNullOrEmpty(x)));
            }
            else list.AddRange(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")));
        }
        return list;
    }

    // ------------------------------------------------------------------ JSON (tcunit list)
    public static JsonObject ToJson(Result r) => new()
    {
        ["solution"] = r.Solution,
        ["projects"] = new JsonArray(r.Projects.Select(p => (JsonNode)ToJson(p)).ToArray())
    };

    public static JsonObject ToJson(Project p)
    {
        var o = new JsonObject
        {
            ["name"] = p.Name, ["treePath"] = p.TreePath, ["plcproj"] = p.PlcProj, ["amsPort"] = p.AmsPort,
            ["runMode"] = p.RunMode, ["tests"] = p.TestCount,
            ["suites"] = new JsonArray(p.Suites.Select(s => (JsonNode)new JsonObject
            {
                ["type"] = s.Type, ["description"] = s.Description, ["details"] = s.Details, ["file"] = s.File, ["line"] = s.Line, ["editorPath"] = s.EditorPath,
                ["runSuiteVar"] = s.RunSuiteVar, ["testsSelectable"] = s.TestsSelectable, ["dynamic"] = s.Dynamic,
                ["tests"] = new JsonArray(s.Tests.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["method"] = t.Method, ["description"] = t.Description, ["details"] = t.Details, ["enableVar"] = t.EnableVar,
                    ["ordered"] = t.Ordered, ["file"] = t.File, ["line"] = t.Line, ["editorPath"] = t.EditorPath, ["editorLine"] = t.PartLine
                }).ToArray())
            }).ToArray()),
            ["instances"] = new JsonArray(p.Instances.Select(i => (JsonNode)new JsonObject
            {
                ["path"] = i.Path, ["suite"] = i.Suite.Type, ["description"] = i.Description, ["file"] = i.File, ["line"] = i.Line,
                ["runSuite"] = i.Suite.RunSuiteVar == null ? null : i.Path + "." + i.Suite.RunSuiteVar,
                ["tests"] = new JsonArray(i.Suite.Tests.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["enable"] = t.EnableVar == null ? null : i.Path + "." + t.EnableVar
                }).ToArray())
            }).ToArray())
        };
        if (p.Notes.Count > 0) o["notes"] = new JsonArray(p.Notes.Select(n => (JsonNode)n).ToArray());
        return o;
    }
}
