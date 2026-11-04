// TcAgent Knowledge - tool window in Visual Studio/TwinCAT XAE Shell: everything the agent gets and where it comes from,
// and how the engineering workflows develop (docs/KNOWLEDGE_LAYERS.md, "Knowledge manager").
//
//   Overview       what the agent gets in this project (each count leads to the Files tab), what needs attention, KPI tiles
//                  (runs, first-time-right, failed checks, corrections, minutes per run, gate stops - the last 10 runs
//                  against the 10 before, with a sparkline), the workflows, open improvement proposals, frequent errors
//   Files          coding rules, risk rules, skills, workflows, lessons, templates and prompts of every level with their state
//                  (✓ applies, ↷ replaced, ⊘ switched off, ✎ changed locally ...) - filter by kind and level; new (from a
//                  template), open, open the folder, move to another level, delete (own levels), switch off / on - toolbar,
//                  context menu, Del; per level kept in Git: local changes, commits behind/ahead, Fetch / Pull / Commit / Push
//   Sources        the Git sources (browse and install - license, files and scripts shown first -, add, edit, remove) and the
//                  items installed from them (pinned commit, license, changed locally, newer upstream; update, merge, diff)
//   Platforms      the known platforms with their detection, which apply to the project; new, edit platform.md, set / detect
//   Modules        the groups of tools the agent sees (on/off for me or this project) and the plugins (approve) - KnowledgeModules.cs
//   Reference      what tc_docs looks things up in; add links, folders and documents; search like the agent
//   Feedback       the tc_feedback reports; the selected ones as an anonymized, editable issue text - copy, save, share with
//                  the TcAgent developers (a new issue in the browser, sent by the user), set the status
//   Backups        the backups of the knowledge levels (KnowledgeBackup.cs): back up, compare, restore, delete
//   Workflow runs  per workflow failed checks and corrections over the runs, moving average, the adopted changes as
//                  markers; the changes with their values before and after; export as CSV
//
// The window does no work itself - it starts TcAgent.exe knowledge json / disable / enable / new / move / delete / git
// (CliKnowledge.cs, KnowledgeOps.cs) in the background (Cli: a thin progress bar, the IDE stays responsive).
// Success goes to the status line or the info bar, errors are always a dialog. Proposals go through the chat.
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

        // F5 and Ctrl+F belong to the window while it is active - otherwise the IDE starts debugging or opens its own find
        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
        {
            const int WM_KEYDOWN = 0x100;
            if (m.Msg == WM_KEYDOWN && Content is KnowledgeControl k &&
                k.Shortcut((System.Windows.Forms.Keys)(int)(long)m.WParam & System.Windows.Forms.Keys.KeyCode, System.Windows.Forms.Control.ModifierKeys == System.Windows.Forms.Keys.Control))
                return true;
            return base.PreProcessMessage(ref m);
        }
    }

    public sealed partial class KnowledgeControl : UserControl
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
            public bool On { get; set; }   // ticked in a list with check boxes
            public bool Can { get; set; } = true;   // the check box can be changed (grey otherwise - the tooltip of the line says why)
            public string Tip { get; set; }
        }

        // ------------------------------------------------------------------ view
        static readonly Brush Green = TcUnitControl.Frozen(Color.FromRgb(0x2E, 0xA0, 0x43)), Red = TcUnitControl.Frozen(Color.FromRgb(0xD1, 0x34, 0x38)),
                              Blue = TcUnitControl.Frozen(Color.FromRgb(0x1E, 0x88, 0xE5)), Orange = TcUnitControl.Frozen(Color.FromRgb(0xE8, 0x8A, 0x1A)),
                              Gray = TcUnitControl.Frozen(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), TileBack = TcUnitControl.Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                              InfoBack = TcUnitControl.Frozen(Color.FromArgb(0x30, 0xE8, 0xB0, 0x1A)), InfoBorder = TcUnitControl.Frozen(Color.FromArgb(0x80, 0xE8, 0xB0, 0x1A));

        readonly ToggleButton tOverview = Tab("Overview"), tEffective = Tab("Files"), tSources = Tab("Sources"), tPlatforms = Tab("Platforms"), tDocs = Tab("Reference"),
                              tFeedback = Tab("Feedback"), tBackups = Tab("Backups"), tHistory = Tab("Workflow runs"), tModules = Tab("Modules");
        ToggleButton[] AllTabs => new[] { tOverview, tEffective, tSources, tPlatforms, tModules, tDocs, tFeedback, tBackups, tHistory };
        readonly ContentControl page = new ContentControl();
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock summary = new TextBlock { Margin = new Thickness(12, 2, 6, 4), Opacity = 0.8 };
        readonly TextBlock platform = new TextBlock { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly ProgressBar progress = new ProgressBar { Height = 2, IsIndeterminate = true, BorderThickness = new Thickness(0), Foreground = Blue, Background = Brushes.Transparent,
                                                          Visibility = Visibility.Collapsed, ToolTip = "TcAgent.exe is working" };
        readonly Border infoBar = new Border { Background = InfoBack, BorderBrush = InfoBorder, BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 2, 3),
                                               Margin = new Thickness(6, 4, 6, 2), Visibility = Visibility.Collapsed };
        readonly TextBlock infoText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock infoLink = new TextBlock { Margin = new Thickness(12, 0, 8, 0), Foreground = Blue, TextDecorations = TextDecorations.Underline, Cursor = Cursors.Hand,
                                                      VerticalAlignment = VerticalAlignment.Center };
        Action infoAct;
        int busy;
        DateTime readAt;

        // overview
        readonly WrapPanel gets = new WrapPanel { Margin = new Thickness(8, 0, 8, 2) }, attention = new WrapPanel { Margin = new Thickness(8, 0, 8, 2) };
        readonly TextBlock runsHeading = Heading("Workflow runs");
        readonly WrapPanel tiles = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        readonly ListView workflows = NewList();
        readonly StackPanel proposals = new StackPanel { Margin = new Thickness(8, 0, 8, 6) };
        readonly TextBlock facts = new TextBlock { Margin = new Thickness(8, 4, 8, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        // files
        static readonly string[] KindIds = { "coding-rules", "risk-rules", "skills", "workflows", "lessons", "doc-templates", "prompts" };
        static readonly Dictionary<string, string> KindNames = new Dictionary<string, string>
            { ["coding-rules"] = "Coding rules", ["risk-rules"] = "Risk rules", ["skills"] = "Skill", ["workflows"] = "Workflow", ["lessons"] = "Lessons", ["doc-templates"] = "Doc template", ["prompts"] = "Prompt" };
        static string KindName(string k) => k != null && KindNames.TryGetValue(k, out var n) ? n : k;
        readonly ComboBox kindBox = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Filter by kind" };
        readonly CheckBox onlyApplied = new CheckBox { Content = "Only what applies", Margin = new Thickness(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = false };
        readonly TextBox filter = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Filter by name, kind, level, state or reason (Ctrl+F)" };
        readonly ListView entries = NewList();
        readonly ComboBox levelBox = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Filter by level" };
        readonly StackPanel gitPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 2) };
        readonly TextBlock detailTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        readonly Grid detailKv = new Grid();
        readonly TextBox detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(4), MaxHeight = 320,
                                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        // sources
        readonly ListView sourceList = NewList(), installedList = NewList();
        readonly TextBlock sourcesHeading = Heading(""), installedHeading = Heading("");
        readonly TextBox sourceDetail = new TextBox { IsReadOnly = true, Height = 160, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Padding = new Thickness(6),
                                                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        // platforms
        readonly ListView platformList = NewList();
        readonly TextBlock platformInfo = new TextBlock { Margin = new Thickness(8, 8, 8, 2), TextWrapping = TextWrapping.Wrap };
        // reference sources (tc_docs)
        readonly ListView docsList = NewList();
        readonly TextBlock docsInfo = new TextBlock { Margin = new Thickness(8, 2, 8, 4), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        string[] docsLevels = { "user" };
        bool docsLoaded;
        int docsUnreachable, docsSources, docsDocuments;
        readonly ListView docsHits = NewList();
        readonly TextBox docsQuery = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Words from the documents, e.g. EtherCAT diagnosis (Enter searches; Ctrl+F)" };
        readonly TextBlock docsHitsHeading = Heading("Hits - double-click opens the document or page");
        // feedback
        readonly ListView feedbackList = new ListView { BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
        readonly ComboBox feedbackStatus = new ComboBox { MinWidth = 100, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                                                          ItemsSource = new[] { "open", "all", "resolved", "wontfix", "duplicate" }, SelectedIndex = 0 };
        readonly TextBox feedbackText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(0, 1, 0, 0) };
        readonly TextBlock feedbackInfo = new TextBlock { Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        string feedbackUrl;
        // workflow runs
        readonly ComboBox wfBox = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Canvas chart = new Canvas { MinHeight = 100, Margin = new Thickness(8), ClipToBounds = true };
        readonly ListView changes = NewList();
        // backups of the knowledge levels (KnowledgeBackup.cs)
        readonly ListView backupList = NewList();
        readonly TextBox backupDetail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Padding = new Thickness(6),
                                                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                      FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Brushes.Transparent };
        const string BackupLabel = "Backup now …", RestoreLabel = "Restore …";
        List<Dictionary<string, object>> backupLevels = new List<Dictionary<string, object>>();
        EnvDTE.DocumentEvents docEvents;   // kept: the events object must live as long as the window

        // tabs: the selected one underlined; a badge counts what needs attention there
        static readonly ControlTemplate TabTemplate = MakeTabTemplate();

        static ControlTemplate MakeTabTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 3));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, TileBack, "bd"));
            t.Triggers.Add(hover);
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BorderBrushProperty, Blue, "bd"));
            on.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.SemiBold, "bd"));
            t.Triggers.Add(on);
            return t;
        }

        static ToggleButton Tab(string text)
        {
            var label = new TextBlock { Text = text };
            label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var count = new TextBlock { FontSize = 10, Foreground = Brushes.White, Margin = new Thickness(5, 0, 5, 1), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var badge = new Border { Background = Orange, CornerRadius = new CornerRadius(7), MinWidth = 16, Margin = new Thickness(5, 0, 0, 0), Child = count,
                                     VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(label); sp.Children.Add(badge);
            return new ToggleButton { Content = sp, Padding = new Thickness(10, 6, 10, 4), Margin = new Thickness(0, 0, 2, 0), Template = TabTemplate, Tag = badge,
                                      Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
        }

        const string RefDocsTip = "Manuals, datasheets and library docs the agent searches (tc_docs)";

        static void Badge(ToggleButton tab, int n, string tip, string idleTip = null)
        {
            var b = (Border)tab.Tag;
            ((TextBlock)b.Child).Text = n.ToString(CultureInfo.InvariantCulture);
            b.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            tab.ToolTip = n > 0 ? tip : idleTip;
        }

        static Button Text(string text, string tip)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip,
                                 Template = TcUnitControl.FlatButton, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
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

        /// <summary>Columns (header, width, property of Line); a property with ! in front is the state: shown in the color of Mark
        /// (always with a symbol and a word - the color alone says nothing to everybody); ? in front = a check box (several items
        /// are ticked, not selected with Ctrl/Shift - one click beside them would lose the whole selection).</summary>
        static void Columns(ListView l, params (string Header, double Width, string Prop)[] cols)
        {
            var g = new GridView { AllowsColumnReorder = false };
            var header = new Style(typeof(GridViewColumnHeader));
            header.Setters.Add(new Setter(Control.TemplateProperty, TcUnitControl.HeaderTemplate()));
            header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            g.ColumnHeaderContainerStyle = header;
            foreach (var col in cols)
            {
                var c = new GridViewColumn { Header = col.Header, Width = col.Width };
                if (col.Prop.StartsWith("?"))
                {
                    var box = new FrameworkElementFactory(typeof(CheckBox));
                    box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(col.Prop.Substring(1)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                    box.SetValue(FocusableProperty, false);
                    box.SetBinding(IsEnabledProperty, new Binding(nameof(Line.Can)));
                    c.CellTemplate = new DataTemplate { VisualTree = box };
                }
                else if (col.Prop.StartsWith("!"))
                {
                    var txt = new FrameworkElementFactory(typeof(TextBlock));
                    txt.SetBinding(TextBlock.TextProperty, new Binding(col.Prop.Substring(1)));
                    txt.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Line.Mark)));
                    c.CellTemplate = new DataTemplate { VisualTree = txt };
                }
                else c.DisplayMemberBinding = new Binding(col.Prop);
                g.Columns.Add(c);
            }
            l.View = g;
        }

        /// <summary>An action of a list - the toolbar and the context menu show the same entries with the same names.
        /// Label null = separator; Enabled null = always; Sub = a drop-down ("Switch off ▾"); Key = the key shown in the menu.</summary>
        sealed class Act
        {
            public string Label, Tip, Key; public Action Do; public Func<bool> Enabled; public Act[] Sub; public bool Primary;
        }

        static Act A(string label, string tip, Action act, Func<bool> enabled = null, string key = null) => new Act { Label = label, Tip = tip, Do = act, Enabled = enabled, Key = key };
        static Act Sep => new Act();
        static Act DropDown(string label, string tip, Func<bool> enabled, params Act[] sub) => new Act { Label = label, Tip = tip, Enabled = enabled, Sub = sub };
        static Act Main(string label, string tip, Action act) => new Act { Label = label, Tip = tip, Do = act, Primary = true };

        // the buttons whose state depends on the selection - set again whenever a selection changes
        readonly List<(UIElement Element, Func<bool> Enabled)> gates = new List<(UIElement, Func<bool>)>();

        void UpdateGates()
        {
            foreach (var (el, en) in gates)
            {
                bool on;
                try { on = en(); } catch { on = false; }
                el.IsEnabled = on;
            }
        }

        WrapPanel Bar(params Act[] acts)
        {
            var p = new WrapPanel { Margin = new Thickness(6, 2, 6, 4) };
            foreach (var a in acts)
            {
                if (a.Label == null) { p.Children.Add(TcUnitControl.Separator()); continue; }
                var b = Text(a.Sub != null ? a.Label + " ▾" : a.Label, a.Tip + (a.Key != null ? $" ({a.Key})" : ""));
                if (a.Sub != null)
                {
                    var m = MenuOf(a.Sub);
                    m.PlacementTarget = b; m.Placement = PlacementMode.Bottom;
                    b.Click += (s, e) => m.IsOpen = true;
                }
                else b.Click += (s, e) => a.Do();
                if (a.Primary)
                {
                    // the main action of the tab: first and highlighted
                    var tb = (TextBlock)b.Content;
                    b.Content = null;
                    tb.Foreground = Brushes.White;
                    b.Content = new Border { Background = Blue, CornerRadius = new CornerRadius(2), Padding = new Thickness(8, 2, 8, 2), Child = tb };
                    b.Padding = new Thickness(0);
                }
                if (a.Enabled != null) gates.Add((b, a.Enabled));
                p.Children.Add(b);
            }
            return p;
        }

        static ContextMenu MenuOf(params Act[] acts)
        {
            var m = new ContextMenu();
            foreach (var i in MenuItems(m, acts)) m.Items.Add(i);
            return m;
        }

        // the items of a menu; enabled is evaluated when the menu opens
        static List<object> MenuItems(ContextMenu m, Act[] acts)
        {
            var items = new List<object>();
            foreach (var a in acts)
            {
                if (a.Label == null) { items.Add(new Separator()); continue; }
                var i = new MenuItem { Header = a.Label, InputGestureText = a.Key ?? "" };
                if (a.Sub != null) foreach (var s in MenuItems(m, a.Sub)) i.Items.Add(s);
                else { var act = a.Do; i.Click += (s, e) => act(); }
                if (a.Enabled != null)
                {
                    var en = a.Enabled;
                    m.Opened += (s, e) => { try { i.IsEnabled = en(); } catch { i.IsEnabled = false; } };
                }
                items.Add(i);
            }
            return items;
        }

        static TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 10, 8, 4), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        // a heading with a quieter explanation after it
        static void Heading(TextBlock t, string text, string note)
        {
            t.Inlines.Clear();
            t.Inlines.Add(new System.Windows.Documents.Run(text));
            if (!string.IsNullOrEmpty(note)) t.Inlines.Add(new System.Windows.Documents.Run("  – " + note) { FontWeight = FontWeights.Normal, Foreground = Gray });
        }

        static TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        // a search box with its hint inside and ✕ to clear it
        static Grid SearchBox(TextBox box, string hint, double width)
        {
            var g = new Grid { Width = width, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var w = new TextBlock { Text = hint, Opacity = 0.55, Margin = new Thickness(5, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                                    TextTrimming = TextTrimming.CharacterEllipsis };
            w.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var clear = TcUnitControl.Tool("✕", null, "Clear");
            clear.HorizontalAlignment = HorizontalAlignment.Right; clear.MinWidth = 18; clear.Padding = new Thickness(3, 0, 3, 0); clear.Visibility = Visibility.Collapsed;
            clear.Click += (s, e) => box.Clear();
            box.Padding = new Thickness(2, 1, 20, 1);
            box.TextChanged += (s, e) =>
            {
                w.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = box.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            g.Children.Add(box); g.Children.Add(w); g.Children.Add(clear);
            return g;
        }

        // a list with a text in its middle while it is empty (what it is for and how to fill it)
        static Grid WithEmpty(ListView l, string text)
        {
            var g = new Grid();
            var t = new TextBlock { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20, 34, 20, 8),
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, MaxWidth = 560 };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            t.Visibility = l.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ((System.Collections.Specialized.INotifyCollectionChanged)l.Items).CollectionChanged += (s, e) => t.Visibility = l.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            g.Children.Add(l); g.Children.Add(t);
            return g;
        }

        // an output box, folded while it is empty
        static Expander Output(TextBox box)
        {
            var e = new Expander { Header = "Output (empty)", Content = box, IsExpanded = false, Margin = new Thickness(6, 2, 6, 2) };
            e.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return e;
        }

        // the text of an action in its output box - opened, with the header "Output"
        static void Out(TextBox box, string text)
        {
            box.Text = text ?? "";
            if (box.Parent is Expander e) { e.Header = box.Text.Length > 0 ? "Output" : "Output (empty)"; e.IsExpanded = box.Text.Length > 0; }
        }

        static GridSplitter Splitter()
        {
            var s = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            s.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return s;
        }

        // rows that share the height of a page: (element, height, minimum)
        static Grid Rows(params (UIElement Element, GridLength Height, double Min)[] parts)
        {
            var g = new Grid();
            for (int i = 0; i < parts.Length; i++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = parts[i].Height, MinHeight = parts[i].Min });
                Grid.SetRow(parts[i].Element, i); g.Children.Add(parts[i].Element);
            }
            return g;
        }

        static GridLength Star(double n) => new GridLength(n, GridUnitType.Star);
        static readonly GridLength AutoH = GridLength.Auto;

        // a chip of the overview: a count that leads to the tab where it is
        Button Chip(string text, Brush accent, string tip, Action act)
        {
            var t = new TextBlock { Text = text, Foreground = accent ?? Blue };
            if (act != null) t.TextDecorations = TextDecorations.Underline;
            var border = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 2, 10, 3), Child = t,
                                      BorderBrush = accent == Orange ? InfoBorder : Gray, Background = accent == Orange ? InfoBack : Brushes.Transparent };
            var b = new Button { Content = border, Template = TcUnitControl.FlatButton, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip,
                                 Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Cursor = act != null ? Cursors.Hand : null };
            if (act != null) b.Click += (s, e) => act();
            return b;
        }

        public KnowledgeControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var t in new[] { platform, status, summary, facts, infoText, platformInfo, docsInfo, feedbackInfo, detailTitle })
                t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            // the top bar: the tabs left; the platform, refresh and ⋯ (levels, backup, restore, diagnostics) right
            var top = new DockPanel { Margin = new Thickness(4, 2, 4, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var refresh = TcUnitControl.Tool("⟳", null, "Refresh - read the knowledge and the run logs again (F5)");
            refresh.Click += (s, e) => RefreshAll();
            var more = TcUnitControl.Tool("⋯", null, "Levels, backup, restore, diagnostics");
            var moreMenu = MenuOf(A("Levels …", "Company, team and platform folders or repositories (settings.json)", EditLevels),
                                  Sep,
                                  A(BackupLabel, "Back up the knowledge of the levels now (it is also backed up automatically before every change)", BackupNow),
                                  A(RestoreLabel, "Restore a level, a file or a skill from a backup", RestoreBackup),
                                  Sep,
                                  A("Refresh", "Read the knowledge and the run logs again", RefreshAll, null, "F5"),
                                  A("Copy diagnostics", "Versions of TcAgent, the extension, TwinCAT and the IDEs for the Diagnostics field of an issue", () => CopyDiagnostics(t => Info(t))));
            moreMenu.PlacementTarget = more; moreMenu.Placement = PlacementMode.Bottom;
            more.Click += (s, e) => moreMenu.IsOpen = true;
            foreach (var e in new UIElement[] { platform, refresh, more }) right.Children.Add(e);
            DockPanel.SetDock(right, Dock.Right); top.Children.Add(right);
            var tabs = new WrapPanel();
            foreach (var t in AllTabs) { tabs.Children.Add(t); t.Click += (s, e) => Show((ToggleButton)s); }
            top.Children.Add(tabs);
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            // the info bar: a success or a hint the user should notice without a dialog
            var close = TcUnitControl.Tool("✕", null, "Close");
            close.Click += (s, e) => infoBar.Visibility = Visibility.Collapsed;
            infoLink.MouseLeftButtonUp += (s, e) => infoAct?.Invoke();
            var ib = new DockPanel();
            DockPanel.SetDock(close, Dock.Right); ib.Children.Add(close);
            DockPanel.SetDock(infoLink, Dock.Right); ib.Children.Add(infoLink);
            ib.Children.Add(infoText);
            infoBar.Child = ib;
            // the status line: the last message left, what the tab shows right
            var bottom = new DockPanel();
            var bottomLine = new Border { Height = 1 };
            bottomLine.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            DockPanel.SetDock(bottomLine, Dock.Top); bottom.Children.Add(bottomLine);
            DockPanel.SetDock(summary, Dock.Right); bottom.Children.Add(summary);
            bottom.Children.Add(status);
            var root = new DockPanel();
            foreach (var (el, dock) in new (UIElement, Dock)[] { (top, Dock.Top), (line, Dock.Top), (progress, Dock.Top), (infoBar, Dock.Top), (bottom, Dock.Bottom) })
            { DockPanel.SetDock(el, dock); root.Children.Add(el); }
            root.Children.Add(page);
            Content = root;

            Columns(workflows, ("Workflow", 200, nameof(Line.C1)), ("Runs", 60, nameof(Line.C2)), ("First-time-right", 110, nameof(Line.C3)),
                    ("Failed checks / run", 220, "!" + nameof(Line.C4)), ("Corrections / run", 130, nameof(Line.C5)), ("Proposals", 80, nameof(Line.C6)));
            Columns(entries, ("Name", 200, nameof(Line.C1)), ("Kind", 100, nameof(Line.C2)), ("Level", 120, nameof(Line.C3)),
                    ("State", 190, "!" + nameof(Line.C4)), ("Source / license", 320, nameof(Line.C6)), ("Git", 60, nameof(Line.C7)), ("Why", 260, nameof(Line.C5)));
            Columns(sourceList, ("Source", 180, nameof(Line.C1)), ("Level", 80, nameof(Line.C2)), ("Repository", 420, nameof(Line.C3)), ("Branch / tag", 100, nameof(Line.C4)));
            Columns(installedList, ("Item", 230, nameof(Line.C1)), ("Level", 80, nameof(Line.C2)), ("State", 190, "!" + nameof(Line.C3)),
                    ("Source @ version", 200, nameof(Line.C4)), ("License", 100, nameof(Line.C5)), ("Installed", 100, nameof(Line.C6)));
            Columns(changes, ("Change", 150, nameof(Line.C1)), ("Date", 90, nameof(Line.C2)), ("What", 360, nameof(Line.C3)),
                    ("Before", 110, nameof(Line.C4)), ("After (runs)", 130, nameof(Line.C5)), ("Result", 110, "!" + nameof(Line.C6)));

            kindBox.ItemsSource = new[] { "All kinds", "Coding rules", "Risk rules", "Skills", "Workflows", "Lessons", "Doc templates", "Prompts" };
            kindBox.SelectedIndex = 0;
            kindBox.SelectionChanged += (s, e) => FillEntries();
            onlyApplied.Click += (s, e) => FillEntries();
            filter.TextChanged += (s, e) => FillEntries();
            onlyApplied.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            entries.SelectionChanged += (s, e) => ShowEntry();
            entries.MouseDoubleClick += (s, e) => OpenSelected();
            levelBox.SelectionChanged += (s, e) => FillEntries();
            entries.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeleteItem(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
            workflows.MouseDoubleClick += (s, e) => { if (workflows.SelectedItem is Line l) { wfBox.SelectedItem = l.C1; Show(tHistory); } };
            wfBox.SelectionChanged += (s, e) => FillHistory();
            chart.SizeChanged += (s, e) => DrawChart();
            // the platform next to the tabs leads to the Platforms tab
            platform.Cursor = Cursors.Hand;
            platform.MouseLeftButtonUp += (s, e) => Show(tPlatforms);
            // every selection (lists, filters) sets the toolbar buttons again
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => UpdateGates()));
            PreviewKeyDown += (s, e) =>
            {
                if (Shortcut((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), Keyboard.Modifiers == ModifierKeys.Control)) e.Handled = true;
            };
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
            tDocs.ToolTip = RefDocsTip;
            Show(tOverview);
            status.Text = "Reading the knowledge …";
        }

        /// <summary>F5 = refresh, Ctrl+F = the search box of the tab (Files, Reference).</summary>
        internal bool Shortcut(System.Windows.Forms.Keys key, bool ctrl)
        {
            if (key == System.Windows.Forms.Keys.F5 && !ctrl) { RefreshAll(); return true; }
            if (key == System.Windows.Forms.Keys.F && ctrl)
            {
                if (tDocs.IsChecked != true && tEffective.IsChecked != true) Show(tEffective);
                var box = tDocs.IsChecked == true ? docsQuery : filter;
                Dispatcher.BeginInvoke(new Action(() => { box.Focus(); Keyboard.Focus(box); box.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
                return true;
            }
            // in a list (not in a text box): F2 = Rename (Files), Ctrl+C = Copy path of the selected line
            var list = Keyboard.FocusedElement is DependencyObject f ? (f as ListView ?? ItemsControl.ItemsControlFromItemContainer(f) as ListView) : null;
            if (list == null) return false;
            if (key == System.Windows.Forms.Keys.F2 && !ctrl && list == entries) { RenameItem(); return true; }
            if (key == System.Windows.Forms.Keys.C && ctrl && list.SelectedItem is Line l)
            {
                CopyPath(new[] { "path", "zip", "value", "url", "file", "where" }.Select(k => S(l.D, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? l.C1);
                return true;
            }
            return false;
        }

        /// <summary>User folders as variables: %LOCALAPPDATA%, %APPDATA%, %USERPROFILE% - shorter, and no user name on the screen
        /// (the full path stays in the tooltip and in Copy path).</summary>
        internal static string UserPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var (folder, name) in new[] { (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"), (Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
                                                   (Environment.SpecialFolder.UserProfile, "%USERPROFILE%") })
            {
                var p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) && s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return name + s.Substring(p.Length);
            }
            return s;
        }

        /// <summary>A long path shortened in the middle: "D:\Projekte\…\POUs\MAIN.TcPOU" (the full one stays in Copy path).</summary>
        internal static string MiddlePath(string path, int max = 70)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
            var parts = path.Split('\\');
            if (parts.Length < 4) return path.Substring(0, max / 2) + "…" + path.Substring(path.Length - max / 2);
            string head = parts[0] + "\\" + parts[1], tail = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i > 1 && head.Length + tail.Length + parts[i].Length + 3 <= max; i--) tail = parts[i] + "\\" + tail;
            return head + "\\…\\" + tail;
        }

        void RefreshAll()
        {
            Load();
            LoadDocs();
            if (backupsPage != null) LoadBackups();
            if (modulesPage != null) LoadModules();
        }

        void Show(ToggleButton tab)
        {
            foreach (var t in AllTabs) t.IsChecked = t == tab;
            infoBar.Visibility = Visibility.Collapsed;
            try
            {
                page.Content = tab == tOverview ? OverviewPage() : tab == tEffective ? EffectivePage() : tab == tSources ? SourcesPage()
                             : tab == tPlatforms ? PlatformsPage() : tab == tModules ? ModulesPage() : tab == tDocs ? DocsPage() : tab == tFeedback ? FeedbackPage() : tab == tBackups ? BackupsPage() : (object)HistoryPage();
            }
            catch (Exception ex)
            {
                status.Text = "This page could not be shown: " + ex.Message;
                BridgePackage.Instance?.LogLine("Knowledge window: " + ex);
            }
            Summary();
            UpdateGates();
        }

        // the right part of the status line: what the shown tab holds
        void Summary()
        {
            string Count(IEnumerable<Line> l) => (l?.Count() ?? 0).ToString(CultureInfo.InvariantCulture);
            var read = readAt == default ? "" : $"   read {readAt:HH:mm}";
            if (tEffective.IsChecked == true)
            {
                var l = (entries.ItemsSource as IEnumerable<Line>)?.ToList() ?? new List<Line>();
                summary.Text = $"{l.Count} files · {l.Count(x => B(x.D, "active"))} apply · {l.Count(x => S(x.D, "status") == "disabled")} switched off" +
                               $" · {l.Count(x => (S(x.D, "sourceState") ?? "").Contains("changed locally"))} changed locally" + read;
            }
            else if (tSources.IsChecked == true)
            {
                var i = (installedList.ItemsSource as IEnumerable<Line>)?.ToList() ?? new List<Line>();
                summary.Text = $"{Count(sourceList.ItemsSource as IEnumerable<Line>)} source(s) · {i.Count} installed · {i.Count(x => (S(x.D, "state") ?? "").Contains("changed locally"))} changed" +
                               $" · {i.Count(x => (S(x.D, "state") ?? "").Contains("newer upstream"))} newer" + read;
            }
            else if (tPlatforms.IsChecked == true) summary.Text = $"{Count(platformList.ItemsSource as IEnumerable<Line>)} platform(s)" + read;
            else if (tModules.IsChecked == true) summary.Text = ModulesSummary();
            else if (tDocs.IsChecked == true) summary.Text = $"{docsSources} source(s) · {docsDocuments} document(s) in folders" + (docsUnreachable > 0 ? $" · {docsUnreachable} not reachable" : "");
            else if (tFeedback.IsChecked == true) summary.Text = $"{Count(feedbackList.ItemsSource as IEnumerable<Line>)} report(s) shown" + read;
            else if (tBackups.IsChecked == true) summary.Text = $"{Count(backupList.ItemsSource as IEnumerable<Line>)} backup(s) · the last 20 per level are kept";
            else if (tHistory.IsChecked == true) summary.Text = Workflow() is Dictionary<string, object> w ? $"{Arr(w["series"]).Count()} run(s) of {S(w, "name")}" + read : "";
            else summary.Text = data == null ? "" : $"{(string.IsNullOrEmpty(TcUnitControl.SolutionPath()) ? "no solution open" : Path.GetFileNameWithoutExtension(TcUnitControl.SolutionPath()))} · TcAgent {S(data, "version")}" + read;
        }

        FrameworkElement overviewPage, effectivePage, sourcesPage, historyPage, platformsPage, docsPage, feedbackPage, backupsPage;

        FrameworkElement OverviewPage()
        {
            if (overviewPage != null) return overviewPage;
            var sp = new StackPanel();
            var h1 = Heading("");
            Heading(h1, "What the agent gets in this project", "each entry shows its files");
            sp.Children.Add(h1);
            sp.Children.Add(gets);
            sp.Children.Add(Heading("Needs attention"));
            sp.Children.Add(attention);
            sp.Children.Add(runsHeading);
            sp.Children.Add(tiles);
            sp.Children.Add(Heading("Workflows (double-click: the runs over time)"));
            sp.Children.Add(WithEmpty(workflows, "No workflow run yet - the workflows appear here after their first run."));
            sp.Children.Add(Heading("Improvement proposals (ask the agent: \"apply proposal <id>\")"));
            sp.Children.Add(proposals);
            sp.Children.Add(facts);
            return overviewPage = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        Dictionary<string, object> Entry() => (entries.SelectedItem as Line)?.D;

        // switched off for the project needs a solution; locked files only the company level switches off
        bool CanOff(string level) => Entry() is Dictionary<string, object> d && !B(d, "locked") && S(d, "status") != "disabled" &&
                                     (level == "user" || !string.IsNullOrEmpty(S(data, "solutionDir")));

        FrameworkElement EffectivePage()
        {
            if (effectivePage != null) return effectivePage;
            bool Sel() => entries.SelectedItem is Line;
            bool FromSource() => S(Entry(), "sourceState") != null;
            (string, string) Item0() { var d = Entry(); return (S(d, "name"), S(d, "installLevel") ?? (S(d, "label") ?? "").Split(' ')[0]); }
            var acts = new[] {
                A("New …", "A new rule, risk rule, skill, workflow or lesson from a template", () => NewItem(false)),
                A("Add existing …", "Take an existing file (or skill/workflow folder) over to a level", () => NewItem(true)),
                Sep,
                A("Open", "Open the file in the editor (or double-click)", OpenSelected, Sel, "Enter"),
                A("Open folder", "Show the file in the Explorer", OpenFolder, Sel),
                Sep,
                A("Rename …", "Rename the file or the skill/workflow folder (the name in its header follows)", RenameItem, Sel),
                A("Move to level …", "Move the file to another level (shared levels in Git: as a pull request)", MoveItem, Sel),
                Sep,
                DropDown("Switch off", "disabled.json - the file itself stays unchanged", () => CanOff("project") || CanOff("user"),
                     A("For this project", "disabled.json of the project", () => Switch("disable", "project"), () => CanOff("project")),
                     A("For me (all projects)", "disabled.json of your user level", () => Switch("disable", "user"), () => CanOff("user"))),
                A("Switch on", "Remove the entry from disabled.json (project and user)", SwitchOn, () => S(Entry(), "status") == "disabled"),
                Sep,
                A("Delete …", "Delete the file (own levels only)", DeleteItem, () => Entry() is Dictionary<string, object> d && LayerOf(d) is Dictionary<string, object> ly && B(ly, "deletable"), "Del"),
                Sep,
                A("Copy path", "Copy the path of the file", () => CopyPath(S(Entry(), "path")), Sel),
                Sep,
                DropDown("Source", "An item installed from a source: your changes, a newer version, back to the source", FromSource,
                     A(SrcCompare, "Your version and the installed one in the compare window", () => { var (n, lv) = Item0(); CompareSource(n, lv, detail); }),
                     A(SrcChanges, "What changed in the source since the install", () => { var (n, lv) = Item0(); RunAsyncTo($"sources diff --item {Quote(n)}", detail); }),
                     A(SrcMerge, "Merge the newer version of the source into your item (three-way; conflicts in the merge window)", () => { var (n, lv) = Item0(); MergeSource(n, lv, detail); }),
                     Sep,
                     A(SrcRevert, "Back to the installed version (yours goes to the backups)", () => { var (n, lv) = Item0(); RevertSource(n, lv); }),
                     A(SrcDetach, "No longer tied to the source - no updates, the origin stays in the header", () => { var (n, lv) = Item0(); DetachSource(n, lv); }),
                     A(SrcPropose, "Your changes as a branch for the source - push it and open the pull request", () => { var (n, lv) = Item0(); ProposeSource(n, lv, detail); }),
                     Sep,
                     A(SrcUninstall, "Remove the item from its level (a copy goes to the backups; the source stays)", () => { var (n, lv) = Item0(); UninstallSource(n, lv, S(Entry(), "sourceState")); })) };
            var tools = Bar(acts);
            entries.ContextMenu = MenuOf(acts);
            var filters = new WrapPanel { Margin = new Thickness(6, 0, 6, 4) };
            foreach (var e in new UIElement[] { kindBox, levelBox, SearchBox(filter, "Search name, level, state, reason (Ctrl+F)", 260), onlyApplied }) filters.Children.Add(e);
            // the selected file: a short header, the facts with their actions, the file folded
            var kvCol = new ColumnDefinition { Width = new GridLength(110) };
            detailKv.ColumnDefinitions.Add(kvCol);
            detailKv.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(1) });
            var exp = Output(detail);
            exp.Margin = new Thickness(0, 4, 0, 0);
            var dp = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
            dp.Children.Add(detailTitle); dp.Children.Add(detailKv); dp.Children.Add(exp);
            var detailView = new ScrollViewer { Content = dp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var list = WithEmpty(entries, "Nothing matches the filter - clear the search or choose All kinds / All levels.");
            effectivePage = Rows((tools, AutoH, 0), (filters, AutoH, 0), (gitPanel, AutoH, 0), (list, Star(3), 80), (Splitter(), AutoH, 0), (detailView, Star(1.4), 70));
            ShowEntry();
            return effectivePage;
        }

        // the names of the actions on an item installed from a source - the same in Files and Sources
        const string SrcCompare = "My changes", SrcChanges = "What's new upstream", SrcRevert = "Revert to installed version …",
                     SrcDetach = "Keep as own version …", SrcPropose = "Propose upstream (pull request) …", SrcMerge = "Update and merge …", SrcUninstall = "Uninstall …";

        FrameworkElement SourcesPage()
        {
            if (sourcesPage != null) return sourcesPage;
            bool SSel() => sourceList.SelectedItem is Line;
            string Src(string k) => S((sourceList.SelectedItem as Line)?.D, k);
            var srcActs = new[] {
                Main("Browse and install …", "Show what the sources offer and install items - license, files and scripts are shown first", BrowseSource),
                Sep,
                A("New …", "A new source: a Git repository with skills, workflows, rules or lessons", () => EditSource(null)),
                Sep,
                A("Open", "The repository in the browser (or its folder; double-click)", () => OpenUrlOrFolder(Src("url")), SSel, "Enter"),
                A("Open folder", "The local copy of the repository (TcAgent's cache)", () => OpenSourceCache(Src("name")), SSel),
                Sep,
                A("Edit …", "Change URL, branch/tag or level of the selected source", () => EditSource(sourceList.SelectedItem as Line), SSel),
                Sep,
                A("Delete …", "Delete the selected source from the list (installed items stay)", RemoveSource, SSel, "Del"),
                Sep,
                A("Copy URL", "Copy the URL of the repository", () => CopyPath(Src("url")), SSel) };
            var tools1 = Bar(srcActs);
            bool ISel() => installedList.SelectedItem is Line;
            string Inst(string k) => S((installedList.SelectedItem as Line)?.D, k);
            void On(Action<string, string> act) { var (n, lv) = Installed(); if (n != null) act(n, lv); }
            var instActs = new[] {
                A("Open", "Open the installed file in the editor (or double-click)", () => OpenInEditor(Inst("path")), ISel, "Enter"),
                A("Open folder", "Show the installed item in the Explorer", () => ShowInExplorer(Inst("path")), ISel),
                Sep,
                A(SrcCompare, "Your version and the installed one in the compare window of Visual Studio", () => On((n, lv) => CompareSource(n, lv, sourceDetail)), ISel),
                A(SrcMerge, "Merge the newer version of the source into your item (three-way; conflicts in the merge window)", () => On((n, lv) => MergeSource(n, lv, sourceDetail)), ISel),
                DropDown("More", "Rarer actions on the installed item", ISel,
                     A(SrcChanges, "What changed in the source since the install", DiffInstalled),
                     Sep,
                     A(SrcRevert, "Back to the installed version (yours goes to the backups)", () => On(RevertSource)),
                     A(SrcDetach, "No longer tied to the source - no updates, the origin stays in the header", () => On(DetachSource)),
                     A(SrcPropose, "Your changes as a branch for the source - push it and open the pull request", () => On((n, lv) => ProposeSource(n, lv, sourceDetail)))),
                Sep,
                A(SrcUninstall, "Remove the item from its level (a copy goes to the backups; the source stays)", () => On((n, lv) => UninstallSource(n, lv, Inst("state"))), ISel, "Del"),
                Sep,
                A("Copy path", "Copy the path of the installed item", () => CopyPath(Inst("path")), ISel) };
            var tools2 = Bar(instActs);
            // the actions on all installed items next to their heading
            var global = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 2) };
            var check = Text("Check for updates", "Fetch the sources and list what is newer there");
            check.Click += (s, e) => RunAsync("sources pull --dry-run", true, what: "Checking the sources");
            var all = Text("Update all …", "Update every installed item that is newer in its source (not the ones changed locally)");
            all.Click += (s, e) => UpdateAll();
            global.Children.Add(check); global.Children.Add(all);
            var head2 = new DockPanel();
            DockPanel.SetDock(global, Dock.Right); head2.Children.Add(global); head2.Children.Add(installedHeading);
            var srcList = WithEmpty(sourceList, "No source yet - \"New …\" adds a Git repository with skills, workflows, rules or lessons; \"Browse and install …\" shows what it offers.");
            srcList.MaxHeight = 170;
            var instList = WithEmpty(installedList, "Nothing installed from a source yet - \"Browse and install …\" above.");
            sourceList.MouseDoubleClick += (s, e) => OpenUrlOrFolder(Src("url"));
            installedList.MouseDoubleClick += (s, e) => OpenInEditor(Inst("path"));
            sourceList.ContextMenu = MenuOf(srcActs);
            sourceList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveSource(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenUrlOrFolder(Src("url")); e.Handled = true; } };
            installedList.ContextMenu = MenuOf(instActs);
            installedList.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete) { On((n, lv) => UninstallSource(n, lv, Inst("state"))); e.Handled = true; }
                else if (e.Key == Key.Enter) { OpenInEditor(Inst("path")); e.Handled = true; }
            };
            return sourcesPage = Rows((sourcesHeading, AutoH, 0), (tools1, AutoH, 0), (srcList, AutoH, 0), (head2, AutoH, 0), (tools2, AutoH, 0),
                                      (instList, Star(1), 80), (Output(sourceDetail), AutoH, 0));
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

        /// <summary>A message the user should notice without a dialog (a success, a hint): the info bar below the tabs, with an optional link.</summary>
        void Info(string text, string link = null, Action act = null)
        {
            text = (text ?? "").Trim();
            status.Text = text.Split('\n')[0];
            infoText.Text = text;
            infoLink.Text = link ?? "";
            infoLink.Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
            infoAct = act;
            infoBar.Visibility = Visibility.Visible;
        }

        // a hint the user sees (the status line is easy to miss)
        void Hint(string text) => Info(text);

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
            var export = Text("Export CSV", "The runs of this workflow and the changes as CSV");
            export.Click += (s, e) => ExportCsv();
            var tools = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
            foreach (var e in new UIElement[] { Label("Workflow:"), wfBox, export }) tools.Children.Add(e);
            var legend = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            legend.Inlines.Add(new System.Windows.Documents.Run("━ failed checks  ") { Foreground = Blue });
            legend.Inlines.Add(new System.Windows.Documents.Run("━ corrections  ") { Foreground = Orange });
            legend.Inlines.Add(new System.Windows.Documents.Run("┅ moving average (5)  ") { Foreground = Gray });
            legend.Inlines.Add(new System.Windows.Documents.Run("│ adopted improvement") { Foreground = Green });
            tools.Children.Add(legend);
            var frame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(6), Child = chart };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var h = Heading("");
            Heading(h, "Adopted improvements", "failed checks + corrections per run, before and over the next 10 runs");
            var list = WithEmpty(changes, "No improvement adopted yet - the agent proposes them on the Overview (\"apply proposal <id>\").");
            return historyPage = Rows((tools, AutoH, 0), (frame, Star(3), 110), (h, AutoH, 0), (list, Star(1.4), 60));
        }

        FrameworkElement BackupsPage()
        {
            if (backupsPage != null) return backupsPage;
            bool BSel() => backupList.SelectedItem is Line;
            var backupActs = new[] {
                Main(BackupLabel, "Back up levels now - into a folder of your choice", BackupNow),
                A(RestoreLabel, "The whole level, one file or one skill - the difference first, then you confirm; the current state is backed up before", RestoreBackup),
                A("Restore from a file …", "A backup ZIP from another folder or PC", RestoreFromFile),
                Sep,
                A("Open folder", "The folder with the backups (ZIP files with manifest.json)", OpenBackupFolder),
                A("Compare with current state", "What restoring the selected backup would change (or double-click)", () => DiffBackup(null), BSel, "Enter"),
                Sep,
                A("Delete …", "Delete the selected backup file", DeleteBackup, BSel, "Del"),
                Sep,
                A("Copy path", "Copy the path of the backup file", () => CopyPath(S((backupList.SelectedItem as Line)?.D, "zip")), BSel) };
            var bt = Bar(backupActs);
            var hb = Heading("");
            Heading(hb, "Backups of the knowledge", "made automatically before every change; the last 20 per level are kept; levels in Git: uncommitted changes only");
            Columns(backupList, ("Level", 140, nameof(Line.C1)), ("Created", 150, nameof(Line.C2)), ("Reason", 300, nameof(Line.C3)), ("Files", 60, nameof(Line.C4)),
                    ("Kind", 170, nameof(Line.C5)), ("TcAgent", 70, nameof(Line.C6)));
            backupList.ContextMenu = MenuOf(backupActs);
            backupList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeleteBackup(); e.Handled = true; } else if (e.Key == Key.Enter) { DiffBackup(null); e.Handled = true; } };
            backupList.SelectionChanged += (s, e) =>
            {
                if (backupList.SelectedItem is Line l)
                    backupDetail.Text = $"{UserPath(S(l.D, "zip"))}\n\nContents ({S(l.D, "files")}):\n" + string.Join("\n", ((l.D["paths"] as IEnumerable) ?? new object[0]).Cast<object>());
            };
            backupList.MouseDoubleClick += (s, e) => DiffBackup(null);
            backupDetail.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            backupDetail.Text = "Select a backup - its file and contents appear here; double-click (or Enter) compares it with the current state.";
            var list = WithEmpty(backupList, "No backup yet - they are made automatically before every change, or now with \"" + BackupLabel + "\".");
            LoadBackups();
            return backupsPage = Rows((hb, AutoH, 0), (bt, AutoH, 0), (list, Star(2), 90), (Splitter(), AutoH, 0), (backupDetail, Star(1), 50));
        }

        // ------------------------------------------------------------------ load
        bool loading;

        internal void Load()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Load)); return; }
            if (loading) return;
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { status.Text = "TcAgent.exe not found - start the IDE once after installing TcAgent (VSIX in Visual Studio 2022, install-xaeshell.ps1 in the XAE Shell), or set TCAGENT_EXE."; return; }
            var sln = TcUnitControl.SolutionPath();
            loading = true;
            Busy(+1, "Reading the knowledge");
            System.Threading.Tasks.Task.Run(() =>
            {
                string json = null, err = null;
                try { json = Run(exe, "knowledge json" + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)), out err); }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    loading = false;
                    Busy(-1, null);
                    try
                    {
                        data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json ?? "");
                        readAt = DateTime.Now;
                        try { Fill(); }
                        catch (Exception fx) { BridgePackage.Instance?.LogLine("Knowledge window, fill: " + fx); throw; }
                        status.Text = string.IsNullOrEmpty(sln) ? "No solution open - only the general levels" : "Ready.";
                        if (!docsLoaded) LoadDocs();
                    }
                    // not a dialog: this also runs when the IDE starts with the window open
                    catch (Exception ex) { Info("Could not read the knowledge: " + (string.IsNullOrWhiteSpace(err) ? ex.Message : err.Trim()), "Try again", Load); }
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
            // the platform short, as a link to the Platforms tab; why in the tooltip
            platform.Inlines.Clear();
            platform.Inlines.Add(new System.Windows.Documents.Run("Platform: "));
            platform.Inlines.Add(new System.Windows.Documents.Run((active.Count > 0 ? string.Join(", ", active) : "none") + " ›") { Foreground = Blue, TextDecorations = TextDecorations.Underline });
            platform.ToolTip = (S(pf, "reason") is string r ? r + "\n" : "") + "Click: the platforms of this project and how they are detected";
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
            Badges();
            Summary();
            UpdateGates();
        }

        // ------------------------------------------------------------------ overview
        // the counts of the rates make sense from this many runs on
        const int MinRuns = 5;

        void FillOverview()
        {
            // what the agent gets: one chip per kind, it leads to the Files tab with that kind
            gets.Children.Clear();
            var counts = data["counts"] as Dictionary<string, object> ?? new Dictionary<string, object>();
            foreach (var kind in KindIds)
            {
                var n = counts.TryGetValue(kind, out var c) ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : 0;
                if (n == 0) continue;
                var name = KindName(kind).ToLowerInvariant();
                var text = $"{n} {(kind is "coding-rules" or "risk-rules" ? name.Replace(" rules", " rule") + (n == 1 ? " file" : " files") : kind == "lessons" ? "lessons file" + (n == 1 ? "" : "s") : name + (n == 1 ? "" : "s"))}";
                var k = kind;
                gets.Children.Add(Chip(text, null, "Show them in the Files tab", () => ShowKind(k)));
            }
            if (docsSources > 0) gets.Children.Add(Chip($"{docsSources} reference source{(docsSources == 1 ? "" : "s")}", null, RefDocsTip, () => Show(tDocs)));
            if (gets.Children.Count == 0) gets.Children.Add(Chip("nothing yet - only the built-in knowledge", Gray, null, null));

            // what needs attention, each with a link to its tab
            attention.Children.Clear();
            var inst = Arr(data["installed"]).Select(i => S(i, "state") ?? "").ToList();
            int mine = inst.Count(s => s.Contains("changed locally")), theirs = inst.Count(s => s.Contains("newer upstream")), missing = inst.Count(s => s.StartsWith("missing"));
            if (mine > 0) attention.Children.Add(Chip($"✎ {mine} item(s) from a source changed locally - Sources", Orange, "Compare, merge, propose or revert", () => Show(tSources)));
            if (theirs > 0) attention.Children.Add(Chip($"↑ {theirs} item(s) newer in their source - Sources", Orange, "Update and merge", () => Show(tSources)));
            if (missing > 0) attention.Children.Add(Chip($"✗ {missing} installed item(s) missing - Sources", Orange, "Install them again or uninstall them", () => Show(tSources)));
            foreach (var l in Arr(data["layers"]))
                if (l.TryGetValue("git", out var go) && go is Dictionary<string, object> g && !B(g, "same") && S(l, "level") != null)
                {
                    var ch = (g.TryGetValue("changes", out var cg) ? cg as Dictionary<string, object> : null)?.Count ?? 0;
                    if (ch > 0 || N(g, "behind") > 0 || N(g, "ahead") > 0)
                        attention.Children.Add(Chip($"⇵ {S(l, "label")}: Git changes to commit, pull or push - Files", Orange, "The Git bar of the Files tab", () => Show(tEffective)));
                }
            if (docsUnreachable > 0) attention.Children.Add(Chip($"⚠ {docsUnreachable} reference source(s) not reachable - Reference", Orange, "A folder or share the agent cannot read", () => Show(tDocs)));
            var urgent = Arr(data.TryGetValue("feedback", out var fo) ? fo : null).Count(f => S(f, "status") == "open" && S(f, "severity") is "blocker" or "major");
            if (urgent > 0) attention.Children.Add(Chip($"! {urgent} open blocker/major feedback report(s) - Feedback", Orange, "Share them with the TcAgent developers", () => Show(tFeedback)));
            if (attention.Children.Count == 0) attention.Children.Add(Chip("✓ nothing - everything is up to date", Green, null, null));

            // the runs: rates only from MinRuns runs on, trends from 20 (the last 10 against the 10 before)
            tiles.Children.Clear();
            var o = data["overview"] as Dictionary<string, object>;
            var series = Arr(o?["series"]).ToList();
            int runs = (int)(N(o, "runs") ?? 0);
            bool few = runs < MinRuns;
            Heading(runsHeading, "Workflow runs", runs == 0 ? "none yet - start a workflow in the chat (e.g. new-feature)"
                                                   : $"{runs} run{(runs == 1 ? "" : "s")} so far" + (runs < 20 ? "; trends appear from 20 runs" : "; the last 10 against the 10 before"));
            string Rate(string v) => few ? "—" : v;
            var fewNote = $"needs {MinRuns} runs";
            tiles.Children.Add(Tile("Runs", runs.ToString(CultureInfo.InvariantCulture), null, null, false, null, "all recorded runs of the project (the last 100 are kept)",
                                    Arr(data["workflows"]).Count() == 1 ? S(Arr(data["workflows"]).First(), "name") : null));
            tiles.Children.Add(Tile("First-time-right", Rate(Pct(N(o, "firstTimeRight"))), N(o, "firstTimeRight"), N(o, "firstTimeRightPrev"), true,
                                    series.Select(x => B(x, "firstTimeRight") ? 1.0 : 0.0).ToList(), "runs without a failed check and without a correction (last 10 runs)", few ? fewNote : null));
            tiles.Children.Add(Tile("Failed checks / run", Rate(F(N(o, "failedChecks"))), N(o, "failedChecks"), N(o, "failedChecksPrev"), false,
                                    series.Select(x => N(x, "failedChecks") ?? 0).ToList(), "tc_check / tc_build with compiler errors per run (last 10 runs)", few ? fewNote : null));
            tiles.Children.Add(Tile("Corrections / run", Rate(F(N(o, "corrections"))), N(o, "corrections"), N(o, "correctionsPrev"), false,
                                    series.Select(x => N(x, "corrections") ?? 0).ToList(), "your 'no' to a confirmation or an answer other than the recommended one", few ? fewNote : null));
            tiles.Children.Add(Tile("Minutes / run", Rate(F(N(o, "minutes"))), N(o, "minutes"), N(o, "minutesPrev"), false,
                                    series.Select(x => N(x, "minutes") ?? 0).ToList(), "duration of a workflow run", few ? fewNote : null));
            tiles.Children.Add(Tile("Gate stops", N(o, "gateStops")?.ToString("0") ?? "0", N(o, "gateStops"), N(o, "gateStopsPrev"), false, null,
                                    "checkpoints not met and activations refused because of build errors (last 10 runs)", null));

            var lines = new List<Line>();
            foreach (var w in Arr(data["workflows"]))
            {
                var chg = Change(N(w, "failedChecks"), N(w, "failedChecksPrev"), false);
                bool wfew = (N(w, "runs") ?? 0) < MinRuns;
                lines.Add(new Line
                {
                    D = w, C1 = S(w, "name"), C2 = S(w, "runs"), C3 = wfew ? "—" : Pct(N(w, "firstTimeRight")),
                    C4 = wfew ? "—" : F(N(w, "failedChecks")) + (chg.Text.Length > 0 ? "   " + chg.Text : ""),
                    C5 = wfew ? "—" : F(N(w, "corrections")), C6 = S(w, "proposals"), Mark = chg.Brush ?? Gray
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
            if (proposals.Children.Count == 0) proposals.Children.Add(new TextBlock { Text = "None - the run logs, lessons and knowledge files show nothing to change.", Foreground = Gray });
            var errors = Arr(data["topErrors"]).Select(e => $"{S(e, "code")} ({S(e, "count")}×)").ToList();
            facts.Text = (errors.Count > 0 ? "Most frequent compiler errors in failed checks: " + string.Join(", ", errors) + "\n" : "") +
                         "Run logs are kept locally (%LOCALAPPDATA%\\TcAgent\\runs) - counts and names only, no code.";
        }

        // the Files tab with one kind, only what applies
        void ShowKind(string kind)
        {
            kindBox.SelectedIndex = Array.IndexOf(KindIds, kind) + 1;
            levelBox.SelectedIndex = 0;
            filter.Text = "";
            onlyApplied.IsChecked = true;
            FillEntries();
            Show(tEffective);
        }

        // the badges of the tabs: how many items there need attention
        void Badges()
        {
            var inst = Arr(data?["installed"]).Select(i => S(i, "state") ?? "").ToList();
            var src = inst.Count(s => s.Contains("changed locally") || s.Contains("newer upstream") || s.StartsWith("missing"));
            Badge(tSources, src, $"{src} installed item(s) changed locally, newer in their source or missing");
            var files = Arr(data?["entries"]).Count(e => (S(e, "sourceState") ?? "").Contains("changed locally")) +
                        Arr(data?["layers"]).Count(l => l.TryGetValue("git", out var go) && go is Dictionary<string, object> g && !B(g, "same") && S(l, "level") != null &&
                                                        (((g.TryGetValue("changes", out var c) ? c as Dictionary<string, object> : null)?.Count ?? 0) > 0 || N(g, "behind") > 0 || N(g, "ahead") > 0));
            Badge(tEffective, files, $"{files} file(s) changed locally or level(s) with Git changes");
            Badge(tDocs, docsUnreachable, $"{docsUnreachable} reference source(s) not reachable", RefDocsTip);
        }

        static string F(double? v) => v?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        static string Pct(double? v) => v == null ? "—" : v.Value.ToString("0", CultureInfo.InvariantCulture) + " %";

        /// <summary>Change against the previous 10 runs: arrow, value, green when better.</summary>
        static (string Text, Brush Brush) Change(double? now, double? prev, bool higherIsBetter)
        {
            if (now == null || prev == null) return ("", null);
            var d = now.Value - prev.Value;
            if (Math.Abs(d) < 0.05) return ("= previous", Gray);
            bool better = higherIsBetter ? d > 0 : d < 0;
            return ((d > 0 ? "▲ " : "▼ ") + Math.Abs(d).ToString("0.0", CultureInfo.InvariantCulture) + " vs. previous 10", better ? Green : Orange);   // red is for errors only
        }

        // a KPI tile; note replaces the change line (e.g. "needs 5 runs" while there are fewer)
        Border Tile(string title, string value, double? now, double? prev, bool higherIsBetter, List<double> spark, string tip, string note)
        {
            var sp = new StackPanel { Margin = new Thickness(10, 8, 10, 8), MinWidth = 140 };
            var t = new TextBlock { Text = title, Opacity = 0.8, FontSize = 11 };
            var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold };
            foreach (var x in new[] { t, v }) x.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            if (value == "—") v.Opacity = 0.5;
            sp.Children.Add(t); sp.Children.Add(v);
            var (text, brush) = note != null ? (note, Gray) : Change(now, prev, higherIsBetter);
            sp.Children.Add(new TextBlock { Text = text.Length > 0 ? text : " ", FontSize = 11, Foreground = brush ?? Gray });
            if (note == null && spark != null && spark.Count > 1) sp.Children.Add(Sparkline(spark.Skip(Math.Max(0, spark.Count - 30)).ToList(), 140, 26));
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

        // ------------------------------------------------------------------ files
        string KindFilter => kindBox.SelectedIndex > 0 ? KindIds[kindBox.SelectedIndex - 1] : null;

        void FillEntries()
        {
            if (data == null) return;
            var kind = KindFilter;
            var level = levelBox.SelectedIndex > 0 ? levelBox.SelectedItem as string : null;
            var q = filter.Text.Trim();
            var keep = S(Entry(), "path");
            var lines = new List<Line>();
            foreach (var e in Arr(data["entries"]))
            {
                if (kind != null && S(e, "kind") != kind) continue;
                if (level != null && S(e, "label") != level) continue;
                if (onlyApplied.IsChecked == true && !B(e, "active")) continue;
                var (state, mark) = EntryState(e);
                var src = S(e, "source") is string so ? so + (S(e, "license") is string li ? " · " + li : "") : S(e, "detached") is string dt ? "own version (from " + dt + ")" : "–";
                var l = new Line { D = e, C1 = S(e, "name"), C2 = KindName(S(e, "kind")), C3 = S(e, "label"), C4 = state, C5 = S(e, "reason") ?? "", C6 = src, C7 = S(e, "git") ?? "",
                                   Mark = mark, Tip = S(e, "path") };
                if (q.Length > 0 && !$"{l.C1} {l.C2} {l.C3} {l.C4} {l.C5} {l.C6}".ToLowerInvariant().Contains(q.ToLowerInvariant())) continue;
                lines.Add(l);
            }
            entries.ItemsSource = lines;
            // Git and Why only while a line has something in them - otherwise the room goes to the other columns
            if (entries.View is GridView gv && gv.Columns.Count >= 7)
            {
                gv.Columns[5].Width = lines.Any(x => x.C7.Length > 0) ? 60 : 0;
                gv.Columns[6].Width = lines.Any(x => x.C5.Length > 0) ? 260 : 0;
            }
            entries.SelectedItem = lines.FirstOrDefault(x => keep != null && S(x.D, "path") == keep);
            ShowEntry();
            Summary();
        }

        /// <summary>The state of a file as symbol and word: applies, replaced, switched off - and for an item from a source its changes.</summary>
        static (string Text, Brush Mark) EntryState(Dictionary<string, object> e)
        {
            var st = S(e, "status");
            var lockedNote = B(e, "locked") ? ", locked" : "";
            if (st == "replaced") return ("↷ replaced" + lockedNote, Gray);
            if (st == "disabled") return ("⊘ switched off" + lockedNote, Gray);
            var (srcText, srcMark) = SourceState(S(e, "sourceState"));
            if (srcText != null) return (srcText, srcMark);
            return ((st == "forced" ? "✓ applies (forced)" : "✓ applies") + lockedNote, Green);
        }

        /// <summary>The state of an item from a source: changed locally (orange), newer in the source (blue), both changed (red).</summary>
        static (string Text, Brush Mark) SourceState(string state)
        {
            bool mine = (state ?? "").Contains("changed locally"), theirs = (state ?? "").Contains("newer upstream");
            return mine && theirs ? ("⇅ both changed", Orange) : mine ? ("✎ changed locally", Orange) : theirs ? ("↑ newer in the source", Blue)
                 : (state ?? "").StartsWith("missing") ? ("✗ missing", Red) : (null, null);
        }

        // the selected file: name and state, the facts, the actions that fit, the file folded below
        void ShowEntry()
        {
            var l = entries.SelectedItem as Line;
            detailTitle.Inlines.Clear();
            detailKv.Children.Clear();
            detailKv.RowDefinitions.Clear();
            var exp = detail.Parent as Expander;
            if (l == null)
            {
                detailTitle.Inlines.Add(new System.Windows.Documents.Run("Select a file") { FontWeight = FontWeights.Normal, Foreground = Gray });
                Row("", "A later level replaces a file with the same name; locked files can only be switched off by the company level.");
                detail.Text = "";
                if (exp != null) { exp.Header = "Preview"; exp.IsExpanded = false; exp.Visibility = Visibility.Collapsed; }
                return;
            }
            var d = l.D;
            var (state, mark) = EntryState(d);
            if (exp != null) exp.Visibility = Visibility.Visible;
            detailTitle.Inlines.Add(new System.Windows.Documents.Run(S(d, "name")));
            detailTitle.Inlines.Add(new System.Windows.Documents.Run("   " + state) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = mark });
            var path = S(d, "path");
            Row("Level", $"{S(d, "label")} · {KindName(S(d, "kind"))} · {MiddlePath(UserPath(path))} ", ("Open folder", OpenFolder), ("Copy path", () => CopyPath(path)));
            if (S(d, "description") is string de) Row("Description", de);
            if (S(d, "reason") is string r && r.Length > 0) Row("Why", r);
            if (S(d, "git") is string gs) Row("Git", gs + " - not committed yet");
            if (S(d, "source") is string so)
            {
                Row("Source", $"{so} · license {S(d, "license") ?? "unknown"}" + (S(d, "sourceState") is string ss ? " · " + (SourceState(ss).Text ?? (ss == "ok" ? "✓ as installed" : ss)) : ""));
                (string, string) It() => (S(d, "name"), S(d, "installLevel") ?? (S(d, "label") ?? "").Split(' ')[0]);
                var ssv = S(d, "sourceState") ?? "";
                if (ssv.Contains("changed locally"))
                    Row("Your changes", "", (SrcCompare, () => { var (n, lv) = It(); CompareSource(n, lv, detail); }), (SrcMerge, () => { var (n, lv) = It(); MergeSource(n, lv, detail); }),
                        (SrcPropose, () => { var (n, lv) = It(); ProposeSource(n, lv, detail); }), (SrcRevert, () => { var (n, lv) = It(); RevertSource(n, lv); }));
                else if (ssv.Contains("newer upstream"))
                    Row("In the source", "a newer version ", (SrcChanges, () => RunAsyncTo($"sources diff --item {Quote(S(d, "name"))}", detail)), (SrcMerge, () => { var (n, lv) = It(); MergeSource(n, lv, detail); }));
            }
            else if (S(d, "detached") is string dt) Row("Source", "own version (from " + dt + ")");
            // the file itself, folded (the first 60 lines)
            try
            {
                var f = Directory.Exists(path) ? new[] { "SKILL.md", "WORKFLOW.md" }.Select(m => Path.Combine(path, m)).FirstOrDefault(File.Exists) : path;
                detail.Text = f != null && File.Exists(f) ? string.Join("\n", File.ReadLines(f).Take(60)) : "";
                if (exp != null) exp.Header = f != null ? "Preview of " + Path.GetFileName(f) : "Preview";
            }
            catch { detail.Text = ""; }
        }

        // one line of the detail: a label, a text and links to actions
        void Row(string label, string text, params (string Label, Action Act)[] links)
        {
            int r = detailKv.RowDefinitions.Count;
            detailKv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 1, 8, 1) };
            var v = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            foreach (var t in new[] { k, v }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            v.Inlines.Add(new System.Windows.Documents.Run(text));
            for (int i = 0; i < links.Length; i++)
            {
                if (i > 0) v.Inlines.Add(new System.Windows.Documents.Run(" · "));
                var act = links[i].Act;
                var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(links[i].Label)) { Foreground = Blue };
                h.Click += (s, e) => act();
                v.Inlines.Add(h);
            }
            Grid.SetRow(k, r); Grid.SetRow(v, r);
            if (label.Length > 0) Grid.SetColumn(v, 1); else Grid.SetColumnSpan(v, 2);   // a note without a label uses the whole width
            detailKv.Children.Add(k); detailKv.Children.Add(v);
        }

        // ------------------------------------------------------------------ new / open folder / move / delete
        Dictionary<string, object> LayerOf(Dictionary<string, object> e) => Arr(data?["layers"]).FirstOrDefault(l => S(l, "label") == S(e, "label"));

        // the levels a file can be created at / moved to: user, project, team(s), company, every platform (built-in never);
        // a shared level in Git gets the file in its working copy - commit and push in the Git bar (pull request)
        List<(string Label, string Level, bool Pr)> Levels(bool forNew) =>
            Arr(data?.TryGetValue("levels", out var lv) == true ? lv : null)
                .Select(l => (S(l, "label") + (B(l, "viaPullRequest") ? "  (Git: commit & push in the Git bar - pull request)" : ""), S(l, "level"), B(l, "viaPullRequest"))).ToList();

        async void NewItem(bool existing)
        {
            var levels = Levels(true);
            if (levels.Count == 0) { Hint("No level to create files at."); return; }
            var kinds = new[] { "coding-rules", "risk-rules", "skills", "workflows", "lessons", "prompts" };
            var names = kinds.Select(KindName).ToArray();
            var kind0 = KindName(kinds.Contains(KindFilter) ? KindFilter : "skills");
            var level0 = levels.Any(x => x.Level == "project") ? levels.First(x => x.Level == "project").Label : levels[0].Label;
            var r = existing
                ? Prompt("Add an existing file", ("Kind", names, kind0), ("Level", levels.Select(x => x.Label).ToArray(), level0))
                : Ask("New knowledge file", new[] { 1 }, ("Kind", names, kind0), ("Name (other characters become hyphens)", null, ""), ("Level", levels.Select(x => x.Label).ToArray(), level0));
            if (r == null) return;
            r = existing ? new[] { r[0], null, "", r[1] } : new[] { r[0], null, r[1], r[2] };
            var kind = kinds[Array.IndexOf(names, r[0])];
            var level = levels.First(x => x.Label == r[3]).Level;
            string args;
            if (existing)
            {
                var skillLike = kind is "skills" or "workflows";
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = skillLike ? $"Pick the {(kind == "skills" ? "SKILL.md" : "WORKFLOW.md")} of the {kind.TrimEnd('s')} (its folder is taken over)" : "Pick the Markdown file",
                    Filter = skillLike ? (kind == "skills" ? "SKILL.md|SKILL.md" : "WORKFLOW.md|WORKFLOW.md") : "Markdown|*.md"
                };
                if (dlg.ShowDialog() != true) return;
                args = $"knowledge new --key {Quote(kind + "/x")} --path {Quote(dlg.FileName)} --level {Quote(level)}";
            }
            else args = $"knowledge new --key {Quote(kind + "/" + r[2].Trim())} --level {Quote(level)}";
            var (ok, output) = await Cli(args, existing ? "Adding the file" : "Creating the file");
            Report(ok, output, "New");
            if (ok && levels.First(x => x.Label == r[3]).Pr)
                Info("The file is in the working copy of the shared repository - commit and push it in the Git bar of the Files tab (pull request).");
            if (ok && File.Exists(output.Trim()))
                try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(output.Trim()); } catch { }
            Load();
        }

        // company, team and platform roots: settings.json "env" (TcAgent.exe reads it on every start; Set up clients passes it on)
        async void EditLevels()
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
                    var (ok, output) = await Cli($"knowledge settings --name {names[i]} --url {Quote(r[i].Trim())}", "Saving the levels");
                    all &= ok; outs.Add(output);
                }
            if (outs.Count == 0) { status.Text = "Levels unchanged."; return; }
            Report(all, string.Join("\n", outs.Distinct()), "Levels");
            if (all) Info("Levels saved - this window uses them at once. Run \"⋯ > Set up clients\" in the chat so the AI clients get them too (and start a new chat).");
            Load();
        }

        async void RenameItem()
        {
            var d = Entry();
            if (d == null) { Hint("Select a file first."); return; }
            var r = Ask("Rename " + S(d, "key"), new[] { 0 }, ("New name (other characters become hyphens)", null, S(d, "name") ?? ""));
            if (r == null || r[0].Trim() == S(d, "name")) return;
            var (ok, output) = await Cli($"knowledge rename --path {Quote(S(d, "path"))} --name {Quote(r[0].Trim())}", "Renaming");
            Report(ok, output, "Rename");
            Load();
        }

        void OpenFolder() => ShowInExplorer(S(Entry(), "path"));

        async void MoveItem()
        {
            if (!(entries.SelectedItem is Line l)) return;
            var levels = Levels(false).Where(x => !x.Label.StartsWith(S(l.D, "label") + "  ") && x.Label != S(l.D, "label")).ToList();
            if (levels.Count == 0) { Hint("There is no other level to move the file to."); return; }
            var r = Prompt($"Move {S(l.D, "key")} ({S(l.D, "label")})", ("To level", levels.Select(x => x.Label).ToArray(), null));
            if (r == null) return;
            var target = levels.First(x => x.Label == r[0]);
            var (mok, output) = await Cli($"knowledge move --path {Quote(S(l.D, "path"))} --level {Quote(target.Level)}", "Moving");
            Report(mok, output, "Move");
            Load();
        }

        async void DeleteItem()
        {
            if (!(entries.SelectedItem is Line l)) return;
            if (!(LayerOf(l.D) is Dictionary<string, object> ly && B(ly, "deletable")))
            { Hint($"Files of the {S(l.D, "label")} level are not deleted here - switch them off instead (Switch off ▾)."); return; }
            var what = S(l.D, "kind") is "skills" or "workflows" ? "the folder of " + S(l.D, "key") : S(l.D, "path");
            if (MessageBox.Show($"Delete {what}?\n\nThe level is backed up first (Backups tab).", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (dok, output) = await Cli($"knowledge delete --path {Quote(S(l.D, "path"))}", "Deleting");
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
                t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                t.Inlines.Add(new System.Windows.Documents.Run($"Git · {S(l, "label")} · {Path.GetFileName(S(g, "repo"))} ({S(g, "branch")}): ") { FontWeight = FontWeights.SemiBold });
                var parts = new List<string>();
                if (ch > 0) parts.Add($"{ch} local change(s) to commit");
                if (ahead > 0) parts.Add($"{ahead} commit(s) to push");
                if (behind > 0) parts.Add($"{behind} new commit(s) in the repository - pull");
                if (!B(g, "upstream")) parts.Add("no remote branch");
                var todo = behind > 0 || ch > 0 || ahead > 0;
                t.Inlines.Add(new System.Windows.Documents.Run((todo ? "⚠ " : "✓ ") + (parts.Count > 0 ? string.Join(" · ", parts) : "up to date (as of the last fetch)"))
                                  { Foreground = todo ? Orange : Green });
                row.Children.Add(t);
                var level = S(l, "level"); bool pr = B(l, "viaPullRequest");
                foreach (var (label, action, tip) in new[] { ("Fetch", "fetch", "Look for new commits in the repository"), ("Pull", "pull", "Take over the new commits (fast-forward)"),
                                                            ("Commit …", "commit", "Commit the local changes of this level"),
                                                            ("Push", "push", pr ? "Push to a new branch - then open a pull request (shared level)" : "Push the current branch") })
                {
                    var b = Text(label, tip);
                    b.Click += (s, e) => GitAction(level, action);
                    b.IsEnabled = action switch { "pull" => B(g, "upstream"), "commit" => ch > 0, "push" => ahead > 0, _ => true };
                    row.Children.Add(b);
                }
                gitPanel.Children.Add(row);
            }
        }

        async void GitAction(string level, string action)
        {
            string message = null;
            if (action == "commit")
            {
                var r = Ask($"Commit the changes of {level}", new[] { 0 }, ("Message", null, "Update knowledge"));
                if (r == null) return;
                message = r[0].Trim();
            }
            var args = $"knowledge git --action {action} --level {Quote(level)}" + (message != null ? " --message " + Quote(message) : "");
            var (ok, output) = await Cli(args, $"git {action} ({level})");
            Report(ok, output, "Git " + action);
            Load();
        }

        /// <summary>The result of an action: success in the status line, a failure as a dialog (it must not go unnoticed).</summary>
        void Report(bool ok, string output, string what)
        {
            output = (output ?? "").Trim();
            if (ok) status.Text = output.Split('\n')[0];
            else
            {
                status.Text = what + " failed: " + output.Split('\n')[0];
                MessageBox.Show(output.Length > 0 ? output : "No output.", "TcAgent Knowledge - " + what, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>TcAgent.exe in the background: the IDE stays responsive, a thin bar under the tabs and the status line show what runs.</summary>
        async System.Threading.Tasks.Task<(bool ok, string output)> Cli(string args, string what = null)
        {
            var sln = TcUnitControl.SolutionPath();   // the IDE only answers on its own thread
            Busy(+1, what);
            try { return await System.Threading.Tasks.Task.Run(() => RunCli(args, sln)); }
            finally { Busy(-1, null); }
        }

        void Busy(int delta, string what)
        {
            busy = Math.Max(0, busy + delta);
            progress.Visibility = busy > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (what != null) status.Text = what + " …";
        }

        static (bool ok, string output) RunCli(string args, string sln)
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) return (false, "TcAgent.exe not found.");
            try
            {
                // stdin closed at once: a TcAgent.exe that does not know the command (older than the extension) starts the MCP
                // server - which then ends instead of waiting for input
                var psi = new ProcessStartInfo(exe, args + (string.IsNullOrEmpty(sln) ? "" : " --solution " + Quote(sln)))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                psi.EnvironmentVariables["TCAGENT_CLIENT"] = "window:knowledge";
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var err = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(120_000);
                    return (p.ExitCode == 0, (o + " " + err.Result).Trim());
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ------------------------------------------------------------------ dialogs
        /// <summary>A dialog in the colors of the IDE (light, dark, blue theme).</summary>
        internal static Window Dialog(string title, double minWidth, bool fixedSize = true)
        {
            var w = new Window { Title = title, MinWidth = minWidth, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow, ShowInTaskbar = false,
                                 ResizeMode = fixedSize ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip, SizeToContent = fixedSize ? SizeToContent.WidthAndHeight : SizeToContent.Manual };
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            return w;
        }

        // OK and Cancel at the bottom right
        internal static StackPanel Buttons(Window w, Button ok, params Button[] more)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            ok.MinWidth = 86; ok.IsDefault = true; ok.Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(ok);
            foreach (var b in more) { b.MinWidth = 86; b.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(b); }
            bar.Children.Add(new Button { Content = "Cancel", MinWidth = 86, IsCancel = true });
            return bar;
        }

        /// <summary>A small dialog: one field per entry (choices = combo box, null = text box). Returns the values or null.</summary>
        string[] Prompt(string title, params (string Label, string[] Choices, string Value)[] fields) => Ask(title, null, fields);

        /// <summary>Like Prompt; required = the indexes of the text fields that must not be empty - OK stays off until they are filled.</summary>
        string[] Ask(string title, int[] required, params (string Label, string[] Choices, string Value)[] fields)
        {
            var w = Dialog(title, 420);
            var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 12) };
            var getters = new List<Func<string>>();
            var okB = new Button { Content = "OK" };
            void Check() => okB.IsEnabled = required == null || required.All(i => (getters[i]() ?? "").Trim().Length > 0);
            Control first = null;
            foreach (var f in fields)
            {
                sp.Children.Add(new TextBlock { Text = f.Label, Margin = new Thickness(0, 8, 0, 3), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
                if (f.Choices != null)
                {
                    var cb = new ComboBox { ItemsSource = f.Choices, SelectedItem = f.Value ?? f.Choices.FirstOrDefault(), MinWidth = 400 };
                    sp.Children.Add(cb); getters.Add(() => cb.SelectedItem as string); first ??= cb;
                }
                else
                {
                    var tb = new TextBox { Text = f.Value ?? "", MinWidth = 400, Padding = new Thickness(2) };
                    tb.TextChanged += (s, e) => Check();
                    sp.Children.Add(tb); getters.Add(() => tb.Text); first ??= tb;
                }
            }
            okB.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(Buttons(w, okB));
            w.Content = sp;
            // the first text field gets the focus; required text fields first
            var focus = required != null && required.Length > 0 ? sp.Children.OfType<Control>().Where(c => c is TextBox || c is ComboBox).ElementAtOrDefault(required[0]) ?? first : first;
            w.Loaded += (s, e) => { focus?.Focus(); (focus as TextBox)?.SelectAll(); };
            Check();
            return w.ShowDialog() == true ? getters.Select(g => g()).ToArray() : null;
        }

        /// <summary>A choice from a long list: a filter box and the list; double-click or Enter = OK.</summary>
        string Pick(string title, string text, string[] choices, string selected)
        {
            var w = Dialog(title, 560, false);
            w.Width = 640; w.Height = 460;
            var box = new TextBox { Padding = new Thickness(2) };
            var list = new ListBox { ItemsSource = choices, SelectedItem = selected ?? choices.FirstOrDefault(), Margin = new Thickness(0, 6, 0, 0) };
            box.TextChanged += (s, e) =>
            {
                var q = box.Text.Trim().ToLowerInvariant();
                list.ItemsSource = q.Length == 0 ? choices : choices.Where(c => c.ToLowerInvariant().Contains(q)).ToArray();
                if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0;
            };
            var okB = new Button { Content = "OK" };
            okB.Click += (s, e) => { if (list.SelectedItem != null) w.DialogResult = true; };
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem != null) w.DialogResult = true; };
            list.SelectionChanged += (s, e) => okB.IsEnabled = list.SelectedItem != null;
            var dp = new DockPanel { Margin = new Thickness(14, 8, 14, 12) };
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            head.Children.Add(new TextBlock { Text = "Filter:", Margin = new Thickness(0, 0, 0, 2) });
            head.Children.Add(box);
            DockPanel.SetDock(head, Dock.Top); dp.Children.Add(head);
            var bar = Buttons(w, okB);
            DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
            dp.Children.Add(list);
            w.Content = dp;
            w.Loaded += (s, e) => box.Focus();
            return w.ShowDialog() == true ? list.SelectedItem as string : null;
        }

        /// <summary>A question with a long text below it (a difference, a plan, a list of files) - the text scrolls. True = yes.</summary>
        bool Confirm(string title, string question, string text, string yes, bool warn = false)
        {
            var w = Dialog(title, 560, false);
            w.Width = 780; w.Height = string.IsNullOrWhiteSpace(text) ? 230 : 500;
            var q = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            if (warn) q.Inlines.Add(new System.Windows.Documents.Run("⚠ ") { Foreground = Orange, FontWeight = FontWeights.SemiBold });
            q.Inlines.Add(new System.Windows.Documents.Run(question));
            var box = new TextBox { Text = (text ?? "").Trim(), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(4),
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var okB = new Button { Content = yes };
            okB.Click += (s, e) => w.DialogResult = true;
            var bar = Buttons(w, okB);
            if (warn) { okB.IsDefault = false; ((Button)bar.Children[bar.Children.Count - 1]).IsDefault = true; }   // a risky action: Enter cancels
            var dp = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
            DockPanel.SetDock(q, Dock.Top); dp.Children.Add(q);
            DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
            if (!string.IsNullOrWhiteSpace(text)) dp.Children.Add(box);
            w.Content = dp;
            return w.ShowDialog() == true;
        }

        void OpenSelected()
        {
            var p = S(Entry(), "path");
            if (p == null) { Hint("Select a file first."); return; }
            OpenInEditor(p);
        }

        async void Switch(string action, string level)
        {
            var key = S(Entry(), "key");
            if (key == null) return;
            var (ok, output) = await Cli($"knowledge {action} --key {Quote(key)} --level {level}", action == "disable" ? "Switching off" : "Switching on");
            Report(ok, output, action == "disable" ? "Switch off" : "Switch on");
            Load();
        }

        async void SwitchOn()
        {
            var key = S(Entry(), "key");
            if (key == null) return;
            var outs = new List<string>();
            foreach (var level in new[] { "project", "user" })
            {
                var (ok, output) = await Cli($"knowledge enable --key {Quote(key)} --level {level}", "Switching on");
                if (ok) outs.Add(output.Trim());
                else if (level == "project" && string.IsNullOrEmpty(S(data, "solutionDir"))) continue;
                else { Report(false, output, "Switch on"); break; }
            }
            status.Text = string.Join(" · ", outs.Where(x => x.Length > 0).Distinct());
            Load();
        }

        // ------------------------------------------------------------------ sources
        void FillSources()
        {
            var nSrc = Arr(data["sources"]).Count();
            Heading(sourcesHeading, $"Sources ({nSrc})", "Git repositories with skills, workflows, rules and lessons");
            Heading(installedHeading, $"Installed from sources ({Arr(data["installed"]).Count()})", "fixed to the installed version until you update");
            sourceList.ItemsSource = Arr(data["sources"]).Select(s => new Line { D = s, C1 = S(s, "name"), C2 = S(s, "level"), C3 = S(s, "url"), C4 = S(s, "ref") == "HEAD" ? "default" : S(s, "ref"),
                                                                                 Mark = Blue, Tip = S(s, "url") }).ToList();
            installedList.ItemsSource = Arr(data["installed"]).Select(i =>
            {
                var st = S(i, "state") ?? "";
                var (stateText, stateBrush) = SourceState(st);
                return new Line
                {
                    D = i, C1 = S(i, "key"), C2 = S(i, "level"), C3 = stateText ?? (st == "ok" ? "✓ as installed" : st), C4 = S(i, "source") + " @" + Short(S(i, "commit")),
                    C5 = S(i, "license"), C6 = S(i, "installed"), Mark = stateBrush ?? (st == "ok" ? Green : Orange), Tip = st + " · " + S(i, "url")
                };
            }).ToList();
        }

        static string Short(string commit) => (commit ?? "").Substring(0, Math.Min(8, (commit ?? "").Length));

        // ------------------------------------------------------------------ sources: edit, browse and install, update, diff
        async void RunAsync(string args, bool toDetail, bool reload = false, string what = null)
        {
            var (ok, output) = await Cli(args, what ?? "TcAgent.exe " + string.Join(" ", args.Split(' ').Take(2)));
            if (toDetail) Out(sourceDetail, output);
            Report(ok, output, what ?? args.Split(' ')[0]);
            if (reload) Load();
        }

        // the levels sources can be defined at (sources.json): user and project
        string[] SourceLevels() => string.IsNullOrEmpty(S(data, "solutionDir")) ? new[] { "personal" } : new[] { "personal", "project" };

        async void EditSource(Line l)
        {
            var d = l?.D;
            var r = Ask(d == null ? "New source" : "Edit source " + S(d, "name"), new[] { 0, 1 },
                        ("Name (lowercase, hyphens)", null, S(d, "name") ?? ""), ("Git URL or path", null, S(d, "url") ?? ""),
                        ("Branch or tag (empty = the default branch)", null, S(d, "ref") == "HEAD" ? "" : S(d, "ref") ?? ""),
                        ("Defined for", SourceLevels(), S(d, "level") == "project" ? "project" : "personal"));
            if (r == null) return;
            // a renamed or moved source: the old entry goes
            if (d != null && (S(d, "name") != r[0].Trim() || (S(d, "level") == "project" ? "project" : "personal") != r[3]))
                await Cli($"sources remove --name {Quote(S(d, "name"))} --level {(S(d, "level") == "project" ? "project" : "personal")}");
            var (aok, output) = await Cli($"sources add --name {Quote(r[0].Trim())} --url {Quote(r[1].Trim())} --level {r[3]}" + (r[2].Trim().Length > 0 ? " --ref " + Quote(r[2].Trim()) : ""), "Saving the source");
            Report(aok, output, "Source");
            if (aok && d == null) Info($"Source {r[0].Trim()} added - \"Browse and install …\" shows what it offers.", "Browse and install …", BrowseSource);
            Load();
        }

        async void RemoveSource()
        {
            if (!(sourceList.SelectedItem is Line l)) { Hint("Select a source first."); return; }
            if (MessageBox.Show($"Delete the source {S(l.D, "name")} from the list?\n\nItems installed from it stay (and keep their installed version).", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = await Cli($"sources remove --name {Quote(S(l.D, "name"))} --level {(S(l.D, "level") == "project" ? "project" : "personal")}", "Deleting the source");
            Report(ok, output, "Delete source");
            Load();
        }

        async void BrowseSource()
        {
            if (data != null && !Arr(data["sources"]).Any())
            { Info("There is no source yet - \"New …\" adds a Git repository first.", "New …", () => EditSource(null)); return; }
            var name = S((sourceList.SelectedItem as Line)?.D, "name");
            var (ok, output) = await Cli("sources search" + (name != null ? " --name " + Quote(name) : ""), "Reading " + (name ?? "all sources") + " (git fetch)");
            if (!ok) { Report(false, output, "Browse"); return; }
            List<Dictionary<string, object>> items;
            try { items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<ArrayList>(output).OfType<Dictionary<string, object>>().ToList(); }
            catch { Report(false, output, "Browse"); return; }
            status.Text = $"{items.Count} item(s) offered.";
            InstallDialog(items, name);
        }

        static List<string> Strings(Dictionary<string, object> d, string k) => ((d != null && d.TryGetValue(k, out var v) ? v as IEnumerable : null) ?? new object[0]).Cast<object>().Select(x => x.ToString()).ToList();

        // what a source offers: search, kind, state (installed already?), license, scripts; the details of the selected one;
        // several can be installed at once - license, files and scripts are confirmed first
        void InstallDialog(List<Dictionary<string, object>> items, string sourceName)
        {
            var w = Dialog("Browse and install" + (sourceName != null ? " - " + sourceName : " - all sources"), 760, false);
            w.Width = 1000; w.Height = 620;
            var installed = Arr(data?["installed"]).ToList();
            string InstalledAt(Dictionary<string, object> i) =>
                string.Join(", ", installed.Where(x => (S(x, "key") ?? "").Split('/').Last() == S(i, "name") && ((S(x, "key") ?? "").StartsWith(S(i, "kind") + "/") || !(S(x, "key") ?? "").Contains("/")))
                                            .Select(x => S(x, "level")).Distinct());
            var lines = items.Select(i =>
            {
                var at = InstalledAt(i);
                var cls = S(i, "licenseClass");
                var refused = cls is "source-available" or "none";
                return new Line
                {
                    D = i, C1 = S(i, "name"), C2 = KindName(S(i, "kind")), C3 = S(i, "source"),
                    C4 = refused ? "⊘ not installable" : at.Length > 0 ? "✓ installed (" + at + ")" : "○ not installed",
                    C5 = S(i, "license") + (cls == "accepted" ? "" : " (" + cls + ")"), C6 = Strings(i, "scripts").Count > 0 ? "⚠ yes" : "", C7 = S(i, "description"),
                    Mark = refused ? Orange : at.Length > 0 ? Green : Gray, Tip = S(i, "refusal") ?? S(i, "path")
                };
            }).ToList();
            var list = NewList();
            Columns(list, ("", 30, "?" + nameof(Line.On)), ("Name", 190, nameof(Line.C1)), ("Kind", 90, nameof(Line.C2)), ("Source", 120, nameof(Line.C3)), ("State", 150, "!" + nameof(Line.C4)),
                    ("License", 110, nameof(Line.C5)), ("Scripts", 60, nameof(Line.C6)), ("Description", 360, nameof(Line.C7)));
            var query = new TextBox();
            var kinds = new[] { "All kinds" }.Concat(lines.Select(l => l.C2).Distinct()).ToArray();
            var kind = new ComboBox { ItemsSource = kinds, SelectedIndex = 0, MinWidth = 110, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var notYet = new CheckBox { Content = "Only not installed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            void Filter()
            {
                var q = query.Text.Trim().ToLowerInvariant();
                list.ItemsSource = lines.Where(l => (kind.SelectedIndex <= 0 || l.C2 == (string)kind.SelectedItem) && (notYet.IsChecked != true || l.C4.StartsWith("○")) &&
                                                    (q.Length == 0 || $"{l.C1} {l.C2} {l.C3} {l.C5} {l.C7}".ToLowerInvariant().Contains(q))).ToList();
            }
            query.TextChanged += (s, e) => Filter();
            kind.SelectionChanged += (s, e) => Filter();
            notYet.Click += (s, e) => Filter();
            Filter();
            // tick: the shown items, none (the ticks stay when the filter changes)
            var tickAll = Text("Tick all shown", "Tick every item the filter shows");
            var tickNone = Text("Untick all", "Remove every tick");
            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var e in new UIElement[] { SearchBox(query, "Search name, license, description", 280), kind, notYet, TcUnitControl.Separator(), tickAll, tickNone }) top.Children.Add(e);
            // the details of the selected item
            var info = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 130, Padding = new Thickness(4),
                                     FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            // every level: user, project, team, company, platforms - a shared level in Git gets a branch for a pull request
            var levelNames = Levels(true).Where(x => x.Level != null).Select(x => x.Level + (x.Pr ? "  (pull request)" : "")).ToList();
            if (levelNames.Count == 0) levelNames.Add("user");
            var level = new ComboBox { ItemsSource = levelNames, SelectedIndex = Math.Max(0, levelNames.FindIndex(x => x.StartsWith("user"))), MinWidth = 200, Margin = new Thickness(6, 0, 6, 0) };
            var install = new Button { Content = "Install …", MinWidth = 110, IsDefault = true };
            var say = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
            List<Dictionary<string, object>> Chosen() => lines.Where(l => l.On).Select(l => l.D).ToList();
            void Ticked()
            {
                var c = Chosen();
                var ok = c.Where(i => !(S(i, "licenseClass") is "source-available" or "none")).ToList();
                install.IsEnabled = ok.Count > 0;
                install.Content = ok.Count > 1 ? $"Install {ok.Count} …" : "Install …";
                say.Text = c.Count == 0 ? "Tick the items to install (Space ticks the selected line)." : $"{c.Count} ticked" + (c.Count > ok.Count ? $", {c.Count - ok.Count} of them not installable" : "") + ".";
            }
            list.AddHandler(ToggleButton.ClickEvent, new RoutedEventHandler((s, e) => Ticked()));
            list.KeyDown += (s, e) => { if (e.Key == Key.Space && list.SelectedItem is Line l) { l.On = !l.On; Filter(); list.SelectedItem = l; Ticked(); e.Handled = true; } };
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem is Line l) { l.On = !l.On; Filter(); list.SelectedItem = l; Ticked(); } };
            tickAll.Click += (s, e) => { foreach (var l in list.Items.OfType<Line>()) l.On = true; Filter(); Ticked(); };
            tickNone.Click += (s, e) => { foreach (var l in lines) l.On = false; Filter(); Ticked(); };
            // the details: the line the cursor is on
            void Selected()
            {
                if (!(list.SelectedItem is Line sel)) { info.Text = "Select a line to see its details; tick the items to install."; return; }
                var i = sel.D;
                var files = Strings(i, "files"); var scripts = Strings(i, "scripts");
                info.Text = $"{S(i, "name")} ({KindName(S(i, "kind"))}) from {S(i, "source")} @{Short(S(i, "commit"))}\n{S(i, "description")}\n\n" +
                            $"License: {S(i, "license")} ({S(i, "licenseClass")})" + (S(i, "refusal") is string rf ? "\nNot installable: " + rf : "") + "\n" +
                            $"Files ({files.Count}): {string.Join(", ", files.Take(30))}{(files.Count > 30 ? " …" : "")}" +
                            (scripts.Count > 0 ? $"\nScripts: {string.Join(", ", scripts)} - TcAgent never runs them; read them before you use the item." : "");
            }
            list.SelectionChanged += (s, e) => Selected();
            Selected(); Ticked();
            install.Click += async (s, e) =>
            {
                var chosen = Chosen();
                var skipped = chosen.Where(i => S(i, "licenseClass") is "source-available" or "none").ToList();
                chosen = chosen.Except(skipped).ToList();
                if (chosen.Count == 0) return;
                var to = (string)level.SelectedItem;
                var pr = to.Contains("pull request");
                var text = string.Join("\n\n", chosen.Select(i =>
                {
                    var files = Strings(i, "files"); var scripts = Strings(i, "scripts");
                    return $"{KindName(S(i, "kind"))} \"{S(i, "name")}\" from {S(i, "source")} @{Short(S(i, "commit"))}\n" +
                           $"  License: {S(i, "license")}{(S(i, "licenseClass") == "accepted" ? "" : $" - {S(i, "licenseClass")}: check it before you accept it")}\n" +
                           $"  Files ({files.Count}): {string.Join(", ", files.Take(20))}{(files.Count > 20 ? " …" : "")}" +
                           (scripts.Count > 0 ? $"\n  Contains scripts: {string.Join(", ", scripts)} - TcAgent never runs them; read them before you use the item." : "");
                })) + (skipped.Count > 0 ? "\n\nNot installed (license): " + string.Join(", ", skipped.Select(i => S(i, "name"))) : "");
                var risky = chosen.Any(i => Strings(i, "scripts").Count > 0 || S(i, "licenseClass") != "accepted");
                if (!Confirm("Install", $"Install {(chosen.Count == 1 ? "this item" : chosen.Count + " items")} to the {to.Split(' ')[0]} level?" +
                                        (pr ? " (a commit on a new branch of its repository - you push it and open the pull request)" : ""), text, "Install", risky)) return;
                install.IsEnabled = false;
                var results = new List<string>(); bool all = true;
                foreach (var i in chosen)
                {
                    say.Text = $"Installing {S(i, "name")} …";
                    var (ok, output) = await Cli($"sources install --name {Quote(S(i, "source"))} --item {Quote(S(i, "name"))} --kind {S(i, "kind")} --level {Quote(to.Split(' ')[0])}" +
                                                 (S(i, "licenseClass") == "accepted" ? "" : " --accept-license"));
                    all &= ok;
                    results.Add((ok ? "✓ " : "✗ ") + S(i, "name") + ": " + output.Trim().Split('\n')[0]);
                    if (!ok) MessageBox.Show(w, output, "Not installed: " + S(i, "name"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                say.Text = string.Join(" · ", results.Select(x => x.Split(':')[0]));
                install.IsEnabled = true;
                Load();
                if (all) { w.DialogResult = true; Info(string.Join("\n", results)); }
            };
            var bar = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(new TextBlock { Text = "Install to:", VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(level); right.Children.Add(install);
            right.Children.Add(new Button { Content = "Close", MinWidth = 86, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) });
            DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right); bar.Children.Add(say);
            var dp = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
            DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
            DockPanel.SetDock(info, Dock.Bottom); dp.Children.Add(info);
            var listBox = WithEmpty(list, "Nothing matches - clear the search or choose All kinds.");
            var frame = new Border { BorderThickness = new Thickness(1), Child = listBox };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            dp.Children.Add(frame);
            w.Content = dp;
            w.Loaded += (s, e) => query.Focus();
            w.ShowDialog();
        }

        async void UpdateAll()
        {
            var (_, plan) = await Cli("sources pull --dry-run", "Checking the sources");
            Out(sourceDetail, plan);
            if (plan.StartsWith("Everything is up to date")) { Info(plan.Trim()); return; }
            if (!Confirm("Update all", "Update these items from their sources? (Items you changed locally are not touched - use \"Update and merge …\" for them.)", plan, "Update")) return;
            RunAsync("sources pull", true, reload: true, what: "Updating");
        }

        (string Name, string Level) Installed()
        {
            if (!(installedList.SelectedItem is Line l)) { Hint("Select an installed item first."); return (null, null); }
            return ((S(l.D, "key") ?? "").Split('/').Last(), S(l.D, "level"));
        }

        // a preview first (nothing changed), then the merge after asking; conflicts stay marked in the file
        async void MergeSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            var (pok, preview) = await Cli($"sources merge --dry-run --item {Quote(name)} --level {Quote(level)}", $"Fetching the source of {name}");
            Out(output, preview.Trim());
            if (preview.Contains("has no newer version")) { Info(preview.Trim()); return; }
            var conflicts = preview.Contains("Conflicts");
            if (!pok && !conflicts) { Report(false, preview, "Merge"); return; }
            if (!Confirm("Update and merge", $"Merge the new version of the source into your {name}? Your version is backed up first." +
                                             (conflicts ? " Conflicts open in the merge window of Visual Studio (or are marked with <<<<<<< in the file)." : ""),
                         preview, "Merge", conflicts)) return;
            var (ok, o) = await Cli($"sources merge --json --item {Quote(name)} --level {Quote(level)}", "Merging " + name);
            var lines = o.Split('\n').ToList();
            var cf = lines.FirstOrDefault(x => x.StartsWith("CONFLICT-FILES "));
            Out(output, string.Join("\n", lines.Where(x => !x.StartsWith("CONFLICT-FILES "))).Trim());
            if (!ok && !o.Contains("Conflicts")) Report(false, o, "Merge");
            else status.Text = output.Text.Split('\n')[0];
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
        async void CompareSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            var (ok, o) = await Cli($"sources changes --json --item {Quote(name)} --level {Quote(level)}", "Comparing " + name);
            if (!ok) { Report(false, o, "Compare"); return; }
            List<Dictionary<string, object>> files;
            try { files = Arr(new JavaScriptSerializer().DeserializeObject(o)).ToList(); } catch { Out(output, o); return; }
            if (files.Count == 0) { Info($"{name}: no local changes - it is the same as the installed version."); return; }
            Out(output, string.Join("\n", files.Select(f => $"{S(f, "kind")}: {S(f, "rel")}")));
            var diff = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
            if (diff == null) { Out(output, (await Cli($"sources changes --item {Quote(name)} --level {Quote(level)}")).output); return; }
            foreach (var f in files.Take(8))
            {
                var left = S(f, "installed") ?? EmptyFile(); var right = S(f, "yours") ?? EmptyFile();
                try
                {
                    diff.OpenComparisonWindow2(left, right, $"{name}: {S(f, "rel")}", $"{left} ↔ {right}", "Installed", "Yours", $"{name}: {S(f, "rel")}", null,
                                               (uint)Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
                }
                catch (Exception ex) { Error("Compare: " + ex.Message); }
            }
            status.Text = files.Count > 8 ? $"{files.Count} files changed - the first 8 are open." : $"{files.Count} changed file(s) open in the compare window.";
        }

        static string EmptyFile() { var p = Path.Combine(Path.GetTempPath(), "tcagent-empty.txt"); if (!File.Exists(p)) File.WriteAllText(p, ""); return p; }

        async void RevertSource(string name, string level)
        {
            if (name == null) return;
            if (MessageBox.Show($"Revert {name} to the installed version?\n\nYour changes are copied to %LOCALAPPDATA%\\TcAgent\\backups\\sources first (and the knowledge level is backed up).",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, o) = await Cli($"sources revert --item {Quote(name)} --level {Quote(level)}", "Reverting " + name);
            Report(ok, o, "Revert");
            Load();
        }

        async void DetachSource(string name, string level)
        {
            if (name == null) return;
            if (MessageBox.Show($"Keep {name} as your own version?\n\nIt is no longer updated from its source and no longer shown as changed; where it came from stays in its header (tcagent-source, tcagent-detached).",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, o) = await Cli($"sources detach --item {Quote(name)} --level {Quote(level)}", "Detaching " + name);
            Report(ok, o, "Keep as own version");
            Load();
        }

        // a commit on a new branch of a working copy of the source, then - only if the user wants - the push and the PR page
        async void ProposeSource(string name, string level, TextBox output)
        {
            if (name == null) return;
            var r = Ask($"Propose your changes of {name} to the source", new[] { 0 }, ("What does your change improve? (the commit message, in the language of the source)", null, name + ": "));
            if (r == null) return;
            var (ok, o) = await Cli($"sources propose --item {Quote(name)} --level {Quote(level)} --message {Quote(r[0].Trim())}", "Preparing the proposal");
            if (!ok) { Out(output, o.Trim()); Report(false, o, "Propose"); return; }
            Dictionary<string, object> p;
            try { p = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(o); } catch { Out(output, o); return; }
            var sp = Strings(p, "specifics");
            Out(output, $"Prepared: branch {S(p, "branch")} in {S(p, "folder")}\nFiles: {string.Join(", ", Strings(p, "files"))}\n" +
                        $"Patch (to send by hand): {S(p, "patch")}" + (sp.Count > 0 ? $"\n\nWARNING - private terms found: {string.Join(", ", sp)}" : ""));
            if (sp.Count > 0 && MessageBox.Show($"The proposal contains terms from your private list: {string.Join(", ", sp)}\n\nThe source is public or shared - remove them first " +
                                                "(edit the item, propose again). Push anyway?", "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var push = Prompt("Push the proposal",
                              ("Push the branch to (the source, or the URL of your fork if you may not write to the source) - nothing is sent before OK, empty = do not push", null, S(p, "url")));
            if (push == null || push[0].Trim().Length == 0) { Info("Not pushed - the branch and the patch are prepared (see the output)."); return; }
            var (pushOk, po) = await Cli($"sources push --name {Quote(S(p, "source"))} --ref {Quote(S(p, "branch"))} --url {Quote(push[0].Trim())}", "Pushing");
            if (!pushOk) { Report(false, po, "Push"); return; }
            try
            {
                var pr = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(po);
                if (S(pr, "compareUrl") is string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Info("Pushed - the pull request page is open in the browser."); }
                else Info("Pushed: " + S(p, "branch") + " - open the pull request / merge request on the server of the source.");
            }
            catch { Info("Pushed."); }
        }

        async void UninstallSource(string name, string level, string state)
        {
            if (name == null) return;
            var changed = (state ?? "").Contains("changed locally");
            if (MessageBox.Show($"Uninstall {name} from the {level} level?\n\nIts files are removed and the entry leaves skills.lock.json; a copy goes to " +
                                "%LOCALAPPDATA%\\TcAgent\\backups\\sources. The source itself stays." + (changed ? "\n\nYou changed this item - your changes are only in that copy afterwards." : ""),
                                "TcAgent Knowledge", MessageBoxButton.YesNo, changed ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = await Cli($"sources uninstall --item {Quote(name)} --level {Quote(level)}", "Uninstalling " + name);
            Report(ok, output, "Uninstall");
            Load();
        }

        async void RunAsyncTo(string args, TextBox output)
        {
            var (ok, o) = await Cli(args, "TcAgent.exe " + string.Join(" ", args.Split(' ').Take(2)));
            Out(output, o.Trim());
            if (!ok) Report(false, o, args.Split(' ')[0]);
            else status.Text = "Done - see the output.";
        }

        void DiffInstalled()
        {
            if (!(installedList.SelectedItem is Line l)) { Hint("Select an installed item first."); return; }
            RunAsyncTo("sources diff --item " + Quote((S(l.D, "key") ?? "").Split('/').Last()), sourceDetail);
        }

        // ------------------------------------------------------------------ backups of the knowledge levels
        async void LoadBackups()
        {
            var (ok, output) = await Cli("knowledgebase list --json");
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
                Summary();
            }
            catch (Exception ex) { Error("Backups: " + ex.Message); }
        }

        // the list of the backups for a dialog when the Backups tab was not open yet
        async System.Threading.Tasks.Task LoadBackupsNow()
        {
            var (ok, output) = await Cli("knowledgebase list --json", "Reading the backups");
            if (ok) try { backupLevels = Arr(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(output)).ToList(); } catch { }
        }

        // Backup: the levels (all checked), the folder (TcAgent's backup folder, changeable), "open the folder afterwards"
        async void BackupNow()
        {
            if (backupLevels.Count == 0) await LoadBackupsNow();
            var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backups", "knowledge");
            var w = Dialog("Backup of the knowledge", 540);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            sp.Children.Add(new TextBlock { Text = "Levels:", Margin = new Thickness(0, 0, 0, 4) });
            var boxes = backupLevels.Select(l => new CheckBox { Content = $"{S(l, "level")}  ({S(l, "root")})", Tag = S(l, "level"), IsChecked = true, Margin = new Thickness(0, 1, 0, 1) }).ToList();
            foreach (var b in boxes) sp.Children.Add(b);
            if (boxes.Count == 0) sp.Children.Add(new TextBlock { Text = "No level found - refresh the window (⟳) and try again.", Opacity = 0.7 });
            sp.Children.Add(new TextBlock { Text = "Folder (the backups of TcAgent are kept here; another folder gets a copy as well):", Margin = new Thickness(0, 10, 0, 3) });
            var folderRow = new DockPanel();
            var browse = new Button { Content = "Browse …", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
            var folder = new TextBox { Text = defaultFolder, MinWidth = 400, Padding = new Thickness(2) };
            DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse); folderRow.Children.Add(folder);
            browse.Click += (s, e) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder for the backup", SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dlg.SelectedPath;
            };
            sp.Children.Add(folderRow);
            sp.Children.Add(new TextBlock { Text = "Reason (optional):", Margin = new Thickness(0, 10, 0, 3) });
            var reason = new TextBox { Text = "by hand", Padding = new Thickness(2) };
            sp.Children.Add(reason);
            var openAfter = new CheckBox { Content = "Open the folder afterwards", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(openAfter);
            var ok = new Button { Content = "Back up" };
            void Check() => ok.IsEnabled = boxes.Any(b => b.IsChecked == true) && folder.Text.Trim().Length > 0;
            foreach (var b in boxes) b.Click += (s, e) => Check();
            folder.TextChanged += (s, e) => Check();
            ok.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(Buttons(w, ok));
            w.Content = sp;
            Check();
            if (w.ShowDialog() != true) return;
            var chosen = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            var target = folder.Text.Trim();
            var copy = target.Length > 0 && !Path.GetFullPath(target).TrimEnd('\\').Equals(defaultFolder, StringComparison.OrdinalIgnoreCase) ? target : null;
            var (rok, output) = await Cli($"knowledgebase backup --level {Quote(string.Join(",", chosen))} --message {Quote(reason.Text.Trim().Length > 0 ? reason.Text.Trim() : "by hand")}" +
                                          (copy != null ? " --out " + Quote(copy) : ""), "Backing up");
            backupDetail.Text = output.Trim();
            LoadBackups();
            if (!rok) { Report(false, output, "Backup"); return; }
            var where = copy ?? defaultFolder;
            Info("Backup done: " + output.Trim().Split('\n')[0], "Open folder", () => ShowInExplorer(where));
            if (openAfter.IsChecked == true) ShowInExplorer(where);
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

        async void DiffBackup(string item)
        {
            if (!(backupList.SelectedItem is Line l)) { Hint("Select a backup first."); return; }
            var args = $"knowledgebase diff --level {Quote(S(l.D, "level"))} --from {Quote(S(l.D, "zip"))}" + (item != null ? " --item " + Quote(item) : "");
            var (ok, output) = await Cli(args, "Comparing with the current state");
            backupDetail.Text = output.Trim();
            if (!ok) Report(false, output, "Compare");
            else status.Text = output.Trim().Split('\n').FirstOrDefault(x => x.Contains("would change")) ?? "Compared - see below.";
        }

        // Restore: choose a backup (all levels, newest first; filter), then what - the whole level, one skill/workflow folder or one file
        async void RestoreBackup()
        {
            await LoadBackupsNow();
            var all = backupLevels.SelectMany(l => Arr(l["backups"]).Select(b => { b["level"] = S(l, "level"); return b; }))
                                  .OrderByDescending(b => S(b, "created")).ToList();
            if (all.Count == 0) { Hint("There are no backups yet - they are made automatically before every change, or with \"" + BackupLabel + "\"."); return; }
            var preselect = (backupList.SelectedItem as Line)?.D;
            var w = Dialog("Restore the knowledge", 640, false);
            w.Width = 800; w.Height = 460;
            var list = NewList();
            Columns(list, ("Level", 140, nameof(Line.C1)), ("Created", 150, nameof(Line.C2)), ("Reason", 290, nameof(Line.C3)), ("Files", 60, nameof(Line.C4)), ("Kind", 130, nameof(Line.C5)));
            var lines = all.Select(b => new Line { D = b, C1 = S(b, "level"), C2 = (S(b, "created") ?? "").Replace('T', ' '), C3 = S(b, "reason"), C4 = Convert.ToString(b["files"]),
                                                   C5 = B(b, "git") ? "uncommitted (Git)" : "whole level", Mark = B(b, "git") ? Blue : Green, Tip = S(b, "zip") }).ToList();
            var query = new TextBox();
            query.TextChanged += (s, e) =>
            {
                var q = query.Text.Trim().ToLowerInvariant();
                list.ItemsSource = lines.Where(x => q.Length == 0 || $"{x.C1} {x.C2} {x.C3}".ToLowerInvariant().Contains(q)).ToList();
                if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0;
            };
            list.ItemsSource = lines;
            list.SelectedItem = lines.FirstOrDefault(x => preselect != null && S(x.D, "zip") == S(preselect, "zip")) ?? lines.FirstOrDefault();
            var ok = new Button { Content = "Next …" };
            var file = new Button { Content = "From a file …" };
            Dictionary<string, object> chosen = null; bool fromFile = false;
            ok.Click += (s, e) => { chosen = (list.SelectedItem as Line)?.D; if (chosen != null) w.DialogResult = true; };
            list.MouseDoubleClick += (s, e) => { chosen = (list.SelectedItem as Line)?.D; if (chosen != null) w.DialogResult = true; };
            list.SelectionChanged += (s, e) => ok.IsEnabled = list.SelectedItem != null;
            file.Click += (s, e) => { fromFile = true; w.DialogResult = true; };
            var dp = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "Choose a backup - next you choose the whole level, one skill/workflow or one file, and see the difference before anything changes.",
                                              TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            head.Children.Add(SearchBox(query, "Filter by level, date or reason", 300));
            DockPanel.SetDock(head, Dock.Top); dp.Children.Add(head);
            var bar = Buttons(w, ok, file);
            DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
            var frame = new Border { BorderThickness = new Thickness(1), Child = list, Margin = new Thickness(0, 6, 0, 0) };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            dp.Children.Add(frame);
            w.Content = dp;
            if (w.ShowDialog() != true) return;
            if (fromFile) { RestoreFromFile(); return; }
            RestoreFrom(S(chosen, "zip"), S(chosen, "level"), Strings(chosen, "paths"), (S(chosen, "created") ?? "").Replace('T', ' '));
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
        async void RestoreFrom(string zip, string level, List<string> paths, string label)
        {
            const string Whole = "(the whole level)";
            var folders = paths.Where(p => p.StartsWith("skills/") || p.StartsWith("workflows/") || p.StartsWith("platforms/"))
                               .Select(p => string.Join("/", p.Split('/').Take(2))).Distinct();
            var choices = new[] { Whole }.Concat(folders).Concat(paths).ToArray();
            var pick = Pick("Restore - what", $"From the backup {(level != null ? "of " + level + " " : "")}{label}: the whole level, a skill/workflow folder or one file.", choices, Whole);
            if (pick == null) return;
            var item = pick == Whole ? null : pick;
            var common = (level != null ? $"--level {Quote(level)} " : "") + $"--from {Quote(zip)}" + (item != null ? " --item " + Quote(item) : "");
            var (dok, diff) = await Cli("knowledgebase diff " + common, "Comparing");
            backupDetail.Text = diff.Trim();
            if (!dok) { Report(false, diff, "Restore"); return; }
            if (diff.Contains(": 0 file(s) would change")) { Info("Nothing to restore - the current state is the same as the backup."); return; }
            if (!Confirm("Restore", $"Restore {item ?? "the whole level"}? The current state is backed up first.", diff, "Restore", true)) return;
            var (ok, output) = await Cli("knowledgebase restore " + common, "Restoring");
            backupDetail.Text = output.Trim();
            Report(ok, output, "Restore");
            if (ok) Info("Restored: " + output.Trim().Split('\n')[0]);
            Load(); LoadBackups();
        }

        void OpenBackupFolder()
        {
            var f = backupList.SelectedItem is Line l ? S(l.D, "folder") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backups", "knowledge");
            if (f == null) return;
            try { Directory.CreateDirectory(f); ShowInExplorer(f); } catch (Exception ex) { Error(ex.Message); }
        }

        // ------------------------------------------------------------------ platforms
        FrameworkElement PlatformsPage()
        {
            if (platformsPage != null) return platformsPage;
            Columns(platformList, ("Platform", 180, nameof(Line.C1)), ("Applies here", 110, "!" + nameof(Line.C2)), ("Detected by libraries", 220, nameof(Line.C3)),
                    ("Detected by terms", 180, nameof(Line.C4)), ("Defined in", 360, nameof(Line.C5)));
            bool PSel() => platformList.SelectedItem is Line;
            var acts = new[] {
                A("New …", "A platform of your own: name, description, the libraries and terms that detect it", NewPlatform),
                Sep,
                A("Open", "Its platform.md: description and detection of the selected platform (or double-click)", OpenPlatform, PSel, "Enter"),
                A("Open folder", "The folder of the platform (its coding rules, skills, lessons ...)", OpenPlatformFolder, PSel),
                Sep,
                A("Delete …", "Delete the selected platform of your own with its folder", DeletePlatform, PSel, "Del"),
                Sep,
                A("Copy path", "Copy the path of its platform.md", () => CopyPath(S((platformList.SelectedItem as Line)?.D, "file")), PSel) };
            var tools = Bar(acts);
            // the decision for this project first: what applies and why, with the two ways to change it
            var decide = new WrapPanel { Margin = new Thickness(6, 0, 6, 6) };
            var set = Text("Set for this project …", "Choose the platforms of this solution (stored in .tcagent\\config.json)");
            set.Click += (s, e) => SetPlatforms();
            var auto = Text("Detect automatically", "Remove the choice from .tcagent\\config.json - detection by libraries and terms again");
            auto.Click += async (s, e) => { var (ok, o) = await Cli("knowledge platform --action auto", "Detecting the platform"); Report(ok, o, "Detect automatically"); Load(); };
            gates.Add((set, () => !string.IsNullOrEmpty(S(data, "solutionDir"))));
            gates.Add((auto, () => B(data, "platformConfigured")));
            decide.Children.Add(set); decide.Children.Add(auto);
            platformList.MouseDoubleClick += (s, e) => OpenPlatform();
            platformList.ContextMenu = MenuOf(acts);
            platformList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeletePlatform(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenPlatform(); e.Handled = true; } };
            var h = Heading("");
            Heading(h, "All platforms", "a platform brings its own rules, skills and lessons - it applies when its libraries or terms are found in the project");
            var list = WithEmpty(platformList, "No platform defined - \"New …\" creates one (a name, the libraries and terms that detect it).");
            return platformsPage = Rows((platformInfo, AutoH, 0), (decide, AutoH, 0), (h, AutoH, 0), (tools, AutoH, 0), (list, Star(1), 80));
        }

        void FillPlatforms()
        {
            var pf = data.TryGetValue("platform", out var pv) ? pv as Dictionary<string, object> : null;
            var libs = Strings(data, "libraries");
            var active = Arr(data["platforms"]).Where(p => B(p, "active")).Select(p => S(p, "name")).ToList();
            platformInfo.Inlines.Clear();
            platformInfo.Inlines.Add(new System.Windows.Documents.Run("This project: " + (active.Count > 0 ? string.Join(", ", active) : "no platform")) { FontWeight = FontWeights.SemiBold, FontSize = 13 });
            platformInfo.Inlines.Add(new System.Windows.Documents.Run((B(data, "platformConfigured") ? "   chosen by hand" : "   detected automatically") + (S(pf, "reason") is string r ? " - " + r : ""))
                                         { Foreground = Gray });
            platformInfo.Inlines.Add(new System.Windows.Documents.LineBreak());
            platformInfo.Inlines.Add(new System.Windows.Documents.Run("Libraries of the project: " + (libs.Count > 0 ? string.Join(", ", libs) : "none found")) { Foreground = Gray });
            string J(Dictionary<string, object> p, string k) => string.Join(", ", Strings(p, k));
            platformList.ItemsSource = Arr(data["platforms"]).Select(p => new Line
            {
                D = p, C1 = S(p, "name"), C2 = B(p, "active") ? "✓ applies" : "–", C3 = J(p, "libraries"), C4 = J(p, "terms"), C5 = MiddlePath(UserPath(S(p, "file") ?? J(p, "roots")), 60),
                Mark = B(p, "active") ? Green : Gray, Tip = S(p, "description") + "\n" + (S(p, "file") ?? J(p, "roots"))
            }).ToList();
        }

        async void NewPlatform()
        {
            var st = data?.TryGetValue("settings", out var so) == true ? so as Dictionary<string, object> : null;
            var where = string.IsNullOrEmpty(S(st, "TCAGENT_COMPANY")) ? new[] { "user (only for you)" } : new[] { "user (only for you)", "company (for everybody - Git bar: pull request)" };
            var r = Ask("New platform", new[] { 0 }, ("Name (e.g. framework-a - other characters become hyphens)", null, ""), ("Description", null, ""),
                        ("Detected by libraries (comma-separated, * allowed)", null, ""), ("Detected by terms in the project (comma-separated)", null, ""),
                        ("Defined at", where, where[0]));
            if (r == null) return;
            var (ok, output) = await Cli($"knowledge platform --action new --name {Quote(r[0].Trim())} --message {Quote(r[1])} --libraries {Quote(r[2])} --terms {Quote(r[3])} --level {(r[4].StartsWith("company") ? "company" : "user")}",
                                         "Creating the platform");
            Report(ok, output, "New platform");
            if (ok && File.Exists(output.Trim())) try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(output.Trim()); } catch { }
            Load();
        }

        void OpenPlatform()
        {
            var f = S((platformList.SelectedItem as Line)?.D, "file");
            if (f == null || !File.Exists(f)) { Hint("This platform has no platform.md of its own (only a folder) - \"New …\" creates one."); return; }
            OpenInEditor(f);
        }

        // only a platform folder of the user (or of the company folder without Git) - the delete of KnowledgeOps checks it
        async void DeletePlatform()
        {
            var d = (platformList.SelectedItem as Line)?.D;
            var folder = S(d, "file") is string f ? Path.GetDirectoryName(f) : null;
            if (folder == null) { Hint("This platform has no folder of its own to delete."); return; }
            if (MessageBox.Show($"Delete the platform {S(d, "name")} with everything in its folder?\n\n{folder}\n\n(rules, skills and lessons of the platform go too; the level is backed up first)",
                                "TcAgent Knowledge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = await Cli("knowledge delete --path " + Quote(folder), "Deleting the platform");
            Report(ok, output, "Delete platform");
            Load();
        }

        void OpenPlatformFolder()
        {
            var d = (platformList.SelectedItem as Line)?.D;
            var folder = S(d, "file") is string f ? Path.GetDirectoryName(f) : Strings(d, "roots").FirstOrDefault();
            ShowInExplorer(folder);
        }

        async void SetPlatforms()
        {
            if (string.IsNullOrEmpty(S(data, "solutionDir"))) { Hint("No solution open - the platforms are chosen per project."); return; }
            var all = Arr(data["platforms"]).ToList();
            var w = Dialog("Platforms of this project", 340);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            sp.Children.Add(new TextBlock { Text = "The project is built on:", Margin = new Thickness(0, 0, 0, 6) });
            var boxes = all.Select(p => new CheckBox { Content = S(p, "name") + (S(p, "description") is string de ? " – " + de : ""), Tag = S(p, "name"), IsChecked = B(p, "active"), Margin = new Thickness(0, 2, 0, 2) }).ToList();
            foreach (var b in boxes) sp.Children.Add(b);
            if (boxes.Count == 0) sp.Children.Add(new TextBlock { Text = "No platforms defined - create one with New … first.", Opacity = 0.7 });
            var ok = new Button { Content = "OK" };
            ok.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(Buttons(w, ok));
            w.Content = sp;
            if (w.ShowDialog() != true) return;
            var names = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            var (rok, output) = await Cli("knowledge platform --action set --name " + Quote(names.Count > 0 ? string.Join(",", names) : "none"), "Setting the platforms");
            Report(rok, output, "Platforms");
            Load();
        }

        // ------------------------------------------------------------------ documentation: what tc_docs looks things up in
        FrameworkElement DocsPage()
        {
            if (docsPage != null) { LoadDocs(); return docsPage; }
            Columns(docsList, ("Source", 300, nameof(Line.C1)), ("Type", 110, nameof(Line.C2)), ("Level", 80, nameof(Line.C3)),
                    ("State", 200, "!" + nameof(Line.C4)), ("Comment", 170, nameof(Line.C5)), ("Defined in", 300, nameof(Line.C6)));
            Columns(docsHits, ("Hit", 300, nameof(Line.C1)), ("Where", 420, nameof(Line.C2)), ("Text", 600, nameof(Line.C3)));
            bool DSel() => docsList.SelectedItem is Line;
            Dictionary<string, object> Doc() => (docsList.SelectedItem as Line)?.D;
            bool Own() => (S(Doc(), "origin") ?? "").EndsWith("knowledge.md", StringComparison.OrdinalIgnoreCase);
            var acts = new[] {
                A("Add link or folder …", "A web address (only pages below it are read), a folder or a network share - one line in the knowledge.md of a level", () => EditDoc(null)),
                A("Add documents …", "Copy PDF, Word, HTML, Markdown or text files into the knowledge\\ folder of a level - the agent searches them", CopyDocs),
                Sep,
                A("Open", "The link in the browser, the folder in the Explorer (or double-click)", OpenDoc, DSel, "Enter"),
                A("Open folder", "The folder in the Explorer (document folders, folders, shares)", () => ShowInExplorer(S(Doc(), "value")), () => DSel() && !(S(Doc(), "type") is "web" or "link")),
                A("Open knowledge.md", "The list of links and folders of the level of the selected source (created if it does not exist yet)", OpenDocOrigin),
                Sep,
                A("Edit …", "Change the selected link or folder, its comment or its level", () => EditDoc(docsList.SelectedItem as Line), () => DSel() && Own()),
                Sep,
                A("Delete …", "Delete the selected link or folder from its knowledge.md (the documents themselves stay)", RemoveDoc, () => DSel() && Own(), "Del"),
                Sep,
                A("Copy path", "Copy the link or the path", () => CopyPath(S(Doc(), "value")), DSel) };
            var tools = Bar(acts);
            docsInfo.Text = "What the agent looks things up in (tc_docs): Beckhoff InfoSys, the links and folders of the knowledge.md of every level and the documents " +
                            "in their knowledge\\ folders. Read only.";
            docsList.MouseDoubleClick += (s, e) => OpenDoc();
            docsList.ContextMenu = MenuOf(acts);
            docsList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveDoc(); e.Handled = true; } else if (e.Key == Key.Enter) { OpenDoc(); e.Handled = true; } };
            // search like the agent: right here, the hits below
            var search = new WrapPanel { Margin = new Thickness(6, 8, 6, 0) };
            var go = Text("Search", "The same search as tc_docs (Enter)");
            go.Click += (s, e) => SearchDocs();
            docsQuery.KeyDown += (s, e) => { if (e.Key == Key.Enter) { SearchDocs(); e.Handled = true; } };
            search.Children.Add(Label("Search like the agent:"));
            search.Children.Add(SearchBox(docsQuery, "Words from the documents, e.g. EtherCAT diagnosis (Ctrl+F)", 380));
            search.Children.Add(go);
            bool HSel() => docsHits.SelectedItem is Line;
            docsHits.MouseDoubleClick += (s, e) => OpenHit();
            docsHits.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { OpenHit(); e.Handled = true; } };
            docsHits.ContextMenu = MenuOf(A("Open", "The document on the page of the hit, or the page in the browser", OpenHit, HSel, "Enter"),
                                          A("Open folder", "The folder of the document", OpenHitFolder, () => HSel() && S((docsHits.SelectedItem as Line)?.D, "kind") == "file"),
                                          Sep,
                                          A("Copy path", "Copy the path or the address", () => CopyPath(S((docsHits.SelectedItem as Line)?.D, "where")), HSel));
            var list = WithEmpty(docsList, "No reference yet - \"Add link or folder …\" or \"Add documents …\".");
            var hits = WithEmpty(docsHits, "Type words above and press Enter - the hits are listed here and open with a double-click.");
            return docsPage = Rows((tools, AutoH, 0), (docsInfo, AutoH, 0), (list, Star(3), 80), (search, AutoH, 0), (docsHitsHeading, AutoH, 0), (hits, Star(2), 70));
        }

        // folders are counted with a time limit (network shares) - in the background
        async void LoadDocs()
        {
            docsLoaded = true;
            var (ok, output) = await Cli("knowledge docs");
            if (!ok) { status.Text = "Reference sources: " + output.Trim(); return; }
            try
            {
                var r = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                docsLevels = Strings(r, "levels").ToArray();
                var types = new Dictionary<string, string> { ["web"] = "Beckhoff InfoSys", ["link"] = "Link", ["folder"] = "Folder", ["share"] = "Network share", ["documents"] = "Document folder" };
                var list = Arr(r["sources"]).ToList();
                docsList.ItemsSource = list.Select(d =>
                {
                    var (state, mark) = DocState(d);
                    return new Line
                    {
                        D = d, C1 = UserPath(S(d, "value")), C2 = types.TryGetValue(S(d, "type") ?? "", out var t) ? t : S(d, "type"), C3 = S(d, "level"), C4 = state,
                        C5 = S(d, "note"), C6 = MiddlePath(UserPath(S(d, "origin")), 60), Mark = mark, Tip = S(d, "raw") != S(d, "value") ? S(d, "raw") + " → " + S(d, "value") : S(d, "value")
                    };
                }).ToList();
                docsSources = list.Count;
                docsDocuments = list.Where(d => S(d, "type") is "folder" or "share" or "documents").Sum(d => Convert.ToInt32(d["documents"]));
                docsUnreachable = list.Count(d => d.TryGetValue("reachable", out var re) && re is bool b && !b && S(d, "type") != "documents");
                if (data != null) { Badges(); FillOverview(); }
                Summary();
            }
            catch (Exception ex) { Error("Reference sources: " + ex.Message); }
        }

        // a link is read online; a folder is reachable (with its documents), empty or not reachable
        static (string Text, Brush Mark) DocState(Dictionary<string, object> d)
        {
            var type = S(d, "type");
            var note = S(d, "state");
            if (type is "web" or "link") return ("↗ online" + (string.IsNullOrEmpty(note) ? "" : " · " + note), Blue);
            if (d.TryGetValue("reachable", out var re) && re is bool b)
            {
                if (!b) return type == "documents" ? ("○ empty - use Add documents …", Gray) : ("⚠ not reachable", Red);
                var n = d.TryGetValue("documents", out var dn) ? Convert.ToInt32(dn) : 0;
                return n == 0 ? (type == "documents" ? "○ empty - use Add documents …" : "○ no documents", Gray) : ($"✓ {n} document{(n == 1 ? "" : "s")}", Green);
            }
            return ("? " + note, Orange);
        }

        async void EditDoc(Line l)
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
                var (rok, rout) = await Cli($"knowledge docs --action remove --path {Quote(S(d, "origin"))} --url {Quote(S(d, "raw"))}", "Saving");
                if (!rok) { Report(false, rout, "Edit reference source"); return; }
            }
            var (ok, output) = await Cli($"knowledge docs --action add --level {Quote(r[2])} --url {Quote(r[0].Trim())}" + (r[1].Trim().Length > 0 ? " --message " + Quote(r[1].Trim()) : ""), "Saving");
            Report(ok, output, d == null ? "Add reference source" : "Edit reference source");
            LoadDocs();
        }

        // a link or a folder: for a folder a folder dialog (also for network shares), for a link the address
        string[] DocSourceDialog(string title, string value, string note, string level, string[] levels, string type)
        {
            var w = Dialog(title, 560);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
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
            var problem = new TextBlock { Foreground = Orange, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(problem);
            var ok = new Button { Content = "OK" };
            // checked while typing: OK only with a link or a folder path
            void Check()
            {
                var v = box.Text.Trim();
                problem.Text = isLink.IsChecked == true && (!(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) || v == "https://")
                                   ? "A link starts with https:// (or http://)."
                             : isFolder.IsChecked == true && !(v.StartsWith(@"\\") || System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z]:\\") || v.StartsWith("%"))
                                   ? "Choose a folder (Browse …) or type a path like C:\\Manuals or \\\\server\\share\\manuals." : "";
                problem.Visibility = problem.Text.Length > 0 && v.Length > 0 && v != "https://" ? Visibility.Visible : Visibility.Collapsed;
                ok.IsEnabled = problem.Text.Length == 0;
            }
            box.TextChanged += (s, e) => Check();
            void Kind()
            {
                var folder = isFolder.IsChecked == true;
                label.Text = folder ? "Folder (C:\\…) or network share (\\\\server\\share\\…):" : "Address (https://…):";
                browse.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
                if (!folder && box.Text.Length == 0) box.Text = "https://";
                if (folder && box.Text == "https://") box.Text = "";
                Check();
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
            ok.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(Buttons(w, ok));
            w.Content = sp;
            w.Loaded += (s, e) => { box.Focus(); box.CaretIndex = box.Text.Length; };
            return w.ShowDialog() == true ? new[] { box.Text.Trim(), comment.Text.Trim(), lv.SelectedItem as string } : null;
        }

        async void RemoveDoc()
        {
            var d = (docsList.SelectedItem as Line)?.D;
            if (d == null) return;
            if (!(S(d, "origin") ?? "").EndsWith("knowledge.md", StringComparison.OrdinalIgnoreCase))
            { Hint(S(d, "type") == "documents" ? "A document folder cannot be removed - open it and delete the documents you do not want." : S(d, "type") == "web" ? "Beckhoff InfoSys is built in." : "This source comes from TCAGENT_KNOWLEDGE (settings.json) - change it there."); return; }
            if (MessageBox.Show($"Delete this source from {S(d, "origin")}?\n\n{S(d, "raw")}\n\n(the documents themselves stay where they are)", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = await Cli($"knowledge docs --action remove --path {Quote(S(d, "origin"))} --url {Quote(S(d, "raw"))}", "Deleting");
            Report(ok, output, "Delete reference source");
            LoadDocs();
        }

        async void CopyDocs()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Documents for the agent",
                                                           Filter = "Documents|*.pdf;*.docx;*.html;*.htm;*.md;*.txt;*.xml;*.csv;*.json;*.st|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            var levels = docsLevels.Length > 0 ? docsLevels : new[] { "user" };
            var r = Prompt($"Add {dlg.FileNames.Length} document(s)", ("Into the knowledge\\ folder of the level", levels, levels.Contains("project") ? "project" : levels[0]));
            if (r == null) return;
            var (ok, output) = await Cli($"knowledge docs --action copy --level {Quote(r[0])} --url {Quote(string.Join("|", dlg.FileNames))}", "Copying the documents");
            Report(ok, output, "Add documents");
            if (ok) Info(output.Trim().Split('\n')[0] + " - the agent searches them from now on.");
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
                    if (Directory.Exists(v)) ShowInExplorer(v);
                    else Hint("Not reachable: " + v + " - is the drive or the network share connected?");
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

        async void SearchDocs()
        {
            var q = docsQuery.Text.Trim();
            if (q.Length == 0) { docsQuery.Focus(); Hint("Type words to search for - e.g. EtherCAT diagnosis."); return; }
            Heading(docsHitsHeading, "Hits", $"searching for \"{q}\" …");
            docsHits.ItemsSource = null;
            var (ok, output) = await Cli("knowledge docs --action search --json --query " + Quote(q), "Searching");
            if (!ok) { Heading(docsHitsHeading, "Hits", "the search failed"); Report(false, output, "Search"); return; }
            try
            {
                var res = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                var hits = Arr(res["hits"]).ToList();
                docsHits.ItemsSource = hits.Select(h => new Line
                {
                    D = h, C1 = (S(h, "title") ?? S(h, "where")) + (h.TryGetValue("page", out var pg) && Convert.ToInt32(pg) > 0 ? $" (page {pg})" : ""), C2 = S(h, "where"), C3 = S(h, "snippet"),
                    Mark = S(h, "kind") == "file" ? Green : Blue, Tip = S(h, "snippet")
                }).ToList();
                Heading(docsHitsHeading, $"Hits ({hits.Count})", hits.Count == 0 ? $"nothing found for \"{q}\" in the documents and the pages read so far"
                                                                                 : $"for \"{q}\" - double-click opens the document on its page, or the page in the browser");
                status.Text = $"{hits.Count} hit(s).";
            }
            catch (Exception ex) { Error("Search: " + ex.Message); }
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
            Columns(feedbackList, ("Report", 110, nameof(Line.C1)), ("Status", 110, "!" + nameof(Line.C2)), ("Severity / type", 140, nameof(Line.C3)),
                    ("Tool", 140, nameof(Line.C4)), ("Summary", 420, nameof(Line.C5)), ("Version · date", 150, nameof(Line.C6)));
            feedbackList.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            feedbackList.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var item = new Style(typeof(ListViewItem));
            item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            item.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Line.Tip))));
            feedbackList.ItemContainerStyle = item;
            feedbackStatus.SelectionChanged += (s, e) => FillFeedback();
            feedbackList.SelectionChanged += (s, e) => PrepareIssue();
            bool FSel() => feedbackList.SelectedItems.Count > 0;
            var acts = new[] {
                A("Share with the TcAgent developers …", "Opens a new issue in the browser with the text below - you check it there and send it yourself", ShareIssue, FSel),
                A("Copy text", "Copy the text below", () => { try { Clipboard.SetText(feedbackText.Text); status.Text = "Copied."; } catch (Exception ex) { Error(ex.Message); } }, FSel),
                A("Save as …", "Save the text below as a Markdown file", SaveIssue, FSel),
                Sep,
                A("Set status …", "resolved / wontfix / duplicate / open for the selected reports", SetFeedbackStatus, FSel),
                A("Anonymize again", "Build the text of the selected reports again (names, paths and addresses replaced)", PrepareIssue, FSel),
                Sep,
                A("Delete …", "Delete the selected reports for good (from the feedback file)", DeleteFeedback, FSel, "Del"),
                Sep,
                A("Copy diagnostics", "Versions of TcAgent, the extension, TwinCAT and the IDEs for the Diagnostics field of an issue", () => CopyDiagnostics(t => Info(t))) };
            feedbackList.ContextMenu = MenuOf(acts);
            feedbackList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete) { DeleteFeedback(); e.Handled = true; } };
            feedbackText.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            feedbackText.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var show = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
            show.Children.Add(Label("Show:"));
            show.Children.Add(feedbackStatus);
            var tools = Bar(acts);
            var textHead = Heading("");
            Heading(textHead, "Issue text", "anonymized and editable - check it before you share it");
            feedbackInfo.Text = "Select one or more reports above.";
            var list = WithEmpty(feedbackList, "No reports with this status - the agent writes one with tc_feedback when a tool fails or is missing.");
            return feedbackPage = Rows((show, AutoH, 0), (tools, AutoH, 0), (list, Star(2), 70), (Splitter(), AutoH, 0), (textHead, AutoH, 0), (feedbackInfo, AutoH, 0), (feedbackText, Star(3), 80));
        }

        void FillFeedback()
        {
            if (data == null) return;
            var st = feedbackStatus.SelectedItem as string ?? "open";
            feedbackList.ItemsSource = Arr(data.TryGetValue("feedback", out var fo) ? fo : null).Where(f => st == "all" || S(f, "status") == st).Select(f => new Line
            {
                D = f, C1 = S(f, "id"), C2 = S(f, "status") == "open" ? "● open" : "✓ " + S(f, "status"), C3 = $"{S(f, "severity") ?? "–"} / {S(f, "type") ?? "–"}", C4 = S(f, "tool"), C5 = S(f, "summary"),
                C6 = $"{S(f, "version")} · {(S(f, "time") ?? "").Split('T')[0]}",
                Mark = S(f, "status") == "open" ? (S(f, "severity") is "blocker" or "major" ? Red : Orange) : Green, Tip = S(f, "summary")
            }).ToList();
            Summary();
        }

        async void PrepareIssue()
        {
            var ids = feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "id")).ToList();
            if (ids.Count == 0) { feedbackText.Text = ""; feedbackInfo.Text = "Select one or more reports above."; return; }
            var (ok, output) = await Cli("knowledge feedback --action issue --key " + Quote(string.Join(",", ids)), "Anonymizing");
            // the selection changed meanwhile: a newer call fills the text
            if (!ids.SequenceEqual(feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "id")))) return;
            if (!ok) { feedbackText.Text = output; Report(false, output, "Feedback"); return; }
            try
            {
                var r = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
                feedbackText.Text = WithEnvironment(S(r, "text"));
                feedbackUrl = S(r, "url");
                var repl = Strings(r, "replaced");
                feedbackInfo.Text = (repl.Count > 0 ? $"Replaced automatically ({repl.Count}): {string.Join(", ", repl.Take(12))}{(repl.Count > 12 ? " …" : "")}. " : "Nothing found to replace automatically. ") +
                                    "Abbreviations of customers or projects are not known to TcAgent - add them to privateTerms in settings.json.";
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
            if (text.Length == 0) { Hint("Select a report first."); return; }
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
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); Info("The issue is open in the browser - check the text there and send it yourself."); }
            catch (Exception ex) { Error(ex.Message); }
        }

        // delete is delete: the reports and their status records leave the feedback file - after a question (default No)
        async void DeleteFeedback()
        {
            var sel = feedbackList.SelectedItems.OfType<Line>().ToList();
            if (sel.Count == 0) { Hint("Select one or more reports first."); return; }
            var list = string.Join("\n", sel.Take(8).Select(l => $"{S(l.D, "id")}  {Short(S(l.D, "summary"), 70)}")) + (sel.Count > 8 ? $"\n… and {sel.Count - 8} more" : "");
            if (MessageBox.Show($"Delete {sel.Count} report(s) for good?\n\n{list}\n\nThis cannot be undone.", "TcAgent Knowledge - delete feedback",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var (ok, output) = await Cli($"knowledge feedback --action delete --key {Quote(string.Join(",", sel.Select(l => S(l.D, "id"))))}", "Deleting");
            Report(ok, output, "Delete feedback");
            if (ok) Info(output.Trim());
            Load();
        }

        static string Short(string s, int n) => (s ?? "").Length <= n ? s ?? "" : s.Substring(0, n) + "…";

        async void SetFeedbackStatus()
        {
            var ids = feedbackList.SelectedItems.OfType<Line>().Select(l => S(l.D, "id")).ToList();
            if (ids.Count == 0) { Hint("Select one or more reports first."); return; }
            var r = Prompt($"Status of {ids.Count} report(s)", ("Status", new[] { "resolved", "wontfix", "duplicate", "open" }, "resolved"));
            if (r == null) return;
            var (ok, output) = await Cli($"knowledge feedback --action resolve --key {Quote(string.Join(",", ids))} --message {r[0]}", "Setting the status");
            Report(ok, output, "Set status");
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
            Summary();
        }

        void DrawChart()
        {
            chart.Children.Clear();
            var w = Workflow();
            double W = chart.ActualWidth, H = chart.ActualHeight;
            var runs = w == null ? new List<Dictionary<string, object>>() : Arr(w["series"]).ToList();
            if (w == null || runs.Count < 2 || W < 50 || H < 50)
            {
                // too little for a curve: say why instead of an empty frame
                if (data != null && W >= 50)
                    chart.Children.Add(new TextBlock { Margin = new Thickness(12), Foreground = Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Max(100, W - 24),
                                                       Text = w == null ? "No workflow runs yet - start a workflow in the chat (e.g. new-feature); every run adds a point here."
                                                                        : $"{runs.Count} run of {S(w, "name")} so far - the curve appears from the second run, trends from 20 runs." });
                return;
            }
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
