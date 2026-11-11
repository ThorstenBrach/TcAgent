// Tests for the saved chat conversations (ChatHistory.cs): a folder per solution, events, the handover for the next
// session, the open tasks of the plan, list / read / search for tc_chat_history and the retention.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("chat-history")]
public sealed class ChatHistoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-chat-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "work", "MachineX");
    static readonly DateTime T0 = new(2026, 11, 11, 17, 42, 0);

    public ChatHistoryTests()
    {
        ChatHistory.RootOverride = Path.Combine(dir, "chats");
        Directory.CreateDirectory(Sln);
        File.WriteAllText(Path.Combine(Sln, "Machine.sln"), "");
    }

    public void Dispose()
    {
        ChatHistory.RootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    string Conversation(DateTime start, bool withPlan = true, bool stopped = false)
    {
        var f = ChatHistory.NewFile(Sln, start);
        ChatHistory.Append(f, "start", new() { ["backend"] = "Claude Code" }, start);
        ChatHistory.Append(f, "question", new() { ["text"] = "Rework the homing of axis 2", ["where"] = "FB_Homing · line 12" }, start.AddMinutes(1));
        ChatHistory.Append(f, "tool", new() { ["name"] = "tc_plc_write", ["ok"] = true, ["result"] = "Saved" }, start.AddMinutes(2));
        ChatHistory.Append(f, "tool", new() { ["name"] = "tc_check", ["ok"] = false, ["result"] = "2 errors" }, start.AddMinutes(3));
        if (withPlan)
            ChatHistory.Append(f, "plan", new()
            {
                ["items"] = new List<object>
                {
                    new Dictionary<string, object> { ["text"] = "Change the homing sequence", ["status"] = "completed" },
                    new Dictionary<string, object> { ["text"] = "Fix the compile errors", ["status"] = "in_progress" },
                    new Dictionary<string, object> { ["text"] = "TcUnit test for FB_Homing", ["status"] = "pending" }
                }
            }, start.AddMinutes(3));
        ChatHistory.Append(f, "answer", new() { ["text"] = "The homing now uses the\nreference cam.", ["seconds"] = 42, ["tools"] = 2 }, start.AddMinutes(4));
        if (stopped)
        {
            ChatHistory.Append(f, "question", new() { ["text"] = "Now write the test" }, start.AddMinutes(5));
            ChatHistory.Append(f, "stopped", null, start.AddMinutes(6));
        }
        return f;
    }

    [Fact]
    public void Folder_is_per_solution_folder_and_named_after_the_solution_file()
    {
        var a = ChatHistory.FolderFor(Sln);
        Assert.StartsWith("Machine-", Path.GetFileName(a));
        Assert.Equal(a, ChatHistory.FolderFor(Sln + "\\"));                       // same folder, other spelling
        Assert.Equal(a, ChatHistory.FolderFor(Sln.ToUpperInvariant()));
        var other = Path.Combine(dir, "copy", "MachineX");
        Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "Machine.sln"), "");
        Assert.NotEqual(a, ChatHistory.FolderFor(other));                         // a second checkout keeps its own conversations
        Assert.EndsWith(ChatHistory.NoSolution, ChatHistory.FolderFor(null));
    }

    [Fact]
    public void Events_are_read_back_with_pairs_plan_and_backend()
    {
        var c = ChatHistory.Load(Conversation(T0));
        Assert.Equal("Claude Code", c.Backend);
        Assert.Equal(T0, c.Started);
        var pairs = c.Pairs();
        Assert.Single(pairs);
        Assert.Equal("Rework the homing of axis 2", pairs[0].Key);
        Assert.Equal("The homing now uses the\nreference cam.", pairs[0].Value);   // line breaks survive the one-line format
        Assert.Equal(2, c.Plan().Count(i => i.Open));
        Assert.False(c.Unfinished);
        Assert.Single(File.ReadAllLines(c.File), l => l.Contains("\"answer\""));
    }

    [Fact]
    public void A_broken_line_is_skipped()
    {
        var f = Conversation(T0);
        File.AppendAllText(f, "{\"t\":\"2026-11-11T18:00:00\",\"type\":\"quest");     // cut off by a crash
        Assert.Single(ChatHistory.Load(f).Questions);
    }

    [Fact]
    public void Handover_names_the_questions_the_open_tasks_and_a_stop()
    {
        var h = ChatHistory.Handover(ChatHistory.Load(Conversation(T0, stopped: true)));
        Assert.Contains("2026-11-11 17:42", h);
        Assert.Contains("Rework the homing of axis 2", h);
        Assert.Contains("[in_progress] Fix the compile errors", h);
        Assert.Contains("[pending] TcUnit test for FB_Homing", h);
        Assert.DoesNotContain("Change the homing sequence", h);                    // done tasks are not repeated
        Assert.Contains("stopped", h);
        Assert.Contains("tc_chat_history", h);
        Assert.True(h.Length < 2600);
        Assert.Null(ChatHistory.Handover(null));
    }

    [Fact]
    public void Latest_skips_conversations_without_a_question()
    {
        Conversation(T0);
        var empty = ChatHistory.NewFile(Sln, T0.AddDays(1));
        ChatHistory.Append(empty, "start", new() { ["backend"] = "Copilot" }, T0.AddDays(1));
        Assert.Equal("Claude Code", ChatHistory.Latest(Sln).Backend);
        Assert.Equal(2, ChatHistory.Files(Sln).Count);
    }

    [Fact]
    public void List_read_and_search_for_the_tool()
    {
        var f = Conversation(T0, stopped: true);
        var id = Path.GetFileNameWithoutExtension(f);
        var list = ChatHistory.ListText(Sln);
        Assert.Contains(id, list);
        Assert.Contains("2 open task(s)", list);
        Assert.Contains("stopped", list);
        var read = ChatHistory.Format(ChatHistory.Load(ChatHistory.Find(Sln, "latest")));
        Assert.Contains("User: Rework the homing of axis 2  (FB_Homing · line 12)", read);
        Assert.Contains("(tools: tc_plc_write, ✗ tc_check)", read);
        Assert.Contains("(stopped by the user)", read);
        Assert.Contains("[pending] TcUnit test for FB_Homing", read);
        Assert.Equal(f, ChatHistory.Find(Sln, id.Substring(0, 15)));
        Assert.Contains("reference cam", ChatHistory.Search(Sln, "reference CAM"));
        Assert.StartsWith("No match", ChatHistory.Search(Sln, "conveyor"));
        Assert.StartsWith("No saved chat", ChatHistory.ListText(Path.Combine(dir, "other")));
    }

    [Fact]
    public void Prune_keeps_the_newest_and_the_one_in_use()
    {
        var files = Enumerable.Range(0, 5).Select(i => Conversation(T0.AddMinutes(i * 10), withPlan: false)).ToList();
        File.SetLastWriteTime(files[0], DateTime.Now.AddDays(-200));
        Assert.Equal(1, ChatHistory.Prune(Sln, days: 90, max: 3, keep: files[0]));   // the oldest is in use, the 4th newest goes
        Assert.True(File.Exists(files[0]));
        Assert.False(File.Exists(files[1]));
        Assert.Equal(1, ChatHistory.Prune(Sln, days: 90, max: 3));                  // now the old one goes
        Assert.False(File.Exists(files[0]));
        Assert.Equal(3, ChatHistory.DeleteProject(Sln));
        Assert.Empty(ChatHistory.Files(Sln));
    }
}
