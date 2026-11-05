// Tests for skills that need a plugin (PluginRequirements.cs): versions, what is missing, the effect on the catalog and on
// installing from a source.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class PluginRequirementsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-pr-" + Guid.NewGuid().ToString("N"));
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");
    string Sln => Path.Combine(dir, "MachineX");

    public PluginRequirementsTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Skill(string root, string name, string requires)
    {
        var d = Path.Combine(root, "skills", name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), $"---\nname: {name}\ndescription: Test skill.\nmetadata:\n  tcagent-requires-plugin: {requires}\n---\nBody.\n");
    }

    void PluginFolder(string id, string version)
    {
        var d = Path.Combine(dir, "user", "plugins", id);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "plugin.json"), $"{{\"id\":\"{id}\",\"name\":\"{id}\",\"version\":\"{version}\",\"command\":\"x.exe\"}}");
    }

    static Plugins.Plugin P(string id, string version, string status = "ready") => new() { Id = id, Version = version, Status = status };

    [Theory]
    [InlineData("1.2.0", ">=1.2", true)]
    [InlineData("1.1.9", ">=1.2", false)]
    [InlineData("1.10.0", ">=1.9", true)]
    [InlineData("2.0.0", ">=1.2 <2.0", false)]
    [InlineData("1.5", ">=1.2 <2.0", true)]
    [InlineData("1.2", "1.2", true)]
    [InlineData("1.3", "=1.2", false)]
    [InlineData("1.2.0-beta", ">= 1.2", true)]
    [InlineData("0.1.0", "", true)]
    [InlineData(null, ">=1.0", false)]
    [InlineData("1.0", "latest", false)]
    public void Fits(string version, string versions, bool expected) => Assert.Equal(expected, PluginRequirements.Fits(version, versions));

    [Fact]
    public void Parse_reads_several_plugins_with_and_without_versions()
    {
        var needs = PluginRequirements.Parse("line-check >=1.2 <2.0, label-printer");
        Assert.Equal(new PluginRequirements.Need("line-check", ">=1.2 <2.0"), needs[0]);
        Assert.Equal(new PluginRequirements.Need("label-printer", ""), needs[1]);
    }

    [Fact]
    public void Missing_names_what_is_wrong()
    {
        Assert.Null(PluginRequirements.Missing("line-check >=1.0", new[] { P("line-check", "1.2") }));
        Assert.Contains("not installed", PluginRequirements.Missing("line-check", Array.Empty<Plugins.Plugin>()));
        Assert.Contains("installed is 0.9", PluginRequirements.Missing("line-check >=1.0", new[] { P("line-check", "0.9") }));
        Assert.Contains("needs approval", PluginRequirements.Missing("line-check", new[] { P("line-check", "1.0", "needs approval") }));
    }

    [Fact]
    public void Skill_without_its_plugin_is_not_offered_but_named()
    {
        Skill(Path.Combine(dir, "user"), "line-report", "line-check >=1.0");
        var entries = Skills.Entries(Sln, Array.Empty<string>());
        var e = Assert.Single(entries);
        Assert.False(e.Active);
        Assert.Equal(PluginRequirements.Status, e.Status);
        Assert.Contains("line-report: not available, plugin line-check >=1.0 is not installed", Skills.Catalog(entries));
    }

    [Fact]
    public void Every_kind_waits_for_its_plugin()
    {
        var user = Path.Combine(dir, "user");
        var head = "---\ndescription: Test.\nmetadata:\n  tcagent-requires-plugin: line-check\n---\n";
        Directory.CreateDirectory(Path.Combine(user, "workflows", "line-flow"));
        File.WriteAllText(Path.Combine(user, "workflows", "line-flow", "WORKFLOW.md"), head + "1. **check** – check.\n");
        Directory.CreateDirectory(Path.Combine(user, "prompts"));
        File.WriteAllText(Path.Combine(user, "prompts", "line-prompt.md"), head + "Check the line.\n");
        Directory.CreateDirectory(Path.Combine(user, "coding-rules"));
        File.WriteAllText(Path.Combine(user, "coding-rules", "line-rule.md"), head + "A rule.\n");

        var none = Array.Empty<string>();
        Assert.Contains("line-flow: not available", Workflows.Catalog(Workflows.Entries(Sln, none)));
        Assert.Empty(Prompts.Active(Sln, none).Where(p => p.Name == "line-prompt"));
        Assert.Equal(PluginRequirements.Status, KnowledgeLayers.Resolve("coding-rules", Sln, none, replaceByName: true).Single(e => e.Name == "line-rule").Status);

        PluginFolder("line-check", "1.0.0");
        Assert.True(Workflows.Entries(Sln, none).Single().Active);
        Assert.Single(Prompts.Active(Sln, none).Where(p => p.Name == "line-prompt"));
        Assert.True(KnowledgeLayers.Resolve("coding-rules", Sln, none, replaceByName: true).Single(e => e.Name == "line-rule").Active);
    }

    [Fact]
    public void A_rule_of_a_source_is_refused_like_a_skill()
    {
        var repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "coding-rules"));
        File.WriteAllText(Path.Combine(repo, "coding-rules", "line-rule.md"), "---\nmetadata:\n  tcagent-requires-plugin: line-check\n---\nA rule.\n");
        Assert.Contains("is not installed", Sources.RequirementRefusal(Assert.Single(Sources.Scan(repo)), Sln));
    }

    [Fact]
    public void An_item_for_a_newer_TcAgent_waits_and_is_refused()
    {
        var old = KnowledgeLayers.TcAgentVersion;
        KnowledgeLayers.TcAgentVersion = "0.52.0";
        try
        {
            var head = "---\nname: new-tool\ndescription: Test.\nmetadata:\n  tcagent-requires-tcagent: >=0.53\n---\nBody.\n";
            var d = Path.Combine(dir, "user", "skills", "new-tool");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "SKILL.md"), head);
            var e = Assert.Single(Skills.Entries(Sln, Array.Empty<string>()));
            Assert.Equal(PluginRequirements.TcAgentStatus, e.Status);
            Assert.Contains("new-tool: not available, needs TcAgent >=0.53, this is 0.52.0", Skills.Catalog(new[] { e }));

            var repo = Path.Combine(dir, "repo", "skills", "new-tool");
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "SKILL.md"), head);
            Assert.Contains("update TcAgent first", Sources.RequirementRefusal(Assert.Single(Sources.Scan(Path.Combine(dir, "repo"))), Sln));

            KnowledgeLayers.TcAgentVersion = "0.53.1";
            Assert.True(Assert.Single(Skills.Entries(Sln, Array.Empty<string>())).Active);
        }
        finally { KnowledgeLayers.TcAgentVersion = old; }
    }

    [Fact]
    public void Skill_with_a_fitting_plugin_is_offered()
    {
        Skill(Path.Combine(dir, "user"), "line-report", "line-check >=1.0");
        PluginFolder("line-check", "1.2.0");
        Assert.True(Assert.Single(Skills.Entries(Sln, Array.Empty<string>())).Active);
    }

    [Fact]
    public void Skill_with_an_old_plugin_is_refused_by_the_source()
    {
        var repo = Path.Combine(dir, "repo");
        Skill(repo, "line-report", "line-check >=2.0");
        PluginFolder("line-check", "1.2.0");
        var it = Assert.Single(Sources.Scan(repo));
        Assert.Contains("needs plugin line-check >=2.0, installed is 1.2.0", Sources.RequirementRefusal(it, Sln));
    }

    [Fact]
    public void Skill_without_requirement_is_not_refused()
    {
        var repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "skills", "plain"));
        File.WriteAllText(Path.Combine(repo, "skills", "plain", "SKILL.md"), "---\nname: plain\ndescription: Test.\n---\nBody.\n");
        Assert.Null(Sources.RequirementRefusal(Assert.Single(Sources.Scan(repo)), Sln));
    }
}
