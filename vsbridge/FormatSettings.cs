// TcAgentBridge - "Formatter settings": every rule of the ST formatter with a live preview (before | after) of what it does,
// saved into formatting.json of a knowledge level, and "Wish a rule or feature ..." as a GitHub issue.
//
//   Tools > Formatter settings (TcAgent) ... ; from the message of Format ST code as well.
//   The preview formats an example, or the object open in the editor, with the rules as they are set in the window
//   (TcAgent.exe format --stdin --rules <temp file>), a moment after each change. Nothing is saved before "Save".
//   Rules locked by a level above cannot be changed here; the window says by which level.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        const int CmdFormatSettings = 0x0104;

        const string SampleDeclaration =
"FUNCTION_BLOCK FB_Station\r\nVAR_INPUT\r\nbStart:BOOL; // start the cycle\r\nnParts : INT:=0; // parts so far\r\ntTimeout:TIME:=t#5s;\r\nEND_VAR\r\nVAR\r\nfbTimer   :   TON;\r\neState : E_State;\r\nEND_VAR\r\n";

        const string SampleImplementation =
"// cycle of the station\r\nif bStart and not bBusy or bSetup and bKey then\r\nbBusy:=true;\r\nend_if\r\n\r\n\r\ncase eState of\r\nE_State.Idle:\r\nif bStart then eState:=E_State.Run; end_if\r\nE_State.Run:\r\nfbTimer(IN:=bBusy AND NOT bDone, PT:=tTimeout);\r\nfbAxis(Execute := bBusy, Position := rTarget, Velocity := rVelocity, Acceleration := rAcc, Deceleration := rAcc, Jerk := rJerk);\r\nelse\r\n;\r\nend_case\r\n";

        internal void ShowFormatSettings()
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { FormatMessage("Formatter settings: TcAgent.exe was not found - set it up in the TcAgent Chat first.", true); return; }
            var sln = TcUnitControl.SolutionPath();
            Dictionary<string, object> state;
            try { state = FmtJson(FmtRun(exe, "format --show-rules --json" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), null)); }
            catch (Exception ex) { FormatMessage("Formatter settings: " + Inner(ex) + "\n\nTcAgent 0.54 or newer is needed.", true); return; }
            var rules = (state["rules"] as Dictionary<string, object>) ?? new Dictionary<string, object>();

            // the object open in the editor, for the preview
            string openDecl = null, openImpl = null;
            try
            {
                var dv = ActiveDocView(out _);
                var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
                foreach (var pn in new[] { "InterfaceEditorView", "ImplementationEditorView" })
                {
                    if (!(SafeGet(cds, pn) is System.Windows.Forms.Control ctl)) continue;
                    var syn = ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl
                            : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0);
                    var t = SafeGet(syn, "Text") as string;
                    if (pn == "InterfaceEditorView") openDecl = t; else openImpl = t;
                }
            }
            catch { }

            var w = new Window
            {
                Title = "Formatter settings (TcAgent)", Icon = AppIcon, Width = 1180, Height = 760, MinWidth = 760, MinHeight = 480,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            try { w.Owner = Application.Current?.MainWindow; } catch { }
            if (Application.Current != null) ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(Control.BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(Control.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

            var loaded = rules.ToDictionary(k => k.Key, k => FmtStr(k.Value as Dictionary<string, object>, "value") ?? "", StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);

            // ---- left: the rules
            var grid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sources = new Dictionary<string, TextBlock>();
            int row = 0;
            string lastGroup = null;
            Action changed = null;
            foreach (var kv in rules)
            {
                var r = kv.Value as Dictionary<string, object>;
                string key = kv.Key, group = key.Split('.')[0];
                if (group != lastGroup)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var h = new TextBlock { Text = GroupName(group), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 2) };
                    Grid.SetRow(h, row); Grid.SetColumnSpan(h, 3); grid.Children.Add(h); row++;
                    lastGroup = group;
                }
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var desc = FmtStr(r, "description") ?? "";
                var lab = new TextBlock { Text = key, ToolTip = desc + "\nDefault: " + FmtStr(r, "default"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 6, 1) };
                var values = (FmtStr(r, "values") ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                bool number = values.Contains("number");
                var box = new ComboBox { IsEditable = number, Margin = new Thickness(0, 1, 6, 1), MinWidth = 110, ToolTip = desc };
                foreach (var v in values.Where(v => v != "number")) box.Items.Add(v);
                box.Text = current[key];
                if (!number) box.SelectedItem = current[key];
                var src = new TextBlock { Text = FmtStr(r, "from"), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = desc };
                sources[key] = src;
                if (FmtStr(r, "locked") is string by)
                {
                    box.IsEnabled = false;
                    src.Text = "🔒 locked by " + by;
                    lab.ToolTip = desc + "\nLocked by " + by + " - it cannot be changed on another level.";
                }
                void Pick()
                {
                    var v = (box.IsEditable ? box.Text : box.SelectedItem as string ?? box.Text)?.Trim() ?? "";
                    if (v.Length == 0 || v == current[key]) return;
                    current[key] = v;
                    src.Text = v == loaded[key] ? FmtStr(r, "from") : "changed - not saved";
                    changed?.Invoke();
                }
                box.SelectionChanged += (s, e) => { if (!box.IsEditable) Pick(); else if (box.SelectedItem is string si) { box.Text = si; Pick(); } };
                box.LostFocus += (s, e) => Pick();
                box.KeyUp += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) Pick(); };
                Grid.SetRow(lab, row); Grid.SetColumn(lab, 0); grid.Children.Add(lab);
                Grid.SetRow(box, row); Grid.SetColumn(box, 1); grid.Children.Add(box);
                Grid.SetRow(src, row); Grid.SetColumn(src, 2); grid.Children.Add(src);
                row++;
            }
            var left = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

            // ---- right: the preview
            TextBox Code(bool before) => new TextBox
            {
                IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Opacity = before ? 0.8 : 1,
                Margin = new Thickness(0, 2, before ? 4 : 0, 0)
            };
            var declBefore = Code(true); var declAfter = Code(false); var implBefore = Code(true); var implAfter = Code(false);
            UIElement Pair(TextBox a, TextBox b)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition());
                var ha = new TextBlock { Text = "Before", Opacity = 0.7 }; var hb = new TextBlock { Text = "After", Opacity = 0.7 };
                Grid.SetColumn(hb, 1); Grid.SetRow(a, 1); Grid.SetRow(b, 1); Grid.SetColumn(b, 1);
                g.Children.Add(ha); g.Children.Add(hb); g.Children.Add(a); g.Children.Add(b);
                return g;
            }
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Declaration", Content = Pair(declBefore, declAfter) });
            tabs.Items.Add(new TabItem { Header = "Implementation", Content = Pair(implBefore, implAfter) });
            tabs.SelectedIndex = 1;
            var useOpen = new CheckBox { Content = "Preview the object open in the editor", IsChecked = openImpl != null || openDecl != null, IsEnabled = openImpl != null || openDecl != null, Margin = new Thickness(0, 0, 0, 4) };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) };
            var right = new DockPanel();
            DockPanel.SetDock(useOpen, Dock.Top); right.Children.Add(useOpen);
            DockPanel.SetDock(note, Dock.Bottom); right.Children.Add(note);
            right.Children.Add(tabs);

            // ---- the preview runs a moment after the last change, the newest one wins
            int version = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                int my = ++version;
                bool open = useOpen.IsChecked == true;
                string d = open ? openDecl ?? "" : SampleDeclaration, i = open ? openImpl ?? "" : SampleImplementation;
                declBefore.Text = d; implBefore.Text = i;
                note.Text = "Formatting the preview ...";
                var rulesFile = Path.Combine(Path.GetTempPath(), "tcagent-format-preview-" + Process.GetCurrentProcess().Id + ".json");
                var obj = new Dictionary<string, object> { ["rules"] = current.ToDictionary(k => k.Key, k => (object)k.Value) };
                File.WriteAllText(rulesFile, new JavaScriptSerializer().Serialize(obj), new UTF8Encoding(false));
                try
                {
                    var dt = Task.Run(() => d.Length == 0 ? null : FmtJson(FmtRun(exe, $"format --stdin --json --part declaration --rules \"{rulesFile}\"", d)));
                    var it = Task.Run(() => i.Length == 0 ? null : FmtJson(FmtRun(exe, $"format --stdin --json --part implementation --rules \"{rulesFile}\"", i)));
                    var dr = await dt; var ir = await it;
                    if (my != version) return;
                    declAfter.Text = FmtStr(dr, "text") ?? d; implAfter.Text = FmtStr(ir, "text") ?? i;
                    var errs = new[] { ("Declaration", FmtStr(dr, "error")), ("Implementation", FmtStr(ir, "error")) }.Where(x => !string.IsNullOrEmpty(x.Item2)).Select(x => x.Item1 + ": " + x.Item2).ToList();
                    int n = current.Count(k => k.Value != loaded[k.Key]);
                    note.Text = errs.Count > 0 ? string.Join("\n", errs) : n == 0 ? "The preview shows the rules as they apply now." : $"{n} rule(s) changed - not saved yet.";
                }
                catch (Exception ex) { if (my == version) note.Text = "Preview: " + Inner(ex); }
            };
            changed = () => { timer.Stop(); timer.Start(); };
            useOpen.Checked += (s, e) => changed(); useOpen.Unchecked += (s, e) => changed();

            // ---- bottom: level, save, wish
            var level = new ComboBox { MinWidth = 150, Margin = new Thickness(6, 0, 12, 0), ToolTip = "The knowledge level whose formatting.json gets the changed rules" };
            if (!string.IsNullOrEmpty(sln)) level.Items.Add("project");
            level.Items.Add("user");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TCAGENT_TEAM"))) level.Items.Add("team");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TCAGENT_COMPANY"))) level.Items.Add("company");
            level.SelectedIndex = 0;
            var wish = new Button { Content = "Wish a rule or feature ...", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Opens a new issue on GitHub with the example of the preview - you check it there and send it yourself" };
            wish.Click += (s, e) => WishFormatterRule(tabs.SelectedIndex == 0 ? declBefore.Text : implBefore.Text, tabs.SelectedIndex == 0 ? declAfter.Text : implAfter.Text,
                                                      current.Where(k => k.Value != FmtStr(rules[k.Key] as Dictionary<string, object>, "default")).Select(k => k.Key + " = " + k.Value).ToList());
            var reset = new Button { Content = "Undo changes", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            var save = new Button { Content = "Save", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
            var cancel = new Button { Content = "Close", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            cancel.Click += (s, e) => w.Close();
            reset.Click += (s, e) => { w.Close(); ShowFormatSettings(); };
            save.Click += (s, e) =>
            {
                var diff = current.Where(k => k.Value != loaded[k.Key]).ToDictionary(k => k.Key, k => (object)k.Value);
                if (diff.Count == 0) { note.Text = "Nothing changed."; return; }
                var lv = level.SelectedItem as string ?? "project";
                var file = Path.Combine(Path.GetTempPath(), "tcagent-format-save-" + Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(file, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["rules"] = diff }), new UTF8Encoding(false));
                try
                {
                    var res = FmtRun(exe, $"format --save \"{file}\" --level {lv}" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), null);
                    note.Text = res.Trim() + " - a later level can still change them; Format ST code uses them from now on.";
                    foreach (var k in diff.Keys) { loaded[k] = current[k]; sources[k].Text = lv; }
                }
                catch (Exception ex) { note.Text = "Not saved: " + Inner(ex); }
            };
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 10), LastChildFill = false };
            var lvLabel = new TextBlock { Text = "Save to level:", VerticalAlignment = VerticalAlignment.Center };
            foreach (var x in new UIElement[] { lvLabel, level, wish }) { DockPanel.SetDock(x, Dock.Left); bar.Children.Add(x); }
            foreach (var x in new UIElement[] { cancel, save, reset }) { DockPanel.SetDock(x, Dock.Right); bar.Children.Add(x); }

            var body = new Grid { Margin = new Thickness(12, 10, 12, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
            body.Children.Add(left); body.Children.Add(split); body.Children.Add(right);
            var root = new DockPanel();
            var head = new TextBlock
            {
                Text = "Every rule of the ST formatter. Change one and the preview shows what it does; Save writes the changed rules into formatting.json of the chosen level. Rules: docs/FORMATTER.md",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(12, 10, 12, 0)
            };
            DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            root.Children.Add(body);
            w.Content = root;
            w.Loaded += (s, e) => changed();
            w.ShowDialog();
        }

        static string GroupName(string g) => g switch
        {
            "indent" => "Indentation", "case" => "Case", "space" => "Spaces", "comment" => "Comments", "align" => "Alignment of declarations",
            "wrap" => "Long lines", "blank" => "Blank lines", "eol" => "Line ends", "paren" => "Brackets in conditions", _ => g
        };

        static string FmtStr(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;

        static Dictionary<string, object> FmtJson(string s) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(s);

        /// <summary>TcAgent.exe with arguments and optionally text on stdin; the output, or an exception with the error text.</summary>
        static string FmtRun(string exe, string args, string input)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(psi))
            {
                if (input != null)
                {
                    var bytes = new UTF8Encoding(false).GetBytes(input);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                }
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new Exception("TcAgent.exe did not answer within 30 s"); }
                if (string.IsNullOrWhiteSpace(o.Result) || p.ExitCode == 2) throw new Exception((e.Result + o.Result).Trim());
                return o.Result;
            }
        }

        /// <summary>A new issue on GitHub: the wished rule or feature with the example of the preview. The person checks and sends it.</summary>
        void WishFormatterRule(string before, string after, List<string> settings)
        {
            string Cut(string s, int n) { s = (s ?? "").Replace("\r\n", "\n").TrimEnd(); return s.Length <= n ? s : s.Substring(0, n) + "\n..."; }
            var body = new StringBuilder();
            body.AppendLine("### Rule or feature I wish for the ST formatter").AppendLine().AppendLine("(What should the formatter do? Which construct?)").AppendLine();
            body.AppendLine("### Example").AppendLine().AppendLine("Code (as written):").AppendLine("```").AppendLine(Cut(before, 1200)).AppendLine("```").AppendLine();
            body.AppendLine("The formatter makes of it today:").AppendLine("```").AppendLine(Cut(after, 1200)).AppendLine("```").AppendLine();
            body.AppendLine("What I would like instead:").AppendLine("```").AppendLine().AppendLine("```").AppendLine();
            body.AppendLine("### Context").AppendLine().AppendLine($"TcAgent {BridgeVersion}" + (settings.Count > 0 ? "; rules other than the default: " + string.Join(", ", settings) : "; default rules"));
            body.AppendLine().AppendLine("Please remove names of customers, plants or projects from the example before you send it.");
            const string url = "https://github.com/ThorstenBrach/TcAgent/issues/new";
            var full = url + "?labels=" + Uri.EscapeDataString("enhancement,formatter") + "&title=" + Uri.EscapeDataString("Formatter: ") + "&body=" + Uri.EscapeDataString(body.ToString());
            bool tooLong = full.Length > 7500;
            if (tooLong) { try { System.Windows.Forms.Clipboard.SetText(body.ToString()); } catch { } full = url + "?labels=" + Uri.EscapeDataString("enhancement,formatter") + "&title=" + Uri.EscapeDataString("Formatter: "); }
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
            if (MessageBox.Show(owner, "Open a new issue on GitHub in the browser?\n\nIt contains the example of the preview - check it there, remove names of customers or plants, and send it yourself." +
                                (tooLong ? "\n\nThe text is long: it is in the clipboard - paste it into the issue." : ""),
                                "Wish a rule or feature", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
            catch (Exception ex) { FormatMessage("Could not open the browser: " + ex.Message, true); }
        }
    }
}
