// TcAgentBridge - the condition window: the condition at the cursor with its rewrites (De Morgan, comparisons turned,
// simplified, written out, = TRUE uniform), its truth table and its sub-conditions. Built after the approved mockup of the
// large condition window; the logic diagram itself stays in the Inline-Logic-View (Ctrl+Alt+B), whose ⤢ opens this window.
//
//   Rewrite:      the rewrites as a list, before | after in the look of the editor, the proof (every combination of the
//                 sub-conditions gives the same result, and the code for the editor is read again and checked); Apply
//                 replaces the condition in the editor (one step, Ctrl+Z takes it back). Never offered when it would change
//                 the meaning, never with calls (simplify) or AND_THEN / OR_ELSE.
//   Truth table:  when it is TRUE (one row per group, "–" = any) or every combination; the combination of now is marked
//                 when the values came from the Inline-Logic-View online.
//   Sub-conditions: each one with where its variables come from and its value now.
//
//   TcAgent.exe condition --stdin --layout --json --solution … --pou … [--method …] [--instance …]
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    /// <summary>Where a condition stands in the editor: the text of the editor, the span of the statement head (or the
    /// boolean assignment, or the selection) and the indent of its next lines.</summary>
    sealed class ConditionSite
    {
        public System.Windows.Forms.Control Syn;
        public string Text, Pou, Method;
        public int Start, Length, Line;
        /// <summary>false: a selection that was not found in the text again - shown, never written back.</summary>
        public bool Found = true;
        public string Original => Text == null ? null : Text.Substring(Start, Length);

        /// <summary>What goes in front of the next lines of the condition: the leading white space of its first line when the
        /// condition starts the line, otherwise as many spaces as its column.</summary>
        public string Prefix
        {
            get
            {
                if (Text == null) return "";
                int ls = Text.LastIndexOf('\n', Math.Max(0, Start - 1)) + 1;
                if (Start == 0) ls = 0;
                var lead = Text.Substring(ls, Start - ls);
                return lead.All(c => c == ' ' || c == '\t') ? lead : new string(' ', lead.Length);
            }
        }
    }

    public sealed partial class BridgePackage
    {
        const int CmdCondition = 0x0106, CmdConditionWindow = 0x0107;
        static Window condWindow;

        static readonly Dictionary<string, (string En, string De)> RewriteHow = new Dictionary<string, (string, string)>
        {
            ["not-inward"] = ("NOT (a OR b) → NOT a AND NOT b", "NOT (a OR b) → NOT a AND NOT b"),
            ["not-inward-turn"] = ("NOT (a OR b < 5) → NOT a AND b >= 5", "NOT (a OR b < 5) → NOT a AND b >= 5"),
            ["turn"] = ("NOT (b < 5) → b >= 5, the rest stays", "NOT (b < 5) → b >= 5, der Rest bleibt"),
            ["not-outward"] = ("NOT a AND NOT b → NOT (a OR b)", "NOT a AND NOT b → NOT (a OR b)"),
            ["simplify"] = ("the shortest form with the same result: OR of AND groups", "die kürzeste Form mit demselben Ergebnis: ODER von UND-Gruppen"),
            ["write-out"] = ("every combination that is TRUE as its own AND group", "jede Kombination, die TRUE ist, als eigene UND-Gruppe"),
            ["bool-uniform"] = ("x → x = TRUE, NOT x → x = FALSE, like the comparisons", "x → x = TRUE, NOT x → x = FALSE, wie die Vergleiche"),
            ["bool-remove"] = ("x = TRUE → x, x = FALSE → NOT x", "x = TRUE → x, x = FALSE → NOT x")
        };

        static string Js(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        static bool? Jb(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is bool b ? b : (bool?)null;
        static List<Dictionary<string, object>> Jl(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is ArrayList a ? a.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();

        /// <summary>The condition window for the condition at the cursor (Tools menu).</summary>
        internal void ShowConditionWindowFromEditor()
        {
            fmtGerman = UiGerman();
            var site = ConditionSiteFromEditor(out var why);
            if (site == null) { SetStatus(why); return; }
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { FormatMessage(T("TcAgent.exe was not found - set it up in the TcAgent Chat first.", "TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
            ShowConditionWindow(site, exe, TcUnitControl.SolutionPath(), null, null);
        }

        /// <summary>The condition window. instance: the instance of the POU to show where variables come from; now: the values of
        /// the sub-conditions (from the Inline-Logic-View online) to mark the combination of now, or null.</summary>
        internal void ShowConditionWindow(ConditionSite site, string exe, string sln, string instance, List<bool?> now)
        {
            fmtGerman = UiGerman();
            try { condWindow?.Close(); } catch { }
            var look = ReadEditorLook(site?.Syn);
            Dictionary<string, object> data = null;

            var w = new Window
            {
                Title = T("Condition (TcAgent)", "Bedingung (TcAgent)"), Icon = AppIcon, Width = 1280, Height = 720, MinWidth = 760, MinHeight = 440,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            condWindow = w;
            w.Closed += (s, e) => { if (condWindow == w) condWindow = null; };
            try { w.Owner = Application.Current?.MainWindow; } catch { }
            if (Application.Current != null) ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(Control.BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(Control.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            var green = new SolidColorBrush(Color.FromRgb(0x2E, 0x9D, 0x4A)); green.Freeze();
            var orange = new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00)); orange.Freeze();
            var red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); red.Freeze();
            var greenTint = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0x9D, 0x4A)); greenTint.Freeze();
            var nowTint = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x6F, 0xD8)); nowTint.Freeze();

            // ---- heading
            var head = new TextBlock { Margin = new Thickness(12, 10, 12, 6), TextWrapping = TextWrapping.Wrap };
            void Heading()
            {
                head.Inlines.Clear();
                head.Inlines.Add(new Run(T("Condition", "Bedingung")) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                var kw = Js(data, "keyword");
                var what = string.IsNullOrEmpty(kw) ? T("selection", "Auswahl") : kw.EndsWith(":=") ? T("assignment ", "Zuweisung ") + kw.TrimEnd(':', '=', ' ') : kw;
                var where = site == null ? "" : $" {T("in", "in")} {site.Pou}{(site.Method != null ? "." + site.Method : "")}, {T("line", "Zeile")} {site.Line}";
                int n = Jl(data, "atoms").Count;
                head.Inlines.Add(new Run($" – {what}{where}" + (data == null ? "" : $" · {n} {T("sub-conditions", "Teilbedingungen")}")) { Foreground = Brushes.Gray });
            }
            Heading();

            // ---- status line
            var statusLeft = new TextBlock { Text = T("Reading the condition …", "Lese die Bedingung …"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var statusRight = new TextBlock { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            var progress = new ProgressBar { Height = 2, IsIndeterminate = true, BorderThickness = new Thickness(0) };
            var status = new DockPanel();
            var statusInner = new DockPanel { Margin = new Thickness(12, 3, 12, 3) };
            DockPanel.SetDock(statusRight, Dock.Right); statusInner.Children.Add(statusRight); statusInner.Children.Add(statusLeft);
            DockPanel.SetDock(progress, Dock.Top); status.Children.Add(progress); status.Children.Add(statusInner);
            status.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.StatusBarDefaultBrushKey);
            statusLeft.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);
            statusRight.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);

            // ---- buttons
            var apply = new Button { Content = T("Apply", "Übernehmen"), Padding = new Thickness(18, 3, 18, 3), IsEnabled = false, ToolTip = T("Replaces the condition in the editor with the rewrite - Ctrl+Z takes it back", "Ersetzt die Bedingung im Editor durch die Umformung - Strg+Z nimmt es zurück") };
            apply.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.SystemHighlightBrushKey);
            apply.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemHighlightTextBrushKey);
            var close = new Button { Content = T("Close", "Schließen"), Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            close.Click += (s, e) => w.Close();
            var barHint = new TextBlock { Text = T("Apply replaces the condition in the editor with the rewrite (one step, Ctrl+Z takes it back).", "Übernehmen ersetzt die Bedingung im Editor durch die Umformung (ein Schritt, Strg+Z nimmt es zurück)."), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
            DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
            DockPanel.SetDock(apply, Dock.Right); bar.Children.Add(apply);
            bar.Children.Add(barHint);

            // ---- tab 1: rewrite
            var list = new ListBox { BorderThickness = new Thickness(1), MinWidth = 260 };
            list.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
            TcUnitControl.ThemedSelection(itemStyle, false);
            list.ItemContainerStyle = itemStyle;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var compare = new ContentControl();
            var proof = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var rewriteRight = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(proof, Dock.Bottom); rewriteRight.Children.Add(proof); rewriteRight.Children.Add(compare);
            var rewriteGrid = new Grid { Margin = new Thickness(12, 8, 12, 4) };
            rewriteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            rewriteGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(rewriteRight, 1);
            rewriteGrid.Children.Add(list); rewriteGrid.Children.Add(rewriteRight);

            // ---- tab 2: truth table
            var tableHost = new ContentControl();
            var shortForm = new RadioButton { Content = T("When it is TRUE", "Wann ist sie TRUE"), IsChecked = true, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var fullForm = new RadioButton { Content = T("Every combination", "Jede Kombination"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var copyTable = new Button { Content = T("Copy table", "Tabelle kopieren"), Padding = new Thickness(10, 2, 10, 2), ToolTip = T("The table as text with tabs (for Excel or a test plan)", "Die Tabelle als Text mit Tabulatoren (für Excel oder einen Testplan)") };
            var tableNote = new TextBlock { Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var tableBar = new DockPanel { Margin = new Thickness(12, 8, 12, 6), LastChildFill = true };
            foreach (var x in new UIElement[] { shortForm, fullForm, copyTable }) { DockPanel.SetDock(x, Dock.Left); tableBar.Children.Add(x); }
            tableBar.Children.Add(tableNote);
            var tablePage = new DockPanel();
            DockPanel.SetDock(tableBar, Dock.Top); tablePage.Children.Add(tableBar);
            tableHost.Margin = new Thickness(12, 0, 12, 4);
            tablePage.Children.Add(tableHost);

            // ---- tab 3: sub-conditions
            var subsHost = new ContentControl { Margin = new Thickness(12, 8, 12, 4) };

            var tabs = new TabControl { Margin = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
            TabItem Tab(string h, UIElement c) { var t = new TabItem { Header = h, Content = c, Padding = new Thickness(10, 3, 10, 3) }; tabs.Items.Add(t); return t; }
            var tRewrite = Tab(T("Rewrite", "Umformen"), rewriteGrid);
            Tab(T("Truth table", "Wahrheitstabelle"), tablePage);
            Tab(T("Sub-conditions", "Teilbedingungen"), subsHost);

            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            root.Children.Add(tabs);
            w.Content = root;
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };

            // ---- filling
            List<string> AtomNames() => Jl(data, "atoms").Select(a => Js(a, "text")).ToList();
            int? NowIndex()
            {
                var n = AtomNames().Count;
                if (now == null || now.Count < n || n == 0 || n > 20) return null;
                int v = 0;
                for (int i = 0; i < n; i++) { if (now[i] == null) return null; if (now[i] == true) v |= 1 << i; }
                return v;
            }
            bool editorReadOnly() => site?.Syn != null && SafeGet(site.Syn, "ReadOnly") is bool ro && ro;

            void ShowRewrite()
            {
                proof.Children.Clear();
                var rw = (list.SelectedItem as ListBoxItem)?.Tag as Dictionary<string, object>;
                if (rw == null) { compare.Content = null; apply.IsEnabled = false; return; }
                var before = data.TryGetValue("sourceTokens", out var st) ? st as ArrayList : null;
                var after = rw.TryGetValue("codeTokens", out var ct) ? ct as ArrayList : null;
                var bl = before != null ? LinesOf(before) : new List<string>();
                var al = after != null ? LinesOf(after) : new List<string>();
                compare.Content = Arrange(new List<(string, FrameworkElement, bool)>
                {
                    (T("Before", "Vorher"), CodeView(look, before ?? new ArrayList(), Changed(al, bl)), false),
                    (T("After", "Nachher"), CodeView(look, after ?? new ArrayList(), Changed(bl, al)), true)
                }, false);
                bool proven = Jb(rw, "proven") == true, checkedCode = Jb(rw, "codeChecked") == true;
                var combos = Js(rw, "combinations");
                var chip = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                var chipText = new TextBlock();
                chip.Child = chipText;
                var line = new DockPanel();
                var explain = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                DockPanel.SetDock(chip, Dock.Left); line.Children.Add(chip); line.Children.Add(explain);
                if (proven && checkedCode)
                {
                    chip.BorderBrush = green; chipText.Foreground = green; chipText.Text = "✓ " + T("the same meaning", "gleichwertig");
                    explain.Text = T($"all {combos} combinations of the {AtomNames().Count} sub-conditions checked - the result is always the same; the code for the editor was read again and checked too.",
                                     $"alle {combos} Kombinationen der {AtomNames().Count} Teilbedingungen geprüft - das Ergebnis ist immer gleich; der Code für den Editor wurde noch einmal gelesen und geprüft.");
                }
                else if (!proven)
                {
                    chip.BorderBrush = orange; chipText.Foreground = orange; chipText.Text = "⚠ " + T("not proven", "nicht bewiesen");
                    explain.Text = T("too many sub-conditions to check every combination - it is shown, but not applied.", "zu viele Teilbedingungen, um jede Kombination zu prüfen - sie wird gezeigt, aber nicht übernommen.");
                }
                else
                {
                    chip.BorderBrush = red; chipText.Foreground = red; chipText.Text = "✗ " + T("not applied", "nicht übernehmbar");
                    explain.Text = T("the code for the editor could not be checked against the condition.", "der Code für den Editor ließ sich nicht gegen die Bedingung prüfen.");
                }
                proof.Children.Add(line);
                var note = fmtGerman ? Js(rw, "noteDe") ?? Js(rw, "note") : Js(rw, "note");
                if (note != null) proof.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) });
                if (Jb(data, "hasComments") == true)
                    proof.Children.Add(new TextBlock { Text = "⚠ " + T("The condition holds comments - they are not in the rewrite and go when you apply it.", "Die Bedingung enthält Kommentare - sie stehen nicht in der Umformung und gehen beim Übernehmen verloren."), Foreground = orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                proof.Children.Add(new TextBlock { Text = T("Laid out with the formatter rules of the project. Calls and AND_THEN / OR_ELSE are never rearranged.", "Gesetzt mit den Formatierer-Regeln des Projekts. Aufrufe und AND_THEN / OR_ELSE werden nie umgestellt."), TextWrapping = TextWrapping.Wrap, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) });
                string off = site?.Syn == null ? T("no editor to write into (opened outside the editor)", "kein Editor zum Schreiben (außerhalb des Editors geöffnet)")
                           : !site.Found ? T("the selection was not found in the editor again - put the cursor into the condition instead of selecting it", "die Auswahl wurde im Editor nicht wiedergefunden - setze den Cursor in die Bedingung, statt sie zu markieren")
                           : editorReadOnly() ? T("the editor is read only - logged in online? Log out, then apply.", "der Editor ist schreibgeschützt - online eingeloggt? Ausloggen, dann übernehmen.")
                           : !(proven && checkedCode) ? T("only a proven rewrite can be applied", "nur eine bewiesene Umformung kann übernommen werden") : null;
                apply.IsEnabled = off == null && tabs.SelectedItem == tRewrite;
                apply.ToolTip = off ?? T("Replaces the condition in the editor with the rewrite - Ctrl+Z takes it back", "Ersetzt die Bedingung im Editor durch die Umformung - Strg+Z nimmt es zurück");
            }
            list.SelectionChanged += (s, e) => ShowRewrite();
            tabs.SelectionChanged += (s, e) => { if (e.Source == tabs) ShowRewrite(); };

            void FillRewrites()
            {
                list.Items.Clear();
                foreach (var rw in Jl(data, "rewrites"))
                {
                    var id = Js(rw, "id") ?? "";
                    var sp = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
                    sp.Children.Add(new TextBlock { Text = fmtGerman ? Js(rw, "titleDe") ?? Js(rw, "title") : Js(rw, "title"), FontWeight = FontWeights.SemiBold });
                    if (RewriteHow.TryGetValue(id, out var how)) sp.Children.Add(new TextBlock { Text = fmtGerman ? how.De : how.En, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
                    list.Items.Add(new ListBoxItem { Content = sp, Tag = rw });
                }
                if (list.Items.Count == 0)
                {
                    var why = new List<string>();
                    if (Js(data, "rewriteNote") is string rn) why.Add(fmtGerman ? "AND_THEN / OR_ELSE legen die Reihenfolge der Auswertung fest - die Bedingung wird nicht umgestellt." : rn);
                    if (Js(data, "simplifyNote") is string sn) why.Add(fmtGerman ? "Die Bedingung ruft eine Funktion oder Methode auf - Vereinfachen könnte den Aufruf weglassen oder doppeln, deshalb wird es nicht angeboten." : sn);
                    compare.Content = new TextBlock { Text = T("No rewrite for this condition - it is already as short as it gets, and there is no NOT to move.", "Keine Umformung für diese Bedingung - sie ist schon so kurz wie möglich, und es gibt kein NOT zum Verschieben.") + (why.Count > 0 ? "\n\n" + string.Join("\n", why) : ""), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(4) };
                }
                else list.SelectedIndex = 0;
            }

            void FillTable()
            {
                var names = AtomNames();
                int n = names.Count;
                var results = Js(data, "results");
                var rows = data.TryGetValue("trueRows", out var tr) && tr is ArrayList ta ? ta.OfType<string>().ToList() : null;
                fullForm.IsEnabled = results != null;
                var g = new Grid();
                for (int c = 0; c <= n + 1; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tsv = new StringBuilder();
                int r = 0;
                void Cell(int row, int col, string text, Brush fore = null, bool bold = false, Brush back = null, string tip = null)
                {
                    var tb = new TextBlock { Text = text, Padding = new Thickness(8, 3, 8, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = tip, TextAlignment = col > 0 && col <= n ? TextAlignment.Center : TextAlignment.Left };
                    if (fore != null) tb.Foreground = fore;
                    var b = new Border { Child = tb, Background = back ?? Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1) };
                    b.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                    Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
                }
                void Row() { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
                // header: the sub-conditions by number, the name in the tooltip and in a legend line
                Row();
                Cell(0, 0, "", null, true);
                for (int i = 0; i < n; i++) Cell(0, i + 1, (i + 1).ToString(), null, true, null, names[i]);
                Cell(0, n + 1, T("Result", "Ergebnis"), null, true);
                tsv.AppendLine(string.Join("\t", new[] { "" }.Concat(names).Concat(new[] { T("Result", "Ergebnis") })));
                r = 1;
                var nowV = NowIndex();
                int nowRow = -1;
                string Bit(int v, int i) => (v >> i & 1) == 1 ? "1" : "0";
                if (fullForm.IsChecked == true && results != null)
                {
                    // like a printed truth table: the first sub-condition changes slowest, the last one fastest
                    for (int k = 0; k < results.Length; k++)
                    {
                        int v = 0;
                        for (int i = 0; i < n; i++) if ((k >> (n - 1 - i) & 1) == 1) v |= 1 << i;
                        Row();
                        bool res = results[v] == '1', isNow = nowV == v;
                        var back = isNow ? nowTint : res ? greenTint : null;
                        Cell(r, 0, isNow ? "● " + (k + 1) + " " + T("now", "jetzt") : (k + 1).ToString(), null, isNow, back);
                        if (isNow) nowRow = r;
                        for (int i = 0; i < n; i++) Cell(r, i + 1, Bit(v, i), null, false, back);
                        Cell(r, n + 1, res ? "TRUE" : "FALSE", res ? green : null, res, back);
                        tsv.AppendLine(string.Join("\t", new[] { (k + 1).ToString() }.Concat(Enumerable.Range(0, n).Select(i => Bit(v, i))).Concat(new[] { res ? "TRUE" : "FALSE" })));
                        r++;
                    }
                    tableNote.Text = T($"{results.Length} combinations, {results.Count(c => c == '1')} of them TRUE. 1 = the sub-condition is TRUE.", $"{results.Length} Kombinationen, davon {results.Count(c => c == '1')} TRUE. 1 = die Teilbedingung ist TRUE.");
                }
                else if (rows != null)
                {
                    bool Covers(string p, int v) { for (int i = 0; i < p.Length; i++) if (p[i] != '-' && (p[i] == '1') != ((v >> i & 1) == 1)) return false; return true; }
                    int k = 1;
                    foreach (var p in rows)
                    {
                        Row();
                        bool isNow = nowV != null && Covers(p, nowV.Value);
                        var back = isNow ? nowTint : greenTint;
                        Cell(r, 0, (isNow ? "● " : "") + T("group ", "Gruppe ") + k + (isNow ? " " + T("now", "jetzt") : ""), null, isNow, back);
                        for (int i = 0; i < n && i < p.Length; i++) Cell(r, i + 1, p[i] == '-' ? "–" : p[i].ToString(), p[i] == '-' ? Brushes.Gray : null, false, back);
                        Cell(r, n + 1, "TRUE", green, true, back);
                        tsv.AppendLine(string.Join("\t", new[] { T("group ", "Gruppe ") + k }.Concat(p.Select(c => c == '-' ? "-" : c.ToString())).Concat(new[] { "TRUE" })));
                        r++; k++;
                    }
                    Row();
                    bool otherNow = nowV != null && !rows.Any(p => Covers(p, nowV.Value));
                    Cell(r, 0, otherNow ? "● " + T("now", "jetzt") : "", null, otherNow, otherNow ? nowTint : null);
                    var other = new TextBlock { Text = rows.Count == 0 ? T("every combination", "jede Kombination") : T("every other combination", "jede andere Kombination"), Padding = new Thickness(8, 3, 8, 3), Opacity = 0.7 };
                    var ob = new Border { Child = other, Background = otherNow ? nowTint : Brushes.Transparent };
                    Grid.SetRow(ob, r); Grid.SetColumn(ob, 1); Grid.SetColumnSpan(ob, Math.Max(1, n)); g.Children.Add(ob);
                    Cell(r, n + 1, "FALSE", null, true, otherNow ? nowTint : null);
                    tsv.AppendLine(string.Join("\t", new[] { T("every other", "jede andere") }.Concat(Enumerable.Repeat("", n)).Concat(new[] { "FALSE" })));
                    tableNote.Text = T("One row per group that makes it TRUE; – = any value. ", "Eine Zeile je Gruppe, die sie TRUE macht; – = beliebig. ") + (results == null ? T("Every combination: at most 10 sub-conditions.", "Jede Kombination: höchstens 10 Teilbedingungen.") : "");
                }
                else tableNote.Text = T("Too many sub-conditions for a truth table (at most 12).", "Zu viele Teilbedingungen für eine Wahrheitstabelle (höchstens 12).");
                // the legend: number = sub-condition
                var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                for (int i = 0; i < n; i++)
                {
                    var tb = new TextBlock { Margin = new Thickness(0, 0, 16, 2) };
                    tb.Inlines.Add(new Run((i + 1) + "  ") { FontWeight = FontWeights.SemiBold });
                    tb.Inlines.Add(new Run(names[i]) { FontFamily = look.Font });
                    if (now != null && i < now.Count && now[i] != null) tb.Inlines.Add(new Run("  = " + (now[i] == true ? "TRUE" : "FALSE")) { Foreground = now[i] == true ? green : Brushes.Gray });
                    legend.Children.Add(tb);
                }
                var page = new DockPanel();
                DockPanel.SetDock(legend, Dock.Top); page.Children.Add(legend);
                page.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                tableHost.Content = page;
                // the row of now in view
                if (nowRow > 0)
                {
                    var target = g.Children.OfType<FrameworkElement>().FirstOrDefault(c => Grid.GetRow(c) == nowRow);
                    if (target != null) target.Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                copyTable.Tag = tsv.ToString();
            }
            shortForm.Checked += (s, e) => { if (data != null) FillTable(); };
            fullForm.Checked += (s, e) => { if (data != null) FillTable(); };
            copyTable.Click += (s, e) => { try { Clipboard.SetText(copyTable.Tag as string ?? ""); statusLeft.Text = T("Table copied.", "Tabelle kopiert."); } catch (Exception ex) { statusLeft.Text = ex.Message; } };

            void FillSubs()
            {
                var g = new Grid();
                foreach (var wd in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = wd });
                int r = 0;
                void Cell(int col, UIElement c) { Grid.SetRow(c, r); Grid.SetColumn(c, col); g.Children.Add(c); }
                TextBlock Tx(string s, bool bold = false, Brush fore = null, FontFamily font = null) { var t = new TextBlock { Text = s, Padding = new Thickness(8, 3, 8, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap }; if (fore != null) t.Foreground = fore; if (font != null) t.FontFamily = font; return t; }
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(0, Tx("#", true)); Cell(1, Tx(T("Sub-condition", "Teilbedingung"), true)); Cell(2, Tx(T("Now", "Jetzt"), true)); Cell(3, Tx(T("Where its variables come from", "Woher ihre Variablen kommen"), true));
                var atoms = Jl(data, "atoms");
                for (int i = 0; i < atoms.Count; i++)
                {
                    r++; g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Cell(0, Tx((i + 1).ToString(), true));
                    Cell(1, Tx(Js(atoms[i], "text"), false, null, look.Font));
                    var v = now != null && i < now.Count ? now[i] : null;
                    Cell(2, Tx(v == true ? "TRUE" : v == false ? "FALSE" : "–", v == true, v == true ? green : Brushes.Gray));
                    var ops = Jl(atoms[i], "operands");
                    var from = ops.Count == 0 ? (Jb(atoms[i], "call") == true ? T("a call - never called by TcAgent", "ein Aufruf - TcAgent ruft ihn nie auf") : "")
                             : string.Join("\n", ops.Select(o => Js(o, "text") + ": " + (Jb(o, "constant") == true ? T("constant ", "Konstante ") + Js(o, "from")
                                                                    : Js(o, "path") != null ? (fmtGerman ? Js(o, "fromDe") ?? Js(o, "from") : Js(o, "from")) + "  (" + Js(o, "path") + ")"
                                                                    : "⚠ " + (fmtGerman ? Js(o, "reasonDe") ?? Js(o, "reason") : Js(o, "reason")))));
                    Cell(3, Tx(from, false, Brushes.Gray));
                }
                subsHost.Content = new ScrollViewer { Content = g, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }

            async void Load()
            {
                progress.Visibility = Visibility.Visible;
                statusLeft.Text = T("Reading the condition …", "Lese die Bedingung …");
                var input = site?.Original ?? "";
                var dir = string.IsNullOrEmpty(sln) ? null : Path.GetDirectoryName(sln);
                string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
                var args = "condition --stdin --layout --json" + (dir == null ? "" : " --solution " + Q(dir))
                         + (string.IsNullOrEmpty(site?.Pou) ? "" : " --pou " + Q(site.Pou)) + (string.IsNullOrEmpty(site?.Method) ? "" : " --method " + Q(site.Method))
                         + (string.IsNullOrEmpty(instance) ? "" : " --instance " + Q(instance));
                try
                {
                    var outText = await Task.Run(() => FmtRun(exe, args, input));
                    data = FmtJson(outText);
                    Heading();
                    FillRewrites(); FillTable(); FillSubs();
                    int n = AtomNames().Count, k = Jl(data, "rewrites").Count;
                    statusLeft.Text = k == 0 ? T("No rewrite for this condition.", "Keine Umformung für diese Bedingung.") : T($"{k} rewrites, each proven.", $"{k} Umformungen, jede bewiesen.");
                    statusRight.Text = $"{n} {T("sub-conditions", "Teilbedingungen")} · {Js(data, "combinations")} {T("combinations", "Kombinationen")}" + (NowIndex() != null ? T(" · values of now from the Inline-Logic-View", " · Werte von jetzt aus dem Inline-Logic-View") : "");
                }
                catch (Exception ex)
                {
                    statusLeft.Text = T("The condition could not be read: ", "Die Bedingung konnte nicht gelesen werden: ") + ex.Message;
                    compare.Content = new TextBlock { Text = statusLeft.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
                }
                finally { progress.Visibility = Visibility.Collapsed; }
            }

            apply.Click += (s, e) =>
            {
                var rw = (list.SelectedItem as ListBoxItem)?.Tag as Dictionary<string, object>;
                if (rw == null || site?.Syn == null || !site.Found) return;
                try
                {
                    var cur = SafeGet(site.Syn, "Text") as string;
                    if (cur == null) throw new Exception(T("the editor gives no text", "der Editor liefert keinen Text"));
                    var full = Rewritten(site, cur, Js(rw, "code") ?? "", out var start, out var codeLength);
                    if (full == null)
                    { FormatMessage(T("The code was changed since the window opened - the condition is not where it was. Open the window again.", "Der Code wurde geändert, seit das Fenster offen ist - die Bedingung steht nicht mehr da. Öffne das Fenster neu."), true); return; }
                    if (!Replace(site.Syn, full))
                    { FormatMessage(T("The editor did not take the text - check the code, Ctrl+Z takes it back.", "Der Editor hat den Text nicht übernommen - prüfe den Code, Strg+Z nimmt es zurück."), true); return; }
                    var title = fmtGerman ? Js(rw, "titleDe") ?? Js(rw, "title") : Js(rw, "title");
                    SetStatus(T("TcAgent: condition rewritten (", "TcAgent: Bedingung umgeformt (") + title + T(") - Ctrl+Z takes it back", ") - Strg+Z nimmt es zurück"));
                    // the window goes on with the new condition
                    site.Text = full; site.Start = start; site.Length = codeLength;
                    now = null;
                    Load();
                    statusLeft.Text = "✓ " + T("Applied: ", "Übernommen: ") + title + T(" - Ctrl+Z in the editor takes it back.", " - Strg+Z im Editor nimmt es zurück.");
                    w.Activate();
                }
                catch (Exception ex) { FormatMessage(T("Apply failed: ", "Übernehmen fehlgeschlagen: ") + Inner(ex), true); }
            };

            w.Loaded += (s, e) => Load();
            w.Show();
        }

        /// <summary>The text of the editor with the condition replaced by the code of a rewrite: the next lines of the code get the
        /// indent of the line the condition stands in, the line ends of the editor. null when the condition is no longer where it
        /// was and not exactly once in the text.</summary>
        internal static string Rewritten(ConditionSite site, string cur, string code, out int start, out int length)
        {
            start = site.Start; length = 0;
            var original = site.Original;
            if (start < 0 || start + site.Length > cur.Length || string.CompareOrdinal(cur, start, original, 0, site.Length) != 0)
            {
                int a = cur.IndexOf(original, StringComparison.Ordinal);
                if (a < 0 || cur.IndexOf(original, a + 1, StringComparison.Ordinal) >= 0) return null;
                start = a;
            }
            var nl = cur.Contains("\r\n") ? "\r\n" : "\n";
            var probe = new ConditionSite { Text = cur, Start = start, Length = site.Length };
            var prefix = probe.Prefix;
            var text = string.Join(nl, code.Replace("\r\n", "\n").Split('\n').Select((l, i) => i == 0 || l.Length == 0 ? l : prefix + l));
            length = text.Length;
            return cur.Substring(0, start) + text + cur.Substring(start + site.Length);
        }

        /// <summary>The condition at the cursor of the active editor as a site (text, span, POU, method, line), or null and why.</summary>
        internal ConditionSite ConditionSiteFromEditor(out string why)
        {
            why = null;
            var dv = ActiveDocView(out var doc);
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
            if (string.IsNullOrEmpty(text)) { why = T("TcAgent: put the cursor into a condition (IF, ELSIF, WHILE, UNTIL) of an open POU first", "TcAgent: Setze den Cursor zuerst in eine Bedingung (IF, ELSIF, WHILE, UNTIL) einer offenen POU"); return null; }
            var textLines = text.Replace("\r\n", "\n").Split('\n');
            int nl = text.Contains("\r\n") ? 2 : 1, caret = -1;
            var cpos = SafeGet(syn, "CaretPosition");
            if (cpos is int cpi && cpi >= 0 && cpi <= text.Length) caret = cpi;
            else if (cpos is long cpl && cpl >= 0 && cpl <= text.Length) caret = (int)cpl;
            if (caret < 0 && SafeGet(syn, "CaretLine") is int cl)
                for (int i = 0, k = 0; i < textLines.Length; k += textLines[i].Length + nl, i++) if (i == cl) { caret = k; break; }
            if (caret < 0) caret = 0;
            string sel = SafeGet(syn, "SelectedText") as string;
            if (!string.IsNullOrWhiteSpace(sel) && !(sel.Contains("\n") || Regex.IsMatch(sel, @"\b(AND|OR|XOR|NOT|AND_THEN|OR_ELSE)\b|[<>=]", RegexOptions.IgnoreCase))) sel = null;
            var span = ConditionSpanAt(text, caret, sel);
            if (span.Cond == null) { why = T("TcAgent: no condition at the cursor - put it into an IF, ELSIF, WHILE or UNTIL (or a boolean assignment)", "TcAgent: Keine Bedingung am Cursor - setze ihn in ein IF, ELSIF, WHILE oder UNTIL (oder eine boolesche Zuweisung)"); return null; }
            var (pou, method) = EditorPouMethod(doc);
            if (span.Start < 0)          // a selection that is not found in the text again: shown, never written back
                return new ConditionSite { Syn = syn, Found = false, Text = span.Cond, Start = 0, Length = span.Cond.Length, Line = span.Line + 1, Pou = pou, Method = method };
            return new ConditionSite { Syn = syn, Text = text, Start = span.Start, Length = span.Length, Line = span.Line + 1, Pou = pou, Method = method };
        }

        /// <summary>POU and method of the active editor: its caption ("FB_Station.Cycle", "FB_Station [Online]"), otherwise the file.</summary>
        static (string Pou, string Method) EditorPouMethod(string doc)
        {
            string caption = null;
            try
            {
                var mon = (Microsoft.VisualStudio.Shell.Interop.IVsMonitorSelection)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsShellMonitorSelection));
                if (mon.GetCurrentElementValue(2, out var fo) == 0 && fo is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame fr && fr.GetProperty((int)Microsoft.VisualStudio.Shell.Interop.__VSFPROPID.VSFPROPID_Caption, out var cap) == 0) caption = cap?.ToString();
            }
            catch { }
            var name = Regex.Replace(caption ?? "", @"\s*\[.*?\]|\*", "").Trim();
            if (name.Length == 0 && !string.IsNullOrEmpty(doc)) name = Path.GetFileNameWithoutExtension(doc.Split('|').Last().Trim());
            if (name.Contains(".")) return (name.Substring(0, name.IndexOf('.')), name.Substring(name.IndexOf('.') + 1));
            return (name, null);
        }
    }
}
