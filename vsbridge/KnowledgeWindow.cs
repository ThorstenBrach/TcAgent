// TcAgent Knowledge - tool window in Visual Studio/TwinCAT XAE Shell: everything the agent gets and where it comes from,
// and how the engineering workflows develop (docs/KNOWLEDGE_LAYERS.md, "Knowledge manager").
//
//   Overview    KPI tiles (runs, first-time-right, failed checks, corrections, minutes per run, gate stops - the last 10
//               runs against the 10 before, with a sparkline), the workflows, open improvement proposals, frequent errors
//   Files       coding rules, risk rules, skills, workflows, lessons and templates of every level with their status
//               (applies, replaced, locked, forced, switched off) - filter by kind and level; new (from a template), open,
//               open the folder, move to another level, delete (own levels), switch off / on - toolbar, context menu, Del;
//               per level kept in Git: local changes, commits behind/ahead, Fetch / Pull / Commit / Push
//   Sources     the Git sources (add, edit, remove, browse and install - license, files and scripts shown first) and the
//               items installed from them (pinned commit, license, changed locally, newer upstream; update, diff)
//   Platforms   the known platforms with their detection, which apply to the project; new, edit platform.md, set / detect
//   Feedback    the tc_feedback reports; the selected ones as an anonymized, editable issue text - copy, save, share with the
//               TcAgent developers (a new issue in the browser, sent by the user), set the status
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
        readonly ToggleButton tOverview = Tab("Overview"), tEffective = Tab("Files"), tSources = Tab("Sources"), tPlatforms = Tab("Platforms"), tDocs = Tab("Documentation"), tFeedback = Tab("Feedback"), tHistory = Tab("History");
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
                        addExistingBtn = Text("Add existing …", "Take an existing file (or skill/workflow folder) over to a level"),
                        renameBtn = Text("Rename …", "Rename the file or the skill/workflow folder (the name in its header follows)"),
                        copyPathBtn = Text("Copy path", "Copy the path of the file"),
                        folderBtn = Text("Open folder", "Show the file in the Explorer"),
                        moveBtn = Text("Move to level …", "Move the file to another level (shared levels in Git: as a pull request)"),
                        deleteBtn = Text("Delete …", "Delete the file (own levels only; Del)"),
                        levelsBtn = Text("Levels …", "Company, team and platform folders or repositories (settings.json)");
        readonly StackPanel gitPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 2) };
        readonly Button open = Text("Open", "Open the file in the editor (or double-click)"), offProject = Text("Switch off for the project", "disabled.json of the project - the file stays unchanged"),
                        offUser = Text("Switch off for me", "disabled.json of your user level"), on = Text("Switch on", "Remove the entry from disabled.json (project and user)");
        readonly TextBox detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(6),
                                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        // sources
        readonly ListView sourceList = NewList(), installedList = NewList();
        readonly TextBlock sourcesHeading = Heading(""), installedHeading = Heading("");
        readonly TextBox sourceDetail = new TextBox { IsReadOnly = true, Height = 160, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6),
                                                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        // platforms
        readonly ListView platformList = NewList();
        readonly TextBlock platformInfo = new TextBlock { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        // documentation sources (tc_docs)
        readonly ListView docsList = NewList();
        readonly TextBlock docsInfo = new TextBlock { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        string[] docsLevels = { "user" };
        readonly ListView docsHits = NewList();
        readonly TextBlock docsHitsHeading = Heading("Search like the agent: the hits appear here - double-click opens them");
        // feedback
        readonly ListView feedbackList = new ListView { BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
        readonly ComboBox feedbackStatus = new ComboBox { MinWidth = 100, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                                                          ItemsSource = new[] { "open", "all", "resolved", "wontfix", "duplicate" }, SelectedIndex = 0 };
        readonly TextBox feedbackText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(0, 1, 0, 0) };
        readonly TextBlock feedbackInfo = new TextBlock { Margin = new Thickness(6, 2, 6, 2), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        string feedbackUrl;
        // history
        readonly ComboBox wfBox = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Canvas chart = new Canvas { Height = 220, Margin = new Thickness(8), ClipToBounds = true };
        readonly ListView changes = NewList();
        readonly Button export = Text("Export CSV", "The runs of this workflow and the changes as CSV");
        // backups of the knowledge levels (KnowledgeBackup.cs)
        readonly ListView backupList = NewList();
        readonly TextBox backupDetail = new TextBox { IsReadOnly = true, Height = 170, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6),
                                                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        const string BackupLabel = "Backup …", RestoreLabel = "Restore …";
        readonly Button backupBtn = Text(BackupLabel, "Back up the knowledge of a level (or all) now - it is also backed up automatically before every change"),
                        restoreBtn = Text(RestoreLabel, "Restore a level, a file or a skill from a backup");
        List<Dictionary<string, object>> backupLevels = new List<Dictionary<string, object>>();
        EnvDTE.DocumentEvents docEvents;   // kept: the events object must live as long as the window

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

        // one list of actions per list: the toolbar and the context menu show the same entries with the same names
        // (null = separator; Enabled null = always)
        WrapPanel Bar(params (string Label, string Tip, Action Act, Func<bool> Enabled)[] acts)
        {
            var p = new WrapPanel { Margin = new Thickness(6, 0, 6, 4) };
            foreach (var a in acts)
            {
                if (a.Label == null) { p.Children.Add(TcUnitControl.Separator()); continue; }
                var b = Text(a.Label, a.Tip);
                b.Click += (s, e) => a.Act();
                p.Children.Add(b);
            }
            return p;
        }

        static ContextMenu MenuOf(params (string Label, string Tip, Action Act, Func<bool> Enabled)[] acts) =>
            Menu(acts.Select(a => (a.Label, a.Act, a.Enabled)).ToArray());

        static (string, string, Action, Func<bool>) Sep => (null, null, null, null);

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
            foreach (var e in new UIElement[] { refresh, TcUnitControl.Separator(), tOverview, tEffective, tSources, tPlatforms, tDocs, tFeedback, tHistory, TcUnitControl.Separator(), levelsBtn, backupBtn, restoreBtn, platform }) bar.Children.Add(e);
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
            newBtn.Click += (s, e) => NewItem(false);
            addExistingBtn.Click += (s, e) => NewItem(true);
            renameBtn.Click += (s, e) => RenameItem();
            copyPathBtn.Click += (s, e) => CopyPath((entries.SelectedItem as Line)?["path"]);
            levelsBtn.Click += (s, e) => EditLevels();
            backupBtn.Click += (s, e) => BackupNow();
            restoreBtn.Click += (s, e) => RestoreBackup();
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
            refresh.Click += (s, e) => { Load(); if (docsPage != null && tDocs.IsChecked == true) LoadDocs(); if (historyPage != null) LoadBackups(); };
            foreach (var t in new[] { tOverview, tEffective, tSources, tPlatforms, tDocs, tFeedback, tHistory }) t.Click += (s, e) => Show((ToggleButton)s);
            // the platform line next to the tabs leads to the Platforms tab
            platform.Cursor = Cursors.Hand;
            platform.ToolTip = "Platforms of this project - click to show and change them";
            platform.MouseLeftButtonUp += (s, e) => Show(tPlatforms);
            IsVisibleChanged += (s, e) => { if (IsVisible && data == null && !loading) Load(); };
            // a file of a knowledge level saved in the editor: read again (changed items from sources show their new state)
            try
            {
                docEvents = (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.Events.DocumentEvents;
                if (docEvents != null) docEvents.DocumentSaved += d =>
                {
                    try
                    {
                        var p = d?.FullName;
                        if (p == null || data == null || loading) return;
                        if (Arr(data.TryGetValue("layers", out var lo) ? lo : null).Any(l => S(l, "root") is string r && p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                            Load();
                    }
                    catch { }
                };
            }
            catch { }
            Show(tOverview);
            status.Text = "Reading the knowledge ...";
        }

        void Show(ToggleButton tab)
        {
            foreach (var t in new[] { tOverview, tEffective, tSources, tPlatforms, tDocs, tFeedback, tHistory }) t.IsChecked = t == tab;
            try
            {
                page.Content = tab == tOverview ? OverviewPage() : tab == tEffective ? EffectivePage() : tab == tSources ? SourcesPage()
                             : tab == tPlatforms ? PlatformsPage() : tab == tDocs ? DocsPage() : tab == tFeedback ? FeedbackPage() : (object)HistoryPage();
            }
            catch (Exception ex)
            {
                status.Text = "This page could not be shown: " + ex.Message;
                BridgePackage.Instance?.LogLine("Knowledge window: " + ex);
            }
        }

        FrameworkElement overviewPage, effectivePage, sourcesPage, historyPage, platformsPage, docsPage, feedbackPage;

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
            foreach (var e in new UIElement[] { kindBox, levelBox, filter, onlyApplied, TcUnitControl.Separator(), newBtn, addExistingBtn, TcUnitControl.Separator(),
                                                open, folderBtn, TcUnitControl.Separator(), renameBtn, moveBtn, TcUnitControl.Separator(), offProject, offUser, on,
                                                TcUnitControl.Separator(), deleteBtn, TcUnitControl.Separator(), copyPathBtn }) tools.Children.Add(e);
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

        // the names of the actions on an item installed from a source - the same in Files and Sources
        const string SrcCompare = "Compare with installed version", SrcChanges = "Changes in the source", SrcRevert = "Revert to installed version …",
                     SrcDetach = "Keep as own version …", SrcPropose = "Propose upstream (pull request) …", SrcMerge = "Update and merge …", SrcUninstall = "Uninstall …";

        FrameworkElement SourcesPage()
        {
            if (sourcesPage != null) return sourcesPage;
            var sp = new DockPanel();
            var h1 = sourcesHeading;
            DockPanel.SetDock(h1, Dock.Top); sp.Children.Add(h1);
            bool SSel() => sourceList.SelectedItem is Line;
            var srcActs = new (string, string, Action, Func<bool>)[] {
                ("New …", "A new source: a Git repository with skills, workflows, rules or lessons", () => EditSource(null), null),
                Sep,
                ("Open", "The repository in the browser (or its folder)", () => OpenUrlOrFolder(S((sourceList.SelectedItem as Line)?.D, "url")), SSel),
                ("Open folder", "The local copy of the repository (TcAgent's cache)", () => OpenSourceCache(S((sourceList.SelectedItem as Line)?.D, "name")), SSel),
                Sep,
                ("Edit …", "Change URL, branch/tag or level of the selected source", () => { if (SSel()) EditSource(sourceList.SelectedItem as Line); else Hint("Select a source first."); }, SSel),
                Sep,
                ("Delete …", "Delete the selected source from the list (installed items stay; Del)", RemoveSource, SSel),
                Sep,
                ("Copy path", "Copy the URL of the repository", () => CopyPath(S((sourceList.SelectedItem as Line)?.D, "url")), SSel),
                Sep,
                ("Browse and install …", "Show what the source offers and install items - license, files and scripts are shown first", BrowseSource, null) };
            var tools1 = Bar(srcActs);
            DockPanel.SetDock(tools1, Dock.Top); sp.Children.Add(tools1);
            DockPanel.SetDock(sourceList, Dock.Top); sourceList.MaxHeight = 160; sp.Children.Add(sourceList);
            var h2 = installedHeading;
            DockPanel.SetDock(h2, Dock.Top); sp.Children.Add(h2);
            bool ISel() => installedList.SelectedItem is Line;
            void On(Action<string, string> act) { var (n, lv) = Installed(); if (n != null) act(n, lv); }
            var instActs = new (string, string, Action, Func<bool>)[] {
                ("Open", "Open the installed file in the editor (or double-click)", () => OpenInEditor(S((installedList.SelectedItem as Line)?.D, "path")), ISel),
                ("Open folder", "Show the installed item in the Explorer", () => ShowInExplorer(S((installedList.SelectedItem as Line)?.D, "path")), ISel),
                Sep,
                (SrcCompare, "Your version and the installed one in the compare window of Visual Studio", () => On((n, lv) => CompareSource(n, lv, sourceDetail)), ISel),
                (SrcChanges, "What changed in the source since the install", DiffInstalled, ISel),
                Sep,
                (SrcRevert, "Back to the installed version (yours goes to the backups)", () => On(RevertSource), ISel),
                (SrcDetach, "No longer tied to the source - no updates, the origin stays in the header", () => On(DetachSource), ISel),
                (SrcPropose, "Your changes as a branch for the source - push it and open the pull request", () => On((n, lv) => ProposeSource(n, lv, sourceDetail)), ISel),
                (SrcMerge, "Merge the newer version of the source into your item (three-way; conflicts in the merge window)", () => On((n, lv) => MergeSource(n, lv, sourceDetail)), ISel),
                Sep,
                (SrcUninstall, "Remove the item from its level (a copy goes to the backups; the source stays; Del)", () => On((n, lv) => UninstallSource(n, lv, S((installedList.SelectedItem as Line)?.D, "state"))), ISel),
                Sep,
                ("Copy path", "Copy the path of the installed item", () => CopyPath(S((installedList.SelectedItem as Line)?.D, "path")), ISel),
                Sep,
                ("Check for updates", "Fetch the sources and list what is newer there", () => RunAsync("sources pull --dry-run", true), null),
                ("Update all …", "Update every installed item that is newer in its source (not the ones changed locally)", UpdateAll, null) };
            var tools2 = Bar(instActs);
            DockPanel.SetDock(tools2, Dock.Top); sp.Children.Add(tools2);
            DockPanel.SetDock(sourceDetail, Dock.Bottom); sp.Children.Add(sourceDetail);
            sp.Children.Add(installedList);
            sourceList.MouseDoubleClick += (s, e) => EditSource(sourceList.SelectedItem as Line);
            installedList.MouseDoubleClick += (s, e) => OpenInEditor(S((installedList.SelectedItem as Line)?.D, "path"));
            sourceList.ContextMenu = MenuOf(srcActs);
            sourceList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveSource(); e.Handled = true; } };
            installedList.ContextMenu = MenuOf(instActs);
            installedList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { On((n, lv) => UninstallSource(n, lv, S((installedList.SelectedItem as Line)?.D, "state"))); e.Handled = true; } };
            return sourcesPage = sp;
        }

        void OpenInEditor(string path)
        {
            if (string.IsNullOrEmpty(path)) { Hint("Select an entry first."); return; }
            var f = Directory.Exists(path) ? new[] { "SKILL.md", "WORKFLOW.md" }.Select(m => Path.Combine(path, m)).FirstOrDefault(File.Exists) ?? path : path;
            if (!File.Exists(f)) { ShowInExplorer(path); return; }
            try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(f); status.Text = "Opened: " + f; }
            catch (Exception ex) { Error($"Could not open {f}: {ex.Message}"); }
        }

        void ShowInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) { Hint("Select an entry first."); return; }
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
                else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
                else { Hint("Not found: " + path); return; }
                status.Text = "Explorer: " + path;
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        void OpenUrlOrFolder(string target)
        {
            if (string.IsNullOrEmpty(target)) { Hint("Select an entry first."); return; }
            if (Directory.Exists(target)) { ShowInExplorer(target); return; }
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); status.Text = "Opened: " + target; }
            catch (Exception ex) { Error($"Could not open {target}: {ex.Message}"); }
        }

        void OpenSourceCache(string name)
        {
            if (name == null) { Hint("Select a source first."); return; }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "sources", name);
            if (Directory.Exists(d)) ShowInExplorer(d); else Hint($"There is no local copy of {name} yet - \"Browse and install …\" fetches it.");
        }

        // a hint the user sees (the status line is easy to miss)
        void Hint(string text)
        {
            status.Text = text;
            MessageBox.Show(text, "TcAgent Knowledge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // an error is always a dialog
        void Error(string text)
        {
            status.Text = text.Split('\n')[0];
            MessageBox.Show(text, "TcAgent Knowledge - error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        void CopyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) { Hint("Select an entry first."); return; }
            try { Clipboard.SetText(path); status.Text = "Copied: " + path; } catch (Exception ex) { Error("Could not copy: " + ex.Message); }
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
            changes.MaxHeight = 160;
            DockPanel.SetDock(changes, Dock.Top); dp.Children.Add(changes);
            // backups of the knowledge levels
            var hb = Heading("Backups of the knowledge - made automatically before every change (last 20 per level; Git levels: uncommitted changes only)");
            DockPanel.SetDock(hb, Dock.Top); dp.Children.Add(hb);
            bool BSel() => backupList.SelectedItem is Line;
            var backupActs = new (string, string, Action, Func<bool>)[] {
                (BackupLabel, "Back up levels now - into a folder of your choice", BackupNow, null),
                (RestoreLabel, "The whole level, one file or one skill - the difference first, then you confirm; the current state is backed up before", RestoreBackup, null),
                ("Restore from a file …", "A backup ZIP from another folder or PC", RestoreFromFile, null),
                Sep,
                ("Open folder", "The folder with the backups (ZIP files with manifest.json)", OpenBackupFolder, null),
                ("Compare with current state", "What restoring the selected backup would change", () => DiffBackup(null), BSel),
                Sep,
                ("Delete …", "Delete the selected backup file (Del)", DeleteBackup, BSel),
                Sep,
                ("Copy path", "Copy the path of the backup file", () => CopyPath(S((backupList.SelectedItem as Line)?.D, "zip")), BSel) };
            var bt = Bar(backupActs);
            DockPanel.SetDock(bt, Dock.Top); dp.Children.Add(bt);
            Columns(backupList, ("Level", 160, nameof(Line.C1)), ("Created", 150, nameof(Line.C2)), ("Reason", 260, nameof(Line.C3)), ("Files", 60, nameof(Line.C4)),
                    ("Kind", 150, nameof(Line.C5)), ("TcAgent", 70, nameof(Line.C6)));
            backupList.ContextMenu = MenuOf(backupActs);
            backupList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeleteBackup(); e.Handled = true; } };
            backupList.SelectionChanged += (s, e) => { if (backupList.SelectedItem is Line l) backupDetail.Text = $"{S(l.D, "zip")}\n\n" + string.Join("\n", ((l.D["paths"] as IEnumerable) ?? new object[0]).Cast<object>()); };
            backupList.MouseDoubleClick += (s, e) => DiffBackup(null);
            backupDetail.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            DockPanel.SetDock(backupDetail, Dock.Bottom); dp.Children.Add(backupDetail);
            dp.Children.Add(backupList);
            LoadBackups();
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
                        try { Fill(); }
                        catch (Exception fx) { BridgePackage.Instance?.LogLine("Knowledge window, fill: " + fx); throw; }
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
            FillPlatforms();
            FillFeedback();
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
                var ss = S(e, "sourceState") ?? "";
                var (stateText, stateBrush) = SourceState(ss);
                if (stateText != null) src = stateText + " · " + src;
                else if (S(e, "detached") is string dt) src = "own version (from " + dt + ")";
                var l = new Line { D = e, C1 = S(e, "name"), C2 = S(e, "kind"), C3 = S(e, "label"), C4 = st, C5 = S(e, "reason") ?? "", C6 = src, C7 = S(e, "git") ?? "",
                                   Mark = B(e, "active") ? stateBrush ?? Green : S(e, "status") == "disabled" ? Orange : Gray, Tip = S(e, "path") };
                if (q.Length > 0 && !$"{l.C1} {l.C2} {l.C3} {l.C4} {l.C5} {l.C6}".ToLowerInvariant().Contains(q.ToLowerInvariant())) continue;
                lines.Add(l);
            }
            entries.ItemsSource = lines;
            ShowEntry();
        }

        /// <summary>The state of an item from a source: locally changed (orange), upstream newer (blue), both changed (red).</summary>
        static (string Text, Brush Mark) SourceState(string state)
        {
            bool mine = (state ?? "").Contains("changed locally"), theirs = (state ?? "").Contains("newer upstream");
            return mine && theirs ? ("⇅ both changed", Red) : mine ? ("✎ locally changed", Orange) : theirs ? ("↑ upstream newer", Blue)
                 : (state ?? "").StartsWith("missing") ? ("✗ missing", Red) : (null, null);
        }

        void ShowEntry()
        {
            var l = entries.SelectedItem as Line;
            foreach (var b in new[] { open, offProject, offUser, on, folderBtn, moveBtn, deleteBtn, renameBtn, copyPathBtn }) b.IsEnabled = l != null;
            if (l == null) { detail.Text = "Select an entry. A later level replaces an entry with the same name; locked entries can only be switched off by the company level."; return; }
            var d = l.D;
            var sb = new StringBuilder($"{S(d, "key")} – {S(d, "label")} – {S(d, "status")}{(B(d, "locked") ? " (locked)" : "")}{(B(d, "force") ? " (force)" : "")}\n");
            if (S(d, "reason") is string r) sb.AppendLine("Why: " + r);
            if (S(d, "description") is string de) sb.AppendLine("Description: " + de);
            if (S(d, "source") is string so) sb.AppendLine($"Source: {so} · license {S(d, "license") ?? "unknown"}" + (S(d, "sourceState") is string ss ? " · " + ss : ""));
            if ((S(d, "sourceState") ?? "").Contains("changed locally"))
                sb.AppendLine("Changed by you since the install - context menu: Show my changes, Merge the new version, Propose to the source (pull request).");
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

        /// <summary>A context menu; null in the list = separator. enabled is evaluated when the menu opens.</summary>
        static ContextMenu Menu(params (string Header, Action Act, Func<bool> Enabled)[] items)
        {
            var m = new ContextMenu();
            foreach (var it in items)
            {
                if (it.Header == null) { m.Items.Add(new Separator()); continue; }
                var i = new MenuItem { Header = it.Header };
                var act = it.Act; i.Click += (s, e) => act();
                if (it.Enabled != null) { var en = it.Enabled; m.Opened += (s, e) => i.IsEnabled = en(); }
                m.Items.Add(i);
            }
            return m;
        }

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
            m.Items.Add(Item("New …", () => NewItem(false)));
            m.Items.Add(Item("Add existing …", () => NewItem(true)));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Open", OpenSelected, Sel, "Enter"));
            m.Items.Add(Item("Open folder", OpenFolder, Sel));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Rename …", RenameItem, Sel));
            m.Items.Add(Item("Move to level …", MoveItem, Sel));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Switch off for the project", () => Switch("disable", "project"), () => offProject.IsEnabled));
            m.Items.Add(Item("Switch off for me", () => Switch("disable", "user"), () => offUser.IsEnabled));
            m.Items.Add(Item("Switch on", () => { Switch("enable", "project"); Switch("enable", "user"); }, Sel));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Delete …", DeleteItem, () => entries.SelectedItem is Line l && LayerOf(l.D) is Dictionary<string, object> ly && B(ly, "deletable"), "Del"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Copy path", () => CopyPath((entries.SelectedItem as Line)?["path"]), Sel));
            // items installed from a source: your changes, a newer version, back to the source
            bool FromSource() => S((entries.SelectedItem as Line)?.D, "sourceState") != null;
            (string, string) Item0() { var d = (entries.SelectedItem as Line)?.D; return (S(d, "name"), S(d, "installLevel") ?? (S(d, "label") ?? "").Split(' ')[0]); }
            m.Items.Add(new Separator());
            m.Items.Add(Item(SrcCompare, () => { var (n, lv) = Item0(); CompareSource(n, lv, detail); }, FromSource));
            m.Items.Add(Item(SrcChanges, () => { var (n, lv) = Item0(); RunAsyncTo($"sources diff --item {Quote(n)}", detail); }, FromSource));
            m.Items.Add(Item(SrcRevert, () => { var (n, lv) = Item0(); RevertSource(n, lv); }, FromSource));
            m.Items.Add(Item(SrcDetach, () => { var (n, lv) = Item0(); DetachSource(n, lv); }, FromSource));
            m.Items.Add(Item(SrcPropose, () => { var (n, lv) = Item0(); ProposeSource(n, lv, detail); }, FromSource));
            m.Items.Add(Item(SrcMerge, () => { var (n, lv) = Item0(); MergeSource(n, lv, detail); }, FromSource));
            m.Items.Add(Item(SrcUninstall, () => { var (n, lv) = Item0(); UninstallSource(n, lv, S((entries.SelectedItem as Line)?.D, "sourceState")); }, FromSource));
            return m;
        }

        // the levels a file can be created at / moved to: user, project, team(s), company, every platform (built-in never);
        // a shared level in Git gets the file in its working copy - commit and push in the Git bar (pull request)
        List<(string Label, string Level, bool Pr)> Levels(bool forNew) =>
            Arr(data?.TryGetValue("levels", out var lv) == true ? lv : null)
                .Select(l => (S(l, "label") + (B(l, "viaPullRequest") ? "  (Git: commit & push in the Git bar - pull request)" : ""), S(l, "level"), B(l, "viaPullRequest"))).ToList();

        void NewItem(bool existing)
        {
            var levels = Levels(true);
            if (levels.Count == 0) { Hint("No level to create files at."); return; }
            var kinds = new[] { "coding-rules", "risk-rules", "skills", "workflows", "lessons" };
            var kind0 = kindBox.SelectedIndex > 0 && kinds.Contains(kindBox.SelectedItem as string) ? (string)kindBox.SelectedItem : "skills";
            var level0 = levels.Any(x => x.Level == "project") ? levels.First(x => x.Level == "project").Label : levels[0].Label;
            var r = existing
                ? Prompt("Add an existing file", ("Kind", kinds, kind0), ("Level", levels.Select(x => x.Label).ToArray(), level0))
                : Prompt("New knowledge file", ("Kind", kinds, kind0), ("Name (other characters become hyphens)", null, ""), ("Level", levels.Select(x => x.Label).ToArray(), level0));
            if (r == null) return;
            r = existing ? new[] { r[0], null, "", r[1] } : new[] { r[0], null, r[1], r[2] };
            var level = levels.First(x => x.Label == r[3]).Level;
            if (levels.First(x => x.Label == r[3]).Pr)
                status.Text = "The file goes into the working copy of the shared repository - commit and push it in the Git bar.";
            string args;
            if (existing)
            {
                var skillLike = r[0] is "skills" or "workflows";
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = skillLike ? $"Pick the {(r[0] == "skills" ? "SKILL.md" : "WORKFLOW.md")} of the {r[0].TrimEnd('s')} (its folder is taken over)" : "Pick the Markdown file",
                    Filter = skillLike ? (r[0] == "skills" ? "SKILL.md|SKILL.md" : "WORKFLOW.md|WORKFLOW.md") : "Markdown|*.md"
                };
                if (dlg.ShowDialog() != true) return;
                args = $"knowledge new --key {Quote(r[0] + "/x")} --path {Quote(dlg.FileName)} --level {Quote(level)}";
            }
            else
            {
                if (r[2].Trim().Length == 0) { Hint("A name is needed for a new file."); return; }
                args = $"knowledge new --key {Quote(r[0] + "/" + r[2].Trim())} --level {Quote(level)}";
            }
            var (ok, output) = RunCli(args);
            Report(ok, output, "New");
            if (ok && File.Exists(output.Trim()))
                try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(output.Trim()); } catch { }
            Load();
        }

        // company, team and platform roots: settings.json "env" (TcAgent.exe reads it on every start; Set up clients passes it on)
        void EditLevels()
        {
            var st = data?.TryGetValue("settings", out var so) == true ? so as Dictionary<string, object> : null;
            var r = Prompt("Levels of the knowledge",
                           ("Company level (TCAGENT_COMPANY): a folder, network share or Git working copy", null, S(st, "TCAGENT_COMPANY") ?? ""),
                           ("Team level(s) (TCAGENT_TEAM): folder or Git working copy, several separated by ;", null, S(st, "TCAGENT_TEAM") ?? ""),
                           ("Platform repositories (TCAGENT_PLATFORMS), several separated by ; - optional", null, S(st, "TCAGENT_PLATFORMS") ?? ""));
            if (r == null) return;
            var names = new[] { "TCAGENT_COMPANY", "TCAGENT_TEAM", "TCAGENT_PLATFORMS" };
            var outs = new List<string>(); bool all = true;
            for (int i = 0; i < names.Length; i++)
                if ((r[i] ?? "").Trim() != (S(st, names[i]) ?? ""))
                {
                    var (ok, output) = RunCli($"knowledge settings --name {names[i]} --url {Quote(r[i].Trim())}");
                    all &= ok; outs.Add(output);
                }
            if (outs.Count == 0) return;
            Report(all, string.Join("\n", outs.Distinct()), "Levels");
            if (all) MessageBox.Show("Saved. The knowledge window uses the new levels at once.\n\nRun \"⋯ > Set up clients\" in the chat so the AI clients get them too (and start a new chat).",
                                     "TcAgent Knowledge", MessageBoxButton.OK, MessageBoxImage.Information);
            Load();
        }

        void RenameItem()
        {
            var d = (entries.SelectedItem as Line)?.D;
            if (d == null) { Hint("Select a file first."); return; }
            var r = Prompt("Rename " + S(d, "key"), ("New name (other characters become hyphens)", null, S(d, "name") ?? ""));
            if (r == null || r[0].Trim().Length == 0 || r[0].Trim() == S(d, "name")) return;
            var (ok, output) = RunCli($"knowledge rename --path {Quote(S(d, "path"))} --name {Quote(r[0].Trim())}");
            Report(ok, output, "Rename");
            Load();
        }

        void OpenFolder()
        {
            var p = (entries.SelectedItem as Line)?["path"];
            if (p == null) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + p + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Error(ex.Message); }
        }

        void MoveItem()
        {
            if (!(entries.SelectedItem is Line l)) return;
            var levels = Levels(false).Where(x => !x.Label.StartsWith(S(l.D, "label") + "  ") && x.Label != S(l.D, "label")).ToList();
            if (levels.Count == 0) { status.Text = "No other level to move to."; return; }
            var r = Prompt($"Move {S(l.D, "key")} ({S(l.D, "label")})", ("To level", levels.Select(x => x.Label).ToArray(), null));
            if (r == null) return;
            var target = levels.First(x => x.Label == r[0]);
            var (mok, output) = RunCli($"knowledge move --path {Quote(S(l.D, "path"))} --level {Quote(target.Level)}");
            Report(mok, output, "Move");
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
            var (dok, output) = RunCli($"knowledge delete --path {Quote(S(l.D, "path"))}");
            Report(dok, output, "Delete");
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

        /// <summary>The result of an action: success in the status line, a failure also as a dialog (it must not go unnoticed).</summary>
        void Report(bool ok, string output, string what)
        {
            status.Text = output.Split('\n')[0];
            if (!ok) MessageBox.Show(output, "TcAgent Knowledge - " + what, MessageBoxButton.OK, MessageBoxImage.Error);
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
            catch (Exception ex) { Error("Could not open the file: " + ex.Message); }
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
            catch (Exception ex) { Error(ex.Message); }
            Load();
        }

        // ------------------------------------------------------------------ sources
        void FillSources()
        {
            sourcesHeading.Text = $"Sources ({Arr(data["sources"]).Count()}) - Git repositories with skills, workflows, rules and lessons";
            installedHeading.Text = $"Installed from sources ({Arr(data["installed"]).Count()}) - pinned in skills.lock.json of their level";
            sourceList.ItemsSource = Arr(data["sources"]).Select(s => new Line { D = s, C1 = S(s, "name"), C2 = S(s, "level"), C3 = S(s, "url"), C4 = S(s, "ref"), Mark = Blue }).ToList();
            installedList.ItemsSource = Arr(data["installed"]).Select(i =>
            {
                var st = S(i, "state") ?? "";
                var (stateText, stateBrush) = SourceState(st);
                return new Line
                {
                    D = i, C1 = S(i, "key"), C2 = S(i, "level"), C3 = stateText ?? st, C4 = S(i, "source") + " @" + (S(i, "commit") ?? "").Substring(0, Math.Min(8, (S(i, "commit") ?? "").Length)),
                    C5 = S(i, "license"), C6 = S(i, "installed"), Mark = stateBrush ?? (st == "ok" ? Green : Orange), Tip = st + " · " + S(i, "url")
                };
            }).ToList();
        }

        // ------------------------------------------------------------------ sources: edit, browse and install, update, diff
        void RunAsync(string args, bool toDetail, bool reload = false)
        {
            status.Text = "TcAgent.exe " + args.Split(' ')[0] + " " + args.Split(' ')[1] + " ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, output) = RunCli(args);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (toDetail) sourceDetail.Text = output;
                    status.Text = output.Split('\n')[0];
                    if (reload) Load();
                }));
            });
        }

        // the levels sources can be defined at (sources.json): user and project
        string[] SourceLevels() => string.IsNullOrEmpty(S(data, "solutionDir")) ? new[] { "personal" } : new[] { "personal", "project" };

        void EditSource(Line l)
        {
            var d = l?.D;
            var r = Prompt(d == null ? "Add a source" : "Edit source " + S(d, "name"),
                           ("Name (lowercase, hyphens)", null, S(d, "name") ?? ""), ("Git URL or path", null, S(d, "url") ?? ""),
                           ("Branch or tag (empty = default)", null, S(d, "ref") == "HEAD" ? "" : S(d, "ref") ?? ""),
                           ("Defined for", SourceLevels(), S(d, "level") == "project" ? "project" : "personal"));
            if (r == null || r[0].Trim().Length == 0 || r[1].Trim().Length == 0) return;
            // a renamed or moved source: the old entry goes
            if (d != null && (S(d, "name") != r[0].Trim() || (S(d, "level") == "project" ? "project" : "personal") != r[3]))
                RunCli($"sources remove --name {Quote(S(d, "name"))} --level {(S(d, "level") == "project" ? "project" : "personal")}");
            var (aok, output) = RunCli($"sources add --name {Quote(r[0].Trim())} --url {Quote(r[1].Trim())} --level {r[3]}" + (r[2].Trim().Length > 0 ? " --ref " + Quote(r[2].Trim()) : ""));
            Report(aok, output, "Source");
            Load();
        }

        void RemoveSource()
        {
            if (!(sourceList.SelectedItem is Line l)) { status.Text = "Select a source first."; return; }
            if (MessageBox.Show($"Remove the source {S(l.D, "name")}?\n\nItems installed from it stay (and keep their pinned commit).", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            status.Text = RunCli($"sources remove --name {Quote(S(l.D, "name"))} --level {(S(l.D, "level") == "project" ? "project" : "personal")}").output;
            Load();
        }

        void BrowseSource()
        {
            var name = S((sourceList.SelectedItem as Line)?.D, "name");
            status.Text = "Reading " + (name ?? "all sources") + " (git fetch) ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, output) = RunCli("sources search" + (name != null ? " --name " + Quote(name) : ""));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok) { status.Text = output; return; }
                    List<Dictionary<string, object>> items;
                    try { items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<ArrayList>(output).OfType<Dictionary<string, object>>().ToList(); }
                    catch { status.Text = output; return; }
                    status.Text = $"{items.Count} item(s) offered.";
                    InstallDialog(items);
                }));
            });
        }

        void InstallDialog(List<Dictionary<string, object>> items)
        {
            var w = new Window { Title = "Install from a source", Width = 900, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                 Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var list = new ListView { SelectionMode = SelectionMode.Single };
            Columns(list, ("Name", 200, nameof(Line.C1)), ("Kind", 90, nameof(Line.C2)), ("Source", 110, nameof(Line.C3)), ("License", 110, nameof(Line.C4)),
                    ("Scripts", 70, nameof(Line.C5)), ("Description", 300, nameof(Line.C6)));
            list.ItemsSource = items.Select(i => new Line
            {
                D = i, C1 = S(i, "name"), C2 = S(i, "kind"), C3 = S(i, "source"), C4 = S(i, "license") + (S(i, "licenseClass") == "accepted" ? "" : " (" + S(i, "licenseClass") + ")"),
                C5 = ((IEnumerable)i["scripts"]).Cast<object>().Any() ? "yes" : "",
                C6 = S(i, "description"), Mark = S(i, "licenseClass") == "accepted" ? Green : S(i, "refusal") != null && S(i, "licenseClass") is "source-available" or "none" ? Red : Orange,
                Tip = S(i, "refusal") ?? S(i, "path")
            }).ToList();
            // every level: user, project, team, company, platforms - a shared level in Git gets a branch for a pull request
            var levelNames = Levels(true).Where(x => x.Level != null).Select(x => x.Level + (x.Pr ? "  (pull request)" : "")).ToList();
            if (levelNames.Count == 0) levelNames.Add("user");
            var level = new ComboBox { ItemsSource = levelNames, SelectedIndex = Math.Max(0, levelNames.FindIndex(x => x.StartsWith("user"))), MinWidth = 200, Margin = new Thickness(6, 0, 6, 0) };
            var install = new Button { Content = "Install …", MinWidth = 90, IsDefault = true };
            var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bar.Children.Add(new TextBlock { Text = "Install to:", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(level); bar.Children.Add(install); bar.Children.Add(close);
            var dp = new DockPanel(); DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar); dp.Children.Add(list);
            w.Content = dp;
            install.Click += (s, e) =>
            {
                if (!(list.SelectedItem is Line l)) return;
                var i = l.D;
                var cls = S(i, "licenseClass");
                if (cls is "source-available" or "none") { MessageBox.Show(w, S(i, "refusal"), "Not installed", MessageBoxButton.OK, MessageBoxImage.Stop); return; }
                var files = ((IEnumerable)i["files"]).Cast<object>().Select(x => x.ToString()).ToList();
                var scripts = ((IEnumerable)i["scripts"]).Cast<object>().Select(x => x.ToString()).ToList();
                var text = $"Install {S(i, "kind")} \"{S(i, "name")}\" from {S(i, "source")} @{(S(i, "commit") ?? "").Substring(0, Math.Min(8, (S(i, "commit") ?? "").Length))} to the {level.SelectedItem} level?{(((string)level.SelectedItem).Contains("pull request") ? " (a commit on a new branch of its repository - you push it and open the pull request)" : "")}\n\n" +
                           $"License: {S(i, "license")}{(cls == "accepted" ? "" : $" - {cls}: check it before you accept it")}\n" +
                           $"Files ({files.Count}): {string.Join(", ", files.Take(20))}{(files.Count > 20 ? " …" : "")}\n" +
                           (scripts.Count > 0 ? $"\nContains scripts: {string.Join(", ", scripts)}\nTcAgent never runs them - read them before you use the skill.\n" : "");
                if (MessageBox.Show(w, text, "Install", MessageBoxButton.YesNo, scripts.Count > 0 || cls != "accepted" ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var (ok, output) = RunCli($"sources install --name {Quote(S(i, "source"))} --item {Quote(S(i, "name"))} --kind {S(i, "kind")} --level {Quote(((string)level.SelectedItem).Split(' ')[0])}" + (cls == "accepted" ? "" : " --accept-license"));
                MessageBox.Show(w, output, ok ? "Installed" : "Not installed", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                if (ok) Load();
            };
            w.ShowDialog();
        }

        void UpdateAll()
        {
            status.Text = "Checking the sources ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, plan) = RunCli("sources pull --dry-run");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    sourceDetail.Text = plan;
                    if (plan.StartsWith("Everything is up to date")) { status.Text = plan; return; }
                    if (MessageBox.Show("Update these items from their sources?\n\n" + plan, "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    RunAsync("sources pull", true, reload: true);
                }));
            });
        }

        (string Name, string Level) Installed()
        {
            if (!(installedList.SelectedItem is Line l)) { Hint("Select an installed item first."); return (null, null); }
            return ((S(l.D, "key") ?? "").Split('/').Last(), S(l.D, "level"));
        }

        void SourceChanges(string name, string level, TextBox output)
        {
            if (name == null) return;
            output.Text = "Comparing with the installed version ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, o) = RunCli($"sources changes --item {Quote(name)} --level {Quote(level)}");
                Dispatcher.BeginInvoke(new Action(() => output.Text = o.Trim()));
            });
        }

        // a preview first (nothing changed), then the merge after asking; conflicts stay marked in the file
        void MergeSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            status.Text = $"Fetching the source of {name} ...";
            var (pok, preview) = RunCli($"sources merge --dry-run --item {Quote(name)} --level {Quote(level)}");
            output.Text = preview.Trim();
            if (preview.Contains("has no newer version")) { status.Text = preview.Trim(); return; }
            var conflicts = preview.Contains("Conflicts");
            if (!pok && !conflicts) { Report(false, preview, "Merge"); return; }
            if (MessageBox.Show($"Merge the new version of the source into your {name}?\n\n{preview.Trim()}\n\n" +
                                (conflicts ? "Conflicts open in the merge window of Visual Studio (or are marked with <<<<<<< in the file). " : "") +
                                "Your version is backed up first.", "TcAgent Knowledge", MessageBoxButton.YesNo,
                                conflicts ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, o) = RunCli($"sources merge --json --item {Quote(name)} --level {Quote(level)}");
            var lines = o.Split('\n').ToList();
            var cf = lines.FirstOrDefault(x => x.StartsWith("CONFLICT-FILES "));
            output.Text = string.Join("\n", lines.Where(x => !x.StartsWith("CONFLICT-FILES "))).Trim();
            status.Text = output.Text.Split('\n')[0];
            if (!ok && !o.Contains("Conflicts")) Report(false, o, "Merge");
            if (cf != null)
                try
                {
                    foreach (var c in Arr(new JavaScriptSerializer().DeserializeObject(cf.Substring("CONFLICT-FILES ".Length))))
                        OpenMergeTool(S(c, "theirs"), S(c, "yours"), S(c, "base"), S(c, "result"), S(c, "rel"));
                }
                catch (Exception ex) { Error("Merge window: " + ex.Message); }
            Load();
        }

        // Visual Studio's merge tool (vsDiffMerge /m): source = the new version, target = yours, base = the installed one,
        // result = your file (it holds the conflict markers until you save the merge)
        void OpenMergeTool(string theirs, string yours, string @base, string result, string rel)
        {
            string tool = null;
            try
            {
                var ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                tool = new[] { @"CommonExtensions\Microsoft\TeamFoundation\Team Explorer\vsDiffMerge.exe", "vsDiffMerge.exe" }.Select(p => Path.Combine(ide, p)).FirstOrDefault(File.Exists);
            }
            catch { }
            if (tool == null || theirs == null || yours == null || @base == null)
            {
                // no merge tool (e.g. the XAE Shell): the file with the conflict markers in the editor
                try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(result); } catch { }
                return;
            }
            Process.Start(new ProcessStartInfo(tool, $"/m \"{theirs}\" \"{yours}\" \"{@base}\" \"{result}\" \"Source (new): {rel}\" \"Yours: {rel}\" \"Installed: {rel}\" \"Result: {rel}\"")
                          { UseShellExecute = false });
        }

        // the compare window of Visual Studio for every file you changed (installed version left, yours right)
        void CompareSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            var (ok, o) = RunCli($"sources changes --json --item {Quote(name)} --level {Quote(level)}");
            if (!ok) { Report(false, o, "Diff"); return; }
            List<Dictionary<string, object>> files;
            try { files = Arr(new JavaScriptSerializer().DeserializeObject(o)).ToList(); } catch { output.Text = o; return; }
            if (files.Count == 0) { status.Text = $"{name}: no local changes."; return; }
            output.Text = string.Join("\n", files.Select(f => $"{S(f, "kind")}: {S(f, "rel")}"));
            var diff = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
            foreach (var f in files.Take(8))
            {
                var left = S(f, "installed") ?? EmptyFile(); var right = S(f, "yours") ?? EmptyFile();
                try
                {
                    if (diff != null)
                        diff.OpenComparisonWindow2(left, right, $"{name}: {S(f, "rel")}", $"{left} ↔ {right}", "Installed", "Yours", $"{name}: {S(f, "rel")}", null,
                                                   (uint)Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
                    else { output.Text = RunCli($"sources changes --item {Quote(name)} --level {Quote(level)}").output; break; }
                }
                catch (Exception ex) { Error("Compare: " + ex.Message); }
            }
            if (files.Count > 8) status.Text = $"{files.Count} files changed - the first 8 are open.";
        }

        static string EmptyFile() { var p = Path.Combine(Path.GetTempPath(), "tcagent-empty.txt"); if (!File.Exists(p)) File.WriteAllText(p, ""); return p; }

        void RevertSource(string name, string level)
        {
            if (name == null) return;
            if (MessageBox.Show($"Revert {name} to the installed version?\n\nYour changes are copied to %LOCALAPPDATA%\\TcAgent\\backups\\sources first (and the knowledge level is backed up).",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, o) = RunCli($"sources revert --item {Quote(name)} --level {Quote(level)}");
            Report(ok, o, "Revert");
            Load();
        }

        void DetachSource(string name, string level)
        {
            if (name == null) return;
            if (MessageBox.Show($"Keep {name} as your own version?\n\nIt is no longer updated from its source and no longer shown as changed; where it came from stays in its header (tcagent-source, tcagent-detached).",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, o) = RunCli($"sources detach --item {Quote(name)} --level {Quote(level)}");
            Report(ok, o, "Keep as own version");
            Load();
        }

        // a commit on a new branch of a working copy of the source, then - only if the user wants - the push and the PR page
        void ProposeSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            var r = Prompt($"Propose your changes of {name} to the source", ("What does your change improve? (the commit message, in the language of the source)", null, name + ": "));
            if (r == null || r[0].Trim().Length == 0) return;
            var (ok, o) = RunCli($"sources propose --item {Quote(name)} --level {Quote(level)} --message {Quote(r[0].Trim())}");
            if (!ok) { output.Text = o.Trim(); Report(false, o, "Propose"); return; }
            Dictionary<string, object> p;
            try { p = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(o); } catch { output.Text = o; return; }
            var sp = ((p.TryGetValue("specifics", out var so) ? so as IEnumerable : null) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToList();
            output.Text = $"Prepared: branch {S(p, "branch")} in {S(p, "folder")}\nFiles: {string.Join(", ", ((p.TryGetValue("files", out var fo) ? fo as IEnumerable : null) ?? new object[0]).Cast<object>())}\n" +
                          $"Patch (to send by hand): {S(p, "patch")}" + (sp.Count > 0 ? $"\n\nWARNING - private terms found: {string.Join(", ", sp)}" : "");
            if (sp.Count > 0 && MessageBox.Show($"The proposal contains terms from your private list: {string.Join(", ", sp)}\n\nThe source is public or shared - remove them first " +
                                                "(edit the item, propose again). Push anyway?", "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var push = Prompt("Push the proposal",
                              ("Push the branch to (the source, or the URL of your fork if you may not write to the source) - nothing is sent before OK, empty = do not push", null, S(p, "url")));
            if (push == null || push[0].Trim().Length == 0) { status.Text = "Not pushed - the branch and the patch are prepared."; return; }
            var (pushOk, po) = RunCli($"sources push --name {Quote(S(p, "source"))} --ref {Quote(S(p, "branch"))} --url {Quote(push[0].Trim())}");
            if (!pushOk) { Report(false, po, "Push"); return; }
            try
            {
                var pr = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(po);
                if (S(pr, "compareUrl") is string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); status.Text = "Pushed - the pull request page is open in the browser."; }
                else status.Text = "Pushed: " + S(p, "branch") + " - open the pull request / merge request on the server of the source.";
            }
            catch { status.Text = "Pushed."; }
        }

        void UninstallSource(string name, string level, string state)
        {
            if (name == null) return;
            var changed = (state ?? "").Contains("changed locally");
            if (MessageBox.Show($"Uninstall {name} from the {level} level?\n\nIts files are removed and the entry leaves skills.lock.json; a copy goes to " +
                                "%LOCALAPPDATA%\\TcAgent\\backups\\sources. The source itself stays." + (changed ? "\n\nYou changed this item - your changes are only in that copy afterwards." : ""),
                                "TcAgent Knowledge", MessageBoxButton.YesNo, changed ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = RunCli($"sources uninstall --item {Quote(name)} --level {Quote(level)}");
            Report(ok, output, "Uninstall");
            Load();
        }

        void RunAsyncTo(string args, TextBox output)
        {
            output.Text = "...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, o) = RunCli(args);
                Dispatcher.BeginInvoke(new Action(() => output.Text = o.Trim()));
            });
        }

        // ------------------------------------------------------------------ backups of the knowledge levels
        void LoadBackups()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, output) = RunCli("knowledgebase list --json");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok) { status.Text = "Backups: " + output.Trim(); return; }
                    try
                    {
                        backupLevels = Arr(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(output)).ToList();
                        backupList.ItemsSource = backupLevels.SelectMany(l => Arr(l["backups"]).Select(b =>
                        {
                            b["level"] = S(l, "level"); b["folder"] = S(l, "folder");
                            return new Line { D = b, C1 = S(l, "level"), C2 = (S(b, "created") ?? "").Replace('T', ' '), C3 = S(b, "reason"), C4 = Convert.ToString(b["files"]),
                                              C5 = B(b, "git") ? "uncommitted only (Git)" : "whole level", C6 = S(b, "version"), Mark = B(b, "git") ? Blue : Green, Tip = S(b, "zip") };
                        })).OrderByDescending(x => x.C2).ToList();
                    }
                    catch (Exception ex) { Error("Backups: " + ex.Message); }
                }));
            });
        }

        // Backup: the levels (all checked), the folder (TcAgent's backup folder, changeable), "open the folder afterwards"
        void BackupNow()
        {
            if (backupLevels.Count == 0) LoadBackupsNow();
            var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backups", "knowledge");
            var w = new Window { Title = "Backup of the knowledge", SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 520, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = "Levels:", Margin = new Thickness(0, 0, 0, 4) });
            var boxes = backupLevels.Select(l => new CheckBox { Content = $"{S(l, "level")}  ({S(l, "root")})", Tag = S(l, "level"), IsChecked = true, Margin = new Thickness(0, 1, 0, 1) }).ToList();
            foreach (var b in boxes) sp.Children.Add(b);
            if (boxes.Count == 0) sp.Children.Add(new TextBlock { Text = "No level found - open the window again (⟳).", Opacity = 0.7 });
            sp.Children.Add(new TextBlock { Text = "Folder (the backups of TcAgent are kept here; another folder gets a copy as well):", Margin = new Thickness(0, 10, 0, 2) });
            var folderRow = new DockPanel();
            var browse = new Button { Content = "Browse …", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
            var folder = new TextBox { Text = defaultFolder, MinWidth = 380 };
            DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse); folderRow.Children.Add(folder);
            browse.Click += (s, e) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder for the backup", SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dlg.SelectedPath;
            };
            sp.Children.Add(folderRow);
            sp.Children.Add(new TextBlock { Text = "Reason (optional):", Margin = new Thickness(0, 10, 0, 2) });
            var reason = new TextBox { Text = "by hand" };
            sp.Children.Add(reason);
            var openAfter = new CheckBox { Content = "Open the folder afterwards", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(openAfter);
            var ok = new Button { Content = "Back up", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 12, 6, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
            ok.Click += (s, e) => w.DialogResult = true;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bar.Children.Add(ok); bar.Children.Add(cancel); sp.Children.Add(bar);
            w.Content = sp;
            if (w.ShowDialog() != true) return;
            var chosen = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            if (chosen.Count == 0) { Hint("No level chosen - nothing backed up."); return; }
            var target = folder.Text.Trim();
            var copy = target.Length > 0 && !Path.GetFullPath(target).TrimEnd('\\').Equals(defaultFolder, StringComparison.OrdinalIgnoreCase) ? target : null;
            status.Text = "Backing up ...";
            var (rok, output) = RunCli($"knowledgebase backup --level {Quote(string.Join(",", chosen))} --message {Quote(reason.Text.Trim().Length > 0 ? reason.Text.Trim() : "by hand")}" +
                                       (copy != null ? " --out " + Quote(copy) : ""));
            backupDetail.Text = output.Trim();
            LoadBackups();
            if (!rok) { Report(false, output, "Backup"); return; }
            status.Text = "Backup done.";
            MessageBox.Show(output.Trim(), "TcAgent Knowledge - backup done", MessageBoxButton.OK, MessageBoxImage.Information);
            if (openAfter.IsChecked == true) ShowInExplorer(copy ?? defaultFolder);
        }

        void DeleteBackup()
        {
            var d = (backupList.SelectedItem as Line)?.D;
            var z = S(d, "zip");
            if (z == null) { Hint("Select a backup first."); return; }
            if (MessageBox.Show($"Delete this backup?\n\n{z}", "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { File.Delete(z); status.Text = "Deleted: " + z; } catch (Exception ex) { Error(ex.Message); }
            LoadBackups();
        }

        // the list of the backups for a dialog when the History tab was not open yet
        void LoadBackupsNow()
        {
            var (ok, output) = RunCli("knowledgebase list --json");
            if (ok) try { backupLevels = Arr(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(output)).ToList(); } catch { }
        }

        void DiffBackup(string item)
        {
            if (!(backupList.SelectedItem is Line l)) { Hint("Select a backup first."); return; }
            backupDetail.Text = "Comparing ...";
            var args = $"knowledgebase diff --level {Quote(S(l.D, "level"))} --from {Quote(S(l.D, "zip"))}" + (item != null ? " --item " + Quote(item) : "");
            System.Threading.Tasks.Task.Run(() =>
            {
                var (_, output) = RunCli(args);
                Dispatcher.BeginInvoke(new Action(() => backupDetail.Text = output.Trim()));
            });
        }

        // Restore: choose a backup (all levels, newest first), then what - the whole level, one skill/workflow folder or one file
        void RestoreBackup()
        {
            LoadBackupsNow();
            var all = backupLevels.SelectMany(l => Arr(l["backups"]).Select(b => { b["level"] = S(l, "level"); return b; }))
                                  .OrderByDescending(b => S(b, "created")).ToList();
            if (all.Count == 0) { Hint("There are no backups yet - they are made automatically before every change, or with \"" + BackupLabel + "\"."); return; }
            var preselect = (backupList.SelectedItem as Line)?.D;
            var w = new Window { Title = "Restore the knowledge", Width = 760, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                 Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var list = NewList();
            Columns(list, ("Level", 150, nameof(Line.C1)), ("Created", 150, nameof(Line.C2)), ("Reason", 260, nameof(Line.C3)), ("Files", 60, nameof(Line.C4)), ("Kind", 110, nameof(Line.C5)));
            list.ItemsSource = all.Select(b => new Line { D = b, C1 = S(b, "level"), C2 = (S(b, "created") ?? "").Replace('T', ' '), C3 = S(b, "reason"), C4 = Convert.ToString(b["files"]),
                                                          C5 = B(b, "git") ? "uncommitted (Git)" : "whole level", Mark = B(b, "git") ? Blue : Green, Tip = S(b, "zip") }).ToList();
            list.SelectedIndex = Math.Max(0, all.FindIndex(b => preselect != null && S(b, "zip") == S(preselect, "zip")));
            var ok = new Button { Content = "Restore …", MinWidth = 90, IsDefault = true };
            var file = new Button { Content = "From a file …", MinWidth = 100, Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
            Dictionary<string, object> chosen = null; bool fromFile = false;
            ok.Click += (s, e) => { chosen = (list.SelectedItem as Line)?.D; if (chosen != null) w.DialogResult = true; };
            list.MouseDoubleClick += (s, e) => { chosen = (list.SelectedItem as Line)?.D; if (chosen != null) w.DialogResult = true; };
            file.Click += (s, e) => { fromFile = true; w.DialogResult = true; };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bar.Children.Add(ok); bar.Children.Add(file); bar.Children.Add(cancel);
            var dp = new DockPanel();
            var hint = new TextBlock { Text = "Choose a backup - next you choose the whole level, one skill/workflow or one file, and see the difference before anything changes.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(hint, Dock.Top); dp.Children.Add(hint);
            DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
            dp.Children.Add(list);
            w.Content = dp;
            if (w.ShowDialog() != true) return;
            if (fromFile) { RestoreFromFile(); return; }
            RestoreFrom(S(chosen, "zip"), S(chosen, "level"), ((chosen["paths"] as IEnumerable) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToList(), S(chosen, "created"));
        }

        void RestoreFromFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A backup of the knowledge", Filter = "TcAgent knowledge backup|*.zip" };
            if (dlg.ShowDialog() != true) return;
            List<string> paths;
            try
            {
                using (var z = System.IO.Compression.ZipFile.OpenRead(dlg.FileName))
                    paths = z.Entries.Where(en => en.FullName.StartsWith("files/")).Select(en => en.FullName.Substring(6)).ToList();
            }
            catch (Exception ex) { Report(false, "Not a backup of TcAgent: " + ex.Message, "Restore"); return; }
            RestoreFrom(dlg.FileName, null, paths, Path.GetFileName(dlg.FileName));
        }

        // the whole level, one skill/workflow folder or one file: the difference first, then the confirmation
        void RestoreFrom(string zip, string level, List<string> paths, string label)
        {
            var folders = paths.Where(p => p.StartsWith("skills/") || p.StartsWith("workflows/") || p.StartsWith("platforms/"))
                               .Select(p => string.Join("/", p.Split('/').Take(2))).Distinct();
            var choices = new[] { "(the whole level)" }.Concat(folders).Concat(paths).ToArray();
            var r = Prompt($"Restore from {(level != null ? "the backup of " + level : "")} {label}", ("What", choices, choices[0]));
            if (r == null) return;
            var item = r[0] == choices[0] ? null : r[0];
            var common = (level != null ? $"--level {Quote(level)} " : "") + $"--from {Quote(zip)}" + (item != null ? " --item " + Quote(item) : "");
            var (dok, diff) = RunCli("knowledgebase diff " + common);
            backupDetail.Text = diff.Trim();
            if (!dok) { Report(false, diff, "Restore"); return; }
            if (diff.Contains(": 0 file(s) would change")) { Hint("Nothing to restore - it is the same as the backup."); return; }
            var head = string.Join("\n", diff.Split('\n').Where(x => x.StartsWith("[") || x.Contains("would change")).Take(25));
            if (MessageBox.Show($"Restore {(item ?? "the whole level")}?\n\n{head}\n\n(The full difference is in the History tab below the backups.) The current state is backed up first.",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = RunCli("knowledgebase restore " + common);
            backupDetail.Text = output.Trim();
            Report(ok, output, "Restore");
            if (ok) MessageBox.Show(output.Trim(), "TcAgent Knowledge - restored", MessageBoxButton.OK, MessageBoxImage.Information);
            Load(); LoadBackups();
        }

        void OpenBackupFolder()
        {
            var f = backupList.SelectedItem is Line l ? S(l.D, "folder") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backups", "knowledge");
            if (f != null) { Directory.CreateDirectory(f); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + f + "\"") { UseShellExecute = false }); }
        }

        void DiffInstalled()
        {
            if (!(installedList.SelectedItem is Line l)) { Hint("Select an installed item first."); return; }
            RunAsync("sources diff --item " + Quote((S(l.D, "key") ?? "").Split('/').Last()), true);
        }

        // ------------------------------------------------------------------ platforms
        FrameworkElement PlatformsPage()
        {
            if (platformsPage != null) return platformsPage;
            Columns(platformList, ("Platform", 180, nameof(Line.C1)), ("Applies here", 90, nameof(Line.C2)), ("Detected by libraries", 220, nameof(Line.C3)),
                    ("Detected by terms", 180, nameof(Line.C4)), ("Defined in", 360, nameof(Line.C5)));
            bool PSel() => platformList.SelectedItem is Line;
            var acts = new (string, string, Action, Func<bool>)[] {
                ("New …", "A platform of your own: name, description, the libraries and terms that detect it", NewPlatform, null),
                Sep,
                ("Open", "Its platform.md: description and detection of the selected platform (or double-click)", OpenPlatform, PSel),
                ("Open folder", "The folder of the platform (its coding rules, skills, lessons ...)", OpenPlatformFolder, PSel),
                Sep,
                ("Delete …", "Delete the selected platform of your own with its folder (Del)", DeletePlatform, PSel),
                Sep,
                ("Copy path", "Copy the path of its platform.md", () => CopyPath(S((platformList.SelectedItem as Line)?.D, "file")), PSel),
                Sep,
                ("Set for this project …", "Choose the platforms of this solution (stored in .tcagent\\config.json)", SetPlatforms, null),
                ("Detect automatically", "Remove the choice from .tcagent\\config.json - detection by libraries and terms again", () => { status.Text = RunCli("knowledge platform --action auto").output; Load(); }, null) };
            var tools = Bar(acts);
            tools.Margin = new Thickness(6, 4, 6, 4);
            platformInfo.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            platformList.MouseDoubleClick += (s, e) => OpenPlatform();
            platformList.ContextMenu = MenuOf(acts);
            platformList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeletePlatform(); e.Handled = true; } };
            var dp = new DockPanel();
            DockPanel.SetDock(tools, Dock.Top); dp.Children.Add(tools);
            DockPanel.SetDock(platformInfo, Dock.Top); dp.Children.Add(platformInfo);
            dp.Children.Add(platformList);
            return platformsPage = dp;
        }

        void FillPlatforms()
        {
            var pf = data.TryGetValue("platform", out var pv) ? pv as Dictionary<string, object> : null;
            var libs = ((data.TryGetValue("libraries", out var lo) ? lo as IEnumerable : null) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToList();
            platformInfo.Text = $"This project: {(Arr(data["platforms"]).Any(p => B(p, "active")) ? string.Join(", ", Arr(data["platforms"]).Where(p => B(p, "active")).Select(p => S(p, "name"))) : "no platform")} - " +
                                $"{S(pf, "reason")}{(B(data, "platformConfigured") ? " (chosen by hand)" : "")}\nLibraries of the project: {(libs.Count > 0 ? string.Join(", ", libs) : "none found")}";
            string J(Dictionary<string, object> p, string k) => string.Join(", ", ((p.TryGetValue(k, out var v) ? v as IEnumerable : null) ?? new object[0]).Cast<object>());
            platformList.ItemsSource = Arr(data["platforms"]).Select(p => new Line
            {
                D = p, C1 = S(p, "name"), C2 = B(p, "active") ? "yes" : "", C3 = J(p, "libraries"), C4 = J(p, "terms"), C5 = S(p, "file") ?? J(p, "roots"),
                Mark = B(p, "active") ? Green : Gray, Tip = S(p, "description")
            }).ToList();
        }

        void NewPlatform()
        {
            var st = data?.TryGetValue("settings", out var so) == true ? so as Dictionary<string, object> : null;
            var where = string.IsNullOrEmpty(S(st, "TCAGENT_COMPANY")) ? new[] { "user (only for you)" } : new[] { "user (only for you)", "company (for everybody - Git bar: pull request)" };
            var r = Prompt("New platform", ("Name (e.g. framework-a - other characters become hyphens)", null, ""), ("Description", null, ""),
                           ("Detected by libraries (comma-separated, * allowed)", null, ""), ("Detected by terms in the project (comma-separated)", null, ""),
                           ("Defined at", where, where[0]));
            if (r == null || r[0].Trim().Length == 0) return;
            var (ok, output) = RunCli($"knowledge platform --action new --name {Quote(r[0].Trim())} --message {Quote(r[1])} --libraries {Quote(r[2])} --terms {Quote(r[3])} --level {(r[4].StartsWith("company") ? "company" : "user")}");
            Report(ok, output, "New platform");
            if (ok && File.Exists(output.Trim())) try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(output.Trim()); } catch { }
            Load();
        }

        void OpenPlatform()
        {
            var f = S((platformList.SelectedItem as Line)?.D, "file");
            if (f == null || !File.Exists(f)) { status.Text = "This platform has no platform.md of its own (only a folder) - create one with New …"; return; }
            try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(f); } catch (Exception ex) { Error(ex.Message); }
        }

        // only a platform folder of the user (or of the company folder without Git) - the delete of KnowledgeOps checks it
        void DeletePlatform()
        {
            var d = (platformList.SelectedItem as Line)?.D;
            var folder = S(d, "file") is string f ? Path.GetDirectoryName(f) : null;
            if (folder == null) { status.Text = "This platform has no folder of its own to delete."; return; }
            if (MessageBox.Show($"Delete the platform {S(d, "name")} with everything in its folder?\n\n{folder}\n\n(rules, skills and lessons of the platform go too)",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = RunCli("knowledge delete --path " + Quote(folder));
            Report(ok, output, "Delete platform");
            Load();
        }

        void OpenPlatformFolder()
        {
            var d = (platformList.SelectedItem as Line)?.D;
            var folder = S(d, "file") is string f ? Path.GetDirectoryName(f) : ((d?["roots"] as IEnumerable)?.Cast<object>().FirstOrDefault()?.ToString());
            if (folder != null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false });
        }

        void SetPlatforms()
        {
            if (string.IsNullOrEmpty(S(data, "solutionDir"))) { status.Text = "No solution open."; return; }
            var all = Arr(data["platforms"]).ToList();
            var w = new Window { Title = "Platforms of this project", SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 320, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = "The project is built on:", Margin = new Thickness(0, 0, 0, 6) });
            var boxes = all.Select(p => new CheckBox { Content = S(p, "name") + (S(p, "description") is string de ? " – " + de : ""), Tag = S(p, "name"), IsChecked = B(p, "active"), Margin = new Thickness(0, 2, 0, 2) }).ToList();
            foreach (var b in boxes) sp.Children.Add(b);
            if (boxes.Count == 0) sp.Children.Add(new TextBlock { Text = "No platforms defined - create one with New … first.", Opacity = 0.7 });
            var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 12, 6, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
            ok.Click += (s, e) => w.DialogResult = true;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bar.Children.Add(ok); bar.Children.Add(cancel); sp.Children.Add(bar);
            w.Content = sp;
            if (w.ShowDialog() != true) return;
            var names = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            status.Text = RunCli("knowledge platform --action set --name " + Quote(names.Count > 0 ? string.Join(",", names) : "none")).output;
            Load();
        }

        // ------------------------------------------------------------------ documentation: what tc_docs looks things up in
        FrameworkElement DocsPage()
        {
            if (docsPage != null) { LoadDocs(); return docsPage; }
            Columns(docsList, ("Source", 380, nameof(Line.C1)), ("Type", 120, nameof(Line.C2)), ("Level", 130, nameof(Line.C3)),
                    ("State", 190, nameof(Line.C4)), ("Comment", 200, nameof(Line.C5)), ("Defined in", 320, nameof(Line.C6)));
            Columns(docsHits, ("Hit", 300, nameof(Line.C1)), ("Where", 420, nameof(Line.C2)), ("Text", 600, nameof(Line.C3)));
            bool DSel() => docsList.SelectedItem is Line;
            var acts = new (string, string, Action, Func<bool>)[] {
                ("Add link or folder …", "A web address (only pages below it are read), a folder or a network share - one line in the knowledge.md of a level", () => EditDoc(null), null),
                ("Add documents …", "Copy PDF, Word, HTML, Markdown or text files into the knowledge\\ folder of a level - the agent searches them", CopyDocs, null),
                Sep,
                ("Open", "The link in the browser, the folder in the Explorer (or double-click / Enter)", OpenDoc, DSel),
                ("Open folder", "The folder in the Explorer (document folders, folders, shares)", () => { var d = (docsList.SelectedItem as Line)?.D; if (S(d, "type") is "web" or "link") Hint("A link has no folder - Open shows it in the browser."); else ShowInExplorer(S(d, "value")); }, DSel),
                ("Open knowledge.md", "The list of links and folders of the level of the selected source (created if it does not exist yet)", OpenDocOrigin, null),
                Sep,
                ("Edit …", "Change the selected link or folder, its comment or its level", () => { if (DSel()) EditDoc(docsList.SelectedItem as Line); else Hint("Select a link or folder first."); }, DSel),
                Sep,
                ("Delete …", "Delete the selected link or folder from its knowledge.md (the documents themselves stay; Del)", RemoveDoc, DSel),
                Sep,
                ("Copy path", "Copy the link or the path", () => CopyPath(S((docsList.SelectedItem as Line)?.D, "value")), DSel),
                Sep,
                ("Search like the agent …", "The same search as tc_docs - the hits are listed below and open with a double-click", SearchDocs, null) };
            var tools = Bar(acts);
            tools.Margin = new Thickness(6, 4, 6, 4);
            docsInfo.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            docsInfo.Text = "What the agent looks things up in (tc_docs): Beckhoff InfoSys, the links and folders listed in the knowledge.md of every level, and the " +
                            "documents in their knowledge\\ folders. Links: only pages below them are read. Read only - the agent never changes these sources.";
            docsList.MouseDoubleClick += (s, e) => OpenDoc();
            docsList.ContextMenu = MenuOf(acts);
            docsList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveDoc(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenDoc(); e.Handled = true; } };
            bool HSel() => docsHits.SelectedItem is Line;
            docsHits.MouseDoubleClick += (s, e) => OpenHit();
            docsHits.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { OpenHit(); e.Handled = true; } };
            docsHits.ContextMenu = Menu(("Open", OpenHit, HSel), ("Open folder", OpenHitFolder, () => HSel() && S((docsHits.SelectedItem as Line)?.D, "kind") == "file"),
                                        (null, null, null), ("Copy path", () => CopyPath(S((docsHits.SelectedItem as Line)?.D, "where")), HSel));
            docsHits.Height = 190;
            var bottom = new DockPanel();
            DockPanel.SetDock(docsHitsHeading, Dock.Top); bottom.Children.Add(docsHitsHeading);
            bottom.Children.Add(docsHits);
            var dp = new DockPanel();
            DockPanel.SetDock(tools, Dock.Top); dp.Children.Add(tools);
            DockPanel.SetDock(docsInfo, Dock.Top); dp.Children.Add(docsInfo);
            DockPanel.SetDock(bottom, Dock.Bottom); dp.Children.Add(bottom);
            dp.Children.Add(docsList);
            LoadDocs();
            return docsPage = dp;
        }

        // folders are counted with a time limit (network shares) - in the background
        void LoadDocs()
        {
            status.Text = "Reading the documentation sources ...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, output) = RunCli("knowledge docs");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok) { status.Text = "Documentation sources: " + output.Trim(); return; }
                    try
                    {
                        var r = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                        docsLevels = ((r.TryGetValue("levels", out var lv) ? lv as IEnumerable : null) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToArray();
                        var types = new Dictionary<string, string> { ["web"] = "Beckhoff InfoSys", ["link"] = "Link", ["folder"] = "Folder", ["share"] = "Network share", ["documents"] = "Document folder" };
                        var list = Arr(r["sources"]).ToList();
                        docsList.ItemsSource = list.Select(d => new Line
                        {
                            D = d, C1 = S(d, "value"), C2 = types.TryGetValue(S(d, "type") ?? "", out var t) ? t : S(d, "type"), C3 = S(d, "level"), C4 = S(d, "state"),
                            C5 = S(d, "note"), C6 = S(d, "origin"),
                            Mark = d.TryGetValue("reachable", out var re) && re is bool b ? (b ? (S(d, "type") == "documents" && Convert.ToInt32(d["documents"]) == 0 ? Gray : Green) : Red)
                                 : S(d, "type") is "web" or "link" ? Blue : Orange,
                            Tip = S(d, "raw") != S(d, "value") ? S(d, "raw") + " → " + S(d, "value") : S(d, "value")
                        }).ToList();
                        status.Text = $"{list.Count} documentation source(s) · {list.Where(d => S(d, "type") is "folder" or "share" or "documents").Sum(d => Convert.ToInt32(d["documents"]))} document(s) in folders";
                    }
                    catch (Exception ex) { Error("Documentation sources: " + ex.Message); }
                }));
            });
        }

        void EditDoc(Line l)
        {
            var d = l?.D;
            if (d != null && !(S(d, "origin") ?? "").EndsWith("knowledge.md", StringComparison.OrdinalIgnoreCase))
            {
                if (S(d, "type") == "documents") { Hint("A document folder: its documents are what the agent searches. It opens now - add or delete documents there (or use \"Add documents …\")."); OpenDoc(); }
                else Hint(S(d, "type") == "web" ? "Beckhoff InfoSys is built in and cannot be changed." : "This source comes from the variable TCAGENT_KNOWLEDGE (settings.json) - change it there.");
                return;
            }
            var levels = docsLevels.Length > 0 ? docsLevels : new[] { "user" };
            var r = DocSourceDialog(d == null ? "Add link or folder" : "Edit link or folder", S(d, "raw"), S(d, "note"),
                                    S(d, "levelName") ?? (levels.Contains("project") ? "project" : levels[0]), levels, S(d, "type"));
            if (r == null || r[0].Trim().Length == 0) return;
            if (d != null)
            {
                var (rok, rout) = RunCli($"knowledge docs --action remove --path {Quote(S(d, "origin"))} --url {Quote(S(d, "raw"))}");
                if (!rok) { Report(false, rout, "Edit documentation source"); return; }
            }
            var (ok, output) = RunCli($"knowledge docs --action add --level {Quote(r[2])} --url {Quote(r[0].Trim())}" + (r[1].Trim().Length > 0 ? " --message " + Quote(r[1].Trim()) : ""));
            Report(ok, output, d == null ? "Add documentation source" : "Edit documentation source");
            LoadDocs();
        }

        // a link or a folder: for a folder a folder dialog (also for network shares), for a link the address
        string[] DocSourceDialog(string title, string value, string note, string level, string[] levels, string type)
        {
            var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 560, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow, ShowInTaskbar = false };
            var sp = new StackPanel { Margin = new Thickness(12) };
            var isLink = new RadioButton { Content = "Link (web address - only pages below it are read)", GroupName = "kind", Margin = new Thickness(0, 0, 0, 2) };
            var isFolder = new RadioButton { Content = "Folder or network share (its documents are searched)", GroupName = "kind", Margin = new Thickness(0, 0, 0, 8) };
            var folderLike = type is "folder" or "share" || value != null && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase) && value.Length > 0;
            (folderLike ? isFolder : isLink).IsChecked = true;
            sp.Children.Add(isLink); sp.Children.Add(isFolder);
            var label = new TextBlock { Margin = new Thickness(0, 0, 0, 2) };
            sp.Children.Add(label);
            var row = new DockPanel();
            var browse = new Button { Content = "Browse …", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
            var box = new TextBox { Text = value ?? "", MinWidth = 420 };
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box);
            sp.Children.Add(row);
            void Kind()
            {
                var folder = isFolder.IsChecked == true;
                label.Text = folder ? "Folder (C:\\…) or network share (\\\\server\\share\\…):" : "Address (https://…):";
                browse.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
                if (!folder && box.Text.Length == 0) box.Text = "https://";
                if (folder && box.Text == "https://") box.Text = "";
            }
            isLink.Checked += (s, e) => Kind(); isFolder.Checked += (s, e) => Kind();
            Kind();
            browse.Click += (s, e) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder with documents for the agent", ShowNewFolderButton = false,
                                                                               SelectedPath = Directory.Exists(box.Text) ? box.Text : "" })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) box.Text = dlg.SelectedPath;
            };
            sp.Children.Add(new TextBlock { Text = "Comment (optional):", Margin = new Thickness(0, 8, 0, 2) });
            var comment = new TextBox { Text = note ?? "" };
            sp.Children.Add(comment);
            sp.Children.Add(new TextBlock { Text = "Level (its knowledge.md):", Margin = new Thickness(0, 8, 0, 2) });
            var lv = new ComboBox { ItemsSource = levels, SelectedItem = levels.Contains(level) ? level : levels[0] };
            sp.Children.Add(lv);
            var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 12, 6, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
            ok.Click += (s, e) =>
            {
                var v = box.Text.Trim();
                if (isLink.IsChecked == true && !(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) || v == "https://")
                { MessageBox.Show(w, "A link starts with https:// (or http://).", title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (isFolder.IsChecked == true && !(v.StartsWith(@"\\") || System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z]:\\") || v.StartsWith("%")))
                { MessageBox.Show(w, "Choose a folder (Browse …) or type a path like C:\\Manuals or \\\\server\\share\\manuals.", title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
                w.DialogResult = true;
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bar.Children.Add(ok); bar.Children.Add(cancel); sp.Children.Add(bar);
            w.Content = sp;
            return w.ShowDialog() == true ? new[] { box.Text.Trim(), comment.Text.Trim(), lv.SelectedItem as string } : null;
        }

        void RemoveDoc()
        {
            var d = (docsList.SelectedItem as Line)?.D;
            if (d == null) return;
            if (!(S(d, "origin") ?? "").EndsWith("knowledge.md", StringComparison.OrdinalIgnoreCase))
            { Hint(S(d, "type") == "documents" ? "A document folder cannot be removed - open it and delete the documents you do not want." : S(d, "type") == "web" ? "Beckhoff InfoSys is built in." : "This source comes from TCAGENT_KNOWLEDGE (settings.json) - change it there."); return; }
            if (MessageBox.Show($"Remove this source from {S(d, "origin")}?\n\n{S(d, "raw")}\n\n(the documents themselves stay where they are)", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = RunCli($"knowledge docs --action remove --path {Quote(S(d, "origin"))} --url {Quote(S(d, "raw"))}");
            Report(ok, output, "Remove documentation source");
            LoadDocs();
        }

        void CopyDocs()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Documents for the agent",
                                                           Filter = "Documents|*.pdf;*.docx;*.html;*.htm;*.md;*.txt;*.xml;*.csv;*.json;*.st|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            var levels = docsLevels.Length > 0 ? docsLevels : new[] { "user" };
            var r = Prompt("Copy documents in", ("Into the knowledge\\ folder of the level", levels, levels.Contains("project") ? "project" : levels[0]));
            if (r == null) return;
            var (ok, output) = RunCli($"knowledge docs --action copy --level {Quote(r[0])} --url {Quote(string.Join("|", dlg.FileNames))}");
            Report(ok, output, "Copy documents");
            LoadDocs();
        }

        void OpenDoc()
        {
            var d = (docsList.SelectedItem as Line)?.D;
            var v = S(d, "value");
            if (v == null) return;
            try
            {
                if (S(d, "type") is "web" or "link") Process.Start(new ProcessStartInfo(v) { UseShellExecute = true });
                else
                {
                    if (S(d, "type") == "documents") Directory.CreateDirectory(v);
                    if (Directory.Exists(v)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + v + "\"") { UseShellExecute = false });
                    else status.Text = "Not reachable: " + v;
                }
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        void OpenDocOrigin()
        {
            var d = (docsList.SelectedItem as Line)?.D;
            var f = S(d, "origin");
            if (f == null || !f.EndsWith("knowledge.md", StringComparison.OrdinalIgnoreCase))
            {
                // the level of the selected source, otherwise a level to choose
                var level = S(d, "levelName");
                if (level == null)
                {
                    var r = Prompt("Open knowledge.md", ("The list of links and folders of the level", docsLevels.Length > 0 ? docsLevels : new[] { "user" }, docsLevels.Contains("project") ? "project" : docsLevels.FirstOrDefault() ?? "user"));
                    if (r == null) return;
                    level = r[0];
                }
                var root = Arr(data?.TryGetValue("layers", out var lo) == true ? lo : null).Where(x => S(x, "level") == level).Select(x => S(x, "root")).FirstOrDefault()
                           ?? (level == "user" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent") : null);
                if (root == null) { Hint($"The folder of the level {level} is not known - open the window again (⟳)."); return; }
                f = Path.Combine(root, "knowledge.md");
            }
            try
            {
                if (!File.Exists(f))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.WriteAllText(f, "# Knowledge sources for TcAgent (read only)\r\n# One per line: - <URL or folder/network drive>  - comment\r\n", new UTF8Encoding(false));
                }
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null) dte.ItemOperations.OpenFile(f);
                else Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
                status.Text = "Opened: " + f + " - save it and click ⟳ to see the change.";
            }
            catch (Exception ex) { Report(false, $"Could not open {f}: {ex.Message}", "Open knowledge.md"); }
        }

        void SearchDocs()
        {
            var r = Prompt("Search like the agent", ("Words from the documents (e.g. EtherCAT diagnosis) - the same search as tc_docs", null, ""));
            if (r == null || r[0].Trim().Length == 0) return;
            docsHitsHeading.Text = $"Searching for \"{r[0].Trim()}\" ...";
            docsHits.ItemsSource = null;
            var q = r[0].Trim();
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, output) = RunCli("knowledge docs --action search --json --query " + Quote(q));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok) { docsHitsHeading.Text = "Search: " + output.Trim(); return; }
                    try
                    {
                        var res = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                        var hits = Arr(res["hits"]).ToList();
                        docsHits.ItemsSource = hits.Select(h => new Line
                        {
                            D = h, C1 = (S(h, "title") ?? S(h, "where")) + (h.TryGetValue("page", out var pg) && Convert.ToInt32(pg) > 0 ? $" (page {pg})" : ""), C2 = S(h, "where"), C3 = S(h, "snippet"),
                            Mark = S(h, "kind") == "file" ? Green : Blue, Tip = S(h, "snippet")
                        }).ToList();
                        docsHitsHeading.Text = hits.Count == 0 ? $"Nothing found for \"{q}\" in the documents and the pages read so far."
                                                               : $"{hits.Count} hit(s) for \"{q}\" - double-click opens the document or page";
                    }
                    catch (Exception ex) { docsHitsHeading.Text = "Search: " + ex.Message; }
                }));
            });
        }

        // a document with its program (a .md or .txt in the editor, a PDF on the page of the hit), a page in the browser
        void OpenHit()
        {
            var h = (docsHits.SelectedItem as Line)?.D;
            var w = S(h, "where");
            if (w == null) { Hint("Select a hit first."); return; }
            var page = h.TryGetValue("page", out var pv) ? Convert.ToInt32(pv) : 0;
            try
            {
                if (S(h, "kind") == "file" && Path.GetExtension(w).ToLowerInvariant() is ".md" or ".txt" or ".st" or ".xml" or ".json" or ".csv")
                    OpenInEditor(w);
                else if (S(h, "kind") == "file" && w.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && page > 0) OpenPdf(w, page);
                else { Process.Start(new ProcessStartInfo(w) { UseShellExecute = true }); status.Text = "Opened: " + w; }
            }
            catch (Exception ex) { Error($"Could not open {w}: {ex.Message}"); }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern uint AssocQueryString(uint flags, uint str, string assoc, string extra, StringBuilder output, ref uint size);

        // the PDF program of Windows on the page: Acrobat/Reader and Foxit (/A page=n), SumatraPDF (-page n),
        // browsers (#page=n); any other program opens the file at its start
        void OpenPdf(string file, int page)
        {
            string exe = null;
            try { var sb = new StringBuilder(1024); uint n = 1024; if (AssocQueryString(0, 2, ".pdf", "open", sb, ref n) == 0) exe = sb.ToString(); } catch { }
            var name = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
            if (exe != null && File.Exists(exe))
            {
                string args = name.Contains("acro") || name.Contains("foxit") ? $"/A \"page={page}\" \"{file}\""
                            : name.Contains("sumatra") ? $"-page {page} \"{file}\""
                            : name is "msedge" or "chrome" or "firefox" or "brave" ? $"\"{new Uri(file).AbsoluteUri}#page={page}\"" : null;
                if (args != null) { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); status.Text = $"Opened on page {page}: {file}"; return; }
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            status.Text = $"Opened: {file} (the hit is on page {page})";
        }

        void OpenHitFolder()
        {
            var w = S((docsHits.SelectedItem as Line)?.D, "where");
            ShowInExplorer(w);
        }

        // ------------------------------------------------------------------ feedback: see, anonymize, edit, share
        FrameworkElement FeedbackPage()
        {
            if (feedbackPage != null) return feedbackPage;
            Columns(feedbackList, ("Report", 110, nameof(Line.C1)), ("Status", 80, nameof(Line.C2)), ("Severity / type", 140, nameof(Line.C3)),
                    ("Tool", 140, nameof(Line.C4)), ("Summary", 420, nameof(Line.C5)), ("Version · date", 150, nameof(Line.C6)));
            feedbackList.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            feedbackList.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var item = new Style(typeof(ListViewItem));
            item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            feedbackList.ItemContainerStyle = item;
            feedbackStatus.SelectionChanged += (s, e) => FillFeedback();
            feedbackList.SelectionChanged += (s, e) => PrepareIssue();
            bool FSel() => feedbackList.SelectedItems.Count > 0;
            var acts = new (string, string, Action, Func<bool>)[] {
                ("Share with the TcAgent developers …", "Opens a new issue in the browser with the text below - you check it there and send it yourself", ShareIssue, FSel),
                ("Copy text", "Copy the text below", () => { try { Clipboard.SetText(feedbackText.Text); status.Text = "Copied."; } catch (Exception ex) { Error(ex.Message); } }, FSel),
                ("Save as …", "Save the text below as a Markdown file", SaveIssue, FSel),
                Sep,
                ("Set status …", "resolved / wontfix / duplicate / open for the selected reports", SetFeedbackStatus, FSel),
                ("Anonymize again", "Build the text of the selected reports again (names, paths and addresses replaced)", PrepareIssue, FSel),
                Sep,
                ("Copy diagnostics", "Versions of TcAgent, the extension, TwinCAT and the IDEs for the Diagnostics field of an issue", () => CopyDiagnostics(t => status.Text = t), null) };
            feedbackList.ContextMenu = MenuOf(acts);
            feedbackText.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            feedbackText.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            feedbackInfo.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var tools = Bar(acts);
            tools.Margin = new Thickness(6, 4, 6, 4);
            tools.Children.Insert(0, feedbackStatus);
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
            var split = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            split.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(tools, 0); Grid.SetRow(feedbackList, 1); Grid.SetRow(split, 2); Grid.SetRow(feedbackInfo, 3); Grid.SetRow(feedbackText, 4);
            foreach (var e in new UIElement[] { tools, feedbackList, split, feedbackInfo, feedbackText }) g.Children.Add(e);
            feedbackInfo.Text = "Select one or more reports. The text below is anonymized and can be edited before you share it - check it for names of customers, machines and projects.";
            return feedbackPage = g;
        }

        void FillFeedback()
        {
            if (data == null) return;
            var st = feedbackStatus.SelectedItem as string ?? "open";
            feedbackList.ItemsSource = Arr(data.TryGetValue("feedback", out var fo) ? fo : null).Where(f => st == "all" || S(f, "status") == st).Select(f => new Line
            {
                D = f, C1 = S(f, "id"), C2 = S(f, "status"), C3 = $"{S(f, "severity") ?? "-"} / {S(f, "type") ?? "-"}", C4 = S(f, "tool"), C5 = S(f, "summary"),
                C6 = $"{S(f, "version")} · {(S(f, "time") ?? "").Split('T')[0]}",
                Mark = S(f, "status") == "open" ? (S(f, "severity") is "blocker" or "major" ? Red : Orange) : Green, Tip = S(f, "summary")
            }).ToList();
        }

        void PrepareIssue()
        {
            var ids = feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "id")).ToList();
            if (ids.Count == 0) { feedbackText.Text = ""; return; }
            var (ok, output) = RunCli("knowledge feedback --action issue --key " + Quote(string.Join(",", ids)));
            if (!ok) { feedbackText.Text = output; return; }
            try
            {
                var r = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                feedbackText.Text = WithEnvironment(S(r, "text"));
                feedbackUrl = S(r, "url");
                var repl = ((r.TryGetValue("replaced", out var rv) ? rv as IEnumerable : null) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToList();
                feedbackInfo.Text = (repl.Count > 0 ? $"Replaced automatically ({repl.Count}): {string.Join(", ", repl.Take(12))}{(repl.Count > 12 ? " …" : "")}. " : "Nothing found to replace automatically. ") +
                                    "Check and edit the text below before you share it (abbreviations of customers or projects are not known to TcAgent - add them to privateTerms in settings.json).";
            }
            catch { feedbackText.Text = output; }
        }

        // the server does not know the IDE it runs beside - the bridge adds its own version and the IDE to the diagnostics
        internal static string BridgeLines()
        {
            string ide = "?";
            try
            {
                var p = Process.GetCurrentProcess();
                var fv = FileVersionInfo.GetVersionInfo(p.MainModule.FileName);
                var name = p.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase)
                    ? (fv.ProductMajorPart >= 17 ? "Visual Studio 2022" : fv.ProductMajorPart == 16 ? "Visual Studio 2019" : "Visual Studio")
                    : p.ProcessName.IndexOf("TcXaeShell", StringComparison.OrdinalIgnoreCase) >= 0 ? $"TwinCAT XAE Shell ({(Environment.Is64BitProcess ? "64" : "32")}-bit)" : p.ProcessName;
                ide = $"{name} {fv.ProductVersion}";
            }
            catch { }
            return $"- **TcAgent extension (bridge):** {BridgePackage.BridgeVersion}\n- **IDE (this window):** {ide}\n";
        }

        static string WithEnvironment(string text)
        {
            const string head = "**Diagnostics**\n\n";
            text = text.Replace("\r\n", "\n");
            var at = text.LastIndexOf(head, StringComparison.Ordinal);
            return at < 0 ? text : text.Insert(at + head.Length, BridgeLines());
        }

        // "Copy diagnostics": the bridge lines plus TcAgent.exe diag - for the Diagnostics field of an issue
        internal static void CopyDiagnostics(Action<string> say)
        {
            var exe = TcUnitControl.TcAgentExe();
            var sln = TcUnitControl.SolutionPath();
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                string text, err = null;
                try { text = exe == null ? "- **TcAgent:** TcAgent.exe not found" : Run(exe, "diag" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)), out err).Trim(); }
                catch (Exception ex) { text = "- **TcAgent diag:** " + ex.Message; }
                if (text.Length == 0) text = "- **TcAgent diag:** " + err?.Trim();
                ui.BeginInvoke(new Action(() =>
                {
                    try { Clipboard.SetText(BridgeLines() + text); say("Diagnostics copied - paste them into the Diagnostics field of the issue."); }
                    catch (Exception ex) { say("Could not copy: " + ex.Message); }
                }));
            });
        }

        void SaveIssue()
        {
            if (feedbackText.Text.Trim().Length == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "tcagent-feedback.md", Filter = "Markdown|*.md" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, feedbackText.Text, new UTF8Encoding(false));
            status.Text = "Saved: " + dlg.FileName;
        }

        void ShareIssue()
        {
            var text = feedbackText.Text.Trim();
            if (text.Length == 0) { status.Text = "Select a report first."; return; }
            var title = text.Split('\n')[0].Trim();
            var body = text.Substring(text.IndexOf('\n') + 1).Trim();
            var url = feedbackUrl ?? "https://github.com/ThorstenBrach/TcAgent/issues/new";
            // a GitHub issue form of the repository: its template for TcAgent reports and a label by the type of the report
            if (url.EndsWith("/issues/new", StringComparison.OrdinalIgnoreCase))
            {
                var types = feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "type")).Distinct().ToList();
                var labels = new List<string> { "feedback", "needs-triage" };
                foreach (var t in types)
                    labels.Add(t == "bug" ? "bug" : t == "missing_feature" ? "enhancement" : t == "docs" ? "documentation" : t ?? "feedback");
                url += "?template=tcagent_feedback.md&labels=" + Uri.EscapeDataString(string.Join(",", labels.Distinct()));
            }
            var full = url + (url.Contains("?") ? "&" : "?") + "title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
            var tooLong = full.Length > 7500;
            if (tooLong) { try { Clipboard.SetText(body); } catch { } full = url + (url.Contains("?") ? "&" : "?") + "title=" + Uri.EscapeDataString(title); }   // the template stays, the text comes from the clipboard
            if (MessageBox.Show($"Open a new issue in the browser?\n\n{url}\n\nThe browser shows the text - check it there and send it yourself." +
                                (tooLong ? "\n\nThe text is long: it is in the clipboard - paste it into the issue." : ""),
                                "Share with the TcAgent developers", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); status.Text = "Issue opened in the browser."; }
            catch (Exception ex) { Error(ex.Message); }
        }

        void SetFeedbackStatus()
        {
            var ids = feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "id")).ToList();
            if (ids.Count == 0) { status.Text = "Select one or more reports first."; return; }
            var r = Prompt($"Status of {ids.Count} report(s)", ("Status", new[] { "resolved", "wontfix", "duplicate", "open" }, "resolved"));
            if (r == null) return;
            status.Text = RunCli($"knowledge feedback --action resolve --key {Quote(string.Join(",", ids))} --message {r[0]}").output.Split('\n')[0];
            Load();
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
