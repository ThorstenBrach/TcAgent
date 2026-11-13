// Tests for the clean-up of %LOCALAPPDATA%\TcAgent (Storage.cs): the rules per area, settings.json "retention", the
// projects, and that backups and the knowledge are never touched.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class StorageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-st-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0);

    public StorageTests()
    {
        Directory.CreateDirectory(dir);
        KnowledgeLayers.UserRootOverride = dir;
        Storage.RootOverride = dir;
    }

    public void Dispose()
    {
        KnowledgeLayers.UserRootOverride = null;
        Storage.RootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    string Write(string rel, string text = "x", DateTime? time = null)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        if (time != null) File.SetLastWriteTime(p, time.Value);
        return p;
    }

    [Fact]
    public void The_rules_pick_old_and_surplus_files_and_never_backups_or_knowledge()
    {
        Write(@"logs\tcagent-20260901.log", "old", Now.AddDays(-60));
        Write(@"logs\tcagent-20261112.log", "new", Now.AddDays(-1));
        Write(@"attachments\20260901\a.pdf", "a", Now.AddDays(-70));
        Write(@"attachments\20261112\b.pdf", "b", Now.AddDays(-1));
        for (int i = 1; i <= 5; i++) Write($@"settings.json.bak-2026110{i}-120000");
        Write(@"busy.999999.txt");                                  // no such process
        Write($@"busy.{Environment.ProcessId}.txt");                // this test: runs
        Write(@"backups\user\20260101.zip", "zip", Now.AddDays(-300));
        Write(@"skills\my-skill\SKILL.md", "---\nname: my-skill\n---\n", Now.AddDays(-300));
        Write(@"app\0.53.0\TcAgent.exe"); Write(@"app\0.54.0\TcAgent.exe"); Write(@"app\0.55.0\TcAgent.exe");

        var plan = Storage.Plan(now: Now).Select(i => Path.GetRelativePath(dir, i.Path)).ToList();
        Assert.Contains(@"logs\tcagent-20260901.log", plan);
        Assert.DoesNotContain(@"logs\tcagent-20261112.log", plan);
        Assert.Contains(@"attachments\20260901", plan);
        Assert.DoesNotContain(@"attachments\20261112", plan);
        Assert.Equal(2, plan.Count(p => p.StartsWith("settings.json.bak-")));            // the newest 3 stay
        Assert.Contains("settings.json.bak-20261101-120000", plan);
        Assert.Contains("busy.999999.txt", plan);
        Assert.DoesNotContain($"busy.{Environment.ProcessId}.txt", plan);
        Assert.Contains(@"app\0.53.0", plan);
        Assert.DoesNotContain(@"app\0.54.0", plan);
        Assert.DoesNotContain(plan, p => p.StartsWith("backups") || p.StartsWith("skills"));

        var r = Storage.Clean(Storage.Plan(now: Now), permanent: true);
        Assert.Empty(r.Errors);
        Assert.False(File.Exists(Path.Combine(dir, @"logs\tcagent-20260901.log")));
        Assert.True(File.Exists(Path.Combine(dir, @"backups\user\20260101.zip")));
    }

    [Fact]
    public void Chats_keep_the_newest_per_solution_and_the_auto_run_leaves_them()
    {
        for (int i = 0; i < 4; i++) Write($@"chats\MachineX-0a1b2c3d\2026111{i}-120000-ab0{i}.jsonl", "{}", Now.AddDays(-3 + i * 0.1));
        Write(@"settings.json", """{ "retention": { "chats": { "keep": 2 }, "logs": false } }""");
        var plan = Storage.Plan(now: Now);
        Assert.Equal(new[] { "20261110-120000-ab00.jsonl", "20261111-120000-ab01.jsonl" }, plan.Where(i => i.Area == "chats").Select(i => Path.GetFileName(i.Path)).OrderBy(x => x));
        Assert.Empty(Storage.Plan(auto: true, now: Now).Where(i => i.Area == "chats"));       // chats only on request
        Assert.False(Storage.Rules().Single(r => r.Def.Key == "logs").On);
    }

    [Fact]
    public void A_big_log_is_set_aside()
    {
        var log = Write(@"bridge\bridge.log", new string('x', 2 * 1024 * 1024));
        Write(@"settings.json", """{ "retention": { "logs": { "keep": 1 } } }""");
        var i = Assert.Single(Storage.Plan(area: "logs", now: Now));
        Assert.True(i.Rotate);
        Storage.Clean(new[] { i }, permanent: true);
        Assert.False(File.Exists(log));
        Assert.True(File.Exists(log + ".old"));
    }

    [Fact]
    public void Projects_bring_their_chats_tcunit_runs_and_runs_together()
    {
        Write(@"chats\MachineX-0a1b2c3d\20261112-120000-ab00.jsonl");
        Write(@"chats\MachineX-0a1b2c3d\solution.json", """{ "solutionDir": "C:\\gone\\MachineX", "name": "MachineX" }""");
        Write(@"tcunit\MachineX-1F2E3D4C\last.json");
        Write(@"runs\MachineX\project.json");
        Write(@"runs\Other\project.json");
        var p = Assert.Single(Storage.Projects(), x => x.Name == "MachineX");
        Assert.Equal(3, p.Parts.Count);
        Assert.False(p.Exists);                                     // its solution folder is gone
        var r = Storage.DeleteProject("MachineX", permanent: true);
        Assert.Equal(3, r.Removed);
        Assert.DoesNotContain(Storage.Projects(), x => x.Name == "MachineX");
        Assert.Contains(Storage.Projects(), x => x.Name == "Other");
    }
}
