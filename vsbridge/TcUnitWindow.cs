// TcUnit Tests - tool window in Visual Studio/TwinCAT XAE Shell, laid out like the Test Explorer of Visual Studio
// (the XAE Shell has none): a tool bar with words (Run all, Run selected, Run failed, Stop, Report, ⋯), the counters as
// filter chips (All / Passed / Failed / Off) with a search box, the progress bar with "Running · 4 of 18 · <test>" or the
// result of the run and its target, a tree list PLC project > suite instance > test with the columns Test / Description /
// Result / Time (suites switched off for the last run as one line "⊘ switched off (n)"), check boxes to choose the tests
// (a click beside them loses nothing), the details like the HTML report (expected / actual / message, code, enable bool,
// Run again, Copy failure, the last 5 runs) and the legend of the symbols in the status line. Test selections can be
// saved and loaded (.tcunit.json). Double-click opens the test method. Rules for the look: CONTRIBUTING.md.
// The window does no TwinCAT work itself - it starts TcAgent.exe:
//   tcunit list --solution <sln>               tests from the code (JSON), after a build and when a .TcPOU/.TcGVL changes
//   tcunit run --attach <pid> [--tests ...]    the run; one JSON event per line, "cancel" on stdin stops it
//   tcunit report --solution <sln>             HTML report of the last run
// User guide: docs/TCUNIT_TESTS.md
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace TcAgentBridge
{
    [Guid("5b0f6c8e-2d3a-4f61-9a7e-3c1d2b8e4f70")]
    public sealed class TcUnitPane : ToolWindowPane
    {
        public TcUnitPane() : base(null)
        {
            Caption = "TcUnit Tests";
            Content = new TcUnitControl();
        }

        // F5 and Ctrl+F belong to the window while it is active - otherwise the IDE starts debugging or opens its own find
        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
        {
            const int WM_KEYDOWN = 0x100;
            if (m.Msg == WM_KEYDOWN && Content is TcUnitControl c &&
                c.Shortcut((System.Windows.Forms.Keys)(int)(long)m.WParam & System.Windows.Forms.Keys.KeyCode, System.Windows.Forms.Control.ModifierKeys == System.Windows.Forms.Keys.Control))
                return true;
            return base.PreProcessMessage(ref m);
        }
    }

    public sealed class TcUnitControl : UserControl
    {
        internal static TcUnitControl Instance;

        // ------------------------------------------------------------------ model
        sealed class TestNode
        {
            public string Instance, Name, Description, Details, EditorPath, File, Enable;
            public int Line, EditorLine;
            public bool Ordered, Ticked;
            public string Status = "notrun", Message, RunInfo;   // RunInfo: "cycle 3/10" of a test that runs over many cycles
            public double? Duration;
            public DateTime RunningSince;
            public Row Row;
        }

        sealed class SuiteNode
        {
            public string Path, Type, Description, Details, File, EditorPath, RunSuite;
            public int Line;
            public bool Selectable, Dynamic, Expanded = true;
            public bool? Ticked = false;
            public readonly List<TestNode> Tests = new List<TestNode>();
            public Row Row;
        }

        /// <summary>One line of the list (project, suite or test) - the columns bind to it.</summary>
        sealed class Row : INotifyPropertyChanged
        {
            public SuiteNode Suite; public TestNode Test;
            public event PropertyChangedEventHandler PropertyChanged;
            internal Action<Row, bool?> OnTick;
            string name, glyph, duration, description, result, tip, expander;
            Brush glyphBrush, resultBrush;
            bool? ticked = false;
            public double Indent { get; set; }   // set again by Rebuild (switched-off suites sit one level deeper)
            public bool CanTick { get; set; }
            public string TickTip { get; set; }   // why a check box is grey
            public Visibility TickVisibility { get; set; } = Visibility.Visible;
            public FontWeight Weight { get; set; } = FontWeights.Normal;
            public string Name { get => name; set => Set(ref name, value, nameof(Name)); }
            public string Glyph { get => glyph; set => Set(ref glyph, value, nameof(Glyph)); }
            public Brush GlyphBrush { get => glyphBrush; set { glyphBrush = value; Raise(nameof(GlyphBrush)); } }
            public string Duration { get => duration; set => Set(ref duration, value, nameof(Duration)); }
            public string Description { get => description; set => Set(ref description, value, nameof(Description)); }
            public string Result { get => result; set => Set(ref result, value, nameof(Result)); }
            public Brush ResultBrush { get => resultBrush; set { resultBrush = value; Raise(nameof(ResultBrush)); } }
            public string Tip { get => tip; set => Set(ref tip, value, nameof(Tip)); }
            public string Expander { get => expander; set => Set(ref expander, value, nameof(Expander)); }
            // set by the user (checkbox) -> OnTick; set by the window (SetTicked) -> only shown
            public bool? Ticked { get => ticked; set { if (ticked == value) return; ticked = value; Raise(nameof(Ticked)); OnTick?.Invoke(this, value); } }
            public void SetTicked(bool? v) { if (ticked == v) return; ticked = v; Raise(nameof(Ticked)); }
            void Set(ref string f, string v, string n) { if (f == v) return; f = v; Raise(n); }
            void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        readonly List<SuiteNode> suites = new List<SuiteNode>();
        // results survive a refresh of the list: instance@test -> (status, message, duration)
        readonly Dictionary<string, (string Status, string Message, double? Duration)> lastResults = new Dictionary<string, (string, string, double?)>(StringComparer.OrdinalIgnoreCase);
        string project, treePath, configTarget, runMode;
        List<string> projects = new List<string>();
        Row projectRow;
        bool projectExpanded = true;
        readonly ObservableCollection<Row> rows = new ObservableCollection<Row>();

        // ------------------------------------------------------------------ view
        static readonly Brush Green = Frozen(Color.FromRgb(0x2E, 0xA0, 0x43)), Red = Frozen(Color.FromRgb(0xD1, 0x34, 0x38)),
                              Blue = Frozen(Color.FromRgb(0x1E, 0x88, 0xE5)), Gray = Frozen(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)),
                              FailBack = Frozen(Color.FromArgb(0x22, 0xD1, 0x34, 0x38));
        internal static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        internal static readonly FontFamily Symbols = new FontFamily("Segoe UI Symbol");

        // tool bar with words: the actions are found without hovering
        readonly Button runAll = Labeled("▶▶", Green, "Run all", "Run every test of the PLC project");
        readonly Button runSel = Labeled("▶", Green, "Run selected", "Run the ticked suites and tests - nothing ticked: the selected line");
        readonly Button runFailed = Labeled("▶✖", Red, "Run failed", "Run the tests that failed in the last run");
        readonly Button cancel = Labeled("■", Red, "Stop", "Stop the run (reset of the PLC; open tests stay \"not run\")");
        readonly Button report = Labeled("\U0001F4C4", null, "Report", "HTML test report of the last run (the command of \"report\" in .tcagent\\tcunit.json or TcAgent's own report)");
        readonly Button more = Tool("⋯", null, "Refresh, expand / collapse, save and load a test selection");
        // the counters are the filters: a word, the number, several can be combined
        readonly ToggleButton fAll = Filter(null, null, "All", "All tests"), fPassed = Filter("✔", Green, "Passed", "Only the passed tests"),
                              fFailed = Filter("✖", Red, "Failed", "Only the failed tests"), fSkipped = Filter("⊘", Gray, "Off", "Only the tests switched off for the last run");
        readonly ComboBox projectBox = new ComboBox { MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, ToolTip = "PLC project", VerticalAlignment = VerticalAlignment.Center };
        readonly TextBox search = new TextBox { BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent };
        readonly TextBlock selection = new TextBlock { Margin = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
        readonly ProgressBar progress = new ProgressBar { Height = 4, Margin = new Thickness(6, 2, 6, 0), Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0) };
        readonly TextBlock progressText = new TextBlock { Margin = new Thickness(6, 2, 6, 2), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Symbols };
        readonly ListView list = new ListView { BorderThickness = new Thickness(0) };
        readonly StackPanel detailBox = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };

        // flat look of the VS tool bars in the light and the dark theme: translucent hover / pressed backgrounds
        static readonly Brush Hover = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Pressed = Frozen(Color.FromArgb(0x50, 0x1E, 0x88, 0xE5)),
                              PressedBorder = Frozen(Color.FromArgb(0xA0, 0x1E, 0x88, 0xE5));

        static ControlTemplate Flat(Type type)
        {
            // the triggers set the border itself: a local Background of the button would win over a trigger on the button
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var t = new ControlTemplate(type) { VisualTree = border };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "bd"));
            t.Triggers.Add(hover);
            if (type == typeof(ToggleButton))
            {
                var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                on.Setters.Add(new Setter(Border.BackgroundProperty, Pressed, "bd"));
                on.Setters.Add(new Setter(Border.BorderBrushProperty, PressedBorder, "bd"));
                t.Triggers.Add(on);
            }
            var off = new Trigger { Property = IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(OpacityProperty, 0.4));
            t.Triggers.Add(off);
            return t;
        }

        internal static readonly ControlTemplate FlatButton = Flat(typeof(Button)), FlatToggle = Flat(typeof(ToggleButton));

        /// <summary>Column header: flat, with the gripper that resizes the column (PART_HeaderGripper).</summary>
        internal static ControlTemplate HeaderTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.AppendChild(content);
            var grip = new FrameworkElementFactory(typeof(Thumb), "PART_HeaderGripper");
            grip.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            grip.SetValue(WidthProperty, 8.0);
            grip.SetValue(MarginProperty, new Thickness(0, 0, -4, 0));
            grip.SetValue(CursorProperty, Cursors.SizeWE);
            grip.SetValue(OpacityProperty, 0.0);
            grid.AppendChild(border); grid.AppendChild(grip);
            return new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = grid };
        }

        internal static Button Tool(string glyph, Brush brush, string tip)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = Symbols, FontSize = 13, Foreground = brush ?? SystemColors.ControlTextBrush },
                Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 2, 0), ToolTip = tip, BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent, MinWidth = 26, Focusable = false, Template = FlatButton
            };
            ToolTipService.SetShowOnDisabled(b, true);
            if (brush == null) ((TextBlock)b.Content).SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return b;
        }

        // a tool bar button with symbol and word
        static Button Labeled(string glyph, Brush brush, string label, string tip)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, FontFamily = Symbols, FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            if (brush != null) g.Foreground = brush; else g.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            sp.Children.Add(g); sp.Children.Add(t);
            var b = new Button { Content = sp, Padding = new Thickness(6, 2, 8, 2), Margin = new Thickness(0, 0, 2, 0), ToolTip = tip, Template = FlatButton,
                                 Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            ToolTipService.SetShowOnDisabled(b, true);
            return b;
        }

        // status filter chip: symbol, word and count; several can be pressed (none or "All" = everything)
        static ToggleButton Filter(string glyph, Brush brush, string label, string what)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null)
            {
                var g = new TextBlock { Text = glyph, FontFamily = Symbols, Margin = new Thickness(0, 0, 4, 0) };
                if (brush != null) g.Foreground = brush; else g.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                sp.Children.Add(g);
            }
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 5, 0) };
            var n = new TextBlock { Text = "0", FontWeight = FontWeights.SemiBold };
            foreach (var x in new[] { l, n }) x.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            sp.Children.Add(l); sp.Children.Add(n);
            var b = new ToggleButton
            {
                Content = sp, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), Focusable = false, Tag = n,
                Template = FlatToggle, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                ToolTip = what + " - click to show only these (several can be combined), click again to show all"
            };
            return b;
        }

        static void Count(ToggleButton b, int n) => ((TextBlock)b.Tag).Text = n.ToString();

        internal static Border Separator()
        {
            var s = new Border { Width = 1, Margin = new Thickness(4, 3, 6, 3) };
            s.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return s;
        }

        public TcUnitControl()
        {
            Instance = this;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            // tool bar: Run all, Run selected, Run failed, Stop | Report, ⋯ (PLC project when there are several)
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 2) };
            foreach (var e in new UIElement[] { runAll, runSel, runFailed, cancel, Separator(), report, more, projectBox }) bar.Children.Add(e);
            cancel.IsEnabled = false;
            var moreMenu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            MenuItem Item(string header, string key, Action act)
            {
                var i = new MenuItem { Header = header, InputGestureText = key ?? "" };
                i.Click += (s, e) => act();
                moreMenu.Items.Add(i);
                return i;
            }
            var refreshItem = Item("Refresh", "F5", Discover);
            moreMenu.Items.Add(new Separator());
            Item("Expand all", null, () => ExpandAll(true));
            Item("Collapse all", null, () => ExpandAll(false));
            moreMenu.Items.Add(new Separator());
            var saveItem = Item("Save selection …", null, SaveSelection);
            var loadItem = Item("Load selection …", null, LoadSelection);
            moreMenu.Opened += (s, e) => { refreshItem.IsEnabled = loadItem.IsEnabled = running == null; saveItem.IsEnabled = TickedItems().Count > 0; };
            more.Click += (s, e) => moreMenu.IsOpen = true;

            // the counters as filters, the search on the right
            var filters = new DockPanel { Margin = new Thickness(4, 0, 4, 2) };
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in new[] { fAll, fPassed, fFailed, fSkipped }) chips.Children.Add(f);
            var sb = SearchBox();
            DockPanel.SetDock(sb, Dock.Right); filters.Children.Add(sb);
            filters.Children.Add(chips);

            // the list: Test, Description, Result (symbol and word), Time - widths resizable at the header
            var grid = new GridView { AllowsColumnReorder = false };
            grid.Columns.Add(new GridViewColumn { Header = "Test", Width = 340, CellTemplate = TestCell() });
            grid.Columns.Add(new GridViewColumn { Header = "Description", Width = 280, DisplayMemberBinding = new Binding(nameof(Row.Description)) });
            var res = new FrameworkElementFactory(typeof(TextBlock));
            res.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Result)));
            res.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Row.ResultBrush)) { TargetNullValue = Gray });
            res.SetValue(TextBlock.FontFamilyProperty, Symbols);   // ✔ and ✖ as symbols, not as colored emoji
            grid.Columns.Add(new GridViewColumn { Header = "Result", Width = 240, CellTemplate = new DataTemplate { VisualTree = res } });
            grid.Columns.Add(new GridViewColumn { Header = "Time", Width = 80, DisplayMemberBinding = new Binding(nameof(Row.Duration)) });
            var header = new Style(typeof(GridViewColumnHeader));
            header.Setters.Add(new Setter(Control.TemplateProperty, HeaderTemplate()));
            header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            grid.ColumnHeaderContainerStyle = header;
            list.View = grid;
            list.ItemsSource = rows;
            list.SelectionMode = SelectionMode.Single;
            var item = new Style(typeof(ListViewItem));
            item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            item.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Row.Tip))));
            item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            list.ItemContainerStyle = item;
            list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            // the details of the selected line, laid out like the HTML report
            var detailsView = new ScrollViewer { Content = detailBox, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var split = new Grid();
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 80 });
            var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(list, 0); Grid.SetRow(splitter, 1); Grid.SetRow(detailsView, 2);
            split.Children.Add(list); split.Children.Add(splitter); split.Children.Add(detailsView);

            // progress: the bar, "Running · 4 of 18 · <test>" or the result of the run with the target left, the ticked tests right
            var progressRow = new DockPanel();
            DockPanel.SetDock(selection, Dock.Right);
            progressRow.Children.Add(selection); progressRow.Children.Add(progressText);
            // status line: the last message left, what the symbols mean right
            var legend = new TextBlock { Margin = new Thickness(12, 2, 6, 4), FontSize = 11 };
            foreach (var (g, b, w) in new[] { ("✔", Green, "passed"), ("✖", Red, "failed"), ("◐", Blue, "running"), ("○", Gray, "not run"), ("⊘", Gray, "switched off") })
            {
                legend.Inlines.Add(new System.Windows.Documents.Run(g + " ") { Foreground = b, FontFamily = Symbols });
                var word = new System.Windows.Documents.Run(w + "   ");
                word.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                legend.Inlines.Add(word);
            }
            var bottom = new DockPanel();
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            DockPanel.SetDock(line, Dock.Top); bottom.Children.Add(line);
            DockPanel.SetDock(legend, Dock.Right); bottom.Children.Add(legend);
            bottom.Children.Add(status);
            // the module Tests switched off (modules.json): the window stays, a hint says that the agent does not see the test tools
            var moduleClose = Tool("✕", null, "Close");
            moduleClose.Click += (s, e) => moduleHint.Visibility = Visibility.Collapsed;
            var moduleLink = new TextBlock { Text = "Switch on (for me)", Foreground = Blue, TextDecorations = TextDecorations.Underline, Cursor = Cursors.Hand,
                                             Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            moduleLink.MouseLeftButtonUp += (s, e) => SwitchTestsOn();
            var moduleText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                                             Text = "The module Tests is switched off – the AI agent does not see tc_test_run and the other test tools. This window works as before." };
            moduleText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var mh = new DockPanel();
            DockPanel.SetDock(moduleClose, Dock.Right); mh.Children.Add(moduleClose);
            DockPanel.SetDock(moduleLink, Dock.Right); mh.Children.Add(moduleLink);
            mh.Children.Add(moduleText);
            moduleHint.Child = mh;
            var root = new DockPanel();
            foreach (var top in new UIElement[] { bar, moduleHint, filters, progress, progressRow }) { DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); }
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(split);
            Content = root;
            foreach (var t in new[] { selection, progressText, status }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            runAll.Click += (s, e) => StartRun(null, "all tests");
            runSel.Click += (s, e) => RunSelected();
            runFailed.Click += (s, e) => RunFailed();
            cancel.Click += (s, e) => Cancel();
            report.Click += (s, e) => MakeReport();
            foreach (var f in new[] { fPassed, fFailed, fSkipped })
                f.Click += (s, e) => { fAll.IsChecked = !(fPassed.IsChecked == true || fFailed.IsChecked == true || fSkipped.IsChecked == true); Rebuild(); };
            fAll.IsChecked = true;
            fAll.Click += (s, e) => { fAll.IsChecked = true; fPassed.IsChecked = fFailed.IsChecked = fSkipped.IsChecked = false; Rebuild(); };
            search.TextChanged += (s, e) => Rebuild();
            projectBox.SelectionChanged += (s, e) => { if (projectBox.SelectedItem is string p && p != project) { project = p; Discover(); } };
            list.SelectionChanged += (s, e) => ShowDetails();
            list.MouseDoubleClick += (s, e) =>
            {
                if (Selected()?.Test != null) { OpenSelected(); e.Handled = true; }
                else if (Selected() is Row r && e.OriginalSource is FrameworkElement fe && !(fe.TemplatedParent is CheckBox)) { Toggle(r); e.Handled = true; }
            };
            list.PreviewKeyDown += (s, e) =>
            {
                var r = Selected();
                if (r == null) return;
                if (e.Key == Key.Space && r.CanTick) { r.Ticked = r.Ticked != true; e.Handled = true; }
                else if ((e.Key == Key.Left && IsExpanded(r)) || (e.Key == Key.Right && !IsExpanded(r) && r.Test == null)) { Toggle(r); e.Handled = true; }
                else if (e.Key == Key.Enter && r.Test != null) { OpenSelected(); e.Handled = true; }
            };
            PreviewKeyDown += (s, e) =>
            {
                if (Shortcut((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), Keyboard.Modifiers == ModifierKeys.Control)) e.Handled = true;
            };
            // a test that runs over many cycles: its time (or cycle) goes on while it runs
            // the running test, its suite and the project count on while the test runs (not only when it is done)
            runningClock.Tick += (s, e) =>
            {
                var running = suites.Where(x => x.Tests.Any(t => t.Status == "running")).ToList();
                foreach (var sn in running) { foreach (var t in sn.Tests.Where(x => x.Status == "running")) UpdateTest(t); UpdateSuite(sn); }
                if (running.Count > 0) UpdateProject();
                // the progress line with the time of the run - it goes on even while one test takes long
                if (runningLine != null && runStarted > DateTime.MinValue) progressText.Text = runningLine + " · " + Seconds((DateTime.Now - runStarted).TotalSeconds);
            };
            IsVisibleChanged += (s, e) => { if (IsVisible && suites.Count == 0 && !discovering) Discover(); };
            Dispatcher.ShutdownStarted += (s, e) => { try { if (running?.HasExited == false) running.StandardInput.WriteLine("cancel"); } catch { } StopFollow(); };
            HookIdeEvents();
            Progress(null, 0, 0);
            ShowDetails();
            status.Text = "Reading the tests from the code …";
        }

        readonly System.Windows.Threading.DispatcherTimer runningClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        /// <summary>F5 = refresh, Ctrl+F = search (the pane hands them over before the IDE takes them).</summary>
        internal bool Shortcut(System.Windows.Forms.Keys key, bool ctrl)
        {
            if (key == System.Windows.Forms.Keys.F5 && !ctrl) { if (running == null) Discover(); return true; }
            if (key == System.Windows.Forms.Keys.F && ctrl) { search.Focus(); Keyboard.Focus(search); search.SelectAll(); return true; }
            return false;
        }

        /// <summary>Search box with magnifier and placeholder, framed like the one of the Test Explorer.</summary>
        FrameworkElement SearchBox()
        {
            var hint = new TextBlock { Text = "Search tests (Ctrl+F)", Opacity = 0.55, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            search.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            search.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            search.TextChanged += (s, e) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var glass = new TextBlock { Text = "\U0001F50D", FontFamily = Symbols, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            glass.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var clear = new TextBlock { Text = "✕", Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Clear the search", Visibility = Visibility.Collapsed };
            clear.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            clear.MouseLeftButtonDown += (s, e) => { search.Text = ""; e.Handled = true; };
            search.TextChanged += (s, e) => clear.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(hint); g.Children.Add(search);
            Grid.SetColumn(clear, 1); Grid.SetColumn(glass, 2);
            g.Children.Add(clear); g.Children.Add(glass);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = g, MinWidth = 180, MaxWidth = 340, Height = 24,
                                     Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            return frame;
        }

        /// <summary>First column: indentation, expander, checkbox, status symbol, name.</summary>
        DataTemplate TestCell()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var indent = new FrameworkElementFactory(typeof(FrameworkElement));
            indent.SetBinding(WidthProperty, new Binding(nameof(Row.Indent)));
            var exp = new FrameworkElementFactory(typeof(TextBlock));
            exp.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Expander)));
            exp.SetValue(WidthProperty, 14.0);
            exp.SetValue(TextBlock.FontFamilyProperty, Symbols);
            exp.SetValue(TextBlock.FontSizeProperty, 10.0);
            exp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            exp.SetValue(CursorProperty, Cursors.Hand);
            exp.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                if ((s as FrameworkElement)?.DataContext is Row r) { Toggle(r); e.Handled = true; }
            }));
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Row.Ticked)) { Mode = BindingMode.TwoWay });
            cb.SetBinding(IsEnabledProperty, new Binding(nameof(Row.CanTick)));
            cb.SetBinding(VisibilityProperty, new Binding(nameof(Row.TickVisibility)));
            cb.SetBinding(ToolTipProperty, new Binding(nameof(Row.TickTip)));
            cb.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            cb.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(MarginProperty, new Thickness(0, 0, 4, 0));
            cb.SetValue(FocusableProperty, false);
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Glyph)));
            glyph.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Row.GlyphBrush)));
            glyph.SetValue(TextBlock.FontFamilyProperty, Symbols);
            glyph.SetValue(WidthProperty, 16.0);
            glyph.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            glyph.SetValue(MarginProperty, new Thickness(0, 0, 4, 0));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
            name.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(Row.Weight)));
            foreach (var c in new[] { indent, exp, cb, glyph, name }) sp.AppendChild(c);
            return new DataTemplate { VisualTree = sp };
        }

        // ------------------------------------------------------------------ TcAgent.exe
        internal static string TcAgentExe()
        {
            var env = Environment.GetEnvironmentVariable("TCAGENT_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            if (File.Exists(Installer.Exe)) return Installer.Exe;
            // the server the chat starts (mcp-claude.json), e.g. an older version not yet updated
            var v = Installer.RegisteredServerVersion();
            var reg = v == null ? null : Path.Combine(Path.GetDirectoryName(Installer.AppDir) ?? "", v, "TcAgent.exe");
            return reg != null && File.Exists(reg) ? reg : null;
        }

        internal static string SolutionPath()
        {
            try { return (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.Solution?.FullName; } catch { return null; }
        }

        static Process Start(string exe, string args, bool input)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.EnvironmentVariables["TCAGENT_CLIENT"] = "window:tcunit";
            return Process.Start(psi);
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        // ------------------------------------------------------------------ discovery
        bool discovering, rediscover;

        internal void Discover()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Discover)); return; }
            if (discovering) { rediscover = true; return; }
            var sln = SolutionPath();
            var exe = TcAgentExe();
            if (string.IsNullOrEmpty(sln) || !File.Exists(sln)) { Clear(); status.Text = "No solution open."; return; }
            if (exe == null) { Clear(); status.Text = "TcAgent.exe not found - start the IDE once after installing TcAgent (VSIX in Visual Studio 2022, install-xaeshell.ps1 in the XAE Shell), or set TCAGENT_EXE."; return; }
            discovering = true;
            status.Text = "Reading the tests from the code ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                string json = null, err = null;
                try
                {
                    using (var p = Start(exe, "tcunit list --solution " + Quote(sln), false))
                    {
                        var e = p.StandardError.ReadToEndAsync();
                        json = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(60000);
                        if (p.ExitCode != 0) err = (e.Result ?? "").Trim();
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                var testsOff = TestsModuleOff(exe, sln);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    discovering = false;
                    moduleHint.Visibility = testsOff ? Visibility.Visible : Visibility.Collapsed;
                    if (err != null) status.Text = "tcunit list failed: " + err;
                    else try { Load(json); } catch (Exception ex) { status.Text = "Test list not readable: " + ex.Message; }
                    if (externalQueue.Count > 0 && suites.Count > 0) FlushExternal();   // a run from the chat that started meanwhile
                    if (rediscover) { rediscover = false; Discover(); }
                }));
            });
        }

        void Clear() { suites.Clear(); projectRow = null; rows.Clear(); UpdateCounters(); }

        // ------------------------------------------------------------------ the module Tests (modules.json, docs/PLUGINS.md)
        readonly Border moduleHint = new Border { Background = Frozen(Color.FromArgb(0x30, 0xE8, 0xB0, 0x1A)), BorderBrush = Frozen(Color.FromArgb(0x80, 0xE8, 0xB0, 0x1A)),
                                                  BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 2, 3), Margin = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed };

        /// <summary>True if the module Tests is switched off for this solution (TcAgent.exe modules --json). Call in the background.</summary>
        static bool TestsModuleOff(string exe, string sln)
        {
            try
            {
                using (var p = Start(exe, "modules --json --solution " + Quote(sln), true))
                {
                    p.StandardInput.Close();         // an older TcAgent.exe without "modules" starts the MCP server: it ends at once
                    var e = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    var m = L(new JavaScriptSerializer().DeserializeObject(o), "modules").FirstOrDefault(x => S(x, "id") == "test");
                    return m != null && !B(m, "on");
                }
            }
            catch { return false; }       // an older TcAgent.exe without modules: nothing to say
        }

        void SwitchTestsOn()
        {
            var exe = TcAgentExe();
            if (exe == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                string o = "";
                try
                {
                    using (var p = Start(exe, "modules enable test", true))
                    {
                        p.StandardInput.Close();
                        var e = p.StandardError.ReadToEndAsync();
                        o = (p.StandardOutput.ReadToEnd() + " " + e.Result).Trim();
                        p.WaitForExit(30000);
                    }
                }
                catch (Exception ex) { o = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { status.Text = o.Split('\n')[0]; Discover(); }));
            });
        }

        static object Get(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v : null;
        static string S(object o, string k) => Get(o, k) as string;
        static int I(object o, string k) => Get(o, k) is int i ? i : 0;
        static bool B(object o, string k) => Get(o, k) is bool b && b;
        static IEnumerable<object> L(object o, string k) => (Get(o, k) as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();

        void Load(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            var all = L(root, "projects").Where(p => I(p, "tests") > 0 || L(p, "instances").Any()).ToList();
            projects = all.Select(p => S(p, "name")).ToList();
            configTarget = S(Get(root, "config"), "target");
            var wanted = project ?? S(Get(root, "config"), "plcProject");
            var pj = all.FirstOrDefault(p => string.Equals(S(p, "name"), wanted, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(S(p, "treePath"), wanted, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
            projectBox.Items.Clear();
            foreach (var p in projects) projectBox.Items.Add(p);
            projectBox.Visibility = projects.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            // keep ticks and expansion across a refresh
            var ticked = new HashSet<string>(suites.SelectMany(s => s.Tests.Where(t => t.Ticked).Select(t => t.Instance + "@" + t.Name)), StringComparer.OrdinalIgnoreCase);
            var tickedSuites = new HashSet<string>(suites.Where(s => s.Ticked == true).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
            var collapsed = new HashSet<string>(suites.Where(s => !s.Expanded).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
            Clear();
            if (pj == null) { project = null; status.Text = "No TcUnit test suites in this solution (FUNCTION_BLOCK ... EXTENDS TcUnit.FB_TestSuite with an instance in a PROGRAM or GVL)."; return; }
            project = S(pj, "name"); treePath = S(pj, "treePath"); runMode = S(pj, "runMode");
            projectBox.SelectedItem = project;
            var types = L(pj, "suites").ToDictionary(s => S(s, "type"), s => s, StringComparer.OrdinalIgnoreCase);
            foreach (var inst in L(pj, "instances"))
            {
                types.TryGetValue(S(inst, "suite") ?? "", out var type);
                var sn = new SuiteNode
                {
                    Path = S(inst, "path"), Type = S(inst, "suite"), Description = S(inst, "description") ?? S(type, "description"), Details = S(type, "details"),
                    File = S(type, "file"), Line = I(type, "line"), EditorPath = S(type, "editorPath"), RunSuite = S(inst, "runSuite"),
                    Selectable = B(type, "testsSelectable"), Dynamic = B(type, "dynamic")
                };
                sn.Expanded = !collapsed.Contains(sn.Path);
                foreach (var t in L(type, "tests"))
                {
                    var enable = L(inst, "tests").FirstOrDefault(x => S(x, "name") == S(t, "name"));
                    var tn = new TestNode
                    {
                        Instance = sn.Path, Name = S(t, "name"), Description = S(t, "description"), Details = S(t, "details"), EditorPath = S(t, "editorPath"),
                        File = S(t, "file"), Line = I(t, "line"), EditorLine = I(t, "editorLine"), Enable = S(enable, "enable"), Ordered = B(t, "ordered")
                    };
                    if (lastResults.TryGetValue(tn.Instance + "@" + tn.Name, out var r)) { tn.Status = r.Status; tn.Message = r.Message; tn.Duration = r.Duration; }
                    sn.Tests.Add(tn);
                }
                // tests known only from an earlier run (names built at run time)
                foreach (var kv in lastResults.Where(k => k.Key.StartsWith(sn.Path + "@", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = kv.Key.Substring(sn.Path.Length + 1);
                    if (sn.Tests.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    sn.Tests.Add(new TestNode { Instance = sn.Path, Name = name, Status = kv.Value.Status, Message = kv.Value.Message, Duration = kv.Value.Duration, EditorPath = sn.EditorPath });
                }
                suites.Add(sn);
                MakeRows(sn);
                foreach (var t in sn.Tests) t.Ticked = ticked.Contains(t.Instance + "@" + t.Name) || (tickedSuites.Contains(sn.Path) && CanTick(sn, t));
                if (tickedSuites.Contains(sn.Path) && !sn.Selectable) sn.Ticked = true;
                ShowTicks(sn);
            }
            projectRow = new Row { Name = project + "  (" + treePath + ")", Weight = FontWeights.SemiBold, TickVisibility = Visibility.Collapsed, Indent = 0, Tip = treePath };
            UpdateAll();
            var n = suites.Sum(x => x.Tests.Count);
            status.Text = $"{n} test(s) in {suites.Count} suite instance(s)" + (runMode == "parallel" ? " - TcUnit.RUN(): during a run only the overall progress" : runMode == null ? " - no TcUnit.RUN / RUN_IN_SEQUENCE found" : "") +
                          (string.IsNullOrEmpty(configTarget) || configTarget.Equals("local", StringComparison.OrdinalIgnoreCase) ? "" : " - target " + configTarget);
            Follow();
        }

        static bool CanTick(SuiteNode sn, TestNode t) => sn.Selectable || t.Enable != null;

        void MakeRows(SuiteNode sn)
        {
            sn.Row = new Row
            {
                Suite = sn, Name = sn.Path, Indent = 14, CanTick = true, Weight = FontWeights.SemiBold, OnTick = OnTick,
                Tip = (sn.Description ?? sn.Type) + (sn.RunSuite == null ? "\nNo bRunSuite: this suite always runs." : "") +
                      (!sn.Selectable && sn.Tests.Count > 0 ? "\nWhole suite only: not every test has a bRun_<TestMethod> bool." : "") +
                      (sn.Dynamic ? "\nTest names are built at run time: they appear after the first run." : "")
            };
            foreach (var t in sn.Tests) MakeRow(sn, t);
        }

        void MakeRow(SuiteNode sn, TestNode t)
        {
            t.Row = new Row
            {
                Suite = sn, Test = t, Name = t.Name, Indent = 28, CanTick = CanTick(sn, t), OnTick = OnTick, Expander = "",
                Tip = (t.Description ?? t.Name) + (t.Enable == null ? "\nNo bRun_ bool: runs with its suite." : ""),
                TickTip = CanTick(sn, t) ? "Tick to run this test with \"Run selected\"" : "no enable bool – runs only with its whole suite or Run all"
            };
        }

        // ------------------------------------------------------------------ ticks
        // ticking the suite ticks all its tests; a partly ticked suite shows the third state (only those tests run)
        void OnTick(Row r, bool? value)
        {
            if (r.Test != null)
            {
                r.Test.Ticked = value == true;
                ShowTicks(r.Suite);
            }
            else if (r.Suite != null)
            {
                var on = value != false;
                foreach (var t in r.Suite.Tests) t.Ticked = on && CanTick(r.Suite, t);
                r.Suite.Ticked = on;
                ShowTicks(r.Suite);
            }
            UpdateSelection();
        }

        /// <summary>Checkbox states of a suite and its tests: all ticked = ticked, some = third state, none = empty.</summary>
        void ShowTicks(SuiteNode sn)
        {
            foreach (var t in sn.Tests) t.Row?.SetTicked(t.Ticked);
            var boxes = sn.Tests.Where(t => CanTick(sn, t)).ToList();
            if (boxes.Count > 0)
            {
                int on = boxes.Count(t => t.Ticked);
                sn.Ticked = on == 0 ? false : on == boxes.Count ? true : (bool?)null;
            }
            sn.Row?.SetTicked(sn.Ticked);
        }

        /// <summary>The --tests list of "Run" and of a saved selection: whole suites as instance, single tests as instance@test.</summary>
        List<string> TickedItems()
        {
            var items = new List<string>();
            foreach (var s in suites)
            {
                var ticked = s.Tests.Where(t => t.Ticked).ToList();
                if (s.Ticked == true && (ticked.Count == s.Tests.Count || !s.Selectable)) items.Add(s.Path);
                else items.AddRange(ticked.Select(t => CanTick(s, t) ? t.Instance + "@" + t.Name : s.Path));
            }
            return items.Distinct().ToList();
        }

        /// <summary>Tests "Run" would run: the ticked ones, whole suites where single tests cannot be switched off.</summary>
        int TickedCount() => suites.Sum(s => s.Ticked == true && !s.Selectable ? s.Tests.Count : s.Tests.Count(t => t.Ticked));

        // run progress: tests of the run and the ones that have a result
        int runTotal;
        readonly HashSet<string> runFinished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void UpdateSelection()
        {
            if (running != null)
            {
                var total = Math.Max(runTotal, runFinished.Count);
                selection.Text = total == 0 ? "" : $"{runFinished.Count} of {total} done ({100.0 * runFinished.Count / total:0} %)";
                if (total > 0) { progress.Maximum = total; progress.Value = runFinished.Count; }
                return;
            }
            var ticked = TickedCount();
            var all = suites.Sum(s => s.Tests.Count);
            // the state of the runs and where they go: "Not run yet · target: local runtime"
            var tests = suites.SelectMany(s => s.Tests).ToList();
            var state = tests.Any(t => t.Status is "passed" or "failed")
                ? (tests.Any(t => t.Status == "failed") ? $"Last run: {tests.Count(t => t.Status == "failed")} failed" : "Last run: passed")
                : "Not run yet";
            selection.Text = state + (ticked > 0 ? $" · {ticked} of {all} ticked" : $" · {all} tests") + " · target: " + TargetText();
        }

        // ------------------------------------------------------------------ saved test selections (.tcunit.json)
        // {"project": "Tests", "tests": ["MAIN.fbMotor", "MAIN.fbCalc@Adds"]} - the same list as --tests; tcunit run --tests @file reads it
        string SelectionDir()
        {
            var sln = SolutionPath();
            return string.IsNullOrEmpty(sln) ? null : Path.Combine(Path.GetDirectoryName(sln) ?? "", ".tcagent");
        }

        void SaveSelection()
        {
            var items = TickedItems();
            if (items.Count == 0) { status.Text = "Tick the suites/tests of the selection first."; return; }
            var dir = SelectionDir();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save test selection", Filter = "TcUnit test selection (*.tcunit.json)|*.tcunit.json", DefaultExt = ".tcunit.json",
                FileName = "selection.tcunit.json", InitialDirectory = dir != null && Directory.Exists(dir) ? dir : null
            };
            if (dlg.ShowDialog() != true) return;
            var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["project"] = project, ["tests"] = items });
            File.WriteAllText(dlg.FileName, json.Replace("[\"", "[\n    \"").Replace("\",\"", "\",\n    \"").Replace("\"]", "\"\n  ]").Replace("{\"", "{\n  \"").Replace(",\"tests\"", ",\n  \"tests\"").Replace("]}", "]\n}") + "\n",
                              new UTF8Encoding(false));
            status.Text = $"Test selection saved ({items.Count} entr{(items.Count == 1 ? "y" : "ies")}): {dlg.FileName}";
        }

        void LoadSelection()
        {
            var dir = SelectionDir();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load test selection", Filter = "TcUnit test selection (*.tcunit.json)|*.tcunit.json|All files (*.*)|*.*",
                InitialDirectory = dir != null && Directory.Exists(dir) ? dir : null
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(dlg.FileName));
                var items = L(o, "tests").Select(x => x as string).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (S(o, "project") is string p && project != null && !p.Equals(project, StringComparison.OrdinalIgnoreCase))
                    status.Text = $"Note: the selection was saved for the PLC project {p}.";
                var missing = new List<string>();
                foreach (var s in suites) { foreach (var t in s.Tests) t.Ticked = false; s.Ticked = false; }
                foreach (var it in items)
                {
                    var at = it.IndexOf('@');
                    var inst = at > 0 ? it.Substring(0, at) : it;
                    var s = suites.FirstOrDefault(x => x.Path.Equals(inst, StringComparison.OrdinalIgnoreCase) || x.Type.Equals(inst, StringComparison.OrdinalIgnoreCase));
                    if (s == null) { missing.Add(it); continue; }
                    if (at < 0) { foreach (var t in s.Tests) t.Ticked = CanTick(s, t); s.Ticked = true; continue; }
                    var test = s.Tests.FirstOrDefault(x => x.Name.Equals(it.Substring(at + 1), StringComparison.OrdinalIgnoreCase));
                    if (test == null) missing.Add(it); else test.Ticked = true;
                }
                foreach (var s in suites) { if (s.Ticked != true || s.Selectable) s.Ticked = false; ShowTicks(s); }
                UpdateSelection();
                status.Text = $"Test selection loaded: {Path.GetFileName(dlg.FileName)} ({items.Count - missing.Count} of {items.Count})" +
                              (missing.Count > 0 ? " - not found: " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? " ..." : "") : "");
            }
            catch (Exception ex) { status.Text = "Test selection not readable: " + ex.Message; }
        }

        // ------------------------------------------------------------------ list content
        // suites whose tests were all switched off for the last run: one line "⊘ switched off (n)" instead of n lines
        Row offRow;
        bool offExpanded;

        bool IsExpanded(Row r) => r == projectRow ? projectExpanded : r == offRow ? offExpanded : r.Suite != null && r.Test == null && r.Suite.Expanded;

        /// <summary>Opens or closes every suite (the project line stays open).</summary>
        void ExpandAll(bool open)
        {
            projectExpanded = true;
            offExpanded = open;
            foreach (var s in suites) s.Expanded = open;
            Rebuild();
        }

        void Toggle(Row r)
        {
            if (r == projectRow) projectExpanded = !projectExpanded;
            else if (r == offRow) offExpanded = !offExpanded;
            else if (r.Suite != null && r.Test == null) r.Suite.Expanded = !r.Suite.Expanded;
            else return;
            Rebuild();
        }

        bool StatusShown(TestNode t)
        {
            if (fAll.IsChecked == true) return true;
            return (fPassed.IsChecked == true && t.Status == "passed") || (fFailed.IsChecked == true && t.Status == "failed") || (fSkipped.IsChecked == true && t.Status == "skipped");
        }

        static bool SwitchedOff(SuiteNode s) => s.Tests.Count > 0 && s.Tests.All(t => t.Status == "skipped");

        /// <summary>Visible lines from expansion, status filter and search (the selected line stays selected).</summary>
        void Rebuild()
        {
            var keep = Selected();
            rows.Clear();
            if (projectRow == null) return;
            projectRow.Expander = projectExpanded ? "◢" : "▷";
            rows.Add(projectRow);
            if (!projectExpanded) return;
            var q = (search.Text ?? "").Trim();
            bool Hit(string s) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            var shown = new List<(SuiteNode Suite, List<TestNode> Tests)>();
            foreach (var s in suites)
            {
                bool suiteHit = q.Length > 0 && (Hit(s.Path) || Hit(s.Type) || Hit(s.Description));
                var tests = s.Tests.Where(t => StatusShown(t) && (q.Length == 0 || suiteHit || Hit(t.Name) || Hit(t.Description))).ToList();
                bool showEmpty = s.Tests.Count == 0 && fAll.IsChecked == true && (q.Length == 0 || suiteHit);
                if (tests.Count > 0 || showEmpty) shown.Add((s, tests));
            }
            // grouped only in the full list - the filter "Off" and a search show them where they are
            var off = fAll.IsChecked == true && q.Length == 0 ? shown.Where(x => SwitchedOff(x.Suite)).ToList() : new List<(SuiteNode Suite, List<TestNode> Tests)>();
            void Add(SuiteNode s, List<TestNode> tests, double indent)
            {
                s.Row.Indent = indent;
                s.Row.Expander = s.Tests.Count == 0 ? "" : s.Expanded ? "◢" : "▷";
                rows.Add(s.Row);
                if (s.Expanded) foreach (var t in tests) { t.Row.Indent = indent + 14; rows.Add(t.Row); }
            }
            foreach (var (s, tests) in shown.Except(off)) Add(s, tests, 14);
            if (off.Count > 0)
            {
                offRow ??= new Row { TickVisibility = Visibility.Hidden, Glyph = "⊘", GlyphBrush = Gray, ResultBrush = Gray };
                offRow.Indent = 14;
                offRow.Name = $"switched off ({off.Count})";
                offRow.Result = $"⊘ {off.Sum(x => x.Suite.Tests.Count)} tests switched off";
                offRow.Description = "suites not run in the last run";
                offRow.Tip = string.Join("\n", off.Select(x => x.Suite.Path));
                offRow.Expander = offExpanded ? "◢" : "▷";
                rows.Add(offRow);
                if (offExpanded) foreach (var (s, tests) in off) Add(s, tests, 28);
            }
            if (keep != null && rows.Contains(keep)) list.SelectedItem = keep;
        }

        // ------------------------------------------------------------------ status display
        void UpdateTest(TestNode t)
        {
            if (t.Row == null) return;
            var (g, b) = Look(t.Status);
            t.Row.Glyph = g; t.Row.GlyphBrush = b;
            t.Row.Duration = t.Duration is double d && t.Status is "passed" or "failed" ? Seconds(d) : "";
            t.Row.Description = t.Description ?? "";
            t.Row.ResultBrush = b;
            t.Row.Result = t.Status == "running"
                ? "◐ running · " + (t.RunInfo ?? Seconds((DateTime.Now - t.RunningSince).TotalSeconds))   // a test over many cycles goes on
                : g + " " + Text(t.Status);
        }

        // the result of several tests in words: "✖ 1 failed · ✔ 3 passed · ⊘ 2 off"
        static (string Text, Brush Brush) Sum(List<TestNode> tests)
        {
            if (tests.Any(t => t.Status == "running")) return ("◐ running", Blue);
            int p = tests.Count(t => t.Status == "passed"), f = tests.Count(t => t.Status == "failed"), o = tests.Count(t => t.Status == "skipped"), n = tests.Count(t => t.Status == "notrun");
            var parts = new List<string>();
            if (f > 0) parts.Add($"✖ {f} failed");
            if (p > 0) parts.Add($"✔ {p} passed");
            if (n > 0) parts.Add($"○ {n} not run");
            if (o > 0) parts.Add($"⊘ {o} off");
            return (parts.Count == 0 ? "–" : string.Join(" · ", parts), f > 0 ? Red : p > 0 && n == 0 ? Green : Gray);
        }

        void UpdateSuite(SuiteNode sn)
        {
            if (sn.Row == null) return;
            var st = sn.Tests.Any(t => t.Status == "running") ? "running" : sn.Tests.Any(t => t.Status == "failed") ? "failed"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status == "skipped") ? "skipped"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status is "passed" or "skipped") ? "passed" : "notrun";
            var (g, b) = Look(st);
            sn.Row.Glyph = g; sn.Row.GlyphBrush = b;
            sn.Row.Name = $"{sn.Path} ({sn.Tests.Count})";
            sn.Row.Duration = Time(sn.Tests);
            sn.Row.Description = sn.Description ?? sn.Type;
            (sn.Row.Result, sn.Row.ResultBrush) = Sum(sn.Tests);
        }

        void UpdateProject()
        {
            if (projectRow == null) return;
            var all = suites.SelectMany(s => s.Tests).ToList();
            var st = all.Any(t => t.Status == "running") ? "running" : all.Any(t => t.Status == "failed") ? "failed"
                   : all.Count > 0 && all.All(t => t.Status is "passed" or "skipped") && all.Any(t => t.Status == "passed") ? "passed" : "notrun";
            var (g, b) = Look(st);
            projectRow.Glyph = g; projectRow.GlyphBrush = b;
            projectRow.Name = $"{project} ({all.Count})";
            projectRow.Duration = Time(all);
            projectRow.Description = runMode == "sequence" ? "runs suites in sequence" : runMode == "parallel" ? "runs suites in parallel" : "";
            (projectRow.Result, projectRow.ResultBrush) = Sum(all);
        }

        // the time of several tests: the finished ones plus the one running now (so far)
        static string Time(IEnumerable<TestNode> tests)
        {
            double sum = 0; bool any = false;
            foreach (var t in tests)
            {
                if (t.Status is "passed" or "failed") { sum += t.Duration ?? 0; any = true; }
                else if (t.Status == "running" && t.RunningSince > DateTime.MinValue) { sum += Math.Max(0, (DateTime.Now - t.RunningSince).TotalSeconds); any = true; }
            }
            return any ? Seconds(sum) : "";
        }

        void UpdateAll()
        {
            foreach (var s in suites) { foreach (var t in s.Tests) UpdateTest(t); UpdateSuite(s); }
            UpdateProject();
            UpdateCounters();
            UpdateSelection();
            Rebuild();
        }

        static (string, Brush) Look(string status)
        {
            switch (status)
            {
                case "passed": return ("✔", Green);
                case "failed": return ("✖", Red);
                case "running": return ("◐", Blue);
                case "skipped": return ("⊘", Gray);
                default: return ("○", Gray);
            }
        }

        static string Seconds(double s) => s < 1 ? $"{s * 1000:0} ms" : s < 60 ? $"{s:0.0} s" : $"{(int)(s / 60)}:{(int)s % 60:00} min";

        void UpdateCounters()
        {
            var all = suites.SelectMany(s => s.Tests).ToList();
            Count(fAll, all.Count);
            Count(fPassed, all.Count(t => t.Status == "passed"));
            Count(fFailed, all.Count(t => t.Status == "failed"));
            Count(fSkipped, all.Count(t => t.Status == "skipped"));
            runFailed.IsEnabled = running == null && all.Any(t => t.Status == "failed");
        }

        Row Selected() => list.SelectedItem as Row;

        static string Text(string status) => status == "notrun" ? "not run" : status == "skipped" ? "switched off" : status;

        // ------------------------------------------------------------------ details (laid out like the HTML report)
        TextBlock Words(string text, double size = 12, bool bold = false, Brush brush = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            if (brush != null) t.Foreground = brush; else t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        // label / value lines; a value may hold links
        Grid Facts(params (string Label, object Value)[] lines)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var (label, value) in lines.Where(l => l.Value != null))
            {
                var r = g.RowDefinitions.Count;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = Words(label, bold: true); k.Opacity = 0.8; k.Margin = new Thickness(0, 1, 8, 1);
                var v = value as UIElement ?? Words(value.ToString());
                if (v is FrameworkElement fe) fe.Margin = new Thickness(0, 1, 0, 1);
                Grid.SetRow(k, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                g.Children.Add(k); g.Children.Add(v);
            }
            return g;
        }

        TextBlock Links(params (string Label, Action Act)[] links)
        {
            var t = Words("");
            for (int i = 0; i < links.Length; i++)
            {
                if (i > 0) t.Inlines.Add(new System.Windows.Documents.Run(" · "));
                var act = links[i].Act;
                var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(links[i].Label)) { Foreground = Blue };
                h.Click += (s, e) => act();
                t.Inlines.Add(h);
            }
            return t;
        }

        /// <summary>TcUnit's message "EXP: 100, ACT: 101, MSG: text" as expected / actual / message.</summary>
        static (string Expected, string Actual, string Message) Failure(string m)
        {
            m = string.Join("\n", (m ?? "").Split('\n').Distinct()).Trim();
            string Take(string key)
            {
                var i = m.IndexOf(key + ":", StringComparison.Ordinal);
                if (i < 0) return null;
                i += key.Length + 1;
                var ends = new[] { ", EXP:", ", ACT:", ", MSG:", "\nEXP:", "\nACT:", "\nMSG:" }.Select(k => m.IndexOf(k, i, StringComparison.Ordinal)).Where(x => x >= 0).ToList();
                return m.Substring(i, (ends.Count > 0 ? ends.Min() : m.Length) - i).Trim();
            }
            var exp = Take("EXP"); var act = Take("ACT"); var msg = Take("MSG");
            return exp == null && act == null && msg == null ? (null, null, m) : (exp, act, msg);
        }

        void ShowDetails()
        {
            detailBox.Children.Clear();
            detailBox.Tag = null;
            var r = Selected();
            if (r?.Test is TestNode t)
            {
                var (g, b) = Look(t.Status);
                var head = Words(t.Name, 14, true);
                head.Inlines.Add(new System.Windows.Documents.Run("   " + (r.Result ?? g + " " + Text(t.Status)) + (t.Duration is double d && t.Status is "passed" or "failed" ? " · " + Seconds(d) : ""))
                                     { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = b, FontFamily = Symbols });
                detailBox.Children.Add(head);
                if (t.Description != null) detailBox.Children.Add(Words(t.Description));
                var doc = t.Details ?? (r.Suite?.Details != null && t.File == null ? r.Suite.Details : null);
                if (doc != null) { var dt = Words(doc); dt.Opacity = 0.85; dt.Margin = new Thickness(0, 4, 0, 0); detailBox.Children.Add(dt); }
                if (!string.IsNullOrEmpty(t.Message))
                {
                    var (exp, act, msg) = Failure(t.Message);
                    var box = new Border { Background = FailBack, BorderBrush = Red, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0) };
                    var f = Facts(("Expected", exp), ("Actual", act), ("Message", msg));
                    f.Margin = new Thickness(0);
                    foreach (var tb in f.Children.OfType<TextBlock>().Where(x => Grid.GetColumn(x) == 1)) tb.FontFamily = new FontFamily("Consolas");
                    box.Child = f;
                    detailBox.Children.Add(box);
                }
                var key = CanTick(r.Suite, t) ? t.Instance + "@" + t.Name : r.Suite.Path;
                detailBox.Children.Add(Facts(
                    ("Suite", r.Suite.Path + (r.Suite.Type != null ? " : " + r.Suite.Type : "")),
                    ("Code", t.File != null ? (object)Links((Path.GetFileName(t.File) + ":" + t.Line, OpenSelected)) : "test name built at run time / not found in the code"),
                    ("Enable bool", t.Enable ?? "– (runs with its suite)"),
                    ("Order", t.Ordered ? "TEST_ORDERED" : null),
                    ("Actions", Links(("Open code", OpenSelected), ("Run again", () => StartRun(new List<string> { key }, t.Name)), ("Copy failure", () => CopyFailure(t)))),
                    ("Last 5 runs", LastRuns(t))));
            }
            else if (r?.Suite is SuiteNode s)
            {
                var (text, brush) = Sum(s.Tests);
                var head = Words(s.Path, 14, true);
                head.Inlines.Add(new System.Windows.Documents.Run("   " + text) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = brush, FontFamily = Symbols });
                detailBox.Children.Add(head);
                if (s.Description != null) detailBox.Children.Add(Words(s.Description));
                if (s.Details != null) { var dt = Words(s.Details); dt.Opacity = 0.85; dt.Margin = new Thickness(0, 4, 0, 0); detailBox.Children.Add(dt); }
                detailBox.Children.Add(Facts(
                    ("Type", s.Type),
                    ("Code", s.File != null ? (object)Links((Path.GetFileName(s.File) + ":" + s.Line, OpenSelected)) : null),
                    ("Enable bool", s.RunSuite ?? "– (no bRunSuite: the suite always runs)"),
                    ("Note", !s.Selectable && s.Tests.Count > 0 ? "Single tests cannot be switched off (bRun_<TestMethod> missing) - whole suite only." : s.Dynamic ? "Test names are built at run time; they appear after the first run." : null),
                    ("Actions", Links(("Open code", OpenSelected), ("Run again", () => StartRun(new List<string> { s.Path }, s.Path))))));
            }
            else if (r != null && r == projectRow)
            {
                var all = suites.SelectMany(x => x.Tests).ToList();
                var (text, brush) = Sum(all);
                var head = Words(project, 14, true);
                head.Inlines.Add(new System.Windows.Documents.Run("   " + text) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = brush, FontFamily = Symbols });
                detailBox.Children.Add(head);
                detailBox.Children.Add(Facts(("PLC project", treePath), ("Suites", $"{suites.Count} instance(s), {all.Count} test(s)"), ("Run mode", runMode == "sequence" ? "runs suites in sequence (TcUnit.RUN_IN_SEQUENCE)" : runMode == "parallel" ? "runs suites in parallel (TcUnit.RUN)" : "–"),
                                             ("Target", TargetText())));
            }
            else if (r != null && r == offRow)
                detailBox.Children.Add(Words("Suites whose tests were all switched off for the last run - open the line to see them; tick them and \"Run selected\" runs them.", brush: Gray));
            else
                detailBox.Children.Add(Words("Select a test - its result, the failure (expected / actual / message), the code, the enable bool and the last runs appear here.", brush: Gray));
        }

        string TargetText() => string.IsNullOrEmpty(configTarget) || configTarget.Equals("local", StringComparison.OrdinalIgnoreCase) ? "local runtime" : configTarget;

        void CopyFailure(TestNode t)
        {
            var text = $"{t.Instance}@{t.Name}: {Text(t.Status)}" + (t.Duration is double d ? $" ({Seconds(d)})" : "") + (string.IsNullOrEmpty(t.Message) ? "" : "\n" + t.Message.Trim());
            try { Clipboard.SetText(text); status.Text = "Copied: " + t.Name; } catch (Exception ex) { status.Text = "Could not copy: " + ex.Message; }
        }

        // the last results of a test, newest first: "✔ 12 ms  ✔ 11 ms  ✖ 1.1 s"
        object LastRuns(TestNode t)
        {
            var h = HistoryOf(t.Instance + "@" + t.Name);
            if (h.Count == 0) return "no run recorded yet";
            var tb = Words("");
            foreach (var e in h)
            {
                var st = S(e, "s");
                var (g, b) = Look(st);
                var d = Get(e, "d") is decimal dd ? (double)dd : Get(e, "d") is int di ? di : (double?)null;
                var text = g + " " + (d is double x && st is "passed" or "failed" ? Seconds(x) : Text(st));
                tb.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new TextBlock { Text = text, Foreground = b, FontFamily = Symbols, Margin = new Thickness(0, 0, 14, 0), ToolTip = (S(e, "t") ?? "").Replace('T', ' ') }));
            }
            return tb;
        }

        // ------------------------------------------------------------------ the last 5 results per test (per solution)
        static readonly string HistoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "tcunit", "history.json");
        Dictionary<string, object> history;

        Dictionary<string, object> Histories()
        {
            if (history != null) return history;
            try { history = File.Exists(HistoryFile) ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(HistoryFile)) as Dictionary<string, object> : null; } catch { }
            return history ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, object> SolutionHistory(bool create)
        {
            var sln = (SolutionPath() ?? "").ToLowerInvariant();
            var all = Histories();
            if (all.TryGetValue(sln, out var o) && o is Dictionary<string, object> d) return d;
            if (!create) return null;
            d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            all[sln] = d;
            return d;
        }

        List<Dictionary<string, object>> HistoryOf(string key) =>
            SolutionHistory(false) is Dictionary<string, object> d && d.TryGetValue(key, out var o) && o is IEnumerable e
                ? e.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();

        // after a run: the result of every test that ran goes in front, the last 5 are kept
        void RememberRun()
        {
            try
            {
                var d = SolutionHistory(true);
                var when = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var t in suites.SelectMany(s => s.Tests).Where(t => runFinished.Contains(t.Instance + "@" + t.Name) && t.Status is "passed" or "failed"))
                {
                    var key = t.Instance + "@" + t.Name;
                    var list5 = new List<object> { new Dictionary<string, object> { ["t"] = when, ["s"] = t.Status, ["d"] = t.Duration } };
                    list5.AddRange(HistoryOf(key).Take(4));
                    d[key] = list5;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile));
                File.WriteAllText(HistoryFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Histories()), new UTF8Encoding(false));
            }
            catch (Exception ex) { BridgePackage.Instance?.LogLine("TcUnit history: " + ex.Message); }
        }

        void OpenSelected()
        {
            try
            {
                var r = Selected();
                if (r?.Test is TestNode t && t.EditorPath != null) BridgePackage.Instance?.OpenInEditor(t.EditorPath, "implementation", t.EditorLine);
                else if (r?.Suite is SuiteNode s && s.EditorPath != null) BridgePackage.Instance?.OpenInEditor(s.EditorPath, "declaration", 0);
            }
            catch (Exception ex) { status.Text = "Could not open the code: " + ex.Message; }
        }

        void Progress(string text, int index, int total)
        {
            progress.Maximum = Math.Max(1, total);
            progress.Value = Math.Min(index, Math.Max(1, total));
            progress.Visibility = total > 0 ? Visibility.Visible : Visibility.Hidden;
            progressText.Text = text ?? "";
        }

        // ------------------------------------------------------------------ run
        Process running;
        DateTime runStarted;
        string runningLine;     // "Running · 4 of 18 · <test>" - the clock adds the time of the run
        int expected;

        void RunSelected()
        {
            var items = TickedItems();
            if (items.Count == 0)
            {
                // nothing ticked: the selected line
                var r = Selected();
                if (r?.Test != null) items.Add(CanTick(r.Suite, r.Test) ? r.Test.Instance + "@" + r.Test.Name : r.Suite.Path);
                else if (r?.Suite != null) items.Add(r.Suite.Path);
                else if (r != null && r == projectRow) { StartRun(null, "all tests"); return; }
            }
            if (items.Count == 0) { status.Text = "Tick suites/tests (or select one) first."; return; }
            StartRun(items, items.Count == 1 ? items[0] : items.Count + " selection(s)");
        }

        void RunFailed()
        {
            var failed = suites.SelectMany(s => s.Tests.Where(t => t.Status == "failed").Select(t => CanTick(s, t) ? t.Instance + "@" + t.Name : s.Path)).Distinct().ToList();
            if (failed.Count == 0) { status.Text = "No failed tests."; return; }
            StartRun(failed, failed.Count + " failed test(s)");
        }

        void StartRun(List<string> tests, string what)
        {
            if (running != null) return;
            var exe = TcAgentExe();
            if (exe == null || project == null) { status.Text = exe == null ? "TcAgent.exe not found." : "No tests."; return; }
            // a test PLC in the network: one question per run (the local runtime: the click is the confirmation)
            if (!string.IsNullOrEmpty(configTarget) && !configTarget.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var owner = Application.Current?.MainWindow;
                var text = $"Run {what} on the test PLC {configTarget}?\n\nTcAgent builds and activates the configuration there if the code changed, resets and starts the PLC.";
                var r = owner != null ? MessageBox.Show(owner, text, "TcUnit Tests", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                                      : MessageBox.Show(text, "TcUnit Tests", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (r != MessageBoxResult.Yes) { status.Text = "Run cancelled."; return; }
            }
            var args = new StringBuilder("tcunit run --attach " + Process.GetCurrentProcess().Id + " --plc " + Quote(project));
            if (tests != null) args.Append(" --tests " + Quote(string.Join(",", tests)));
            if (!string.IsNullOrEmpty(configTarget)) args.Append(" --target " + Quote(configTarget));
            foreach (var t in suites.SelectMany(s => s.Tests)) if (t.Status == "running") t.Status = "notrun";
            StopFollow();                                             // this run is shown from its own events
            try { running = Start(exe, args.ToString(), true); }
            catch (Exception ex) { status.Text = "TcAgent.exe could not be started: " + ex.Message; Follow(); return; }
            BridgePackage.Instance?.LogLine("TcUnit run: " + exe + " " + args);
            runStarted = DateTime.Now; runningLine = null;
            expected = 0; runTotal = 0; runFinished.Clear();
            SetRunning(true);
            UpdateSelection();
            status.Text = "Starting " + what + " …";
            Progress("Preparing · " + what + " · target " + TargetText(), 0, 1);
            progress.Foreground = Blue;
            runningClock.Start();
            progressText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var p = running;
            var err = new StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            System.Threading.Tasks.Task.Run(() =>
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                try
                {
                    for (string line; (line = p.StandardOutput.ReadLine()) != null;)
                    {
                        if (!line.StartsWith("{")) continue;
                        Dictionary<string, object> ev;
                        try { ev = ser.Deserialize<Dictionary<string, object>>(line); } catch { continue; }
                        Dispatcher.BeginInvoke(new Action(() => OnEvent(ev)));
                    }
                    p.WaitForExit();
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    running = null;
                    runningClock.Stop();
                    SetRunning(false);
                    if (!gotDone)
                    {
                        status.Text = $"TcAgent ended without a result (exit {code}): {err.ToString().Trim()}";
                        Progress($"✖ The run ended without a result (exit {code}) - see the status line", 0, 0);
                        progressText.Foreground = Red;
                    }
                    gotDone = false;
                    UpdateCounters();
                    Follow();
                }));
            });
        }

        bool gotDone;
        TestNode runningTest;   // the test TcUnit calls now (for "cycle x/y" of the progress block)

        void SetRunning(bool on)
        {
            runAll.IsEnabled = runSel.IsEnabled = projectBox.IsEnabled = report.IsEnabled = !on;
            cancel.IsEnabled = on;
            runFailed.IsEnabled = !on && suites.SelectMany(s => s.Tests).Any(t => t.Status == "failed");
        }

        /// <summary>HTML report of the last run (TcAgent.exe tcunit report) and open it in the browser.</summary>
        void MakeReport()
        {
            var exe = TcAgentExe();
            var sln = SolutionPath();
            if (exe == null || string.IsNullOrEmpty(sln)) { status.Text = exe == null ? "TcAgent.exe not found." : "No solution open."; return; }
            report.IsEnabled = false;
            status.Text = "Creating the test report ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                string html = null, msg = null;
                try
                {
                    var approval = RunReport(exe, "tcunit report --solution " + Quote(sln), out html, out msg);
                    // the command of "report" in tcunit.json comes with the solution: it runs only if the user agrees once
                    // (per solution and command text); TcAgent's own report is already made
                    if (approval != null && html != null && (bool)Dispatcher.Invoke(new Func<bool>(() => MessageBox.Show(
                            "The test report of this solution is made by a command of its own (" + S(approval, "file") + "):\n\n" + S(approval, "command") +
                            "\n\nRun this command now and from now on (until it changes)? Only if you trust this solution - it runs as you, on this PC." +
                            "\n\nNo: TcAgent's own report (already created).",
                            "TcAgent - report command", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)))
                    {
                        Dispatcher.BeginInvoke(new Action(() => status.Text = "Creating the test report with the command of tcunit.json ..."));
                        RunReport(exe, "tcunit report --solution " + Quote(sln) + " --approve " + Quote(S(approval, "id")), out html, out msg);
                    }
                    // only an HTML file is opened (the path comes from the output of TcAgent.exe)
                    if (html != null && (html.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || html.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) && File.Exists(html))
                        Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
                    else if (html != null) { msg = "not an HTML file: " + html; html = null; }
                }
                catch (Exception ex) { msg = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    report.IsEnabled = running == null;
                    status.Text = html != null ? "Test report: " + html : "Report not created: " + msg;
                }));
            });
        }

        /// <summary>One TcAgent.exe tcunit report; returns the "approval" of the report event (command not approved yet) or null.</summary>
        Dictionary<string, object> RunReport(string exe, string args, out string html, out string msg)
        {
            html = null; msg = null;
            Dictionary<string, object> approval = null;
            using (var p = Start(exe, args, false))
            {
                var err = p.StandardError.ReadToEndAsync();
                var ser = new JavaScriptSerializer();
                for (string line; (line = p.StandardOutput.ReadLine()) != null;)
                {
                    if (!line.StartsWith("{")) continue;
                    try
                    {
                        var ev = ser.Deserialize<Dictionary<string, object>>(line);
                        if (S(ev, "event") != "report") continue;
                        html = B(ev, "ok") ? S(ev, "html") : null; msg = S(ev, "message");
                        approval = ev.TryGetValue("approval", out var a) ? a as Dictionary<string, object> : null;
                    }
                    catch { }
                }
                p.WaitForExit(330_000);
                if (html == null && msg == null) msg = err.Result.Trim();
            }
            return approval;
        }

        void Cancel()
        {
            try { running?.StandardInput.WriteLine("cancel"); status.Text = "Cancelling (reset of the PLC) ..."; }
            catch (Exception ex) { status.Text = "Cancel failed: " + ex.Message; }
        }

        TestNode Find(string inst, string name, bool create)
        {
            var s = suites.FirstOrDefault(x => x.Path.Equals(inst ?? "", StringComparison.OrdinalIgnoreCase));
            if (s == null || name == null) return null;
            var t = s.Tests.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (t == null && create)
            {
                // a test whose name is built at run time
                t = new TestNode { Instance = s.Path, Name = name, EditorPath = s.EditorPath };
                s.Tests.Add(t);
                MakeRow(s, t);
                UpdateTest(t);
                Rebuild();
            }
            return t;
        }

        // ------------------------------------------------------------------ runs started elsewhere (tc_test_run in the chat)
        readonly List<Dictionary<string, object>> externalQueue = new List<Dictionary<string, object>>();
        bool externalRun;

        /// <summary>An event of a run started by an AI client (TcAgentBridge command tcunit_event). Call on the UI thread.</summary>
        internal void External(string json)
        {
            Dictionary<string, object> ev;
            try { ev = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json); } catch { return; }
            if (ev == null || running != null) return;            // a run of this window itself has the runtime - nothing else runs
            followRun = false;                                    // the same run, seen by the follower too: the client's events win
            // the list is not read yet (window just opened): keep the events until it is
            if (discovering || suites.Count == 0)
            {
                externalQueue.Add(ev);
                if (!discovering) Discover();
                return;
            }
            FlushExternal();
            ExternalEvent(ev);
        }

        void FlushExternal()
        {
            var q = externalQueue.ToList();
            externalQueue.Clear();
            foreach (var e in q) ExternalEvent(e);
        }

        void ExternalEvent(Dictionary<string, object> ev)
        {
            if (!externalRun && S(ev, "event") != "done")
            {
                externalRun = true;
                runStarted = DateTime.Now; runningLine = null; expected = 0; runTotal = 0; runFinished.Clear();
                SetRunning(true);
                runningClock.Start();
                cancel.IsEnabled = false;                             // the run belongs to the AI client: it is stopped there
                Progress("Running · started from the chat …", 0, 1);
            }
            OnEvent(ev);
            if (S(ev, "event") == "done") { externalRun = false; runningClock.Stop(); SetRunning(false); gotDone = false; UpdateCounters(); }
        }

        // ------------------------------------------------------------------ runs started outside TcAgent (IDE: activate + start, a pipeline)
        Process follower;           // TcAgent.exe tcunit watch: reads over ADS only, writes and starts nothing
        string followerKey;
        bool followRun;

        /// <summary>Starts the follower for the shown PLC project (again, if solution, project or target changed). Call on the UI thread.</summary>
        void Follow()
        {
            var sln = SolutionPath();
            var exe = TcAgentExe();
            if (running != null || project == null || exe == null || string.IsNullOrEmpty(sln) || !File.Exists(sln)) { StopFollow(); return; }
            var key = sln + "|" + project + "|" + configTarget;
            if (follower != null && !follower.HasExited && key == followerKey) return;
            StopFollow();
            var args = "tcunit watch --solution " + Quote(sln) + " --plc " + Quote(project) + (string.IsNullOrEmpty(configTarget) ? "" : " --target " + Quote(configTarget));
            Process p;
            try { p = Start(exe, args, true); }
            catch (Exception ex) { BridgePackage.Instance?.LogLine("TcUnit follower: " + ex.Message); return; }
            follower = p; followerKey = key;
            p.ErrorDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            System.Threading.Tasks.Task.Run(() =>
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                try
                {
                    for (string line; (line = p.StandardOutput.ReadLine()) != null;)
                    {
                        if (!line.StartsWith("{")) continue;
                        Dictionary<string, object> ev;
                        try { ev = ser.Deserialize<Dictionary<string, object>>(line); } catch { continue; }
                        Dispatcher.BeginInvoke(new Action(() => { if (follower == p) FollowEvent(ev); }));
                    }
                }
                catch { }
                // ended (refused target, no solution ...): a half followed run is not left as "running"
                Dispatcher.BeginInvoke(new Action(() => { if (follower == p) { follower = null; EndFollowRun(); } }));
            });
        }

        void StopFollow()
        {
            var p = follower;
            follower = null; followerKey = null;
            EndFollowRun();
            if (p == null) return;
            try { if (!p.HasExited) { p.StandardInput.WriteLine("cancel"); p.StandardInput.Close(); } } catch { }
        }

        void EndFollowRun()
        {
            if (!followRun) return;
            followRun = false;
            runningClock.Stop(); SetRunning(false); gotDone = false; UpdateCounters();
        }

        void FollowEvent(Dictionary<string, object> ev)
        {
            var kind = S(ev, "event");
            if (kind == "plan")
            {
                if (running != null || externalRun) return;           // shown from the events of the run itself
                followRun = true;
                runStarted = DateTime.Now; runningLine = null; expected = 0; runTotal = 0; runFinished.Clear();
                SetRunning(true);
                runningClock.Start();
                cancel.IsEnabled = false;                             // started elsewhere: it is stopped there
                Progress("Running · started outside this window …", 0, 1);
            }
            if (!followRun) return;
            OnEvent(ev);
            if (kind == "done") EndFollowRun();
        }

        void OnEvent(Dictionary<string, object> ev)
        {
            switch (S(ev, "event"))
            {
                case "plan":
                    expected = I(ev, "expected");
                    foreach (var r in L(ev, "tests"))
                        if (Find(S(r, "instance"), S(r, "test"), false) is TestNode t) { t.Status = S(r, "status"); t.Message = null; t.Duration = null; }
                    runTotal = Math.Max(expected, L(ev, "tests").Count(r => S(r, "status") == "notrun"));
                    UpdateAll();
                    Progress($"Running · 0 of {expected} · target {TargetText()}", 0, expected);
                    runningLine = progressText.Text;
                    break;
                case "step":
                    status.Text = S(ev, "text");
                    break;
                case "compile":
                    if (S(ev, "level") == "ERROR")
                    {
                        // the build errors in the details area, as the failure of the run
                        if (!(detailBox.Tag is string)) { detailBox.Children.Clear(); detailBox.Tag = "build"; detailBox.Children.Add(Words("Build errors - the tests did not run", 14, true, Red)); }
                        var e = Words(S(ev, "text") + (S(ev, "file") != null ? $"  ({Path.GetFileName(S(ev, "file"))}:{I(ev, "line")})" : ""));
                        e.FontFamily = new FontFamily("Consolas");
                        detailBox.Children.Add(e);
                    }
                    break;
                case "suite":
                    var sn = suites.FirstOrDefault(x => x.Path == S(ev, "instance"));
                    if (sn?.Row != null && rows.Contains(sn.Row)) list.ScrollIntoView(sn.Row);
                    break;
                case "running":
                {
                    int index = I(ev, "index"), total = Math.Max(I(ev, "total"), expected);
                    var name = S(ev, "test");
                    if (name != null && Find(S(ev, "instance"), name, true) is TestNode t)
                    {
                        runningTest = t;
                        if (t.Status == "notrun")
                        {
                            t.Status = "running"; t.RunningSince = DateTime.Now; t.RunInfo = null;
                            UpdateTest(t); UpdateSuite(suites.First(x => x.Path == t.Instance)); UpdateProject();
                        }
                    }
                    var rem = Get(ev, "remainingSec") is int r ? $" · about {Seconds(r)} left" : Get(ev, "remainingSec") is decimal rd ? $" · about {Seconds((double)rd)} left" : "";
                    progressText.Text = runningLine = name != null ? $"Running · {index} of {total} · {name}{rem}" : $"Running · {index} of {total} finished{rem}";
                    break;
                }
                case "progress":
                {
                    // the project's own progress block (tcunit.json "progress") - shown as it is
                    var parts = new[] { "current", "total", "test", "remaining" }.Select(k => Get(ev, k)).ToList();
                    if (parts[1] != null)
                        progressText.Text = runningLine = $"Running · {Convert.ToInt32(parts[0] ?? 0)} of {Convert.ToInt32(parts[1])} · {parts[2]}" + (parts[3] != null ? $" · remaining {parts[3]}" : "");
                    // a test over many cycles: "cycle 3/10" from the progress block (Cycle, Cycles)
                    if (Get(ev, "cycle") != null && runningTest?.Status == "running")
                    {
                        runningTest.RunInfo = "cycle " + Convert.ToString(Get(ev, "cycle"), System.Globalization.CultureInfo.InvariantCulture) +
                                              (Get(ev, "cycles") != null ? "/" + Convert.ToString(Get(ev, "cycles"), System.Globalization.CultureInfo.InvariantCulture) : "");
                        UpdateTest(runningTest);
                    }
                    break;
                }
                case "result":
                {
                    var t = Find(S(ev, "instance"), S(ev, "test"), true);
                    if (t == null) break;
                    t.Status = S(ev, "status"); t.Message = S(ev, "message"); t.RunInfo = null;
                    t.Duration = Get(ev, "duration") is decimal d ? (double)d : Get(ev, "duration") is int di ? di : (double?)null;
                    lastResults[t.Instance + "@" + t.Name] = (t.Status, t.Message, t.Duration);
                    UpdateTest(t);
                    UpdateSuite(suites.First(x => x.Path == t.Instance));
                    UpdateProject();
                    UpdateCounters();
                    if (Selected() == t.Row) ShowDetails();
                    if (t.Status is "passed" or "failed" or "skipped") runFinished.Add(t.Instance + "@" + t.Name);
                    UpdateSelection();
                    break;
                }
                case "done":
                {
                    gotDone = true;
                    foreach (var t in suites.SelectMany(s => s.Tests).Where(t => t.Status == "running")) t.Status = "notrun";
                    var st = S(ev, "status");
                    UpdateSelection();
                    foreach (var s in suites) { foreach (var t in s.Tests) UpdateTest(t); UpdateSuite(s); }
                    UpdateProject();
                    UpdateCounters();
                    Rebuild();
                    status.Text = S(ev, "message");
                    progress.Value = progress.Maximum;
                    // "✖ 2 failed · 16 passed · 24 switched off · 12.5 s · target local runtime"
                    int pa = I(ev, "passed"), fa = I(ev, "failed"), sk = I(ev, "skipped"), nr = I(ev, "notRun");
                    var counts = st is "passed" or "failed"
                        ? string.Join(" · ", new[] { fa > 0 ? $"✖ {fa} failed" : null, $"{(fa > 0 ? "" : "✔ ")}{pa} passed", nr > 0 ? $"{nr} not run" : null, sk > 0 ? $"{sk} switched off" : null }.Where(x => x != null))
                        : "✖ " + S(ev, "message");
                    progressText.Text = counts + (runStarted > DateTime.MinValue ? $" · {Seconds((DateTime.Now - runStarted).TotalSeconds)}" : "") + $" · target {TargetText()}";
                    progressText.Foreground = st == "passed" ? Green : st is "failed" or "error" or "timeout" or "refused" or "build" ? Red : null;
                    progress.Foreground = st == "passed" ? Green : Red;
                    runningClock.Stop();
                    RememberRun();
                    if (progressText.Foreground == null) progressText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    if (Selected() != null) ShowDetails();
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ re-read the list after a build and when code is saved
        EnvDTE.BuildEvents buildEvents;
        EnvDTE.SolutionEvents solutionEvents;
        FileSystemWatcher watcher;
        System.Windows.Threading.DispatcherTimer debounce;

        void HookIdeEvents()
        {
            debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            debounce.Tick += (s, e) => { debounce.Stop(); if (IsVisible && running == null) Discover(); };
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) return;
                buildEvents = dte.Events.BuildEvents;          // keep references: DTE event objects are collected otherwise
                buildEvents.OnBuildDone += (scope, action) => Soon();
                solutionEvents = dte.Events.SolutionEvents;
                solutionEvents.Opened += () => { Watch(); project = null; lastResults.Clear(); Dispatcher.BeginInvoke(new Action(() => suites.Clear())); Soon(); };
                solutionEvents.AfterClosing += () => { Watch(); Dispatcher.BeginInvoke(new Action(() => { StopFollow(); Clear(); status.Text = "No solution open."; })); };
                Watch();
            }
            catch (Exception ex) { BridgePackage.Instance?.LogLine("TcUnit window events: " + ex.Message); }
        }

        void Soon() => Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));

        void Watch()
        {
            try
            {
                watcher?.Dispose(); watcher = null;
                var sln = SolutionPath();
                if (string.IsNullOrEmpty(sln) || !File.Exists(sln)) return;
                watcher = new FileSystemWatcher(Path.GetDirectoryName(sln)) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                FileSystemEventHandler h = (s, e) =>
                {
                    var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                    if (ext == ".tcpou" || ext == ".tcgvl" || ext == ".plcproj" || e.FullPath.EndsWith("tcunit.json", StringComparison.OrdinalIgnoreCase)) Soon();
                };
                watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
                watcher.Renamed += (s, e) => Soon();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { BridgePackage.Instance?.LogLine("TcUnit window watcher: " + ex.Message); }
        }
    }
}
