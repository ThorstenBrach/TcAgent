// TcAgent Chat over the Agent Client Protocol (ACP, agentclientprotocol.com) - backend format "acp".
// One protocol for every agent CLI that speaks it (GitHub Copilot CLI: copilot --acp, Gemini CLI: gemini --experimental-acp,
// Claude Code and Codex through their ACP adapters): JSON-RPC 2.0, one message per line on stdin/stdout.
//
//   initialize           protocol version, client capabilities (no file system / terminal of our own - the agent uses its tools)
//   session/new          working folder = solution folder, MCP server "tcagent" (TcAgent.exe of mcp-copilot.json, with the
//                        IDE's process id so TcAgent works in THIS instance)
//   session/prompt       the request (same prompt as for the other backends); the answer streams as session/update:
//                        agent_message_chunk (text), tool_call / tool_call_update (one line per tool), usage_update;
//                        the prompt returns with its stopReason when the turn is done
//   session/request_permission   the permission card of the chat (allow once / always for this session / deny)
//   session/cancel       Stop - the session stays open for the next question
//
// The session stays open like the persistent Claude session: follow-up questions keep the agent's own history.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Task = System.Threading.Tasks.Task;
using System.Threading.Tasks;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        static bool IsAcp(Backend be) => be?.Format == "acp";

        sealed class AcpState
        {
            public string SessionId;
            public long NextId = 100;
            public readonly Dictionary<long, TaskCompletionSource<Dictionary<string, object>>> Pending = new Dictionary<long, TaskCompletionSource<Dictionary<string, object>>>();
            public Task Opening;
            public bool LastNewline = true;
            public readonly Dictionary<string, string> ToolTitles = new Dictionary<string, string>();
            public string Usage;
        }

        static JavaScriptSerializer Js() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        void AcpWrite(Session s, Dictionary<string, object> msg)
        {
            var bytes = new UTF8Encoding(false).GetBytes(Js().Serialize(msg) + "\n");
            lock (writeLock) { s.In.Write(bytes, 0, bytes.Length); s.In.Flush(); }
        }

        /// <summary>A request to the agent; the task ends with its result (or throws with the error of the agent).</summary>
        async Task<Dictionary<string, object>> AcpCall(Session s, string method, Dictionary<string, object> prms)
        {
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            long id;
            lock (s.Acp.Pending) { id = s.Acp.NextId++; s.Acp.Pending[id] = tcs; }
            AcpWrite(s, new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms });
            var done = await Task.WhenAny(tcs.Task, Task.Run(async () => { while (!s.Exited) await Task.Delay(200); }));
            if (done != tcs.Task) throw new Exception("the agent ended");
            var m = await tcs.Task;
            if (m.TryGetValue("error", out var eo) && eo is Dictionary<string, object> err)
                throw new Exception($"{method}: {(err.TryGetValue("message", out var em) ? em : "error")}" +
                                    (err.TryGetValue("data", out var ed) && ed != null ? " - " + Short(Js().Serialize(ed), 300) : ""));
            return m.TryGetValue("result", out var r) ? r as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
        }

        /// <summary>initialize + session/new; started once per process (also in advance by WarmUp).</summary>
        Task AcpOpen(Session s)
        {
            lock (s) return s.Acp.Opening ?? (s.Acp.Opening = Open());
            async Task Open()
            {
                var init = await AcpCall(s, "initialize", new Dictionary<string, object>
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new Dictionary<string, object> { ["fs"] = new Dictionary<string, object> { ["readTextFile"] = false, ["writeTextFile"] = false }, ["terminal"] = false },
                    ["clientInfo"] = new Dictionary<string, object> { ["name"] = "tcagent-chat", ["version"] = BridgePackage.BridgeVersion }
                });
                Dictionary<string, object> sn;
                try { sn = await AcpCall(s, "session/new", new Dictionary<string, object> { ["cwd"] = SolutionDir() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["mcpServers"] = AcpMcpServers(s.Be) }); }
                catch (Exception ex) when (ex.Message.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var how = (init.TryGetValue("authMethods", out var am) ? am as IEnumerable : null)?.OfType<Dictionary<string, object>>()
                              .Select(x => x.TryGetValue("description", out var d) ? d as string : null).FirstOrDefault(x => x != null);
                    throw new Exception($"{s.Be.Name} is not signed in. {how ?? "Sign in once in a terminal."} Then ask again.");
                }
                s.Acp.SessionId = sn.TryGetValue("sessionId", out var sid) ? sid as string : throw new Exception("session/new returned no sessionId");
            }
        }

        // The tcagent MCP server for the session: TcAgent.exe from mcp-copilot.json (written by the client setup), with the
        // process id of this IDE and the backend as client name
        static object[] AcpMcpServers(Backend be)
        {
            string command = null; var args = new List<string>(); var env = new Dictionary<string, string>();
            try
            {
                var j = Js().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Dir, "mcp-copilot.json")));
                if (j["mcpServers"] is Dictionary<string, object> ms && ms.TryGetValue("tcagent", out var t) && t is Dictionary<string, object> tc)
                {
                    command = tc.TryGetValue("command", out var c) ? c as string : null;
                    if (tc.TryGetValue("args", out var a) && a is IEnumerable aa) args.AddRange(aa.OfType<object>().Select(Convert.ToString));
                    if (tc.TryGetValue("env", out var e) && e is Dictionary<string, object> ee) foreach (var kv in ee) env[kv.Key] = Convert.ToString(kv.Value);
                }
            }
            catch { }
            if (string.IsNullOrEmpty(command) || !File.Exists(command)) command = TcUnitControl.TcAgentExe();
            if (command == null) return new object[0];       // the agent may still have tcagent in its own configuration
            env["TCAGENT_CLIENT"] = "chat:" + be.Key;
            env["TCAGENT_VS_PID"] = Process.GetCurrentProcess().Id.ToString();
            return new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "tcagent", ["command"] = command, ["args"] = args.ToArray(),
                    ["env"] = env.Select(kv => (object)new Dictionary<string, object> { ["name"] = kv.Key, ["value"] = kv.Value }).ToArray()
                }
            };
        }

        // One line from the agent: an answer to our request, a request of the agent, or a notification
        void OnAcpLine(Session s, string line)
        {
            Dictionary<string, object> m;
            try { m = Js().Deserialize<Dictionary<string, object>>(line); } catch { return; }
            if (m == null) return;
            var method = m.TryGetValue("method", out var mo) ? mo as string : null;
            if (method == null && m.TryGetValue("id", out var rid))
            {
                TaskCompletionSource<Dictionary<string, object>> tcs = null;
                lock (s.Acp.Pending) { var id = Convert.ToInt64(rid); if (s.Acp.Pending.TryGetValue(id, out tcs)) s.Acp.Pending.Remove(id); }
                tcs?.TrySetResult(m);
                return;
            }
            var prms = m.TryGetValue("params", out var po) ? po as Dictionary<string, object> : null;
            if (m.TryGetValue("id", out var reqId))
            {
                // requests of the agent: permission (a card in the chat); others are not offered by this client
                if (method == "session/request_permission") Task.Run(() => AcpPermission(s, reqId, prms));
                else AcpWrite(s, new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = reqId, ["error"] = new Dictionary<string, object> { ["code"] = -32601, ["message"] = "not supported by the TcAgent Chat: " + method } });
                return;
            }
            if (method == "session/update" && prms?["update"] is Dictionary<string, object> u && s.Turn != null) AcpUpdate(s, u);
        }

        void AcpUpdate(Session s, Dictionary<string, object> u)
        {
            var kind = u.TryGetValue("sessionUpdate", out var k) ? k as string : null;
            var t = s.TurnSw?.Elapsed.TotalSeconds ?? 0;
            switch (kind)
            {
                case "agent_message_chunk":
                    if (u.TryGetValue("content", out var co) && co is Dictionary<string, object> c && c.TryGetValue("text", out var tx) && tx is string text && text.Length > 0)
                    {
                        s.Answer.Append(text);
                        AgentAppend(text);
                        s.Acp.LastNewline = text.EndsWith("\n");
                    }
                    break;
                case "tool_call":
                {
                    var id = u.TryGetValue("toolCallId", out var ti) ? ti as string : "";
                    var title = ToolTitle(u);
                    s.Acp.ToolTitles[id ?? ""] = title;
                    AgentAppend((s.Acp.LastNewline ? "" : "\n") + $"{ToolMark}{title}  [{t:0.0} s]\n");
                    s.Acp.LastNewline = true;
                    Status($"{title} ... ({t:0} s)");
                    break;
                }
                case "tool_call_update":
                {
                    var st = u.TryGetValue("status", out var so) ? so as string : null;
                    if (st == "failed")
                    {
                        var id = u.TryGetValue("toolCallId", out var ti) ? ti as string : "";
                        s.Acp.ToolTitles.TryGetValue(id ?? "", out var title);
                        AgentAppend((s.Acp.LastNewline ? "" : "\n") + $"{ToolMark}{title ?? "tool"} failed  [{t:0.0} s]\n");
                        s.Acp.LastNewline = true;
                    }
                    break;
                }
                case "usage_update":
                    if (u.TryGetValue("used", out var used) && u.TryGetValue("size", out var size))
                        s.Acp.Usage = $"context {Convert.ToInt64(used) / 1000}k of {Convert.ToInt64(size) / 1000}k";
                    break;
            }
        }

        // "tcagent-tc_status" / "tc_status (MCP: tcagent)" / a title with the arguments -> a short line
        static string ToolTitle(Dictionary<string, object> u)
        {
            var title = (u.TryGetValue("title", out var to) ? to as string : null) ?? "tool";
            title = System.Text.RegularExpressions.Regex.Replace(title, @"^(?:tcagent[-_/:.]|mcp__tcagent__)", "");
            if (u.TryGetValue("rawInput", out var ri) && ri is Dictionary<string, object> d && d.Count > 0 && title.IndexOf(' ') < 0)
                title += " " + string.Join(", ", d.Take(4).Select(kv => kv.Key + "=" + Short(Convert.ToString(kv.Value), 60)));
            return Short(title, 160);
        }

        void Status(string text) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => status.Text = text));

        // session/request_permission: options allow_once / allow_always / reject_once / reject_always -> the permission card
        void AcpPermission(Session s, object reqId, Dictionary<string, object> prms)
        {
            var options = (prms != null && prms.TryGetValue("options", out var oo) ? oo as IEnumerable : null)?.OfType<Dictionary<string, object>>().ToList() ?? new List<Dictionary<string, object>>();
            string Opt(string kindPrefix) => options.Where(o => (o.TryGetValue("kind", out var k) ? k as string : "").StartsWith(kindPrefix))
                                                    .Select(o => o.TryGetValue("optionId", out var id) ? id as string : null).FirstOrDefault();
            var call = prms != null && prms.TryGetValue("toolCall", out var tc) ? tc as Dictionary<string, object> : null;
            var title = call != null ? ToolTitle(call) : "a tool";
            var input = call != null && call.TryGetValue("rawInput", out var ri) ? ri as Dictionary<string, object> : null;
            var always = options.FirstOrDefault(o => (o.TryGetValue("kind", out var k) ? k as string : "") == "allow_always");
            var choice = AskPermission(title, null, null, input, always != null ? (always.TryGetValue("name", out var n) ? n as string : "always") : null, s.Be.Name);
            var pick = choice == "session" ? Opt("allow_always") ?? Opt("allow") : choice == "once" ? Opt("allow_once") ?? Opt("allow") : Opt("reject_once") ?? Opt("reject");
            var outcome = pick != null ? new Dictionary<string, object> { ["outcome"] = "selected", ["optionId"] = pick } : new Dictionary<string, object> { ["outcome"] = "cancelled" };
            try { AcpWrite(s, new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = reqId, ["result"] = new Dictionary<string, object> { ["outcome"] = outcome } }); } catch { }
        }

        async Task SendAcp(Backend be, string text, string ctx)
        {
            var warm = sess != null && !sess.Exited && sess.Be == be && sess.Acp?.SessionId != null;
            var s = EnsureSession(be);
            status.Text = $"{be.Name} is starting ...";
            await AcpOpen(s);
            s.Answer.Clear(); s.Summary = null; s.Acp.LastNewline = true; s.Acp.Usage = null;
            s.TurnSw = Stopwatch.StartNew();
            s.Turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = BuildPrompt(text, ctx, followUp: s.Greeted, askTool: true);
            s.Greeted = true;
            status.Text = $"{be.Name} is working ... ({(warm ? "session running" : "session started")})";
            StartAgent(be.Name);
            string stop;
            try
            {
                var r = await AcpCall(s, "session/prompt", new Dictionary<string, object>
                {
                    ["sessionId"] = s.Acp.SessionId,
                    ["prompt"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt } }
                });
                stop = r.TryGetValue("stopReason", out var sr) ? sr as string : "end_turn";
            }
            catch (Exception ex)
            {
                string err; lock (s.Err) err = s.Err.ToString().Trim();
                FlushAgent();
                SystemLine($"{be.Name}: {ex.Message}" + (s.Exited && err.Length > 0 ? "\n" + Tail(err, 2500) : ""), true);
                status.Text = s.Exited ? "Session ended - the next question starts a new one." : "Error.";
                if (s.Exited && sess == s) sess = null;
                s.Turn = null;
                return;
            }
            s.Turn = null;
            var answer = s.Answer.ToString();
            if (stop == "cancelled") SystemLine("cancelled", true);
            else if (stop != "end_turn") SystemLine($"{be.Name} stopped: {stop}", true);
            status.Text = $"Done in {s.TurnSw.Elapsed.TotalSeconds:0} s ({be.Name}, ACP{(warm ? ", running session" : "")})" + (s.Acp.Usage != null ? " - " + s.Acp.Usage : ".");
            if (answer.Trim().Length > 0) history.Add(new KeyValuePair<string, string>(text, answer.Trim()));
        }

        /// <summary>Stop: session/cancel - the agent ends the turn, the session stays open.</summary>
        void AcpCancel(Session s)
        {
            try { AcpWrite(s, new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = "session/cancel", ["params"] = new Dictionary<string, object> { ["sessionId"] = s.Acp.SessionId } }); }
            catch { KillSession(); }
            CancelOpenCards();
        }
    }
}
