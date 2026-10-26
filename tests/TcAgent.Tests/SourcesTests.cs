// Tests for the knowledge sources (Sources.cs): licenses, front matter, install from a local Git repository, lock file
// and status, overlay skills, commit for a pull request. Uses git with local repositories only (no network).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class SourcesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-so-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    string Repo => Path.Combine(dir, "collection");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");

    public SourcesTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        // a skill collection: MIT at the root, one skill with its own source-available license, a lesson
        Write(@"collection\LICENSE", "MIT License\n\nPermission is hereby granted, free of charge, to any person ...");
        Write(@"collection\skills\code-review\SKILL.md", "---\nname: code-review\ndescription: Review a change.\n---\nCheck everything.\n");
        Write(@"collection\skills\code-review\scripts\lint.py", "print('x')");
        Write(@"collection\skills\fancy\SKILL.md", "---\nname: fancy\ndescription: Fancy.\n---\nx\n");
        Write(@"collection\skills\fancy\LICENSE.md", "Business Source License 1.1");
        Write(@"collection\lessons\2026-01-02-axis-not-ready.md", "## Axis not ready\n- id: L1\n- **Solution:** enable first\n");
        Git(Repo, "init -q");
        Commit(Repo, "first");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
    static string Git(string repo, string args) => Sources.Git(repo, "-c user.name=t -c user.email=t@example.com -c core.autocrlf=false " + args);
    static void Commit(string repo, string msg) { Git(repo, "add -A"); Git(repo, $"commit -q -m {msg}"); }

    [Theory]
    [InlineData("MIT", "MIT", "accepted")]
    [InlineData("Apache License\nVersion 2.0, January 2004", "Apache-2.0", "accepted")]
    [InlineData("Redistribution and use in source and binary forms, with or without", "BSD", "accepted")]
    [InlineData("Business Source License 1.1", "source-available", "source-available")]
    [InlineData("GNU GENERAL PUBLIC LICENSE Version 3", "copyleft", "copyleft")]
    [InlineData("", "none", "none")]
    [InlineData("Proprietary-1.0", "Proprietary-1.0", "unknown")]
    public void Licenses(string text, string id, string cls) => Assert.Equal((id, cls), Sources.ClassifyLicense(text));

    [Fact]
    public void MetadataIsAddedOrReplaced()
    {
        var t = Sources.AddMeta("---\nname: a\nmetadata:\n  tcagent-owner: x\n---\nbody\n", new Dictionary<string, string> { ["tcagent-source"] = "s/a" });
        Assert.Equal("---\nname: a\nmetadata:\n  tcagent-owner: x\n  tcagent-source: s/a\n---\nbody\n", t);
        t = Sources.AddMeta(t, new Dictionary<string, string> { ["tcagent-source"] = "s/b" });
        Assert.Single(KnowledgeLayers.ParseFrontMatter(t).Meta, kv => kv.Key == "tcagent-source" && kv.Value == "s/b");
        Assert.StartsWith("---\nmetadata:\n  tcagent-source: s/a\n---\n# Plain", Sources.AddMeta("# Plain\n", new Dictionary<string, string> { ["tcagent-source"] = "s/a" }));
    }

    [Fact]
    public void InstallPinsLicenseAndDetectsChanges()
    {
        var root = KnowledgeLayers.ProjectRoot(Sln)!;
        Sources.AddSource(KnowledgeLayers.UserRoot, "collection", Repo, null);
        var s = Assert.Single(Sources.All(Sln));
        var commit = Sources.Fetch(s);
        var repo = Sources.Checkout(s.Name, s.Url, commit);
        var items = Sources.Scan(repo);
        Assert.Equal(new[] { "lessons/2026-01-02-axis-not-ready", "skills/code-review", "skills/fancy" }, items.Select(i => i.Key).OrderBy(x => x));
        var fancy = items.Single(i => i.Name == "fancy");
        Assert.NotNull(Sources.LicenseRefusal(fancy, accept: true));          // source-available: never
        var review = items.Single(i => i.Name == "code-review");
        Assert.Null(Sources.LicenseRefusal(review, false));
        Assert.Equal(new[] { "scripts/lint.py" }, Sources.Scripts(Sources.FilesOf(repo, review)));

        Sources.Install(root, repo, review, s, commit);
        var skill = Path.Combine(root, "skills", "code-review");
        Assert.True(File.Exists(Path.Combine(skill, "LICENSE.txt")));
        var fm = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(Path.Combine(skill, "SKILL.md")));
        Assert.Equal("MIT", fm.Get("license"));
        Assert.Equal("collection/skills/code-review", fm.Get("source"));
        Assert.Equal("ok", Assert.Single(Sources.Status(root)).State);
        // the skill is found like an own one
        Assert.Contains(Skills.Entries(Sln, Array.Empty<string>()), e => e.Active && e.Name == "code-review" && e.Layer.Level == "project");

        // upstream changes the skill → newer upstream after the next fetch; a local edit → changed locally
        File.AppendAllText(Path.Combine(Repo, @"skills\code-review\SKILL.md"), "Also check the tests.\n");
        Commit(Repo, "second");
        Sources.Fetch(s);
        Assert.Equal("ok, newer upstream", Sources.Status(root).Single().State);
        File.AppendAllText(Path.Combine(skill, "SKILL.md"), "local\n");
        Assert.StartsWith("changed locally", Sources.Status(root).Single().State);
    }

    [Fact]
    public void OverlayShowsTheBaseSkillFirst()
    {
        Write(@"user\skills\code-review\SKILL.md", "---\nname: code-review\ndescription: General review.\n---\nGeneral steps.\n");
        Write(@"MachineX\.tcagent\skills\code-review\SKILL.md", "---\nname: code-review\ndescription: Review with TwinCAT parts.\nmetadata:\n  tcagent-extends: collection/code-review\n---\nCheck the task assignment.\n");
        var entries = Skills.Entries(Sln, Array.Empty<string>());
        var text = Skills.Show(entries.Single(e => e.Active && e.Name == "code-review"), entries);
        Assert.True(text.IndexOf("General steps.") < text.IndexOf("Check the task assignment."));
        Assert.Contains("Additions of code-review (project)", text);
    }

    [Fact]
    public void PromoteToASharedRepositoryIsACommitOnANewBranch()
    {
        var company = Path.Combine(dir, "company");
        Write(@"company\README.md", "company knowledge");
        Git(company, "init -q");
        Commit(company, "init");
        var branch = Git(company, "rev-parse --abbrev-ref HEAD");
        Write(@"MachineX\.tcagent\skills\driver-fb\SKILL.md", "---\nname: driver-fb\ndescription: d\n---\nx\n");
        var msg = Sources.CommitForPullRequest(company, Path.Combine(Sln, @".tcagent\skills\driver-fb"), Path.Combine(company, @"skills\driver-fb"), "Promote skills/driver-fb");
        Assert.Contains("not pushed", msg);
        Assert.Equal(branch, Git(company, "rev-parse --abbrev-ref HEAD"));                  // back on the previous branch
        Assert.False(Directory.Exists(Path.Combine(company, @"skills\driver-fb")));          // the change lives on the new branch only
        Assert.Contains("tcagent/driver-fb-", Git(company, "branch --list tcagent/*"));
    }
}
