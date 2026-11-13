// Tests for newer built-in knowledge (BuiltInKnowledge.cs): versions from the changelog, the offer from the tags of a local
// repository, apply / back, pipelines and pins keep the package, copies notice a changed original. git with local
// repositories only (no network).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / built-in root with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class BuiltInKnowledgeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-bk-" + Guid.NewGuid().ToString("N"));
    string Repo => Path.Combine(dir, "knowledge-repo");
    string Package => Path.Combine(dir, "package");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY"), oldVersion = KnowledgeLayers.TcAgentVersion;

    public BuiltInKnowledgeTests()
    {
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = null;          // the real choice: package or cache
        KnowledgeLayers.TcAgentVersion = "0.55.0";
        BuiltInKnowledge.PackageRootOverride = Package;
        BuiltInKnowledge.RepoUrlOverride = Repo;
        BuiltInKnowledge.PipelineOverride = false;
        BuiltInKnowledge.Reset();
        // the package: knowledge 1.0.0 with one prompt
        Write(@"package\CHANGELOG.md", Log(("1.0.0", "0.50.0", "First.")));
        Write(@"package\prompts\review.md", "---\ndescription: Review\n---\nReview the object.\n");
        // the repository: 1.0.0, 1.1.0 (needs 0.53.0), 2.0.0 (needs a TcAgent that does not exist yet)
        Write(@"knowledge-repo\prompts\review.md", "---\ndescription: Review\n---\nReview the object.\n");
        Write(@"knowledge-repo\CHANGELOG.md", Log(("1.0.0", "0.50.0", "First.")));
        Git(Repo, "init -q"); Commit(Repo, "1.0.0"); Git(Repo, "tag v1.0.0");
        Write(@"knowledge-repo\prompts\review.md", "---\ndescription: Review\n---\nReview the object and its tests.\n");
        Write(@"knowledge-repo\CHANGELOG.md", Log(("1.1.0", "0.53.0", "Review checks the tests."), ("1.0.0", "0.50.0", "First.")));
        Commit(Repo, "1.1.0"); Git(Repo, "tag v1.1.0");
        Write(@"knowledge-repo\CHANGELOG.md", Log(("2.0.0", "9.0.0", "Needs tools that come later."), ("1.1.0", "0.53.0", "Review checks the tests."), ("1.0.0", "0.50.0", "First.")));
        Commit(Repo, "2.0.0"); Git(Repo, "tag v2.0.0");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        KnowledgeLayers.TcAgentVersion = oldVersion;
        BuiltInKnowledge.PackageRootOverride = null;
        BuiltInKnowledge.RepoUrlOverride = null;
        BuiltInKnowledge.PipelineOverride = null;
        BuiltInKnowledge.Reset();
        try { foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
    }

    static string Log(params (string V, string Needs, string Text)[] versions) =>
        "# Changelog\n\n" + string.Concat(versions.Select(v => $"## [{v.V}] - 2026-11-01\n\nNeeds TcAgent {v.Needs} or later.\n\n### Added\n- {v.Text}\n\n"));

    void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
    static string Git(string repo, string args) => Sources.Git(repo, "-c user.name=t -c user.email=t@example.com -c core.autocrlf=false " + args);
    static void Commit(string repo, string msg) { Git(repo, "add -A"); Git(repo, $"commit -q -m v{msg}"); }

    [Fact]
    public void The_version_and_what_it_needs_come_from_the_changelog()
    {
        var i = BuiltInKnowledge.Parse(Log(("1.1.0", "0.53.0", "x"), ("1.0.0", "0.50.0", "y")));
        Assert.Equal(("1.1.0", "0.53.0"), (i.Version, i.NeedsTcAgent));
        Assert.True(BuiltInKnowledge.Fits("0.53.0", "0.55.0"));
        Assert.False(BuiltInKnowledge.Fits("9.0.0", "0.55.0"));
        Assert.True(BuiltInKnowledge.Fits(null, "0.55.0"));
    }

    [Fact]
    public void Without_a_choice_the_package_is_the_built_in_level()
    {
        Assert.Equal(Package, KnowledgeLayers.BuiltInRoot);
        Assert.Equal("1.0.0", BuiltInKnowledge.Describe(Updates.LoadState())["version"]?.ToString());
    }

    [Fact]
    public void The_newest_version_this_TcAgent_can_use_is_offered_with_its_changes()
    {
        var r = BuiltInKnowledge.Check(force: true);
        Assert.Null(r["error"]);
        Assert.Equal("1.1.0", r["latest"]?.ToString());
        Assert.True(r["notify"]!.GetValue<bool>());
        var offer = r["offer"]!;
        Assert.Equal("2.0.0", offer["blocked"]?["version"]?.ToString());          // needs TcAgent 9.0.0
        var notes = offer["releases"]!.AsArray();
        Assert.Equal("1.1.0", Assert.Single(notes)!["version"]?.ToString());
        Assert.Contains("Review checks the tests", notes[0]!["notes"]!.ToString());

        BuiltInKnowledge.Skip("1.1.0");
        Assert.False(BuiltInKnowledge.Describe(Updates.LoadState())["notify"]!.GetValue<bool>());
    }

    [Fact]
    public void Apply_makes_the_version_the_built_in_level_and_back_returns_to_the_package()
    {
        BuiltInKnowledge.Check(force: true);
        var r = BuiltInKnowledge.Apply("1.1.0");
        Assert.Equal("1.1.0", r["version"]?.ToString());
        Assert.Equal("cache", r["why"]?.ToString());
        Assert.Equal("package", r["previous"]?.ToString());
        Assert.Equal(BuiltInKnowledge.Folder("1.1.0"), KnowledgeLayers.BuiltInRoot);
        Assert.Contains("and its tests", File.ReadAllText(Path.Combine(KnowledgeLayers.BuiltInRoot, "prompts", "review.md")));
        Assert.False(BuiltInKnowledge.Describe(Updates.LoadState())["available"]!.GetValue<bool>());     // nothing newer that fits

        // a pipeline keeps the package, also after the user took a version over
        BuiltInKnowledge.PipelineOverride = true;
        Assert.Equal(Package, BuiltInKnowledge.ActiveRoot());
        Assert.Throws<InvalidOperationException>(() => BuiltInKnowledge.Apply("1.1.0"));
        BuiltInKnowledge.PipelineOverride = false;

        // a version that needs a newer TcAgent is refused
        Assert.ThrowsAny<Exception>(() => BuiltInKnowledge.Apply("2.0.0"));
        Assert.Equal(BuiltInKnowledge.Folder("1.1.0"), BuiltInKnowledge.ActiveRoot());

        var back = BuiltInKnowledge.Back();
        Assert.Equal("package", back["why"]?.ToString());
        Assert.Equal(Package, KnowledgeLayers.BuiltInRoot);
    }

    [Fact]
    public void A_package_as_new_as_the_cache_wins_and_an_older_TcAgent_falls_back()
    {
        BuiltInKnowledge.Check(force: true);
        BuiltInKnowledge.Apply("1.1.0");
        KnowledgeLayers.TcAgentVersion = "0.52.0";            // 1.1.0 needs 0.53.0
        BuiltInKnowledge.Reset();
        Assert.Equal(Package, BuiltInKnowledge.ActiveRoot());
        KnowledgeLayers.TcAgentVersion = "0.55.0";
        Write(@"package\CHANGELOG.md", Log(("1.1.0", "0.53.0", "In the package now.")));     // a newer TcAgent brought it along
        BuiltInKnowledge.Reset();
        Assert.Equal(Package, BuiltInKnowledge.ActiveRoot());
    }

    [Fact]
    public void The_company_can_switch_it_off_or_pin_the_package()
    {
        BuiltInKnowledge.Check(force: true);
        BuiltInKnowledge.Apply("1.1.0");
        Write(@"company\updates.json", """{ "builtinVersion": "package" }""");
        BuiltInKnowledge.Reset();
        Assert.Equal(Package, BuiltInKnowledge.ActiveRoot());
        Assert.Throws<InvalidOperationException>(() => BuiltInKnowledge.Apply("1.1.0"));
        Write(@"company\updates.json", """{ "builtinUpdates": false }""");
        BuiltInKnowledge.Reset();
        Assert.Equal(Package, BuiltInKnowledge.ActiveRoot());
        Assert.Equal("company", BuiltInKnowledge.Describe(Updates.LoadState())["lockedBy"]?.ToString());
    }

    [Fact]
    public void A_copy_of_a_built_in_file_notices_when_the_original_changed()
    {
        var copy = KnowledgeOps.AddExisting(null, "prompts", Path.Combine(Package, "prompts", "review.md"), "user");
        var fm = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(copy));
        Assert.Equal("built-in", fm.Get("copy-of"));
        KnowledgeLayers.Entry Mine() => Prompts(null).Single(e => e.Layer.Level == "user" && e.Name == "review");
        Assert.False(KnowledgeOps.OriginalChanged(Mine(), Prompts(null)));
        // a newer built-in knowledge changes the original
        BuiltInKnowledge.Check(force: true);
        BuiltInKnowledge.Apply("1.1.0");
        Assert.True(KnowledgeOps.OriginalChanged(Mine(), Prompts(null)));
    }

    static List<KnowledgeLayers.Entry> Prompts(string sln) => TcAgent.Prompts.Entries(sln, Array.Empty<string>());
}
