// TcAgentBridge - VS 2022 extension: bridge between TcAgent (MCP server, separate process) and the IDE process.
// Runs inside the Visual Studio process and can therefore reach what is not accessible via DTE/Automation Interface:
// - Cursor and selection in the TwinCAT editor
// - Signatures from (compiled) libraries via the IDE
// Protocol: named pipe "TcAgentBridge.<pid>", one JSON line in and one JSON line out per connection.
// All IDE access runs on the UI thread.
// Log and output: %LOCALAPPDATA%\TcAgent\bridge\ ; library cache: %LOCALAPPDATA%\TcAgent\libcache\
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace TcAgentBridge
{
    [ComVisible(true)]
    [Guid("86c8609c-4aac-4e7e-95e6-6b96179a3c4f")]
    public sealed class BridgePackage : AsyncPackage
    {
        public const string BridgeVersion = "0.26.1";
        internal static BridgePackage Instance;
        static readonly Guid ExportTypeGuid = new Guid("75161BEE-FB5F-434C-A3C6-86B7CEE2B8FC");

        string dir;
        System.Windows.Threading.Dispatcher ui;
        Thread server;
        volatile bool stop;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        protected override System.Threading.Tasks.Task InitializeAsync(CancellationToken token, IProgress<ServiceProgressData> progress)
        {
            try
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "bridge");
                Directory.CreateDirectory(dir);
                ui = System.Windows.Application.Current?.Dispatcher;
                Log($"==== TcAgentBridge {BridgeVersion} started, pid={System.Diagnostics.Process.GetCurrentProcess().Id}, 64bit={Environment.Is64BitProcess}");
                server = new Thread(ServerLoop) { IsBackground = true, Name = "TcAgentBridge" };
                server.Start();
                Instance = this;
                // Hotkey "ask from code" (default Ctrl+Shift+Q, "askHotkey" in chat-backends.json)
                ui?.BeginInvoke(new Action(InstallHotkey), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // VSIX installation: deploy TcAgent, generate interop, set up AI clients (first time after confirmation)
                ui?.BeginInvoke(new Action(() =>
                {
                    try { Installer.OnStartup(Log, Notify); } catch (Exception ex) { Log("Installer: " + ex.Message); }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Show chat window on startup (can be disabled: "showOnStartup": false in chat-backends.json)
                if (ShowChatOnStartup() && ui != null)
                    ui.BeginInvoke(new Action(() =>
                    {
                        try { ShowChat(); } catch (Exception ex) { Log("Chat window: " + ex.Message); }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { Log("Init error: " + ex); }
            return System.Threading.Tasks.Task.FromResult(0);
        }

        protected override void Dispose(bool disposing)
        {
            stop = true;
            try { if (hookHandle != IntPtr.Zero) UnhookWindowsHookEx(hookHandle); hookHandle = IntPtr.Zero; } catch { }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ Pipe server
        void ServerLoop()
        {
            string name = "TcAgentBridge." + System.Diagnostics.Process.GetCurrentProcess().Id;
            Log("Pipe: " + name);
            while (!stop)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte))
                    {
                        pipe.WaitForConnection();
                        var reader = new StreamReader(pipe, new UTF8Encoding(false));
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                        string line = reader.ReadLine();
                        string answer;
                        try
                        {
                            var req = json.Deserialize<Dictionary<string, object>>(line ?? "{}");
                            answer = json.Serialize(Handle(req));
                        }
                        catch (Exception ex) { answer = json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = Inner(ex) }); }
                        writer.WriteLine(answer);
                        try { pipe.WaitForPipeDrain(); } catch { }
                    }
                }
                catch (Exception ex) { Log("Pipe error: " + ex.Message); Thread.Sleep(500); }
            }
        }

        Dictionary<string, object> Handle(Dictionary<string, object> req)
        {
            string cmd = Str(req, "command") ?? "ping";
            Log("<- " + cmd);
            CurrentOp = cmd;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, object> r;
            if (cmd == "ping")
                r = new Dictionary<string, object> { ["version"] = BridgeVersion, ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id, ["is64bit"] = Environment.Is64BitProcess };
            else
                r = (Dictionary<string, object>)ui.Invoke(new Func<Dictionary<string, object>>(() =>
                {
                    switch (cmd)
                    {
                        case "context": return Context();
                        case "chat_show": ShowChat(); return new Dictionary<string, object> { ["shown"] = true };
                        case "confirm": return ConfirmAction(Str(req, "title"), Str(req, "text"));
                        case "choice": return ChoiceAction(Str(req, "title"), Str(req, "text"));
                        case "probe": return Probe();
                        case "libs": return Libs();
                        case "lib_export": return LibExport(Str(req, "library"), Str(req, "version"), Str(req, "refresh") == "True");
                        case "goto": return Goto(Str(req, "name"));
                        case "open": return Open(Str(req, "path"), Str(req, "part"), int.TryParse(Str(req, "line"), out var ol) ? ol : 0,
                                                 int.TryParse(Str(req, "column"), out var oc) ? oc : 1);
                        case "caret": return Caret(Str(req, "path"), Str(req, "part"), int.TryParse(Str(req, "line"), out var cl2) ? cl2 : 0,
                                                   int.TryParse(Str(req, "column"), out var cc2) ? cc2 : 1);
                        case "xref": return Xref(Str(req, "name"), int.TryParse(Str(req, "max"), out var xm) ? xm : 300);
                        case "lm_call": return LmCall(Str(req, "target"), Str(req, "method"), req.TryGetValue("args", out var ar) ? ar as ArrayList ?? new ArrayList((ar as object[]) ?? new object[0]) : new ArrayList());
                        case "lm_export": return LmExport(Str(req, "library"), int.TryParse(Str(req, "max"), out var mx) ? mx : 100000);
                        default: throw new Exception("Unknown command: " + cmd);
                    }
                }));
            CurrentOp = null;
            r["ok"] = true;
            r["ms"] = sw.ElapsedMilliseconds;
            Log("-> " + cmd + " ok (" + sw.ElapsedMilliseconds + " ms)");
            return r;
        }

        // ------------------------------------------------------------------ Confirmation before runtime actions
        // TcAgent asks before activating, PLC start/stop, ADS writes ... (TCAGENT_CONFIRM). Default is "No".
        Dictionary<string, object> ConfirmAction(string title, string text)
        {
            System.Windows.MessageBoxResult r;
            var owner = System.Windows.Application.Current?.MainWindow;
            var msg = (text ?? "") + "\n\nExecute?";
            r = owner != null
                ? System.Windows.MessageBox.Show(owner, msg, title ?? "TcAgent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No)
                : System.Windows.MessageBox.Show(msg, title ?? "TcAgent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            bool ok = r == System.Windows.MessageBoxResult.Yes;
            Log("Confirmation '" + text + "': " + (ok ? "yes" : "no"));
            try { ChatControl.Instance?.Notice(ok ? "Confirmed: " + text : "Declined: " + text); } catch { }
            return new Dictionary<string, object> { ["confirmed"] = ok };
        }

        // ------------------------------------------------------------------ Choice: apply / apply all / reject / cancel
        // Used for refactoring proposals (tc_refactor): every change is confirmed by the user.
        Dictionary<string, object> ChoiceAction(string title, string text)
        {
            string result = "cancel";
            var win = new System.Windows.Window
            {
                Title = title ?? "TcAgent",
                SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                MaxWidth = 1000,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner != null) win.Owner = owner; else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBox
            {
                Text = text ?? "", IsReadOnly = true, BorderThickness = new System.Windows.Thickness(0), MinWidth = 520, MaxHeight = 520,
                TextWrapping = System.Windows.TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            });
            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(0, 14, 0, 0)
            };
            foreach (var (key, label) in new[] { ("apply", "_Apply"), ("all", "Apply a_ll"), ("reject", "_Reject"), ("cancel", "Cancel") })
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = label, MinWidth = 96, Margin = new System.Windows.Thickness(8, 0, 0, 0), Padding = new System.Windows.Thickness(10, 3, 10, 3),
                    IsCancel = key == "cancel"
                };
                b.Click += (s, e) => { result = key; win.Close(); };
                buttons.Children.Add(b);
            }
            panel.Children.Add(buttons);
            win.Content = panel;
            win.ShowDialog();
            Log("Choice '" + (title ?? "") + "': " + result);
            try { ChatControl.Instance?.Notice((result == "apply" || result == "all" ? "Applied: " : result == "reject" ? "Rejected: " : "Cancelled: ") + (title ?? "")); } catch { }
            return new Dictionary<string, object> { ["choice"] = result };
        }

        // ------------------------------------------------------------------ Chat window
        internal Dictionary<string, object> ContextForChat() => Context();

        IVsWindowFrame chatFrame;

        // Message in the chat window (from any thread)
        internal void Notify(string msg)
        {
            Log(msg);
            ui?.BeginInvoke(new Action(() =>
            {
                try { if (ChatControl.Instance == null) ShowChat(false); ChatControl.Instance?.Notice(msg); } catch { }
            }));
        }

        // ------------------------------------------------------------------ Hotkey: ask from the code window
        System.Windows.Forms.Keys hotKey = System.Windows.Forms.Keys.Q;
        System.Windows.Forms.Keys hotMods = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift;
        IVsWindowFrame returnFrame;

        void InstallHotkey()
        {
            try
            {
                statusWatch = !(ChatSetting("statusLog") is bool sw0 && !sw0);   // on by default for now (diagnostics)
                StartStallMonitor();
                var spec = ChatSetting("askHotkey") as string ?? "Ctrl+Shift+Q";
                if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) { Log("Hotkey disabled"); return; }
                System.Windows.Forms.Keys mods = 0, key = 0;
                foreach (var part in spec.Split('+').Select(x => x.Trim()))
                {
                    var p = part.ToLowerInvariant();
                    if (p == "ctrl" || p == "strg" || p == "control") mods |= System.Windows.Forms.Keys.Control;
                    else if (p == "shift" || p == "umschalt") mods |= System.Windows.Forms.Keys.Shift;
                    else if (p == "alt") mods |= System.Windows.Forms.Keys.Alt;
                    else key = (System.Windows.Forms.Keys)Enum.Parse(typeof(System.Windows.Forms.Keys), part, true);
                }
                if (key != 0) { hotKey = key; hotMods = mods; }
                // Keyboard hook only for the VS UI thread: sees the key before VS/the TwinCAT editor process it
                // (ComponentDispatcher does not see the keys of the WinForms editor - tested).
                keyHook = OnKeyHook;
                hookHandle = SetWindowsHookEx(2 /* WH_KEYBOARD */, keyHook, IntPtr.Zero, GetCurrentThreadId());
                Log($"Chat hotkey: {spec}" + (hookHandle == IntPtr.Zero ? $" - hook FAILED ({Marshal.GetLastWin32Error()})" : ""));
            }
            catch (Exception ex) { Log("Hotkey: " + Inner(ex)); }
        }

        // Diagnostics: measures how long the VS UI thread is blocked (ticks every 50 ms). Stalls > 150 ms are logged,
        // together with what the bridge was doing - this separates the causes: TcAgent, TwinCAT itself or something else.
        internal static volatile string CurrentOp;
        // Log status bar changes ("statusLog": true in chat-backends.json) - for diagnosing stalls
        bool statusWatch;
        void StartStallMonitor()
        {
            var last = System.Diagnostics.Stopwatch.StartNew();
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
            t.Tick += (s, e) =>
            {
                var ms = last.ElapsedMilliseconds - 50;
                if (ms > 150)
                {
                    string fg = "";
                    try { fg = System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? ""; } catch { }
                    string agent = "-";
                    try
                    {
                        var busy = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent"), "busy.*.txt")
                                            .Select(f => { try { return File.ReadAllText(f); } catch { return null; } }).Where(x => x != null).ToList();
                        if (busy.Count > 0) agent = string.Join(",", busy);
                    }
                    catch { }
                    Log($"UI blocked {ms} ms (TcAgent tool: {agent}, bridge: {CurrentOp ?? "-"}, focus: {fg}, status bar: \"{StatusText()}\")");
                }
                if (statusWatch)
                {
                    var st = StatusText();
                    if (st != lastStatus && !string.IsNullOrWhiteSpace(st) && !System.Text.RegularExpressions.Regex.IsMatch(st, "^(Bereit|Ready)$"))
                        Log("Status bar: " + st);
                    lastStatus = st;
                }
                last.Restart();
            };
            t.Start();
        }

        // Text of the VS status bar - often shows what TwinCAT/VS is doing in the background
        string lastStatus;
        string StatusText()
        {
            try { if (GetService(typeof(SVsStatusbar)) is IVsStatusbar sb && sb.GetText(out var t) == 0) return t; } catch { }
            return "";
        }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        HookProc keyHook;          // field keeps the delegate alive (otherwise the GC collects it)
        IntPtr hookHandle;

        IntPtr OnKeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                long lp = lParam.ToInt64();
                bool down = (lp & 0x80000000L) == 0, repeat = (lp & 0x40000000L) != 0;
                if (code == 0 && down && (int)wParam.ToInt64() == (int)hotKey && System.Windows.Forms.Control.ModifierKeys == hotMods)
                {
                    if (!repeat)
                        ui.BeginInvoke(new Action(() =>
                        {
                            Log("Hotkey pressed");
                            try { AskFromEditor(); } catch (Exception ex) { Log("Ask from editor: " + Inner(ex)); }
                        }));
                    return (IntPtr)1;   // swallow the key so it does not end up in the code
                }
            }
            catch { }
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        // Ask from code: small input box at the cursor line (InlineAsk). If the focus is already in the chat, just go there.
        internal void AskFromEditor()
        {
            try
            {
                var ms = (IVsMonitorSelection)GetService(typeof(SVsShellMonitorSelection));
                if (ms != null && ms.GetCurrentElementValue(1 /* SEID_DocumentFrame */, out var f) == 0 && f is IVsWindowFrame wf) returnFrame = wf;
            }
            catch { }
            if (ChatControl.Instance != null && ChatControl.Instance.IsKeyboardFocusWithin) { ChatControl.Instance.FocusInput(); return; }
            // Capture context now - the editor still has the focus at this moment
            string ctx = null;
            try { ctx = ChatControl.FormatContext(Context()); } catch (Exception ex) { Log("Context: " + Inner(ex)); }
            InlineAsk.Open(ctx, ChatControl.Instance?.IsBusy == true, ChatControl.DefaultBackendName());
        }

        // Question from the inline box: make the chat visible (without changing focus) and send it
        internal void SendInline(string text, string ctx)
        {
            try { ShowChat(false); } catch (Exception ex) { Log("Chat window: " + Inner(ex)); }
            ui.BeginInvoke(new Action(() =>
            {
                if (ChatControl.Instance == null) { Log("Inline question: chat window not available"); return; }
                ChatControl.Instance.SendExternal(text, ctx);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void ReturnToEditor()
        {
            try { returnFrame?.Show(); } catch { }
        }

        static object ChatSetting(string key)
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "chat-backends.json");
                if (!File.Exists(f)) return null;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                return d.TryGetValue(key, out var v) ? v : null;
            }
            catch { return null; }
        }

        void ShowChat() => ShowChat(true);

        // activate=false: make the window visible without taking the focus away from the code (inline question)
        internal void ShowChat(bool activate)
        {
            if (chatFrame != null && (activate ? chatFrame.Show() : chatFrame.ShowNoActivate()) == 0) return;
            // 1) Standard way (needs the ToolWindow registration that the VS SDK would normally generate)
            try
            {
                var w = FindToolWindow(typeof(ChatPane), 0, true);
                if (w?.Frame is IVsWindowFrame f) { chatFrame = f; Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(activate ? f.Show() : f.ShowNoActivate()); return; }
            }
            catch (Exception ex) { Log("FindToolWindow: " + Inner(ex) + " - creating window directly"); }
            // 2) Create directly via the VS shell
            var shell = (IVsUIShell)GetService(typeof(SVsUIShell)) ?? throw new Exception("SVsUIShell not available");
            var pane = new ChatPane();
            Guid empty = Guid.Empty, persist = typeof(ChatPane).GUID, auto = Guid.Empty;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(shell.CreateToolWindow(
                (uint)__VSCREATETOOLWIN.CTW_fInitNew, 0, pane, ref empty, ref persist, ref auto, null, "TcAgent Chat", null, out var frame));
            chatFrame = frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(activate ? frame.Show() : frame.ShowNoActivate());
            Log("Chat window created (IVsUIShell)");
        }

        static bool ShowChatOnStartup()
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "chat-backends.json");
                if (!File.Exists(f)) return true;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                return !(d.TryGetValue("showOnStartup", out var v) && v is bool b && !b);
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ IDE environment
        object exporter;
        Type apEnv;

        // Establish access to the IDE services
        Type Env(StringBuilder trace = null)
        {
            if (apEnv != null) return apEnv;
            if (exporter == null)
                foreach (var cm in FindComponentManagers(trace))
                {
                    exporter = TryCreate(cm, ExportTypeGuid, trace);
                    if (exporter != null) break;
                }
            if (exporter == null) throw new Exception("Export component not available - is a PLC project loaded?");
            apEnv = SafeTypes(exporter.GetType().Assembly).FirstOrDefault(t => t.Name == "APEnvironment");
            if (apEnv == null) throw new Exception("Environment of the export component not found");
            return apEnv;
        }

        object EnvProp(string name)
        {
            var p = Env().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return p?.GetValue(null, null);
        }

        // ------------------------------------------------------------------ Editor context
        Dictionary<string, object> Context()
        {
            var r = new Dictionary<string, object>();
            // 1) VS view: active document window
            object docView = null;
            try
            {
                var mon = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
                mon.GetCurrentElementValue(2 /* SEID_DocumentFrame */, out object fo);
                if (fo is IVsWindowFrame frame)
                {
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out object cap); r["frameCaption"] = cap?.ToString();
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object doc); r["document"] = doc?.ToString();
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView);
                    r["docViewType"] = docView?.GetType().FullName;
                }
            }
            catch (Exception ex) { r["vsError"] = Inner(ex); }

            // 2) Editor view: active editor view
            var candidates = new List<KeyValuePair<string, object>>();
            try
            {
                var engine = EnvProp("Engine");
                var cdsFrame = Get(engine, "Frame");
                var view = Get(cdsFrame, "ActiveView");
                r["cdsViewType"] = view?.GetType().FullName;
                if (view != null) candidates.Add(new KeyValuePair<string, object>("Engine.Frame.ActiveView", view));
            }
            catch (Exception ex) { r["cdsError"] = Inner(ex); }
            if (docView != null)
            {
                candidates.Add(new KeyValuePair<string, object>("DocView", docView));
                foreach (var p in docView.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !Rx(p.PropertyType.Name + " " + p.Name, "View|Editor|Control")) continue;
                    object v = null; try { v = p.GetValue(docView, null); } catch { }
                    if (v != null) candidates.Add(new KeyValuePair<string, object>("DocView." + p.Name, v));
                }
            }
            object cdsViewObj = candidates.Count > 0 && candidates[0].Key == "Engine.Frame.ActiveView" ? candidates[0].Value : null;
            foreach (var pn in new[] { "InterfaceEditorView", "ImplementationEditorView" })
            {
                var pv = SafeGet(cdsViewObj, pn);
                if (pv != null) candidates.Add(new KeyValuePair<string, object>("ActiveView." + pn, pv));
            }
            foreach (var c in candidates)
                if (TryGetSelection(c.Value, r)) { r["selectionSource"] = c.Key; break; }

            // Editor view: selection of the editor view - independent of the keyboard focus in VS
            try
            {
                var gs = cdsViewObj == null ? null : AllMethods(cdsViewObj.GetType()).FirstOrDefault(m => m.Name == "GetSelection" && m.GetParameters().Length == 0);
                if (gs != null && gs.Invoke(cdsViewObj, null) is IEnumerable sel)
                {
                    var items = new List<object>();
                    foreach (var it in sel)
                    {
                        if (it == null || items.Count >= 10) continue;
                        var d = Serialize(it, 1);
                        foreach (var kv in Members(it, ".")) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                        foreach (var kv in d.ToList())
                            if (kv.Value is long lp && kv.Key.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var sp = SplitPos(lp);
                                if (sp != null) { d[kv.Key + "_Line"] = sp[0]; d[kv.Key + "_Offset"] = sp[1]; }
                            }
                        items.Add(d);
                    }
                    r["cdsSelection"] = items;
                }
            }
            catch (Exception ex) { r["cdsSelectionError"] = Inner(ex); }

            // Both halves (declaration/implementation) separately: each has its own editor control with cursor and
            // selection - regardless of which window in VS currently has the focus (tested, see concept spike 4)
            try
            {
                var panes = new List<Dictionary<string, object>>();
                foreach (var part in new[] { new[] { "InterfaceEditorView", "declaration" }, new[] { "ImplementationEditorView", "implementation" } })
                {
                    if (!(SafeGet(cdsViewObj, part[0]) is System.Windows.Forms.Control ctl)) continue;
                    var syn = ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl
                            : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0);
                    if (syn == null) continue;
                    int line = SafeGet(syn, "CaretLine") is int l ? l : 0;
                    int pos = SafeGet(syn, "CaretPosition") is int cp ? cp : 0;
                    int selCount = SafeGet(syn, "SelectionCount") is int sc ? sc : 0;
                    var selText = SafeGet(syn, "SelectedText") as string;
                    var text = SafeGet(syn, "Text") as string ?? "";
                    // Column = character position minus start of line (line ending \r\n or \n)
                    int nl = text.Contains("\r\n") ? 2 : 1, start = 0;
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < line && i < lines.Length; i++) start += lines[i].Length + nl;
                    // Code excerpt around the cursor (+-6 lines) - saves the agent a tool call
                    var exc = new StringBuilder();
                    for (int i = Math.Max(0, line - 6); i <= Math.Min(lines.Length - 1, line + 6); i++)
                        exc.Append(i == line ? ">" : " ").Append((i + 1).ToString().PadLeft(4)).Append(": ").Append(lines[i]).Append('\n');
                    panes.Add(new Dictionary<string, object>
                    {
                        ["part"] = part[1], ["line"] = line + 1, ["column"] = Math.Max(1, pos - start + 1),
                        ["excerpt"] = exc.ToString(), ["lineCount"] = lines.Length,
                        ["selectionCount"] = selCount, ["selectedText"] = string.IsNullOrEmpty(selText) ? null : selText,
                        ["focused"] = syn.Focused || syn.ContainsFocus
                    });
                }
                if (panes.Count > 0)
                {
                    r["panes"] = panes;
                    // Active half: focus > selection > cursor away from line 1/column 1
                    var act = panes.FirstOrDefault(p => (bool)p["focused"])
                           ?? (panes.Count(p => (int)p["selectionCount"] > 0) == 1 ? panes.First(p => (int)p["selectionCount"] > 0) : null)
                           ?? (panes.Count(p => (int)p["line"] > 1 || (int)p["column"] > 1) == 1 ? panes.First(p => (int)p["line"] > 1 || (int)p["column"] > 1) : null);
                    if (act != null) r["active"] = act;
                }
            }
            catch (Exception ex) { r["panesError"] = Inner(ex); }

            // VS view: TwinCAT's VSEditor implements IVsTextView -> cursor, selection, selected text
            if (docView != null)
            {
                int hr;
                var a2 = new object[2];
                if (TryInvoke(docView, "GetCaretPos", a2, out hr)) { r["caretLine"] = Convert.ToInt32(a2[0]) + 1; r["caretColumn"] = Convert.ToInt32(a2[1]) + 1; }
                else if (hr != 0) r["caretHr"] = hr;
                var a4 = new object[4];
                if (TryInvoke(docView, "GetSelection", a4, out hr))
                {
                    r["selStartLine"] = Convert.ToInt32(a4[0]) + 1; r["selStartColumn"] = Convert.ToInt32(a4[1]) + 1;
                    r["selEndLine"] = Convert.ToInt32(a4[2]) + 1; r["selEndColumn"] = Convert.ToInt32(a4[3]) + 1;
                }
                var a1 = new object[1];
                if (TryInvoke(docView, "GetSelectedText", a1, out hr) && a1[0] is string st && st.Length > 0)
                    r["selectedText"] = st.Length > 8000 ? st.Substring(0, 8000) + "..." : st;
                // Which part has the cursor: declaration or implementation?
                try
                {
                    var active = SafeCall(docView, "GetActiveSynEdControl", SafeGet(docView, "EditorView")) as System.Windows.Forms.Control;
                    r["activeEditorControl"] = active?.GetType().FullName;
                    foreach (var part in new[] { new[] { "InterfaceEditorView", "declaration" }, new[] { "ImplementationEditorView", "implementation" } })
                        if (active != null && SafeGet(cdsViewObj, part[0]) is System.Windows.Forms.Control pc && (pc == active || pc.Contains(active)))
                            r["part"] = part[1];
                }
                catch (Exception ex) { r["partError"] = Inner(ex); }
            }

            // 3) Focused WinForms control (TwinCAT editors) + WPF focus
            try
            {
                var chain = new List<string>();
                var control = System.Windows.Forms.Control.FromChildHandle(GetFocus());
                for (int i = 0; control != null && i < 12; i++, control = control.Parent)
                {
                    var t = control.GetType();
                    var info = t.FullName;
                    foreach (var pn in new[] { "SelectedText", "SelectionStart", "SelectionLength", "CaretLine", "CurrentLine", "CaretPosition", "CaretColumn", "Caret", "Position" })
                    {
                        var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
                        if (pi == null || pi.GetIndexParameters().Length > 0) continue;
                        try { info += $" | {pn}={Short(pi.GetValue(control, null), 300)}"; } catch { }
                    }
                    chain.Add(info);
                }
                r["focusChain"] = chain;
                r["wpfFocus"] = System.Windows.Input.Keyboard.FocusedElement?.GetType().FullName;
            }
            catch (Exception ex) { r["focusError"] = Inner(ex); }

            // If neither half has the focus (e.g. because the user is typing in the chat window) and the active half is not
            // otherwise unambiguous: decide by the continuous VS line number (declaration + implementation).
            try
            {
                if (!r.ContainsKey("active") && r.TryGetValue("panes", out var po) && po is List<Dictionary<string, object>> ps && ps.Count == 2
                    && r.TryGetValue("caretLine", out var clo))
                {
                    int cl = Convert.ToInt32(clo), declLines = Convert.ToInt32(ps[0]["lineCount"]);
                    r["active"] = cl > declLines ? ps[1] : ps[0];
                    r["activeBy"] = "caretLine";
                }
            }
            catch (Exception ex) { r["activeError"] = Inner(ex); }
            return r;
        }

        static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control c)
        {
            foreach (System.Windows.Forms.Control k in c.Controls) { yield return k; foreach (var g in Descendants(k)) yield return g; }
        }

        // Properties AND fields (including non-public ones) whose name matches the pattern - for diagnostics
        static Dictionary<string, object> Members(object o, string pattern)
        {
            var d = new Dictionary<string, object>();
            if (o == null) return d;
            var rx = new System.Text.RegularExpressions.Regex(pattern);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object Val(object v)
            {
                if (v == null) return null;
                if (Simple(v)) return v is Enum || v is Guid ? v.ToString() : v;
                var s = v.ToString(); var tn = v.GetType().FullName ?? "";
                if (s != tn) return Short(s, 200);
                // one level deeper for structures (e.g. Caret/Range): simple fields/properties
                var sub = new Dictionary<string, object>();
                foreach (var f in v.GetType().GetFields(bf)) { try { var x = f.GetValue(v); if (x != null && Simple(x)) sub[f.Name] = x.ToString(); } catch { } }
                foreach (var p in v.GetType().GetProperties(bf)) { if (p.GetIndexParameters().Length > 0) continue; try { var x = p.GetValue(v, null); if (x != null && Simple(x)) sub[p.Name] = x.ToString(); } catch { } }
                return sub.Count > 0 ? (object)sub : tn;
            }
            for (var t = o.GetType(); t != null && t != typeof(object) && t != typeof(System.Windows.Forms.Control); t = t.BaseType)
            {
                foreach (var f in t.GetFields(bf | BindingFlags.DeclaredOnly))
                    if (rx.IsMatch(f.Name) && !d.ContainsKey(f.Name)) { try { d[f.Name] = Val(f.GetValue(o)); } catch { } }
                foreach (var p in t.GetProperties(bf | BindingFlags.DeclaredOnly))
                    if (p.GetIndexParameters().Length == 0 && rx.IsMatch(p.Name) && !d.ContainsKey(p.Name)) { try { d[p.Name] = Val(p.GetValue(o, null)); } catch { } }
                if (d.Count > 60) break;
            }
            return d;
        }

        // Split position into line and column
        static object[] SplitPos(long pos)
        {
            foreach (var t in AppDomain.CurrentDomain.GetAssemblies().Where(x => Rx(x.GetName().Name, "Utilities|_3S|CoDeSys|^Core$")).SelectMany(SafeTypes).Where(t => t.Name == "PositionHelper"))
            {
                var m = t.GetMethod("SplitPosition", BindingFlags.Public | BindingFlags.Static);
                if (m == null || m.GetParameters().Length != 3) continue;
                try { var a = new object[] { pos, null, null }; m.Invoke(null, a); return new[] { a[1], a[2] }; } catch { }
            }
            return null;
        }

        // Call a method that has only by-ref parameters (COM style, HRESULT return)
        static bool TryInvoke(object target, string name, object[] args, out int hr)
        {
            hr = 0;
            var m = AllMethods(target.GetType()).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length && x.GetParameters().All(p => p.ParameterType.IsByRef));
            if (m == null) return false;
            try
            {
                var ret = m.Invoke(target, args);
                if (ret is int i) hr = i;
                return hr == 0;
            }
            catch (Exception ex) { hr = ex.HResult; return false; }
        }

        // Read selection
        bool TryGetSelection(object view, Dictionary<string, object> r)
        {
            var m = AllMethods(view.GetType()).FirstOrDefault(x => x.Name == "GetSelection" && x.GetParameters().Length == 5 && x.GetParameters().All(p => p.ParameterType.IsByRef));
            if (m == null) return false;
            var args = new object[5];
            try { m.Invoke(view, args); }
            catch (Exception ex) { r["selectionError"] = Inner(ex); return false; }
            r["online"] = args[0]; r["projectHandle"] = args[1]; r["objectGuid"] = args[2]?.ToString();
            long pos = Convert.ToInt64(args[3]); r["position"] = pos; r["length"] = args[4];
            // Split position
            foreach (var t in AppDomain.CurrentDomain.GetAssemblies().Where(a => Rx(a.GetName().Name, "^Core$|_3S|CoDeSys|Objects")).SelectMany(SafeTypes).Where(t => t.Name == "PositionHelper"))
            {
                var split = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "SplitPosition" && x.GetParameters().Length == 3);
                if (split == null) continue;
                try
                {
                    var a = new object[] { pos, null, null };
                    split.Invoke(null, a);
                    r["line"] = a[1]; r["column"] = a[2]; r["splitBy"] = t.FullName;
                }
                catch (Exception ex) { r["splitError"] = Inner(ex); }
                break;
            }
            // Object name and path from the ObjectManager
            try
            {
                var om = EnvProp("ObjectMgr");
                int ph = Convert.ToInt32(args[1]); var g = (Guid)args[2];
                var names = new List<string>();
                for (int i = 0; i < 12 && g != Guid.Empty; i++)
                {
                    var mo = Call(om, "GetObjectToRead", ph, g);
                    names.Insert(0, Convert.ToString(Get(mo, "Name")));
                    g = (Guid)Get(mo, "ParentObjectGuid");
                }
                r["objectPath"] = string.Join(".", names);
            }
            catch (Exception ex) { r["objectError"] = Inner(ex); }
            return true;
        }

        // ------------------------------------------------------------------ Libraries
        Dictionary<string, object> Libs()
        {
            var mgr = EnvProp("ManagedLibraryMgr") ?? throw new Exception("ManagedLibraryMgr not available");
            var list = new List<object>();
            foreach (var lib in (IEnumerable)Call(mgr, "GetAllLibraries", true))
                list.Add(new Dictionary<string, object>
                {
                    ["title"] = Convert.ToString(Get(lib, "Title")), ["version"] = Convert.ToString(Get(lib, "Version")),
                    ["company"] = Convert.ToString(Get(lib, "Company")), ["compiled"] = SafeCall(lib, "IsCompiledLibrary"),
                    ["path"] = SafeCall(mgr, "GetFilePath", lib)
                });
            return new Dictionary<string, object> { ["libraries"] = list };
        }

        Dictionary<string, object> LibExport(string title, string version, bool refresh)
        {
            if (string.IsNullOrEmpty(title)) throw new Exception("library missing");
            var mgr = EnvProp("ManagedLibraryMgr") ?? throw new Exception("ManagedLibraryMgr not available");
            var hits = ((IEnumerable)Call(mgr, "GetAllLibraries", true)).Cast<object>()
                .Where(l => string.Equals(Convert.ToString(Get(l, "Title")), title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0) throw new Exception($"Library '{title}' is not installed in the repository");
            object lib = string.IsNullOrEmpty(version) || version == "*"
                ? hits.OrderByDescending(l => (Version)Get(l, "Version")).First()
                : hits.FirstOrDefault(l => Convert.ToString(Get(l, "Version")) == version)
                  ?? throw new Exception($"Version {version} of '{title}' not installed; available: " + string.Join(", ", hits.Select(l => Get(l, "Version"))));
            string ver = Convert.ToString(Get(lib, "Version"));
            string path = Convert.ToString(Call(mgr, "GetFilePath", lib));
            string cacheDir = Path.Combine(Path.GetDirectoryName(dir), "libcache");
            Directory.CreateDirectory(cacheDir);
            string file = Path.Combine(cacheDir, Sanitize(Convert.ToString(Get(lib, "Title")) + "_" + ver) + ".json");
            var r = new Dictionary<string, object> { ["title"] = Get(lib, "Title"), ["version"] = ver, ["company"] = Get(lib, "Company"), ["libPath"] = path, ["file"] = file };
            if (File.Exists(file) && !refresh && new FileInfo(file).Length > 2) { r["cached"] = true; r["chars"] = new FileInfo(file).Length; return r; }

            Env();
            string result = null; var tried = new List<string>();
            try { result = Convert.ToString(Call(exporter, "ExecuteGetDocumentationJson", lib, path)); tried.Add("ExecuteGetDocumentationJson: " + (result?.Length ?? 0)); }
            catch (Exception ex) { tried.Add("ExecuteGetDocumentationJson: " + Inner(ex)); }
            for (int itemType = 0; itemType <= 8 && string.IsNullOrEmpty(result); itemType++)
            {
                try { result = Convert.ToString(Call(exporter, "GetExportInformationJson", itemType, lib, path)); tried.Add($"GetExportInformationJson({itemType}): {result?.Length ?? 0}"); }
                catch (Exception ex) { tried.Add($"GetExportInformationJson({itemType}): {Inner(ex)}"); }
            }
            r["tried"] = tried;
            if (string.IsNullOrEmpty(result)) throw new Exception("Export returned nothing: " + string.Join(" | ", tried));
            File.WriteAllText(file, result, new UTF8Encoding(false));
            r["cached"] = false; r["chars"] = result.Length;
            return r;
        }

        // ------------------------------------------------------------------ Navigation: go to definition / cross references
        // Current object in the editor (for name resolution in the correct scope)
        // Order: active editor view -> editor of the active VS document frame (stays set while the chat or another
        // tool window has the focus) -> last editor object seen.
        (int ph, Guid obj) lastObject;
        (int ph, Guid obj) ActiveObject()
        {
            var r = ObjOf(SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView"));
            if (r.obj == Guid.Empty)
            {
                var dv = ActiveDocView(out _);
                r = ObjOf(SafeGet(dv, "EditorView"));
                if (r.obj == Guid.Empty) r = ObjOf(dv);
            }
            if (r.obj != Guid.Empty) lastObject = r;
            else if (lastObject.obj != Guid.Empty) r = lastObject;
            return r;
        }

        // Project handle and object GUID of an editor view (properties or IEditorView.GetSelection)
        static (int ph, Guid obj) ObjOf(object view)
        {
            if (view == null) return (0, Guid.Empty);
            var g = SafeGet(view, "ObjectGuid"); var h = SafeGet(view, "ProjectHandle");
            if (g is Guid gg && gg != Guid.Empty) return (h is int i ? i : 0, gg);
            var m = AllMethods(view.GetType()).FirstOrDefault(x => x.Name == "GetSelection" && x.GetParameters().Length == 5 && x.GetParameters().All(p => p.ParameterType.IsByRef));
            if (m != null)
                try
                {
                    var a = new object[5];
                    m.Invoke(view, a);
                    if (a[2] is Guid sg && sg != Guid.Empty) return (Convert.ToInt32(a[1]), sg);
                }
                catch { }
            return (0, Guid.Empty);
        }

        // DocView and document moniker of the active VS document frame (also while a tool window has the focus)
        static object ActiveDocView(out string document)
        {
            document = null;
            try
            {
                var mon = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
                mon.GetCurrentElementValue(2 /* SEID_DocumentFrame */, out object fo);
                if (fo is IVsWindowFrame frame)
                {
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object doc); document = doc?.ToString();
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object dv);
                    return dv;
                }
            }
            catch { }
            return null;
        }

        // All open project handles (TwinCAT: one per PLC project) - for lookups without editor context
        List<int> ProjectHandles()
        {
            var list = new List<int>();
            try { if (SafeCall(EnvProp("ObjectMgr"), "GetProjectHandles") is int[] hs) list.AddRange(hs); } catch { }
            try
            {
                var projects = SafeGet(EnvProp("Engine"), "Projects");
                if (SafeGet(projects, "Projects") is IEnumerable ps)
                    foreach (var p in ps) if (SafeGet(p, "Handle") is int h) list.Add(h);
                if (SafeGet(SafeGet(projects, "PrimaryProject"), "Handle") is int hp) list.Add(hp);
            }
            catch { }
            return list.Distinct().ToList();
        }

        string ObjectPath(int ph, Guid g)
        {
            var om = EnvProp("ObjectMgr"); var names = new List<string>();
            try
            {
                for (int i = 0; i < 12 && g != Guid.Empty; i++)
                {
                    var mo = Call(om, "GetObjectToRead", ph, g);
                    names.Insert(0, Convert.ToString(Get(mo, "Name")));
                    g = (Guid)Get(mo, "ParentObjectGuid");
                }
            }
            catch { }
            return string.Join(".", names);
        }

        Dictionary<string, object> SigInfo(object sig, int ph)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = Convert.ToString(SafeGet(sig, "OrgName") ?? SafeGet(sig, "Name")),
                ["kind"] = Convert.ToString(SafeGet(sig, "POUType")),
                ["extends"] = Convert.ToString(SafeGet(sig, "BaseSignature")),
                ["isLibrary"] = SafeGet(sig, "IsLibraryObject"),
                ["library"] = Convert.ToString(SafeGet(sig, "LibraryPath")),
                ["objectGuid"] = Convert.ToString(SafeGet(sig, "ObjectGuid")),
                ["doc"] = Convert.ToString(SafeGet(sig, "DocuComment")),
            };
            if (!(SafeGet(sig, "IsLibraryObject") is bool b && b) && SafeGet(sig, "ObjectGuid") is Guid og) d["objectPath"] = ObjectPath(ph, og);
            return d;
        }

        static IEnumerable<object> Vars(object sig) => (SafeGet(sig, "AllVariables") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();

        Dictionary<string, object> Goto(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name missing");
            var lm = EnvProp("LanguageModelMgr");
            var (ph, obj) = ActiveObject();
            var r = new Dictionary<string, object> { ["name"] = name, ["context"] = ObjectPath(ph, obj), ["projectHandle"] = ph };
            var parts = name.Split('.');
            var first = parts[0];

            // 1) Variable of the current object or its FB (method -> parent object), including base classes
            object FindSig(Guid g) { var a = new object[] { g, null }; try { return AllMethods(lm.GetType()).First(m => m.Name == "FindSignature" && m.GetParameters().Length == 2).Invoke(lm, a); } catch { return null; } }
            var scopes = new List<object>();
            var cur = FindSig(obj);
            if (cur != null) scopes.Add(cur);
            var parentGuid = SafeGet(cur, "ParentObjectGuid") is Guid pg ? pg : Guid.Empty;
            if (parentGuid != Guid.Empty && FindSig(parentGuid) is object ps) scopes.Add(ps);
            foreach (var sc in scopes.ToList())
            {
                var b = SafeGet(sc, "BaseSignature")?.ToString();
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(b); i++)
                {
                    var bs = FindByName(lm, ph, obj, b).FirstOrDefault();
                    if (bs == null) break;
                    scopes.Add(bs); b = SafeGet(bs, "BaseSignature")?.ToString();
                }
            }
            foreach (var sc in scopes)
            {
                var v = Vars(sc).FirstOrDefault(x => string.Equals(Convert.ToString(SafeGet(x, "OrgName") ?? SafeGet(x, "Name")), first, StringComparison.OrdinalIgnoreCase));
                if (v == null) continue;
                var type = Convert.ToString(SafeGet(v, "Type") ?? SafeGet(v, "OriginalType"));
                r["variable"] = new Dictionary<string, object>
                {
                    ["name"] = Convert.ToString(SafeGet(v, "OrgName")), ["type"] = type, ["flags"] = Convert.ToString(SafeGet(v, "Flags")),
                    ["doc"] = Convert.ToString(SafeGet(v, "DocuComment")), ["declaredIn"] = SigInfo(sc, ph)
                };
                // Resolve the variable's type (without ARRAY/POINTER/REFERENCE prefix)
                var bare = System.Text.RegularExpressions.Regex.Replace(type ?? "", @"^(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bare = bare.Split('(')[0].Trim();
                var ts = FindByName(lm, ph, obj, bare).FirstOrDefault();
                if (ts != null) r["type"] = SigInfo(ts, ph);
                break;
            }

            // 2) Type / FB / function / GVL with this name (project and libraries)
            if (!r.ContainsKey("variable"))
            {
                var hits = FindByName(lm, ph, obj, first).Take(10).Select(x => (object)SigInfo(x, ph)).ToList();
                if (hits.Count == 0 && parts.Length > 1) hits = FindByName(lm, ph, obj, parts[parts.Length - 1]).Take(10).Select(x => (object)SigInfo(x, ph)).ToList();
                // No hit (e.g. no editor context because the chat window is active): search at project level -
                // every PLC project including its libraries (library types such as MC_MoveAxesAbsoluteFB)
                if (hits.Count == 0)
                    foreach (var h in ProjectHandles())
                    {
                        hits = FindByName(lm, h, Guid.Empty, first).Take(10).Select(x => (object)SigInfo(x, h)).ToList();
                        if (hits.Count > 0) { r["resolvedAt"] = "project"; r["projectHandle"] = h; break; }
                    }
                r["signatures"] = hits;
            }
            // 3) Dotted names (GVL.Variable, instance.input, a.b.c): resolve member by member through the types
            if (parts.Length > 1)
            {
                object owner = null;
                if (r.TryGetValue("type", out var tinfo) && tinfo is Dictionary<string, object> ti) owner = FindByName(lm, ph, obj, Convert.ToString(ti["name"])).FirstOrDefault();
                else owner = FindByName(lm, ph, obj, first).FirstOrDefault();
                var chain = new List<object>();
                for (int i = 1; i < parts.Length && owner != null; i++)
                {
                    object hitVar = null, hitSig = owner;
                    // also search in base classes
                    for (int depth = 0; depth < 8 && hitSig != null && hitVar == null; depth++)
                    {
                        hitVar = Vars(hitSig).FirstOrDefault(x => string.Equals(Convert.ToString(SafeGet(x, "OrgName") ?? SafeGet(x, "Name")), parts[i], StringComparison.OrdinalIgnoreCase));
                        if (hitVar == null) { var b = SafeGet(hitSig, "BaseSignature")?.ToString(); hitSig = string.IsNullOrEmpty(b) ? null : FindByName(lm, ph, obj, b).FirstOrDefault(); }
                    }
                    if (hitVar == null) { chain.Add(new Dictionary<string, object> { ["name"] = parts[i], ["notFoundIn"] = SigInfo(owner, ph) }); break; }
                    var vt = Convert.ToString(SafeGet(hitVar, "Type"));
                    chain.Add(new Dictionary<string, object>
                    {
                        ["name"] = Convert.ToString(SafeGet(hitVar, "OrgName")), ["type"] = vt, ["flags"] = Convert.ToString(SafeGet(hitVar, "Flags")),
                        ["doc"] = Convert.ToString(SafeGet(hitVar, "DocuComment")), ["declaredIn"] = SigInfo(hitSig, ph)
                    });
                    var bare = System.Text.RegularExpressions.Regex.Replace(vt ?? "", @"^(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Split('(')[0].Trim();
                    owner = FindByName(lm, ph, obj, bare).FirstOrDefault();
                    if (owner != null && i == parts.Length - 1) r["memberType"] = SigInfo(owner, ph);
                }
                r["members"] = chain;
            }
            return r;
        }

        // ------------------------------------------------------------------ Navigation: open an object in the editor (like F12)
        // Opens the object of a TwinCAT tree path via the Solution Explorer (like a double-click) and sets the cursor.
        Dictionary<string, object> Open(string path, string part, int line, int column)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new Exception("path missing");
            var r = new Dictionary<string, object> { ["path"] = path };
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2 ?? throw new Exception("DTE not available");
            var item = FindUiItem(dte, path.Split('^'), r);
            if (item == null) throw new Exception("Object not found in Solution Explorer: " + path);
            // DoDefaultAction acts on the selection of the Solution Explorer - only when that window is active
            string before = null;
            try { before = dte.ActiveDocument?.FullName; } catch { }
            try { dte.ToolWindows.SolutionExplorer.Parent.Activate(); } catch { }
            item.Select(EnvDTE.vsUISelectionType.vsUISelectionTypeSelect);
            dte.ToolWindows.SolutionExplorer.DoDefaultAction();
            r["via"] = "SolutionExplorer";
            string after = null;
            try { after = dte.ActiveDocument?.FullName; } catch { }
            var last = path.Split('^').Last();
            if (!DocMatches(after, last))
            {
                // the node's object may be a project item that can be opened directly
                try
                {
                    if (item.Object is EnvDTE.ProjectItem pi) { var w = pi.Open(EnvDTE.Constants.vsViewKindPrimary); w.Visible = true; w.Activate(); r["via"] = "ProjectItem"; }
                }
                catch (Exception ex) { r["projectItemError"] = ex.Message; }
                try { after = dte.ActiveDocument?.FullName; } catch { }
                if (!DocMatches(after, last))
                    try { dte.ExecuteCommand("View.Open"); r["via"] = "View.Open"; } catch { }
            }
            r["opened"] = true;
            if (line > 0) foreach (var kv in Caret(path, part, line, column)) r[kv.Key] = kv.Value;
            return r;
        }

        // Set the cursor in the active TwinCAT editor. line counts within the part (declaration/implementation) from 1, like tc_plc_read.
        Dictionary<string, object> Caret(string path, string part, int line, int column)
        {
            var r = new Dictionary<string, object>();
            var dv = ActiveDocView(out var doc);
            r["document"] = doc;
            var last = (path ?? "").Split('^').LastOrDefault() ?? "";
            if (dv == null || !DocMatches(doc, last))
            {
                r["lineSet"] = false; r["lineError"] = "editor for '" + last + "' not active (yet); active document: " + (doc ?? "-");
                return r;
            }
            bool impl = part != null && part.StartsWith("impl", StringComparison.OrdinalIgnoreCase);
            // In IVsTextView TwinCAT counts lines continuously across declaration + implementation
            var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
            int declLines = 0;
            bool hasDecl = false, hasImplView = false;
            System.Windows.Forms.Control target = null, anySyn = null;
            foreach (var pn in new[] { "InterfaceEditorView", "ImplementationEditorView" })
            {
                if (!(SafeGet(cds, pn) is System.Windows.Forms.Control ctl)) continue;
                var syn = ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl
                        : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0);
                if (syn == null) continue;
                anySyn = anySyn ?? syn;
                if (pn == "ImplementationEditorView") hasImplView = true;
                if (pn == "InterfaceEditorView") hasDecl = true;
                if (pn == "InterfaceEditorView") declLines = (SafeGet(syn, "Text") as string ?? "").Replace("\r\n", "\n").Split('\n').Length;
                if ((pn == "ImplementationEditorView") == impl) target = syn;
            }
            r["part"] = impl ? "implementation" : "declaration"; r["line"] = line;
            // Actions/transitions have no declaration part: the implementation is the whole document (declLines stays 0)
            if (impl && !hasDecl && !hasImplView && anySyn != null) target = anySyn;
            if (impl && hasDecl && declLines == 0) { r["lineSet"] = false; r["lineError"] = "line count of the declaration part not available"; return r; }
            if (impl && !hasDecl) r["declarationPart"] = "none";
            int vsLine = (impl ? declLines : 0) + line - 1;   // 0-based
            try { target?.Focus(); } catch { }
            int hr = -1;
            try { hr = Convert.ToInt32(Call(dv, "SetCaretPos", vsLine, Math.Max(0, column - 1))); }
            catch (Exception ex) { r["lineError"] = "SetCaretPos: " + Inner(ex); }
            try { Call(dv, "CenterLines", Math.Max(0, vsLine - 5), 11); } catch { }
            var a2 = new object[2];
            if (TryInvoke(dv, "GetCaretPos", a2, out _))
            {
                int now = Convert.ToInt32(a2[0]);
                r["caretVsLine"] = now + 1;
                r["lineSet"] = now == vsLine;
                if (now != vsLine && !r.ContainsKey("lineError")) r["lineError"] = $"caret is on VS line {now + 1} instead of {vsLine + 1}";
            }
            else r["lineSet"] = hr == 0;
            // The TwinCAT text editor (SynEdit control of the part) may ignore IVsTextView.SetCaretPos: set it there directly
            if (!(r.TryGetValue("lineSet", out var ok) && ok is bool b0 && b0) && target != null)
            {
                var how = SetCaretInControl(target, line, Math.Max(1, column));
                if (how != null) { r["lineSet"] = true; r["via"] = how; r.Remove("lineError"); }
                else r["caretMembers"] = string.Join(", ", target.GetType().GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Select(m => m.Name).Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, "Caret|Line|Select|Goto|Cursor", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .Distinct().Take(40));
            }
            return r;
        }

        // Document moniker matches the object: "...\FB.TcPOU" (POU/DUT/GVL) or "...\FB.TcPOU@Method"
        // Cursor in a TwinCAT editor control (line within its part, 1-based). Tries the usual members of SynEdit-like
        // controls; returns how it worked or null.
        static string SetCaretInControl(object ctl, int line, int column)
        {
            var t = ctl.GetType();
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            bool TryCall(string name, params object[] args)
            {
                foreach (var m in t.GetMethods(F).Where(x => x.Name == name && x.GetParameters().Length == args.Length))
                {
                    try
                    {
                        var ps = m.GetParameters();
                        var conv = args.Select((v, i) => Convert.ChangeType(v, ps[i].ParameterType)).ToArray();
                        m.Invoke(ctl, conv);
                        return true;
                    }
                    catch { }
                }
                return false;
            }
            bool TrySet(string name, object v)
            {
                var pi = t.GetProperty(name, F);
                if (pi == null || !pi.CanWrite) return false;
                try { pi.SetValue(ctl, Convert.ChangeType(v, pi.PropertyType)); return true; } catch { return false; }
            }
            int? GetInt(string name) { try { var v = t.GetProperty(name, F)?.GetValue(ctl); return v == null ? (int?)null : Convert.ToInt32(v); } catch { return null; } }
            // 1) line/column members (0- or 1-based - verified by reading back where possible)
            foreach (var (name, zero) in new[] { ("GotoLine", false), ("GoToLine", false), ("SetCaretPosition", true), ("SetCaretPos", true), ("MoveCaret", true) })
            {
                if (TryCall(name, zero ? line - 1 : line, zero ? column - 1 : column) || TryCall(name, zero ? line - 1 : line))
                {
                    var now = GetInt("CaretLine") ?? GetInt("CurrentLine");
                    if (now == null || now == line || now == line - 1) return name;
                }
            }
            foreach (var name in new[] { "CaretLine", "CurrentLine", "CaretLineNumber" })
                if (TrySet(name, line - 1) && (GetInt(name) == line - 1)) return name;
            // 2) character offset of the line in the control's text (Select / SelectionStart)
            string text = null;
            try { text = t.GetProperty("Text", F)?.GetValue(ctl) as string; } catch { }
            if (text != null)
            {
                int off = 0, l = 1;
                while (l < line && off < text.Length) { var nl = text.IndexOf('\n', off); if (nl < 0) { off = text.Length; break; } off = nl + 1; l++; }
                off = Math.Min(text.Length, off + column - 1);
                if (TryCall("Select", off, 0)) return "Select(offset)";
                if (TrySet("SelectionStart", off)) { TrySet("SelectionLength", 0); return "SelectionStart"; }
                if (TrySet("CaretOffset", off) || TrySet("CaretPosition", off)) return "CaretOffset";
            }
            return null;
        }

        static bool DocMatches(string doc, string name)
        {
            if (string.IsNullOrEmpty(doc) || string.IsNullOrEmpty(name)) return false;
            var at = doc.LastIndexOf('@');
            if (at > 0)
            {
                var m = doc.Substring(at + 1);
                return string.Equals(m, name, StringComparison.OrdinalIgnoreCase) || m.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(Path.GetFileNameWithoutExtension(doc), name, StringComparison.OrdinalIgnoreCase);
        }

        // Solution Explorer node for a TwinCAT tree path (TIPC^MyPlc^MyPlc Project^POUs^FB^Method).
        // TwinCAT shows POUs with a type suffix ("MAIN (PRG)", "FB_Axis (FB)"). First find the PLC node (2nd segment) in the
        // upper levels, then descend segment by segment. Nodes expanded only for the search are collapsed again.
        static EnvDTE.UIHierarchyItem FindUiItem(EnvDTE80.DTE2 dte, string[] segs, Dictionary<string, object> r)
        {
            if (segs.Length < 2) return null;
            var expanded = new List<EnvDTE.UIHierarchyItems>();
            bool Is(EnvDTE.UIHierarchyItem it, string seg)
            {
                string n;
                try { n = it.Name ?? ""; } catch { return false; }
                if (string.Equals(n, seg, StringComparison.OrdinalIgnoreCase) || n.StartsWith(seg + " (", StringComparison.OrdinalIgnoreCase)) return true;
                // POUs/DUTs/GVLs are listed with their file name ("FB_Axis.TcPOU")
                var dot = n.LastIndexOf('.');
                if (dot > 0 && n.Substring(dot).StartsWith(".Tc", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.Substring(0, dot), seg, StringComparison.OrdinalIgnoreCase)) return true;
                // the PLC project node is called "<Plc>" in the Solution Explorer, "<Plc> Project" in the tree path
                return seg.EndsWith(" Project", StringComparison.OrdinalIgnoreCase) && string.Equals(n, seg.Substring(0, seg.Length - 8), StringComparison.OrdinalIgnoreCase);
            }
            List<EnvDTE.UIHierarchyItem> Kids(EnvDTE.UIHierarchyItem it)
            {
                var list = new List<EnvDTE.UIHierarchyItem>();
                try
                {
                    var k = it.UIHierarchyItems;
                    if (k == null) return list;
                    if (!k.Expanded) { k.Expanded = true; expanded.Add(k); }
                    foreach (EnvDTE.UIHierarchyItem c in k) list.Add(c);
                }
                catch { }
                return list;
            }
            // Search by the ORDER of the path segments: a node counts when its name is the next segment; nodes in between
            // (Solution, TwinCAT project, "PLC") are allowed in the upper levels; below level 3 only matched nodes are expanded
            EnvDTE.UIHierarchyItem Find(List<EnvDTE.UIHierarchyItem> items, int idx, int depth)
            {
                foreach (var x in items)
                {
                    bool match = Is(x, segs[idx]);
                    if (match && idx + 1 == segs.Length) return x;
                    if ((!match && depth > 3) || depth > 9) continue;
                    var kids = Kids(x);
                    if (match)
                    {
                        var hit = Find(kids, idx + 1, depth + 1);
                        if (hit != null) return hit;
                    }
                    if (depth <= 3)
                    {
                        var hit2 = Find(kids, idx, depth + 1);
                        if (hit2 != null) return hit2;
                    }
                }
                return null;
            }
            var level = new List<EnvDTE.UIHierarchyItem>();
            foreach (EnvDTE.UIHierarchyItem it in dte.ToolWindows.SolutionExplorer.UIHierarchyItems) level.Add(it);
            var cur = Find(level, 1, 0);
            // collapse nodes expanded for the search that are not on the way to the target
            var keep = new HashSet<EnvDTE.UIHierarchyItems>();
            try { for (var p = cur?.Collection?.Parent as EnvDTE.UIHierarchyItem; p != null; p = p.Collection?.Parent as EnvDTE.UIHierarchyItem) keep.Add(p.UIHierarchyItems); }
            catch { }
            foreach (var e in expanded)
                if (!keep.Contains(e)) try { e.Expanded = false; } catch { }
            if (cur == null) r["uiError"] = "tree node not found in Solution Explorer (path: " + string.Join(" > ", segs) + ")";
            return cur;
        }

        IEnumerable<object> FindByName(object lm, int ph, Guid obj, string name)
        {
            foreach (var m in AllMethods(lm.GetType()).Where(m => m.Name == "FindSignaturesByName" && m.GetParameters().Length == 3))
            {
                object res = null;
                try { res = m.Invoke(lm, new object[] { ph, obj, name }); } catch { }
                if (res is IEnumerable e) { var l = e.Cast<object>().Where(x => x != null).ToList(); if (l.Count > 0) return l; }
            }
            return Enumerable.Empty<object>();
        }

        Dictionary<string, object> Xref(string name, int max)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name missing");
            var lm = EnvProp("LanguageModelMgr");
            var (ph, _) = ActiveObject();
            var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name) + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var res = Call(lm, "GetPrecompiledCrossReferences", rx);
            var list = new List<object>();
            if (res is IDictionary dict)
                foreach (DictionaryEntry de in dict)
                {
                    var entry = new Dictionary<string, object> { ["key"] = Short(de.Key, 200) };
                    var refs = new List<object>();
                    if (de.Value is IEnumerable e && !(de.Value is string))
                        foreach (var x in e)
                        {
                            if (refs.Count >= max) break;
                            var d = Serialize(x, 1);
                            foreach (var kv in Members(x, "(?i)pos|line|offset|object|guid|sign")) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                            // Resolve location: object containing the access
                            Guid locGuid = Guid.Empty;
                            if (d.TryGetValue("Position", out var pv) && pv is Dictionary<string, object> pd)
                            {
                                if (pd.TryGetValue("ObjectGuid", out var og)) Guid.TryParse(Convert.ToString(og), out locGuid);
                                if (pd.TryGetValue("Position", out var pp) && long.TryParse(Convert.ToString(pp), out var praw))
                                {
                                    const long DeclFlag = 1L << 47;   // set = declaration part
                                    d["part"] = (praw & DeclFlag) != 0 ? "declaration" : "implementation";
                                    d["lineId"] = praw & (DeclFlag - 1);
                                }
                                if (pd.TryGetValue("PositionOffset", out var po)) d["column"] = Convert.ToInt32(po) + 1;
                                d.Remove("Position");
                            }
                            if (locGuid == Guid.Empty && d.TryGetValue("MessageGuid", out var mg)) Guid.TryParse(Convert.ToString(mg), out locGuid);
                            if (locGuid != Guid.Empty) d["location"] = ObjectPath(ph, locGuid);
                            foreach (var k in new[] { "ObjectGuid", "SignatureGuid", "Guid" })
                                if (d.TryGetValue(k, out var gv) && Guid.TryParse(Convert.ToString(gv), out var g) && g != Guid.Empty) { d["objectPath"] = ObjectPath(ph, g); break; }
                            foreach (var kv in d.ToList())
                                if (kv.Value is long lp && kv.Key.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0 && SplitPos(lp) is object[] sp) { d[kv.Key + "_Line"] = sp[0]; d[kv.Key + "_Offset"] = sp[1]; }
                            refs.Add(d);
                        }
                    else entry["value"] = Short(de.Value, 300);
                    entry["refs"] = refs;
                    list.Add(entry);
                }
            return new Dictionary<string, object> { ["resultType"] = res?.GetType().FullName, ["entries"] = list };
        }

        // Exploration: call only read-only methods (Get/Find/All/Is) of the IDE services
        Dictionary<string, object> LmCall(string target, string method, ArrayList args)
        {
            if (string.IsNullOrEmpty(method) || !System.Text.RegularExpressions.Regex.IsMatch(method, "^(Get|Find|All|Is)")) throw new Exception("only methods starting with Get/Find/All/Is are allowed");
            object t = (target ?? "lm") switch { "om" => EnvProp("ObjectMgr"), "engine" => EnvProp("Engine"), "view" => SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView"), _ => EnvProp("LanguageModelMgr") };
            var (ph, obj) = ActiveObject();
            foreach (var m in AllMethods(t.GetType()).Where(m => m.Name == method && m.GetParameters().Length == args.Count))
            {
                var ps = m.GetParameters(); var a = new object[ps.Length]; bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                {
                    var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                    var sv = Convert.ToString(args[i]);
                    if (ps[i].IsOut) continue;
                    try
                    {
                        if (sv == "$ph") a[i] = ph;
                        else if (sv == "$obj") a[i] = obj;
                        else if (pt == typeof(string)) a[i] = sv;
                        else if (pt == typeof(Guid)) a[i] = Guid.Parse(sv);
                        else if (pt == typeof(System.Text.RegularExpressions.Regex)) a[i] = new System.Text.RegularExpressions.Regex(sv, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        else if (pt == typeof(long[])) a[i] = sv.Length == 0 ? new long[0] : sv.Split(',').Select(long.Parse).ToArray();
                        else if (pt.IsEnum) a[i] = Enum.Parse(pt, sv, true);
                        else if (pt.IsPrimitive) a[i] = Convert.ChangeType(sv, pt, System.Globalization.CultureInfo.InvariantCulture);
                        else if (string.IsNullOrEmpty(sv) || sv == "null") a[i] = null;
                        else ok = false;
                    }
                    catch { ok = false; }
                }
                if (!ok) continue;
                var res = m.Invoke(t, a);
                var r = new Dictionary<string, object> { ["method"] = Sig(m), ["resultType"] = res?.GetType().FullName };
                if (res != null && !Simple(res) && res is IEnumerable e)
                {
                    var l = new List<object>();
                    foreach (var x in e) { if (l.Count >= 100) break; l.Add(x == null ? null : Simple(x) ? x.ToString() : (object)Serialize(x, 1)); }
                    r["result"] = l;
                }
                else r["result"] = res == null ? null : Simple(res) ? res.ToString() : (object)Serialize(res, 1);
                var outs = new Dictionary<string, object>();
                for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef && a[i] != null) outs[ps[i].Name] = Simple(a[i]) ? a[i].ToString() : (object)Serialize(a[i], 1);
                r["outs"] = outs;
                return r;
            }
            var cands = AllMethods(t.GetType()).Where(m => m.Name == method).Select(Sig).Distinct().ToList();
            throw new Exception($"No matching call {method}/{args.Count}. Candidates: " + string.Join(" | ", cands));
        }

        // ------------------------------------------------------------------ Project model
        Dictionary<string, object> LmExport(string library, int max)
        {
            if (string.IsNullOrEmpty(library)) throw new Exception("library missing");
            var lm = EnvProp("LanguageModelMgr") ?? throw new Exception("Project model not available");
            var infos = new List<object>();
            object hit = null, exact = null;
            foreach (var ctx in (IEnumerable)Call(lm, "AllPreCompileContexts", false, true))
            {
                var strings = new List<string>();
                foreach (var p in AllProps(ctx.GetType()))
                {
                    if (p.GetIndexParameters().Length > 0 || (p.PropertyType != typeof(string) && p.PropertyType != typeof(Guid))) continue;
                    try { var v = p.GetValue(ctx, null); if (v != null) strings.Add(p.Name + "=" + Short(v, 120)); } catch { }
                }
                var info = string.Join("; ", strings);
                infos.Add(info);
                // exact match "title, version" before substring (RobotLibrary must not match RobotLibrarySysDep)
                if (exact == null && info.IndexOf(library + ",", StringComparison.OrdinalIgnoreCase) >= 0) exact = ctx;
                if (hit == null && info.IndexOf(library, StringComparison.OrdinalIgnoreCase) >= 0) hit = ctx;
            }
            hit = exact ?? hit;
            var r = new Dictionary<string, object> { ["contexts"] = infos };
            if (hit == null) { r["found"] = false; return r; }

            // Find the context's signature list: evaluate all properties/parameterless methods with "Signature" in the name
            // and take the longest list (e.g. AllSignatures instead of empty serialization lists)
            IEnumerable sigs = null; string via = null; int best = -1;
            var cands = new List<string>();
            void Consider(string label, Func<object> get)
            {
                try
                {
                    if (!(get() is IEnumerable e) || e is string) return;
                    int n = 0; foreach (var _ in e) { if (++n > 200000) break; }
                    cands.Add(label + "=" + n);
                    if (n > best) { best = n; sigs = e; via = label; }
                }
                catch (Exception ex) { cands.Add(label + ": " + Inner(ex)); }
            }
            foreach (var p in AllProps(hit.GetType()).Where(p => p.GetIndexParameters().Length == 0 && p.Name.IndexOf("Signature", StringComparison.OrdinalIgnoreCase) >= 0))
                Consider("prop " + p.Name, () => p.GetValue(hit, null));
            foreach (var m in AllMethods(hit.GetType()).Where(m => m.GetParameters().Length == 0 && !m.IsSpecialName && m.Name.IndexOf("Signature", StringComparison.OrdinalIgnoreCase) >= 0))
                Consider("method " + m.Name, () => m.Invoke(hit, null));
            r["candidates"] = cands;
            r["found"] = true;
            r["contextType"] = hit.GetType().FullName; r["via"] = via;
            r["contextMembers"] = AllProps(hit.GetType()).Select(p => p.PropertyType.Name + " " + p.Name).Distinct().ToList();
            if (sigs == null) return r;
            var list = new List<object>();
            foreach (var sgn in sigs) { if (list.Count >= max) break; list.Add(Serialize(sgn, 0)); }
            string cacheDir = Path.Combine(Path.GetDirectoryName(dir), "libcache");
            Directory.CreateDirectory(cacheDir);
            string file = Path.Combine(cacheDir, Sanitize(library) + ".lm.json");
            File.WriteAllText(file, json.Serialize(new Dictionary<string, object> { ["Name"] = library, ["Signatures"] = list }), new UTF8Encoding(false));
            r["file"] = file; r["count"] = list.Count;
            return r;
        }

        static IEnumerable<PropertyInfo> AllProps(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Concat(t.GetInterfaces().SelectMany(i => i.GetProperties()))
             .GroupBy(p => p.Name).Select(g => g.First());

        static bool Simple(object v) => v is string || v is Guid || v is Enum || v is DateTime || v is Version || v.GetType().IsPrimitive || v is decimal;

        // Generic mapping of a project model object (signature, variable ...) - limited depth
        Dictionary<string, object> Serialize(object o, int depth)
        {
            var d = new Dictionary<string, object> { ["_type"] = o.GetType().Name };
            foreach (var p in AllProps(o.GetType()))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(o, null); } catch { continue; }
                if (v == null) continue;
                if (Simple(v)) d[p.Name] = v is Enum || v is Guid || v is Version || v is DateTime ? v.ToString() : v;
                else if (depth < 2 && v is IEnumerable e)
                {
                    var arr = new List<object>();
                    foreach (var x in e) { if (arr.Count >= 300) break; if (x != null) arr.Add(Simple(x) ? x.ToString() : depth < 1 ? (object)Serialize(x, depth + 1) : Short(x, 200)); }
                    if (arr.Count > 0) d[p.Name] = arr;
                }
                else { var s = Short(v, 300); if (!s.StartsWith(v.GetType().FullName ?? "\0")) d[p.Name] = s; }
            }
            return d;
        }

        // ------------------------------------------------------------------ Probe: what is technically reachable?
        Dictionary<string, object> Probe()
        {
            var sb = new StringBuilder($"TcAgentBridge {BridgeVersion} Probe {DateTime.Now:s}  pid={System.Diagnostics.Process.GetCurrentProcess().Id} 64bit={Environment.Is64BitProcess}\n");
            var summary = new Dictionary<string, object>();
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            var cds = asms.Where(a => Rx(a.GetName().Name, "_3S|CoDeSys|LibManObject|TcDocExport|LanguageModel|^Core$|ComponentModel|Objects")).ToList();
            summary["assemblies"] = asms.Length; summary["ideAssemblies"] = cds.Count;
            sb.AppendLine($"\n## Assemblies: {asms.Length}, IDE: {cds.Count}");
            foreach (var a in cds) sb.AppendLine("  " + a.FullName);

            sb.AppendLine("\n## Components");
            try { Env(sb); summary["exporter"] = exporter.GetType().FullName; sb.AppendLine("OK " + exporter.GetType().AssemblyQualifiedName); }
            catch (Exception ex) { summary["exporter"] = "ERROR: " + Inner(ex); sb.AppendLine("ERROR " + Inner(ex)); }

            if (apEnv != null)
            {
                sb.AppendLine("\n## APEnvironment");
                foreach (var p in apEnv.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    object v = null; string err = null;
                    try { v = p.GetValue(null, null); } catch (Exception ex) { err = Inner(ex); }
                    sb.AppendLine($"  {p.Name} : {p.PropertyType.Name} = {(err ?? v?.GetType().FullName ?? "null")}");
                    summary["env." + p.Name] = err == null ? (v == null ? "null" : "ok") : "error";
                }
                DumpMembers(sb, "Engine", EnvProp("Engine"));
                var frame = SafeGet(EnvProp("Engine"), "Frame");
                DumpMembers(sb, "Engine.Frame", frame);
                var view = SafeGet(frame, "ActiveView");
                summary["cdsActiveView"] = view?.GetType().FullName ?? "null";
                DumpMembers(sb, "Engine.Frame.ActiveView", view);
                DumpMembers(sb, "Project model", EnvProp("LanguageModelMgr"));
                var projects = SafeGet(SafeGet(EnvProp("Engine"), "Projects"), "Projects") as IEnumerable;
                if (projects != null)
                {
                    sb.AppendLine("\n## Engine.Projects");
                    foreach (var pr in projects) sb.AppendLine($"  Handle={SafeGet(pr, "Handle")} Path={SafeGet(pr, "Path")} Primary={SafeGet(pr, "Primary")} Library={SafeGet(pr, "Library")}");
                }
            }

            sb.AppendLine("\n## Editor context");
            var ctx = Context();
            foreach (var kv in ctx) sb.AppendLine($"  {kv.Key} = {(kv.Value is IEnumerable e && !(kv.Value is string) ? string.Join("\n      ", e.Cast<object>()) : kv.Value)}");
            summary["selectionSource"] = ctx.ContainsKey("selectionSource") ? ctx["selectionSource"] : "none";
            try
            {
                var mon = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
                mon.GetCurrentElementValue(2, out object fo);
                object dv = null; (fo as IVsWindowFrame)?.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out dv);
                DumpMembers(sb, "DocView", dv);
            }
            catch (Exception ex) { sb.AppendLine("DocView: " + Inner(ex)); }
            var fc = System.Windows.Forms.Control.FromChildHandle(GetFocus());
            for (int i = 0; fc != null && i < 3; i++, fc = fc.Parent) DumpMembers(sb, "Focus[" + i + "]", fc);

            sb.AppendLine("\n## PositionHelper");
            foreach (var t in asms.SelectMany(SafeTypes).Where(t => t.Name == "PositionHelper"))
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)) sb.AppendLine($"  {t.FullName}.{Sig(m)}");

            string file = Path.Combine(dir, "probe.txt");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            summary["file"] = file;
            return summary;
        }

        void DumpMembers(StringBuilder sb, string label, object o)
        {
            sb.AppendLine($"\n## {label}: {(o == null ? "null" : o.GetType().FullName)}");
            if (o == null) return;
            var t = o.GetType();
            sb.AppendLine("  Interfaces: " + string.Join(", ", t.GetInterfaces().Select(i => i.Name).OrderBy(n => n)));
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
            {
                string val = "";
                if (p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(Guid) || p.PropertyType.IsEnum))
                    try { val = " = " + Short(p.GetValue(o, null), 120); } catch { val = " = (error)"; }
                sb.AppendLine($"  prop {p.PropertyType.Name} {p.Name}{val}");
            }
            foreach (var m in AllMethods(t).Where(m => !m.IsSpecialName).Select(Sig).Distinct().OrderBy(s => s).Take(250))
                sb.AppendLine("  " + m);
        }

        // ------------------------------------------------------------------ Components
        IEnumerable<object> FindComponentManagers(StringBuilder trace)
        {
            var seen = new HashSet<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!Rx(asm.GetName().Name, "_3S|CoDeSys|Core|ComponentModel|Engine")) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t.Name.IndexOf("ComponentManager", StringComparison.Ordinal) < 0 || !seen.Add(t)) continue;
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        object inst = null;
                        try { inst = p.GetValue(null, null); } catch { }
                        if (inst != null) { trace?.AppendLine($"  {t.FullName}.{p.Name} -> {inst.GetType().FullName}"); yield return inst; }
                    }
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        object inst = null;
                        try { inst = f.GetValue(null); } catch { }
                        if (inst != null && !(inst is string) && !inst.GetType().IsPrimitive) { trace?.AppendLine($"  {t.FullName}.{f.Name} -> {inst.GetType().FullName}"); yield return inst; }
                    }
                }
            }
        }

        object TryCreate(object cm, Guid typeGuid, StringBuilder trace)
        {
            var methods = AllMethods(cm.GetType()).Where(mi =>
            {
                var ps = mi.GetParameters();
                return ps.Length == 1 && ps[0].ParameterType == typeof(Guid) && mi.ReturnType != typeof(void);
            }).OrderBy(x => x.Name == "CreateInstance" ? 0 : Rx(x.Name, "Create|Instance") ? 1 : 2).ToList();
            foreach (var m in methods)
            {
                try
                {
                    object r = m.Invoke(cm, new object[] { typeGuid });
                    if (r != null) { trace?.AppendLine($"  {m.Name}(Export) -> {r.GetType().FullName}"); return r; }
                }
                catch (Exception ex) { trace?.AppendLine($"  {m.Name}: {Inner(ex)}"); }
            }
            return null;
        }

        // ------------------------------------------------------------------ Helpers
        [DllImport("user32.dll")] static extern IntPtr GetFocus();

        static IEnumerable<MethodInfo> AllMethods(Type t) =>
            t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Concat(t.GetInterfaces().SelectMany(i => i.GetMethods()));

        static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new NullReferenceException("Target for " + name + " is null");
            foreach (var m in AllMethods(target.GetType()).Where(x => x.Name == name))
            {
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                    if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) { ok = false; break; }
                if (ok) return m.Invoke(target, args);
            }
            throw new MissingMethodException(target.GetType().FullName, name + "/" + args.Length);
        }

        static object SafeCall(object target, string name, params object[] args) { try { return Call(target, name, args); } catch { return null; } }

        static object Get(object target, string prop)
        {
            if (target == null) return null;
            var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                    target.GetType().GetInterfaces().Select(i => i.GetProperty(prop)).FirstOrDefault(x => x != null);
            return p?.GetValue(target, null);
        }

        static object SafeGet(object target, string prop) { try { return Get(target, prop); } catch { return null; } }

        static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return new Type[0]; }
        }

        static string Sig(MethodInfo m) => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})";
        static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        static bool Rx(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", pattern);
        static string Short(object o, int n) { var s = o?.ToString() ?? "null"; return s.Length > n ? s.Substring(0, n) + "..." : s; }
        static string Inner(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex.GetType().Name + ": " + ex.Message; }
        static string Sanitize(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(dir ?? Path.GetTempPath(), "bridge.log"), $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}", Encoding.UTF8); }
            catch { }
        }
    }
}
