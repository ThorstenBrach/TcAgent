// tc_chat_history: the agent reads the saved conversations of the TcAgent Chat of this project (ChatHistory.cs) - what was
// discussed last time, the open tasks of its plan then, a search over older conversations. Read only.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterChatHistory()
    {
        Add("tc_chat_history", "The saved conversations of the TcAgent Chat for THIS project (kept locally per solution): what was discussed " +
                               "and done last time, the open tasks of the plan then, whether the last question was stopped. Use it when the user asks " +
                               "what was discussed before, where you stopped, or to finish open tasks - then check those tasks against the current " +
                               "project before you continue. action=read (default: the latest conversation; id=... another one; full=true for whole answers), " +
                               "action=list (the conversations, newest first), action=search query=... (words in questions and answers).",
            Schema(P("action", "string", "read (default), list or search"),
                   P("id", "string", "read: the conversation from list (e.g. 20261111-174203-a1b2) or 'latest'"),
                   P("full", "boolean", "read: whole answers instead of shortened ones"),
                   P("query", "string", "search: words that must all appear"),
                   P("max", "integer", "list/search: max. entries (default 20 / 10)")),
            a =>
            {
                var dir = SolutionDir();
                switch ((Str(a, "action") ?? "read").ToLowerInvariant())
                {
                    case "list": return ChatHistory.ListText(dir, Int(a, "max", 20));
                    case "search": return ChatHistory.Search(dir, Str(a, "query"), Int(a, "max", 10));
                    case "read":
                        var file = ChatHistory.Find(dir, Str(a, "id"));
                        return file == null ? ChatHistory.ListText(dir) : ChatHistory.Format(ChatHistory.Load(file), Bool(a, "full", false));
                    default: throw new Exception("action: read, list or search");
                }
            });
    }
}
