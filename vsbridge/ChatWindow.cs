// TcAgent Chat - tool window in Visual Studio/TwinCAT with a selectable AI backend.
// Each request starts the selected agent CLI (Claude Code, GitHub Copilot CLI, Gemini CLI, OpenAI Codex CLI)
// headless, with TcAgent as its MCP toolbox. The editor context (object, part, line, selection)
// and a truncated history are passed along automatically - so every CLI works the same, without its own sessions.
// Configuration: %LOCALAPPDATA%\TcAgent\chat-backends.json (created by setup-clients.ps1; otherwise defaults).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;   // the VS 15 shell (XAE Shell) also has Microsoft.VisualStudio.Shell.Task
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace TcAgentBridge
{
    [Guid("e5e9685c-14eb-4e4d-b470-61f48dba2b20")]
    public sealed class ChatPane : ToolWindowPane
    {
        public ChatPane() : base(null)
        {
            Caption = "TcAgent Chat";
            Content = new ChatControl();
        }
    }

    sealed class Backend
    {
        public string Key, Name, Exe, Args, Format;
        public bool Stdin;
    }

    public sealed partial class ChatControl : UserControl
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
        static readonly string SettingsFile = Path.Combine(Dir, "chat-backends.json");

        readonly ComboBox backendBox = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
        readonly CheckBox buildBox = new CheckBox { Content = "Check after changes", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly CheckBox contextBox = new CheckBox { Content = "Send editor context", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBox input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly Button send = new Button { Content = "Send", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        readonly Button stop = new Button { Content = "Stop", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        readonly Button setup = new Button { Content = "Set up clients", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
                                             ToolTip = "Register TcAgent in Claude, Copilot, Gemini and Codex (setup-clients.ps1)" };
        readonly Button clear = new Button { Content = "New chat", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Clear history - next request starts without previous conversation" };
        readonly Button tests = new Button { Content = "Tests", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = "TcUnit Tests window: run and watch the TcUnit tests of this solution" };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Opacity = 0.75 };
        readonly TextBlock versionLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), FontSize = 11, Opacity = 0.7 };

        // TcAgent version in the top bar: the server the chat starts (mcp-claude.json) and the extension; a mismatch is
        // shown in orange - the chat then still talks to the old TcAgent.exe until "Set up clients" / a VS 2022 start
        internal void UpdateVersionLabel()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateVersionLabel)); return; }
            var ext = BridgePackage.BridgeVersion;
            var srv = Installer.RegisteredServerVersion();
            if (srv == null)
            {
                versionLabel.Text = "TcAgent " + ext + " (server not set up)";
                versionLabel.ToolTip = "No TcAgent server registered for the chat yet - click \"Set up clients\" (or start Visual Studio 2022 once after installing the VSIX).";
                versionLabel.ClearValue(TextBlock.ForegroundProperty); versionLabel.Opacity = 0.9;
            }
            else if (srv == ext)
            {
                versionLabel.Text = "TcAgent " + ext;
                versionLabel.ToolTip = "TcAgent server and extension " + ext;
                versionLabel.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey); versionLabel.Opacity = 0.7;
            }
            else
            {
                versionLabel.Text = "TcAgent server " + srv + " \u2260 extension " + ext;
                versionLabel.ToolTip = "The chat still starts TcAgent " + srv + ". Click \"Set up clients\" (or start Visual Studio 2022 once after installing the VSIX), then \"New chat\".";
                versionLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x7B, 0x00)); versionLabel.Opacity = 1;
            }
        }

        // the backend chosen last is the default next time (chat-backends.json "default")
        void SaveDefaultBackend(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                var ser = new JavaScriptSerializer();
                // no file yet: setup-clients.ps1 writes the full template later - do not create a partial one before it
                if (!File.Exists(SettingsFile)) return;
                var js = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                if (js.TryGetValue("default", out var d) && d as string == key) return;
                js["default"] = key;
                Directory.CreateDirectory(Dir);
                File.WriteAllText(SettingsFile, Indent(ser.Serialize(js)), new UTF8Encoding(false));
            }
            catch (Exception ex) { status.Text = "Could not remember the backend: " + ex.Message; }
        }

        // JavaScriptSerializer writes one line - keep chat-backends.json readable for manual editing
        static string Indent(string json)
        {
            var sb = new StringBuilder(); int depth = 0; bool str = false, esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                var ch = json[i];
                if (str)
                {
                    // inside a string: copy verbatim, track escapes (a string may end with an escaped backslash: "C:\\")
                    sb.Append(ch);
                    if (esc) esc = false; else if (ch == '\\') esc = true; else if (ch == '"') str = false;
                    continue;
                }
                if (ch == '"') { str = true; sb.Append(ch); continue; }
                switch (ch)
                {
                    case '{': case '[': sb.Append(ch).Append('\n').Append(' ', ++depth * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --depth * 2).Append(ch); break;
                    case ',': sb.Append(ch).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        readonly List<Backend> backends = new List<Backend>();
        readonly List<KeyValuePair<string, string>> history = new List<KeyValuePair<string, string>>();
        int historyChars = 6000, preferred;
        Process proc;

        internal static ChatControl Instance;

        internal void Notice(string msg)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Notice(msg))); return; }
            SystemLine("[TcAgent] " + msg);
            UpdateVersionLabel();      // e.g. after the automatic setup at startup ("TcAgent x registered")
        }

        internal void FocusInput()
        {
            input.Focus();
            Keyboard.Focus(input);
            input.CaretIndex = input.Text.Length;
        }

        public ChatControl()
        {
            Instance = this;
            LoadSettings();
            foreach (var b in backends) backendBox.Items.Add(new ComboBoxItem { Content = b.Name, Tag = b });
            if (backendBox.Items.Count > 0) backendBox.SelectedIndex = Math.Min(preferred, backendBox.Items.Count - 1);

            Content = BuildView();

            // Adopt the VS color theme (light/dark)
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ThemeView();
            contextBox.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            buildBox.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            send.Click += (s, e) => Send();
            stop.Click += (s, e) => Stop();
            setup.Click += (s, e) =>
            {
                status.Text = "Setting up AI clients ...";
                System.Threading.Tasks.Task.Run(() => { Installer.Run(true, _ => { }, m => BridgePackage.Instance?.Notify(m)); UpdateVersionLabel(); });
            };
            tests.Click += (s, e) =>
            {
                try { BridgePackage.Instance?.ShowTcUnit(true); } catch (Exception ex) { status.Text = "TcUnit window: " + ex.Message; }
            };
            clear.Click += (s, e) => { history.Clear(); msgs.Children.Clear(); cur = null; KillSession(); UpdateVersionLabel(); status.Text = "New conversation."; WarmUp(); };
            // Pre-start the Claude session as soon as the window becomes visible or the backend changes
            IsVisibleChanged += (s, e) => { if (IsVisible) WarmUp(); };
            backendBox.SelectionChanged += (s, e) => { SaveDefaultBackend(Current?.Key); if (IsVisible && !busy) WarmUp(); };
            Dispatcher.ShutdownStarted += (s, e) => KillSession();
            input.PreviewKeyDown += (s, e) =>
            {
                if (InputKey(e)) e.Handled = true;
                else if (e.Key == Key.Escape) { e.Handled = true; BridgePackage.Instance?.ReturnToEditor(); }
            };
            status.Text = File.Exists(SettingsFile) ? "Ready." : "Default settings - run setup-clients.ps1 once to set up all clients.";
            SystemLine("TcAgent Chat - choose a backend above, enter your question below. The agent works directly in this TwinCAT solution through the TcAgent tools (tc_*). " +
                       "Paste or drop documents (manuals, data sheets, screenshots) into the input box to give them to the agent.");
        }

        // ------------------------------------------------------------------ Settings
        void LoadSettings()
        {
            var defaults = new[]
            {
                new Backend { Key = "claude", Name = "Claude (Claude Code)", Exe = "claude", Args = "-p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config --mcp-config {mcpClaude} --allowedTools mcp__tcagent WebSearch", Stdin = true, Format = "claude-stream" },
                new Backend { Key = "copilot", Name = "GitHub Copilot (CLI)", Exe = "copilot", Args = "-p {prompt} --allow-tool tcagent --additional-mcp-config @{mcpCopilot}", Stdin = false },
                new Backend { Key = "gemini", Name = "Gemini (Gemini CLI)", Exe = "gemini", Args = "--allowed-mcp-server-names tcagent", Stdin = true },
                new Backend { Key = "codex", Name = "ChatGPT (Codex CLI)", Exe = "codex", Args = "exec --skip-git-repo-check -", Stdin = true },
            };
            string def = null;
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var js = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                    def = js.TryGetValue("default", out var d) ? d as string : null;
                    if (js.TryGetValue("historyChars", out var hc) && hc is int h) historyChars = h;
                    if (js.TryGetValue("sendContext", out var sc) && sc is bool b0) contextBox.IsChecked = b0;
                    if (js.TryGetValue("buildAfterChange", out var ba) && ba is bool b1) buildBox.IsChecked = b1;
                    if (js.TryGetValue("backends", out var bo) && bo is Dictionary<string, object> bs)
                        foreach (var kv in bs)
                            if (kv.Value is Dictionary<string, object> v)
                                backends.Add(new Backend
                                {
                                    Key = kv.Key, Name = v.TryGetValue("name", out var n) ? n as string : kv.Key,
                                    Exe = v.TryGetValue("exe", out var x) ? x as string : kv.Key, Args = v.TryGetValue("args", out var a) ? a as string : "",
                                    Stdin = v.TryGetValue("stdin", out var si) && si is bool sb && sb,
                                    Format = v.TryGetValue("format", out var fm) ? fm as string : null
                                });
                }
            }
            catch (Exception ex) { status.Text = "chat-backends.json is invalid: " + ex.Message; }
            if (backends.Count == 0) backends.AddRange(defaults);
            preferred = Math.Max(0, backends.FindIndex(x => x.Key == def));
        }

        // ------------------------------------------------------------------ Send
        // Two kinds of backends:
        //  * one-shot: one process per question (Copilot, Gemini, Codex) - the history is sent along in the prompt
        //  * persistent: Claude Code with --input-format stream-json stays open in the background; follow-up questions
        //    save the startup, MCP connection and history (Claude keeps the history itself)
        static bool IsPersistent(Backend be) => be.Format == "claude-stream" && (be.Args ?? "").Contains("--input-format stream-json");

        sealed class Session
        {
            public Process P; public Backend Be; public Stream In;
            public readonly StringBuilder Answer = new StringBuilder(), Err = new StringBuilder();
            public TaskCompletionSource<string> Turn; public Stopwatch TurnSw; public string Summary;
            public bool Exited, Greeted; public readonly Stopwatch Up = Stopwatch.StartNew();
        }
        Session sess;

        Backend Current => (backendBox.SelectedItem as ComboBoxItem)?.Tag as Backend;

        (string file, string args) CommandLine(Backend be, string prompt)
        {
            var a = be.Args ?? "";
            a = a.Replace("{mcpClaude}", Quote(Path.Combine(Dir, "mcp-claude.json")))
                 .Replace("{mcpCopilot}", Quote(Path.Combine(Dir, "mcp-copilot.json")))
                 .Replace("{prompt}", Quote(prompt ?? ""));
            // attachments (PDF, images) are read by Claude Code with its Read tool from the attachments folder
            if ((be.Exe ?? "").IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 && a.Contains("--allowedTools"))
            {
                if (!Regex.IsMatch(a, @"--allowedTools\s[^-]*\bRead\b")) a = a.Replace("--allowedTools ", "--allowedTools Read ");
                if (!a.Contains("--add-dir")) { Directory.CreateDirectory(AttachDir); a += " --add-dir " + Quote(AttachDir); }
            }
            var (f, prefix) = Resolve(be.Exe);
            return (f, (prefix + " " + a).Trim());
        }

        static ProcessStartInfo Psi(string file, string args, Backend be)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = SolutionDir() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            psi.EnvironmentVariables["TCAGENT_CLIENT"] = "chat:" + be.Key;
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            // TcAgent always works in THIS IDE - no project name needed, even when several instances are open
            psi.EnvironmentVariables["TCAGENT_VS_PID"] = Process.GetCurrentProcess().Id.ToString();
            psi.EnvironmentVariables.Remove("TCAGENT_SOLUTION");
            // Otherwise Claude Code loads MCP tools lazily via ToolSearch (one extra round each); TcAgent has few tools
            if (!psi.EnvironmentVariables.ContainsKey("ENABLE_TOOL_SEARCH")) psi.EnvironmentVariables["ENABLE_TOOL_SEARCH"] = "false";
            return psi;
        }

        // Start the persistent session (also in advance once the window is visible -> first question without startup time)
        Session EnsureSession(Backend be)
        {
            if (sess != null && !sess.Exited && sess.Be == be) return sess;
            KillSession();
            var (file, args) = CommandLine(be, null);
            var s = new Session { Be = be };
            s.P = Process.Start(Psi(file, args, be));
            s.In = s.P.StandardInput.BaseStream;
            Task.Run(() =>
            {
                string line;
                try { while ((line = s.P.StandardOutput.ReadLine()) != null) OnSessionLine(s, line); } catch { }
                s.Exited = true;
                s.Turn?.TrySetResult(null);
            });
            Task.Run(() => { string line; try { while ((line = s.P.StandardError.ReadLine()) != null) lock (s.Err) s.Err.AppendLine(line); } catch { } });
            sess = s;
            return s;
        }

        void OnSessionLine(Session s, string line)
        {
            var t = s.TurnSw?.Elapsed.TotalSeconds ?? 0;
            var (show, final, stat, sum, done) = ClaudeEvent(line, t, s.Answer.Length > 0);
            if (s.Turn == null) return;                     // do not show output before the first question (startup)
            if (final != null) s.Answer.AppendLine(final);
            if (sum != null) s.Summary = sum;
            if (show != null) AgentAppend(show);
            if (stat != null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => status.Text = stat));
            if (done) s.Turn.TrySetResult(s.Answer.ToString());
        }

        void KillSession()
        {
            var s = sess; sess = null;
            if (s == null) return;
            s.Turn?.TrySetResult(null);
            if (s.Exited) return;
            try { s.In.Close(); } catch { }
            try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {s.P.Id}") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }

        void WarmUp()
        {
            var be = Current;
            if (be == null || !IsPersistent(be) || (sess != null && !sess.Exited && sess.Be == be)) return;
            try { EnsureSession(be); status.Text = $"{be.Name} is starting in the background ..."; }
            catch (Exception ex) { status.Text = $"{be.Name} could not be pre-started: {ex.Message}"; }
        }

        bool busy;

        internal bool IsBusy => busy;

        // Question from outside (inline box in the code window); the context was already captured there
        internal void SendExternal(string text, string ctx) => Send(text, ctx);

        // Backend name for the inline box - even if the chat window does not exist yet
        internal static string DefaultBackendName()
        {
            if (Instance?.Current != null) return Instance.Current.Name;
            try
            {
                var js = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                var def = js.TryGetValue("default", out var d) ? d as string : "claude";
                if (js.TryGetValue("backends", out var bo) && bo is Dictionary<string, object> bs && bs.TryGetValue(def ?? "", out var b)
                    && b is Dictionary<string, object> bd && bd.TryGetValue("name", out var n)) return n as string;
            }
            catch { }
            return "Claude (Claude Code)";
        }

        async void Send(string external = null, string externalCtx = null)
        {
            if (busy) return;
            var text = (external ?? input.Text).Trim();
            if (text.Length == 0) return;
            var be = Current;
            if (be == null) return;

            string ctx = external != null ? (contextBox.IsChecked == true ? externalCtx : null)
                                          : (contextBox.IsChecked == true ? EditorContext() : null);
            var att = external == null ? TakeAttachments() : new List<Attachment>();
            AddUserBubble(text, ctx, att, external != null);
            if (external == null) { RememberPrompt(text); input.Clear(); }
            var docs = AttachmentPrompt(att);
            if (docs != null) ctx = (ctx != null ? ctx + "\n\n" : "") + docs;
            busy = true; send.IsEnabled = false; stop.IsEnabled = true;
            try
            {
                if (IsPersistent(be)) await SendPersistent(be, text, ctx);
                else await SendOnce(be, text, ctx);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                SystemLine($"{be.Name} could not be started: {ex.Message}\nIs the CLI installed and signed in? If needed, enter its path in chat-backends.json.", true);
                status.Text = "Start failed.";
            }
            catch (Exception ex) { SystemLine(ex.Message, true); }
            finally { FlushAgent(); cur = null; busy = false; send.IsEnabled = true; stop.IsEnabled = false; }
        }

        async Task SendPersistent(Backend be, string text, string ctx)
        {
            var warm = sess != null && !sess.Exited && sess.Be == be;
            var s = EnsureSession(be);
            s.Answer.Clear(); s.Summary = null;
            s.TurnSw = Stopwatch.StartNew();
            s.Turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = BuildPrompt(text, ctx, followUp: s.Greeted);
            s.Greeted = true;
            status.Text = $"{be.Name} is working ... ({(warm ? "session running" : "session starting")})";
            StartAgent(be.Name);
            var msg = new Dictionary<string, object>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt } }
                }
            };
            var bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(msg) + "\n");
            await s.In.WriteAsync(bytes, 0, bytes.Length);
            await s.In.FlushAsync();
            var answer = await s.Turn.Task;
            s.Turn = null;
            if (answer == null)
            {
                string err; lock (s.Err) err = s.Err.ToString().Trim();
                FlushAgent();
                SystemLine($"{be.Name} session ended." + (err.Length > 0 ? "\n" + Tail(err, 2500) : ""), true);
                status.Text = "Session ended - the next question starts a new one.";
                if (sess == s) sess = null;
                return;
            }
            status.Text = $"Done in {s.TurnSw.Elapsed.TotalSeconds:0} s ({be.Name}, {(warm ? "running session" : "newly started")})" + (s.Summary != null ? " - " + s.Summary : ".");
            if (answer.Length > 0) history.Add(new KeyValuePair<string, string>(text, answer.Trim()));
        }

        async Task SendOnce(Backend be, string text, string ctx)
        {
            var prompt = BuildPrompt(text, ctx, followUp: false);
            var (file, args) = CommandLine(be, prompt);
            var answer = new StringBuilder();
            var errors = new StringBuilder();
            var sw = Stopwatch.StartNew();
            proc = Process.Start(Psi(file, args, be));
            try
            {
                status.Text = $"{be.Name} is working ... ({Path.GetFileName(file)})";
                StartAgent(be.Name);
                // Prompt via stdin (UTF-8, independent of the console code page)
                var stdin = proc.StandardInput.BaseStream;
                if (be.Stdin) { var bytes = new UTF8Encoding(false).GetBytes(prompt); await stdin.WriteAsync(bytes, 0, bytes.Length); }
                stdin.Close();
                var p = proc;
                bool stream = be.Format == "claude-stream";
                string summary = null;
                var outTask = Task.Run(() =>
                {
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        if (!stream)
                        {
                            var l = line;
                            answer.AppendLine(l);
                            AgentAppend(l + "\n");
                            continue;
                        }
                        var (show, final, stat, sum, _) = ClaudeEvent(line, sw.Elapsed.TotalSeconds, answer.Length > 0);
                        if (final != null) answer.AppendLine(final);
                        if (sum != null) summary = sum;
                        if (show != null) AgentAppend(show);
                        if (stat != null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => status.Text = stat));
                    }
                });
                var errTask = Task.Run(() => { string line; while ((line = p.StandardError.ReadLine()) != null) lock (errors) errors.AppendLine(line); });
                await Task.Run(() => p.WaitForExit());
                await Task.WhenAll(outTask, errTask);
                var code = p.ExitCode;
                if (code != 0 || answer.Length == 0)
                {
                    var err = errors.ToString().Trim();
                    FlushAgent();
                    SystemLine($"{be.Name} exited with code {code}." + (err.Length > 0 ? "\n" + Tail(err, 2500) : ""), true);
                }
                status.Text = $"Done in {sw.Elapsed.TotalSeconds:0} s ({be.Name})" + (summary != null ? " - " + summary : ".");
                if (answer.Length > 0) history.Add(new KeyValuePair<string, string>(text, answer.ToString().Trim()));
            }
            finally { proc = null; }
        }

        // Evaluate one event from "claude -p --output-format stream-json --verbose".
        // Returns: text to display, answer text for the history, status line, summary, answer complete.
        static (string show, string final, string stat, string sum, bool done) ClaudeEvent(string line, double t, bool haveText)
        {
            Dictionary<string, object> e;
            try { e = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(line); }
            catch { return (line + "\n", null, null, null, false); }   // not JSON -> show raw
            if (e == null) return (null, null, null, null, false);
            var type = e.TryGetValue("type", out var ty) ? ty as string : null;
            if (type == "system" && (e.TryGetValue("subtype", out var st) ? st as string : null) == "init")
                return (null, null, $"Claude ready after {t:0.0} s - thinking ...", null, false);
            if (type == "assistant" && e.TryGetValue("message", out var mo) && mo is Dictionary<string, object> m
                && m.TryGetValue("content", out var co) && co is IEnumerable items)
            {
                var show = new StringBuilder(); var final = new StringBuilder(); string stat = null;
                foreach (var it in items)
                {
                    if (!(it is Dictionary<string, object> c)) continue;
                    var ct = c.TryGetValue("type", out var x) ? x as string : null;
                    if (ct == "text" && c.TryGetValue("text", out var tx) && tx is string s && s.Trim().Length > 0)
                    { show.Append(s.TrimEnd()).Append('\n'); final.Append(s.TrimEnd()).Append('\n'); }
                    else if (ct == "tool_use")
                    {
                        var name = (c.TryGetValue("name", out var nm) ? nm as string : "?") ?? "?";
                        name = Regex.Replace(name, "^mcp__[^_]+(?:_[^_]+)*?__", "");
                        var arg = "";
                        if (c.TryGetValue("input", out var inp) && inp is Dictionary<string, object> d && d.Count > 0)
                            arg = " " + string.Join(", ", d.Select(kv => kv.Key + "=" + Short(Convert.ToString(kv.Value), 60)));
                        show.Append($"{ToolMark}{name}{arg}  [{t:0.0} s]\n");
                        stat = $"{name} ... ({t:0} s)";
                    }
                }
                return (show.Length > 0 ? show.ToString() : null, final.Length > 0 ? final.ToString() : null, stat, null, false);
            }
            if (type == "result")
            {
                string sum = null;
                try
                {
                    var turns = e.TryGetValue("num_turns", out var nt) ? Convert.ToInt32(nt) : 0;
                    var api = e.TryGetValue("duration_api_ms", out var da) ? Convert.ToDouble(da) / 1000 : 0;
                    sum = $"{turns} steps, model time {api:0} s";
                }
                catch { }
                // answer text only if it did not already arrive via the assistant events
                var res = e.TryGetValue("result", out var r) ? r as string : null;
                if (!haveText && !string.IsNullOrWhiteSpace(res)) return (res.TrimEnd() + "\n", res, null, sum, true);
                return (null, null, null, sum, true);
            }
            return (null, null, null, null, false);
        }

        static string Short(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length <= n ? s : s.Substring(0, n) + "..."; }

        void Stop()
        {
            if (sess != null && sess.Turn != null)
            {
                KillSession();          // abort the running answer; the next question starts a new session
                SystemLine("cancelled (session restarted)", true);
                return;
            }
            var p = proc;
            if (p == null) return;
            try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            SystemLine("cancelled", true);
        }

        string BuildPrompt(string text, string ctx, bool followUp)
        {
            var sb = new StringBuilder();
            var buildRule = buildBox.IsChecked == true
                ? "After code changes, check for compiler errors once at the end with tc_check (not after every intermediate step); do not check for pure questions. Use tc_build only if the user asks for it or the project is to be activated."
                : "Do NOT check/build (no tc_check, no tc_build) - the user does that. Only write changes and save them with tc_save.";
            if (followUp)
            {
                // running session: Claude already knows the instructions and history
                sb.AppendLine("[Note] " + buildRule);
                if (ctx != null) sb.AppendLine("\n[User's editor context]\n" + ctx);
                sb.AppendLine("\n[Request]\n" + text);
                return sb.ToString();
            }
            sb.AppendLine("You are working in a TwinCAT 3 project (Visual Studio). Use the MCP tools of the \"tcagent\" server (tc_*, ads_*) " +
                          "and follow the TcAgent guide (skill twincat-tcagent or AGENTS.md/GEMINI.md/copilot-instructions.md). " +
                          "Answer concisely, in the language of the user's request; name changed objects with their tree path. Before writing code for the first time, call tc_coding_rules once and follow the rules. " + buildRule);
            if (ctx != null) sb.AppendLine("\n[User's editor context]\n" + ctx);
            if (history.Count > 0)
            {
                var h = new StringBuilder();
                foreach (var kv in Enumerable.Reverse(history))
                {
                    var entry = $"User: {kv.Key}\nAssistant: {kv.Value}\n";
                    if (h.Length + entry.Length > historyChars) break;
                    h.Insert(0, entry);
                }
                sb.AppendLine("\n[Previous conversation, truncated]\n" + h);
            }
            sb.AppendLine("\n[Request]\n" + text);
            return sb.ToString();
        }

        string EditorContext()
        {
            try { return FormatContext(BridgePackage.Instance?.ContextForChat()); }
            catch { return null; }
        }

        internal static string FormatContext(Dictionary<string, object> c)
        {
            try
            {
                if (c == null) return null;
                var sb = new StringBuilder();
                // Qualified name from the document path: "...\\FB.TcPOU@Method" -> FB.Method
                string obj = null;
                if (c.TryGetValue("document", out var dobj) && dobj is string doc && doc.Length > 0)
                {
                    var at = doc.LastIndexOf('@');
                    obj = at > 0 ? Path.GetFileNameWithoutExtension(doc.Substring(0, at)) + "." + doc.Substring(at + 1)
                                 : Path.GetFileNameWithoutExtension(doc);
                }
                else if (c.TryGetValue("frameCaption", out var cap)) obj = cap as string;
                sb.Append("Open object: ").Append(obj ?? "?");
                if (c.TryGetValue("active", out var act) && act is Dictionary<string, object> a)
                {
                    sb.Append($"\nCursor: {a["part"]}, line {a["line"]}, column {a["column"]}");
                    if (a.TryGetValue("selectedText", out var st) && st is string s && s.Length > 0)
                        sb.Append("\nSelected:\n").Append(s.Length > 3000 ? s.Substring(0, 3000) + "..." : s);
                    if (a.TryGetValue("excerpt", out var ex) && ex is string exs && exs.Length > 0)
                        sb.Append($"\nCode around the cursor ({a["part"]}, > = cursor line):\n").Append(exs);
                }
                sb.Append("\n(This context is current - call tc_editor_context only if more is needed, e.g. the tree path for tc_plc_write.)");
                return sb.ToString();
            }
            catch { return null; }
        }

        static string SolutionDir()
        {
            try
            {
                var sol = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));
                sol.GetSolutionInfo(out var dir, out _, out _);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        static string Tail(string s, int n) => s.Length <= n ? s : "..." + s.Substring(s.Length - n);

        // ------------------------------------------------------------------ Locate the program (including npm shims .cmd -> node + script)
        static (string file, string prefix) Resolve(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) throw new Exception("No program configured");
            var candidates = new List<string>();
            if (Path.IsPathRooted(exe)) candidates.Add(exe);
            else
            {
                var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Length > 0).ToList();
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
                foreach (var d in dirs)
                    foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" })
                        candidates.Add(Path.Combine(d.Trim('"'), exe + ext));
            }
            foreach (var c in candidates)
            {
                if (!File.Exists(c)) continue;
                var ext = Path.GetExtension(c).ToLowerInvariant();
                if (ext == ".exe") return (c, "");
                if (ext == ".cmd" || ext == ".bat")
                {
                    // npm shim: ... "%dp0%\node_modules\<package>\<file>.js" %*  -> start directly with node (no cmd quoting)
                    var m = Regex.Match(File.ReadAllText(c), @"%(?:~)?dp0%?\\?(?<p>node_modules\\[^""%]+?\.(?:js|mjs|cjs))", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        var dir = Path.GetDirectoryName(c);
                        var js = Path.Combine(dir, m.Groups["p"].Value);
                        var node = File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : Resolve("node").file;
                        return (node, Quote(js));
                    }
                    return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c " + Quote(c));
                }
            }
            throw new System.ComponentModel.Win32Exception(2, $"'{exe}' not found (PATH, %APPDATA%\\npm, ~\\.local\\bin)");
        }

        // Windows argument quoting (CommandLineToArgvW rules)
        static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (var ch in s)
            {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(ch); bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }
    }
}
