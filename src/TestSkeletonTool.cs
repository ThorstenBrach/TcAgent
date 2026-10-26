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
                   P("suiteName", "string", "name of the suite (default <FB>_Test - follow the naming of the coding rules)"),
                   P("parentPath", "string", "folder for the suite (default: the folder of the function block)"),
                   P("dryRun", "boolean", "only show the generated code")),
            a => tc.Sta.Run(() => TestSkeletonCall(a)));
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
        var suite = TestSkeleton.Generate(item.Name, decl, methods, Str(a, "suiteName"));
        var parentPath = Str(a, "parentPath") ?? path[..path.LastIndexOf('^')];
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
