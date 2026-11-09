// TcAgentBridge - Inline-Scope: the history of a variable in a small popup at the code (Ctrl+Alt+T on a variable, "scopeHotkey" in
// chat-backends.json; or a click on a sub-condition in the Inline-Logic-View). Built after the approved mockup.
//
//   TcAgent.exe watch --vars ... --interval 20   reads over ADS (only reads) and streams the samples
//   BOOL as a step, numbers as a line, enums as steps with their names; several variables one below the other.
//   Time axis: 5 s / 30 s / 1 min / 5 min / 10 min, the mouse wheel zooms (1 s … 10 min); paused, dragging moves through the
//   recorded 10 minutes. A cursor shows time and values; current, min and max below; CSV to the clipboard.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;
using WLine = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        System.Windows.Forms.Keys scopeKey = System.Windows.Forms.Keys.T;
        System.Windows.Forms.Keys scopeMods = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt;
        bool scopeOn = true;

        void ReadScopeHotkey()
        {
            var spec = ChatSetting("scopeHotkey") as string ?? "Ctrl+Alt+T";
            if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) { scopeOn = false; return; }
            System.Windows.Forms.Keys mods = 0, key = 0;
            foreach (var part in spec.Split('+').Select(x => x.Trim()))
            {
                var p = part.ToLowerInvariant();
                if (p == "ctrl" || p == "strg" || p == "control") mods |= System.Windows.Forms.Keys.Control;
                else if (p == "shift" || p == "umschalt") mods |= System.Windows.Forms.Keys.Shift;
                else if (p == "alt") mods |= System.Windows.Forms.Keys.Alt;
                else key = (System.Windows.Forms.Keys)Enum.Parse(typeof(System.Windows.Forms.Keys), part, true);
            }
            if (key != 0) { scopeKey = key; scopeMods = mods; }
            Log("Scope hotkey: " + spec);
        }

        bool IsScopeKey(int vk) => scopeOn && vk == (int)scopeKey && System.Windows.Forms.Control.ModifierKeys == scopeMods;

        // Alt+click on a variable in the code editor opens the Inline-Scope ("scopeClick" in chat-backends.json: Alt, Ctrl+Alt or off)
        delegate IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, MouseProc fn, IntPtr hMod, uint threadId);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct MOUSEHOOKSTRUCT { public int X, Y; public IntPtr Hwnd; public uint HitTest; public IntPtr Extra; }
        MouseProc mouseHook;           // the field keeps the delegate alive
        IntPtr mouseHandle;
        System.Windows.Forms.Keys scopeClickMods = System.Windows.Forms.Keys.Alt;

        void InstallScopeClick()
        {
            var spec = ChatSetting("scopeClick") as string ?? "Alt";
            if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
            System.Windows.Forms.Keys mods = 0;
            foreach (var part in spec.Split('+').Select(x => x.Trim().ToLowerInvariant()))
                mods |= part is "ctrl" or "strg" or "control" ? System.Windows.Forms.Keys.Control : part is "shift" or "umschalt" ? System.Windows.Forms.Keys.Shift : part == "alt" ? System.Windows.Forms.Keys.Alt : 0;
            if (mods == 0) return;
            scopeClickMods = mods;
            mouseHook = OnMouseHook;
            mouseHandle = SetWindowsHookEx(7 /* WH_MOUSE */, mouseHook, IntPtr.Zero, GetCurrentThreadId());
            Log("Scope click: " + spec + "+click" + (mouseHandle == IntPtr.Zero ? " - hook FAILED" : ""));
        }

        IntPtr OnMouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // WM_LBUTTONUP in a TwinCAT code editor with the modifier held: the click has set the caret - then the scope
                if (code == 0 && wParam.ToInt64() == 0x0202 && System.Windows.Forms.Control.ModifierKeys == scopeClickMods)
                {
                    var ms = (MOUSEHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MOUSEHOOKSTRUCT));
                    var ctl = System.Windows.Forms.Control.FromChildHandle(ms.Hwnd);
                    if (ctl != null && ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0)
                        ui.BeginInvoke(new Action(() =>
                        {
                            Log("Scope click");
                            try { ShowScopeFromEditor(); } catch (Exception ex) { Log("Scope: " + Inner(ex)); }
                        }));
                }
            }
            catch { }
            return CallNextHookEx(mouseHandle, code, wParam, lParam);
        }

        /// <summary>The variable at the cursor (a.b[i]^.c) in the Inline-Scope.</summary>
        internal void ShowScopeFromEditor()
        {
            fmtGerman = UiGerman();
            try
            {
                var (syn, text, caret, pou, method) = EditorAtCaret();
                if (string.IsNullOrEmpty(text)) { SetStatus(T("TcAgent: put the cursor on a variable of an open POU first", "TcAgent: Setze den Cursor zuerst auf eine Variable einer offenen POU")); return; }
                var sel = SafeGet(syn, "SelectedText") as string;
                var name = !string.IsNullOrWhiteSpace(sel) && Regex.IsMatch(sel.Trim(), @"^[\w.^\[\]]+$") ? sel.Trim() : VariableAt(text, caret);
                if (string.IsNullOrEmpty(name)) { SetStatus(T("TcAgent: no variable at the cursor", "TcAgent: keine Variable am Cursor")); return; }
                var exe = TcUnitControl.TcAgentExe();
                if (exe == null) { FormatMessage(T("TcAgent.exe was not found - set it up in the TcAgent Chat first.", "TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
                ScopePopup.Open(syn, exe, TcUnitControl.SolutionPath(), new List<string> { name }, pou, method, null, TargetNetId(), fmtGerman);
            }
            catch (Exception ex) { Log("Scope: " + Inner(ex)); SetStatus("TcAgent: " + Inner(ex)); }
        }

        /// <summary>The name chain under the caret: letters, digits, _, ., ^ and [index].</summary>
        internal static string VariableAt(string text, int caret)
        {
            caret = Math.Max(0, Math.Min(caret, text.Length));
            bool Part(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '^';
            int s = caret, e = caret, depth = 0;
            while (s > 0 && (Part(text[s - 1]) || text[s - 1] == ']' || text[s - 1] == '[' && depth > 0 || depth > 0))
            {
                var c = text[s - 1];
                if (c == ']') depth++; else if (c == '[') depth--;
                if (depth < 0) break;
                s--;
            }
            depth = 0;
            while (e < text.Length && (Part(text[e]) || text[e] == '[' || depth > 0))
            {
                if (text[e] == '[') depth++; else if (text[e] == ']') { depth--; if (depth < 0) break; }
                e++;
            }
            var name = text.Substring(s, e - s).Trim('.', '^');
            return Regex.IsMatch(name, @"^[A-Za-z_]") && !StKeyword(name) ? name : null;
        }

        static bool StKeyword(string w) => Regex.IsMatch(w, @"^(IF|THEN|ELSE|ELSIF|END_IF|CASE|OF|END_CASE|FOR|TO|BY|DO|END_FOR|WHILE|END_WHILE|REPEAT|UNTIL|END_REPEAT|AND|OR|XOR|NOT|MOD|TRUE|FALSE|RETURN|EXIT|AND_THEN|OR_ELSE)$", RegexOptions.IgnoreCase);

        /// <summary>The editor with the caret (implementation, or the declaration when it has the focus): its control, text, caret
        /// position, and the POU and method of the window caption.</summary>
        (System.Windows.Forms.Control Syn, string Text, int Caret, string Pou, string Method) EditorAtCaret()
        {
            var dv = ActiveDocView(out var doc);
            var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
            System.Windows.Forms.Control Syn(string pane) =>
                SafeGet(cds, pane) is System.Windows.Forms.Control ctl
                    ? (ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0))
                    : null;
            var impl = Syn("ImplementationEditorView");
            var decl = Syn("InterfaceEditorView");
            var syn = decl != null && (decl.Focused || decl.ContainsFocus) ? decl : impl ?? SafeCall(dv, "GetActiveSynEdControl", SafeGet(dv, "EditorView")) as System.Windows.Forms.Control;
            var text = SafeGet(syn, "Text") as string ?? "";
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int nl = text.Contains("\r\n") ? 2 : 1, caret = -1;
            var cpos = SafeGet(syn, "CaretPosition");
            if (cpos is int cpi && cpi >= 0 && cpi <= text.Length) caret = cpi;
            else if (cpos is long cpl && cpl >= 0 && cpl <= text.Length) caret = (int)cpl;
            if (caret < 0 && SafeGet(syn, "CaretLine") is int cl)
                for (int i = 0, k = 0; i < lines.Length; k += lines[i].Length + nl, i++) if (i == cl) { caret = k; break; }
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
            return (syn, text, Math.Max(0, caret), pou, method);
        }
    }

    sealed class ScopePopup : Window
    {
        static ScopePopup open;
        static bool german;
        static string T(string en, string de) => german ? de : en;

        readonly System.Windows.Forms.Control editor;
        readonly string exe, sln, pou, method, netId;
        string instance;
        readonly List<string> names;
        readonly Canvas chart = new Canvas { ClipToBounds = true };
        readonly TextBlock foot = new TextBlock { FontSize = 11, Margin = new Thickness(10, 4, 10, 6), TextWrapping = TextWrapping.Wrap };
        readonly StackPanel ranges = new StackPanel { Orientation = Orientation.Horizontal };
        readonly ComboBox instances = new ComboBox { MinWidth = 160, Margin = new Thickness(6, 0, 0, 0), FontSize = 11 };
        readonly System.Windows.Threading.DispatcherTimer redraw = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        // samples: time in ms since the start of the reading, one value per variable (double, string for an enum, null)
        readonly List<(long T, object[] V)> samples = new List<(long, object[])>();
        List<Dictionary<string, object>> meta = new List<Dictionary<string, object>>();
        Process watch;
        double spanMs = 30000, offsetMs;          // the visible window: its width and how far it is from now
        bool paused, pinned, closing;
        double? cursorX;
        int interval = 20;
        const double Left0 = 110, PlotW = 560, Gap = 10;
        // one variable: tall, so small changes show; several: one below the other
        double BandBool => names.Count == 1 ? 90 : 26;
        double BandNum => names.Count == 1 ? 240 : 110;

        static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static readonly Brush[] Colors = { Frozen(0x3B, 0x6F, 0xD8), Frozen(0xC6, 0x28, 0x28), Frozen(0x3A, 0xA3, 0x35), Frozen(0xC2, 0x6A, 0x00), Frozen(0x8E, 0x44, 0xAD), Frozen(0x00, 0x96, 0x88) };
        static readonly Brush Grey = Frozen(0x9A, 0x9A, 0xA2);
        static Brush Theme(Microsoft.VisualStudio.Shell.ThemeResourceKey key, Brush fallback)
        {
            try { var c = VSColorTheme.GetThemedColor(key); if (c.A > 0) { var b = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)); b.Freeze(); return b; } } catch { }
            return fallback;
        }
        static Brush Back => Theme(EnvironmentColors.ToolWindowBackgroundColorKey, Brushes.White);
        static Brush Fore => Theme(EnvironmentColors.ToolWindowTextColorKey, Brushes.Black);
        static Brush GridBrush => Theme(EnvironmentColors.ToolWindowBorderColorKey, Frozen(0xDD, 0xDD, 0xE0));

        internal static void Open(System.Windows.Forms.Control editor, string exe, string sln, List<string> names, string pou, string method, string instance, string netId, bool isGerman)
        {
            german = isGerman;
            // another variable while a scope of the same POU is open: it shows that one now (more come with + Variable)
            if (open != null && open.IsVisible && open.pou == pou && (instance == null || instance == open.instance))
            {
                open.names.Clear(); open.names.AddRange(names.Take(1));
                open.Restart();
                open.Activate();
                return;
            }
            open?.CloseNow();
            var w = new ScopePopup(editor, exe, sln, names, pou, method, instance, netId);
            open = w;
            var m = System.Windows.Forms.Cursor.Position;
            System.Drawing.Rectangle er = default;
            try { if (editor != null) er = editor.RectangleToScreen(editor.ClientRectangle); } catch { }
            var at = !er.IsEmpty && !er.Contains(m) ? new System.Drawing.Point(er.Left + 120, er.Top + 80) : new System.Drawing.Point(m.X + 20, m.Y + 20);
            var scr = System.Windows.Forms.Screen.FromPoint(at).WorkingArea;
            double sx = 1, sy = 1;
            try { var src = PresentationSource.FromVisual(Application.Current.MainWindow); if (src?.CompositionTarget != null) { var tm = src.CompositionTarget.TransformFromDevice; sx = tm.M11; sy = tm.M22; } } catch { }
            int px = Math.Min(at.X, (int)(scr.Right - 760 / sx)), py = Math.Min(at.Y, (int)(scr.Bottom - 420 / sy));
            w.Left = Math.Max(scr.Left, px) * sx; w.Top = Math.Max(scr.Top, py) * sy;
            w.Show();
            w.Load();
        }

        ScopePopup(System.Windows.Forms.Control editor, string exe, string sln, List<string> names, string pou, string method, string instance, string netId)
        {
            this.editor = editor; this.exe = exe; this.sln = sln; this.names = names.Take(1).ToList(); this.pou = pou; this.method = method; this.instance = instance; this.netId = netId;
            Icon = BridgePackage.AppIcon;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
            try { Owner = Application.Current?.MainWindow; } catch { }
            Background = Back; Foreground = Fore;

            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330 };
            Button Btn(string s, string tip) => new Button { Content = s, ToolTip = tip, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Fore };
            var pause = Btn("⏸", T("Freeze - then the mouse wheel zooms and dragging moves through the last 10 minutes", "Einfrieren - dann zoomt das Mausrad und Ziehen verschiebt durch die letzten 10 Minuten"));
            pause.Click += (s, e) => { paused = !paused; pause.Content = paused ? "▶" : "⏸"; if (!paused) offsetMs = 0; Draw(); };
            var pin = Btn("📌", T("Keep open when you click elsewhere", "Offen lassen, wenn du woanders klickst")); pin.Opacity = 0.55;
            pin.Click += (s, e) => { pinned = !pinned; pin.Opacity = pinned ? 1 : 0.55; };
            var close = Btn("✕", T("Close (Esc)", "Schließen (Esc)")); close.Click += (s, e) => CloseNow();
            var head = new DockPanel { Margin = new Thickness(10, 5, 6, 5) };
            foreach (var b in new UIElement[] { close, pin, pause }) { DockPanel.SetDock(b, Dock.Right); head.Children.Add(b); }
            DockPanel.SetDock(instances, Dock.Right); head.Children.Add(instances);
            head.Children.Add(title);
            var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), Background = Theme(EnvironmentColors.ToolWindowTabSelectedTabColorKey, Back), BorderBrush = GridBrush };
            headBorder.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            title.Loaded += (s, e) => UpdateTitle(title);
            titleBlock = title;

            // the time axis: fixed ranges, the mouse wheel in between
            foreach (var (ms, label) in new[] { (5000.0, "5 s"), (30000.0, "30 s"), (60000.0, "1 min"), (300000.0, "5 min"), (600000.0, "10 min") })
            {
                var chip = new Button { Content = label, Tag = ms, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), Foreground = Fore, Background = Brushes.Transparent, BorderBrush = Grey };
                chip.Click += (s, e) => { spanMs = (double)((Button)s).Tag; offsetMs = Math.Min(offsetMs, MaxOffset()); Draw(); };
                ranges.Children.Add(chip);
            }
            var add = new Button { Content = T("+ Variable", "+ Variable"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 4, 0), Foreground = Fore, Background = Brushes.Transparent, BorderBrush = Grey, ToolTip = T("Add a variable (name or path)", "Eine Variable dazunehmen (Name oder Pfad)") };
            add.Click += (s, e) => AddVariable();
            var csv = new Button { Content = T("Copy CSV", "CSV kopieren"), Padding = new Thickness(8, 1, 8, 1), Foreground = Fore, Background = Brushes.Transparent, BorderBrush = Grey, ToolTip = T("The samples of the visible time as CSV to the clipboard", "Die Werte der sichtbaren Zeit als CSV in die Zwischenablage") };
            csv.Click += (s, e) => CopyCsv();
            var tools = new DockPanel { Margin = new Thickness(10, 6, 10, 2) };
            DockPanel.SetDock(csv, Dock.Right); tools.Children.Add(csv);
            DockPanel.SetDock(add, Dock.Right); tools.Children.Add(add);
            tools.Children.Add(ranges);

            chart.Width = Left0 + PlotW + 60;     // room for the values of the axis on the right
            chart.Background = Back;
            chart.MouseMove += (s, e) => { var x = e.GetPosition(chart).X; cursorX = x >= Left0 && x <= Left0 + PlotW ? x : (double?)null; if (dragFrom != null && paused) { offsetMs = Math.Max(0, Math.Min(MaxOffset(), dragOffset + (x - dragFrom.Value) / PlotW * spanMs)); } Draw(); };
            chart.MouseLeave += (s, e) => { cursorX = null; dragFrom = null; Draw(); };
            chart.MouseLeftButtonDown += (s, e) => { if (paused) { dragFrom = e.GetPosition(chart).X; dragOffset = offsetMs; chart.CaptureMouse(); } };
            chart.MouseLeftButtonUp += (s, e) => { dragFrom = null; chart.ReleaseMouseCapture(); };
            chart.MouseWheel += (s, e) => { spanMs = Math.Max(1000, Math.Min(600000, spanMs * (e.Delta > 0 ? 0.8 : 1.25))); offsetMs = Math.Min(offsetMs, MaxOffset()); Draw(); e.Handled = true; };

            var panel = new StackPanel();
            panel.Children.Add(headBorder);
            panel.Children.Add(tools);
            panel.Children.Add(new Border { Child = chart, Margin = new Thickness(6, 4, 6, 0) });
            panel.Children.Add(foot);
            Content = new Border { Child = panel, BorderThickness = new Thickness(1), BorderBrush = GridBrush };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseNow(); } };
            Deactivated += (s, e) => { if (!pinned) CloseNow(); };
            Closed += (s, e) => { redraw.Stop(); StopWatch(); };
            redraw.Tick += (s, e) => { if (!paused) Draw(); };
            instances.SelectionChanged += (s, e) => { if (instances.SelectedItem is string i && i != instance) { instance = i; Restart(); } };
        }

        TextBlock titleBlock;
        double? dragFrom; double dragOffset;

        void UpdateTitle(TextBlock title)
        {
            title.Inlines.Clear();
            title.Inlines.Add(new System.Windows.Documents.Run(string.Join(", ", names)) { FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            var m = meta.FirstOrDefault();
            if (m != null && names.Count == 1)
                title.Inlines.Add(new System.Windows.Documents.Run($"  {S(m, "type")} · {S(m, "path")}" + (string.IsNullOrEmpty(S(m, "from")) ? "" : " · " + ((german ? S(m, "fromDe") : null) ?? S(m, "from")))) { Foreground = Grey });
        }

        static string S(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        double MaxOffset() => samples.Count == 0 ? 0 : Math.Max(0, samples[samples.Count - 1].T - samples[0].T - spanMs);

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

        async void Load()
        {
            // the instances of the POU (the same as in the Inline-Logic-View)
            try
            {
                var list = await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo(exe, $"condition --instances --json --pou {Q(pou)}" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Q(Path.GetDirectoryName(sln))))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
                    using (var p = Process.Start(psi)) { p.StandardInput.Close(); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(20000); return new JavaScriptSerializer().Deserialize<ArrayList>(o) ?? new ArrayList(); }
                });
                foreach (var i in list) instances.Items.Add(i);
                if (instance == null && list.Count > 0) instance = list[0] as string;
                if (instance != null) { if (!instances.Items.Contains(instance)) instances.Items.Add(instance); instances.SelectedItem = instance; }
                if (list.Count == 0 && instance == null) { instances.Items.Add(T("(no instance)", "(keine Instanz)")); instances.IsEnabled = false; }
            }
            catch { }
            Restart();
            redraw.Start();
        }

        void Restart()
        {
            StopWatch();
            samples.Clear(); meta.Clear(); offsetMs = 0;
            if (titleBlock != null) UpdateTitle(titleBlock);
            var args = $"watch --vars {Q(string.Join(";", names))} --interval {interval}" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Q(Path.GetDirectoryName(sln)))
                       + (string.IsNullOrEmpty(pou) ? "" : " --pou " + Q(pou)) + (string.IsNullOrEmpty(method) ? "" : " --method " + Q(method))
                       + (string.IsNullOrEmpty(instance) ? "" : " --instance " + Q(instance)) + (string.IsNullOrEmpty(netId) ? "" : " --netId " + netId);
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = new UTF8Encoding(false) };
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
                                if (d.TryGetValue("vars", out var v) && v is ArrayList vl)
                                {
                                    meta = vl.OfType<Dictionary<string, object>>().ToList();
                                    if (d.TryGetValue("interval", out var iv)) interval = Convert.ToInt32(iv);
                                    if (titleBlock != null) UpdateTitle(titleBlock);
                                    var state = S(d, "state") ?? "";
                                    if (!state.Equals("Run", StringComparison.OrdinalIgnoreCase)) foot.Text = T("Offline: the PLC is ", "Offline: die SPS ist ") + state;
                                }
                                else if (d.TryGetValue("s", out var sv) && sv is ArrayList rows)
                                    foreach (var r in rows.OfType<ArrayList>())
                                    {
                                        samples.Add((Convert.ToInt64(r[0]), r.Cast<object>().Skip(1).ToArray()));
                                        // 10 minutes are kept
                                        if (samples.Count > 2 && samples[samples.Count - 1].T - samples[0].T > 600000) samples.RemoveRange(0, Math.Max(1, samples.Count / 100));
                                    }
                            }));
                        }
                        var err = p.StandardError.ReadToEnd().Trim();
                        Dispatcher.BeginInvoke(new Action(() => { if (watch == p && !closing) foot.Text = "Offline: " + (err.Length > 0 ? err.Split('\n')[0] : T("no connection to the PLC", "keine Verbindung zur SPS")); }));
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

        void AddVariable()
        {
            var box = new TextBox { MinWidth = 280, Margin = new Thickness(10), Foreground = Fore, Background = Back };
            var w = new Window { Title = T("Add a variable", "Variable dazunehmen"), Content = box, SizeToContent = SizeToContent.WidthAndHeight, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Background = Back };
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { w.DialogResult = true; } else if (e.Key == Key.Escape) w.DialogResult = false; };
            w.Loaded += (s, e) => box.Focus();
            pinned = true;                                       // the dialog takes the focus: do not close the scope
            if (w.ShowDialog() == true && box.Text.Trim().Length > 0 && !names.Contains(box.Text.Trim(), StringComparer.OrdinalIgnoreCase)) { names.Add(box.Text.Trim()); Restart(); }
        }

        void CopyCsv()
        {
            var (t0, t1) = Window();
            var sb = new StringBuilder("ms;" + string.Join(";", names) + "\r\n");
            foreach (var s in samples.Where(x => x.T >= t0 && x.T <= t1))
                sb.Append(s.T).Append(';').Append(string.Join(";", s.V.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Append("\r\n");
            try { System.Windows.Forms.Clipboard.SetText(sb.ToString()); foot.Text = T("Copied to the clipboard: ", "In die Zwischenablage kopiert: ") + samples.Count(x => x.T >= t0 && x.T <= t1) + T(" samples", " Werte"); } catch { }
        }

        (double T0, double T1) Window()
        {
            if (samples.Count == 0) return (0, spanMs);
            double end = samples[samples.Count - 1].T - offsetMs;
            return (end - spanMs, end);
        }

        static double? Num(object v) => v == null ? (double?)null : v is string ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

        void Draw()
        {
            chart.Children.Clear();
            var fore = Fore;
            var (t0, t1) = Window();
            double X(double t) => Left0 + (t - t0) / (t1 - t0) * PlotW;
            var visible = samples.Where(s => s.T >= t0 - interval && s.T <= t1 + interval).ToList();
            double y = 6;
            var footParts = new List<string>();
            void Txt(string s, double x, double yy, Brush b, double size = 11, bool bold = false, double maxW = 0)
            {
                var tb = new TextBlock { Text = s, Foreground = b, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                if (maxW > 0) { tb.MaxWidth = maxW; tb.TextTrimming = TextTrimming.CharacterEllipsis; }
                Canvas.SetLeft(tb, x); Canvas.SetTop(tb, yy); chart.Children.Add(tb);
            }
            for (int i = 0; i < names.Count; i++)
            {
                var m = meta.ElementAtOrDefault(i);
                var color = Colors[i % Colors.Length];
                var type = S(m, "type") ?? "";
                bool isBool = type.Equals("BOOL", StringComparison.OrdinalIgnoreCase) || type.Equals("BIT", StringComparison.OrdinalIgnoreCase);
                bool isText = visible.Any(s => s.V.ElementAtOrDefault(i) is string);
                double h = S(m, "error") != null ? 26 : isBool || isText ? BandBool : BandNum;
                Txt(names[i], 4, y + (h / 2) - 8, color, 11, true, Left0 - 10);
                var frame = new Rectangle { Width = PlotW, Height = h, Stroke = GridBrush, StrokeThickness = 1 };
                Canvas.SetLeft(frame, Left0); Canvas.SetTop(frame, y); chart.Children.Add(frame);
                if (((german ? S(m, "errorDe") : null) ?? S(m, "error")) is string err) { Txt(err, Left0 + 8, y + h / 2 - 8, Grey, 11, false, PlotW - 16); y += h + Gap; footParts.Add(names[i] + ": —"); continue; }
                var vals = visible.Select(s => (s.T, V: s.V.ElementAtOrDefault(i))).Where(s => s.V != null).ToList();
                if (isBool || isText)
                {
                    // steps: TRUE (or a changed value) high
                    var poly = new Polyline { Stroke = color, StrokeThickness = 1.6 };
                    string last = null;
                    foreach (var (t, v) in vals)
                    {
                        double yy = isBool ? (Num(v) > 0.5 ? y + 4 : y + h - 4) : y + h / 2;
                        if (poly.Points.Count > 0) poly.Points.Add(new Point(Math.Max(Left0, X(t)), poly.Points[poly.Points.Count - 1].Y));
                        poly.Points.Add(new Point(Math.Max(Left0, Math.Min(Left0 + PlotW, X(t))), yy));
                        if (isText && v.ToString() != last) { var x = X(t); if (x >= Left0 && x < Left0 + PlotW - 20) { chart.Children.Add(new WLine { X1 = x, X2 = x, Y1 = y + 2, Y2 = y + h - 2, Stroke = color, StrokeThickness = 1 }); Txt(v.ToString(), x + 3, y + 5, color, 10, false, 120); } last = v.ToString(); }
                    }
                    if (isBool) chart.Children.Add(poly);
                    if (isBool) { Txt("1", Left0 - 12, y - 1, Grey, 9); Txt("0", Left0 - 12, y + h - 13, Grey, 9); }
                    var cur = vals.LastOrDefault().V;
                    footParts.Add($"{names[i]}: {(isBool ? (Num(cur) > 0.5 ? "TRUE" : "FALSE") : cur?.ToString() ?? "—")}");
                }
                else
                {
                    var nums = vals.Select(v => (v.T, N: Num(v.V))).Where(v => v.N.HasValue).Select(v => (v.T, N: v.N.Value)).ToList();
                    double lo = nums.Count > 0 ? nums.Min(v => v.N) : 0, hi = nums.Count > 0 ? nums.Max(v => v.N) : 1;
                    if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
                    double pad = (hi - lo) * 0.08; lo -= pad; hi += pad;
                    double Y(double v) => y + h - 3 - (v - lo) / (hi - lo) * (h - 6);
                    int lines = h > 150 ? 8 : 4;
                    for (int g = 1; g < lines; g++)
                    {
                        double gy = y + g * h / lines;
                        chart.Children.Add(new WLine { X1 = Left0, X2 = Left0 + PlotW, Y1 = gy, Y2 = gy, Stroke = GridBrush, StrokeThickness = 0.5 });
                        if (h > 150) Txt(Fmt(lo + (hi - lo) * (1 - (gy - y - 3) / (h - 6))), Left0 + PlotW + 2, gy - 7, Grey, 9);
                    }
                    var poly = new Polyline { Stroke = color, StrokeThickness = 1.6 };
                    // many samples: one point per pixel column is enough (min and max kept)
                    foreach (var grp in nums.GroupBy(v => (int)X(v.T)))
                    {
                        var px = Math.Max(Left0, Math.Min(Left0 + PlotW, grp.Key));
                        poly.Points.Add(new Point(px, Y(grp.First().N)));
                        if (grp.Count() > 2) { poly.Points.Add(new Point(px, Y(grp.Min(v => v.N)))); poly.Points.Add(new Point(px, Y(grp.Max(v => v.N)))); }
                        poly.Points.Add(new Point(px, Y(grp.Last().N)));
                    }
                    chart.Children.Add(poly);
                    Txt(Fmt(hi - pad), Left0 + PlotW + 2, y - 2, Grey, 9);
                    Txt(Fmt(lo + pad), Left0 + PlotW + 2, y + h - 12, Grey, 9);
                    var cur = nums.Count > 0 ? nums[nums.Count - 1].N : (double?)null;
                    footParts.Add($"{names[i]}: {(cur.HasValue ? Fmt(cur.Value) : "—")}" + (nums.Count > 0 ? $"  (min {Fmt(nums.Min(v => v.N))}, max {Fmt(nums.Max(v => v.N))})" : ""));
                }
                y += h + Gap;
            }
            // the time axis
            Txt(Ago(t1 - t0 + offsetMs), Left0, y - 2, Grey, 10);
            Txt(offsetMs < 1 ? T("now", "jetzt") : Ago(offsetMs), Left0 + PlotW - 40, y - 2, Grey, 10);
            Txt(Ago(spanMs / 2 + offsetMs), Left0 + PlotW / 2 - 15, y - 2, Grey, 10);
            // the cursor with time and values
            if (cursorX is double cx)
            {
                chart.Children.Add(new WLine { X1 = cx, X2 = cx, Y1 = 4, Y2 = y - 4, Stroke = fore, StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 2, 2 } });
                double tc = t0 + (cx - Left0) / PlotW * (t1 - t0);
                var near = visible.OrderBy(s => Math.Abs(s.T - tc)).FirstOrDefault();
                if (near.V != null)
                {
                    var txt = Ago(Window().T1 - near.T + offsetMs) + "\n" + string.Join("\n", names.Select((n, k) => n + ": " + (near.V.ElementAtOrDefault(k) is object o ? (o is string so ? so : Fmt(Convert.ToDouble(o, CultureInfo.InvariantCulture))) : "—")));
                    var box = new Border { Background = Back, BorderBrush = Grey, BorderThickness = new Thickness(1), Padding = new Thickness(5, 2, 5, 2), Child = new TextBlock { Text = txt, FontSize = 10, Foreground = fore } };
                    Canvas.SetLeft(box, cx + 6 > Left0 + PlotW - 150 ? cx - 156 : cx + 6); Canvas.SetTop(box, 8); chart.Children.Add(box);
                }
            }
            chart.Height = y + 14;
            var rate = interval + " ms";
            foot.Text = string.Join(" · ", footParts) + "   ·   " + T("every ", "alle ") + rate + T(", read only", ", nur lesen") + (paused ? T(" · frozen: wheel zooms, drag moves", " · eingefroren: Mausrad zoomt, Ziehen verschiebt") : T(" · wheel zooms", " · Mausrad zoomt"));
            foreach (Button b in ranges.Children) b.FontWeight = Math.Abs((double)b.Tag - spanMs) < 1 ? FontWeights.Bold : FontWeights.Normal;
        }

        static string Fmt(double v) => Math.Abs(v) >= 1000 || Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("0.##", CultureInfo.CurrentCulture) : v.ToString("0.###", CultureInfo.CurrentCulture);
        static string Ago(double ms) => ms < 1000 ? $"-{ms:0} ms" : ms < 60000 ? $"-{ms / 1000:0.#} s" : $"-{ms / 60000:0.#} min";
    }
}
