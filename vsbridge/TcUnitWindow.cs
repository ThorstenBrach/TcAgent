// TcUnit Tests - tool window in Visual Studio/TwinCAT XAE Shell, laid out like the Test Explorer of Visual Studio
// (the XAE Shell has none): toolbar with run buttons, status filter buttons with counts and a search box, a tree list
// PLC project > suite instance > test with the columns Test / Duration / Traits / Error Message (resizable), progress
// bar, details pane. Test selections can be saved and loaded (.tcunit.json). Double-click opens the test method.
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
            public string Status = "notrun", Message;
            public double? Duration;
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
            string name, glyph, duration, traits, message, tip, expander;
            Brush glyphBrush;
            bool? ticked = false;
            public double Indent { get; set; }
            public bool CanTick { get; set; }
            public Visibility TickVisibility { get; set; } = Visibility.Visible;
            public FontWeight Weight { get; set; } = FontWeights.Normal;
            public string Name { get => name; set => Set(ref name, value, nameof(Name)); }
            public string Glyph { get => glyph; set => Set(ref glyph, value, nameof(Glyph)); }
            public Brush GlyphBrush { get => glyphBrush; set { glyphBrush = value; Raise(nameof(GlyphBrush)); } }
            public string Duration { get => duration; set => Set(ref duration, value, nameof(Duration)); }
            public string Traits { get => traits; set => Set(ref traits, value, nameof(Traits)); }
            public string Message { get => message; set => Set(ref message, value, nameof(Message)); }
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
                              Blue = Frozen(Color.FromRgb(0x1E, 0x88, 0xE5)), Gray = Frozen(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static readonly FontFamily Symbols = new FontFamily("Segoe UI Symbol");

        readonly Button runAll = Tool("▶▶", Green, "Run All Tests (all tests of the PLC project)");
        readonly Button runSel = Tool("▶", Green, "Run (the ticked suites and tests, or the selected line)");
        readonly Button runFailed = Tool("▶✖", Red, "Run Failed Tests (of the last run)");
        readonly Button cancel = Tool("■", Red, "Cancel (reset of the PLC; open tests stay \"not run\")");
        readonly Button refresh = Tool("⟳", null, "Refresh (read the tests from the code again)");
        readonly Button expandAll = Tool("⊞", null, "Expand all suites");
        readonly Button collapseAll = Tool("⊟", null, "Collapse all suites");
        readonly Button report = Tool("\U0001F4C4", null, "Test report (HTML of the last run: the command of \"report\" in .tcagent\\tcunit.json or TcAgent's own report)");
        readonly Button save = Tool("\U0001F4BE", null, "Save the ticked tests as a test selection (.tcunit.json)");
        readonly Button load = Tool("\U0001F4C2", null, "Load a test selection (.tcunit.json) - ticks its tests");
        readonly ToggleButton fAll = Filter("⚗", null, "All tests"), fPassed = Filter("✔", Green, "Passed tests"), fFailed = Filter("✖", Red, "Failed tests"),
                              fNotRun = Filter("○", Gray, "Tests not run"), fSkipped = Filter("⊘", Gray, "Skipped tests");
        readonly ComboBox projectBox = new ComboBox { MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, ToolTip = "PLC project", VerticalAlignment = VerticalAlignment.Center };
        readonly TextBox search = new TextBox { BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent };
        readonly TextBlock selection = new TextBlock { Margin = new Thickness(6, 2, 6, 2), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly ProgressBar progress = new ProgressBar { Height = 5, Margin = new Thickness(6, 0, 6, 0), Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0) };
        readonly TextBlock progressText = new TextBlock { Margin = new Thickness(6, 0, 6, 2), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly ListView list = new ListView { BorderThickness = new Thickness(0) };
        readonly TextBox details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                  FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6) };
        readonly Button openCode = new Button { Content = "Open code", Padding = new Thickness(8, 1, 8, 1), ToolTip = "Open the selected test in the editor (or double-click it)" };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };

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

        static readonly ControlTemplate FlatButton = Flat(typeof(Button)), FlatToggle = Flat(typeof(ToggleButton));

        /// <summary>Column header: flat, with the gripper that resizes the column (PART_HeaderGripper).</summary>
        static ControlTemplate HeaderTemplate()
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

        static Button Tool(string glyph, Brush brush, string tip)
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

        // status filter like the Test Explorer: glyph + count, several can be pressed (none or "all" = everything)
        static ToggleButton Filter(string glyph, Brush brush, string what)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = glyph, FontFamily = Symbols, Margin = new Thickness(0, 0, 4, 0) };
            if (brush != null) g.Foreground = brush; else g.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var n = new TextBlock { Text = "0" };
            n.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            sp.Children.Add(g); sp.Children.Add(n);
            return new ToggleButton
            {
                Content = sp, Padding = new Thickness(5, 2, 6, 2), Margin = new Thickness(0, 0, 2, 0), Focusable = false, Tag = n,
                Template = FlatToggle, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                ToolTip = what + " - click to show only these (several can be combined), click again to show all"
            };
        }

        static void Count(ToggleButton b, int n) => ((TextBlock)b.Tag).Text = n.ToString();

        static Border Separator()
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

            // toolbar: run buttons | refresh | status filters | report, selections, project ... search box on the right
            var bar = new DockPanel { Margin = new Thickness(4, 4, 4, 2), LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var e in new UIElement[] { runAll, runSel, runFailed, cancel, Separator(), refresh, expandAll, collapseAll, Separator(), fAll, fPassed, fFailed, fNotRun, fSkipped, Separator(), report, save, load, projectBox })
                left.Children.Add(e);
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);
            bar.Children.Add(SearchBox());
            cancel.IsEnabled = false;

            // the list: columns like the Test Explorer, widths resizable at the header
            var grid = new GridView { AllowsColumnReorder = false };
            grid.Columns.Add(new GridViewColumn { Header = "Test", Width = 360, CellTemplate = TestCell() });
            grid.Columns.Add(new GridViewColumn { Header = "Duration", Width = 80, DisplayMemberBinding = new Binding(nameof(Row.Duration)) });
            grid.Columns.Add(new GridViewColumn { Header = "Traits", Width = 140, DisplayMemberBinding = new Binding(nameof(Row.Traits)) });
            grid.Columns.Add(new GridViewColumn { Header = "Error Message", Width = 420, DisplayMemberBinding = new Binding(nameof(Row.Message)) });
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

            var detailsPanel = new DockPanel();
            var detailsBar = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
            DockPanel.SetDock(openCode, Dock.Right);
            detailsBar.Children.Add(openCode);
            var dt = new TextBlock { Text = "Test Detail Summary", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            dt.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            detailsBar.Children.Add(dt);
            DockPanel.SetDock(detailsBar, Dock.Top);
            detailsPanel.Children.Add(detailsBar); detailsPanel.Children.Add(details);
            details.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            details.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var split = new Grid();
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(list, 0); Grid.SetRow(splitter, 1); Grid.SetRow(detailsPanel, 2);
            split.Children.Add(list); split.Children.Add(splitter); split.Children.Add(detailsPanel);

            // progress: bar, "Test 3 of 9: name - about 5 s left" on the left, "3 of 9 done (33 %)" on the right
            var progressRow = new DockPanel();
            DockPanel.SetDock(selection, Dock.Right);
            progressRow.Children.Add(selection); progressRow.Children.Add(progressText);
            var root = new DockPanel();
            foreach (var top in new UIElement[] { bar, progress, progressRow }) { DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); }
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);
            root.Children.Add(split);
            Content = root;
            foreach (var t in new[] { selection, progressText, status }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            runAll.Click += (s, e) => StartRun(null, "all tests");
            runSel.Click += (s, e) => RunSelected();
            runFailed.Click += (s, e) => RunFailed();
            cancel.Click += (s, e) => Cancel();
            refresh.Click += (s, e) => Discover();
            expandAll.Click += (s, e) => ExpandAll(true);
            collapseAll.Click += (s, e) => ExpandAll(false);
            report.Click += (s, e) => MakeReport();
            save.Click += (s, e) => SaveSelection();
            load.Click += (s, e) => LoadSelection();
            foreach (var f in new[] { fPassed, fFailed, fNotRun, fSkipped })
                f.Click += (s, e) => { fAll.IsChecked = !(fPassed.IsChecked == true || fFailed.IsChecked == true || fNotRun.IsChecked == true || fSkipped.IsChecked == true); Rebuild(); };
            fAll.IsChecked = true;
            fAll.Click += (s, e) => { fAll.IsChecked = true; fPassed.IsChecked = fFailed.IsChecked = fNotRun.IsChecked = fSkipped.IsChecked = false; Rebuild(); };
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
            openCode.Click += (s, e) => OpenSelected();
            IsVisibleChanged += (s, e) => { if (IsVisible && suites.Count == 0 && !discovering) Discover(); };
            Dispatcher.ShutdownStarted += (s, e) => { try { if (running?.HasExited == false) running.StandardInput.WriteLine("cancel"); } catch { } };
            HookIdeEvents();
            Progress(null, 0, 0);
            status.Text = "Reading the tests from the code ...";
        }

        /// <summary>Search box with magnifier and placeholder, framed like the one of the Test Explorer.</summary>
        FrameworkElement SearchBox()
        {
            var hint = new TextBlock { Text = "Search TcUnit Tests", Opacity = 0.55, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
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
        static string TcAgentExe()
        {
            var env = Environment.GetEnvironmentVariable("TCAGENT_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            if (File.Exists(Installer.Exe)) return Installer.Exe;
            // the server the chat starts (mcp-claude.json), e.g. an older version not yet updated
            var v = Installer.RegisteredServerVersion();
            var reg = v == null ? null : Path.Combine(Path.GetDirectoryName(Installer.AppDir) ?? "", v, "TcAgent.exe");
            return reg != null && File.Exists(reg) ? reg : null;
        }

        static string SolutionPath()
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
            if (exe == null) { Clear(); status.Text = "TcAgent.exe not found - start Visual Studio 2022 once after installing the VSIX (or set TCAGENT_EXE)."; return; }
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
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    discovering = false;
                    if (err != null) status.Text = "tcunit list failed: " + err;
                    else try { Load(json); } catch (Exception ex) { status.Text = "Test list not readable: " + ex.Message; }
                    if (rediscover) { rediscover = false; Discover(); }
                }));
            });
        }

        void Clear() { suites.Clear(); projectRow = null; rows.Clear(); UpdateCounters(); }

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
                Tip = (t.Description ?? t.Name) + (t.Enable == null ? "\nNo bRun_ bool: runs with its suite." : "")
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
            selection.Text = ticked > 0 ? $"{ticked} of {all} test(s) selected" : $"{all} test(s)";
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
        bool IsExpanded(Row r) => r == projectRow ? projectExpanded : r.Suite != null && r.Test == null && r.Suite.Expanded;

        /// <summary>Opens or closes every suite (the project line stays open).</summary>
        void ExpandAll(bool open)
        {
            projectExpanded = true;
            foreach (var s in suites) s.Expanded = open;
            Rebuild();
        }

        void Toggle(Row r)
        {
            if (r == projectRow) projectExpanded = !projectExpanded;
            else if (r.Suite != null && r.Test == null) r.Suite.Expanded = !r.Suite.Expanded;
            else return;
            Rebuild();
        }

        bool StatusShown(TestNode t)
        {
            if (fAll.IsChecked == true) return true;
            return (fPassed.IsChecked == true && t.Status == "passed") || (fFailed.IsChecked == true && t.Status == "failed")
                   || (fNotRun.IsChecked == true && t.Status is "notrun" or "running") || (fSkipped.IsChecked == true && t.Status == "skipped");
        }

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
            foreach (var s in suites)
            {
                bool suiteHit = q.Length > 0 && (Hit(s.Path) || Hit(s.Type) || Hit(s.Description));
                var tests = s.Tests.Where(t => StatusShown(t) && (q.Length == 0 || suiteHit || Hit(t.Name) || Hit(t.Description))).ToList();
                bool showEmpty = s.Tests.Count == 0 && fAll.IsChecked == true && (q.Length == 0 || suiteHit);
                if (tests.Count == 0 && !showEmpty) continue;
                s.Row.Expander = s.Tests.Count == 0 ? "" : s.Expanded ? "◢" : "▷";
                rows.Add(s.Row);
                if (s.Expanded) foreach (var t in tests) rows.Add(t.Row);
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
            t.Row.Message = string.IsNullOrEmpty(t.Message) ? "" : t.Message.Split('\n')[0];
            t.Row.Traits = string.Join(", ", new[] { t.Ordered ? "Ordered" : null, t.Enable == null ? "No bRun_" : null, t.Status == "skipped" ? "Skipped" : null }.Where(x => x != null));
        }

        void UpdateSuite(SuiteNode sn)
        {
            if (sn.Row == null) return;
            var st = sn.Tests.Any(t => t.Status == "running") ? "running" : sn.Tests.Any(t => t.Status == "failed") ? "failed"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status == "skipped") ? "skipped"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status is "passed" or "skipped") ? "passed" : "notrun";
            var (g, b) = Look(st);
            sn.Row.Glyph = g; sn.Row.GlyphBrush = b;
            var done = sn.Tests.Where(t => t.Status is "passed" or "failed").ToList();
            sn.Row.Name = $"{sn.Path} ({sn.Tests.Count})";
            sn.Row.Duration = done.Count > 0 ? Seconds(done.Sum(t => t.Duration ?? 0)) : "";
            sn.Row.Traits = string.Join(", ", new[] { sn.Type, sn.RunSuite == null ? "No bRunSuite" : null, !sn.Selectable && sn.Tests.Count > 0 ? "Whole suite" : null, sn.Dynamic ? "Run-time names" : null }.Where(x => x != null));
            var failed = done.Count(t => t.Status == "failed");
            sn.Row.Message = failed > 0 ? $"{failed} failed" : "";
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
            var done = all.Where(t => t.Status is "passed" or "failed").ToList();
            projectRow.Duration = done.Count > 0 ? Seconds(done.Sum(t => t.Duration ?? 0)) : "";
            projectRow.Traits = runMode == "sequence" ? "RUN_IN_SEQUENCE" : runMode == "parallel" ? "RUN (parallel)" : "";
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
            Count(fNotRun, all.Count(t => t.Status is "notrun" or "running"));
            Count(fSkipped, all.Count(t => t.Status == "skipped"));
            runFailed.IsEnabled = running == null && all.Any(t => t.Status == "failed");
        }

        Row Selected() => list.SelectedItem as Row;

        void ShowDetails()
        {
            var sb = new StringBuilder();
            var r = Selected();
            if (r?.Test is TestNode t)
            {
                sb.AppendLine(t.Instance + " @ " + t.Name);
                if (t.Description != null) sb.AppendLine(t.Description);
                if (t.Details != null) { sb.AppendLine(); sb.AppendLine(t.Details); }
                else if (r.Suite?.Details != null && t.File == null) { sb.AppendLine(); sb.AppendLine(r.Suite.Details); }
                sb.AppendLine();
                sb.AppendLine("Status:   " + Text(t.Status) + (t.Duration is double d ? "   (" + Seconds(d) + ")" : ""));
                if (!string.IsNullOrEmpty(t.Message)) { sb.AppendLine(); sb.AppendLine(t.Message.Replace(", ACT:", "\nACT:").Replace(", MSG:", "\nMSG:")); sb.AppendLine(); }
                if (t.File != null) sb.AppendLine("Code:     " + Path.GetFileName(t.File) + ":" + t.Line);
                sb.AppendLine("Enable:   " + (t.Enable ?? "- (runs with its suite)"));
            }
            else if (r?.Suite is SuiteNode s)
            {
                sb.AppendLine(s.Path + " : " + s.Type);
                if (s.Description != null) sb.AppendLine(s.Description);
                if (s.Details != null) { sb.AppendLine(); sb.AppendLine(s.Details); }
                sb.AppendLine();
                sb.AppendLine($"{s.Tests.Count} test(s): {s.Tests.Count(x => x.Status == "passed")} passed, {s.Tests.Count(x => x.Status == "failed")} failed, {s.Tests.Count(x => x.Status == "skipped")} skipped");
                sb.AppendLine("Enable:   " + (s.RunSuite ?? "- (no bRunSuite: the suite always runs)"));
                if (!s.Selectable && s.Tests.Count > 0) sb.AppendLine("Single tests cannot be switched off (bRun_<TestMethod> missing) - whole suite only.");
                if (s.Dynamic) sb.AppendLine("Test names are built at run time; they appear after the first run.");
                if (s.File != null) sb.AppendLine("Code:     " + Path.GetFileName(s.File) + ":" + s.Line);
            }
            else if (r != null && r == projectRow)
            {
                var all = suites.SelectMany(x => x.Tests).ToList();
                sb.AppendLine(project + "  (" + treePath + ")");
                sb.AppendLine($"{suites.Count} suite instance(s), {all.Count} test(s): {all.Count(x => x.Status == "passed")} passed, {all.Count(x => x.Status == "failed")} failed");
            }
            details.Text = sb.ToString();
            openCode.IsEnabled = r?.Test != null ? r.Test.EditorPath != null : r?.Suite?.EditorPath != null;
        }

        static string Text(string status) => status == "notrun" ? "not run" : status;

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
            try { running = Start(exe, args.ToString(), true); }
            catch (Exception ex) { status.Text = "TcAgent.exe could not be started: " + ex.Message; return; }
            BridgePackage.Instance?.LogLine("TcUnit run: " + exe + " " + args);
            runStarted = DateTime.Now;
            expected = 0; runTotal = 0; runFinished.Clear();
            SetRunning(true);
            UpdateSelection();
            status.Text = "Starting " + what + " ...";
            Progress("Preparing ...", 0, 1);
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
                    SetRunning(false);
                    if (!gotDone) { status.Text = $"TcAgent ended without a result (exit {code}): {err.ToString().Trim()}"; Progress(null, 0, 0); }
                    gotDone = false;
                    UpdateCounters();
                }));
            });
        }

        bool gotDone;

        void SetRunning(bool on)
        {
            runAll.IsEnabled = runSel.IsEnabled = refresh.IsEnabled = projectBox.IsEnabled = report.IsEnabled = load.IsEnabled = !on;
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
                    Progress($"0 of {expected} test(s)", 0, expected);
                    break;
                case "step":
                    status.Text = S(ev, "text");
                    break;
                case "compile":
                    if (S(ev, "level") == "ERROR") details.Text += (details.Text.Length > 0 ? "\n" : "") + "Build error: " + S(ev, "text") + (S(ev, "file") != null ? $"  ({Path.GetFileName(S(ev, "file"))}:{I(ev, "line")})" : "");
                    break;
                case "suite":
                    var sn = suites.FirstOrDefault(x => x.Path == S(ev, "instance"));
                    if (sn?.Row != null && rows.Contains(sn.Row)) list.ScrollIntoView(sn.Row);
                    break;
                case "running":
                {
                    int index = I(ev, "index"), total = Math.Max(I(ev, "total"), expected);
                    var name = S(ev, "test");
                    if (name != null && Find(S(ev, "instance"), name, true) is TestNode t && t.Status == "notrun")
                    {
                        t.Status = "running"; UpdateTest(t); UpdateSuite(suites.First(x => x.Path == t.Instance)); UpdateProject();
                    }
                    var rem = Get(ev, "remainingSec") is int r ? $" - about {Seconds(r)} left" : Get(ev, "remainingSec") is decimal rd ? $" - about {Seconds((double)rd)} left" : "";
                    progressText.Text = name != null ? $"Test {index} of {total}: {name}{rem}" : $"{index} of {total} test(s) finished{rem}";
                    break;
                }
                case "progress":
                {
                    // the project's own progress block (tcunit.json "progress") - shown as it is
                    var parts = new[] { "current", "total", "test", "remaining" }.Select(k => Get(ev, k)).ToList();
                    if (parts[1] != null)
                        progressText.Text = $"Test {Convert.ToInt32(parts[0] ?? 0)} of {Convert.ToInt32(parts[1])}: {parts[2]}" + (parts[3] != null ? $" - remaining {parts[3]}" : "");
                    break;
                }
                case "result":
                {
                    var t = Find(S(ev, "instance"), S(ev, "test"), true);
                    if (t == null) break;
                    t.Status = S(ev, "status"); t.Message = S(ev, "message");
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
                    progressText.Text = (st == "passed" ? "✔ " : st == "failed" ? "✖ " : "") + S(ev, "message");
                    progressText.Foreground = st == "passed" ? Green : st is "failed" or "error" or "timeout" or "refused" or "build" ? Red : null;
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
                solutionEvents.AfterClosing += () => { Watch(); Dispatcher.BeginInvoke(new Action(() => { Clear(); status.Text = "No solution open."; })); };
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
