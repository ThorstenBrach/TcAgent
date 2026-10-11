// TcAgent Chat - Werkzeugfenster in Visual Studio/TwinCAT mit waehlbarem KI-Backend.
// Jede Anfrage startet die gewaehlte Agenten-CLI (Claude Code, GitHub Copilot CLI, Gemini CLI, OpenAI Codex CLI)
// ohne Oberflaeche, mit TcAgent als MCP-Werkzeugkasten. Der Editor-Kontext (Objekt, Teil, Zeile, Markierung)
// und ein gekuerzter Verlauf werden automatisch mitgegeben - damit funktioniert jede CLI gleich, ohne eigene Sitzungen.
// Konfiguration: %LOCALAPPDATA%\TcAgent\chat-backends.json (legt setup-clients.ps1 an; sonst Standardwerte).
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

    public sealed class ChatControl : UserControl
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
        static readonly string SettingsFile = Path.Combine(Dir, "chat-backends.json");

        readonly ComboBox backendBox = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
        readonly CheckBox buildBox = new CheckBox { Content = "Nach Aenderungen pruefen", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly CheckBox contextBox = new CheckBox { Content = "Editor-Kontext mitsenden", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBox log = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(0) };
        readonly TextBox input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        readonly Button send = new Button { Content = "Senden (Strg+Enter)", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        readonly Button stop = new Button { Content = "Stopp", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        readonly Button setup = new Button { Content = "Set up clients", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
                                             ToolTip = "Register TcAgent in Claude, Copilot, Gemini and Codex (setup-clients.ps1)" };
        readonly Button clear = new Button { Content = "Neu", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Verlauf leeren - naechste Anfrage ohne Vorgeschichte" };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Opacity = 0.75 };

        readonly List<Backend> backends = new List<Backend>();
        readonly List<KeyValuePair<string, string>> history = new List<KeyValuePair<string, string>>();
        int historyChars = 6000, preferred;
        Process proc;

        internal static ChatControl Instance;

        internal void Notice(string msg) => Append("\n[TcAgent] " + msg + "\n");

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

            var top = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(clear, Dock.Right);
            top.Children.Add(clear);
            if (Installer.IsVsixInstall) { DockPanel.SetDock(setup, Dock.Right); top.Children.Add(setup); }
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(backendBox); left.Children.Add(contextBox); left.Children.Add(buildBox);
            top.Children.Add(left);

            var bottom = new DockPanel { Margin = new Thickness(6) };
            var buttons = new StackPanel { Orientation = Orientation.Vertical };
            buttons.Children.Add(send); buttons.Children.Add(new Border { Height = 4 }); buttons.Children.Add(stop);
            DockPanel.SetDock(buttons, Dock.Right);
            DockPanel.SetDock(status, Dock.Bottom);
            bottom.Children.Add(buttons); bottom.Children.Add(status); bottom.Children.Add(input);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(new Border { Margin = new Thickness(6, 0, 6, 0), Child = log });
            Content = root;

            // VS-Farbschema (hell/dunkel) uebernehmen
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var tb in new[] { log, input })
            {
                tb.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                tb.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            }
            contextBox.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            buildBox.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            send.Click += (s, e) => Send();
            stop.Click += (s, e) => Stop();
            setup.Click += (s, e) =>
            {
                status.Text = "Richte KI-Clients ein ...";
                System.Threading.Tasks.Task.Run(() => Installer.Run(true, _ => { }, m => BridgePackage.Instance?.Notify(m)));
            };
            clear.Click += (s, e) => { history.Clear(); log.Clear(); logChars = 0; KillSession(); status.Text = "Neuer Verlauf."; WarmUp(); };
            // Claude-Sitzung vorab starten, sobald das Fenster sichtbar ist bzw. das Backend wechselt
            IsVisibleChanged += (s, e) => { if (IsVisible) WarmUp(); };
            backendBox.SelectionChanged += (s, e) => { if (IsVisible && !busy) WarmUp(); };
            Dispatcher.ShutdownStarted += (s, e) => KillSession();
            input.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Send(); }
                else if (e.Key == Key.Escape) { e.Handled = true; BridgePackage.Instance?.ReturnToEditor(); }
            };
            status.Text = File.Exists(SettingsFile) ? "Bereit." : "Standardeinstellungen - fuer alle Clients einmal setup-clients.ps1 ausfuehren.";
            Append("TcAgent Chat - Backend oben waehlen, Frage unten eingeben. Der Agent arbeitet ueber die TcAgent-Werkzeuge (tc_*) direkt in dieser TwinCAT-Solution.\n");
        }

        // ------------------------------------------------------------------ Einstellungen
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
            catch (Exception ex) { status.Text = "chat-backends.json fehlerhaft: " + ex.Message; }
            if (backends.Count == 0) backends.AddRange(defaults);
            preferred = Math.Max(0, backends.FindIndex(x => x.Key == def));
        }

        // ------------------------------------------------------------------ Senden
        // Ausgabe gepuffert schreiben: hoechstens alle 120 ms und mit niedriger Prioritaet, damit Tippen Vorrang hat.
        // Ein WPF-TextBox mit viel Text wird bei jedem AppendText neu umbrochen - daher auch die Laengenbegrenzung.
        readonly StringBuilder pending = new StringBuilder();
        System.Windows.Threading.DispatcherTimer flushTimer;
        int logChars;
        const int MaxLogChars = 120000;

        void Append(string text)
        {
            lock (pending) pending.Append(text);
            if (flushTimer == null)
            {
                flushTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(120) };
                flushTimer.Tick += (s, e) => FlushLog();
            }
            if (!flushTimer.IsEnabled) flushTimer.Start();
        }

        void FlushLog()
        {
            string t;
            lock (pending) { t = pending.ToString(); pending.Clear(); }
            flushTimer.Stop();
            if (t.Length == 0) return;
            logChars += t.Length;
            if (logChars > MaxLogChars)
            {
                var all = log.Text + t;
                var keep = all.Substring(all.Length - MaxLogChars / 2);
                var cut = keep.IndexOf("\n> Du ", StringComparison.Ordinal);   // an einer Frage abschneiden
                if (cut > 0) keep = keep.Substring(cut);
                log.Text = "(aeltere Ausgaben gekuerzt)\n" + keep;
                logChars = log.Text.Length;
            }
            else log.AppendText(t);
            log.ScrollToEnd();
        }

        // ------------------------------------------------------------------ Senden
        // Zwei Arten von Backends:
        //  * einmalig: pro Frage ein Prozess (Copilot, Gemini, Codex) - Verlauf wird im Prompt mitgeschickt
        //  * dauerhaft: Claude Code mit --input-format stream-json bleibt im Hintergrund offen; Folgefragen
        //    sparen Start, MCP-Verbindung und Verlauf (den haelt Claude selbst)
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
            // TcAgent arbeitet immer in DIESER IDE - kein Projektname noetig, auch wenn mehrere Instanzen offen sind
            psi.EnvironmentVariables["TCAGENT_VS_PID"] = Process.GetCurrentProcess().Id.ToString();
            psi.EnvironmentVariables.Remove("TCAGENT_SOLUTION");
            // Claude Code laedt MCP-Werkzeuge sonst erst per ToolSearch nach (je eine Extra-Runde); TcAgent hat wenige Werkzeuge
            if (!psi.EnvironmentVariables.ContainsKey("ENABLE_TOOL_SEARCH")) psi.EnvironmentVariables["ENABLE_TOOL_SEARCH"] = "false";
            return psi;
        }

        // Dauerhafte Sitzung starten (auch vorab, sobald das Fenster sichtbar ist -> erste Frage ohne Startzeit)
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
            if (s.Turn == null) return;                     // Ausgaben vor der ersten Frage (Start) nicht anzeigen
            if (final != null) s.Answer.AppendLine(final);
            if (sum != null) s.Summary = sum;
            if (show != null || stat != null)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { if (show != null) Append(show); if (stat != null) status.Text = stat; }));
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
            try { EnsureSession(be); status.Text = $"{be.Name} startet im Hintergrund ..."; }
            catch (Exception ex) { status.Text = $"{be.Name} konnte nicht vorab gestartet werden: {ex.Message}"; }
        }

        bool busy;

        internal bool IsBusy => busy;

        // Frage von aussen (Inline-Feld im Code-Fenster), Kontext wurde dort schon erfasst
        internal void SendExternal(string text, string ctx) => Send(text, ctx);

        // Name des Backends fuer das Inline-Feld - auch wenn das Chat-Fenster noch nicht existiert
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
            Append($"\n> Du ({be.Name}){(external != null ? " [aus dem Code]" : "")}:\n{text}\n");
            if (ctx != null) Append($"  [Kontext: {string.Join(" | ", ctx.Split('\n').Take(2))}]\n");
            if (external == null) input.Clear();
            busy = true; send.IsEnabled = false; stop.IsEnabled = true;
            try
            {
                if (IsPersistent(be)) await SendPersistent(be, text, ctx);
                else await SendOnce(be, text, ctx);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Append($"! {be.Name} konnte nicht gestartet werden: {ex.Message}\n  Ist die CLI installiert und angemeldet? Pfad ggf. in chat-backends.json eintragen.\n");
                status.Text = "Start fehlgeschlagen.";
            }
            catch (Exception ex) { Append("! " + ex.Message + "\n"); }
            finally { busy = false; send.IsEnabled = true; stop.IsEnabled = false; }
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
            status.Text = $"{be.Name} arbeitet ... ({(warm ? "Sitzung laeuft" : "Sitzung startet")})";
            Append($"\n< {be.Name}:\n");
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
                Append($"\n! {be.Name}-Sitzung beendet." + (err.Length > 0 ? "\n" + Tail(err, 2500) : "") + "\n");
                status.Text = "Sitzung beendet - naechste Frage startet neu.";
                if (sess == s) sess = null;
                return;
            }
            status.Text = $"Fertig in {s.TurnSw.Elapsed.TotalSeconds:0} s ({be.Name}, {(warm ? "laufende Sitzung" : "neu gestartet")})" + (s.Summary != null ? " - " + s.Summary : ".");
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
                status.Text = $"{be.Name} arbeitet ... ({Path.GetFileName(file)})";
                Append($"\n< {be.Name}:\n");
                // Prompt ueber stdin (UTF-8, unabhaengig von der Konsolen-Codepage)
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
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => Append(l + "\n")));
                            continue;
                        }
                        var (show, final, stat, sum, _) = ClaudeEvent(line, sw.Elapsed.TotalSeconds, answer.Length > 0);
                        if (final != null) answer.AppendLine(final);
                        if (sum != null) summary = sum;
                        if (show != null || stat != null)
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { if (show != null) Append(show); if (stat != null) status.Text = stat; }));
                    }
                });
                var errTask = Task.Run(() => { string line; while ((line = p.StandardError.ReadLine()) != null) lock (errors) errors.AppendLine(line); });
                await Task.Run(() => p.WaitForExit());
                await Task.WhenAll(outTask, errTask);
                var code = p.ExitCode;
                if (code != 0 || answer.Length == 0)
                {
                    var err = errors.ToString().Trim();
                    Append($"\n! {be.Name} beendet mit Code {code}." + (err.Length > 0 ? "\n" + Tail(err, 2500) : "") + "\n");
                }
                status.Text = $"Fertig in {sw.Elapsed.TotalSeconds:0} s ({be.Name})" + (summary != null ? " - " + summary : ".");
                if (answer.Length > 0) history.Add(new KeyValuePair<string, string>(text, answer.ToString().Trim()));
            }
            finally { proc = null; }
        }

        // Ein Ereignis aus "claude -p --output-format stream-json --verbose" auswerten.
        // Rueckgabe: anzuzeigender Text, Antworttext fuer den Verlauf, Statuszeile, Zusammenfassung, Antwort fertig.
        static (string show, string final, string stat, string sum, bool done) ClaudeEvent(string line, double t, bool haveText)
        {
            Dictionary<string, object> e;
            try { e = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(line); }
            catch { return (line + "\n", null, null, null, false); }   // kein JSON -> roh anzeigen
            if (e == null) return (null, null, null, null, false);
            var type = e.TryGetValue("type", out var ty) ? ty as string : null;
            if (type == "system" && (e.TryGetValue("subtype", out var st) ? st as string : null) == "init")
                return (null, null, $"Claude bereit nach {t:0.0} s - denkt ...", null, false);
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
                        show.Append($"  · {name}{arg}  [{t:0.0} s]\n");
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
                    sum = $"{turns} Schritte, davon Modell {api:0} s";
                }
                catch { }
                // Antworttext nur, falls er nicht schon ueber die assistant-Ereignisse kam
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
                KillSession();          // laufende Antwort abbrechen; die naechste Frage startet eine neue Sitzung
                Append("\n! abgebrochen (Sitzung neu gestartet)\n");
                return;
            }
            var p = proc;
            if (p == null) return;
            try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            Append("\n! abgebrochen\n");
        }

        string BuildPrompt(string text, string ctx, bool followUp)
        {
            var sb = new StringBuilder();
            var buildRule = buildBox.IsChecked == true
                ? "Nach Code-Aenderungen einmal am Ende mit tc_check auf Compilerfehler pruefen (nicht nach jedem Teilschritt); bei reinen Fragen nicht pruefen. tc_build nur, wenn der Nutzer es verlangt oder aktiviert werden soll."
                : "NICHT pruefen/bauen (kein tc_check, kein tc_build) - der Nutzer macht das selbst. Aenderungen nur schreiben und mit tc_save speichern.";
            if (followUp)
            {
                // laufende Sitzung: Anleitung und Verlauf kennt Claude schon
                sb.AppendLine("[Hinweis] " + buildRule);
                if (ctx != null) sb.AppendLine("\n[Editor-Kontext des Nutzers]\n" + ctx);
                sb.AppendLine("\n[Anfrage]\n" + text);
                return sb.ToString();
            }
            sb.AppendLine("Du arbeitest in einem TwinCAT-3-Projekt (Visual Studio). Nutze dafuer die MCP-Werkzeuge des Servers \"tcagent\" (tc_*, ads_*) " +
                          "und halte dich an die TcAgent-Anleitung (Skill twincat-tcagent bzw. AGENTS.md/GEMINI.md/copilot-instructions.md). " +
                          "Antworte auf Deutsch und knapp; nenne geaenderte Objekte mit Baumpfad. Vor dem ersten Schreiben von Code einmal tc_coding_rules aufrufen und einhalten. " + buildRule);
            if (ctx != null) sb.AppendLine("\n[Editor-Kontext des Nutzers]\n" + ctx);
            if (history.Count > 0)
            {
                var h = new StringBuilder();
                foreach (var kv in Enumerable.Reverse(history))
                {
                    var entry = $"Nutzer: {kv.Key}\nAssistent: {kv.Value}\n";
                    if (h.Length + entry.Length > historyChars) break;
                    h.Insert(0, entry);
                }
                sb.AppendLine("\n[Bisheriger Verlauf, gekuerzt]\n" + h);
            }
            sb.AppendLine("\n[Anfrage]\n" + text);
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
                // Qualifizierter Name aus dem Dokumentpfad: "...\\FB.TcPOU@Methode" -> FB.Methode
                string obj = null;
                if (c.TryGetValue("document", out var dobj) && dobj is string doc && doc.Length > 0)
                {
                    var at = doc.LastIndexOf('@');
                    obj = at > 0 ? Path.GetFileNameWithoutExtension(doc.Substring(0, at)) + "." + doc.Substring(at + 1)
                                 : Path.GetFileNameWithoutExtension(doc);
                }
                else if (c.TryGetValue("frameCaption", out var cap)) obj = cap as string;
                sb.Append("Offenes Objekt: ").Append(obj ?? "?");
                if (c.TryGetValue("active", out var act) && act is Dictionary<string, object> a)
                {
                    sb.Append($"\nCursor: {a["part"]}, Zeile {a["line"]}, Spalte {a["column"]}");
                    if (a.TryGetValue("selectedText", out var st) && st is string s && s.Length > 0)
                        sb.Append("\nMarkiert:\n").Append(s.Length > 3000 ? s.Substring(0, 3000) + "..." : s);
                    if (a.TryGetValue("excerpt", out var ex) && ex is string exs && exs.Length > 0)
                        sb.Append($"\nCode um den Cursor ({a["part"]}, > = Cursorzeile):\n").Append(exs);
                }
                sb.Append("\n(Dieser Kontext ist aktuell - tc_editor_context nur aufrufen, wenn mehr gebraucht wird, z. B. der Baumpfad fuer tc_plc_write.)");
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

        // ------------------------------------------------------------------ Programm finden (auch npm-Shims .cmd -> node + Skript)
        static (string file, string prefix) Resolve(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) throw new Exception("Kein Programm konfiguriert");
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
                    // npm-Shim: ... "%dp0%\node_modules\<paket>\<datei>.js" %*  -> direkt mit node starten (kein cmd-Quoting)
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
            throw new System.ComponentModel.Win32Exception(2, $"'{exe}' nicht gefunden (PATH, %APPDATA%\\npm, ~\\.local\\bin)");
        }

        // Windows-Argument-Quoting (Regeln von CommandLineToArgvW)
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
