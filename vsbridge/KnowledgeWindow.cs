// TcAgent Knowledge - tool window in Visual Studio/TwinCAT XAE Shell: everything the agent gets and where it comes from,
// and how the engineering workflows develop (docs/KNOWLEDGE_LAYERS.md, "Knowledge manager").
//
//   Overview    KPI tiles (runs, first-time-right, failed checks, corrections, minutes per run, gate stops - the last 10
//               runs against the 10 before, with a sparkline), the workflows, open improvement proposals, frequent errors
//   Files       coding rules, risk rules, skills, workflows, lessons and templates of every level with their status
//               (applies, replaced, locked, forced, switched off) - filter by kind and level; new (from a template), open,
//               open the folder, move to another level, delete (own levels), switch off / on - toolbar, context menu, Del;
//               per level kept in Git: local changes, commits behind/ahead, Fetch / Pull / Commit / Push
//   Sources     the Git sources and the items installed from them (pinned commit, license, changed locally, newer upstream)
//   History     per workflow failed checks and corrections over the runs, moving average, the adopted changes as markers;
//               the changes with their values before and after; export as CSV
//
// The window does no work itself - it starts TcAgent.exe knowledge json / disable / enable / new / move / delete / git
// (CliKnowledge.cs, KnowledgeOps.cs). Proposals, installing from sources and pulling sources go through the chat.
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
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polyline = System.Windows.Shapes.Polyline;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace TcAgentBridge
{
    [Guid("8d2b6f1a-4c3e-4e7a-b9d5-2f6e1c7a9b30")]
    public sealed class KnowledgePane : ToolWindowPane
    {
        public KnowledgePane() : base(null)
        {
            Caption = "TcAgent Knowledge";
            Content = new KnowledgeControl();
        }
    }

    public sealed class KnowledgeControl : UserControl
    {
        // ------------------------------------------------------------------ data (TcAgent.exe knowledge json)
        Dictionary<string, object> data;

        static IEnumerable<Dictionary<string, object>> Arr(object o) => (o as IEnumerable)?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>();
        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        static double? N(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : (double?)null;
        static bool B(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b && b;

        /// <summary>One line of a list - the columns bind to the dictionary of the JSON entry.</summary>
        public sealed class Line
        {
            public Dictionary<string, object> D;
            public string this[string k] => S(D, k);
            public string C1 { get; set; } public string C2 { get; set; } public string C3 { get; set; }
            public string C4 { get; set; } public string C5 { get; set; } public string C6 { get; set; } public string C7 { get; set; }
            public Brush Mark { get; set; }
            public string Tip { get; set; }
        }

        // ------------------------------------------------------------------ view
        static readonly Brush Green = TcUnitControl.Frozen(Color.FromRgb(0x2E, 0xA0, 0x43)), Red = TcUnitControl.Frozen(Color.FromRgb(0xD1, 0x34, 0x38)),
                              Blue = TcUnitControl.Frozen(Color.FromRgb(0x1E, 0x88, 0xE5)), Orange = TcUnitControl.Frozen(Color.FromRgb(0xE8, 0x8A, 0x1A)),
                              Gray = TcUnitControl.Frozen(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), TileBack = TcUnitControl.Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80));

        readonly Button refresh = TcUnitControl.Tool("⟳", null, "Refresh (read the knowledge and the run logs again)");
        readonly ToggleButton tOverview = Tab("Overview"), tEffective = Tab("Files"), tSources = Tab("Sources"), tHistory = Tab("History");
        readonly ContentControl page = new ContentControl();
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock platform = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };

        // overview
        readonly WrapPanel tiles = new WrapPanel { Margin = new Thickness(6) };
        readonly ListView workflows = NewList();
        readonly StackPanel proposals = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        readonly TextBlock facts = new TextBlock { Margin = new Thickness(8, 4, 8, 8), TextWrapping = TextWrapping.Wrap };
        // effective
        readonly ComboBox kindBox = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox onlyApplied = new CheckBox { Content = "Only what applies", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = false };
        readonly TextBox filter = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Filter by name, level or reason" };
        readonly ListView entries = NewList();
        readonly ComboBox levelBox = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Filter by level" };
        readonly Button newBtn = Text("New …", "A new rule, risk rule, skill, workflow or lesson from a template"),
                        folderBtn = Text("Open folder", "Show the file in the Explorer"),
                        moveBtn = Text("Move to level …", "Move the file to another level (shared levels in Git: as a pull request)"),
                        deleteBtn = Text("Delete", "Delete the file (own levels only; Del)");
        readonly StackPanel gitPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 2) };
        readonly Button open = Text("Open", "Open the file in the editor (or double-click)"), offProject = Text("Switch off for the project", "disabled.json of the project - the file stays unchanged"),
                        offUser = Text("Switch off for me", "disabled.json of your user level"), on = Text("Switch on", "Remove the entry from disabled.json (project and user)");
        readonly TextBox detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(6),
                                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        // sources
        readonly ListView sourceList = NewList(), installedList = NewList();
        readonly TextBlock sourcesHeading = Heading(""), installedHeading = Heading("");
        // history
        readonly ComboBox wfBox = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Canvas chart = new Canvas { Height = 220, Margin = new Thickness(8), ClipToBounds = true };
        readonly ListView changes = NewList();
        readonly Button export = Text("Export CSV", "The runs of this workflow and the changes as CSV");

        static ToggleButton Tab(string text)
        {
            var t = new ToggleButton { Content = new TextBlock { Text = text }, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 2, 0),
                                       Template = TcUnitControl.FlatToggle, Focusable = false, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            ((TextBlock)t.Content).SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        static Button Text(string text, string tip)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip,
                                 Template = TcUnitControl.FlatButton, Focusable = false, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            ((TextBlock)b.Content).SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ToolTipService.SetShowOnDisabled(b, true);
            return b;
        }

        static ListView NewList()
        {
            var l = new ListView { BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
            l.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            l.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var item = new Style(typeof(ListViewItem));
            item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            item.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Line.Tip))));
            l.ItemContainerStyle = item;
            return l;
        }

        /// <summary>Columns (header, width, property of Line); the first column can carry a colored mark.</summary>
        static void Columns(ListView l, params (string Header, double Width, string Prop)[] cols)
        {
            var g = new GridView { AllowsColumnReorder = false };
            var header = new Style(typeof(GridViewColumnHeader));
            header.Setters.Add(new Setter(Control.TemplateProperty, TcUnitControl.HeaderTemplate()));
            header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            g.ColumnHeaderContainerStyle = header;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = new GridViewColumn { Header = cols[i].Header, Width = cols[i].Width };
                if (i == 0)
                {
                    var sp = new FrameworkElementFactory(typeof(StackPanel));
                    sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                    var dot = new FrameworkElementFactory(typeof(TextBlock));
                    dot.SetValue(TextBlock.TextProperty, "●");
                    dot.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
                    dot.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Line.Mark)));
                    var txt = new FrameworkElementFactory(typeof(TextBlock));
                    txt.SetBinding(TextBlock.TextProperty, new Binding(cols[i].Prop));
                    sp.AppendChild(dot); sp.AppendChild(txt);
                    c.CellTemplate = new DataTemplate { VisualTree = sp };
                }
                else c.DisplayMemberBinding = new Binding(cols[i].Prop);
                g.Columns.Add(c);
            }
            l.View = g;
        }

        static TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 10, 8, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        public KnowledgeControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 2) };
            foreach (var e in new UIElement[] { refresh, TcUnitControl.Separator(), tOverview, tEffective, tSources, tHistory, platform }) bar.Children.Add(e);
            platform.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            facts.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            detail.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            root.Children.Add(page);
            Content = root;

            Columns(workflows, ("Workflow", 200, nameof(Line.C1)), ("Runs", 60, nameof(Line.C2)), ("First-time-right", 110, nameof(Line.C3)),
                    ("Failed checks / run", 150, nameof(Line.C4)), ("Corrections / run", 140, nameof(Line.C5)), ("Proposals", 80, nameof(Line.C6)));
            Columns(entries, ("Name", 220, nameof(Line.C1)), ("Kind", 100, nameof(Line.C2)), ("Level", 150, nameof(Line.C3)),
                    ("Status", 110, nameof(Line.C4)), ("Git", 80, nameof(Line.C7)), ("Why", 280, nameof(Line.C5)), ("Source / license", 200, nameof(Line.C6)));
            Columns(sourceList, ("Source", 180, nameof(Line.C1)), ("Level", 80, nameof(Line.C2)), ("Repository", 420, nameof(Line.C3)), ("Ref", 100, nameof(Line.C4)));
            Columns(installedList, ("Item", 240, nameof(Line.C1)), ("Level", 80, nameof(Line.C2)), ("State", 220, nameof(Line.C3)),
                    ("Source @ commit", 200, nameof(Line.C4)), ("License", 100, nameof(Line.C5)), ("Installed", 100, nameof(Line.C6)));
            Columns(changes, ("Change", 150, nameof(Line.C1)), ("Date", 90, nameof(Line.C2)), ("What", 360, nameof(Line.C3)),
                    ("Before", 110, nameof(Line.C4)), ("After (runs)", 130, nameof(Line.C5)), ("Result", 110, nameof(Line.C6)));

            kindBox.ItemsSource = new[] { "All kinds", "coding-rules", "risk-rules", "skills", "workflows", "lessons", "doc-templates" };
            kindBox.SelectedIndex = 0;
            kindBox.SelectionChanged += (s, e) => FillEntries();
            onlyApplied.Click += (s, e) => FillEntries();
            filter.TextChanged += (s, e) => FillEntries();
            onlyApplied.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            entries.SelectionChanged += (s, e) => ShowEntry();
            entries.MouseDoubleClick += (s, e) => OpenSelected();
            open.Click += (s, e) => OpenSelected();
            levelBox.SelectionChanged += (s, e) => FillEntries();
            newBtn.Click += (s, e) => NewItem();
            folderBtn.Click += (s, e) => OpenFolder();
            moveBtn.Click += (s, e) => MoveItem();
            deleteBtn.Click += (s, e) => DeleteItem();
            entries.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeleteItem(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
            entries.ContextMenu = EntryMenu();
            offProject.Click += (s, e) => Switch("disable", "project");
            offUser.Click += (s, e) => Switch("disable", "user");
            on.Click += (s, e) => { Switch("enable", "project"); Switch("enable", "user"); };
            workflows.MouseDoubleClick += (s, e) => { if (workflows.SelectedItem is Line l) { wfBox.SelectedItem = l.C1; Show(tHistory); } };
            wfBox.SelectionChanged += (s, e) => FillHistory();
            chart.SizeChanged += (s, e) => DrawChart();
            export.Click += (s, e) => ExportCsv();
            refresh.Click += (s, e) => Load();
            foreach (var t in new[] { tOverview, tEffective, tSources, tHistory }) t.Click += (s, e) => Show((ToggleButton)s);
            IsVisibleChanged += (s, e) => { if (IsVisible && data == null && !loading) Load(); };
            Show(tOverview);
            status.Text = "Reading the knowledge ...";
        }

        void Show(ToggleButton tab)
        {
            foreach (var t in new[] { tOverview, tEffective, tSources, tHistory }) t.IsChecked = t == tab;
            page.Content = tab == tOverview ? OverviewPage() : tab == tEffective ? EffectivePage() : tab == tSources ? SourcesPage() : (object)HistoryPage();
        }

        FrameworkElement overviewPage, effectivePage, sourcesPage, historyPage;

        FrameworkElement OverviewPage()
        {
            if (overviewPage != null) return overviewPage;
            var sp = new StackPanel();
            sp.Children.Add(tiles);
            sp.Children.Add(Heading("Workflows (double-click: history)"));
            sp.Children.Add(workflows);
            sp.Children.Add(Heading("Open improvement proposals (ask the agent: \"apply proposal <id>\")"));
            sp.Children.Add(proposals);
            sp.Children.Add(facts);
            return overviewPage = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        FrameworkElement EffectivePage()
        {
            if (effectivePage != null) return effectivePage;
            var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            foreach (var e in new UIElement[] { kindBox, levelBox, filter, onlyApplied, TcUnitControl.Separator(), newBtn, open, folderBtn, moveBtn, deleteBtn,
                                                TcUnitControl.Separator(), offProject, offUser, on }) tools.Children.Add(e);
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            var split = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            split.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(tools, 0); Grid.SetRow(gitPanel, 1); Grid.SetRow(entries, 2); Grid.SetRow(split, 3); Grid.SetRow(detail, 4);
            foreach (var e in new UIElement[] { tools, gitPanel, entries, split, detail }) g.Children.Add(e);
            return effectivePage = g;
        }

        FrameworkElement SourcesPage()
        {
            if (sourcesPage != null) return sourcesPage;
            var sp = new DockPanel();
            var h1 = sourcesHeading;
            DockPanel.SetDock(h1, Dock.Top); sp.Children.Add(h1);
            DockPanel.SetDock(sourceList, Dock.Top); sourceList.MaxHeight = 160; sp.Children.Add(sourceList);
            var h2 = installedHeading;
            DockPanel.SetDock(h2, Dock.Top); sp.Children.Add(h2);
            sp.Children.Add(installedList);
            return sourcesPage = sp;
        }

        FrameworkElement HistoryPage()
        {
            if (historyPage != null) return historyPage;
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 0) };
            var lbl = new TextBlock { Text = "Workflow:", Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var e in new UIElement[] { lbl, wfBox, export }) tools.Children.Add(e);
            var legend = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            legend.Inlines.Add(new System.Windows.Documents.Run("━ failed checks  ") { Foreground = Blue });
            legend.Inlines.Add(new System.Windows.Documents.Run("━ corrections  ") { Foreground = Orange });
            legend.Inlines.Add(new System.Windows.Documents.Run("┅ moving average (5)  ") { Foreground = Gray });
            legend.Inlines.Add(new System.Windows.Documents.Run("│ adopted change") { Foreground = Green });
            tools.Children.Add(legend);
            var dp = new DockPanel();
            DockPanel.SetDock(tools, Dock.Top); dp.Children.Add(tools);
            var frame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(6), Child = chart };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            DockPanel.SetDock(frame, Dock.Top); dp.Children.Add(frame);
            var h = Heading("Adopted changes - values per run (failed checks + corrections) before and over the next 10 runs");
            DockPanel.SetDock(h, Dock.Top); dp.Children.Add(h);
            dp.Children.Add(changes);
            return historyPage = dp;
        }

        // ------------------------------------------------------------------ load
        bool loading;

        internal void Load()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Load)); return; }
            if (loading) return;
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { status.Text = "TcAgent.exe not found - start Visual Studio 2022 once after installing the VSIX (or set TCAGENT_EXE)."; return; }
            var sln = TcUnitControl.SolutionPath();
            loading = true;
            status.Text = "Reading the knowledge ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                string json = null, err = null;
                try { json = Run(exe, "knowledge json" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)), out err); }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    loading = false;
                    try
                    {
                        data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json ?? "");
                        Fill();
                        status.Text = $"{(string.IsNullOrEmpty(sln) ? "No solution open - only the general levels" : Path.GetFileName(sln))} · TcAgent {S(data, "version")} · {DateTime.Now:HH:mm:ss}";
                    }
                    catch (Exception ex) { status.Text = "Could not read the knowledge: " + (string.IsNullOrWhiteSpace(err) ? ex.Message : err.Trim()); }
                }));
            });
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        static string Run(string exe, string args, out string err)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.EnvironmentVariables["TCAGENT_CLIENT"] = "window:knowledge";
            using (var p = Process.Start(psi))
            {
                var e = p.StandardError.ReadToEndAsync();
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                err = e.Result;
                return o;
            }
        }

        void Fill()
        {
            var pf = data.TryGetValue("platform", out var pv) ? pv as Dictionary<string, object> : null;
            var active = (pf?["active"] as IEnumerable)?.Cast<object>().Select(x => x.ToString()).ToList() ?? new List<string>();
            platform.Text = "Platform: " + (active.Count > 0 ? string.Join(", ", active) : "none") + (S(pf, "reason") is string r ? $" ({r})" : "");
            FillOverview();
            var keepLevel = levelBox.SelectedItem as string;
            var labels = new List<string> { "All levels" };
            labels.AddRange(Arr(data["entries"]).Select(e => S(e, "label")).Where(x => x != null).Distinct());
            levelBox.ItemsSource = labels;
            levelBox.SelectedItem = keepLevel != null && labels.Contains(keepLevel) ? keepLevel : "All levels";
            FillEntries();
            FillGit();
            FillSources();
            var names = Arr(data["workflows"]).Select(w => S(w, "name")).ToList();
            var keep = wfBox.SelectedItem as string;
            wfBox.ItemsSource = names;
            // default: the workflow with the most runs
            wfBox.SelectedItem = keep != null && names.Contains(keep) ? keep : Arr(data["workflows"]).OrderByDescending(w => N(w, "runs") ?? 0).Select(w => S(w, "name")).FirstOrDefault();
            FillHistory();
        }

        // ------------------------------------------------------------------ overview
        void FillOverview()
        {
            tiles.Children.Clear();
            var o = data["overview"] as Dictionary<string, object>;
            var series = Arr(o?["series"]).ToList();
            tiles.Children.Add(Tile("Runs", N(o, "runs")?.ToString("0") ?? "0", null, null, false, null, "all recorded runs of the project (the last 100 are kept)"));
            tiles.Children.Add(Tile("First-time-right", Pct(N(o, "firstTimeRight")), N(o, "firstTimeRight"), N(o, "firstTimeRightPrev"), true,
                                    series.Select(x => B(x, "firstTimeRight") ? 1.0 : 0.0).ToList(), "runs without a failed check and without a correction (last 10 runs)"));
            tiles.Children.Add(Tile("Failed checks / run", F(N(o, "failedChecks")), N(o, "failedChecks"), N(o, "failedChecksPrev"), false,
                                    series.Select(x => N(x, "failedChecks") ?? 0).ToList(), "tc_check / tc_build with compiler errors per run (last 10 runs)"));
            tiles.Children.Add(Tile("Corrections / run", F(N(o, "corrections")), N(o, "corrections"), N(o, "correctionsPrev"), false,
                                    series.Select(x => N(x, "corrections") ?? 0).ToList(), "your 'no' to a confirmation or an answer other than the recommended one"));
            tiles.Children.Add(Tile("Minutes / run", F(N(o, "minutes")), N(o, "minutes"), N(o, "minutesPrev"), false,
                                    series.Select(x => N(x, "minutes") ?? 0).ToList(), "duration of a workflow run"));
            tiles.Children.Add(Tile("Gate stops", N(o, "gateStops")?.ToString("0") ?? "0", N(o, "gateStops"), N(o, "gateStopsPrev"), false, null,
                                    "checkpoints not met and activations refused because of build errors (last 10 runs)"));

            var lines = new List<Line>();
            foreach (var w in Arr(data["workflows"]))
            {
                var chg = Change(N(w, "failedChecks"), N(w, "failedChecksPrev"), false);
                lines.Add(new Line
                {
                    D = w, C1 = S(w, "name"), C2 = S(w, "runs"), C3 = Pct(N(w, "firstTimeRight")), C4 = F(N(w, "failedChecks")) + (chg.Text.Length > 0 ? "   " + chg.Text : ""),
                    C5 = F(N(w, "corrections")), C6 = S(w, "proposals"), Mark = chg.Brush ?? Gray
                });
            }
            workflows.ItemsSource = lines;
            proposals.Children.Clear();
            foreach (var p in Arr(data["proposals"]))
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 4) };
                t.Inlines.Add(new System.Windows.Documents.Run($"[{S(p, "id")}] ") { FontFamily = new FontFamily("Consolas") });
                t.Inlines.Add(new System.Windows.Documents.Run(S(p, "title")) { FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run(" – " + S(p, "evidence")));
                t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                proposals.Children.Add(t);
            }
            if (proposals.Children.Count == 0) proposals.Children.Add(new TextBlock { Text = "None - the run logs, lessons and knowledge files show nothing to change.", Opacity = 0.7, Foreground = Foreground });
            var counts = data["counts"] as Dictionary<string, object>;
            var errors = Arr(data["topErrors"]).Select(e => $"{S(e, "code")} ({S(e, "count")}×)").ToList();
            facts.Text = "Applies here: " + string.Join(" · ", (counts ?? new Dictionary<string, object>()).Select(kv => $"{kv.Value} {kv.Key}")) +
                         (errors.Count > 0 ? "\nMost frequent compiler errors in failed checks: " + string.Join(", ", errors) : "") +
                         "\nRun logs are kept locally (%LOCALAPPDATA%\\TcAgent\\runs) - counts and names only, no code.";
        }

        static string F(double? v) => v?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
        static string Pct(double? v) => v == null ? "-" : v.Value.ToString("0", CultureInfo.InvariantCulture) + " %";

        /// <summary>Change against the previous 10 runs: arrow, value, green when better.</summary>
        static (string Text, Brush Brush) Change(double? now, double? prev, bool higherIsBetter)
        {
            if (now == null || prev == null) return ("", null);
            var d = now.Value - prev.Value;
            if (Math.Abs(d) < 0.05) return ("= previous", Gray);
            bool better = higherIsBetter ? d > 0 : d < 0;
            return ((d > 0 ? "▲ " : "▼ ") + Math.Abs(d).ToString("0.0", CultureInfo.InvariantCulture) + " vs. previous 10", better ? Green : Red);
        }

        Border Tile(string title, string value, double? now, double? prev, bool higherIsBetter, List<double> spark, string tip)
        {
            var sp = new StackPanel { Margin = new Thickness(10, 8, 10, 8), MinWidth = 150 };
            var t = new TextBlock { Text = title, Opacity = 0.8, FontSize = 11 };
            var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold };
            foreach (var x in new[] { t, v }) x.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            sp.Children.Add(t); sp.Children.Add(v);
            var (text, brush) = Change(now, prev, higherIsBetter);
            sp.Children.Add(new TextBlock { Text = text.Length > 0 ? text : prev == null && now != null ? "fewer than 20 runs" : " ", FontSize = 11, Foreground = brush ?? Gray });
            if (spark != null && spark.Count > 1) sp.Children.Add(Sparkline(spark.Skip(Math.Max(0, spark.Count - 30)).ToList(), 150, 26));
            var b = new Border { Child = sp, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Background = TileBack, ToolTip = tip };
            b.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return b;
        }

        static FrameworkElement Sparkline(List<double> values, double w, double h)
        {
            var c = new Canvas { Width = w, Height = h, Margin = new Thickness(0, 4, 0, 0) };
            double max = Math.Max(values.Max(), 1e-9), min = Math.Min(values.Min(), 0);
            var pl = new Polyline { Stroke = Blue, StrokeThickness = 1.5 };
            for (int i = 0; i < values.Count; i++)
                pl.Points.Add(new Point(i * w / (values.Count - 1), h - 2 - (values[i] - min) / (max - min + 1e-9) * (h - 4)));
            c.Children.Add(pl);
            return c;
        }

        // ------------------------------------------------------------------ effective
        void FillEntries()
        {
            if (data == null) return;
            var kind = kindBox.SelectedIndex > 0 ? kindBox.SelectedItem as string : null;
            var level = levelBox.SelectedIndex > 0 ? levelBox.SelectedItem as string : null;
            var q = filter.Text.Trim();
            var lines = new List<Line>();
            foreach (var e in Arr(data["entries"]))
            {
                if (kind != null && S(e, "kind") != kind) continue;
                if (level != null && S(e, "label") != level) continue;
                if (onlyApplied.IsChecked == true && !B(e, "active")) continue;
                var st = S(e, "status") + (B(e, "locked") ? ", locked" : "");
                var src = S(e, "source") is string so ? so + (S(e, "license") is string li ? " · " + li : "") : "";
                var l = new Line { D = e, C1 = S(e, "name"), C2 = S(e, "kind"), C3 = S(e, "label"), C4 = st, C5 = S(e, "reason") ?? "", C6 = src, C7 = S(e, "git") ?? "",
                                   Mark = B(e, "active") ? Green : S(e, "status") == "disabled" ? Orange : Gray, Tip = S(e, "path") };
                if (q.Length > 0 && !$"{l.C1} {l.C2} {l.C3} {l.C4} {l.C5} {l.C6}".ToLowerInvariant().Contains(q.ToLowerInvariant())) continue;
                lines.Add(l);
            }
            entries.ItemsSource = lines;
            ShowEntry();
        }

        void ShowEntry()
        {
            var l = entries.SelectedItem as Line;
            foreach (var b in new[] { open, offProject, offUser, on, folderBtn, moveBtn, deleteBtn }) b.IsEnabled = l != null;
            if (l == null) { detail.Text = "Select an entry. A later level replaces an entry with the same name; locked entries can only be switched off by the company level."; return; }
            var d = l.D;
            var sb = new StringBuilder($"{S(d, "key")} – {S(d, "label")} – {S(d, "status")}{(B(d, "locked") ? " (locked)" : "")}{(B(d, "force") ? " (force)" : "")}\n");
            if (S(d, "reason") is string r) sb.AppendLine("Why: " + r);
            if (S(d, "description") is string de) sb.AppendLine("Description: " + de);
            if (S(d, "source") is string so) sb.AppendLine($"Source: {so} · license {S(d, "license") ?? "unknown"}");
            sb.AppendLine("File: " + S(d, "path"));
            try { var p = S(d, "path"); if (File.Exists(p)) sb.AppendLine().Append(string.Join("\n", File.ReadLines(p).Take(40))); } catch { }
            detail.Text = sb.ToString();
            offProject.IsEnabled = l != null && !B(d, "locked") && !string.IsNullOrEmpty(S(data, "solutionDir"));
            offUser.IsEnabled = l != null && !B(d, "locked");
            deleteBtn.IsEnabled = LayerOf(d) is Dictionary<string, object> ly && B(ly, "deletable");
            if (S(d, "git") is string gs) detail.Text = detail.Text.Insert(detail.Text.IndexOf('\n') + 1, $"Git: {gs} - not committed yet\n");
        }

        // ------------------------------------------------------------------ new / open folder / move / delete
        Dictionary<string, object> LayerOf(Dictionary<string, object> e) => Arr(data?["layers"]).FirstOrDefault(l => S(l, "label") == S(e, "label"));

        ContextMenu EntryMenu()
        {
            var m = new ContextMenu();
            MenuItem Item(string header, Action act, Func<bool> enabled = null, string key = null)
            {
                var i = new MenuItem { Header = header, InputGestureText = key ?? "" };
                i.Click += (s, e) => act();
                if (enabled != null) m.Opened += (s, e) => i.IsEnabled = enabled();
                return i;
            }
            bool Sel() => entries.SelectedItem is Line;
            m.Items.Add(Item("Open", OpenSelected, Sel, "Enter"));
            m.Items.Add(Item("Open folder", OpenFolder, Sel));
            m.Items.Add(new Separator());
            m.Items.Add(Item("New …", NewItem));
            m.Items.Add(Item("Move to level …", MoveItem, Sel));
            m.Items.Add(Item("Delete", DeleteItem, () => entries.SelectedItem is Line l && LayerOf(l.D) is Dictionary<string, object> ly && B(ly, "deletable"), "Del"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Switch off for the project", () => Switch("disable", "project"), () => offProject.IsEnabled));
            m.Items.Add(Item("Switch off for me", () => Switch("disable", "user"), () => offUser.IsEnabled));
            m.Items.Add(Item("Switch on", () => { Switch("enable", "project"); Switch("enable", "user"); }, Sel));
            return m;
        }

        // the levels a file can be created at / moved to (built-in never; company only by moving - as a pull request)
        List<(string Label, string Level, bool Pr)> Levels(bool forNew) =>
            Arr(data?["layers"]).Where(l => S(l, "level") is string lv && lv != "built-in" && (!forNew || !B(l, "viaPullRequest") && lv != "company"))
                                .Select(l => (S(l, "label"), S(l, "level"), B(l, "viaPullRequest") || S(l, "level") == "company")).ToList();

        void NewItem()
        {
            var levels = Levels(true);
            if (levels.Count == 0) { status.Text = "No level to create files at."; return; }
            var kinds = new[] { "coding-rules", "risk-rules", "skills", "workflows", "lessons" };
            var r = Prompt("New knowledge file", ("Kind", kinds, kindBox.SelectedIndex > 0 && kinds.Contains(kindBox.SelectedItem as string) ? (string)kindBox.SelectedItem : "skills"),
                           ("Name", null, ""), ("Level", levels.Select(x => x.Label).ToArray(), levels.Any(x => x.Level == "project") ? levels.First(x => x.Level == "project").Label : levels[0].Label));
            if (r == null || r[1].Trim().Length == 0) return;
            var level = levels.First(x => x.Label == r[2]).Level;
            var (ok, output) = RunCli($"knowledge new --key {Quote(r[0] + "/" + r[1].Trim())} --level {Quote(level)}");
            status.Text = output;
            if (ok && File.Exists(output.Trim()))
                try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(output.Trim()); } catch { }
            Load();
        }

        void OpenFolder()
        {
            var p = (entries.SelectedItem as Line)?["path"];
            if (p == null) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + p + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        void MoveItem()
        {
            if (!(entries.SelectedItem is Line l)) return;
            var levels = Levels(false).Where(x => x.Label != S(l.D, "label")).ToList();
            if (levels.Count == 0) { status.Text = "No other level to move to."; return; }
            var r = Prompt($"Move {S(l.D, "key")} ({S(l.D, "label")})", ("To level", levels.Select(x => x.Label + (x.Pr ? "  (pull request)" : "")).ToArray(), null));
            if (r == null) return;
            var target = levels.First(x => r[0].StartsWith(x.Label));
            var (_, output) = RunCli($"knowledge move --path {Quote(S(l.D, "path"))} --level {Quote(target.Level)}");
            status.Text = output;
            Load();
        }

        void DeleteItem()
        {
            if (!(entries.SelectedItem is Line l)) return;
            if (!(LayerOf(l.D) is Dictionary<string, object> ly && B(ly, "deletable")))
            { status.Text = $"Files of the {S(l.D, "label")} level are not deleted here - switch it off instead."; return; }
            var what = S(l.D, "kind") is "skills" or "workflows" ? "the folder of " + S(l.D, "key") : S(l.D, "path");
            if (MessageBox.Show($"Delete {what}?\n\nThis cannot be undone (unless the level is kept in Git).", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (_, output) = RunCli($"knowledge delete --path {Quote(S(l.D, "path"))}");
            status.Text = output;
            Load();
        }

        // ------------------------------------------------------------------ git
        void FillGit()
        {
            gitPanel.Children.Clear();
            foreach (var l in Arr(data["layers"]))
            {
                if (!(l.TryGetValue("git", out var go) && go is Dictionary<string, object> g) || B(g, "same") || S(l, "level") == null) continue;
                var ch = (g.TryGetValue("changes", out var c) ? c as Dictionary<string, object> : null)?.Count ?? 0;
                int behind = (int)(N(g, "behind") ?? 0), ahead = (int)(N(g, "ahead") ?? 0);
                var row = new WrapPanel { Margin = new Thickness(0, 1, 0, 1) };
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                t.Inlines.Add(new System.Windows.Documents.Run($"{S(l, "label")} · {Path.GetFileName(S(g, "repo"))} ({S(g, "branch")}): ") { FontWeight = FontWeights.SemiBold });
                var parts = new List<string>();
                if (ch > 0) parts.Add($"{ch} local change(s) to commit");
                if (ahead > 0) parts.Add($"{ahead} commit(s) to push");
                if (behind > 0) parts.Add($"{behind} new commit(s) in the repository - pull");
                if (!B(g, "upstream")) parts.Add("no remote branch");
                t.Inlines.Add(new System.Windows.Documents.Run(parts.Count > 0 ? string.Join(" · ", parts) : "up to date (as of the last fetch)")
                                  { Foreground = behind > 0 || ch > 0 || ahead > 0 ? Orange : Green });
                t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                row.Children.Add(t);
                var level = S(l, "level"); bool pr = B(l, "viaPullRequest");
                foreach (var (label, action, tip) in new[] { ("Fetch", "fetch", "Look for new commits in the repository"), ("Pull", "pull", "Take over the new commits (fast-forward)"),
                                                            ("Commit …", "commit", "Commit the local changes of this level"),
                                                            ("Push", "push", pr ? "Push to a new branch - then open a pull request (shared level)" : "Push the current branch") })
                {
                    var b = Text(label, tip);
                    b.Click += (s, e) => GitAction(level, action);
                    b.IsEnabled = action switch { "pull" => B(g, "upstream"), "commit" => ch > 0, "push" => ahead > 0 || pr && ch == 0 && ahead > 0, _ => true };
                    row.Children.Add(b);
                }
                gitPanel.Children.Add(row);
            }
        }

        void GitAction(string level, string action)
        {
            string message = null;
            if (action == "commit")
            {
                var r = Prompt($"Commit the changes of {level}", ("Message", null, "Update knowledge"));
                if (r == null || r[0].Trim().Length == 0) return;
                message = r[0].Trim();
            }
            status.Text = $"git {action} ({level}) ...";
            var args = $"knowledge git --action {action} --level {Quote(level)}" + (message != null ? " --message " + Quote(message) : "");
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, output) = RunCli(args);
                Dispatcher.BeginInvoke(new Action(() => { status.Text = output; Load(); }));
            });
        }

        (bool ok, string output) RunCli(string args)
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) return (false, "TcAgent.exe not found.");
            var sln = TcUnitControl.SolutionPath();
            try
            {
                var psi = new ProcessStartInfo(exe, args + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                psi.EnvironmentVariables["TCAGENT_CLIENT"] = "window:knowledge";
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(120_000);
                    return (p.ExitCode == 0, (o + " " + err.Result).Trim());
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>A small dialog: one field per entry (choices = combo box, null = text box). Returns the values or null.</summary>
        string[] Prompt(string title, params (string Label, string[] Choices, string Value)[] fields)
        {
            var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 380, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var sp = new StackPanel { Margin = new Thickness(12) };
            var getters = new List<Func<string>>();
            foreach (var f in fields)
            {
                sp.Children.Add(new TextBlock { Text = f.Label, Margin = new Thickness(0, 6, 0, 2) });
                if (f.Choices != null)
                {
                    var cb = new ComboBox { ItemsSource = f.Choices, SelectedItem = f.Value ?? f.Choices.FirstOrDefault(), MinWidth = 340 };
                    sp.Children.Add(cb); getters.Add(() => cb.SelectedItem as string);
                }
                else { var tb = new TextBox { Text = f.Value ?? "", MinWidth = 340 }; sp.Children.Add(tb); getters.Add(() => tb.Text); }
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var okB = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancelB = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
            okB.Click += (s, e) => w.DialogResult = true;
            buttons.Children.Add(okB); buttons.Children.Add(cancelB);
            sp.Children.Add(buttons);
            w.Content = sp;
            return w.ShowDialog() == true ? getters.Select(g => g()).ToArray() : null;
        }

        void OpenSelected()
        {
            var p = (entries.SelectedItem as Line)?["path"];
            if (p == null || !File.Exists(p)) return;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(p);
            }
            catch (Exception ex) { status.Text = "Could not open the file: " + ex.Message; }
        }

        void Switch(string action, string level)
        {
            var key = (entries.SelectedItem as Line)?["key"];
            var exe = TcUnitControl.TcAgentExe();
            if (key == null || exe == null) return;
            var sln = TcUnitControl.SolutionPath();
            try
            {
                var o = Run(exe, $"knowledge {action} --key {Quote(key)} --level {level}" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)), out var err);
                status.Text = (o + " " + err).Trim();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            Load();
        }

        // ------------------------------------------------------------------ sources
        void FillSources()
        {
            sourcesHeading.Text = $"Sources ({Arr(data["sources"]).Count()}) - add, search, install, pull: ask the agent, e.g. \"which skills does source X have?\"";
            installedHeading.Text = $"Installed from sources ({Arr(data["installed"]).Count()}) - pinned in skills.lock.json of their level";
            sourceList.ItemsSource = Arr(data["sources"]).Select(s => new Line { D = s, C1 = S(s, "name"), C2 = S(s, "level"), C3 = S(s, "url"), C4 = S(s, "ref"), Mark = Blue }).ToList();
            installedList.ItemsSource = Arr(data["installed"]).Select(i =>
            {
                var st = S(i, "state") ?? "";
                return new Line
                {
                    D = i, C1 = S(i, "key"), C2 = S(i, "level"), C3 = st, C4 = S(i, "source") + " @" + (S(i, "commit") ?? "").Substring(0, Math.Min(8, (S(i, "commit") ?? "").Length)),
                    C5 = S(i, "license"), C6 = S(i, "installed"), Mark = st == "ok" ? Green : st.Contains("changed") || st.Contains("missing") ? Red : Orange, Tip = S(i, "url")
                };
            }).ToList();
        }

        // ------------------------------------------------------------------ history
        Dictionary<string, object> Workflow() => Arr(data?["workflows"]).FirstOrDefault(w => S(w, "name") == wfBox.SelectedItem as string);

        void FillHistory()
        {
            if (data == null) return;
            var wf = wfBox.SelectedItem as string;
            changes.ItemsSource = Arr(data["changes"]).Where(c => wf == null || S(c, "workflow") == null || S(c, "workflow") == wf).Select(c =>
            {
                var verdict = S(c, "verdict");
                var after = N(c, "runsAfter") > 0 ? $"{F((N(c, "failedChecksAfter") ?? 0) + (N(c, "correctionsAfter") ?? 0))} ({S(c, "runsAfter")} runs)" : "no runs yet";
                return new Line
                {
                    D = c, C1 = S(c, "id"), C2 = (S(c, "date") ?? "").Split('T')[0], C3 = $"{S(c, "kind")} {S(c, "step")}: {S(c, "description")}",
                    C4 = F((N(c, "beforeFailedChecks") ?? 0) + (N(c, "beforeCorrections") ?? 0)), C5 = after,
                    C6 = S(c, "status") == "reverted" ? "reverted" : verdict ?? "measuring", Tip = S(c, "target"),
                    Mark = S(c, "status") == "reverted" ? Gray : verdict == "helped" ? Green : verdict == "worse" ? Red : verdict == "no effect" ? Orange : Blue
                };
            }).ToList();
            DrawChart();
        }

        void DrawChart()
        {
            chart.Children.Clear();
            var w = Workflow();
            double W = chart.ActualWidth, H = chart.ActualHeight;
            if (w == null || W < 50 || H < 50)
            {
                if (data != null && w == null) chart.Children.Add(new TextBlock { Text = "No workflow runs yet - start a workflow in the chat (e.g. new-feature).", Margin = new Thickness(8), Foreground = Gray });
                return;
            }
            var runs = Arr(w["series"]).ToList();
            if (runs.Count == 0) return;
            const double padL = 34, padB = 22, padT = 8, padR = 8;
            var failed = runs.Select(r => N(r, "failedChecks") ?? 0).ToList();
            var corr = runs.Select(r => N(r, "corrections") ?? 0).ToList();
            double max = Math.Max(1, Math.Max(failed.Max(), corr.Max()));
            double X(int i) => padL + (runs.Count == 1 ? 0 : i * (W - padL - padR) / (runs.Count - 1));
            double Y(double v) => padT + (1 - v / max) * (H - padT - padB);
            var axis = new System.Windows.Shapes.Line { X1 = padL, Y1 = H - padB, X2 = W - padR, Y2 = H - padB, StrokeThickness = 1, Stroke = Gray };
            chart.Children.Add(axis);
            foreach (var v in new[] { 0, max / 2, max })
            {
                var lbl = new TextBlock { Text = v.ToString("0.#", CultureInfo.InvariantCulture), FontSize = 10, Foreground = Gray };
                Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, Y(v) - 7);
                chart.Children.Add(lbl);
                chart.Children.Add(new System.Windows.Shapes.Line { X1 = padL, X2 = W - padR, Y1 = Y(v), Y2 = Y(v), Stroke = TileBack, StrokeThickness = 1 });
            }
            // the adopted changes as vertical markers at the first run after them
            foreach (var c in Arr(data["changes"]).Where(c => S(c, "workflow") == S(w, "name")))
            {
                var date = S(c, "date");
                int idx = runs.FindIndex(r => string.CompareOrdinal(S(r, "t"), date) > 0);
                if (idx < 0) idx = runs.Count - 1;
                var x = X(idx) - (runs.Count > 1 ? (X(1) - X(0)) / 2 : 0);
                var reverted = S(c, "status") == "reverted";
                chart.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = padT, Y2 = H - padB, Stroke = reverted ? Gray : Green, StrokeThickness = 1.5, StrokeDashArray = reverted ? new DoubleCollection { 3, 3 } : null, ToolTip = $"{S(c, "id")}: {S(c, "description")}" });
                var t = new TextBlock { Text = S(c, "id"), FontSize = 9, Foreground = reverted ? Gray : Green };
                Canvas.SetLeft(t, x + 2); Canvas.SetTop(t, padT);
                chart.Children.Add(t);
            }
            Polyline Series(List<double> v, Brush b, double thick, DoubleCollection dash = null)
            {
                var pl = new Polyline { Stroke = b, StrokeThickness = thick, StrokeDashArray = dash };
                for (int i = 0; i < v.Count; i++) pl.Points.Add(new Point(X(i), Y(v[i])));
                return pl;
            }
            var avg = failed.Select((v, i) => failed.Skip(Math.Max(0, i - 4)).Take(Math.Min(5, i + 1)).Average()).ToList();
            chart.Children.Add(Series(avg, Gray, 1.5, new DoubleCollection { 4, 3 }));
            chart.Children.Add(Series(corr, Orange, 1.5));
            chart.Children.Add(Series(failed, Blue, 2));
            for (int i = 0; i < runs.Count; i++)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Blue, ToolTip = $"{S(runs[i], "t")?.Replace('T', ' ')}: {failed[i]} failed checks, {corr[i]} corrections, {S(runs[i], "minutes")} min" };
                Canvas.SetLeft(dot, X(i) - 2.5); Canvas.SetTop(dot, Y(failed[i]) - 2.5);
                chart.Children.Add(dot);
            }
            var first = new TextBlock { Text = (S(runs[0], "t") ?? "").Split('T')[0], FontSize = 10, Foreground = Gray };
            Canvas.SetLeft(first, padL); Canvas.SetTop(first, H - padB + 4);
            var last = new TextBlock { Text = (S(runs[runs.Count - 1], "t") ?? "").Split('T')[0] + $"  ({runs.Count} runs)", FontSize = 10, Foreground = Gray };
            last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(last, W - padR - last.DesiredSize.Width); Canvas.SetTop(last, H - padB + 4);
            chart.Children.Add(first); chart.Children.Add(last);
        }

        void ExportCsv()
        {
            var w = Workflow();
            if (w == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"tcagent-{S(w, "name")}-runs.csv", Filter = "CSV|*.csv" };
            if (dlg.ShowDialog() != true) return;
            var sb = new StringBuilder("time;failedChecks;corrections;minutes;firstTimeRight;aborted\r\n");
            foreach (var r in Arr(w["series"]))
                sb.Append($"{S(r, "t")};{S(r, "failedChecks")};{S(r, "corrections")};{S(r, "minutes")};{S(r, "firstTimeRight")};{S(r, "aborted")}\r\n");
            sb.Append("\r\nchange;date;kind;step;description;status;before;runsAfter;failedChecksAfter;correctionsAfter;verdict\r\n");
            foreach (var c in Arr(data["changes"]).Where(c => S(c, "workflow") == S(w, "name")))
                sb.Append($"{S(c, "id")};{S(c, "date")};{S(c, "kind")};{S(c, "step")};\"{(S(c, "description") ?? "").Replace("\"", "'")}\";{S(c, "status")};" +
                          $"{F((N(c, "beforeFailedChecks") ?? 0) + (N(c, "beforeCorrections") ?? 0))};{S(c, "runsAfter")};{S(c, "failedChecksAfter")};{S(c, "correctionsAfter")};{S(c, "verdict")}\r\n");
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            status.Text = "Exported: " + dlg.FileName;
        }
    }
}
