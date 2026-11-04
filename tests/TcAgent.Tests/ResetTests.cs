// Reset of TcAgent (Reset.cs): what moves away, what stays, nothing is lost.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public class ResetTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-reset-" + Guid.NewGuid().ToString("N"));
    readonly string old = KnowledgeLayers.UserRootOverride;
    static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0);

    void Put(string rel, string text = "x")
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public ResetTests()
    {
        KnowledgeLayers.UserRootOverride = dir;
        Put(@"prompts\mine.md"); Put(@"coding-rules\style.md"); Put(@"skills\s\SKILL.md"); Put(@"plugins\p\plugin.json");
        Put(@"sources\cache\a.txt"); Put("sources.json", "[]"); Put("modules.json", "{}"); Put("plugins-approved.json", "{}");
        Put("shared-folders.json", "{}"); Put(@"knowledge\manual.pdf");
        Put("settings.json", """{ "env": { "TCAGENT_PLATFORMS": "D:\\x" }, "server": { "version": "1" } }""");
        Put(@"app\1.0\TcAgent.exe"); Put(@"feedback\feedback.jsonl"); Put("chat-backends.json", "{}"); Put(@"runs\r.jsonl");
    }

    public void Dispose() { KnowledgeLayers.UserRootOverride = old; try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Plan_names_everything_and_changes_nothing()
    {
        var (items, connections) = Reset.Plan();
        Assert.Contains("prompts", items);
        Assert.Contains("plugins", items);
        Assert.Equal("TCAGENT_PLATFORMS", Assert.Single(connections));
        Assert.True(File.Exists(Path.Combine(dir, @"prompts\mine.md")));
    }

    [Fact]
    public void Run_moves_the_knowledge_and_loses_nothing()
    {
        var (folder, _, failed) = Reset.Run(Now);
        Assert.Empty(failed);
        Assert.False(Directory.Exists(Path.Combine(dir, "prompts")));
        Assert.False(Directory.Exists(Path.Combine(dir, "plugins")));
        Assert.False(File.Exists(Path.Combine(dir, "modules.json")));
        Assert.True(File.Exists(Path.Combine(folder, @"prompts\mine.md")));
        Assert.True(File.Exists(Path.Combine(folder, @"plugins\p\plugin.json")));
        Assert.True(File.Exists(Path.Combine(folder, @"knowledge\manual.pdf")));
    }

    [Fact]
    public void The_connections_go_and_the_registered_server_stays()
    {
        var (folder, _, _) = Reset.Run(Now);
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.Null(s["env"]);
        Assert.Equal("1", s["server"]!["version"]!.ToString());
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "settings.json")))!["env"]);
    }

    [Fact]
    public void Program_feedback_logs_and_chat_settings_stay()
    {
        Reset.Run(Now);
        foreach (var f in new[] { @"app\1.0\TcAgent.exe", @"feedback\feedback.jsonl", "chat-backends.json", @"runs\r.jsonl" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
    }

    [Fact]
    public void A_file_in_use_is_named_and_the_rest_goes_on()
    {
        using var locked = File.Open(Path.Combine(dir, @"plugins\p\plugin.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var (_, _, failed) = Reset.Run(Now);
        Assert.Contains(failed, f => f.StartsWith("plugins"));
        Assert.False(Directory.Exists(Path.Combine(dir, "prompts")));
    }

    [Fact]
    public void A_second_run_finds_nothing_more()
    {
        Reset.Run(Now);
        var (items, connections) = Reset.Plan();
        Assert.Empty(items);
        Assert.Empty(connections);
    }
}
