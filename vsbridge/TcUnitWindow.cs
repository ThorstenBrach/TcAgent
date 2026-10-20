// TcUnit Tests - tool window in Visual Studio/TwinCAT XAE Shell, close to the Test Explorer of Visual Studio
// (the XAE Shell has none): tree PLC project > suite instance > test with status symbols, run buttons, search and status
// filter, counters, progress bar, details pane. Double-click opens the test method in the TwinCAT editor.
// The window does no TwinCAT work itself - it starts TcAgent.exe:
//   tcunit list --solution <sln>               tests from the code (JSON), after a build and when a .TcPOU/.TcGVL changes
//   tcunit run --attach <pid> [--tests ...]    the run; one JSON event per line, "cancel" on stdin stops it
// User guide: docs/TCUNIT_TESTS.md
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
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
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
            public string Instance, Name, Description, EditorPath, File, Enable;
            public int Line, EditorLine;
            public string Status = "notrun", Message;
            public double? Duration;
            public TreeViewItem Item; public CheckBox Check; public TextBlock Glyph, Extra;
        }

        sealed class SuiteNode
        {
            public string Path, Type, Description, File, EditorPath, RunSuite;
            public int Line;
            public bool Selectable, Dynamic;
            public readonly List<TestNode> Tests = new List<TestNode>();
            public TreeViewItem Item; public CheckBox Check; public TextBlock Glyph, Extra;
        }

        readonly List<SuiteNode> suites = new List<SuiteNode>();
        // results survive a refresh of the tree: instance@test -> (status, message, duration)
        readonly Dictionary<string, (string Status, string Message, double? Duration)> lastResults = new Dictionary<string, (string, string, double?)>(StringComparer.OrdinalIgnoreCase);
        string project, treePath, configTarget, runMode;
        List<string> projects = new List<string>();

        // ------------------------------------------------------------------ view
        readonly Button runAll = Btn("▶ Run all", "Run every test of the PLC project");
        readonly Button runSel = Btn("Run selected", "Run the ticked suites and tests (or the selected one); the others are switched off for this run");
        readonly Button runFailed = Btn("Run failed", "Run the tests that failed in the last run");
        readonly Button cancel = Btn("■ Cancel", "Stop the PLC (reset); tests that have not finished stay \"not run\"");
        readonly Button refresh = Btn("↻ Refresh", "Read the tests from the code again");
        readonly ComboBox projectBox = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = "PLC project" };
        readonly TextBox search = new TextBox { MinWidth = 140, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Search (name, suite, description)" };
        readonly ComboBox filter = new ComboBox { MinWidth = 90, ToolTip = "Show only tests with this status" };
        readonly TextBlock counters = new TextBlock { Margin = new Thickness(6, 2, 6, 2) };
        readonly ProgressBar progress = new ProgressBar { Height = 6, Margin = new Thickness(6, 2, 6, 0), Minimum = 0, Maximum = 1 };
        readonly TextBlock progressText = new TextBlock { Margin = new Thickness(6, 0, 6, 2), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TreeView tree = new TreeView { BorderThickness = new Thickness(0) };
        readonly TextBox details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                  FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6) };
        readonly Button openCode = Btn("Open code", "Open the selected test in the editor (or double-click it)");
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };

        static readonly Brush Green = Frozen(Color.FromRgb(0x2E, 0xA0, 0x43)), Red = Frozen(Color.FromRgb(0xD1, 0x34, 0x38)),
                              Blue = Frozen(Color.FromRgb(0x1E, 0x88, 0xE5)), Gray = Frozen(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        static Button Btn(string text, string tip) =>
            new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };

        public TcUnitControl()
        {
            Instance = this;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var f in new[] { "All", "Failed", "Passed", "Not run", "Skipped" }) filter.Items.Add(f);
            filter.SelectedIndex = 0;

            var bar = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
            foreach (var b in new UIElement[] { runAll, runSel, runFailed, cancel, refresh, projectBox, search, filter }) bar.Children.Add(b);
            cancel.IsEnabled = false;

            var detailsPanel = new DockPanel();
            var detailsBar = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
            DockPanel.SetDock(openCode, Dock.Right);
            detailsBar.Children.Add(openCode);
            detailsBar.Children.Add(new TextBlock { Text = "Details", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(detailsBar, Dock.Top);
            detailsPanel.Children.Add(detailsBar); detailsPanel.Children.Add(details);
            details.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            details.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            tree.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            tree.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var split = new Grid();
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(tree, 0); Grid.SetRow(splitter, 1); Grid.SetRow(detailsPanel, 2);
            split.Children.Add(tree); split.Children.Add(splitter); split.Children.Add(detailsPanel);

            var root = new DockPanel();
            foreach (var top in new UIElement[] { bar, counters, progress, progressText }) { DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); }
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);
            root.Children.Add(split);
            Content = root;
            foreach (var t in new[] { counters, progressText, status }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            runAll.Click += (s, e) => StartRun(null, "all tests");
            runSel.Click += (s, e) => RunSelected();
            runFailed.Click += (s, e) => RunFailed();
            cancel.Click += (s, e) => Cancel();
            refresh.Click += (s, e) => Discover();
            search.TextChanged += (s, e) => ApplyFilter();
            filter.SelectionChanged += (s, e) => ApplyFilter();
            projectBox.SelectionChanged += (s, e) => { if (projectBox.SelectedItem is string p && p != project) { project = p; Discover(); } };
            tree.SelectedItemChanged += (s, e) => ShowDetails();
            tree.MouseDoubleClick += (s, e) => { if (Selected() is TestNode) { OpenSelected(); e.Handled = true; } };
            openCode.Click += (s, e) => OpenSelected();
            IsVisibleChanged += (s, e) => { if (IsVisible && suites.Count == 0 && !discovering) Discover(); };
            Dispatcher.ShutdownStarted += (s, e) => { try { if (running?.HasExited == false) running.StandardInput.WriteLine("cancel"); } catch { } };
            HookIdeEvents();
            Progress(null, 0, 0);
            status.Text = "Reading the tests from the code ...";
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

        void Clear() { suites.Clear(); tree.Items.Clear(); UpdateCounters(); }

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
            Clear();
            if (pj == null) { project = null; status.Text = "No TcUnit test suites in this solution (FUNCTION_BLOCK ... EXTENDS TcUnit.FB_TestSuite with an instance in a PROGRAM or GVL)."; return; }
            project = S(pj, "name"); treePath = S(pj, "treePath"); runMode = S(pj, "runMode");
            projectBox.SelectedItem = project;
            var types = L(pj, "suites").ToDictionary(s => S(s, "type"), s => s, StringComparer.OrdinalIgnoreCase);
            var projNode = new TreeViewItem { IsExpanded = true, Header = Header(null, null, project + "  (" + treePath + ")", null, out _, out _) };
            foreach (var inst in L(pj, "instances"))
            {
                types.TryGetValue(S(inst, "suite") ?? "", out var type);
                var sn = new SuiteNode
                {
                    Path = S(inst, "path"), Type = S(inst, "suite"), Description = S(inst, "description") ?? S(type, "description"),
                    File = S(type, "file"), Line = I(type, "line"), EditorPath = S(type, "editorPath"), RunSuite = S(inst, "runSuite"),
                    Selectable = B(type, "testsSelectable"), Dynamic = B(type, "dynamic")
                };
                foreach (var t in L(type, "tests"))
                {
                    var enable = L(inst, "tests").FirstOrDefault(x => S(x, "name") == S(t, "name"));
                    var tn = new TestNode
                    {
                        Instance = sn.Path, Name = S(t, "name"), Description = S(t, "description"), EditorPath = S(t, "editorPath"),
                        File = S(t, "file"), Line = I(t, "line"), EditorLine = I(t, "editorLine"), Enable = S(enable, "enable")
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
                projNode.Items.Add(BuildSuite(sn));
            }
            tree.Items.Add(projNode);
            ApplyFilter();
            UpdateCounters();
            var n = suites.Sum(x => x.Tests.Count);
            status.Text = $"{n} test(s) in {suites.Count} suite instance(s)" + (runMode == "parallel" ? " - TcUnit.RUN(): during a run only the overall progress" : runMode == null ? " - no TcUnit.RUN / RUN_IN_SEQUENCE found" : "") +
                          (string.IsNullOrEmpty(configTarget) || configTarget.Equals("local", StringComparison.OrdinalIgnoreCase) ? "" : " - target " + configTarget);
        }

        StackPanel Header(object owner, string glyph, string text, string tip, out TextBlock glyphBlock, out TextBlock extra, CheckBox check = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (check != null) sp.Children.Add(check);
            glyphBlock = new TextBlock { Text = glyph ?? "", Width = glyph == null ? 0 : 16, FontFamily = new FontFamily("Segoe UI Symbol"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            sp.Children.Add(glyphBlock);
            var t = new TextBlock { Text = text };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            sp.Children.Add(t);
            extra = new TextBlock { Margin = new Thickness(8, 0, 0, 0), Foreground = Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(extra);
            if (tip != null) sp.ToolTip = tip;
            return sp;
        }

        TreeViewItem BuildSuite(SuiteNode sn)
        {
            sn.Check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            var tip = (sn.Description ?? sn.Type) + (sn.RunSuite == null ? "\nNo bRunSuite: this suite always runs." : "") +
                      (!sn.Selectable && sn.Tests.Count > 0 ? "\nWhole suite only: not every test has a bRun_<TestMethod> bool." : "") +
                      (sn.Dynamic ? "\nTest names are built at run time: they appear after the first run." : "");
            sn.Item = new TreeViewItem { IsExpanded = true, Tag = sn, Header = Header(sn, "○", sn.Path + " : " + sn.Type, tip, out sn.Glyph, out sn.Extra, sn.Check) };
            sn.Check.Checked += (s, e) => { foreach (var t in sn.Tests) if (t.Check != null) t.Check.IsChecked = true; };
            sn.Check.Unchecked += (s, e) => { foreach (var t in sn.Tests) if (t.Check != null) t.Check.IsChecked = false; };
            foreach (var t in sn.Tests) sn.Item.Items.Add(BuildTest(sn, t));
            UpdateSuite(sn);
            return sn.Item;
        }

        TreeViewItem BuildTest(SuiteNode sn, TestNode t)
        {
            t.Check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), IsEnabled = sn.Selectable || t.Enable != null };
            var tip = (t.Description ?? t.Name) + (t.Enable == null ? "\nNo bRun_ bool: runs with its suite." : "");
            t.Item = new TreeViewItem { Tag = t, Header = Header(t, "○", t.Name, tip, out t.Glyph, out t.Extra, t.Check) };
            UpdateTest(t);
            return t.Item;
        }

        // ------------------------------------------------------------------ status display
        void UpdateTest(TestNode t)
        {
            if (t.Glyph == null) return;
            (t.Glyph.Text, t.Glyph.Foreground) = Look(t.Status);
            t.Extra.Text = t.Duration is double d && t.Status is "passed" or "failed" ? Seconds(d) : t.Status == "skipped" ? "skipped" : "";
        }

        void UpdateSuite(SuiteNode sn)
        {
            if (sn.Glyph == null) return;
            var st = sn.Tests.Any(t => t.Status == "running") ? "running" : sn.Tests.Any(t => t.Status == "failed") ? "failed"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status == "skipped") ? "skipped"
                   : sn.Tests.Count > 0 && sn.Tests.All(t => t.Status is "passed" or "skipped") ? "passed" : "notrun";
            (sn.Glyph.Text, sn.Glyph.Foreground) = Look(st);
            var done = sn.Tests.Where(t => t.Status is "passed" or "failed").ToList();
            var dur = done.Sum(t => t.Duration ?? 0);
            sn.Extra.Text = $"{sn.Tests.Count} test(s)" + (done.Count > 0 ? $", {done.Count(t => t.Status == "failed")} failed, {Seconds(dur)}" : "");
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
            counters.Inlines.Clear();
            void Part(string glyph, Brush b, int n, string what)
            {
                counters.Inlines.Add(new System.Windows.Documents.Run(glyph + " ") { Foreground = b, FontFamily = new FontFamily("Segoe UI Symbol") });
                counters.Inlines.Add(new System.Windows.Documents.Run($"{n} {what}    "));
            }
            Part("✔", Green, all.Count(t => t.Status == "passed"), "passed");
            Part("✖", Red, all.Count(t => t.Status == "failed"), "failed");
            Part("⊘", Gray, all.Count(t => t.Status == "skipped"), "skipped");
            Part("○", Gray, all.Count(t => t.Status is "notrun" or "running"), "not run");
            runFailed.IsEnabled = running == null && all.Any(t => t.Status == "failed");
        }

        void ApplyFilter()
        {
            var q = (search.Text ?? "").Trim();
            var f = filter.SelectedItem as string ?? "All";
            bool Match(TestNode t) =>
                (f == "All" || (f == "Failed" && t.Status == "failed") || (f == "Passed" && t.Status == "passed") || (f == "Skipped" && t.Status == "skipped")
                 || (f == "Not run" && t.Status is "notrun" or "running")) &&
                (q.Length == 0 || t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.Instance.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                 || (t.Description ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var s in suites)
            {
                bool suiteHit = q.Length > 0 && (s.Type.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (s.Description ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                int shown = 0;
                foreach (var t in s.Tests)
                {
                    bool vis = Match(t) || (suiteHit && (f == "All"));
                    if (t.Item != null) t.Item.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
                    if (vis) shown++;
                }
                if (s.Item != null) s.Item.Visibility = shown > 0 || (s.Tests.Count == 0 && f == "All" && (q.Length == 0 || suiteHit)) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        object Selected() => (tree.SelectedItem as TreeViewItem)?.Tag;

        void ShowDetails()
        {
            var sb = new StringBuilder();
            switch (Selected())
            {
                case TestNode t:
                    sb.AppendLine(t.Instance + " @ " + t.Name);
                    if (t.Description != null) sb.AppendLine(t.Description);
                    sb.AppendLine();
                    sb.AppendLine("Status:   " + Text(t.Status) + (t.Duration is double d ? "   (" + Seconds(d) + ")" : ""));
                    if (!string.IsNullOrEmpty(t.Message)) { sb.AppendLine(); sb.AppendLine(t.Message.Replace(", ACT:", "\nACT:").Replace(", MSG:", "\nMSG:")); sb.AppendLine(); }
                    if (t.File != null) sb.AppendLine("Code:     " + Path.GetFileName(t.File) + ":" + t.Line);
                    sb.AppendLine("Enable:   " + (t.Enable ?? "- (runs with its suite)"));
                    break;
                case SuiteNode s:
                    sb.AppendLine(s.Path + " : " + s.Type);
                    if (s.Description != null) sb.AppendLine(s.Description);
                    sb.AppendLine();
                    sb.AppendLine($"{s.Tests.Count} test(s): {s.Tests.Count(t => t.Status == "passed")} passed, {s.Tests.Count(t => t.Status == "failed")} failed, {s.Tests.Count(t => t.Status == "skipped")} skipped");
                    sb.AppendLine("Enable:   " + (s.RunSuite ?? "- (no bRunSuite: the suite always runs)"));
                    if (!s.Selectable && s.Tests.Count > 0) sb.AppendLine("Single tests cannot be switched off (bRun_<TestMethod> missing) - whole suite only.");
                    if (s.Dynamic) sb.AppendLine("Test names are built at run time; they appear after the first run.");
                    if (s.File != null) sb.AppendLine("Code:     " + Path.GetFileName(s.File) + ":" + s.Line);
                    break;
            }
            details.Text = sb.ToString();
            openCode.IsEnabled = Selected() is TestNode tn ? tn.EditorPath != null : Selected() is SuiteNode sn2 && sn2.EditorPath != null;
        }

        static string Text(string status) => status == "notrun" ? "not run" : status;

        void OpenSelected()
        {
            try
            {
                if (Selected() is TestNode t && t.EditorPath != null) BridgePackage.Instance?.OpenInEditor(t.EditorPath, "implementation", t.EditorLine);
                else if (Selected() is SuiteNode s && s.EditorPath != null) BridgePackage.Instance?.OpenInEditor(s.EditorPath, "declaration", 0);
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
            var items = new List<string>();
            foreach (var s in suites)
            {
                var ticked = s.Tests.Where(t => t.Check?.IsChecked == true).ToList();
                if (s.Check?.IsChecked == true && (ticked.Count == s.Tests.Count || !s.Selectable)) items.Add(s.Path);
                else items.AddRange(ticked.Select(t => s.Selectable || t.Enable != null ? t.Instance + "@" + t.Name : s.Path));
            }
            if (items.Count == 0)
            {
                // nothing ticked: the selected tree item
                if (Selected() is TestNode t) items.Add(t.Instance + "@" + t.Name);
                else if (Selected() is SuiteNode s) items.Add(s.Path);
            }
            if (items.Count == 0) { status.Text = "Tick suites/tests (or select one) first."; return; }
            items = items.Distinct().ToList();
            StartRun(items, items.Count == 1 ? items[0] : items.Count + " selection(s)");
        }

        void RunFailed()
        {
            var failed = suites.SelectMany(s => s.Tests.Where(t => t.Status == "failed").Select(t => s.Selectable || t.Enable != null ? t.Instance + "@" + t.Name : s.Path)).Distinct().ToList();
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
            foreach (var t in suites.SelectMany(s => s.Tests)) { if (t.Status == "running") t.Status = "notrun"; UpdateTest(t); }
            try { running = Start(exe, args.ToString(), true); }
            catch (Exception ex) { status.Text = "TcAgent.exe could not be started: " + ex.Message; return; }
            BridgePackage.Instance?.LogLine("TcUnit run: " + exe + " " + args);
            runStarted = DateTime.Now;
            expected = 0;
            SetRunning(true);
            status.Text = "Starting " + what + " ...";
            Progress("Preparing ...", 0, 1);
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
            runAll.IsEnabled = runSel.IsEnabled = refresh.IsEnabled = projectBox.IsEnabled = !on;
            cancel.IsEnabled = on;
            runFailed.IsEnabled = !on && suites.SelectMany(s => s.Tests).Any(t => t.Status == "failed");
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
                s.Item.Items.Add(BuildTest(s, t));
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
                        if (Find(S(r, "instance"), S(r, "test"), false) is TestNode t) { t.Status = S(r, "status"); t.Message = null; t.Duration = null; UpdateTest(t); }
                    foreach (var s in suites) UpdateSuite(s);
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
                    if (sn != null) sn.Item.BringIntoView();
                    break;
                case "running":
                {
                    int index = I(ev, "index"), total = Math.Max(I(ev, "total"), expected);
                    var name = S(ev, "test");
                    if (name != null && Find(S(ev, "instance"), name, true) is TestNode t && t.Status == "notrun") { t.Status = "running"; UpdateTest(t); UpdateSuite(suites.First(x => x.Path == t.Instance)); }
                    var rem = Get(ev, "remainingSec") is int r ? $" - about {Seconds(r)} left" : Get(ev, "remainingSec") is decimal rd ? $" - about {Seconds((double)rd)} left" : "";
                    Progress(name != null ? $"Test {index} of {total}: {name}{rem}" : $"{index} of {total} test(s) finished{rem}", Math.Max(0, index - (name != null ? 1 : 0)), total);
                    break;
                }
                case "progress":
                {
                    // the project's own progress block (tcunit.json "progress") - shown as it is
                    var parts = new[] { "current", "total", "test", "remaining" }.Select(k => Get(ev, k)).ToList();
                    if (parts[1] != null)
                    {
                        int cur = Convert.ToInt32(parts[0] ?? 0), tot = Convert.ToInt32(parts[1]);
                        Progress($"Test {cur} of {tot}: {parts[2]}" + (parts[3] != null ? $" - remaining {parts[3]}" : ""), Math.Max(0, cur - 1), tot);
                    }
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
                    UpdateCounters();
                    if (Selected() == t) ShowDetails();
                    var all = suites.SelectMany(s => s.Tests).ToList();
                    int done = all.Count(x => x.Status is "passed" or "failed") ;
                    if (progress.Maximum < done) progress.Maximum = done;
                    break;
                }
                case "done":
                {
                    gotDone = true;
                    foreach (var t in suites.SelectMany(s => s.Tests).Where(t => t.Status == "running")) { t.Status = "notrun"; UpdateTest(t); }
                    foreach (var s in suites) UpdateSuite(s);
                    UpdateCounters();
                    ApplyFilter();
                    var st = S(ev, "status");
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

        // ------------------------------------------------------------------ re-read the tree after a build and when code is saved
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
                solutionEvents.Opened += () => { Watch(); project = null; lastResults.Clear(); Soon(); };
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
