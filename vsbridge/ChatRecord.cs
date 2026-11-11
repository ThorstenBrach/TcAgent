// The chat keeps its conversations per solution (src/ChatHistory.cs, linked): every question, answer, tool call, the plan of
// the agent and a stop are written in the background. When a solution opens, its latest conversation is shown again; the
// first question to a new agent session gets a short handover (what was asked, open tasks), and tc_chat_history lets the
// agent read more. A conversation without an event for 8 hours is finished - the next question starts a new file, the
// handover still names the old one. "New chat" starts a new conversation; the old one stays saved.
// chat-backends.json: "saveHistory": false switches it off.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;   // the VS 15 shell (XAE Shell) also has Microsoft.VisualStudio.Shell.Task
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using TcAgent;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        bool saveHistory = true;
        string convFile, convDir;               // the conversation written now and the solution it belongs to
        DateTime convLast;
        ChatHistory.Conversation handover;      // the conversation of the last session, for the first prompt
        bool stoppedByUser;
        Task writes = Task.CompletedTask;       // the events in order, never on the UI thread
        readonly object writesLock = new object();

        void Record(string type, Dictionary<string, object> data = null)
        {
            if (!saveHistory) return;
            var now = DateTime.Now;
            // the solution is asked only for a question (UI thread); tool calls and plans come from the reader threads
            var dir = type == "question" ? SolutionDir() : null;
            lock (writesLock)
            {
                if (type != "question" && convFile == null) return;     // a conversation starts with a question
                var fresh = type == "question" && (convFile == null || now - convLast > ChatHistory.IdleLimit ||
                                                   !string.Equals(ChatHistory.Key(dir), ChatHistory.Key(convDir), StringComparison.Ordinal));
                if (fresh)
                {
                    convFile = ChatHistory.NewFile(dir, now); convDir = dir;
                    var file = convFile; var backend = ShortName(Current?.Name);
                    writes = writes.ContinueWith(_ =>
                    {
                        ChatHistory.Append(file, "start", new Dictionary<string, object> { ["backend"] = backend }, now);
                        try { ChatHistory.Prune(dir, keep: file); } catch { }
                    }, TaskScheduler.Default);
                }
                convLast = now;
                var f = convFile;
                writes = writes.ContinueWith(_ => ChatHistory.Append(f, type, data, now), TaskScheduler.Default);
            }
        }

        void RecordQuestion(string text, string ctx, IList<Attachment> att)
        {
            stoppedByUser = false;
            var where = ContextLabel(ctx);
            Record("question", new Dictionary<string, object>
            {
                ["text"] = text,
                ["where"] = where == null ? null : Regex.Replace(where, @"^\S+\s+", ""),      // without the 📄
                ["attachments"] = att != null && att.Count > 0 ? att.Select(a => (object)a.Name).ToList() : null
            });
        }

        void RecordAnswer(string text, double seconds)
        {
            Record("answer", new Dictionary<string, object> { ["text"] = text, ["seconds"] = Math.Round(seconds), ["tools"] = turnTools });
            if (stoppedByUser) Record("stopped");
        }

        void RecordTool(string name, bool ok, string first)
        {
            if (name == "TodoWrite") return;        // its content is the plan
            Record("tool", new Dictionary<string, object> { ["name"] = name, ["ok"] = ok, ["result"] = first });
        }

        /// <summary>The task list of the agent: Claude Code's TodoWrite {todos:[{content, status}]}, ACP plan {entries:[{content, status}]}.</summary>
        void RecordPlan(object items)
        {
            if (!(items is IEnumerable list) || items is string) return;
            var plan = list.OfType<IDictionary<string, object>>()
                           .Select(d => (object)new Dictionary<string, object>
                           {
                               ["text"] = d.TryGetValue("content", out var c) ? Convert.ToString(c) : "",
                               ["status"] = d.TryGetValue("status", out var s) ? Convert.ToString(s) : ""
                           }).ToList();
            if (plan.Count > 0) Record("plan", new Dictionary<string, object> { ["items"] = plan });
        }

        /// <summary>The handover for the first prompt of a new agent session - once per opened conversation.</summary>
        string TakeHandover()
        {
            var h = handover;
            handover = null;
            return h == null ? null : ChatHistory.Handover(h);
        }

        void ForgetConversation()
        {
            lock (writesLock) { convFile = null; convDir = null; }
            handover = null;
        }

        // ------------------------------------------------------------------ the conversation of the solution
        /// <summary>Shows the latest conversation of the open solution (UI thread). A conversation of the last 8 hours goes on,
        /// an older one is shown and only handed over.</summary>
        void LoadProjectConversation()
        {
            if (!saveHistory || busy) return;
            ChatHistory.Conversation c;
            string dir;
            try { dir = SolutionDir(); c = ChatHistory.Latest(dir); } catch { return; }
            if (c == null) return;
            var pairs = c.Pairs();
            var shown = pairs.Skip(Math.Max(0, pairs.Count - 10)).ToList();
            foreach (var p in shown)
            {
                AddUserBubble(p.Key, null, null, false);
                if (string.IsNullOrEmpty(p.Value)) continue;
                StartAgent(c.Backend ?? "Agent");
                cur.Raw.Append(p.Value);
                Render(cur);
                cur = null;
            }
            var goesOn = DateTime.Now - c.Last <= ChatHistory.IdleLimit;
            if (goesOn)
            {
                history.Clear();
                history.AddRange(pairs.Where(p => !string.IsNullOrEmpty(p.Value)));
                lock (writesLock) { convFile = c.File; convDir = dir; convLast = c.Last; }
            }
            handover = c;
            var open = c.Plan().Count(i => i.Open);
            SystemLine($"Last conversation in this project: {c.Started:dd.MM. HH:mm}, {c.Questions.Count} question{(c.Questions.Count == 1 ? "" : "s")}" +
                       (pairs.Count > shown.Count ? $" (the last {shown.Count} shown)" : "") +
                       (open > 0 ? $", {open} open task{(open == 1 ? "" : "s")}" : "") + (c.Unfinished ? ", stopped" : "") +
                       (goesOn ? " - the next question continues it. New chat starts fresh." : " - the agent gets a summary with the next question. New chat starts without it."));
            scroller.ScrollToEnd();
        }

        /// <summary>The solution was closed or another one opened: the conversation of the old one ends here.</summary>
        void SolutionChanged(bool opened)
        {
            if (busy) Stop();
            history.Clear(); msgs.Children.Clear(); cur = null; lock (toolCalls) toolCalls.Clear(); contextOff = false;
            ForgetConversation();
            KillSession();
            ShowEmptyState(); UpdateContextChip();
            if (opened) { LoadProjectConversation(); if (IsVisible) WarmUp(); }
            status.Text = opened ? "Ready." : "No solution open.";
        }

        void DeleteProjectHistory()
        {
            var dir = SolutionDir();
            var n = ChatHistory.Files(dir).Count;
            if (n == 0) { status.Text = "No saved conversations for this solution."; return; }
            if (MessageBox.Show($"Delete the {n} saved chat conversation{(n == 1 ? "" : "s")} of this solution?\n\n{ChatHistory.FolderFor(dir)}\n\nThis cannot be undone.",
                                "TcAgent Chat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            lock (writesLock) { writes.Wait(2000); }
            ForgetConversation();
            status.Text = $"Deleted: {ChatHistory.DeleteProject(dir)} conversation(s).";
        }

        // ------------------------------------------------------------------ solution events of Visual Studio
        uint solutionCookie;

        void WatchSolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (solutionCookie != 0) return;
            try
            {
                var sol = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));
                sol?.AdviseSolutionEvents(new SolutionEvents(this), out solutionCookie);
            }
            catch { }
        }

        sealed class SolutionEvents : IVsSolutionEvents
        {
            readonly ChatControl chat;
            public SolutionEvents(ChatControl c) { chat = c; }
            void Later(bool opened) => chat.Dispatcher.BeginInvoke(new Action(() => { try { chat.SolutionChanged(opened); } catch { } }));
            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) { Later(true); return VSConstants.S_OK; }
            public int OnAfterCloseSolution(object pUnkReserved) { Later(false); return VSConstants.S_OK; }
            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
        }
    }
}
