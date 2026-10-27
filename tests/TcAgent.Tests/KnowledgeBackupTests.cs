// Tests for the backup of the knowledge levels (KnowledgeBackup.cs): what belongs to a level, only-when-changed,
// the last 20, restore of the whole level / one file / one skill, and Git working copies (only uncommitted changes).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class KnowledgeBackupTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-kb-" + Guid.NewGuid().ToString("N"));
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY"), oldTeam = Environment.GetEnvironmentVariable("TCAGENT_TEAM");
    string User => Path.Combine(dir, "user");
    string Sln => Path.Combine(dir, "MachineX");
    KnowledgeBackup.Level UserLevel => KnowledgeBackup.Find(Sln, "user");

    public KnowledgeBackupTests()
    {
        KnowledgeLayers.UserRootOverride = User;
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", null);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", null);
        Directory.CreateDirectory(Sln);
        Write(@"user\coding-rules\naming.md", "# Naming\nPrefix fb.\n");
        Write(@"user\skills\review\SKILL.md", "---\nname: review\n---\nStep one.\n");
        Write(@"user\knowledge.md", "- https://example.com/manuals/\n");
        Write(@"user\runs\x.jsonl", "not knowledge");                 // run logs, caches and logs are not part of the knowledge
    }

    public void Dispose()
    {
        KnowledgeLayers.UserRootOverride = null; KnowledgeLayers.BuiltInRootOverride = null;
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", oldTeam);
        try { foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
    string ReadFile(string rel) => File.ReadAllText(Path.Combine(dir, rel));

    [Fact]
    public void BacksUpOnlyTheKnowledgeAndOnlyWhenChanged()
    {
        Assert.Contains(KnowledgeBackup.Levels(Sln), l => l.Name == "user");
        var z = KnowledgeBackup.Backup(UserLevel, "test");
        Assert.NotNull(z);
        var info = KnowledgeBackup.Read(z!);
        Assert.Equal(new[] { "coding-rules/naming.md", "knowledge.md", "skills/review/SKILL.md" }, info.Files.Select(f => f.Path));
        Assert.Equal(64, info.Files[0].Sha256.Length);
        Assert.Null(KnowledgeBackup.Backup(UserLevel, "again"));               // nothing changed: the newest backup is the current state
        Write(@"user\coding-rules\naming.md", "# Naming\nPrefix fb_.\n");
        Assert.NotNull(KnowledgeBackup.Backup(UserLevel, "changed"));
        Assert.Equal(2, KnowledgeBackup.List(UserLevel).Count);
        Assert.Empty(KnowledgeBackup.BeforeWrite(Sln, "x"));                    // unchanged again: nothing new
    }

    [Fact]
    public void RenameAFileAndASkill()
    {
        KnowledgeOps.Rename(Sln, Path.Combine(User, @"skills\review\SKILL.md"), "Code Review", new string[0]);
        Assert.True(File.Exists(Path.Combine(User, @"skills\code-review\SKILL.md")));
        Assert.Contains("name: code-review", ReadFile(@"user\skills\code-review\SKILL.md"));
        KnowledgeOps.Rename(Sln, Path.Combine(User, @"coding-rules\naming.md"), "names", new string[0]);
        Assert.True(File.Exists(Path.Combine(User, @"coding-rules\names.md")));
        Assert.Throws<ArgumentException>(() => KnowledgeOps.Rename(Sln, Path.Combine(User, @"coding-rules\names.md"), "names", new string[0]));
    }

    [Fact]
    public void KeepsTheLast20()
    {
        for (int i = 0; i < 23; i++) { Write(@"user\lessons\l.md", "v" + i); KnowledgeBackup.Backup(UserLevel, "n" + i); }
        var all = KnowledgeBackup.List(UserLevel);
        Assert.Equal(KnowledgeBackup.Keep, all.Count);
        Assert.Equal("n22", all[0].Reason);
    }

    [Fact]
    public void RestoresTheWholeLevelOneFileOrOneSkill()
    {
        var z = KnowledgeBackup.Backup(UserLevel, "good state")!;
        Write(@"user\coding-rules\naming.md", "broken\n");
        Write(@"user\skills\review\SKILL.md", "---\nname: review\n---\nStep one, changed.\n");
        Write(@"user\skills\review\extra.md", "later");
        File.Delete(Path.Combine(User, "knowledge.md"));
        Write(@"user\lessons\new.md", "came later");

        // one file
        var plan = KnowledgeBackup.Plan(UserLevel, z, "coding-rules/naming.md");
        var c = Assert.Single(plan);
        Assert.Equal("changed", c.Kind);
        Assert.Contains("-broken", c.Diff);
        KnowledgeBackup.Restore(UserLevel, z, "coding-rules/naming.md");
        Assert.Equal("# Naming\nPrefix fb.\n", ReadFile(@"user\coding-rules\naming.md"));
        Assert.Contains("Step one, changed.", ReadFile(@"user\skills\review\SKILL.md"));   // the rest stays

        // one skill: its files as in the backup, a file added later goes
        Assert.Equal(new[] { "removed skills/review/extra.md", "changed skills/review/SKILL.md" },
                     KnowledgeBackup.Plan(UserLevel, z, "skills/review").Select(x => x.Kind + " " + x.Path).OrderByDescending(x => x));
        KnowledgeBackup.Restore(UserLevel, z, "skills/review");
        Assert.False(File.Exists(Path.Combine(User, @"skills\review\extra.md")));

        // the whole level; the state before is backed up first
        var (changes, safety) = KnowledgeBackup.Restore(UserLevel, z, null);
        Assert.Equal(new[] { "knowledge.md:restored", "lessons/new.md:removed" }, changes.Select(x => x.Path + ":" + x.Kind).OrderBy(x => x));
        Assert.Contains("lessons/new.md", KnowledgeBackup.Read(safety!).Files.Select(f => f.Path));
        Assert.True(File.Exists(Path.Combine(User, "knowledge.md")));
        Assert.Empty(KnowledgeBackup.Plan(UserLevel, z, null));
        Assert.Throws<ArgumentException>(() => KnowledgeBackup.Plan(UserLevel, z, "skills/unknown"));
    }

    [Fact]
    public void GitLevelOnlyUncommitted()
    {
        var project = Path.Combine(Sln, ".tcagent");
        Write(@"MachineX\.tcagent\coding-rules\style.md", "committed");
        Sources.Git(Sln, "init -q");
        Sources.Git(Sln, "-c user.name=t -c user.email=t@example.com add -A");
        Sources.Git(Sln, "-c user.name=t -c user.email=t@example.com commit -q -m first");
        var level = KnowledgeBackup.Find(Sln, "project");
        Assert.Equal(project, level.Root, ignoreCase: true);
        Assert.Null(KnowledgeBackup.Backup(level, "clean"));                   // everything committed: Git is the backup
        Write(@"MachineX\.tcagent\coding-rules\style.md", "changed, not committed");
        Write(@"MachineX\.tcagent\lessons\new.md", "new");
        var info = KnowledgeBackup.Read(KnowledgeBackup.Backup(level, "dirty")!);
        Assert.True(info.Git);
        Assert.Equal(new[] { "coding-rules/style.md", "lessons/new.md" }, info.Files.Select(f => f.Path));
    }
}
