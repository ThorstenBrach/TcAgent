// The chat keeps its conversations per solution (src/ChatHistory.cs, linked): every question, answer, tool call, the plan of
// the agent and a stop are written in the background. When a solution opens, its latest conversation is shown again; the
// first question to a new agent session gets a short handover (what was asked, open tasks), and tc_chat_history lets the
// agent read more. A conversation without an event for 8 hours is finished - the next question starts a new file, the
// handover still names the old one. "New chat" starts a new conversation; the old one stays saved.
// chat-backends.json: "saveHistory": false switches it off. Stage 2: the id of the agent's own session and the answers to
// question cards are written too; a conversation that goes on continues that session (Claude Code --resume, ACP session/load)
// instead of the summary when it is still there; ⋯ > Earlier conversations reads, continues or deletes a saved one.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;   // the VS 15 shell (XAE Shell) also has Microsoft.VisualStudio.Shell.Task
using System.Windows;
using System.Windows.Controls;
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
            RecordSession();
        }

        // ------------------------------------------------------------------ the session of the agent (native resume)
        // The id of the agent's own session (Claude Code: session_id of the init event; ACP: the sessionId of session/new) goes
        // into the conversation, so a reopened project can continue that very session (--resume, session/load) instead of a
        // summary. A session that starts before the first question (warm-up) is written with that question.
        string agentSession, agentSessionBackend, recordedSession;
        string resumeSession;                    // the session to continue with the next start (LoadProjectConversation)

        void NoteSession(string id, Backend be)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (writesLock) { agentSession = id; agentSessionBackend = be?.Key; }
            RecordSession();
        }

        void RecordSession()
        {
            string id, backend;
            lock (writesLock)
            {
                if (agentSession == null || agentSession == recordedSession || convFile == null) return;
                id = agentSession; backend = agentSessionBackend; recordedSession = id;
            }
            Record("session", new Dictionary<string, object> { ["id"] = id, ["backend"] = backend });
        }

        /// <summary>Questions of a card and the user's answers (null: skipped) - one event per question.</summary>
        void RecordCards(Dictionary<string, object> input, Dictionary<string, object> answers)
        {
            var questions = (input != null && input.TryGetValue("questions", out var qs) ? qs as IEnumerable : null)?.OfType<IDictionary<string, object>>() ?? Enumerable.Empty<IDictionary<string, object>>();
            foreach (var q in questions)
            {
                var text = q.TryGetValue("question", out var qt) ? Convert.ToString(qt) : null;
                if (string.IsNullOrEmpty(text)) continue;
                var answer = answers != null && answers.TryGetValue(text, out var a) ? Convert.ToString(a) : null;
                Record("card", new Dictionary<string, object> { ["question"] = text, ["answer"] = answer ?? "(skipped)" });
            }
        }

        /// <summary>The session to continue for this backend, once: Claude Code only if its session file is still there.</summary>
        string TakeResume(Backend be)
        {
            var id = resumeSession;
            if (id == null || be == null || !Regex.IsMatch(id, @"^[\w-]{8,100}$")) return null;
            if (be.Format == "claude-stream" && !ClaudeSessionExists(id)) { resumeSession = null; return null; }
            resumeSession = null;
            return id;
        }

        static bool ClaudeSessionExists(string id)
        {
            try
            {
                var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var root = System.IO.Path.Combine(string.IsNullOrEmpty(cfg) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : cfg, "projects");
                return System.IO.Directory.Exists(root) && System.IO.Directory.EnumerateDirectories(root).Any(d => System.IO.File.Exists(System.IO.Path.Combine(d, id + ".jsonl")));
            }
            catch { return false; }
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
            lock (writesLock) { convFile = null; convDir = null; recordedSession = null; }
            handover = null;
            resumeSession = null;
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
            ShowConversation(c, dir, DateTime.Now - c.Last <= ChatHistory.IdleLimit, "Last conversation in this project");
        }

        /// <summary>Shows a saved conversation; goesOn: the next question continues it (and the agent's session, if it can).</summary>
        void ShowConversation(ChatHistory.Conversation c, string dir, bool goesOn, string what)
        {
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
            // the agent's own session of that conversation, continued natively with the next start if the backend is the same
            var se = c.Entries.LastOrDefault(e => e.Type == "session");
            var native = goesOn && se?.S("id") != null && se.S("backend") == Current?.Key && (Current?.Format == "claude-stream" || IsAcp(Current));
            if (goesOn)
            {
                history.Clear();
                history.AddRange(pairs.Where(p => !string.IsNullOrEmpty(p.Value)));
                lock (writesLock) { convFile = c.File; convDir = dir; convLast = c.Last; recordedSession = se?.S("id"); }
            }
            resumeSession = native ? se.S("id") : null;
            handover = c;
            var open = c.Plan().Count(i => i.Open);
            SystemLine($"{what}: {c.Started:dd.MM. HH:mm}, {c.Questions.Count} question{(c.Questions.Count == 1 ? "" : "s")}" +
                       (pairs.Count > shown.Count ? $" (the last {shown.Count} shown)" : "") +
                       (open > 0 ? $", {open} open task{(open == 1 ? "" : "s")}" : "") + (c.Unfinished ? ", stopped" : "") +
                       (goesOn ? " - the next question continues it. New chat starts fresh." : " - the agent gets a summary with the next question. New chat starts without it."));
            scroller.ScrollToEnd();
        }

        // ------------------------------------------------------------------ earlier conversations (⋯ menu)
        /// <summary>The saved conversations of this solution: read one, continue one (its agent session too, if it can), delete one.</summary>
        void ShowEarlierConversations()
        {
            var dir = SolutionDir();
            var files = ChatHistory.Files(dir);
            if (files.Count == 0) { status.Text = saveHistory ? "No saved conversations for this solution yet." : "The chat history is switched off (chat-backends.json: saveHistory)."; return; }
            var convs = files.Select(ChatHistory.Load).Where(x => x.Questions.Count > 0).ToList();
            var w = Dialog("Earlier conversations", 720, 480);
            var list = new ListBox { Margin = new Thickness(12, 10, 12, 6), BorderThickness = new Thickness(0) };
            list.SetResourceReference(BackgroundProperty, Microsoft.VisualStudio.PlatformUI.ThemedDialogColors.WindowPanelBrushKey);
            list.SetResourceReference(ForegroundProperty, Microsoft.VisualStudio.PlatformUI.ThemedDialogColors.WindowPanelTextBrushKey);
            string Line(ChatHistory.Conversation c)
            {
                var open = c.Plan().Count(i => i.Open);
                return $"{c.Started:dd.MM.yyyy HH:mm}   {c.Questions.Count} question{(c.Questions.Count == 1 ? "" : "s")}" + (open > 0 ? $", {open} open task{(open == 1 ? "" : "s")}" : "") +
                       (c.Unfinished ? ", stopped" : "") + (c.Backend != null ? "   " + c.Backend : "") + "\n" + c.Title;
            }
            void Fill() { list.ItemsSource = null; list.ItemsSource = convs.Select(c => new ListBoxItem { Content = new TextBlock { Text = Line(c), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) }, Tag = c }).ToList(); if (list.Items.Count > 0) list.SelectedIndex = 0; }
            Fill();
            ChatHistory.Conversation Sel() => (list.SelectedItem as ListBoxItem)?.Tag as ChatHistory.Conversation;
            var read = CardButton("Read");
            read.ToolTip = "The whole conversation as text: questions, answers, tool calls, answers to questions, the plan";
            read.Click += (s, e) => { if (Sel() is ChatHistory.Conversation c) TextDialog("Conversation " + System.IO.Path.GetFileNameWithoutExtension(c.File), ChatHistory.Format(c, full: true)); };
            var go = CardButton("Continue", true);
            go.ToolTip = "Shows it here; the next question continues it - with the agent's own session if it is still there, otherwise with a summary";
            go.Click += (s, e) =>
            {
                if (!(Sel() is ChatHistory.Conversation c)) return;
                if (busy) { status.Text = "Wait until the agent has answered, or stop it first."; return; }
                history.Clear(); msgs.Children.Clear(); cur = null; lock (toolCalls) toolCalls.Clear();
                ForgetConversation();
                KillSession();
                ShowConversation(c, dir, true, "Continued conversation");
                w.Close();
                if (IsVisible) WarmUp();
            };
            var del = CardButton("Delete …");
            del.Click += (s, e) =>
            {
                if (!(Sel() is ChatHistory.Conversation c)) return;
                if (MessageBox.Show($"Delete the conversation of {c.Started:dd.MM.yyyy HH:mm}?\n\n{c.Title}\n\nThis cannot be undone.", "TcAgent Chat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                lock (writesLock) { if (string.Equals(convFile, c.File, StringComparison.OrdinalIgnoreCase)) { writes.Wait(2000); convFile = null; } }
                try { System.IO.File.Delete(c.File); convs.Remove(c); Fill(); } catch (Exception ex) { status.Text = "Could not delete: " + ex.Message; }
            };
            list.MouseDoubleClick += (s, e) => read.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var close = CardButton("Close");
            close.IsCancel = true;
            close.Click += (s, e) => w.Close();
            var bar = new DockPanel { Margin = new Thickness(12, 4, 12, 10), LastChildFill = false };
            foreach (var x in new[] { go, read, del }) { DockPanel.SetDock(x, Dock.Left); x.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(x); }
            DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
            var hint = new TextBlock { Text = $"{convs.Count} saved conversation{(convs.Count == 1 ? "" : "s")} of this solution, newest first - kept {ChatHistory.KeepDays} days, at most {ChatHistory.KeepPerProject}.", Opacity = 0.7, Margin = new Thickness(12, 0, 12, 2), TextWrapping = TextWrapping.Wrap };
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            DockPanel.SetDock(hint, Dock.Bottom); root.Children.Add(hint);
            root.Children.Add(list);
            w.Content = root;
            w.ShowDialog();
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
