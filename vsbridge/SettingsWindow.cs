// TcAgentBridge - "TcAgent settings": every setting of TcAgent in one window, built from the schema of TcAgent.exe
// (src/SettingsSchema.cs, TcAgent.exe settings --json), and the target systems the agent may write to (src/Targets.cs,
// TcAgent.exe targets). Built after the approved mockup and docs/UI_GUIDELINES.md.
//
//   Tools > TcAgent settings ... · ⋯ > TcAgent settings … in the TcAgent Chat and the Knowledge window
//   left: the pages and a search; right: the page - a list of settings with value and where it comes from, the details of
//   the selected one. Save writes settings.json and the running TcAgent servers read it again (Reload.cs).
//   Target systems: release a target for writing (or reading only), for a time or for good; it takes effect at once.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        const int CmdSettings = 0x0105;

        static readonly SolidColorBrush SetGreen = B(0x2E, 0x9D, 0x4A), SetOrange = B(0xD0, 0x7A, 0x10), SetRed = B(0xD0, 0x40, 0x40);

        static string SStr(object d, string k) => d is Dictionary<string, object> x && x.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        static IEnumerable<Dictionary<string, object>> SList(object d, string k) =>
            d is Dictionary<string, object> x && x.TryGetValue(k, out var v) && v is ArrayList a ? a.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

        sealed class SettingRow
        {
            public Dictionary<string, object> Def;
            public string Key, Original, Value;
            public FrameworkElement Editor;
            public TextBlock State;
            public bool Changed => (Value ?? "") != (Original ?? "");
        }

        /// <summary>A themed dialog window owned by the given window (or the IDE).</summary>
        static Window SetWindow(string title, double width, double height, Window owner)
        {
            var w = new Window
            {
                Title = title, Icon = AppIcon, Width = width, Height = height, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = owner == null ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize
            };
            try { w.Owner = owner ?? Application.Current?.MainWindow; } catch { }
            if (Application.Current != null) ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(Control.BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(Control.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            return w;
        }

        static Button SetButton(string text, string tip, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(primary ? 16 : 10, 3, primary ? 16 : 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            if (primary)
            {
                b.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.SystemHighlightBrushKey);
                b.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemHighlightTextBrushKey);
            }
            return b;
        }

        static Border SetFrame(UIElement child, Thickness margin)
        {
            var b = new Border { Child = child, BorderThickness = new Thickness(1), Margin = margin, Padding = new Thickness(8, 6, 8, 6) };
            b.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return b;
        }

        // ------------------------------------------------------------------ the window
        internal void ShowSettings(string page = null)
        {
            fmtGerman = UiGerman();
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { FormatMessage(T("TcAgent settings: TcAgent.exe was not found - set it up in the TcAgent Chat first.", "TcAgent-Einstellungen: TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
            var w = SetWindow(T("TcAgent settings", "TcAgent-Einstellungen"), 1120, 700, null);
            w.MinWidth = 760; w.MinHeight = 480;

            // ---- left: search and pages
            var search = new TextBox { Margin = new Thickness(10, 10, 10, 8), Padding = new Thickness(3, 2, 3, 2), ToolTip = T("Search settings by name, variable or description (Ctrl+F)", "Einstellungen nach Name, Variable oder Beschreibung suchen (Strg+F)") };
            var hint = new TextBlock { Text = T("Search settings (Ctrl+F)", "Einstellungen suchen (Strg+F)"), Opacity = 0.55, IsHitTestVisible = false, Margin = new Thickness(16, 13, 0, 0) };
            var pages = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            var pageStyle = new Style(typeof(ListBoxItem));
            pageStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            pageStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
            TcUnitControl.ThemedSelection(pageStyle, false);
            pages.ItemContainerStyle = pageStyle;
            var left = new DockPanel { Width = 220 };
            var sg = new Grid(); sg.Children.Add(search); sg.Children.Add(hint);
            DockPanel.SetDock(sg, Dock.Top); left.Children.Add(sg); left.Children.Add(pages);
            left.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            var sep = new Border { Width = 1 }; sep.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            // ---- right: the page
            var content = new ContentControl { Margin = new Thickness(14, 10, 14, 6) };

            // ---- bottom: buttons and status line
            var note = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                                       Text = T("Saved settings take effect at once in every running TcAgent server.", "Gespeicherte Einstellungen wirken sofort in allen laufenden TcAgent-Servern.") };
            var undo = SetButton(T("Discard", "Verwerfen"), T("Back to the values as they apply now", "Zurück zu den Werten, wie sie jetzt gelten"));
            var save = SetButton(T("Save", "Speichern"), T("Writes the changed settings into settings.json; the running servers read them again", "Schreibt die geänderten Einstellungen in settings.json; die laufenden Server lesen sie neu"), true);
            var close = SetButton(T("Close", "Schließen"), null); close.IsCancel = true; close.Margin = new Thickness(0);
            save.IsEnabled = undo.IsEnabled = false;
            var reload = SetButton(T("⟳ Reload", "⟳ Neu laden"), T("Every running TcAgent reads settings.json, the target systems, the mode, modules.json and the plugins again, and the chat its backends - for files changed outside this window, no restart (F5)",
                                                                     "Alle laufenden TcAgent-Server lesen settings.json, Zielsysteme, Modus, modules.json und Plugins neu, der Chat seine Backends - für Dateien, die außerhalb dieses Fensters geändert wurden, ohne Neustart (F5)"));
            reload.Margin = new Thickness(0, 0, 12, 0);
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = true };
            DockPanel.SetDock(reload, Dock.Left); bar.Children.Add(reload);
            foreach (var x in new UIElement[] { close, save, undo }) { DockPanel.SetDock(x, Dock.Right); bar.Children.Add(x); }
            bar.Children.Add(note);
            var barLine = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
            barLine.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var statusLeft = new TextBlock { Text = T("Reading the settings …", "Lese die Einstellungen …"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var statusRight = new TextBlock { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            var progress = new ProgressBar { Height = 2, IsIndeterminate = true, BorderThickness = new Thickness(0) };
            var status = new DockPanel();
            var si = new DockPanel { Margin = new Thickness(12, 3, 12, 3) };
            DockPanel.SetDock(statusRight, Dock.Right); si.Children.Add(statusRight); si.Children.Add(statusLeft);
            DockPanel.SetDock(progress, Dock.Top); status.Children.Add(progress); status.Children.Add(si);
            status.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.StatusBarDefaultBrushKey);
            statusLeft.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);
            statusRight.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);

            var main = new DockPanel();
            DockPanel.SetDock(left, Dock.Left); main.Children.Add(left);
            DockPanel.SetDock(sep, Dock.Left); main.Children.Add(sep);
            main.Children.Add(content);
            var root = new DockPanel();
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            DockPanel.SetDock(barLine, Dock.Bottom); root.Children.Add(barLine);
            root.Children.Add(main);
            w.Content = root;
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; } };
            close.Click += (s, e) => w.Close();

            // ---- state
            var rows = new List<SettingRow>();
            Dictionary<string, object> schema = null, targets = null;
            string current = page ?? "general";
            bool busy = false;

            void Changed()
            {
                var n = rows.Count(r => r.Changed);
                save.IsEnabled = undo.IsEnabled = n > 0 && !busy;
                foreach (var r in rows) ShowState(r);
                if (n > 0) statusLeft.Text = fmtGerman ? $"{n} Einstellung{(n == 1 ? "" : "en")} geändert - nicht gespeichert" : $"{n} setting{(n == 1 ? "" : "s")} changed - not saved";
            }

            void ShowState(SettingRow r)
            {
                if (r.State == null) return;
                var from = SStr(r.Def, "from");
                if (r.Changed) { r.State.Text = T("✎ changed, not saved", "✎ geändert, nicht gespeichert"); r.State.Foreground = SetOrange; r.State.Opacity = 1; return; }
                if (from == "outside")
                {
                    r.State.Text = "⚠ " + (fmtGerman ? "von außen gesetzt" : "set from outside") + ": " + OutsideName(SStr(r.Def, "outsideFrom"));
                    r.State.Foreground = SetOrange; r.State.Opacity = 1; return;
                }
                r.State.ClearValue(TextBlock.ForegroundProperty);
                r.State.Opacity = 0.65;
                r.State.Text = from == "saved" ? T("saved (user)", "gespeichert (Benutzer)") : T("default", "Standard");
            }

            string OutsideName(string f) => !fmtGerman || f == null ? f : f.Replace("MCP configuration", "MCP-Konfiguration").Replace("Windows environment variable", "Windows-Umgebungsvariable");

            string Label(Dictionary<string, object> d) => (fmtGerman ? SStr(d, "labelDe") : null) ?? SStr(d, "label");
            string Desc(Dictionary<string, object> d) => (fmtGerman ? SStr(d, "descriptionDe") : null) ?? SStr(d, "description");

            /// <summary>Plain words for a value of a choice or switch.</summary>
            string ValueWord(Dictionary<string, object> d, string v)
            {
                var vals = d.TryGetValue("values", out var a) && a is ArrayList al ? al.Cast<object>().Select(x => x?.ToString() ?? "").ToList() : new List<string>();
                var labels = SList(d, "valueLabels").ToList();
                var i = vals.FindIndex(x => x.Equals(v ?? "", StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i < labels.Count ? (fmtGerman ? SStr(labels[i], "de") : SStr(labels[i], "en")) : v;
            }

            // ---- an editor for one setting
            FrameworkElement Editor(SettingRow r)
            {
                var d = r.Def;
                var type = SStr(d, "type");
                if (type == "choice" || type == "onoff")
                {
                    var box = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
                    var vals = d["values"] is ArrayList al ? al.Cast<object>().Select(x => x?.ToString() ?? "").ToList() : new List<string>();
                    foreach (var v in vals) box.Items.Add(new ComboBoxItem { Content = ValueWord(d, v), Tag = v });
                    var cur = string.IsNullOrEmpty(r.Value) ? SStr(d, "default") ?? "" : r.Value;
                    box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((string)i.Tag).Equals(cur, StringComparison.OrdinalIgnoreCase)) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
                    box.SelectionChanged += (s, e) =>
                    {
                        var v = (string)((ComboBoxItem)box.SelectedItem).Tag;
                        r.Value = v.Equals(SStr(d, "default") ?? "", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.Original) ? r.Original : v;
                        Changed();
                    };
                    return box;
                }
                var tb = new TextBox { Text = r.Value ?? "", MinWidth = 260, Padding = new Thickness(3, 2, 3, 2) };
                var ph = new TextBlock { Text = string.IsNullOrEmpty(SStr(d, "default")) ? T("not set", "nicht gesetzt") : SStr(d, "default"), Opacity = 0.5, IsHitTestVisible = false, Margin = new Thickness(6, 3, 0, 0) };
                ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                var err = new TextBlock { Foreground = SetRed, FontSize = 11, Visibility = Visibility.Collapsed };
                tb.TextChanged += (s, e) =>
                {
                    ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    var t = tb.Text.Trim();
                    string bad = type == "number" && t.Length > 0 && !(int.TryParse(t, out var n) && n >= 0) ? T("a whole number, 0 or more", "eine ganze Zahl, 0 oder größer")
                               : t.Contains("\"") ? T("no quotes", "keine Anführungszeichen") : null;
                    err.Text = bad ?? ""; err.Visibility = bad == null ? Visibility.Collapsed : Visibility.Visible;
                    r.Value = bad == null ? t : r.Original;
                    Changed();
                };
                var g = new Grid(); g.Children.Add(tb); g.Children.Add(ph);
                var sp = new StackPanel();
                var line = new DockPanel();
                if (type == "path" || type == "paths")
                {
                    var browse = new Button { Content = T("Browse …", "Durchsuchen …"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
                    browse.Click += (s, e) =>
                    {
                        if (r.Key == "TCAGENT_AUDIT")      // a file, not a folder
                        {
                            var fd = new Microsoft.Win32.SaveFileDialog { FileName = "audit.jsonl", Filter = "JSON Lines|*.jsonl", OverwritePrompt = false };
                            if (fd.ShowDialog(w) == true) tb.Text = fd.FileName;
                            return;
                        }
                        using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = Label(d) })
                            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                tb.Text = type == "paths" && tb.Text.Trim().Length > 0 ? tb.Text.Trim().TrimEnd(';') + ";" + dlg.SelectedPath : dlg.SelectedPath;
                    };
                    DockPanel.SetDock(browse, Dock.Right); line.Children.Add(browse);
                }
                line.Children.Add(g);
                sp.Children.Add(line); sp.Children.Add(err);
                return sp;
            }

            // ---- the details of a setting
            UIElement Detail(SettingRow r)
            {
                var d = r.Def;
                var sp = new StackPanel();
                var h = new TextBlock { TextWrapping = TextWrapping.Wrap };
                h.Inlines.Add(new Run(Label(d)) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                sp.Children.Add(h);
                var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                void Fact(string k, string v, bool mono = false)
                {
                    if (string.IsNullOrEmpty(v)) return;
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var a = new TextBlock { Text = k, Opacity = 0.65, Margin = new Thickness(0, 1, 8, 1) };
                    var b = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                    if (mono) b.FontFamily = new FontFamily("Consolas");
                    Grid.SetRow(a, grid.RowDefinitions.Count - 1); Grid.SetRow(b, grid.RowDefinitions.Count - 1); Grid.SetColumn(b, 1);
                    grid.Children.Add(a); grid.Children.Add(b);
                }
                Fact(T("What it does", "Was es tut"), Desc(d));
                var def = SStr(d, "default");
                Fact(T("Default", "Standard"), string.IsNullOrEmpty(def) ? T("not set", "nicht gesetzt") : ValueWord(d, def));
                var eff = SStr(d, "effective");
                Fact(T("Applies now", "Wirkt jetzt"), (string.IsNullOrEmpty(eff) ? T("not set", "nicht gesetzt") : ValueWord(d, eff)) +
                     (SStr(d, "from") == "outside" ? " - " + T("set from outside, wins over this window", "von außen gesetzt, gewinnt gegen dieses Fenster") + ": " + OutsideName(SStr(d, "outsideFrom")) : ""));
                Fact(T("Variable", "Variable"), SStr(d, "key"), true);
                Fact(T("Restart needed", "Neustart nötig"), SStr(d, "restart") == "True"
                    ? T("yes - takes effect when the AI client starts TcAgent again", "ja - wirkt, wenn der KI-Client TcAgent neu startet")
                    : T("no - read again at once", "nein - wird sofort neu geladen"));
                sp.Children.Add(grid);
                return sp;
            }

            // ---- a page of settings from the schema
            UIElement SettingsList(IEnumerable<SettingRow> list, Border detail)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                void Head(string t, int c)
                {
                    var b = new Border { Padding = new Thickness(6, 3, 6, 3), Child = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold } };
                    b.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                    Grid.SetColumn(b, c); g.Children.Add(b);
                }
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Head(T("Setting", "Einstellung"), 0); Head(T("Value", "Wert"), 1); Head(T("From", "Woher"), 2);
                Border selected = null;
                var selBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x6F, 0xD8));
                foreach (var r in list)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var i = g.RowDefinitions.Count - 1;
                    var back = new Border { Background = Brushes.Transparent };
                    Grid.SetRow(back, i); Grid.SetColumnSpan(back, 3); g.Children.Add(back);
                    var name = new TextBlock { Text = Label(r.Def), Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = SStr(r.Def, "key") };
                    r.Editor = Editor(r);
                    r.Editor.Margin = new Thickness(6, 3, 6, 3);
                    r.Editor.VerticalAlignment = VerticalAlignment.Center;
                    r.State = new TextBlock { Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                    Grid.SetRow(name, i); Grid.SetRow(r.Editor, i); Grid.SetColumn(r.Editor, 1); Grid.SetRow(r.State, i); Grid.SetColumn(r.State, 2);
                    g.Children.Add(name); g.Children.Add(r.Editor); g.Children.Add(r.State);
                    ShowState(r);
                    void Select() { if (selected != null) selected.Background = Brushes.Transparent; selected = back; back.Background = selBrush; detail.Child = Detail(r); }
                    foreach (var el in new FrameworkElement[] { name, r.State, back }) el.MouseLeftButtonDown += (s, e) => Select();
                    r.Editor.GotKeyboardFocus += (s, e) => Select();
                    r.Editor.PreviewMouseDown += (s, e) => Select();
                    if (selected == null) Select();
                }
                var frame = new Border { BorderThickness = new Thickness(1), Child = g };
                frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                return frame;
            }

            UIElement Heading(string title, string text)
            {
                var h = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                h.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                if (!string.IsNullOrEmpty(text)) h.Inlines.Add(new Run(" – " + text) { Foreground = Brushes.Gray });
                return h;
            }

            string PageTitle(string id) => SList(schema, "pages").Where(p => SStr(p, "id") == id).Select(p => fmtGerman ? SStr(p, "de") : SStr(p, "en")).FirstOrDefault() ?? id;
            string PageText(string id) => id switch
            {
                "general" => T("what the agent may do and where it writes down what it did", "was der Agent darf und wo er festhält, was er getan hat"),
                "gates" => T("when the agent stops and asks you", "wann der Agent anhält und dich fragt"),
                "knowledge" => T("where the agent's rules and knowledge come from", "woher Regeln und Wissen des Agenten kommen"),
                "advanced" => T("for finding problems - normally not needed", "zur Fehlersuche - normalerweise nicht nötig"),
                _ => null
            };

            // ---- show a page (or the search result)
            void Show()
            {
                var q = search.Text.Trim();
                hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                var panel = new DockPanel();
                var detail = SetFrame(null, new Thickness(0, 8, 0, 0));
                if (q.Length > 0)
                {
                    var hits = rows.Where(r => (Label(r.Def) + " " + r.Key + " " + Desc(r.Def)).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    DockPanel.SetDock(detail, Dock.Bottom);
                    var head = Heading(fmtGerman ? $"Suche: {hits.Count} Treffer" : $"Search: {hits.Count} found", null);
                    DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
                    if (hits.Count == 0) panel.Children.Add(new TextBlock { Text = T("No setting matches - try another word or clear the search (✕ Esc).", "Keine Einstellung passt - versuche ein anderes Wort oder leere die Suche (Esc)."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                    else { panel.Children.Add(detail); panel.Children.Add(new ScrollViewer { Content = SettingsList(hits, detail), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); }
                    content.Content = panel;
                    return;
                }
                if (current == "targets") { content.Content = TargetsPage(); return; }
                var mine = rows.Where(r => SStr(r.Def, "page") == current).ToList();
                var top = Heading(PageTitle(current), PageText(current));
                DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
                if (current == "general" || current == "knowledge")
                {
                    var links = new TextBlock { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Right };
                    links.Inlines.Add(Link(T("Export …", "Exportieren …"), Export)); links.Inlines.Add(new Run(" · ")); links.Inlines.Add(Link(T("Import …", "Importieren …"), Import));
                    DockPanel.SetDock(links, Dock.Top); panel.Children.Add(links);
                }
                DockPanel.SetDock(detail, Dock.Bottom); panel.Children.Add(detail);
                panel.Children.Add(new ScrollViewer { Content = SettingsList(mine, detail), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                content.Content = panel;
            }

            Hyperlink Link(string text, Action a) { var l = new Hyperlink(new Run(text)); l.Click += (s, e) => a(); return l; }

            // ---- target systems
            UIElement TargetsPage()
            {
                var panel = new DockPanel();
                var top = Heading(PageTitle("targets"), T("where the agent may write (activate, start/stop the PLC, write over ADS). It may read every target. A change takes effect at once.",
                                                         "wohin der Agent schreiben darf (aktivieren, PLC starten/stoppen, über ADS schreiben). Lesen darf er jedes Ziel. Eine Änderung wirkt sofort."));
                DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
                var list = SList(targets, "targets").ToList();
                // user mode runtimes found on this PC and not released yet
                foreach (var um in SList(targets, "userModeRuntimes").Where(u => !list.Any(t => SStr(t, "netId") == SStr(u, "netId"))).Take(2))
                {
                    var info = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    info.Inlines.Add(new Run("ℹ  " + T("A user mode runtime runs on this PC: ", "Auf diesem PC gibt es die Usermode-Runtime ")));
                    info.Inlines.Add(new Run(SStr(um, "name")) { FontWeight = FontWeights.SemiBold });
                    info.Inlines.Add(new Run($" ({SStr(um, "netId")}) – " + T("it has its own AMS Net ID and is not released yet.  ", "sie hat eine eigene AMS Net ID und ist noch nicht freigegeben.  ")));
                    var u = um;
                    info.Inlines.Add(Link(T("Release …", "Freigeben …"), () => EditTarget(SStr(u, "netId"), SStr(u, "name"), true, "1h")));
                    var bar = new Border { Child = info, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1),
                                           Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x8F, 0xE8)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x3B, 0x8F, 0xE8)) };
                    DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
                }
                // toolbar: one action list = the context menu
                Dictionary<string, object> sel = null;
                var add = SetButton(T("+ New target …", "+ Neues Ziel …"), T("Release a target system for writing or reading", "Ein Zielsystem zum Schreiben oder Lesen freigeben"), true);
                var extend = SetButton(T("⏱ Extend ▾", "⏱ Verlängern ▾"), T("Release the selected target for more time", "Das gewählte Ziel länger freigeben"));
                var edit = SetButton(T("Edit …", "Bearbeiten …"), T("Name, right and time of the selected target (Enter)", "Name, Recht und Dauer des gewählten Ziels (Enter)"));
                var del = SetButton(T("Delete …", "Löschen …"), T("Remove the selected target - the agent may then only read it (Del)", "Das gewählte Ziel entfernen - der Agent darf es dann nur noch lesen (Entf)"));
                var fromRoutes = SetButton(T("From routes …", "Aus Routen übernehmen …"), T("Take over targets from the ADS routes of TwinCAT on this PC", "Ziele aus den ADS-Routen von TwinCAT auf diesem PC übernehmen"));
                var newRoutes = SList(targets, "routes").Where(rt => !list.Any(t => SStr(t, "netId") == SStr(rt, "netId"))).ToList();
                fromRoutes.IsEnabled = newRoutes.Count > 0;
                if (newRoutes.Count == 0) fromRoutes.ToolTip = T("Every ADS route of this PC is in the list already (or there is none)", "Alle ADS-Routen dieses PCs stehen schon in der Liste (oder es gibt keine)");
                var tb = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var b in new[] { add, fromRoutes, extend, edit, del }) tb.Children.Add(b);
                DockPanel.SetDock(tb, Dock.Top); panel.Children.Add(tb);
                add.Click += (s, e) => EditTarget("", "", true, "1h");
                fromRoutes.Click += (s, e) => ImportRoutes(newRoutes);
                List<MenuItem> ExtendItems()
                {
                    var l = new List<MenuItem>();
                    foreach (var (k, en, de) in new[] { ("1h", "for 1 hour", "um 1 Stunde"), ("4h", "for 4 hours", "um 4 Stunden"), ("today", "until the end of today", "bis heute Abend"), ("forever", "for good", "dauerhaft") })
                    {
                        var mi = new MenuItem { Header = T(en, de) };
                        var key = k;
                        mi.Click += (s, e) => { if (sel != null) SaveTarget(SStr(sel, "netId"), SStr(sel, "name"), true, key); };
                        l.Add(mi);
                    }
                    return l;
                }
                ContextMenu ExtendMenu() { var m = new ContextMenu(); foreach (var i in ExtendItems()) m.Items.Add(i); return m; }
                extend.Click += (s, e) => { var m = ExtendMenu(); m.PlacementTarget = extend; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true; };
                edit.Click += (s, e) => { if (sel != null) EditTarget(SStr(sel, "netId"), SStr(sel, "name"), SStr(sel, "access") != "read", SStr(sel, "until") == null ? "forever" : "1h"); };
                del.Click += (s, e) => { if (sel != null) DeleteTarget(SStr(sel, "netId"), SStr(sel, "name")); };

                // the list
                var lv = new ListView { BorderThickness = new Thickness(1), MinHeight = 120 };
                lv.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                lv.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                var rowStyle = new Style(typeof(ListViewItem));
                rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
                TcUnitControl.ThemedSelection(rowStyle, true);
                lv.ItemContainerStyle = rowStyle;
                var gv = new GridView();
                GridViewColumn Col(string h, double wd, Func<Dictionary<string, object>, string> text, Func<Dictionary<string, object>, Brush> color = null, bool mono = false)
                {
                    var f = new FrameworkElementFactory(typeof(TextBlock));
                    f.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("[" + h + "]"));
                    if (mono) f.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
                    if (color != null) f.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("[" + h + "#c]"));
                    return new GridViewColumn { Header = h, Width = wd, CellTemplate = new DataTemplate { VisualTree = f } };
                }
                string State(Dictionary<string, object> t) => SStr(t, "state") switch
                {
                    "expired" => T("⊘ expired", "⊘ abgelaufen"), "expires" => T("⏱ runs out", "⏱ läuft ab"), "read" => T("○ read only", "○ nur lesen"), _ => T("✓ released", "✓ freigegeben")
                };
                Brush StateColor(Dictionary<string, object> t) => SStr(t, "state") switch { "expired" => Brushes.Gray, "expires" => SetOrange, "read" => Brushes.Gray, _ => SetGreen };
                string Until(Dictionary<string, object> t)
                {
                    if (!DateTime.TryParse(SStr(t, "until"), out var u)) return SStr(t, "access") == "read" ? "—" : T("for good", "dauerhaft");
                    var left = u - DateTime.Now;
                    var when = u.Date == DateTime.Today ? T("today", "heute") + " " + u.ToString("HH:mm") : u.ToString("dd.MM. HH:mm");
                    return left.TotalSeconds <= 0 ? when : when + (left.TotalHours < 24 ? (fmtGerman ? $" (noch {(int)left.TotalHours} h {left.Minutes:00} min)" : $" ({(int)left.TotalHours} h {left.Minutes:00} min left)") : "");
                }
                var cState = T("Status", "Status"); var cName = T("Name", "Name"); var cId = "AMS Net ID"; var cMay = T("May", "Darf"); var cUntil = T("Until", "Bis"); var cFrom = T("From", "Woher");
                gv.Columns.Add(Col(cState, 105, State, StateColor)); gv.Columns.Add(Col(cName, 170, null)); gv.Columns.Add(Col(cId, 125, null, null, true));
                gv.Columns.Add(Col(cMay, 80, null)); gv.Columns.Add(Col(cUntil, 180, null)); gv.Columns.Add(Col(cFrom, 140, null));
                lv.View = gv;
                var items = new List<Dictionary<string, object>>();
                var mayWrite = T("write", "schreiben"); var mayRead = T("read only", "nur lesen");
                // this PC and what the configuration allows - not changeable here
                var conf = SStr(targets, "configured") ?? "local";
                var confFrom = SStr(targets, "configuredFrom") == "outside" ? T("🔒 MCP configuration", "🔒 MCP-Konfiguration") : SStr(targets, "configuredFrom") == "saved" ? T("setting", "Einstellung") : T("default", "Standard");
                foreach (var c in conf.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                    items.Add(new Dictionary<string, object> { [cState] = T("✓ released", "✓ freigegeben"), [cState + "#c"] = SetGreen, [cName] = c.Equals("local", StringComparison.OrdinalIgnoreCase) ? T("This PC", "Dieser PC") : T("(no name)", "(ohne Namen)"),
                                                                [cId] = c, [cMay] = mayWrite, [cUntil] = T("for good", "dauerhaft"), [cFrom] = confFrom, ["#fixed"] = true });
                foreach (var t in list)
                    items.Add(new Dictionary<string, object> { [cState] = State(t), [cState + "#c"] = StateColor(t), [cName] = string.IsNullOrEmpty(SStr(t, "name")) ? T("(no name)", "(ohne Namen)") : SStr(t, "name"),
                                                                [cId] = SStr(t, "netId"), [cMay] = SStr(t, "access") == "read" ? mayRead : mayWrite, [cUntil] = Until(t), [cFrom] = T("this window", "dieses Fenster"), ["#t"] = t });
                lv.ItemsSource = items;
                var detail = SetFrame(null, new Thickness(0, 8, 0, 0));
                DockPanel.SetDock(detail, Dock.Bottom); panel.Children.Add(detail);

                // the setting TCAGENT_ALLOW_TARGETS below the list
                var allowRow = rows.FirstOrDefault(r => r.Key == "TCAGENT_ALLOW_TARGETS");
                if (allowRow != null)
                {
                    var allowDetail = SetFrame(null, new Thickness(0)); allowDetail.Visibility = Visibility.Collapsed;
                    var sl = SettingsList(new[] { allowRow }, allowDetail);
                    var wrap = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                    wrap.Children.Add(new TextBlock { Text = T("For pipelines and MCP configurations:", "Für Pipelines und MCP-Konfigurationen:"), Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) });
                    wrap.Children.Add(sl);
                    DockPanel.SetDock(wrap, Dock.Bottom); panel.Children.Add(wrap);
                }
                panel.Children.Add(lv);

                void Update()
                {
                    var it = lv.SelectedItem as Dictionary<string, object>;
                    sel = it != null && it.TryGetValue("#t", out var t) ? (Dictionary<string, object>)t : null;
                    extend.IsEnabled = edit.IsEnabled = del.IsEnabled = sel != null;
                    if (it == null) { detail.Child = new TextBlock { Text = T("Select a target to see its details.", "Wähle ein Ziel, um seine Details zu sehen."), Opacity = 0.6 }; return; }
                    var sp = new StackPanel();
                    var h = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    h.Inlines.Add(new Run((string)it[cName]) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    h.Inlines.Add(new Run("   " + it[cState]) { Foreground = (Brush)it[cState + "#c"] });
                    sp.Children.Add(h);
                    var facts = new List<(string, string)> { ("AMS Net ID", (string)it[cId]), (T("The agent may", "Der Agent darf"), (string)it[cMay] + ((string)it[cMay] == mayRead ? "" : T(" and read", " und lesen"))) };
                    if (sel != null)
                    {
                        if (DateTime.TryParse(SStr(sel, "added"), out var a)) facts.Add((T("Released", "Freigegeben"), a.ToString("dd.MM.yyyy HH:mm") + (SStr(sel, "by") is { } by && by.Length > 0 ? " – " + (by == "settings window" ? T("settings window", "Einstellungsfenster") : by) : "")));
                        facts.Add((T("Until", "Bis"), (string)it[cUntil]));
                        if (SStr(sel, "access") != "read") facts.Add((T("Afterwards", "Danach"), T("read only - the entry stays as 'expired' until you delete it", "nur noch lesen - der Eintrag bleibt als „abgelaufen“ stehen, bis du ihn löschst")));
                    }
                    else facts.Add((T("Where", "Wo"), T("TCAGENT_ALLOW_TARGETS (default local = this PC) - change it in the setting 'Allowed by configuration' or in the MCP configuration of the client", "TCAGENT_ALLOW_TARGETS (Standard local = dieser PC) - ändern in der Einstellung „Freigabe per Konfiguration“ oder in der MCP-Konfiguration des Clients")));
                    var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                    foreach (var (k, v) in facts)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var ka = new TextBlock { Text = k, Opacity = 0.65, Margin = new Thickness(0, 1, 8, 1) };
                        var vb = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                        Grid.SetRow(ka, grid.RowDefinitions.Count - 1); Grid.SetRow(vb, grid.RowDefinitions.Count - 1); Grid.SetColumn(vb, 1);
                        grid.Children.Add(ka); grid.Children.Add(vb);
                    }
                    sp.Children.Add(grid);
                    detail.Child = sp;
                }
                lv.SelectionChanged += (s, e) => Update();
                lv.MouseDoubleClick += (s, e) => { if (edit.IsEnabled) edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                lv.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Delete && del.IsEnabled) { del.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                    else if (e.Key == Key.Enter && edit.IsEnabled) { edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                };
                // context menu = the toolbar
                var cm = new ContextMenu();
                foreach (var (b, label) in new[] { (add, T("New target …", "Neues Ziel …")), (edit, T("Edit …", "Bearbeiten …")), (del, T("Delete …", "Löschen …")) })
                {
                    var mi = new MenuItem { Header = label }; var bb = b;
                    mi.Click += (s, e) => bb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    cm.Items.Add(mi);
                }
                var ext = new MenuItem { Header = T("Extend", "Verlängern") };
                foreach (var m in ExtendItems()) ext.Items.Add(m);
                cm.Items.Insert(1, ext);
                cm.Opened += (s, e) => { foreach (var o in cm.Items.OfType<MenuItem>().Skip(1)) o.IsEnabled = sel != null; };
                lv.ContextMenu = cm;
                var count = list.Count(t => SStr(t, "state") is "write" or "expires");
                statusRight.Text = fmtGerman ? $"{items.Count} Ziel{(items.Count == 1 ? "" : "e")} · {items.Count(i => i.ContainsKey("#fixed")) + count} dürfen schreiben · {list.Count(t => SStr(t, "state") == "expired")} abgelaufen"
                                             : $"{items.Count} target{(items.Count == 1 ? "" : "s")} · {items.Count(i => i.ContainsKey("#fixed")) + count} may write · {list.Count(t => SStr(t, "state") == "expired")} expired";
                Update();
                if (items.Count > 0) lv.SelectedIndex = Math.Min(items.Count - 1, items.Count(i => i.ContainsKey("#fixed")));
                return panel;
            }

            // ---- target dialog: name, AMS Net ID (checked while typing), right, how long
            void EditTarget(string netId, string name, bool write, string until)
            {
                var d = SetWindow(string.IsNullOrEmpty(netId) || !SList(targets, "targets").Any(t => SStr(t, "netId") == netId) ? T("New target system", "Neues Zielsystem") : T("Edit target system", "Zielsystem bearbeiten"), 620, 330, w);
                var g = new Grid { Margin = new Thickness(14, 12, 14, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                int r = 0;
                void Row(string label, UIElement el) { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) }; Grid.SetRow(l, r); Grid.SetRow(el, r); Grid.SetColumn(el, 1); g.Children.Add(l); g.Children.Add(el); r++; }
                var nameBox = new TextBox { Text = name ?? "", Padding = new Thickness(3, 2, 3, 2), Margin = new Thickness(0, 4, 0, 4) };
                var idBox = new TextBox { Text = netId ?? "", Padding = new Thickness(3, 2, 3, 2), Margin = new Thickness(0, 4, 0, 4), FontFamily = new FontFamily("Consolas"), IsReadOnly = !string.IsNullOrEmpty(netId) && SList(targets, "targets").Any(t => SStr(t, "netId") == netId) };
                var idErr = new TextBlock { Foreground = SetRed, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                var rw = new RadioButton { Content = T("read and write", "lesen und schreiben"), IsChecked = write, Margin = new Thickness(0, 0, 14, 0) };
                var ro = new RadioButton { Content = T("read only", "nur lesen"), IsChecked = !write };
                var mayPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) }; mayPanel.Children.Add(rw); mayPanel.Children.Add(ro);
                var forTime = new RadioButton { Content = T("for", "für"), IsChecked = until != "forever" && until != "date", VerticalAlignment = VerticalAlignment.Center, GroupName = "until" };
                var dur = new ComboBox { Margin = new Thickness(4, 0, 14, 0), MinWidth = 110 };
                foreach (var (k, en, de) in new[] { ("30m", "30 minutes", "30 Minuten"), ("1h", "1 hour", "1 Stunde"), ("4h", "4 hours", "4 Stunden"), ("today", "today", "heute"), ("7d", "7 days", "7 Tage") })
                    dur.Items.Add(new ComboBoxItem { Content = T(en, de), Tag = k });
                dur.SelectedIndex = Math.Max(0, dur.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == until));
                if (dur.SelectedIndex < 0) dur.SelectedIndex = 1;
                var untilDate = new RadioButton { Content = T("until", "bis"), VerticalAlignment = VerticalAlignment.Center, GroupName = "until" };
                var dateBox = new TextBox { Text = DateTime.Now.AddHours(3).ToString("dd.MM.yyyy HH:mm"), Width = 120, Margin = new Thickness(4, 0, 14, 0), Padding = new Thickness(3, 1, 3, 1) };
                var forever = new RadioButton { Content = T("for good", "dauerhaft"), IsChecked = until == "forever", VerticalAlignment = VerticalAlignment.Center, GroupName = "until" };
                var howLong = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
                foreach (var x in new UIElement[] { forTime, dur, untilDate, dateBox, forever }) howLong.Children.Add(x);
                var dateErr = new TextBlock { Foreground = SetRed, FontSize = 11 };
                Row(T("Name", "Name"), nameBox);
                Row("AMS Net ID", idBox);
                Row("", idErr);
                Row(T("The agent may", "Der Agent darf"), mayPanel);
                Row(T("How long", "Wie lange"), howLong);
                Row("", dateErr);
                var note2 = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
                                            Text = T("Expired means: read only again. Only you can release targets - the agent itself cannot.", "Abgelaufen heißt: wieder nur lesen. Nur du kannst Ziele freigeben - der Agent selbst nicht.") };
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(note2, r); Grid.SetColumnSpan(note2, 2); g.Children.Add(note2);
                var ok = SetButton("OK", null, true); ok.IsDefault = true;
                var cancel = SetButton(T("Cancel", "Abbrechen"), null); cancel.IsCancel = true; cancel.Margin = new Thickness(0);
                var bb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 10, 14, 12) };
                bb.Children.Add(ok); bb.Children.Add(cancel);
                var dp = new DockPanel(); DockPanel.SetDock(bb, Dock.Bottom); dp.Children.Add(bb); dp.Children.Add(g);
                d.Content = dp;
                DateTime? date = null;
                void Validate()
                {
                    var id = idBox.Text.Trim();
                    var parts = id.Split('.');
                    bool idOk = parts.Length == 6 && parts.All(p => int.TryParse(p, out var n) && n >= 0 && n <= 255 && p.Length > 0 && p.Length <= 3);
                    idErr.Text = idOk || id.Length == 0 ? "" : T("Six numbers from 0 to 255, e.g. 5.80.123.4.1.1", "Sechs Zahlen von 0 bis 255, z. B. 5.80.123.4.1.1");
                    date = null; dateErr.Text = "";
                    if (untilDate.IsChecked == true)
                    {
                        if (DateTime.TryParse(dateBox.Text, out var dt) && dt > DateTime.Now) date = dt;
                        else dateErr.Text = T("A date and time in the future, e.g. ", "Ein Datum mit Uhrzeit in der Zukunft, z. B. ") + DateTime.Now.AddHours(3).ToString("dd.MM.yyyy HH:mm");
                    }
                    dur.IsEnabled = forTime.IsChecked == true; dateBox.IsEnabled = untilDate.IsChecked == true;
                    var timed = ro.IsChecked != true;
                    foreach (var x in new Control[] { forTime, dur, untilDate, dateBox, forever }) x.IsEnabled = timed && (x != dur || forTime.IsChecked == true) && (x != dateBox || untilDate.IsChecked == true);
                    ok.IsEnabled = idOk && (untilDate.IsChecked != true || date != null || !timed);
                }
                foreach (var tbx in new[] { idBox, dateBox, nameBox }) tbx.TextChanged += (s, e) => Validate();
                foreach (var rb in new[] { rw, ro, forTime, untilDate, forever }) rb.Checked += (s, e) => Validate();
                Validate();
                d.Loaded += (s, e) => { (idBox.IsReadOnly ? nameBox : string.IsNullOrEmpty(idBox.Text) ? idBox : nameBox).Focus(); };
                ok.Click += (s, e) => { d.DialogResult = true; };
                if (d.ShowDialog() != true) return;
                var how = ro.IsChecked == true ? "forever" : forever.IsChecked == true ? "forever" : untilDate.IsChecked == true ? date.Value.ToString("s") : (string)((ComboBoxItem)dur.SelectedItem).Tag;
                SaveTarget(idBox.Text.Trim(), nameBox.Text.Trim(), rw.IsChecked == true, how);
            }

            // ---- take over ADS routes: tick the routes, choose the right and how long (read only is the safe default)
            async void ImportRoutes(List<Dictionary<string, object>> routes)
            {
                var d = SetWindow(T("Take over from the ADS routes", "Aus den ADS-Routen übernehmen"), 620, 420, w);
                var head = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 12, 14, 8),
                                           Text = T("The routes of the TwinCAT router on this PC that are not in the list yet. Tick the ones to take over.",
                                                    "Die Routen des TwinCAT-Routers auf diesem PC, die noch nicht in der Liste stehen. Hake an, welche du übernehmen willst.") };
                var boxes = new List<(CheckBox Box, Dictionary<string, object> Route)>();
                var sp = new StackPanel { Margin = new Thickness(14, 0, 14, 0) };
                foreach (var rt in routes)
                {
                    var cb = new CheckBox { Margin = new Thickness(0, 3, 0, 3) };
                    var tx = new TextBlock();
                    tx.Inlines.Add(new Run(SStr(rt, "name")) { FontWeight = FontWeights.SemiBold });
                    tx.Inlines.Add(new Run("   " + SStr(rt, "netId")) { FontFamily = new FontFamily("Consolas") });
                    var extra = string.Join(" · ", new[] { SStr(rt, "address"), string.IsNullOrEmpty(SStr(rt, "runtime")) ? null : T("route of ", "Route von ") + SStr(rt, "runtime") }.Where(x => !string.IsNullOrEmpty(x)));
                    if (extra.Length > 0) tx.Inlines.Add(new Run("   " + extra) { Foreground = Brushes.Gray });
                    cb.Content = tx;
                    boxes.Add((cb, rt));
                    sp.Children.Add(cb);
                }
                var all = new TextBlock { Margin = new Thickness(14, 4, 14, 0) };
                all.Inlines.Add(Link(T("Select all", "Alle auswählen"), () => boxes.ForEach(b => b.Box.IsChecked = true)));
                all.Inlines.Add(new Run(" · "));
                all.Inlines.Add(Link(T("Select none", "Keine auswählen"), () => boxes.ForEach(b => b.Box.IsChecked = false)));
                var ro = new RadioButton { Content = T("read only", "nur lesen"), IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
                var rw = new RadioButton { Content = T("read and write for", "lesen und schreiben für"), VerticalAlignment = VerticalAlignment.Center };
                var dur = new ComboBox { Margin = new Thickness(4, 0, 0, 0), MinWidth = 110, IsEnabled = false };
                foreach (var (k, en, de) in new[] { ("1h", "1 hour", "1 Stunde"), ("4h", "4 hours", "4 Stunden"), ("today", "today", "heute"), ("forever", "for good", "dauerhaft") })
                    dur.Items.Add(new ComboBoxItem { Content = T(en, de), Tag = k });
                dur.SelectedIndex = 0;
                rw.Checked += (s, e) => dur.IsEnabled = true; ro.Checked += (s, e) => dur.IsEnabled = false;
                var may = new WrapPanel { Margin = new Thickness(14, 10, 14, 0) };
                may.Children.Add(new TextBlock { Text = T("The agent may: ", "Der Agent darf: "), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                foreach (var x in new UIElement[] { ro, rw, dur }) may.Children.Add(x);
                var ok = SetButton(T("Take over", "Übernehmen"), null, true); ok.IsDefault = true; ok.IsEnabled = false;
                var cancel = SetButton(T("Cancel", "Abbrechen"), null); cancel.IsCancel = true; cancel.Margin = new Thickness(0);
                foreach (var b in boxes) { b.Box.Checked += (s, e) => ok.IsEnabled = true; b.Box.Unchecked += (s, e) => ok.IsEnabled = boxes.Any(x => x.Box.IsChecked == true); }
                var bb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 10, 14, 12) };
                bb.Children.Add(ok); bb.Children.Add(cancel);
                var dp = new DockPanel();
                foreach (var x in new UIElement[] { head, all }) { DockPanel.SetDock(x, Dock.Top); }
                DockPanel.SetDock(bb, Dock.Bottom); DockPanel.SetDock(may, Dock.Bottom);
                dp.Children.Add(head); dp.Children.Add(bb); dp.Children.Add(may); dp.Children.Add(all);
                dp.Children.Add(new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
                d.Content = dp;
                ok.Click += (s, e) => d.DialogResult = true;
                if (d.ShowDialog() != true) return;
                var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Route).ToList();
                var write = rw.IsChecked == true;
                var until = write ? (string)((ComboBoxItem)dur.SelectedItem).Tag : "forever";
                busy = true; progress.Visibility = Visibility.Visible;
                try
                {
                    foreach (var rt in chosen)
                    {
                        var a = $"targets add {SStr(rt, "netId")} --name \"{(SStr(rt, "name") ?? "").Replace("\"", "")}\" --access {(write ? "write" : "read")} --until {until} --by \"settings window (ADS route)\"";
                        await Task.Run(() => FmtRun(exe, a, null));
                    }
                    await Load();
                    statusLeft.Text = fmtGerman ? $"{chosen.Count} Route{(chosen.Count == 1 ? "" : "n")} übernommen - {(write ? "lesen und schreiben" : "nur lesen")}"
                                                : $"{chosen.Count} route{(chosen.Count == 1 ? "" : "s")} taken over - {(write ? "read and write" : "read only")}";
                }
                catch (Exception ex) { MessageBox.Show(w, Inner(ex), T("TcAgent settings", "TcAgent-Einstellungen"), MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { busy = false; progress.Visibility = Visibility.Collapsed; Changed(); }
            }

            async void SaveTarget(string netId, string name, bool write, string until)
            {
                var args = $"targets add {netId} --name \"{(name ?? "").Replace("\"", "")}\" --access {(write ? "write" : "read")} --until {until} --by \"settings window\"";
                await RunCli(args, T("Released: ", "Freigegeben: ") + (string.IsNullOrEmpty(name) ? netId : name) + " – " + T("takes effect at once", "wirkt sofort"));
            }

            async void DeleteTarget(string netId, string name)
            {
                var label = string.IsNullOrEmpty(name) ? netId : $"{name} ({netId})";
                if (MessageBox.Show(w, fmtGerman ? $"{label} aus der Liste löschen?\n\nDer Agent darf das Ziel danach nur noch lesen." : $"Delete {label} from the list?\n\nThe agent may then only read this target.",
                                    T("Delete target system", "Zielsystem löschen"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await RunCli($"targets remove {netId}", T("Deleted: ", "Gelöscht: ") + label);
            }

            /// <summary>A change through TcAgent.exe, then everything read again.</summary>
            async Task RunCli(string args, string done)
            {
                busy = true; progress.Visibility = Visibility.Visible;
                try
                {
                    await Task.Run(() => FmtRun(exe, args, null));
                    await Load();
                    statusLeft.Text = done;
                }
                catch (Exception ex) { MessageBox.Show(w, Inner(ex), T("TcAgent settings", "TcAgent-Einstellungen"), MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { busy = false; progress.Visibility = Visibility.Collapsed; Changed(); }
            }

            void Export()
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "tcagent-settings.json", Filter = "JSON|*.json" };
                if (dlg.ShowDialog(w) != true) return;
                _ = RunCli($"settings --export \"{dlg.FileName}\"", T("Exported to ", "Exportiert nach ") + dlg.FileName);
            }

            void Import()
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
                if (dlg.ShowDialog(w) != true) return;
                if (MessageBox.Show(w, T("Take over the settings of this file? Values it does not name stay as they are.", "Die Einstellungen dieser Datei übernehmen? Werte, die sie nicht nennt, bleiben, wie sie sind."),
                                    T("Import settings", "Einstellungen importieren"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _ = RunCli($"settings --import \"{dlg.FileName}\"", T("Imported and read again: ", "Importiert und neu geladen: ") + Path.GetFileName(dlg.FileName));
            }

            // ---- read schema, values and targets
            async Task Load()
            {
                var sTask = Task.Run(() => FmtJson(FmtRun(exe, "settings --json", null)));
                var tTask = Task.Run(() => FmtJson(FmtRun(exe, "targets --json", null)));
                schema = await sTask; targets = await tTask;
                rows.Clear();
                foreach (var d in SList(schema, "settings"))
                    rows.Add(new SettingRow { Def = d, Key = SStr(d, "key"), Original = SStr(d, "saved") ?? "", Value = SStr(d, "saved") ?? "" });
                // the pages, with a badge on target systems for a user mode runtime that is not released
                var keep = current;
                pages.Items.Clear();
                var umOpen = SList(targets, "userModeRuntimes").Count(u => !SList(targets, "targets").Any(t => SStr(t, "netId") == SStr(u, "netId")));
                foreach (var p in SList(schema, "pages"))
                {
                    var id = SStr(p, "id");
                    var dp = new DockPanel { Margin = new Thickness(6, 3, 6, 3) };
                    if (id == "targets" && umOpen > 0)
                    {
                        var badge = new Border { CornerRadius = new CornerRadius(8), Background = SetOrange, Padding = new Thickness(6, 0, 6, 0), ToolTip = T("A user mode runtime on this PC is not released", "Eine Usermode-Runtime auf diesem PC ist nicht freigegeben"),
                                                 Child = new TextBlock { Text = umOpen.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 11 } };
                        DockPanel.SetDock(badge, Dock.Right); dp.Children.Add(badge);
                    }
                    dp.Children.Add(new TextBlock { Text = fmtGerman ? SStr(p, "de") : SStr(p, "en") });
                    pages.Items.Add(new ListBoxItem { Content = dp, Tag = id, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                pages.Items.Add(new ListBoxItem { Content = new TextBlock { Text = T("Formatter ›", "Formatierer ›"), Margin = new Thickness(6, 3, 6, 3) }, Tag = "formatter", ToolTip = T("Opens the formatter settings", "Öffnet die Formatierer-Einstellungen") });
                current = keep;
                pages.SelectedItem = pages.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? pages.Items[0];
                var outside = rows.Count(r => SStr(r.Def, "from") == "outside");
                if (current != "targets")
                    statusRight.Text = fmtGerman ? $"{rows.Count} Einstellungen · {outside} von außen gesetzt" : $"{rows.Count} settings · {outside} set from outside";
                Show();
            }

            pages.SelectionChanged += (s, e) =>
            {
                if (!(pages.SelectedItem is ListBoxItem it)) return;
                var id = (string)it.Tag;
                if (id == "formatter")
                {
                    pages.SelectedItem = pages.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
                    try { ShowFormatSettings(); } catch (Exception ex) { Log("Formatter settings: " + Inner(ex)); }
                    return;
                }
                if (id == current && content.Content != null) return;
                current = id;
                if (search.Text.Length > 0) search.Text = "";
                if (current != "targets") statusRight.Text = fmtGerman ? $"{rows.Count} Einstellungen · {rows.Count(r => SStr(r.Def, "from") == "outside")} von außen gesetzt" : $"{rows.Count} settings · {rows.Count(r => SStr(r.Def, "from") == "outside")} set from outside";
                Show();
            };
            search.TextChanged += (s, e) => Show();
            search.KeyDown += (s, e) => { if (e.Key == Key.Escape && search.Text.Length > 0) { search.Text = ""; e.Handled = true; } };

            reload.Click += async (s, e) =>
            {
                if (busy) return;
                if (rows.Any(r => r.Changed) && MessageBox.Show(w, T("Reload discards the changes that are not saved. Go on?", "Neu laden verwirft die nicht gespeicherten Änderungen. Weiter?"), T("TcAgent settings", "TcAgent-Einstellungen"),
                                                                 MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                busy = true; reload.IsEnabled = false; progress.Visibility = Visibility.Visible;
                statusLeft.Text = T("Reading the settings again …", "Lese die Einstellungen neu …");
                try
                {
                    ChatControl.Instance?.ReloadChatSettings();
                    var lines = await ChatControl.ReloadServers();
                    await Load();
                    statusLeft.Text = lines.Count == 0
                        ? T("Read again. No TcAgent server is running - the next question starts one with these settings.", "Neu geladen. Kein TcAgent-Server läuft gerade - die nächste Frage startet einen mit diesen Einstellungen.")
                        : T("Read again - ", "Neu geladen - ") + string.Join(" · ", lines);
                    statusLeft.ToolTip = string.Join("\n", lines);
                }
                catch (Exception ex) { MessageBox.Show(w, Inner(ex), T("TcAgent settings", "TcAgent-Einstellungen"), MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { busy = false; reload.IsEnabled = true; progress.Visibility = Visibility.Collapsed; Changed(); }
            };
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.F5) { reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
            undo.Click += (s, e) => { foreach (var r in rows) r.Value = r.Original; Show(); Changed(); statusLeft.Text = T("Changes discarded", "Änderungen verworfen"); };
            save.Click += async (s, e) =>
            {
                var changed = rows.Where(r => r.Changed).ToList();
                if (changed.Count == 0) return;
                // the safety settings once more in plain words
                var risky = changed.Where(r => r.Key == "TCAGENT_CONFIRM" && r.Value == "off" || r.Key == "TCAGENT_ACTIVATE_GATE" && r.Value == "0" || r.Key == "TCAGENT_ALLOW_TARGETS" && r.Value.Length > 0).ToList();
                if (risky.Count > 0 && MessageBox.Show(w, T("You change safety settings:", "Du änderst Sicherheits-Einstellungen:") + "\n\n" + string.Join("\n", risky.Select(r => "• " + Label(r.Def) + ": " + ValueWord(r.Def, r.Value))) +
                                                         "\n\n" + T("The agent then acts with fewer questions. Save?", "Der Agent handelt dann mit weniger Rückfragen. Speichern?"),
                                                         T("TcAgent settings", "TcAgent-Einstellungen"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var args = "settings " + string.Join(" ", changed.Select(r => $"--set \"{r.Key}={r.Value}\""));
                var outsideWins = changed.Where(r => SStr(r.Def, "from") == "outside").ToList();
                await RunCli(args, (fmtGerman ? $"Gespeichert - {changed.Count} Einstellung{(changed.Count == 1 ? "" : "en")}, die laufenden TcAgent-Server lesen neu"
                                              : $"Saved - {changed.Count} setting{(changed.Count == 1 ? "" : "s")}, the running TcAgent servers read them again") +
                                   (outsideWins.Count > 0 ? T(" (set from outside still wins: ", " (von außen gesetzt gewinnt weiter: ") + string.Join(", ", outsideWins.Select(r => Label(r.Def))) + ")" : "") +
                                   (changed.Any(r => SStr(r.Def, "restart") == "True") ? T(" - some need a restart of the AI client", " - manche brauchen einen Neustart des KI-Clients") : ""));
            };
            w.Closing += (s, e) =>
            {
                if (rows.Any(r => r.Changed) && MessageBox.Show(w, T("Close without saving the changes?", "Ohne Speichern der Änderungen schließen?"), T("TcAgent settings", "TcAgent-Einstellungen"),
                                                                 MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
            };
            w.Loaded += async (s, e) =>
            {
                try { await Load(); statusLeft.Text = T("Ready", "Bereit"); }
                catch (Exception ex)
                {
                    FormatMessage(T("TcAgent settings: the settings could not be read.", "TcAgent-Einstellungen: Die Einstellungen konnten nicht gelesen werden.") + "\n\n" + Inner(ex) + "\n\n" +
                                  T("TcAgent 0.54 or newer is needed.", "TcAgent 0.54 oder neuer wird gebraucht."), true);
                    w.Close(); return;
                }
                finally { progress.Visibility = Visibility.Collapsed; }
            };
            w.ShowDialog();
        }
    }
}
