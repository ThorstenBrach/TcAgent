// Tests for the knowledge layers (KnowledgeLayers.cs): levels × platforms, detection, replace / locked / force / disabled,
// front matter, splitting a former single file.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

public sealed class KnowledgeLayersTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-kl-" + Guid.NewGuid().ToString("N"));
    string Company => Path.Combine(dir, "company");
    string User => Path.Combine(dir, "user");
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");

    public KnowledgeLayersTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Company);
        KnowledgeLayers.UserRootOverride = User;
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Write(@"company\platforms\framework-a\platform.md", "---\nname: framework-a\ndescription: Automation framework A\nmetadata:\n  tcagent-detect-libraries: \"FwA_Base*, FwA_Motion\"\n---\nOverview\n");
        Write(@"company\platforms\framework-b\platform.md", "---\nname: framework-b\nmetadata:\n  tcagent-detect-libraries: FwB_*\n---\n");
        Write(@"MachineX\PLC\PLC.plcproj", "<Project><ItemGroup><PlaceholderReference Include=\"FwA_Base\"><DefaultResolution>FwA_Base, *</DefaultResolution></PlaceholderReference>" +
                                           "<LibraryReference Include=\"Tc2_Standard,3.4,Beckhoff\" /></ItemGroup></Project>");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    static string Locked(string body) => "---\nname: x\ndescription: d\nmetadata:\n  tcagent-locked: \"true\"\n---\n" + body;
    static string Forced(string body) => "---\nname: x\ndescription: d\nmetadata:\n  tcagent-force: \"true\"\n---\n" + body;

    [Fact]
    public void PlatformIsDetectedFromTheLibraries()
    {
        var pi = KnowledgeLayers.Platforms(Sln);
        Assert.Equal(new[] { "framework-a" }, pi.Active);
        Assert.Contains("FwA_Base", pi.Reason);
        Assert.Contains("FwA_Base", KnowledgeLayers.ReferencedLibraries(Sln));
        Assert.Contains("Tc2_Standard", KnowledgeLayers.ReferencedLibraries(Sln));
    }

    [Fact]
    public void AmbiguousPlatformsAreReportedAndConfigWins()
    {
        Write(@"MachineX\PLC2\PLC2.plcproj", "<Project><ItemGroup><PlaceholderReference Include=\"FwB_Core\" /></ItemGroup></Project>");
        var pi = KnowledgeLayers.Platforms(Sln);
        Assert.Empty(pi.Active);
        Assert.Equal(new[] { "framework-a", "framework-b" }, pi.Ambiguous.OrderBy(x => x));
        Assert.Contains("tc_ask_user", KnowledgeLayers.PlatformNote(pi));
        KnowledgeLayers.SetPlatforms(Sln, new[] { "framework-b" });
        pi = KnowledgeLayers.Platforms(Sln);
        Assert.True(pi.Configured);
        Assert.Equal(new[] { "framework-b" }, pi.Active);
    }

    [Fact]
    public void LaterLevelReplacesLockedRefusesForceKeeps()
    {
        Write(@"company\coding-rules\naming.md", "company naming");
        Write(@"company\coding-rules\comments.md", "company comments");
        Write(@"company\platforms\framework-a\coding-rules\naming.md", Locked("platform naming"));
        Write(@"company\platforms\framework-a\coding-rules\base-classes.md", "platform base classes");
        Write(@"company\platforms\framework-a\coding-rules\short-names.md", "platform short names");
        Write(@"user\coding-rules\short-names.md", Forced("my short names"));
        Write(@"MachineX\.tcagent\coding-rules\naming.md", "project naming");
        Write(@"MachineX\.tcagent\coding-rules\base-classes.md", "project base classes");
        var e = KnowledgeLayers.Resolve("coding-rules", Sln, new[] { "framework-a" }, replaceByName: true);
        string Status(string name, string level, string platform = null) =>
            e.Single(x => x.Name == name && x.Layer.Level == level && x.Layer.Platform == platform).Status;
        Assert.Equal("replaced", Status("naming", "company"));
        Assert.Equal("active", Status("naming", "company", "framework-a"));
        Assert.Equal("refused", Status("naming", "project"));
        Assert.Equal("replaced", Status("base-classes", "company", "framework-a"));
        Assert.Equal("active", Status("base-classes", "project"));
        Assert.Equal("forced", Status("short-names", "user"));
        Assert.Equal("replaced", Status("short-names", "company", "framework-a"));
        Assert.Equal("active", Status("comments", "company"));
    }

    [Fact]
    public void PlatformFilesOnlyApplyToTheirPlatform()
    {
        Write(@"company\platforms\framework-b\coding-rules\b-only.md", "b");
        var e = KnowledgeLayers.Resolve("coding-rules", Sln, KnowledgeLayers.Platforms(Sln).Active, replaceByName: true);
        Assert.DoesNotContain(e, x => x.Name == "b-only");
    }

    [Fact]
    public void DisabledFilesAreSwitchedOffButNotLockedOnes()
    {
        Write(@"company\coding-rules\comments.md", "company comments");
        Write(@"company\platforms\framework-a\coding-rules\naming.md", Locked("platform naming"));
        KnowledgeLayers.SetDisabled(Sln, "project", "coding-rules/comments", true);
        KnowledgeLayers.SetDisabled(Sln, "user", "coding-rules/naming", true);
        var e = KnowledgeLayers.Resolve("coding-rules", Sln, new[] { "framework-a" }, replaceByName: true);
        Assert.Equal("disabled", e.Single(x => x.Name == "comments").Status);
        var naming = e.Single(x => x.Name == "naming");
        Assert.True(naming.Active);
        Assert.Contains("locked", naming.Reason);
        KnowledgeLayers.SetDisabled(Sln, "project", "coding-rules/comments", false);
        Assert.True(KnowledgeLayers.Resolve("coding-rules", Sln, null, true).Single(x => x.Name == "comments").Active);
    }

    [Fact]
    public void FormerSingleFilesAndEnvironmentPathsAreRead()
    {
        Write(@"user\coding-rules.md", "# Coding rules\n- old personal rule");
        Write(@"env\team-rules.md", "team rule");
        var e = KnowledgeLayers.Resolve("coding-rules", Sln, null, true, new[] { Path.Combine(dir, "env") });
        Assert.Contains(e, x => x.Name == "coding-rules" && x.Layer.Level == "user" && x.Active);
        Assert.Contains(e, x => x.Name == "team-rules" && x.Layer.Level == "company" && x.Active);
    }

    [Fact]
    public void LessonsAreCollectedNotReplaced()
    {
        Write(@"company\lessons\a.md", "## A\n- **Solution:** x");
        Write(@"user\lessons\a.md", "## A2\n- **Solution:** y");
        Write(@"MachineX\.tcagent\lessons.md", "## B\n- **Solution:** z");
        var e = KnowledgeLayers.Resolve("lessons", Sln, null, replaceByName: false);
        Assert.Equal(3, e.Count(x => x.Active));
    }

    [Fact]
    public void FrontMatterOfTheAgentSkillsFormat()
    {
        var fm = KnowledgeLayers.ParseFrontMatter("---\nname: driver-fb\ndescription: Create a driver FB.\n  Use when asked.\nmetadata:\n  tcagent-locked: \"true\"\n  tcagent-owner: team a\n---\nBody line\n");
        Assert.Equal("driver-fb", fm.Top["name"]);
        Assert.Equal("Create a driver FB. Use when asked.", fm.Top["description"]);
        Assert.True(fm.Flag("locked"));
        Assert.Equal("team a", fm.Get("owner"));
        Assert.Equal("Body line\n", fm.Body);
        Assert.Equal("plain", KnowledgeLayers.ParseFrontMatter("plain").Body);
    }

    [Fact]
    public void SplitMakesOneFilePerTopic()
    {
        Write(@"MachineX\.tcagent\coding-rules.md", "# Coding rules\n\n## Names\n- a\n\n## Comments\n- b\n\n## Names\n- c\n");
        var single = Path.Combine(Sln, ".tcagent", "coding-rules.md");
        var plan = KnowledgeLayers.Split(single, write: false);
        Assert.Equal(new[] { "names.md", "comments.md", "names-2.md" }, plan.Select(p => Path.GetFileName(p.File)));
        Assert.True(File.Exists(single));
        KnowledgeLayers.Split(single, write: true);
        Assert.False(File.Exists(single));
        Assert.Contains("- a", File.ReadAllText(Path.Combine(Sln, ".tcagent", "coding-rules", "names.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(Sln, ".tcagent"), "coding-rules.md.split-*"));
    }

    [Fact]
    public void UserPlatformFolderAppliesForItsPlatform()
    {
        Write(@"user\platforms\framework-a\lessons\x.md", "## X\n- **Solution:** s");
        var layers = KnowledgeLayers.Layers(Sln, new[] { "framework-a" });
        Assert.Contains(layers, l => l.Level == "user" && l.Platform == "framework-a");
        Assert.True(layers.Single(l => l.Level == "user" && l.Platform == "framework-a").Rank >
                    layers.Single(l => l.Level == "company" && l.Platform == "framework-a").Rank);
        Assert.Contains(KnowledgeLayers.Resolve("lessons", Sln, new[] { "framework-a" }, false), x => x.Name == "x" && x.Active);
    }
}
