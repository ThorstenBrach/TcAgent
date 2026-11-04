// Tests for the prompt library (Prompts.cs): levels, replacing and switching off, the front matter, the shipped prompts.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class PromptsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-pr-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");
    readonly string oldTeam = Environment.GetEnvironmentVariable("TCAGENT_TEAM");

    public PromptsTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", null);
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", oldTeam);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text)
    {
        var f = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    const string Sample = "---\ndescription: Tests for this block\nmetadata:\n  tcagent-category: Tests\n  tcagent-tags: tcunit, test\n---\nWrite tests for {object}.\n";

    [Fact]
    public void ReadsNameFrontMatterAndBody()
    {
        Write(@"user\prompts\test-fb.md", Sample);
        var p = Assert.Single(Prompts.Active(Sln, Array.Empty<string>()));
        Assert.Equal("test-fb", p.Name);
        Assert.Equal("Tests for this block", p.Description);
        Assert.Equal("Tests", p.Category);
        Assert.Equal(new[] { "tcunit", "test" }, p.Tags);
        Assert.Equal("Write tests for {object}.", p.Body);
        Assert.False(p.Send);
    }

    [Fact]
    public void ShortFormsBecomeTheSameModel()
    {
        var body = "A {input:Which cases?} B {input:Target|local|test PLC} C {input:Timeout=30 s} D {input:Which cases?}";
        var p = Prompts.ParamsOf(body, body);
        Assert.Equal(3, p.Count);                                   // the same question once
        Assert.Equal(("Which cases?", "text", (string)null), (p[0].Ask, p[0].Type, p[0].Default));
        Assert.Equal("{input:Target|local|test PLC}", p[1].Token);
        Assert.Equal(("choice", "local"), (p[1].Type, p[1].Default));
        Assert.Equal(new[] { "local", "test PLC" }, p[1].Options);
        Assert.Equal(("Timeout", "30 s"), (p[2].Ask, p[2].Default));
    }

    const string Long = "---\ndescription: x\nparams:\n  - name: target\n    ask: Which object?\n    type: pou\n    default: {object}\n    required: true\n    help: POU, GVL or task\n" +
                        "  - name: depth\n    type: number\n    default: 2\n  - name: deep\n    ask: Deep?\n    type: bool\n  - name: format\n    type: choice\n    options: [list, table]\n" +
                        "  - name: unused\n    ask: never\nmetadata:\n  tcagent-category: Tests\n---\n";

    [Fact]
    public void LongFormFromTheHeaderInTheOrderOfTheText()
    {
        var body = "{format} of {target} depth {depth}, deep {deep}, and {input:More?}";
        var warnings = new List<string>();
        var p = Prompts.ParamsOf(Long + body, body, warnings);
        Assert.Equal(new[] { "format", "target", "depth", "deep", "More?" }, p.Select(x => x.Name));
        var t = p[1];
        Assert.Equal(("Which object?", "pou", "{object}", true, "POU, GVL or task", "{target}"), (t.Ask, t.Type, t.Default, t.Required, t.Help, t.Token));
        Assert.Equal(new[] { "list", "table" }, p[0].Options);
        Assert.Equal("depth", p[2].Ask);                            // no ask: the name
        Assert.Contains(warnings, w => w.Contains("unused"));
    }

    [Fact]
    public void BadParamsAreReportedAndDoNotBreakThePrompt()
    {
        var text = "---\nparams:\n  - name: object\n  - name: a\n    type: colour\n  - name: b\n    type: choice\n---\n";
        var warnings = new List<string>();
        var p = Prompts.ParamsOf(text + "{object} {a} {b}", "{object} {a} {b}", warnings);
        Assert.Equal(new[] { "a", "b" }, p.Select(x => x.Name));    // {object} stays the editor's
        Assert.All(p, x => Assert.Equal("text", x.Type));
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void LongFormInAFileAndHeaderStillRead()
    {
        Write(@"user\prompts\long.md", Long + "Show {target}.\n");
        var i = Assert.Single(Prompts.Active(Sln, Array.Empty<string>()));
        Assert.Equal("Tests", i.Category);
        Assert.Equal("Show {target}.", i.Body);
        Assert.Equal("target", Assert.Single(i.Params).Name);
    }

    [Fact]
    public void ObjectsFromTheFilesOfTheSolution()
    {
        Write(@"MachineX\Plc\POUs\MAIN.TcPOU", "x");
        Write(@"MachineX\Plc\GVLs\GVL_Io.TcGVL", "x");
        Write(@"MachineX\Plc\_Boot\Old.TcPOU", "x");
        Write(@"MachineX\Plc\PlcTask.TcTTO", "x");
        Write(@"MachineX\Machine.tsproj", "<Tasks><Task Id=\"1\"><Name>FastTask</Name></Task></Tasks>");
        var o = Prompts.Objects(Sln);
        Assert.Equal(new[] { "FastTask", "GVL_Io", "MAIN", "PlcTask" }, o.Select(x => x.Name));
        Assert.Equal("Task", o[0].Kind);
    }

    [Fact]
    public void SendFlagFromTheFrontMatter()
    {
        Write(@"user\prompts\go.md", "---\ndescription: x\nmetadata:\n  tcagent-send: true\n---\nDo it.\n");
        Assert.True(Assert.Single(Prompts.Active(Sln, Array.Empty<string>())).Send);
    }

    [Fact]
    public void LaterLevelReplacesAndAllLevelsCount()
    {
        Write(@"builtin\prompts\test-fb.md", "built-in text");
        Write(@"company\prompts\company-only.md", "company text");
        Write(@"MachineX\.tcagent\prompts\test-fb.md", "project text");
        var items = Prompts.Active(Sln, Array.Empty<string>());
        Assert.Equal(new[] { "company-only", "test-fb" }, items.Select(i => i.Name));
        Assert.Equal("project text", items.Single(i => i.Name == "test-fb").Body);
        // without the solution the built-in one applies
        Assert.Equal("built-in text", Prompts.Active(null, Array.Empty<string>()).Single(i => i.Name == "test-fb").Body);
    }

    [Fact]
    public void SwitchedOffAndEmptyPromptsAreLeftOut()
    {
        Write(@"user\prompts\one.md", "text");
        Write(@"user\prompts\empty.md", "---\ndescription: nothing\n---\n");
        KnowledgeLayers.SetDisabled(Sln, "project", "prompts/one", true);
        Assert.Empty(Prompts.Active(Sln, Array.Empty<string>()));
        Assert.Equal("disabled", Prompts.Entries(Sln, Array.Empty<string>()).Single(e => e.Name == "one").Status);
    }

    [Fact]
    public void NewPromptFromTemplateAndFromAFile()
    {
        var f = KnowledgeOps.New(Sln, "prompts", "Check Limits", "user");
        Assert.Equal(Path.Combine(dir, "user", "prompts", "check-limits.md"), f);
        Assert.Contains("{object}", File.ReadAllText(f));
        Write(@"elsewhere\my-prompt.md", "text");
        var g = KnowledgeOps.AddExisting(Sln, "prompts", Path.Combine(dir, "elsewhere", "my-prompt.md"), "project");
        Assert.Equal(Path.Combine(Sln, ".tcagent", "prompts", "my-prompt.md"), g);
        Assert.Equal(2, Prompts.Active(Sln, Array.Empty<string>()).Count);
    }

    [Fact]
    public void ShippedPromptsAreComplete()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "knowledge", "prompts"))) root = root.Parent;
        Assert.NotNull(root);
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(root!.FullName, "knowledge");
        var items = Prompts.Active(null, Array.Empty<string>());
        Assert.True(items.Count >= 5, "prompts: " + items.Count);
        Assert.All(items, p => { Assert.False(string.IsNullOrEmpty(p.Description)); Assert.False(string.IsNullOrEmpty(p.Category)); });
    }
}
