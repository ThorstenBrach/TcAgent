// tc_ask_user: the agent asks the user a question with options and/or a free answer - in every AI client.
//
// Channel (like the confirmations, Confirm.cs): from the TcAgent Chat a question card in the chat window of the IDE
// (TcAgentBridge command "ask"); in other clients the client's own question dialog (MCP elicitation), the card in the IDE
// as fallback. Without any channel the agent gets a clear answer: ask in the reply instead. The format and the checks are
// in AskQuestions.cs.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterAskUser()
    {
        var option = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["label"] = new JsonObject { ["type"] = "string", ["description"] = "short answer (1-5 words)" },
                ["description"] = new JsonObject { ["type"] = "string", ["description"] = "what it means / consequences" }
            },
            ["required"] = new JsonArray("label")
        };
        var question = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["question"] = new JsonObject { ["type"] = "string", ["description"] = "the complete question" },
                ["header"] = new JsonObject { ["type"] = "string", ["description"] = "very short topic label, e.g. 'Cycle time'" },
                ["options"] = new JsonObject { ["type"] = "array", ["items"] = option, ["description"] = "2-4 options, recommended one first; the user can always type an own answer" },
                ["multiSelect"] = new JsonObject { ["type"] = "boolean", ["description"] = "several options may be picked" }
            },
            ["required"] = new JsonArray("question")
        };
        Add("tc_ask_user", "Ask the user when a decision or missing information blocks the work: 1-4 questions, each with 2-4 options " +
                           "(recommended option first, with a short description) - the user can always answer in free text. Shown as a question " +
                           "card in the TcAgent Chat or as the client's question dialog. Do not ask what the project or the tools can tell you. " +
                           "Use it instead of ending your reply with a question.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["questions"] = new JsonObject { ["type"] = "array", ["items"] = question, ["description"] = "1-4 questions" } },
                ["required"] = new JsonArray("questions")
            },
            a => AskUser(a));
    }

    string AskUser(JsonObject a)
    {
        List<AskQuestions.Question> qs;
        try { qs = AskQuestions.Parse(a["questions"]); }
        catch (ArgumentException ex) { throw new Exception("NOT asked: " + ex.Message); }

        int pid = tc.AttachedPid > 0 ? tc.AttachedPid : int.TryParse(Environment.GetEnvironmentVariable("TCAGENT_VS_PID"), out var vp) ? vp : 0;
        bool fromChat = (Environment.GetEnvironmentVariable("TCAGENT_CLIENT") ?? "").StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                        || TwinCatSession.StartedFrom(pid);

        // the card in the chat window of the IDE: null = not reachable, "" entries = skipped
        Dictionary<string, string> ViaBridge()
        {
            // pid 0 (not attached): the only running bridge, if there is exactly one
            if (!BridgeClient.Available(pid)) return null;
            try
            {
                var r = BridgeClient.Call(pid, "ask", AskQuestions.ForCard(qs), 30 * 60_000);
                if (r["answered"]?.GetValue<bool>() != true) return new Dictionary<string, string>();
                var d = new Dictionary<string, string>();
                if (r["answers"] is JsonObject ans) foreach (var kv in ans) d[kv.Key] = kv.Value?.ToString();
                return d;
            }
            catch (UntrustedBridgeException ex) { Log.Error("tc_ask_user via the bridge: " + ex.Message); return null; }
            catch (TimeoutException) { return new Dictionary<string, string>(); }
            catch (Exception ex) { Log.Error("tc_ask_user via the bridge: " + ex.Message); return null; }     // e.g. an older bridge without "ask"
        }
        Dictionary<string, string> ViaClient()
        {
            if (!Mcp.CanElicit) return null;
            var answer = Mcp.Elicit(qs.Count == 1 ? qs[0].Text : "Questions from the agent", AskQuestions.ElicitationSchema(qs), 30 * 60_000);
            if (answer != null) return AskQuestions.FromElicitation(qs, answer);
            // decline/cancel count as the user's answer only if the question was really on screen
            return Mcp.LastElicitOutcome is "decline" or "cancel" && Mcp.LastElicitMs >= 1500 ? new Dictionary<string, string>() : null;
        }

        var answers = fromChat ? ViaBridge() ?? ViaClient() : ViaClient() ?? ViaBridge();
        if (answers == null)
            return "NOT asked: no question dialog is available (the client cannot show questions and no TcAgent Chat is reachable). " +
                   "Ask the user in your reply instead and wait for the answer.";
        if (answers.Count == 0 || answers.Values.All(string.IsNullOrWhiteSpace))
            return "The user closed the question without answering. Do not ask the same again; continue without it if you can, " +
                   "otherwise say in your reply what you need.";
        Log.Info("tc_ask_user answered: " + string.Join("; ", answers.Values));
        // for the run log: an answer other than the recommended (first) option is a correction of the agent
        if (run != null) run.Corrections += qs.Count(q => q.Options.Count > 0 && answers.TryGetValue(q.Text, out var v) && !string.IsNullOrWhiteSpace(v)
                                                          && !v.Trim().Equals(q.Options[0].Label, StringComparison.OrdinalIgnoreCase));
        return AskQuestions.Format(qs, answers);
    }
}
