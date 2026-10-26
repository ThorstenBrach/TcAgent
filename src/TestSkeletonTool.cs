// tc_test_skeleton (TestSkeleton.cs): the TcUnit test suite of a function block, generated deterministically - TcAgent
// writes the structure (suite, enable bools, one test per public method, a complete InitialOutputs test), the agent fills
// in the expected behaviour where the skeleton says TODO.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterTestSkeleton()
    {
        Add("tc_test_skeleton", "Generates the TcUnit test suite of a function block deterministically: <FB>_Test EXTENDS TcUnit.FB_TestSuite with the " +
                                "enable bools of the TcUnit Tests window, a complete test InitialOutputs (every output against its initial value) and one " +
                                "test per public method (arrange / act / an assertion that FAILS until you replace the TODO). Use it instead of writing a " +
                                "suite by hand, then fill in the expected behaviour (tc_plc_write) and add an instance of the suite to the test program " +
                                "(TcUnit.RUN() must be called). dryRun=true shows the code (plain-text ST) without creating anything.",
            Schema(Req(P("name", "string", "the function block (name or tree path)")),
                   P("suiteName", "string", "name of the suite (default: in the style of the existing suites, e.g. <FB>TestSuiteFB, otherwise <FB>_Test)"),
                   P("instanceName", "string", "name of the instance under test in the tests (default fbUnderTest - follow the naming of the coding rules)"),
                   P("parentPath", "string", "folder for the suite (default: the folder of the existing suites, a folder Tests/Test, otherwise the folder of the function block)"),
                   P("dryRun", "boolean", "only show the generated code")),
            a => tc.Sta.Run(() => TestSkeletonCall(a)));
    }

    // The folder for a new suite: where most existing suites of the same PLC project are, otherwise a folder named Tests/Test
    string TestFolder(string fbPath, List<string> suites)
    {
        var project = fbPath.Contains(" Project^") ? fbPath[..(fbPath.IndexOf(" Project^") + 8)] : null;
        if (project == null) return null;
        var index = tc.PlcIndex().Where(x => x.Path.StartsWith(project + "^", StringComparison.OrdinalIgnoreCase)).ToList();
        var suiteFolder = index.Where(x => suites.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Path[..x.Path.LastIndexOf('^')])
                               .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (suiteFolder != null) return suiteFolder;
        foreach (var name in new[] { "Tests", "Test", "UnitTests" })
            foreach (var (n, p) in index.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                try { if (tc.Item(p).ItemType == 601) return p; } catch { }
        return null;
    }

    string TestSkeletonCall(JsonObject a)
    {
        var name = Str(a, "name") ?? throw new Exception("name is required");
        var path = name.Contains('^') ? name : tc.ResolvePlcPathsPublic(name).LastOrDefault() ?? throw new Exception($"'{name}' not found in the PLC project");
        var item = tc.Item(path);
        var decl = (item as ITcPlcDeclaration)?.DeclarationText ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(decl, @"^\s*FUNCTION_BLOCK\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new Exception($"{path} is not a function block");
        var methods = new List<(string, string)>();
        foreach (var c in TwinCatSession.Children(item))
            try { if (c.ItemType == 609 && c is ITcPlcDeclaration md) methods.Add((c.Name, md.DeclarationText)); } catch { }
        // defaults in the style of the project: the naming of the existing suites, their folder (or a folder Tests/Test)
        var existing = new List<string>();
        try
        {
            var sln = (string)tc.RequireDte().Solution.FullName;
            foreach (var p in TcUnitDiscovery.Discover(sln).Projects) existing.AddRange(p.Suites.Select(s => s.Type));
        }
        catch { }
        var suite = TestSkeleton.Generate(item.Name, decl, methods, Str(a, "suiteName") ?? TestSkeleton.SuiteName(item.Name, existing), Str(a, "instanceName"));
        var parentPath = Str(a, "parentPath") ?? TestFolder(path, existing) ?? path[..path.LastIndexOf('^')];
        var text = TestSkeleton.ToPlainText(suite);
        var notes = suite.Notes.Count > 0 ? "\nNotes: " + string.Join("; ", suite.Notes) : "";
        if (DryRun(a)) return $"PROPOSAL (nothing created): {suite.Fb.Name} under {parentPath} - {suite.Methods.Count} test(s){notes}\n\n{text}";
        if (tc.ResolvePlcPathsPublic(suite.Fb.Name).Count > 0) throw new Exception($"{suite.Fb.Name} exists already - pass another suiteName or extend the existing suite");

        string Create(string parent, string n, string kind, TestSkeleton.Block b) => PlcCreate(new JsonObject
        {
            ["parentPath"] = parent, ["name"] = n, ["kind"] = kind, ["declaration"] = b.Comment + "\n" + b.Declaration, ["implementation"] = b.Implementation
        });
        var sb = new StringBuilder();
        sb.AppendLine(Create(parentPath, suite.Fb.Name, "function_block", suite.Fb));
        var suitePath = parentPath + "^" + suite.Fb.Name;
        foreach (var m in suite.Methods) sb.AppendLine(Create(suitePath, m.Name, "method", m));
        tc.InvalidatePlcIndex();
        sb.AppendLine($"\nGenerated {suite.Methods.Count} test(s){notes}.");
        sb.AppendLine("Next: replace the TODO assertions with the expected behaviour (they fail until then), declare an instance of the suite in " +
                      "the test program (e.g. PRG_TEST: fb" + item.Name.Replace("FB_", "") + "_Test : " + suite.Fb.Name + ";) that calls TcUnit.RUN(), then tc_check.");
        return sb.ToString();
    }
}
