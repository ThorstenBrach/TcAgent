// TcUnit test skeleton of a function block - deterministic first (docs/DETERMINISTIC_FIRST.md): TcAgent writes the
// structure the same way every time, the agent only fills in what needs understanding (the expected values).
//
//   suite         FUNCTION_BLOCK <FB>_Test EXTENDS TcUnit.FB_TestSuite with the enable bools of the TcUnit Tests window
//                 (bRunSuite, bRun_<TestMethod>) and one call per test (docs/TCUNIT_TESTS.md, section 4)
//   InitialOutputs  a complete test: a fresh instance, one call, every output with a known type is compared with its
//                 declared initial value (or the default of its type)
//   Test_<Method>   one test per public method: arrange (the inputs as comments, a local per parameter), act (the call
//                 with every parameter), and an assertion that
//                 FAILS until it is replaced ("TODO") - a skeleton never passes by accident
//
// VAR_IN_OUT parameters get local variables, because a call without them does not compile. Pure text (no IDE), so the
// unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class TestSkeleton
{
    public sealed record Var(string Section, string Name, string Type, string Init);
    public sealed record Method(string Name, string Access, string ReturnType, List<Var> Vars);
    public sealed record Block(string Name, string Declaration, string Implementation, string Comment);

    public sealed class Suite
    {
        public Block Fb;
        public readonly List<Block> Methods = new();
        public readonly List<string> Notes = new();
    }

    static readonly Regex Section = new(@"^\s*(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_INST|VAR_STAT|VAR_TEMP|VAR_GLOBAL|VAR_EXTERNAL|VAR_CONFIG|VAR)\b(?:\s+(?:CONSTANT|RETAIN|PERSISTENT))*\s*$", RegexOptions.IgnoreCase);
    static readonly Regex VarLine = new(@"^\s*(\w+)\s*(?:AT\s+%\S+\s*)?:\s*(.+?)\s*(?::=\s*(.+?))?\s*;\s*$", RegexOptions.IgnoreCase);
    static readonly Regex MethodHead = new(@"^\s*METHOD\s+((?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*)(\w+)\s*(?::\s*(.+?))?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static string StripComments(string decl) =>
        Regex.Replace(Regex.Replace(decl ?? "", @"\(\*.*?\*\)", "", RegexOptions.Singleline), @"//.*$|\{[^}]*\}", "", RegexOptions.Multiline);

    /// <summary>The variables of a declaration with their section (VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR ...).</summary>
    public static List<Var> ParseVars(string decl)
    {
        var list = new List<Var>();
        string section = null;
        foreach (var line in StripComments(decl).Replace("\r\n", "\n").Split('\n'))
        {
            if (Section.Match(line) is { Success: true } s) { section = s.Groups[1].Value.ToUpperInvariant(); continue; }
            if (Regex.IsMatch(line, @"^\s*END_VAR\b", RegexOptions.IgnoreCase)) { section = null; continue; }
            if (section == null) continue;
            foreach (var part in line.Split(';').Where(p => p.Trim().Length > 0))
                if (VarLine.Match(part.Trim() + ";") is { Success: true } m)
                    list.Add(new Var(section, m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Success ? m.Groups[3].Value.Trim() : null));
        }
        return list;
    }

    public static Method ParseMethod(string name, string decl)
    {
        var m = MethodHead.Match(StripComments(decl));
        var access = m.Success ? m.Groups[1].Value.Trim().ToUpperInvariant() : "";
        return new Method(m.Success ? m.Groups[2].Value : name, access, m.Success && m.Groups[3].Success ? m.Groups[3].Value.Trim() : null, ParseVars(decl));
    }

    // ------------------------------------------------------------------ asserts
    static readonly string[] Plain = { "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT",
                                       "TIME", "LTIME", "DATE", "TIME_OF_DAY", "TOD", "DATE_AND_TIME", "DT" };

    static string BaseType(string type) => Regex.Replace(type.Trim().ToUpperInvariant(), @"^(W?STRING)\s*[\(\[].*$", "$1");

    /// <summary>The TcUnit assertion for a type, null when there is none (structs, arrays, enums, references ...).</summary>
    public static string Assert(string type, string expected, string actual, string message)
    {
        var t = BaseType(type);
        var msg = "'" + message.Replace("'", "$'") + "'";
        if (t is "REAL" or "LREAL") return $"AssertEquals_{t}(Expected := {expected}, Actual := {actual}, Delta := 0.0001, Message := {msg});";
        if (t is "STRING" or "WSTRING") return $"AssertEquals_{t}(Expected := {expected}, Actual := {actual}, Message := {msg});";
        if (t == "TOD") t = "TIME_OF_DAY";
        if (t == "DT") t = "DATE_AND_TIME";
        return Plain.Contains(t) ? $"AssertEquals_{t}(Expected := {expected}, Actual := {actual}, Message := {msg});" : null;
    }

    /// <summary>The value a variable has before the first call: its literal initial value or the default of its type.</summary>
    public static string Default(string type, string init)
    {
        if (init != null) return Regex.IsMatch(init, @"^(TRUE|FALSE|-?[\d_.]+(E[+-]?\d+)?|\d+#[\w_]+|[\w]+#[\w_.:-]+|'[^']*'|""[^""]*"")$", RegexOptions.IgnoreCase) ? init : null;
        return BaseType(type) switch
        {
            "BOOL" => "FALSE",
            "REAL" or "LREAL" => "0.0",
            "STRING" => "''",
            "WSTRING" => "\"\"",
            "TIME" => "T#0S",
            "LTIME" => "LTIME#0S",
            "DATE" => "D#1970-01-01",
            "TIME_OF_DAY" or "TOD" => "TOD#00:00:00",
            "DATE_AND_TIME" or "DT" => "DT#1970-01-01-00:00:00",
            var t when Plain.Contains(t) => "0",
            _ => null
        };
    }

    // ------------------------------------------------------------------ generate
    /// <summary>The suite name in the style of the existing suites: the suffix most of them use (e.g. TestSuiteFB, _Test);
    /// a trailing FB of the block is dropped when the suffix ends with FB. Default &lt;FB&gt;_Test.</summary>
    public static string SuiteName(string fbName, IEnumerable<string> existingSuites)
    {
        var names = existingSuites?.ToList() ?? new List<string>();
        var suffix = new[] { "TestSuiteFB", "Test_FB", "TestSuite", "_Tests", "_Test", "Tests", "Test" }
                     .Select(s => (S: s, N: names.Count(n => n.EndsWith(s, StringComparison.OrdinalIgnoreCase))))
                     .Where(x => x.N > 0).OrderByDescending(x => x.N).ThenByDescending(x => x.S.Length).Select(x => x.S).FirstOrDefault();
        if (suffix == null) return fbName + "_Test";
        var bas = suffix.EndsWith("FB") && fbName.EndsWith("FB") && fbName.Length > 2 ? fbName[..^2] : fbName;
        return bas + suffix;
    }

    public static Suite Generate(string fbName, string fbDecl, IEnumerable<(string Name, string Decl)> methods, string suiteName = null)
    {
        var s = new Suite();
        suiteName ??= fbName + "_Test";
        var vars = ParseVars(fbDecl);
        var inOuts = vars.Where(v => v.Section == "VAR_IN_OUT").ToList();
        var tests = new List<string>();

        // locals of a test: the instance and one variable per VAR_IN_OUT of the block
        string Locals(IEnumerable<Var> extra = null)
        {
            var sb = new StringBuilder("VAR\n").Append($"    fbUnderTest : {fbName};\n");
            foreach (var v in inOuts) sb.Append($"    {v.Name}_ : {v.Type};\n");
            foreach (var v in extra ?? Enumerable.Empty<Var>()) sb.Append($"    {v.Name} : {v.Type};\n");
            return sb.Append("END_VAR").ToString();
        }
        string Call() => "fbUnderTest(" + string.Join(", ", inOuts.Select(v => $"{v.Name} := {v.Name}_")) + ");";

        // InitialOutputs: complete
        var outputs = vars.Where(v => v.Section == "VAR_OUTPUT").ToList();
        if (outputs.Count > 0)
        {
            var impl = new StringBuilder("TEST('InitialOutputs');\n\n").Append(Call()).Append("\n\n");
            int n = 0;
            foreach (var o in outputs)
            {
                var exp = Default(o.Type, o.Init);
                var a = exp == null ? null : Assert(o.Type, exp, "fbUnderTest." + o.Name, o.Name + " after the first call");
                if (a != null) { impl.Append(a).Append('\n'); n++; }
                else impl.Append($"// {o.Name} : {o.Type} - compare by hand\n");
            }
            impl.Append("\nTEST_FINISHED();");
            if (n == 0) s.Notes.Add("InitialOutputs: no output with a comparable type - only comments");
            s.Methods.Add(new Block("InitialOutputs", "METHOD InitialOutputs\n" + Locals(), impl.ToString(),
                                    $"/// After the first call every output of {fbName} has its initial value"));
            tests.Add("InitialOutputs");
        }

        // one test per public method
        foreach (var (name, decl) in methods)
        {
            var m = ParseMethod(name, decl);
            if (m.Name.StartsWith("FB_init", StringComparison.OrdinalIgnoreCase) || m.Name is "FB_exit" or "FB_reinit" or "FB_Init" or "FB_Exit" or "FB_Reinit") continue;
            if (m.Access.Contains("PRIVATE") || m.Access.Contains("PROTECTED")) { s.Notes.Add($"{m.Name}: {m.Access.ToLowerInvariant()} - tested through the public methods"); continue; }
            var test = "Test_" + m.Name;
            var extra = new List<Var>();
            if (m.ReturnType != null) extra.Add(new Var("VAR", "result", m.ReturnType, null));
            // every parameter gets a local variable and is passed - a call without a required input or in-out does not compile
            var mArgs = m.Vars.Where(v => v.Section is "VAR_INPUT" or "VAR_IN_OUT").ToList();
            extra.AddRange(mArgs.Select(v => new Var("VAR", v.Name + "_", v.Type, null)));
            var impl = new StringBuilder($"TEST('{test}');\n\n// Arrange\n");
            foreach (var v in vars.Where(v => v.Section == "VAR_INPUT")) impl.Append($"// fbUnderTest.{v.Name} := ;   ({v.Type})\n");
            foreach (var v in mArgs.Where(v => v.Section == "VAR_INPUT")) impl.Append($"// {v.Name}_ := ;   (parameter {v.Name} : {v.Type})\n");
            impl.Append("\n// Act\n");
            var args = string.Join(", ", mArgs.Select(v => $"{v.Name} := {v.Name}_"));
            impl.Append(m.ReturnType != null ? $"result := fbUnderTest.{m.Name}({args});\n" : $"fbUnderTest.{m.Name}({args});\n");
            impl.Append("\n// Assert - TODO: replace with the expected behaviour (fails until then)\n")
                .Append($"AssertTrue(Condition := FALSE, Message := 'TODO: assert the result of {m.Name}');\n\nTEST_FINISHED();");
            s.Methods.Add(new Block(test, $"METHOD {test}\n" + Locals(extra), impl.ToString(), $"/// {m.Name} - TODO: describe the expected behaviour"));
            tests.Add(test);
        }
        if (tests.Count == 0) s.Notes.Add($"{fbName} has no outputs and no public methods - write the tests by hand");

        var fbDeclText = new StringBuilder($"FUNCTION_BLOCK {suiteName} EXTENDS TcUnit.FB_TestSuite\nVAR\n    bRunSuite : BOOL := TRUE;\n");
        foreach (var t in tests) fbDeclText.Append($"    bRun_{t} : BOOL := TRUE;\n");
        fbDeclText.Append("END_VAR");
        var body = new StringBuilder("IF bRunSuite THEN\n");
        foreach (var t in tests) body.Append($"    IF bRun_{t} THEN {t}(); END_IF\n");
        body.Append("END_IF");
        s.Fb = new Block(suiteName, fbDeclText.ToString(), body.ToString(), $"/// Tests of {fbName} (generated skeleton - replace the TODO assertions)");
        return s;
    }

    /// <summary>The suite in the plain-text ST format (docs/PLAINTEXT.md) - preview and tc_plaintext import.</summary>
    public static string ToPlainText(Suite s)
    {
        static string Indent(string impl) => string.Join("\n", impl.Split('\n').Select(l => l.Length == 0 ? "" : "  " + l));
        var sb = new StringBuilder();
        sb.Append(s.Fb.Comment).Append('\n').Append(s.Fb.Declaration).Append("\n\n").Append(Indent(s.Fb.Implementation)).Append("\n\nEND_FUNCTION_BLOCK\n\n");
        foreach (var m in s.Methods)
            sb.Append(m.Comment).Append('\n').Append(m.Declaration).Append("\n\n").Append(Indent(m.Implementation)).Append("\n\nEND_METHOD\n\n");
        return sb.ToString();
    }
}
