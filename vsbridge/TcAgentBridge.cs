// TcAgentBridge - VS-2022-Erweiterung: Bruecke zwischen TcAgent (MCP-Server, eigener Prozess) und dem IDE-Prozess.
// Laeuft im Prozess von Visual Studio und kommt damit an das heran, was ueber DTE/Automation Interface nicht geht:
// - Cursor und Selektion im TwinCAT-Editor
// - Signaturen aus (compiled) libraries aus der IDE
// Protokoll: Named Pipe "TcAgentBridge.<pid>", je Verbindung eine JSON-Zeile hin, eine JSON-Zeile zurueck.
// Alle IDE-Zugriffe laufen auf dem UI-Thread.
// Log und Ausgaben: %LOCALAPPDATA%\TcAgent\bridge\ ; Bibliotheks-Cache: %LOCALAPPDATA%\TcAgent\libcache\
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
        public const string BridgeVersion = "0.13.0";
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
                Log($"==== TcAgentBridge {BridgeVersion} gestartet, pid={System.Diagnostics.Process.GetCurrentProcess().Id}, 64bit={Environment.Is64BitProcess}");
                server = new Thread(ServerLoop) { IsBackground = true, Name = "TcAgentBridge" };
                server.Start();
                Instance = this;
                // Tastenkuerzel "aus dem Code fragen" (Standard Strg+Umschalt+Q, "askHotkey" in chat-backends.json)
                ui?.BeginInvoke(new Action(InstallHotkey), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // VSIX-Installation: TcAgent bereitstellen, Interop erzeugen, KI-Clients einrichten (erstes Mal nach Rueckfrage)
                ui?.BeginInvoke(new Action(() =>
                {
                    try { Installer.OnStartup(Log, Notify); } catch (Exception ex) { Log("Installer: " + ex.Message); }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Chat-Fenster beim Start zeigen (abschaltbar: "showOnStartup": false in chat-backends.json)
                if (ShowChatOnStartup() && ui != null)
                    ui.BeginInvoke(new Action(() =>
                    {
                        try { ShowChat(); } catch (Exception ex) { Log("Chat-Fenster: " + ex.Message); }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { Log("Init-Fehler: " + ex); }
            return System.Threading.Tasks.Task.FromResult(0);
        }

        protected override void Dispose(bool disposing)
        {
            stop = true;
            try { if (hookHandle != IntPtr.Zero) UnhookWindowsHookEx(hookHandle); hookHandle = IntPtr.Zero; } catch { }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ Pipe-Server
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
                catch (Exception ex) { Log("Pipe-Fehler: " + ex.Message); Thread.Sleep(500); }
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
                        case "probe": return Probe();
                        case "libs": return Libs();
                        case "lib_export": return LibExport(Str(req, "library"), Str(req, "version"), Str(req, "refresh") == "True");
                        case "goto": return Goto(Str(req, "name"));
                        case "xref": return Xref(Str(req, "name"), int.TryParse(Str(req, "max"), out var xm) ? xm : 300);
                        case "lm_call": return LmCall(Str(req, "target"), Str(req, "method"), req.TryGetValue("args", out var ar) ? ar as ArrayList ?? new ArrayList((ar as object[]) ?? new object[0]) : new ArrayList());
                        case "lm_export": return LmExport(Str(req, "library"), int.TryParse(Str(req, "max"), out var mx) ? mx : 100000);
                        default: throw new Exception("Unbekannter Befehl: " + cmd);
                    }
                }));
            CurrentOp = null;
            r["ok"] = true;
            r["ms"] = sw.ElapsedMilliseconds;
            Log("-> " + cmd + " ok (" + sw.ElapsedMilliseconds + " ms)");
            return r;
        }

        // ------------------------------------------------------------------ Chat-Fenster
        internal Dictionary<string, object> ContextForChat() => Context();

        IVsWindowFrame chatFrame;

        // Meldung im Chat-Fenster (aus beliebigem Thread)
        internal void Notify(string msg)
        {
            Log(msg);
            ui?.BeginInvoke(new Action(() =>
            {
                try { if (ChatControl.Instance == null) ShowChat(false); ChatControl.Instance?.Notice(msg); } catch { }
            }));
        }

        // ------------------------------------------------------------------ Tastenkuerzel: aus dem Code-Fenster fragen
        System.Windows.Forms.Keys hotKey = System.Windows.Forms.Keys.Q;
        System.Windows.Forms.Keys hotMods = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift;
        IVsWindowFrame returnFrame;

        void InstallHotkey()
        {
            try
            {
                statusWatch = !(ChatSetting("statusLog") is bool sw0 && !sw0);   // vorerst standardmaessig an (Diagnose)
                StartStallMonitor();
                var spec = ChatSetting("askHotkey") as string ?? "Ctrl+Shift+Q";
                if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) { Log("Tastenkuerzel abgeschaltet"); return; }
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
                // Tastatur-Hook nur fuer den UI-Thread von VS: sieht die Taste, bevor VS/TwinCAT-Editor sie verarbeiten
                // (ComponentDispatcher bekommt die Tasten des WinForms-Editors nicht zu sehen - getestet).
                keyHook = OnKeyHook;
                hookHandle = SetWindowsHookEx(2 /* WH_KEYBOARD */, keyHook, IntPtr.Zero, GetCurrentThreadId());
                Log($"Tastenkuerzel fuer den Chat: {spec}" + (hookHandle == IntPtr.Zero ? $" - Hook FEHLGESCHLAGEN ({Marshal.GetLastWin32Error()})" : ""));
            }
            catch (Exception ex) { Log("Tastenkuerzel: " + Inner(ex)); }
        }

        // Diagnose: misst, wie lange der UI-Thread von VS blockiert ist (Ticks alle 50 ms). Haenger > 150 ms landen im Log,
        // zusammen mit dem, was die Bruecke gerade tat - so laesst sich trennen: TcAgent, TwinCAT selbst oder etwas anderes.
        internal static volatile string CurrentOp;
        // Statusleisten-Wechsel mitschreiben ("statusLog": true in chat-backends.json) - zur Diagnose von Haengern
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
                    Log($"UI blockiert {ms} ms (TcAgent-Werkzeug: {agent}, Bruecke: {CurrentOp ?? "-"}, Fokus: {fg}, Statusleiste: \"{StatusText()}\")");
                }
                if (statusWatch)
                {
                    var st = StatusText();
                    if (st != lastStatus && !string.IsNullOrWhiteSpace(st) && !System.Text.RegularExpressions.Regex.IsMatch(st, "^(Bereit|Ready)$"))
                        Log("Statusleiste: " + st);
                    lastStatus = st;
                }
                last.Restart();
            };
            t.Start();
        }

        // Text der VS-Statusleiste - zeigt oft, was TwinCAT/VS gerade im Hintergrund tut
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
        HookProc keyHook;          // Feld haelt den Delegaten am Leben (sonst sammelt ihn der GC ein)
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
                            Log("Tastenkuerzel gedrueckt");
                            try { AskFromEditor(); } catch (Exception ex) { Log("Fragen aus dem Editor: " + Inner(ex)); }
                        }));
                    return (IntPtr)1;   // Taste verschlucken, damit sie nicht im Code landet
                }
            }
            catch { }
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        // Aus dem Code fragen: kleines Eingabefeld an der Cursorzeile (InlineAsk). Steht der Fokus schon im Chat, nur dorthin.
        internal void AskFromEditor()
        {
            try
            {
                var ms = (IVsMonitorSelection)GetService(typeof(SVsShellMonitorSelection));
                if (ms != null && ms.GetCurrentElementValue(1 /* SEID_DocumentFrame */, out var f) == 0 && f is IVsWindowFrame wf) returnFrame = wf;
            }
            catch { }
            if (ChatControl.Instance != null && ChatControl.Instance.IsKeyboardFocusWithin) { ChatControl.Instance.FocusInput(); return; }
            // Kontext jetzt erfassen - der Editor hat in diesem Moment noch den Fokus
            string ctx = null;
            try { ctx = ChatControl.FormatContext(Context()); } catch (Exception ex) { Log("Kontext: " + Inner(ex)); }
            InlineAsk.Open(ctx, ChatControl.Instance?.IsBusy == true, ChatControl.DefaultBackendName());
        }

        // Frage aus dem Inline-Feld: Chat sichtbar machen (ohne Fokuswechsel) und abschicken
        internal void SendInline(string text, string ctx)
        {
            try { ShowChat(false); } catch (Exception ex) { Log("Chat-Fenster: " + Inner(ex)); }
            ui.BeginInvoke(new Action(() =>
            {
                if (ChatControl.Instance == null) { Log("Inline-Frage: Chat-Fenster nicht verfuegbar"); return; }
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

        // activate=false: Fenster sichtbar machen, ohne den Fokus aus dem Code zu nehmen (Inline-Frage)
        internal void ShowChat(bool activate)
        {
            if (chatFrame != null && (activate ? chatFrame.Show() : chatFrame.ShowNoActivate()) == 0) return;
            // 1) Standardweg (braucht die ToolWindow-Registrierung, die sonst das VS-SDK erzeugt)
            try
            {
                var w = FindToolWindow(typeof(ChatPane), 0, true);
                if (w?.Frame is IVsWindowFrame f) { chatFrame = f; Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(activate ? f.Show() : f.ShowNoActivate()); return; }
            }
            catch (Exception ex) { Log("FindToolWindow: " + Inner(ex) + " - lege Fenster direkt an"); }
            // 2) Direkt ueber die VS-Shell anlegen
            var shell = (IVsUIShell)GetService(typeof(SVsUIShell)) ?? throw new Exception("SVsUIShell nicht verfuegbar");
            var pane = new ChatPane();
            Guid empty = Guid.Empty, persist = typeof(ChatPane).GUID, auto = Guid.Empty;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(shell.CreateToolWindow(
                (uint)__VSCREATETOOLWIN.CTW_fInitNew, 0, pane, ref empty, ref persist, ref auto, null, "TcAgent Chat", null, out var frame));
            chatFrame = frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(activate ? frame.Show() : frame.ShowNoActivate());
            Log("Chat-Fenster angelegt (IVsUIShell)");
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

        // ------------------------------------------------------------------ IDE-Umgebung
        object exporter;
        Type apEnv;

        // Zugang zu den IDE-Diensten herstellen
        Type Env(StringBuilder trace = null)
        {
            if (apEnv != null) return apEnv;
            if (exporter == null)
                foreach (var cm in FindComponentManagers(trace))
                {
                    exporter = TryCreate(cm, ExportTypeGuid, trace);
                    if (exporter != null) break;
                }
            if (exporter == null) throw new Exception("Export-Komponente nicht verfuegbar - ist ein PLC-Projekt geladen?");
            apEnv = SafeTypes(exporter.GetType().Assembly).FirstOrDefault(t => t.Name == "APEnvironment");
            if (apEnv == null) throw new Exception("Umgebung der Export-Komponente nicht gefunden");
            return apEnv;
        }

        object EnvProp(string name)
        {
            var p = Env().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return p?.GetValue(null, null);
        }

        // ------------------------------------------------------------------ Editor-Kontext
        Dictionary<string, object> Context()
        {
            var r = new Dictionary<string, object>();
            // 1) VS-Sicht: aktives Dokumentfenster
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

            // 2) Editor-Sicht: aktive Editoransicht
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

            // Editor-Sicht: Auswahl der Editoransicht - unabhaengig vom Tastaturfokus in VS
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

            // Beide Haelften (Deklaration/Implementierung) einzeln: jede hat ihr eigenes Editor-Control mit Cursor und
            // Markierung - unabhaengig davon, welches Fenster in VS gerade den Fokus hat (getestet, s. Konzept Spike 4)
            try
            {
                var panes = new List<Dictionary<string, object>>();
                foreach (var part in new[] { new[] { "InterfaceEditorView", "Deklaration" }, new[] { "ImplementationEditorView", "Implementierung" } })
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
                    // Spalte = Zeichenposition minus Beginn der Zeile (Zeilenende \r\n oder \n)
                    int nl = text.Contains("\r\n") ? 2 : 1, start = 0;
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < line && i < lines.Length; i++) start += lines[i].Length + nl;
                    // Codeausschnitt um den Cursor (+-6 Zeilen) - spart dem Agenten einen Werkzeugaufruf
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
                    // Aktive Haelfte: Fokus > Markierung > Cursor abseits von Zeile 1/Spalte 1
                    var act = panes.FirstOrDefault(p => (bool)p["focused"])
                           ?? (panes.Count(p => (int)p["selectionCount"] > 0) == 1 ? panes.First(p => (int)p["selectionCount"] > 0) : null)
                           ?? (panes.Count(p => (int)p["line"] > 1 || (int)p["column"] > 1) == 1 ? panes.First(p => (int)p["line"] > 1 || (int)p["column"] > 1) : null);
                    if (act != null) r["active"] = act;
                }
            }
            catch (Exception ex) { r["panesError"] = Inner(ex); }

            // VS-Sicht: TwinCATs VSEditor implementiert IVsTextView -> Cursor, Selektion, markierter Text
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
                // Welcher Teil hat den Cursor: Deklaration oder Implementierung?
                try
                {
                    var active = SafeCall(docView, "GetActiveSynEdControl", SafeGet(docView, "EditorView")) as System.Windows.Forms.Control;
                    r["activeEditorControl"] = active?.GetType().FullName;
                    foreach (var part in new[] { new[] { "InterfaceEditorView", "Deklaration" }, new[] { "ImplementationEditorView", "Implementierung" } })
                        if (active != null && SafeGet(cdsViewObj, part[0]) is System.Windows.Forms.Control pc && (pc == active || pc.Contains(active)))
                            r["part"] = part[1];
                }
                catch (Exception ex) { r["partError"] = Inner(ex); }
            }

            // 3) Fokussiertes WinForms-Control (TwinCAT-Editoren) + WPF-Fokus
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

            // Hat keine Haelfte den Fokus (z. B. weil gerade im Chat-Fenster getippt wird) und ist sie auch sonst nicht
            // eindeutig: ueber die durchgehende VS-Zeilennummer (Deklaration + Implementierung) entscheiden.
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

        // Eigenschaften UND Felder (auch nicht oeffentliche), deren Name zum Muster passt - fuer die Diagnose
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
                // ein Level tiefer fuer Strukturen (z. B. Caret/Range): einfache Felder/Eigenschaften
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

        // Position in Zeile und Spalte zerlegen
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

        // Methode mit ausschliesslich by-ref-Parametern aufrufen (COM-Stil, HRESULT-Rueckgabe)
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

        // Auswahl lesen
        bool TryGetSelection(object view, Dictionary<string, object> r)
        {
            var m = AllMethods(view.GetType()).FirstOrDefault(x => x.Name == "GetSelection" && x.GetParameters().Length == 5 && x.GetParameters().All(p => p.ParameterType.IsByRef));
            if (m == null) return false;
            var args = new object[5];
            try { m.Invoke(view, args); }
            catch (Exception ex) { r["selectionError"] = Inner(ex); return false; }
            r["online"] = args[0]; r["projectHandle"] = args[1]; r["objectGuid"] = args[2]?.ToString();
            long pos = Convert.ToInt64(args[3]); r["position"] = pos; r["length"] = args[4];
            // Position zerlegen
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
            // Objektname und Pfad aus dem ObjectManager
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

        // ------------------------------------------------------------------ Bibliotheken
        Dictionary<string, object> Libs()
        {
            var mgr = EnvProp("ManagedLibraryMgr") ?? throw new Exception("ManagedLibraryMgr nicht verfuegbar");
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
            if (string.IsNullOrEmpty(title)) throw new Exception("library fehlt");
            var mgr = EnvProp("ManagedLibraryMgr") ?? throw new Exception("ManagedLibraryMgr nicht verfuegbar");
            var hits = ((IEnumerable)Call(mgr, "GetAllLibraries", true)).Cast<object>()
                .Where(l => string.Equals(Convert.ToString(Get(l, "Title")), title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0) throw new Exception($"Bibliothek '{title}' ist nicht im Repository installiert");
            object lib = string.IsNullOrEmpty(version) || version == "*"
                ? hits.OrderByDescending(l => (Version)Get(l, "Version")).First()
                : hits.FirstOrDefault(l => Convert.ToString(Get(l, "Version")) == version)
                  ?? throw new Exception($"Version {version} von '{title}' nicht installiert; vorhanden: " + string.Join(", ", hits.Select(l => Get(l, "Version"))));
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
            if (string.IsNullOrEmpty(result)) throw new Exception("Export lieferte nichts: " + string.Join(" | ", tried));
            File.WriteAllText(file, result, new UTF8Encoding(false));
            r["cached"] = false; r["chars"] = result.Length;
            return r;
        }

        // ------------------------------------------------------------------ Navigation: Gehe zu Definition / Querverweise
        // Aktuelles Objekt im Editor (fuer Namensaufloesung im richtigen Gueltigkeitsbereich)
        (int ph, Guid obj) ActiveObject()
        {
            var view = SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
            var g = SafeGet(view, "ObjectGuid"); var h = SafeGet(view, "ProjectHandle");
            return (h is int i ? i : 0, g is Guid gg ? gg : Guid.Empty);
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
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name fehlt");
            var lm = EnvProp("LanguageModelMgr");
            var (ph, obj) = ActiveObject();
            var r = new Dictionary<string, object> { ["name"] = name, ["context"] = ObjectPath(ph, obj), ["projectHandle"] = ph };
            var parts = name.Split('.');
            var first = parts[0];

            // 1) Variable des aktuellen Objekts bzw. seines FBs (Methode -> Elternobjekt), inkl. Basisklassen
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
                // Typ der Variablen aufloesen (ohne ARRAY/POINTER/REFERENCE-Praefix)
                var bare = System.Text.RegularExpressions.Regex.Replace(type ?? "", @"^(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bare = bare.Split('(')[0].Trim();
                var ts = FindByName(lm, ph, obj, bare).FirstOrDefault();
                if (ts != null) r["type"] = SigInfo(ts, ph);
                break;
            }

            // 2) Typ / FB / Funktion / GVL mit diesem Namen (Projekt und Bibliotheken)
            if (!r.ContainsKey("variable"))
            {
                var hits = FindByName(lm, ph, obj, first).Take(10).Select(x => (object)SigInfo(x, ph)).ToList();
                if (hits.Count == 0 && parts.Length > 1) hits = FindByName(lm, ph, obj, parts[parts.Length - 1]).Take(10).Select(x => (object)SigInfo(x, ph)).ToList();
                r["signatures"] = hits;
            }
            // 3) Punktierte Namen (GVL.Variable, instanz.Eingang, a.b.c): Glied fuer Glied ueber die Typen weiterreichen
            if (parts.Length > 1)
            {
                object owner = null;
                if (r.TryGetValue("type", out var tinfo) && tinfo is Dictionary<string, object> ti) owner = FindByName(lm, ph, obj, Convert.ToString(ti["name"])).FirstOrDefault();
                else owner = FindByName(lm, ph, obj, first).FirstOrDefault();
                var chain = new List<object>();
                for (int i = 1; i < parts.Length && owner != null; i++)
                {
                    object hitVar = null, hitSig = owner;
                    // auch in Basisklassen suchen
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
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name fehlt");
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
                            // Ort aufloesen: Objekt, in dem der Zugriff steht
                            Guid locGuid = Guid.Empty;
                            if (d.TryGetValue("Position", out var pv) && pv is Dictionary<string, object> pd)
                            {
                                if (pd.TryGetValue("ObjectGuid", out var og)) Guid.TryParse(Convert.ToString(og), out locGuid);
                                if (pd.TryGetValue("Position", out var pp) && long.TryParse(Convert.ToString(pp), out var praw))
                                {
                                    const long DeclFlag = 1L << 47;   // gesetzt = Deklarationsteil
                                    d["part"] = (praw & DeclFlag) != 0 ? "Deklaration" : "Implementierung";
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

        // Erkundung: nur lesende Methoden (Get/Find/All/Is) der IDE-Dienste aufrufen
        Dictionary<string, object> LmCall(string target, string method, ArrayList args)
        {
            if (string.IsNullOrEmpty(method) || !System.Text.RegularExpressions.Regex.IsMatch(method, "^(Get|Find|All|Is)")) throw new Exception("nur Methoden mit Get/Find/All/Is erlaubt");
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
            throw new Exception($"Kein passender Aufruf {method}/{args.Count}. Kandidaten: " + string.Join(" | ", cands));
        }

        // ------------------------------------------------------------------ Projektmodell
        Dictionary<string, object> LmExport(string library, int max)
        {
            if (string.IsNullOrEmpty(library)) throw new Exception("library fehlt");
            var lm = EnvProp("LanguageModelMgr") ?? throw new Exception("Projektmodell nicht verfuegbar");
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
                // exakter Treffer "titel, version" vor Teilstring (RobotLibrary darf nicht RobotLibrarySysDep treffen)
                if (exact == null && info.IndexOf(library + ",", StringComparison.OrdinalIgnoreCase) >= 0) exact = ctx;
                if (hit == null && info.IndexOf(library, StringComparison.OrdinalIgnoreCase) >= 0) hit = ctx;
            }
            hit = exact ?? hit;
            var r = new Dictionary<string, object> { ["contexts"] = infos };
            if (hit == null) { r["found"] = false; return r; }

            // Signaturliste des Kontexts finden: alle Eigenschaften/parameterlosen Methoden mit "Signature" im Namen
            // auswerten und die laengste Liste nehmen (z. B. AllSignatures statt leerer Serialisierungs-Listen)
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

        // Generische Abbildung eines Projektmodell-Objekts (Signatur, Variable ...) - Tiefe begrenzt
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

        // ------------------------------------------------------------------ Probe: was ist technisch erreichbar?
        Dictionary<string, object> Probe()
        {
            var sb = new StringBuilder($"TcAgentBridge {BridgeVersion} Probe {DateTime.Now:s}  pid={System.Diagnostics.Process.GetCurrentProcess().Id} 64bit={Environment.Is64BitProcess}\n");
            var summary = new Dictionary<string, object>();
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            var cds = asms.Where(a => Rx(a.GetName().Name, "_3S|CoDeSys|LibManObject|TcDocExport|LanguageModel|^Core$|ComponentModel|Objects")).ToList();
            summary["assemblies"] = asms.Length; summary["ideAssemblies"] = cds.Count;
            sb.AppendLine($"\n## Assemblies: {asms.Length}, IDE: {cds.Count}");
            foreach (var a in cds) sb.AppendLine("  " + a.FullName);

            sb.AppendLine("\n## Komponenten");
            try { Env(sb); summary["exporter"] = exporter.GetType().FullName; sb.AppendLine("OK " + exporter.GetType().AssemblyQualifiedName); }
            catch (Exception ex) { summary["exporter"] = "FEHLER: " + Inner(ex); sb.AppendLine("FEHLER " + Inner(ex)); }

            if (apEnv != null)
            {
                sb.AppendLine("\n## APEnvironment");
                foreach (var p in apEnv.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    object v = null; string err = null;
                    try { v = p.GetValue(null, null); } catch (Exception ex) { err = Inner(ex); }
                    sb.AppendLine($"  {p.Name} : {p.PropertyType.Name} = {(err ?? v?.GetType().FullName ?? "null")}");
                    summary["env." + p.Name] = err == null ? (v == null ? "null" : "ok") : "Fehler";
                }
                DumpMembers(sb, "Engine", EnvProp("Engine"));
                var frame = SafeGet(EnvProp("Engine"), "Frame");
                DumpMembers(sb, "Engine.Frame", frame);
                var view = SafeGet(frame, "ActiveView");
                summary["cdsActiveView"] = view?.GetType().FullName ?? "null";
                DumpMembers(sb, "Engine.Frame.ActiveView", view);
                DumpMembers(sb, "Projektmodell", EnvProp("LanguageModelMgr"));
                var projects = SafeGet(SafeGet(EnvProp("Engine"), "Projects"), "Projects") as IEnumerable;
                if (projects != null)
                {
                    sb.AppendLine("\n## Engine.Projects");
                    foreach (var pr in projects) sb.AppendLine($"  Handle={SafeGet(pr, "Handle")} Path={SafeGet(pr, "Path")} Primary={SafeGet(pr, "Primary")} Library={SafeGet(pr, "Library")}");
                }
            }

            sb.AppendLine("\n## Editor-Kontext");
            var ctx = Context();
            foreach (var kv in ctx) sb.AppendLine($"  {kv.Key} = {(kv.Value is IEnumerable e && !(kv.Value is string) ? string.Join("\n      ", e.Cast<object>()) : kv.Value)}");
            summary["selectionSource"] = ctx.ContainsKey("selectionSource") ? ctx["selectionSource"] : "keine";
            try
            {
                var mon = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
                mon.GetCurrentElementValue(2, out object fo);
                object dv = null; (fo as IVsWindowFrame)?.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out dv);
                DumpMembers(sb, "DocView", dv);
            }
            catch (Exception ex) { sb.AppendLine("DocView: " + Inner(ex)); }
            var fc = System.Windows.Forms.Control.FromChildHandle(GetFocus());
            for (int i = 0; fc != null && i < 3; i++, fc = fc.Parent) DumpMembers(sb, "Fokus[" + i + "]", fc);

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
                    try { val = " = " + Short(p.GetValue(o, null), 120); } catch { val = " = (Fehler)"; }
                sb.AppendLine($"  prop {p.PropertyType.Name} {p.Name}{val}");
            }
            foreach (var m in AllMethods(t).Where(m => !m.IsSpecialName).Select(Sig).Distinct().OrderBy(s => s).Take(250))
                sb.AppendLine("  " + m);
        }

        // ------------------------------------------------------------------ Komponenten
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

        // ------------------------------------------------------------------ Hilfsfunktionen
        [DllImport("user32.dll")] static extern IntPtr GetFocus();

        static IEnumerable<MethodInfo> AllMethods(Type t) =>
            t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Concat(t.GetInterfaces().SelectMany(i => i.GetMethods()));

        static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new NullReferenceException("Ziel fuer " + name + " ist null");
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
