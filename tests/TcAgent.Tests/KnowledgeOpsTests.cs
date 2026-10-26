// Tests for the changes of the knowledge manager window (KnowledgeOps.cs): new from a template, move between levels,
// delete on own levels only, Git state of a level.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class KnowledgeOpsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-ko-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY"), oldTeam = Environment.GetEnvironmentVariable("TCAGENT_TEAM");

    public KnowledgeOpsTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", Path.Combine(dir, "team"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Directory.CreateDirectory(Path.Combine(dir, "team"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", oldTeam);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void NewFilesComeFromTemplatesAndAreFoundAtOnce()
    {
        var skill = KnowledgeOps.New(Sln, "skills", "valve-commissioning", "project");
        Assert.EndsWith(@".tcagent\skills\valve-commissioning\SKILL.md", skill);
        Assert.Contains(Skills.Entries(Sln, Array.Empty<string>()), e => e.Name == "valve-commissioning" && e.Active);
        var wf = KnowledgeOps.New(Sln, "workflows", "bug-fix", "user");
        Assert.Contains(Workflows.ParseSteps(KnowledgeLayers.ParseFrontMatter(File.ReadAllText(wf)).Body), s => s.Requires == "build-ok");
        Assert.EndsWith(@"coding-rules\step-sequences.md", KnowledgeOps.New(Sln, "coding-rules", "Step sequences", "user"));
        Assert.EndsWith(@"skills\valve-test\SKILL.md", KnowledgeOps.New(Sln, "skills", "Valve Test", "user"));   // the name is made valid
        Assert.Throws<ArgumentException>(() => KnowledgeOps.New(Sln, "skills", "x", "company"));        // shared: create below and move up
    }

    [Fact]
    public void MoveChecksNamesAndDeleteOnlyOwnLevels()
    {
        var rule = KnowledgeOps.New(Sln, "coding-rules", "naming", "project");
        File.AppendAllText(rule, "- Instances of MachineX start with fb\n");
        Assert.Throws<ArgumentException>(() => KnowledgeOps.Move(Sln, rule, "team", Array.Empty<string>()));   // names the solution
        File.WriteAllText(rule, "- Instances start with fb\n");
        var msg = KnowledgeOps.Move(Sln, rule, "team", Array.Empty<string>());
        Assert.StartsWith("Moved", msg);
        var moved = Path.Combine(dir, @"team\coding-rules\naming.md");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(rule));
        // built-in files are copied (and replaced by name), never deleted
        Directory.CreateDirectory(Path.Combine(dir, @"builtin\skills\plc-debugging"));
        File.WriteAllText(Path.Combine(dir, @"builtin\skills\plc-debugging\SKILL.md"), "---\nname: plc-debugging\ndescription: d\n---\nx\n");
        Assert.StartsWith("Copied", KnowledgeOps.Move(Sln, Path.Combine(dir, @"builtin\skills\plc-debugging\SKILL.md"), "user", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => KnowledgeOps.Delete(Sln, Path.Combine(dir, @"builtin\skills\plc-debugging\SKILL.md"), Array.Empty<string>()));
        Assert.StartsWith("Deleted", KnowledgeOps.Delete(Sln, Path.Combine(dir, @"user\skills\plc-debugging\SKILL.md"), Array.Empty<string>()));
        Assert.False(Directory.Exists(Path.Combine(dir, @"user\skills\plc-debugging")));
    }

    [Fact]
    public void GitStateShowsLocalChangesOfTheLevel()
    {
        var team = Path.Combine(dir, "team");
        Sources.Git(team, "init -q");
        Sources.Git(team, "-c user.name=t -c user.email=t@example.com commit -q --allow-empty -m init");
        Assert.Throws<ArgumentException>(() => KnowledgeOps.New(Sln, "skills", "x", "team"));        // a team in Git: by pull request
        Directory.CreateDirectory(Path.Combine(team, "coding-rules"));
        File.WriteAllText(Path.Combine(team, @"coding-rules\layout.md"), "x");
        var st = KnowledgeOps.GitState(team)!;
        Assert.Equal("new", (string)st["changes"]![Path.Combine(team, @"coding-rules\layout.md")]!);
        Assert.False((bool)st["upstream"]!);
    }
}
