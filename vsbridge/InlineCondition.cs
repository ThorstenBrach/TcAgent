// TcAgentBridge - the condition under the cursor as a logic diagram, in a small popup right at the code (Ctrl+Alt+B,
// "conditionHotkey" in chat-backends.json). Built after the approved mockup: the sub-conditions as a list with a lamp, name and
// the comment of the declaration; gates (&, ≥1, =1) with right-angled lines, NOT as a dot at the input; everything that is
// fulfilled turns green (lines, gates filled, result), the rest stays in the text color, what cannot be read is grey and dashed.
//
//   TcAgent.exe condition --json --pou ...        the structure, the sub-conditions, where each variable comes from
//   TcAgent.exe condition --online --watch 500    the values over ADS, one JSON line per reading (only reads)
//   The instance is chosen in the popup (the first one found, then the last one used for this POU).
//
// SPDX-License-Identifier: MIT
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
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        System.Windows.Forms.Keys condKey = System.Windows.Forms.Keys.B;
        System.Windows.Forms.Keys condMods = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt;
        bool condOn = true;

        void ReadConditionHotkey()
        {
            var spec = ChatSetting("conditionHotkey") as string ?? "Ctrl+Alt+B";
            if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) { condOn = false; return; }
            System.Windows.Forms.Keys mods = 0, key = 0;
            foreach (var part in spec.Split('+').Select(x => x.Trim()))
            {
                var p = part.ToLowerInvariant();
                if (p == "ctrl" || p == "strg" || p == "control") mods |= System.Windows.Forms.Keys.Control;
                else if (p == "shift" || p == "umschalt") mods |= System.Windows.Forms.Keys.Shift;
                else if (p == "alt") mods |= System.Windows.Forms.Keys.Alt;
                else key = (System.Windows.Forms.Keys)Enum.Parse(typeof(System.Windows.Forms.Keys), part, true);
            }
            if (key != 0) { condKey = key; condMods = mods; }
            Log("Condition hotkey: " + spec);
        }

        bool IsConditionKey(int vk) => condOn && vk == (int)condKey && System.Windows.Forms.Control.ModifierKeys == condMods;

        /// <summary>The condition at the cursor of the active editor, with the POU, method and line it is in.</summary>
        internal void ShowConditionFromEditor()
        {
            fmtGerman = UiGerman();
            try
            {
                var dv = ActiveDocView(out var doc);
                // the two editors of the object (declaration, implementation): the one with the focus, otherwise the implementation -
                // the same way tc_editor_context reads them (the VS view reports another pane in online mode)
                var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
                System.Windows.Forms.Control Syn(string pane) =>
                    SafeGet(cds, pane) is System.Windows.Forms.Control ctl
                        ? (ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0))
                        : null;
                var impl = Syn("ImplementationEditorView");
                var decl = Syn("InterfaceEditorView");
                var syn = decl != null && (decl.Focused || decl.ContainsFocus) ? decl : impl
                          ?? SafeCall(dv, "GetActiveSynEdControl", SafeGet(dv, "EditorView")) as System.Windows.Forms.Control;
                var text = SafeGet(syn, "Text") as string;
                if (string.IsNullOrEmpty(text)) { SetStatus(T("TcAgent: put the cursor into a condition (IF, ELSIF, WHILE, UNTIL) of an open POU first", "TcAgent: Setze den Cursor zuerst in eine Bedingung (IF, ELSIF, WHILE, UNTIL) einer offenen POU")); return; }
                // the caret: the position in the text of this editor (CaretPosition), otherwise its line (CaretLine)
                var textLines = text.Replace("\r\n", "\n").Split('\n');
                int nl = text.Contains("\r\n") ? 2 : 1, caret = -1;
                var cpos = SafeGet(syn, "CaretPosition");
                if (cpos is int cpi && cpi >= 0 && cpi <= text.Length) caret = cpi;
                else if (cpos is long cpl && cpl >= 0 && cpl <= text.Length) caret = (int)cpl;
                if (caret < 0 && SafeGet(syn, "CaretLine") is int cl)
                    for (int i = 0, k = 0; i < textLines.Length; k += textLines[i].Length + nl, i++) if (i == cl) { caret = k; break; }
                if (caret < 0) caret = 0;
                Log($"Condition: caret at {caret} of {text.Length} (line {text.Take(caret).Count(c => c == '\n') + 1}), {(syn == decl ? "declaration" : "implementation")}");
                // the selection of this editor: only a condition counts (a word clicked in online mode is not one)
                string sel = SafeGet(syn, "SelectedText") as string;
                if (!string.IsNullOrWhiteSpace(sel) && !(sel.Contains("\n") || Regex.IsMatch(sel, @"\b(AND|OR|XOR|NOT|AND_THEN|OR_ELSE)\b|[<>=]", RegexOptions.IgnoreCase))) sel = null;
                Log($"Condition: selection {(string.IsNullOrWhiteSpace(sel) ? "none" : sel.Length + " characters")}");
                var (cond, line) = ConditionAt(text, caret, sel);
                if (cond == null) { SetStatus(T("TcAgent: no condition at the cursor - put it into an IF, ELSIF, WHILE or UNTIL (or a boolean assignment)", "TcAgent: Keine Bedingung am Cursor - setze ihn in ein IF, ELSIF, WHILE oder UNTIL (oder eine boolesche Zuweisung)")); return; }
                // POU and method: the caption of the editor ("FB_Station.Cycle", "FB_Station [Online]"), otherwise the file
                string caption = null;
                try
                {
                    var mon = (Microsoft.VisualStudio.Shell.Interop.IVsMonitorSelection)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsShellMonitorSelection));
                    if (mon.GetCurrentElementValue(2, out var fo) == 0 && fo is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame fr && fr.GetProperty((int)Microsoft.VisualStudio.Shell.Interop.__VSFPROPID.VSFPROPID_Caption, out var cap) == 0) caption = cap?.ToString();
                }
                catch { }
                var name = Regex.Replace(caption ?? "", @"\s*\[.*?\]|\*", "").Trim();
                if (name.Length == 0 && !string.IsNullOrEmpty(doc)) name = Path.GetFileNameWithoutExtension(doc.Split('|').Last().Trim());
                string pou = name, method = null;
                if (name.Contains(".")) { pou = name.Substring(0, name.IndexOf('.')); method = name.Substring(name.IndexOf('.') + 1); }
                var exe = TcUnitControl.TcAgentExe();
                if (exe == null) { FormatMessage(T("TcAgent.exe was not found - set it up in the TcAgent Chat first.", "TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
                ConditionPopup.Open(this, syn, exe, TcUnitControl.SolutionPath(), cond, pou, method, line + 1, TargetNetId(), fmtGerman);
            }
            catch (Exception ex) { Log("Condition: " + Inner(ex)); SetStatus("TcAgent: " + Inner(ex)); }
        }

        /// <summary>The condition around the caret: the selection, an IF/ELSIF/WHILE/UNTIL up to THEN/DO/;, or a boolean assignment.</summary>
        internal static (string Cond, int Line) ConditionAt(string text, int caret, string selected)
        {
            int LineOf(int idx) => text.Take(Math.Max(0, Math.Min(idx, text.Length))).Count(c => c == '\n');
            if (!string.IsNullOrWhiteSpace(selected) && selected.Trim().Length > 0)
                return (selected, LineOf(Math.Max(0, text.IndexOf(selected, StringComparison.Ordinal))));
            caret = Math.Max(0, Math.Min(caret, text.Length));
            foreach (Match m in Regex.Matches(text, @"(?is)\b(IF|ELSIF|WHILE|UNTIL)\b.*?(\bTHEN\b|\bDO\b|;)"))
                if (m.Index <= caret && caret <= m.Index + m.Length + 1) return (m.Value, LineOf(m.Index));
            // a boolean assignment around the caret, also over several lines: from the ; before it to the ; after it
            int from = caret > 0 ? text.LastIndexOf(';', caret - 1) + 1 : 0;
            int to = text.IndexOf(';', caret);
            if (to < 0) return (null, 0);
            var stmt = text.Substring(from, to - from + 1);
            // what stands before the statement on its lines: THEN, ELSE, DO, a CASE label, comments
            var head = Regex.Match(stmt, @"(?ism)\A.*(\bTHEN\b|\bELSE\b|\bDO\b|\bREPEAT\b|\bOF\b|^[ \t]*[\w.#]+(?:[ \t]*(?:,|\.\.)[ \t]*[\w.#]+)*[ \t]*:(?!=)[ \t]*\r?$)");
            int cut = head.Success ? head.Length : 0;
            stmt = stmt.Substring(cut);
            // comments, pragmas ({warning ...}) and blank lines before the statement
            stmt = Regex.Replace(stmt, @"\A(\s*(//[^\n]*\n|\(\*.*?\*\)|\{[^}]*\}))+", "", RegexOptions.Singleline);
            if (stmt.Contains(":=") && Regex.IsMatch(stmt, @"\b(AND|OR|XOR|NOT|AND_THEN|OR_ELSE)\b", RegexOptions.IgnoreCase))
                return (stmt.Trim(), LineOf(Math.Max(from, text.IndexOf(stmt.Trim(), from, StringComparison.Ordinal))));
            return (null, 0);
        }

        /// <summary>The target of the solution (ITcSysManager.GetTargetNetId), or null = this PC.</summary>
        static string TargetNetId()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                foreach (EnvDTE.Project p in dte.Solution.Projects)
                {
                    object o = null;
                    try { o = p.Object; } catch { }
                    if (o == null) continue;
                    try { var id = o.GetType().InvokeMember("GetTargetNetId", System.Reflection.BindingFlags.InvokeMethod, null, o, null) as string; if (!string.IsNullOrEmpty(id)) return id; } catch { }
                }
            }
            catch { }
            return null;
        }
    }

    sealed class ConditionPopup : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern bool GetCaretPos(out POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

        static ConditionPopup open;
        static readonly Dictionary<string, string> lastInstance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string T(string en, string de) => german ? de : en;
        static bool german;

        readonly BridgePackage pkg;
        readonly System.Windows.Forms.Control editor;
        readonly string exe, sln, cond, pou, method, netId;
        readonly int line;
        readonly Canvas canvas = new Canvas();
        readonly TextBlock result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly Border resultBar = new Border { Padding = new Thickness(10, 5, 10, 5) };
        readonly ComboBox instances = new ComboBox { MinWidth = 180, Margin = new Thickness(6, 0, 0, 0), FontSize = 11 };
        readonly TextBlock foot = new TextBlock { FontSize = 11, Opacity = 0.7, Margin = new Thickness(10, 2, 10, 6), TextWrapping = TextWrapping.Wrap };
        Dictionary<string, object> structure, live;
        Process watch;
        bool pinned, paused, closing;

        static readonly SolidColorBrush Green = Frozen(0x3A, 0xA3, 0x35), Red = Frozen(0xC6, 0x28, 0x28), Grey = Frozen(0x9A, 0x9A, 0xA2), LampOn = Frozen(0x7C, 0xC3, 0x5B);
        // fills that work on the light and the dark theme
        static Brush GreenFill => Dark ? Frozen(0x1E, 0x3B, 0x1E) : Frozen(0xE5, 0xF4, 0xE4);
        static Brush RedFill => Dark ? Frozen(0x40, 0x1E, 0x1E) : Frozen(0xFB, 0xEB, 0xEB);
        static Brush LampOff => Dark ? Frozen(0x55, 0x55, 0x5A) : Frozen(0xE3, 0xE3, 0xE6);
        static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        /// <summary>A color of the VS theme as a brush (outside VS: the fallback).</summary>
        static Brush Theme(Microsoft.VisualStudio.Shell.ThemeResourceKey key, Brush fallback)
        {
            try { var c = VSColorTheme.GetThemedColor(key); if (c.A > 0) { var b = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)); b.Freeze(); return b; } } catch { }
            return fallback;
        }
        // outside VS (screenshots of the docs): light, or dark with TCAGENT_SHOT_DARK=1
        static readonly bool shotDark = Environment.GetEnvironmentVariable("TCAGENT_SHOT_DARK") == "1";
        static Brush Back => Theme(EnvironmentColors.ToolWindowBackgroundColorKey, shotDark ? Frozen(0x25, 0x25, 0x26) : Brushes.White);
        static Brush Fore => Theme(EnvironmentColors.ToolWindowTextColorKey, shotDark ? Frozen(0xDC, 0xDC, 0xDC) : Brushes.Black);
        static bool Dark => Back is SolidColorBrush sb && (sb.Color.R + sb.Color.G + sb.Color.B) / 3 < 100;

        internal static void Open(BridgePackage pkg, System.Windows.Forms.Control editor, string exe, string sln, string cond, string pou, string method, int line, string netId, bool isGerman)
        {
            german = isGerman;
            open?.CloseNow();
            // where: the caret of the editor, otherwise the mouse when it is over the editor, otherwise the top of the editor -
            // always on the monitor of the editor
            var hwnd = GetFocus();
            POINT p = default;
            System.Drawing.Rectangle er = default;
            try { if (editor != null) er = editor.RectangleToScreen(editor.ClientRectangle); } catch { }
            bool haveCaret = hwnd != IntPtr.Zero && GetCaretPos(out p) && ClientToScreen(hwnd, ref p);
            if (haveCaret && (er.IsEmpty || !er.Contains(p.X, p.Y))) haveCaret = false;
            if (!haveCaret)
            {
                var m = System.Windows.Forms.Cursor.Position;
                p = !er.IsEmpty && er.Contains(m) ? new POINT { X = m.X, Y = m.Y } : !er.IsEmpty ? new POINT { X = er.Left + 80, Y = er.Top + 60 } : new POINT { X = m.X, Y = m.Y };
            }
            var w = new ConditionPopup(pkg, editor, exe, sln, cond, pou, method, line, netId);
            open = w;
            w.PlaceAt(p.X + 40, p.Y + (editor?.Font?.Height ?? 16) + 6);
            w.Show();
            w.Load();
        }

        ConditionPopup(BridgePackage pkg, System.Windows.Forms.Control editor, string exe, string sln, string cond, string pou, string method, int line, string netId)
        {
            this.pkg = pkg; this.editor = editor; this.exe = exe; this.sln = sln; this.cond = WithoutComments(cond); this.pou = pou; this.method = method; this.line = line; this.netId = netId;
            Icon = BridgePackage.AppIcon;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
            try { Owner = Application.Current?.MainWindow; } catch { }
            // the colors of the VS theme, taken directly: a popup window does not see the theme resources of the IDE
            Background = Back; Foreground = Fore;

            // header: what it is, the instance, the buttons
            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(Regex.Match(cond.TrimStart(), @"^\w+").Value.ToUpperInvariant()) { FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            title.Inlines.Add(new System.Windows.Documents.Run($"  {pou}{(method != null ? "." + method : "")} · {T("line", "Zeile")} {line}") { Foreground = Brushes.Gray });
            Button Btn(string s, string tip, Action a) { var b = new Button { Content = s, ToolTip = tip, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) }; b.Click += (x, e) => a(); return b; }
            var pause = Btn("⏸", T("Freeze the values", "Werte einfrieren"), null);
            pause.Click += (s, e) => { paused = !paused; pause.Content = paused ? "▶" : "⏸"; if (!paused && live != null) Draw(); };
            var pin = Btn("📌", T("Keep open when you click elsewhere", "Offen lassen, wenn du woanders klickst"), null);
            pin.Click += (s, e) => { pinned = !pinned; pin.Opacity = pinned ? 1 : 0.55; };
            pin.Opacity = 0.55;
            var big = Btn("⤢", T("The large window with truth table and rewrites - comes next", "Das große Fenster mit Wahrheitstabelle und Umformen - kommt als Nächstes"), () => { });
            big.IsEnabled = false;
            var close = Btn("✕", T("Close (Esc)", "Schließen (Esc)"), CloseNow);
            var head = new DockPanel { Margin = new Thickness(10, 5, 6, 5) };
            foreach (var b in new UIElement[] { close, pin, pause, big }) { DockPanel.SetDock(b, Dock.Right); head.Children.Add(b); }
            DockPanel.SetDock(instances, Dock.Right); head.Children.Add(instances);
            head.Children.Add(title);
            var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
            headBorder.Background = Theme(EnvironmentColors.ToolWindowTabSelectedTabColorKey, Back); headBorder.BorderBrush = Theme(EnvironmentColors.ToolWindowBorderColorKey, Grey);
            headBorder.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            instances.SelectionChanged += (s, e) => { if (instances.SelectedItem is string i) { lastInstance[pou] = i; StartWatch(); } };

            result.Text = T("Reading the condition …", "Lese die Bedingung …");
            resultBar.Child = result;
            var panel = new StackPanel();
            panel.Children.Add(headBorder);
            panel.Children.Add(resultBar);
            panel.Children.Add(new Border { Child = canvas, Margin = new Thickness(8, 8, 8, 2) });
            panel.Children.Add(foot);
            var outer = new Border { Child = panel, BorderThickness = new Thickness(1) };
            outer.BorderBrush = Theme(EnvironmentColors.ToolWindowBorderColorKey, Grey);
            Content = outer;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseNow(); } };
            Deactivated += (s, e) => { if (!pinned) CloseNow(); };
            Closed += (s, e) => StopWatch();
        }

        void CloseNow()
        {
            if (closing) return;
            closing = true;
            if (open == this) open = null;
            StopWatch();
            try { Close(); } catch { }
            var ed = editor;
            Dispatcher.BeginInvoke(new Action(() => { try { if (ed != null && !ed.IsDisposed) ed.Focus(); } catch { } }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>The condition goes to TcAgent.exe on one command line: a // comment would swallow the rest, so comments go
        /// (outside of strings), pragmas too.</summary>
        static string WithoutComments(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '"') { int j = s.IndexOf(c, i + 1); if (j < 0) j = s.Length - 1; sb.Append(s, i, j - i + 1); i = j; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; sb.Append('\n'); continue; }
                if (c == '(' && i + 1 < s.Length && s[i + 1] == '*') { int j = s.IndexOf("*)", i + 2, StringComparison.Ordinal); i = j < 0 ? s.Length : j + 1; sb.Append(' '); continue; }
                if (c == '{') { int j = s.IndexOf('}', i + 1); i = j < 0 ? s.Length : j; sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Q(string s) => "\"" + (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\\\"") + "\"";

        string BaseArgs() => "condition --text " + Q(cond) + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Q(Path.GetDirectoryName(sln)))
                             + (string.IsNullOrEmpty(pou) ? "" : " --pou " + Q(pou)) + (string.IsNullOrEmpty(method) ? "" : " --method " + Q(method));

        static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            using (var p = Process.Start(psi))
            {
                try { p.StandardInput.Close(); } catch { }        // nothing to read: never wait on an inherited console
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new Exception("TcAgent.exe did not answer within 30 s"); }
                if (string.IsNullOrWhiteSpace(o.Result)) throw new Exception((e.Result ?? "").Trim());
                return o.Result;
            }
        }

        async void Load()
        {
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var listTask = Task.Run(() => { try { return js.Deserialize<ArrayList>(Run(exe, $"condition --instances --json --pou {Q(pou)}" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Q(Path.GetDirectoryName(sln))))); } catch { return new ArrayList(); } });
                var list = await listTask;
                var pick = lastInstance.TryGetValue(pou ?? "", out var li) && list.Contains(li) ? li : list.Count > 0 ? list[0] as string : null;
                structure = await Task.Run(() => js.Deserialize<Dictionary<string, object>>(Run(exe, BaseArgs() + (pick != null ? " --instance " + Q(pick) : "") + " --json")));
                Draw();
                foreach (var i in list.Cast<object>()) instances.Items.Add(i);
                if (list.Count == 0) { instances.Items.Add(T("(no instance found)", "(keine Instanz gefunden)")); instances.IsEnabled = false; instances.SelectedIndex = 0; foot.Text = T("Offline: no instance of this POU was found in the project - the structure only.", "Offline: keine Instanz dieser POU im Projekt gefunden - nur die Struktur."); }
                else instances.SelectedItem = pick;           // starts the online reading
            }
            catch (Exception ex) { result.Text = T("The condition could not be read: ", "Die Bedingung konnte nicht gelesen werden: ") + ex.Message; }
        }

        void StartWatch()
        {
            StopWatch();
            var inst = instances.SelectedItem as string;
            if (string.IsNullOrEmpty(inst)) return;
            try
            {
                var psi = new ProcessStartInfo(exe, BaseArgs() + " --instance " + Q(inst) + " --online --watch 500" + (string.IsNullOrEmpty(netId) ? "" : " --netId " + netId))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = new UTF8Encoding(false) };
                var p = Process.Start(psi);
                watch = p;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                Task.Run(() =>
                {
                    try
                    {
                        string l;
                        while ((l = p.StandardOutput.ReadLine()) != null)
                        {
                            var d = js.Deserialize<Dictionary<string, object>>(l);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (watch != p) return;
                                // the PLC does not run this project (or not at all): offline, the structure with the comments
                                var state = S(d, "state") ?? "";
                                bool none = L(d, "operands").Where(o => B(o, "constant") != true).All(o => S(o, "error") != null);
                                if (!state.Equals("Run", StringComparison.OrdinalIgnoreCase) || none && S(d, "onlineProject") == null)
                                {
                                    live = null; Draw();
                                    foot.Text = "Offline: " + (!state.Equals("Run", StringComparison.OrdinalIgnoreCase) ? T("the PLC is ", "die SPS ist ") + (state.StartsWith("not reachable") ? T("not reachable", "nicht erreichbar") : T("in state ", "im Zustand ") + state)
                                                                       : T("the PLC does not run this project", "auf der SPS läuft dieses Projekt nicht")) + (string.IsNullOrEmpty(netId) ? "" : " (" + netId + ")") + T(" - the structure only", " - nur der Aufbau");
                                    return;
                                }
                                live = d; if (!paused) Draw();
                            }));
                        }
                        var err = p.StandardError.ReadToEnd().Trim();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (watch != p || closing) return;
                            live = null; Draw();
                            foot.Text = T("Offline: ", "Offline: ") + (err.Length > 0 ? err.Split('\n')[0] : T("no connection to the PLC", "keine Verbindung zur SPS")) + (string.IsNullOrEmpty(netId) ? "" : " (" + netId + ")");
                        }));
                    }
                    catch { }
                });
            }
            catch (Exception ex) { foot.Text = "Offline: " + ex.Message; }
        }

        void StopWatch()
        {
            var p = watch; watch = null;
            if (p == null) return;
            try { p.StandardInput.Close(); } catch { }
            Task.Run(() => { try { if (!p.WaitForExit(2000)) p.Kill(); } catch { } try { p.Dispose(); } catch { } });
        }

        // ------------------------------------------------------------------ drawing
        sealed class G { public string Op; public int Atom = -1; public bool? Value; public readonly List<(G Kid, bool Not)> Kids = new List<(G, bool)>(); public double Y, X; public int H; }

        static IEnumerable<Dictionary<string, object>> L(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is ArrayList a ? a.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
        static string S(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        static bool? B(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is bool b ? b : (bool?)null;

        /// <summary>The tree with the NOTs moved onto the edges (a dot at the input), values from the online reading.</summary>
        static (G Node, bool Not) Build(Dictionary<string, object> n)
        {
            if (S(n, "op") == "NOT") { var k = Build(L(n, "kids").First()); return (k.Node, !k.Not); }
            var g = new G { Op = S(n, "op"), Value = B(n, "value") };
            if (g.Op == "atom" && int.TryParse(S(n, "atom"), out var a)) g.Atom = a;
            foreach (var k in L(n, "kids")) g.Kids.Add(Build(k));
            return (g, false);
        }

        void Draw()
        {
            canvas.Children.Clear();
            var src = live ?? structure;
            if (src == null || !(src.TryGetValue("tree", out var t) && t is Dictionary<string, object> tree)) return;
            var (root, rootNot) = Build(tree);
            var atomsS = L(structure, "atoms").ToList();
            var atomsL = live != null ? L(live, "atoms").ToList() : null;
            var opsL = live != null ? L(live, "operands").ToList() : null;
            var fore = Fore;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double Width(string s, double size, bool bold) => new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : typeface, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

            // rows: the leaves in order
            const double rowH = 28, top = 14;
            var leaves = new List<(G G, bool Not)>();
            void Leaves(G g) { if (g.Op == "atom" || g.Op == "const") leaves.Add((g, false)); else foreach (var k in g.Kids) Leaves(k.Kid); }
            Leaves(root);
            double labelEnd = 0;
            var labels = new List<(string Name, string Note, bool? Value, bool Unreadable, string Tip)>();
            for (int i = 0; i < leaves.Count; i++)
            {
                var g = leaves[i].G;
                g.Y = top + i * rowH;
                string name = g.Op == "const" ? (g.Value == true ? "TRUE" : "FALSE") : S(atomsS.ElementAtOrDefault(g.Atom), "text") ?? "?";
                // the note: the comment of the declaration, or the values of a comparison, or why it cannot be read
                var ops = L(atomsS.ElementAtOrDefault(g.Atom), "operands").ToList();
                var liveOps = opsL?.Where(o => S(o, "atom") == g.Atom.ToString()).ToList();
                string note = null, tip = string.Join("\n", ops.Select(o => $"{S(o, "text")}: {S(o, "path") ?? (german ? S(o, "reasonDe") : null) ?? S(o, "reason") ?? ""}  {(german ? S(o, "fromDe") : null) ?? S(o, "from")}"));
                bool unreadable = false;
                if (liveOps != null && liveOps.Count > 0)
                {
                    var errs = liveOps.Where(o => S(o, "error") != null).ToList();
                    if (errs.Count > 0) { unreadable = true; note = (german ? S(errs[0], "errorDe") : null) ?? S(errs[0], "error"); }
                    else if (liveOps.Count(o => B(o, "constant") != true) > 1 || Regex.IsMatch(name, @"[<>=]"))
                        note = string.Join(" · ", liveOps.Where(o => B(o, "constant") != true).Select(o => S(o, "value") ?? "?"));
                    else note = S(liveOps.FirstOrDefault(), "comment");
                }
                else if (ops.Count == 1) note = S(ops[0], "comment") ?? (S(ops[0], "path") == null && B(ops[0], "constant") != true ? (german ? S(ops[0], "reasonDe") : null) ?? S(ops[0], "reason") : null);
                if (liveOps == null && ops.Count == 1 && S(ops[0], "path") == null && B(ops[0], "constant") != true) unreadable = true;
                var value = g.Value ?? (atomsL != null ? B(atomsL.ElementAtOrDefault(g.Atom), "value") : null);
                if (g.Op == "const") value = g.Value;
                labels.Add((name, note, value, unreadable, tip));
                labelEnd = Math.Max(labelEnd, 30 + Width(name, 12, true) + (note != null ? Width(": " + note, 12, false) : 0));
            }
            labelEnd = Math.Min(labelEnd, 420);
            // columns: by the height of the subtree
            int Height(G g) => g.Kids.Count == 0 ? 0 : (g.H = 1 + g.Kids.Max(k => Height(k.Kid)));
            Height(root);
            const double colW = 74, gateW = 40;
            double gatesStart = labelEnd + 36;
            double Place(G g)
            {
                if (g.Kids.Count == 0) return g.Y;
                var ys = g.Kids.Select(k => Place(k.Kid)).ToList();
                g.X = gatesStart + (g.H - 1) * colW;
                g.Y = (ys.Min() + ys.Max()) / 2;
                return g.Y;
            }
            Place(root);
            // offline (no values yet): solid lines in the text color, like a printed diagram; online an unknown value is grey and dashed
            bool online = live != null;
            Brush Wire(bool? v) => v == true ? Green : v == false || !online ? fore : Grey;
            void Line(double x1, double y1, double x2, double y2, bool? v)
            {
                var l = new System.Windows.Shapes.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Wire(v), StrokeThickness = 1.6 };
                if (v == null && online) l.StrokeDashArray = new DoubleCollection { 4, 3 };
                canvas.Children.Add(l);
            }
            void Dot(double x, double y, bool? v)
            {
                var e = new Ellipse { Width = 8, Height = 8, Fill = v == false ? LampOn : Back, Stroke = Wire(v == null ? null : !v), StrokeThickness = 1.4 };
                Canvas.SetLeft(e, x - 4); Canvas.SetTop(e, y - 4); canvas.Children.Add(e);
            }
            void Text(string s, double x, double y, Brush b, double size = 12, bool bold = false)
            {
                var tb = new TextBlock { Text = s, Foreground = b, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); canvas.Children.Add(tb);
            }
            // the inputs
            for (int i = 0; i < leaves.Count; i++)
            {
                var (name, note, value, unreadable, tip) = labels[i];
                double y = leaves[i].G.Y;
                var lamp = new Ellipse { Width = 16, Height = 16, Fill = unreadable ? Brushes.Transparent : value == true ? LampOn : LampOff, Stroke = unreadable ? Grey : value == true ? Green : Grey, StrokeThickness = 1 };
                if (online && (unreadable || value == null)) lamp.StrokeDashArray = new DoubleCollection { 2, 2 };
                if (!online) { lamp.Fill = LampOff; lamp.Stroke = Grey; }
                Canvas.SetLeft(lamp, 4); Canvas.SetTop(lamp, y - 8); canvas.Children.Add(lamp);
                var tb = new TextBlock { FontSize = 12, ToolTip = tip.Length > 0 ? tip : null, MaxWidth = 400, TextTrimming = TextTrimming.CharacterEllipsis };
                tb.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold, Foreground = unreadable ? Grey : fore });
                if (note != null) tb.Inlines.Add(new System.Windows.Documents.Run(": " + note) { Foreground = Grey });
                Canvas.SetLeft(tb, 28); Canvas.SetTop(tb, y - 9); canvas.Children.Add(tb);
            }
            // the gates and lines (right-angled: horizontal into the gate at the height of the kid)
            double OutX(G g) => g.Kids.Count == 0 ? Math.Min(28 + Width(labels[leaves.FindIndex(l => l.G == g)].Name, 12, true) + (labels[leaves.FindIndex(l => l.G == g)].Note != null ? Width(": " + labels[leaves.FindIndex(l => l.G == g)].Note, 12, false) : 0) + 6, labelEnd + 6) : g.X + gateW;
            void Gate(G g)
            {
                if (g.Kids.Count == 0) return;
                foreach (var k in g.Kids) Gate(k.Kid);
                double gTop = g.Kids.Min(k => k.Kid.Y) - 13, gBot = g.Kids.Max(k => k.Kid.Y) + 13;
                if (gBot - gTop < 34) { var c = (gTop + gBot) / 2; gTop = c - 17; gBot = c + 17; }
                for (int i = 0; i < g.Kids.Count; i++)
                {
                    var (kid, not) = g.Kids[i];
                    double y = kid.Y, x0 = OutX(kid), xIn = g.X - (not ? 8 : 0);
                    // a kid whose output is not at its pin height bends once before the gate
                    Line(x0, y, xIn, y, kid.Value);
                    if (not) Dot(g.X - 4, y, kid.Value);
                }
                var box = new Rectangle { Width = gateW, Height = gBot - gTop, Stroke = g.Value == true ? Green : g.Value == false || !online ? fore : Grey, StrokeThickness = 1.6, Fill = g.Value == true ? GreenFill : Back };
                if (g.Value == null && online) box.StrokeDashArray = new DoubleCollection { 4, 3 };
                Canvas.SetLeft(box, g.X); Canvas.SetTop(box, gTop); canvas.Children.Add(box);
                var sym = g.Op == "AND" ? "&" : g.Op == "OR" ? "≥1" : g.Op == "XOR" ? "=1" : g.Op;
                Text(sym, g.X + (sym.Length == 1 ? 14 : 9), g.Y - 10, g.Value == true ? Green : fore, 15, true);
            }
            Gate(root);
            // the result
            bool? res = root.Value is bool rv ? (rootNot ? !rv : rv) : (bool?)null;
            if (live != null && live.TryGetValue("result", out var r)) res = r as bool?;
            double rx = (root.Kids.Count == 0 ? labelEnd : root.X + gateW) + 34;
            Line(OutX(root), root.Y, rx - (rootNot ? 8 : 0), root.Y, root.Value);
            if (rootNot) Dot(rx - 4, root.Y, root.Value);
            var pill = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.6), Padding = new Thickness(14, 2, 14, 2), BorderBrush = res == true ? Green : res == false ? Red : online ? Grey : fore, Background = res == true ? GreenFill : res == false ? RedFill : Brushes.Transparent,
                                    Child = new TextBlock { Text = res == true ? "TRUE" : res == false ? "FALSE" : "?", FontWeight = FontWeights.Bold, Foreground = res == true ? Green : res == false ? Red : online ? Grey : fore } };
            Canvas.SetLeft(pill, rx); Canvas.SetTop(pill, root.Y - 12); canvas.Children.Add(pill);
            canvas.Width = rx + 100;
            result.MaxWidth = Math.Max(420, canvas.Width);       // the line on top wraps instead of widening the popup
            foot.MaxWidth = result.MaxWidth;
            canvas.Height = top + leaves.Count * rowH;
            // the line on top: the result and what is missing
            if (live == null)
            {
                resultBar.Background = Brushes.Transparent;
                result.Text = T("Structure of the condition - the values come when the PLC is online", "Aufbau der Bedingung - die Werte kommen, wenn die SPS online ist");
            }
            else
            {
                resultBar.Background = res == true ? GreenFill : res == false ? RedFill : Brushes.Transparent;
                result.Foreground = fore;
                var missing = L(live, "missing").ToList();
                var ways = live.TryGetValue("missing", out var mo) && mo is ArrayList ma ? ma.OfType<ArrayList>().Select(w => w.OfType<Dictionary<string, object>>().Select(x => (Atom: int.Parse(S(x, "atom")), Want: B(x, "want") == true)).ToList()).ToList() : new List<List<(int Atom, bool Want)>>();
                string Atom(int i) => S(atomsS.ElementAtOrDefault(i), "text");
                string Way(List<(int Atom, bool Want)> w) => string.Join(T(" and ", " und "), w.Take(4).Select(x => Atom(x.Atom) + (x.Want ? "" : " = FALSE"))) + (w.Count > 4 ? T($" and {w.Count - 4} more", $" und {w.Count - 4} weitere") : "");
                result.Text = res == true ? "✓ TRUE" : res == false ? "✗ FALSE – " + T("missing: ", "es fehlt: ") + string.Join(T(" or ", " oder "), ways.Take(2).Select(Way)) + (ways.Count > 2 ? T(" or …", " oder …") : "")
                            : "? " + T("unknown - a sub-condition cannot be read", "unbekannt - eine Teilbedingung ist nicht lesbar");
                foot.Text = T("online", "online") + (string.IsNullOrEmpty(netId) ? "" : " · " + netId) + " · " + S(live, "time") + (paused ? T(" · frozen", " · eingefroren") : "")
                          + (S(live, "onlineProject") is string op ? " · " + op : "") + T(" · read only", " · nur lesen");
            }
        }

        void PlaceAt(int px, int py)
        {
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(px, py)).WorkingArea;
            double sx = 1, sy = 1;
            try { var src = PresentationSource.FromVisual(Application.Current.MainWindow); if (src?.CompositionTarget != null) { var m = src.CompositionTarget.TransformFromDevice; sx = m.M11; sy = m.M22; } } catch { }
            if (px + 760 / sx > scr.Right) px = (int)(scr.Right - 780 / sx);
            if (py + 360 / sy > scr.Bottom) py = (int)(scr.Bottom - 380 / sy);
            Left = Math.Max(scr.Left, px) * sx; Top = Math.Max(scr.Top, py) * sy;
        }
    }
}
