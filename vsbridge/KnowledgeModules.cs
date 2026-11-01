// TcAgent Knowledge - tab Modules: the groups of tools the AI agent sees and the plugins (server: Modules.cs, Plugins.cs;
// docs/PLUGINS.md).
//
//   Modules  a check box per module switches it on or off - for me (user level, the default) or for this project
//            (<solution>\.tcagent\modules.json, in Git for the team); grey when it cannot be changed here (core, locked by
//            company or team, needs another module) - the tooltip says why. Company and team files are only shown.
//   Plugins  the plugins of every level with their state; a plugin of the project or team level runs only after it was
//            approved for exactly its content. Approve … shows what runs; only a mouse click on Approve approves (like the
//            gate card in the chat: no Enter, no keyboard, no UI automation - an agent cannot approve a plugin here).
//
// The window does no work itself: TcAgent.exe modules --json / enable / disable, plugins --json / approve / revoke.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class KnowledgeControl
    {
        readonly ListView moduleList = NewList(), pluginList = NewList();
        readonly RadioButton forMe = new RadioButton { Content = "me (user level)", IsChecked = true, GroupName = "tcagent-modules-level", Margin = new Thickness(6, 0, 10, 0),
                                                       VerticalAlignment = VerticalAlignment.Center, ToolTip = "%LOCALAPPDATA%\\TcAgent\\modules.json - all your projects" };
        readonly RadioButton forProject = new RadioButton { Content = "this project (in Git)", GroupName = "tcagent-modules-level", VerticalAlignment = VerticalAlignment.Center,
                                                            ToolTip = "<solution>\\.tcagent\\modules.json - for everybody who works on this solution" };
        readonly TextBlock pluginsHeading = Heading("");
        readonly StackPanel moduleDetail = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        Dictionary<string, object> modData, plugData;
        FrameworkElement modulesPage;
        bool modulesLoading;

        // the window of a module (shown in its details)
        static readonly Dictionary<string, string> ModuleWindows = new Dictionary<string, string> { ["test"] = "TcUnit Tests (stays open with a hint when off)" };

        Line SelectedModule => moduleList.SelectedItem as Line ?? pluginList.SelectedItem as Line;
        Dictionary<string, object> SelectedPlugin => pluginList.SelectedItem is Line l ? l.D : null;
        string ModuleLevel => forProject.IsChecked == true ? "project" : "user";

        FrameworkElement ModulesPage()
        {
            if (modulesPage != null) return modulesPage;
            bool NeedsApproval() => S(SelectedPlugin, "status") == "needs approval";
            bool Approved() => SelectedPlugin is Dictionary<string, object> p && S(p, "status") == "ready" && S(p, "level") is "project" or "team";
            var acts = new[] {
                A("Approve plugin …", "Shows what the selected plugin runs - only a mouse click on Approve approves it (for exactly its content)", ApprovePlugin, NeedsApproval),
                A("Revoke approval", "The selected plugin of the project or team level no longer runs until it is approved again", RevokePlugin, Approved),
                Sep,
                A("Open folder", "The folder of the selected plugin, otherwise the plugin folder of your user level", OpenModuleFolder),
                A("Open modules.json", "The modules.json of the level the ticks change (me or this project)", OpenModulesJson, () => File.Exists(ModulesJson(ModuleLevel) ?? "")),
                Sep,
                A("Copy path", "The folder of the selected plugin", () => CopyPath(S(SelectedPlugin, "folder")), () => SelectedPlugin != null) };
            var bar = Bar(acts);
            var h = Heading("");
            Heading(h, "Modules", "the tools the AI agent sees - a change applies when the AI client starts TcAgent again; the command line keeps every tool");
            var scope = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
            scope.Children.Add(Label("Ticks change the setting for"));
            scope.Children.Add(forMe); scope.Children.Add(forProject);
            foreach (var r in new[] { forMe, forProject }) { r.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey); r.Click += (s, e) => { FillModules(); UpdateGates(); }; }
            Columns(moduleList, ("", 30, "?" + nameof(Line.On)), ("Module", 210, nameof(Line.C1)), ("Tools", 50, nameof(Line.C2)), ("Set by", 130, nameof(Line.C3)), ("State", 300, "!" + nameof(Line.C4)));
            Columns(pluginList, ("", 30, "?" + nameof(Line.On)), ("Plugin", 210, nameof(Line.C1)), ("Tools", 50, nameof(Line.C2)), ("Level", 130, nameof(Line.C3)), ("State", 300, "!" + nameof(Line.C4)));
            foreach (var l in new[] { moduleList, pluginList })
            {
                l.ContextMenu = MenuOf(acts);
                var list = l;
                // a tick switches the module on or off (the check box binds Line.On)
                l.AddHandler(ToggleButton.ClickEvent, new RoutedEventHandler((s, e) => { if ((e.OriginalSource as FrameworkElement)?.DataContext is Line line) Tick(line); }));
                l.KeyDown += (s, e) => { if (e.Key == Key.Space && list.SelectedItem is Line line && line.Can) { line.On = !line.On; Tick(line); e.Handled = true; } };
                l.SelectionChanged += (s, e) =>
                {
                    if (list.SelectedItem != null) (list == moduleList ? pluginList : moduleList).SelectedItem = null;
                    ShowModule();
                };
            }
            var modules = WithEmpty(moduleList, "Reading the modules …");
            var plugins = WithEmpty(pluginList, "No plugins - a plugin is a folder with a plugin.json in %LOCALAPPDATA%\\TcAgent\\plugins (or in the company, team or project " +
                                                "folder); see docs/PLUGINS.md and the example docs/plugins/todo-finder.");
            var detail = new ScrollViewer { Content = moduleDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            LoadModules();
            return modulesPage = Rows((h, AutoH, 0), (bar, AutoH, 0), (scope, AutoH, 0), (modules, Star(3), 120), (pluginsHeading, AutoH, 0), (plugins, Star(1.2), 60),
                                      (Splitter(), AutoH, 0), (detail, Star(1.6), 60));
        }

        async void LoadModules()
        {
            if (modulesLoading) return;
            modulesLoading = true;
            try
            {
                var (okM, outM) = await Cli("modules --json", "Reading the modules");
                var (okP, outP) = await Cli("plugins --json");
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (!okM) { status.Text = "Modules: " + outM.Trim().Split('\n')[0] + " (TcAgent.exe of version 0.51 or newer is needed)"; return; }
                modData = ser.Deserialize<Dictionary<string, object>>(Json(outM));
                plugData = okP ? ser.Deserialize<Dictionary<string, object>>(Json(outP)) : null;
                FillModules();
                status.Text = "Ready.";
            }
            catch (Exception ex) { Error("Modules: " + ex.Message); }
            finally { modulesLoading = false; }
        }

        // the JSON object in the output (the log lines of TcAgent.exe go to stderr, which Cli puts behind it)
        static string Json(string output)
        {
            int a = output.IndexOf('{'), b = output.LastIndexOf('}');
            return a >= 0 && b > a ? output.Substring(a, b - a + 1) : "{}";
        }

        Dictionary<string, object> LevelOf(string level) => Arr(modData?["levels"]).FirstOrDefault(l => S(l, "level") == level);
        string ModulesJson(string level) => S(LevelOf(level), "file");

        static bool Lists(Dictionary<string, object> level, string key, string id) =>
            level != null && level.TryGetValue(key, out var v) && (v as System.Collections.IEnumerable)?.Cast<object>().Any(x => string.Equals(x as string, id, StringComparison.OrdinalIgnoreCase)) == true;

        void FillModules()
        {
            if (modData == null) return;
            forProject.IsEnabled = LevelOf("project") != null;
            if (!forProject.IsEnabled) forMe.IsChecked = true;
            var keep = SelectedModule?["id"] ?? S(SelectedPlugin, "id");
            var modules = Arr(modData["modules"]).ToList();
            moduleList.ItemsSource = modules.Where(m => !B(m, "plugin")).Select(m => ModuleLine(m, null)).ToList();
            var plugins = Arr(plugData?["plugins"]).ToList();
            pluginList.ItemsSource = plugins.Select(p => ModuleLine(modules.FirstOrDefault(m => S(m, "id") == "plugin:" + S(p, "id")), p)).ToList();
            Heading(pluginsHeading, $"Plugins ({plugins.Count})", "own tools as MCP programs; project and team plugins run only after approval");
            var again = (moduleList.ItemsSource as IEnumerable<Line>).FirstOrDefault(l => l["id"] == keep);
            if (again != null) moduleList.SelectedItem = again;
            else if ((pluginList.ItemsSource as IEnumerable<Line>).FirstOrDefault(l => S(l.D, "id") == keep) is Line pl) pluginList.SelectedItem = pl;
            ShowModule();
            Summary();
            UpdateGates();
        }

        /// <summary>A line of the module list (m) or the plugin list (p, with its module m if it is ready).</summary>
        Line ModuleLine(Dictionary<string, object> m, Dictionary<string, object> p)
        {
            var id = p != null ? "plugin:" + S(p, "id") : S(m, "id");
            var me = LevelOf(ModuleLevel);
            bool on = m != null && B(m, "on");
            string state, why = null; Brush mark;
            if (p != null && m == null)
            {
                var st = S(p, "status");
                state = st == "needs approval" ? "◐ needs approval" : st == "not allowed" ? "⊘ not allowed by the company" : st == "duplicate" ? "↷ replaced (same id higher up)" : "✗ " + st;
                mark = st == "needs approval" ? Orange : Red;
                why = S(p, "reason");
            }
            else if (B(m, "core")) { state = "always on"; mark = Gray; why = "The core cannot be switched off."; }
            else if (on) { state = "● on" + (S(m, "requires") is string rq ? " · " + rq : S(m, "dependsOn") is string dep ? " · needs " + dep : ""); mark = Green; }
            else { state = "○ off" + (B(m, "locked") ? " · locked" : "") + (S(m, "reason") is string r ? " · " + r : ""); mark = B(m, "locked") || S(m, "reason") != null ? Orange : Gray; }
            // can the tick change it on the chosen level?
            bool can = m != null && !B(m, "core") && !B(m, "locked") && !(S(m, "reason") ?? "").StartsWith("needs the module");
            if (m != null && B(m, "locked")) why = $"Locked by the {S(m, "by") ?? "company"} level - only that level can change it.";
            else if (m != null && !can && why == null) why = S(m, "reason");
            var by = S(m, "by");
            return new Line
            {
                D = p ?? m, C1 = p != null ? (S(p, "name") ?? S(p, "id")) + (S(p, "version") is string v ? "  " + v : "") : S(m, "name"),
                C2 = p != null ? (p.TryGetValue("tools", out var t) && t is Dictionary<string, object> td ? td.Count : 0).ToString(CultureInfo.InvariantCulture) : S(m, "tools"),
                C3 = p != null ? S(p, "level") : by == null ? "–" : by == "user" ? "me" : by + (B(m, "locked") ? " 🔒" : ""),
                C4 = state, Mark = mark, On = on, Can = can,
                Tip = why ?? (can ? $"Tick: switch on or off for {(ModuleLevel == "user" ? "me" : "this project")}" + (Lists(me, "on", id) || Lists(me, "off", id) ? $" (set in {ModulesJson(ModuleLevel)})" : "") : null)
            };
        }

        async void Tick(Line line)
        {
            var id = line.D != null && line.D.ContainsKey("folder") ? "plugin:" + S(line.D, "id") : S(line.D, "id");
            if (!line.Can) { FillModules(); return; }
            var (ok, output) = await Cli($"modules {(line.On ? "enable" : "disable")} {Quote(id)} --level {ModuleLevel}", line.On ? "Switching on" : "Switching off");
            if (ok) Info(output.Trim().Split('\n')[0]);
            else Report(false, output, line.On ? "Switch on" : "Switch off");
            LoadModules();
        }

        void ShowModule()
        {
            moduleDetail.Children.Clear();
            var l = SelectedModule;
            if (l == null)
            {
                moduleDetail.Children.Add(Words("Select a module or plugin - its tools, what it needs and where it is set appear here. Tick a check box to switch it on or off."));
                return;
            }
            var d = l.D;
            var plugin = d.ContainsKey("folder");
            var title = Words(l.C1, true);
            title.FontSize = 14;
            moduleDetail.Children.Add(title);
            moduleDetail.Children.Add(Words(S(d, "description") ?? ""));
            void Kv(string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var key = Words(k); key.Width = 90; key.Opacity = 0.7;
                DockPanel.SetDock(key, Dock.Left); row.Children.Add(key);
                row.Children.Add(Words(v));
                moduleDetail.Children.Add(row);
            }
            if (plugin)
            {
                var tools = d.TryGetValue("tools", out var t) && t is Dictionary<string, object> td ? td : new Dictionary<string, object>();
                Kv("State", l.C4 + (S(d, "reason") is string r ? " - " + r : ""));
                Kv("Tools", tools.Count == 0 ? "none declared - every tool it offers counts as changing and is confirmed before each call"
                                             : string.Join(", ", tools.Select(x => $"{x.Key} ({x.Value})")));
                Kv("Runs", S(d, "command"));
                Kv("Level", S(d, "level"));
                Kv("Folder", UserPath(S(d, "folder")));
                Kv("sha256", S(d, "hash"));
            }
            else
            {
                var names = d.TryGetValue("toolNames", out var tn) ? (tn as System.Collections.IEnumerable)?.Cast<object>().Select(x => x.ToString()).ToList() : null;
                Kv("Tools", names == null ? S(d, "tools") : string.Join(", ", names));
                Kv("Needs", (S(d, "dependsOn") is string dep ? $"the module {dep} (switching it off switches this off too)" : null) ??
                            (S(d, "requires") is string rq ? rq : null));
                Kv("Window", ModuleWindows.TryGetValue(S(d, "id") ?? "", out var win) ? win : null);
                Kv("State", l.C4 + (l.Tip != null && !l.Can ? " - " + l.Tip : ""));
            }
            // what every level's modules.json says about it
            var id = plugin ? "plugin:" + S(d, "id") : S(d, "id");
            var levels = Arr(modData?["levels"]).Select(lv => $"{(S(lv, "level") == "user" ? "me" : S(lv, "level"))} " +
                                                              (Lists(lv, "off", id) ? "off" : Lists(lv, "on", id) ? "on" : "–") + (Lists(lv, "locked", id) ? " 🔒" : ""));
            Kv("Levels", string.Join(" · ", levels) + "   (later wins, unless locked)");
        }

        TextBlock Words(string text, bool bold = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        /// <summary>The approval of a plugin: what it runs, from where, its hash and tools. Only a mouse click on Approve approves -
        /// Enter and Esc cancel, the button takes no focus and reacts to no Click event (keyboard, UI automation).</summary>
        void ApprovePlugin()
        {
            var p = SelectedPlugin;
            if (p == null || S(p, "status") != "needs approval") { Hint("Select a plugin that needs approval."); return; }
            var tools = p.TryGetValue("tools", out var t) && t is Dictionary<string, object> td ? td : new Dictionary<string, object>();
            var w = Dialog("Approve plugin - " + (S(p, "name") ?? S(p, "id")), 560);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12), MaxWidth = 720 };
            var q = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            q.Inlines.Add(new System.Windows.Documents.Run("⚠ ") { Foreground = Orange, FontWeight = FontWeights.SemiBold });
            q.Inlines.Add(new System.Windows.Documents.Run($"Run the plugin \"{S(p, "name") ?? S(p, "id")}\" {S(p, "version")} from the {S(p, "level")} level?"));
            sp.Children.Add(q);
            var text = $"Runs:    {S(p, "command")}\nFolder:  {S(p, "folder")}\nsha256:  {S(p, "hash")}\nTools:   " +
                       (tools.Count == 0 ? "none declared - every tool counts as changing and is confirmed before each call" : string.Join(", ", tools.Select(x => $"{x.Key} ({x.Value})")));
            sp.Children.Add(new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(4), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                                            Text = "The program runs as you, on this PC, whenever an AI client starts TcAgent. Approve only if you trust its content. The approval counts " +
                                                   "for exactly these files - a changed file needs a new approval. Its calls of TwinCAT go through TcAgent's checks; what it does " +
                                                   "on its own (files, network, ADS) TcAgent cannot stop." });
            var approve = new Button { Content = "Approve", MinWidth = 86, Margin = new Thickness(0, 0, 6, 0), Focusable = false, IsTabStop = false, ToolTip = "Approve with a mouse click" };
            bool pressed = false, approved = false;
            approve.PreviewMouseLeftButtonDown += (s, e) => { pressed = true; e.Handled = true; };
            approve.PreviewMouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (pressed && approve.IsMouseOver) { approved = true; w.DialogResult = true; }
                pressed = false;
            };
            approve.MouseLeave += (s, e) => pressed = false;
            var cancel = new Button { Content = "Cancel", MinWidth = 86, IsCancel = true, IsDefault = true };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bar.Children.Add(approve); bar.Children.Add(cancel);
            sp.Children.Add(bar);
            w.Content = sp;
            w.Loaded += (s, e) => cancel.Focus();
            w.ShowDialog();
            if (approved) RunPlugins($"plugins approve {Quote(S(p, "id"))}", "Approving", "Approved - it runs the next time an AI client starts TcAgent.");
        }

        void RevokePlugin()
        {
            var p = SelectedPlugin;
            if (p == null) return;
            if (!Confirm("Revoke approval", $"The plugin \"{S(p, "name") ?? S(p, "id")}\" no longer runs until it is approved again.", null, "Revoke")) return;
            RunPlugins($"plugins revoke {Quote(S(p, "id"))}", "Revoking", null);
        }

        async void RunPlugins(string args, string what, string done)
        {
            var (ok, output) = await Cli(args, what);
            if (ok) Info((done ?? output.Trim().Split('\n')[0]).Trim());
            else Report(false, output, what);
            LoadModules();
        }

        void OpenModuleFolder()
        {
            var f = S(SelectedPlugin, "folder") ?? Arr(plugData?["folders"]).Where(x => S(x, "level") == "user").Select(x => S(x, "folder")).FirstOrDefault();
            if (f == null) return;
            if (!Directory.Exists(f) && S(SelectedPlugin, "folder") == null) Directory.CreateDirectory(f);   // the user's own plugin folder: made on first use
            ShowInExplorer(f);
        }

        void OpenModulesJson()
        {
            var f = ModulesJson(ModuleLevel);
            if (f != null && File.Exists(f)) OpenInEditor(f);
        }

        string ModulesSummary()
        {
            var m = (moduleList.ItemsSource as IEnumerable<Line>)?.ToList() ?? new List<Line>();
            var p = (pluginList.ItemsSource as IEnumerable<Line>)?.ToList() ?? new List<Line>();
            var tools = Arr(modData?["modules"]).Where(x => B(x, "on")).Sum(x => (int)(N(x, "tools") ?? 0));
            return modData == null ? "" : $"{m.Count(x => x.On)} of {m.Count} modules on · {p.Count(x => x.On)} of {p.Count} plugins on · {tools} tools for the agent";
        }
    }
}
