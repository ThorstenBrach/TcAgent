// Tests for the skills (Skills.cs): Agent Skills names, levels, adding, mirroring into the agent CLI folders.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class SkillsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-sk-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");

    public SkillsTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Skill(@"builtin\skills\plc-debugging", "plc-debugging", "Find the cause first.");
        Skill(@"company\skills\code-review", "code-review", "Company review.");
        Skill(@"company\platforms\framework-a\skills\driver-fb", "driver-fb", "Driver of framework A.");
        Skill(@"MachineX\.tcagent\skills\driver-fb", "driver-fb", "Project driver.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Skill(string rel, string name, string desc)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.Combine(p, "references"));
        File.WriteAllText(Path.Combine(p, "SKILL.md"), $"---\nname: {name}\ndescription: {desc}\n---\nSteps of {name}\n");
        File.WriteAllText(Path.Combine(p, "references", "checklist.md"), "check");
    }

    [Theory]
    [InlineData("plc-code-review")]
    [InlineData("a")]
    [InlineData("tcunit-tdd-2")]
    public void ValidNames(string n) => Assert.Equal(n, Skills.ValidateName(n));

    [Theory]
    [InlineData("PLC-Review")]
    [InlineData("-x")]
    [InlineData("x--y")]
    [InlineData("x_y")]
    [InlineData("")]
    public void InvalidNames(string n) => Assert.Throws<ArgumentException>(() => Skills.ValidateName(n));

    [Fact]
    public void LevelsAndPlatformResolveByName()
    {
        var e = Skills.Entries(Sln, new[] { "framework-a" });
        Assert.Equal(new[] { "code-review", "driver-fb", "plc-debugging" }, e.Where(x => x.Active).Select(x => x.Name).OrderBy(x => x));
        Assert.Equal("project", e.Single(x => x.Name == "driver-fb" && x.Active).Layer.Level);
        Assert.Equal("replaced", e.Single(x => x.Name == "driver-fb" && x.Layer.Platform == "framework-a").Status);
        var catalog = Skills.Catalog(e)!;
        Assert.Contains("plc-debugging (built-in): Find the cause first.", catalog);
        Assert.Contains("references", Skills.Show(e.Single(x => x.Name == "code-review")));
    }

    [Fact]
    public void AddWritesTheAgentSkillsFormat()
    {
        var file = Skills.Add(Sln, "personal", "my-check", "My check. Use when x.", "1. do it", "a method", write: true);
        Assert.Equal(Path.Combine(dir, "user", "skills", "my-check", "SKILL.md"), file);
        var fm = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(file));
        Assert.Equal("my-check", fm.Top["name"]);
        Assert.Equal("a method", fm.Get("inspired-by"));
        Assert.Throws<ArgumentException>(() => Skills.Add(Sln, "personal", "Bad Name", "d", "b", null, false));
        Assert.Throws<ArgumentException>(() => Skills.Add(Sln, "company", "x", "d", "b", null, false));
    }

    [Fact]
    public void MirrorCopiesGeneralSkillsAndKeepsForeignFolders()
    {
        var client = Path.Combine(dir, "home", ".claude");
        var target = Path.Combine(client, "skills");
        Directory.CreateDirectory(Path.Combine(target, "code-review"));            // the user's own skill with the same name
        File.WriteAllText(Path.Combine(target, "code-review", "SKILL.md"), "mine");
        var report = Skills.Mirror(Skills.Entries(Sln, new[] { "framework-a" }), new[] { target, Path.Combine(dir, "home", ".none", "skills") });
        Assert.True(File.Exists(Path.Combine(target, "plc-debugging", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(target, "plc-debugging", "references", "checklist.md")));
        Assert.False(Directory.Exists(Path.Combine(target, "driver-fb")));          // platform / project skills are not mirrored
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "code-review", "SKILL.md")));
        Assert.Contains(report, r => r.StartsWith("skipped") && r.Contains(".none"));
        // a second run copies nothing; an edited copy is restored from the source
        Assert.Contains("unchanged plc-debugging", Skills.Mirror(Skills.Entries(Sln, new[] { "framework-a" }), new[] { target }));
        File.WriteAllText(Path.Combine(target, "plc-debugging", "SKILL.md"), "edited in the copy");
        Assert.Contains(Skills.Mirror(Skills.Entries(Sln, new[] { "framework-a" }), new[] { target }), r => r.StartsWith("plc-debugging →"));
        Assert.StartsWith("---", File.ReadAllText(Path.Combine(target, "plc-debugging", "SKILL.md")));
        // a skill that no longer applies is removed from the mirror
        KnowledgeLayers.SetDisabled(Sln, "user", "skills/plc-debugging", true);
        Skills.Mirror(Skills.Entries(Sln, new[] { "framework-a" }), new[] { target });
        Assert.False(Directory.Exists(Path.Combine(target, "plc-debugging")));
    }
}
