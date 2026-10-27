// Tests for local changes of installed items (SourcesMerge.cs): the diff against the installed version, the three-way
// merge of a newer version of the source, conflicts, and the proposal as a commit on a branch of the source.
// Uses git with local repositories only (no network).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class SourcesMergeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-sm-" + Guid.NewGuid().ToString("N"));
    string Repo => Path.Combine(dir, "collection");
    string Root => Path.Combine(dir, "user");
    const string Key = "skills/review";
    const string Skill = "---\nname: review\ndescription: Review a change.\n---\n# Review\n\nStep one.\n\nStep two.\n\nStep three.\n";

    public SourcesMergeTests()
    {
        KnowledgeLayers.UserRootOverride = Root;
        Write(@"collection\LICENSE", "MIT License\n\nPermission is hereby granted, free of charge, to any person ...");
        Write(@"collection\skills\review\SKILL.md", Skill);
        Git(Repo, "init -q");
        Commit(Repo, "first");
        Sources.AddSource(Root, "collection", Repo, null);
        var s = Sources.Load(Root).Single();
        var commit = Sources.Fetch(s);
        var repo = Sources.Checkout(s.Name, s.Url, commit);
        Sources.Install(Root, repo, Sources.Scan(repo).Single(), s, commit);
    }

    public void Dispose()
    {
        KnowledgeLayers.UserRootOverride = null;
        try { foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
    static string Git(string repo, string args) => Sources.Git(repo, "-c user.name=t -c user.email=t@example.com -c core.autocrlf=false " + args);
    static void Commit(string repo, string msg) { Git(repo, "add -A"); Git(repo, $"commit -q -m {msg}"); }
    Sources.LockEntry Lock => Sources.LoadLock(Root)[Key];
    Sources.Source Src => Sources.Load(Root).Single();
    string Local => Path.Combine(Root, "skills", "review", "SKILL.md");
    void EditLocal(string from, string to) => File.WriteAllText(Local, File.ReadAllText(Local).Replace(from, to));

    [Fact]
    public void LocalDiffShowsOnlyYourChange()
    {
        Assert.Equal("No local changes.", SourcesMerge.LocalDiff(Root, Key, Lock, Src));
        EditLocal("Step one.", "Step one - with TwinCAT.");
        var d = SourcesMerge.LocalDiff(Root, Key, Lock, Src);
        Assert.Contains("-Step one.", d);
        Assert.Contains("+Step one - with TwinCAT.", d);
        Assert.DoesNotContain("tcagent-commit", d);
    }

    [Fact]
    public void MergeKeepsYourChangeAndTakesTheSources()
    {
        EditLocal("Step one.", "Step one - with TwinCAT.");
        Write(@"collection\skills\review\SKILL.md", Skill.Replace("Step three.", "Step three, improved."));
        Commit(Repo, "second");
        var up = Sources.Fetch(Src);
        var preview = SourcesMerge.Merge(Root, Key, Lock, Src, up, false, dryRun: true);
        Assert.Empty(preview.Conflicts);
        Assert.DoesNotContain("improved", File.ReadAllText(Local));                                     // the preview changes nothing

        var r = SourcesMerge.Merge(Root, Key, Lock, Src, up, false, dryRun: false);
        Assert.Empty(r.Conflicts);
        var t = File.ReadAllText(Local);
        Assert.Contains("Step one - with TwinCAT.", t);
        Assert.Contains("Step three, improved.", t);
        Assert.Contains("tcagent-commit: " + up[..12], t);
        Assert.Equal(up, Lock.Commit);
        Assert.True(Directory.Exists(Path.GetDirectoryName(r.Backup)));
        Assert.StartsWith("changed locally", Sources.Status(Root).Single().State);   // your change is still yours
    }

    [Fact]
    public void MergeMarksConflicts()
    {
        EditLocal("Step two.", "Step two, my way.");
        Write(@"collection\skills\review\SKILL.md", Skill.Replace("Step two.", "Step two, their way."));
        Commit(Repo, "second");
        var r = SourcesMerge.Merge(Root, Key, Lock, Src, Sources.Fetch(Src), false, dryRun: false);
        Assert.Single(r.Conflicts);
        var t = File.ReadAllText(Local);
        Assert.Contains("<<<<<<< yours", t);
        Assert.Contains("Step two, my way.", t);
        Assert.Contains("Step two, their way.", t);
    }

    [Fact]
    public void ProposeCommitsOnlyYourChangeAndPushes()
    {
        EditLocal("Step two.", "Step two, better.");
        var r = SourcesMerge.Propose(Root, Key, Lock, Src, "review: clearer step two", new string[0]);
        Assert.StartsWith("tcagent/", r.Branch);
        var file = File.ReadAllText(Path.Combine(r.Folder, "skills", "review", "SKILL.md")).Replace("\r\n", "\n");
        Assert.Equal(Skill.Replace("Step two.", "Step two, better."), file);           // no tcagent-* metadata, no copied license
        Assert.Equal("review: clearer step two", Git(r.Folder, "log -1 --format=%s"));
        Assert.Contains("Step two, better.", File.ReadAllText(r.Patch));
        Assert.Null(r.CompareUrl);                                                          // not a GitHub repository

        var fork = Path.Combine(dir, "fork.git");
        Sources.Git(null, $"clone -q --bare \"{Repo}\" \"{fork}\"");
        SourcesMerge.Push(Src, r.Branch, fork);
        Assert.Equal("review: clearer step two", Git(fork, $"log -1 --format=%s {r.Branch}"));
    }

    [Fact]
    public void ProposeRefusesWithoutChanges() =>
        Assert.Throws<InvalidOperationException>(() => SourcesMerge.Propose(Root, Key, Lock, Src, null, new string[0]));
}
