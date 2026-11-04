// TcAgentBridge - new versions and About in the TcAgent Chat: the notices above the conversation, What's new and About TcAgent.
// The work is done by TcAgent.exe update / about (src/Updates.cs) in the background, after the IDE has loaded; a request that
// fails shows nothing. Downloading and installing start only on a click; the XAE Shell installs from the ZIP as documented.
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        const string ReleasesUrl = "https://github.com/ThorstenBrach/TcAgent/releases/latest";
        const string RepoUrl = "https://github.com/ThorstenBrach/TcAgent";

        readonly StackPanel notices = new StackPanel();
        readonly Ellipse updateDot = new Ellipse { Width = 6, Height = 6, Fill = AccentBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 2, 0), Visibility = Visibility.Collapsed };
        Dictionary<string, object> update;       // the last answer of TcAgent.exe update check
        Border updateStrip, sourcesStrip;
        bool checksStarted;

        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
        static bool B(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b && b;
        static IEnumerable<Dictionary<string, object>> List(Dictionary<string, object> d, string k) =>
            (d != null && d.TryGetValue(k, out var v) ? v as IEnumerable : null)?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>();

        static bool InXaeShell
        {
            get { try { return Process.GetCurrentProcess().ProcessName.IndexOf("TcXaeShell", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } }
        }

        // TcAgent.exe answers in JSON; an older server without these commands gives nothing usable - then there is simply no notice
        static async Task<Dictionary<string, object>> Ask(string args, string sln = null)
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) return null;
            return await Task.Run(() =>
            {
                try { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(PromptsCli(exe, args, sln) ?? ""); }
                catch { return null; }
            });
        }

        // once per window, when the IDE has loaded and the agent had its start
        async void StartChecks()
        {
            if (checksStarted) return;
            checksStarted = true;
            try
            {
                await (ideLoaded = ideLoaded ?? WaitForIde());
                await Task.Delay(15000);
                ShowUpdate(await Ask("update check --json --current " + BridgePackage.BridgeVersion));
                var src = await Ask("update sources --json", TcUnitControl.SolutionPath());
                var items = src != null && src.TryGetValue("items", out var o) ? (o as IEnumerable)?.OfType<object>().Count() ?? 0 : 0;
                if (items > 0) ShowSourcesNotice(items);
            }
            catch (Exception ex) { BridgePackage.Instance?.LogLine("Update check: " + ex.Message); }
        }

        void ShowUpdate(Dictionary<string, object> r)
        {
            if (r == null) return;
            update = r;
            updateDot.Visibility = B(r, "available") ? Visibility.Visible : Visibility.Collapsed;
            if (updateStrip != null) { notices.Children.Remove(updateStrip); updateStrip = null; }
            if (!B(r, "notify")) return;
            updateStrip = Strip(AccentBrush, new[] { new Run("TcAgent " + S(r, "latest")) { FontWeight = FontWeights.SemiBold }, new Run($" is available (you have {S(r, "current")})") },
                                "What's new …", true, ShowWhatsNew, () => { notices.Children.Remove(updateStrip); updateStrip = null; });
            notices.Children.Insert(0, updateStrip);
        }

        void ShowSourcesNotice(int items)
        {
            if (sourcesStrip != null) notices.Children.Remove(sourcesStrip);
            var text = items == 1 ? "1 installed item has a newer version in its source" : $"{items} installed items have newer versions in their sources";
            sourcesStrip = Strip(WarnBrush, new[] { new Run(text) }, "Show", false, () =>
            {
                try { BridgePackage.Instance?.ShowKnowledge(true); KnowledgeControl.Instance?.ShowSources(); } catch (Exception ex) { status.Text = "Knowledge window: " + ex.Message; }
            }, () => { notices.Children.Remove(sourcesStrip); sourcesStrip = null; });
            notices.Children.Add(sourcesStrip);
        }

        // a line above the conversation: a colored edge, the text, one button, ✕ (for this session)
        Border Strip(Brush edge, Run[] text, string button, bool primary, Action act, Action close)
        {
            var line = new DockPanel();
            var x = new Button { Content = "✕", Width = 20, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Close" };
            x.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            x.Click += (s, e) => close();
            var b = CardButton(button, primary);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.Click += (s, e) => act();
            DockPanel.SetDock(x, Dock.Right); line.Children.Add(x);
            DockPanel.SetDock(b, Dock.Right); line.Children.Add(b);
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            t.Inlines.AddRange(text);
            line.Children.Add(t);
            return new Border { Child = line, Background = AgentBubble, BorderBrush = edge, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(6, 3, 4, 3), Margin = new Thickness(6, 2, 6, 0) };
        }

        async void CheckNow()
        {
            status.Text = "Looking for a new version …";
            var r = await Ask("update check --json --force --current " + BridgePackage.BridgeVersion);
            if (r == null) { status.Text = "Could not check for updates: this TcAgent.exe has no update command."; return; }
            r["notify"] = B(r, "available");         // asked for it: a skipped version counts again
            ShowUpdate(r);
            status.Text = S(r, "error") is string e ? "Could not check for updates: " + e
                        : B(r, "available") ? $"TcAgent {S(r, "latest")} is available." : $"TcAgent {BridgePackage.BridgeVersion} is up to date.";
            if (S(r, "error") == null && B(r, "available")) ShowWhatsNew();
        }

        static Window Dialog(string title, double width, double height)
        {
            var w = new Window { Title = title, Width = width, Height = height, MinWidth = 380, MinHeight = 300, ShowInTaskbar = false, Owner = Application.Current?.MainWindow,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip };
            if (Application.Current != null) ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            return w;
        }

        static void Open(string target, Action<string> fail)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { fail("Could not open it: " + ex.Message); }
        }

        // ------------------------------------------------------------------ What's new
        void ShowWhatsNew()
        {
            var r = update;
            var releases = List(r, "releases").ToList();
            if (releases.Count == 0) return;
            var latest = releases[0];
            var w = Dialog("TcAgent update", 560, 520);
            var head = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
            head.Children.Add(new TextBlock { Text = "TcAgent " + S(latest, "version"), FontSize = 15, FontWeight = FontWeights.SemiBold });
            var host = InXaeShell ? "TwinCAT XAE Shell" : "Visual Studio 2022";
            var when = DateTime.TryParse(S(latest, "published"), out var d) ? "Published " + d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " · " : "";
            head.Children.Add(new TextBlock { Text = $"{when}you have {S(r, "current")} · {host}", Opacity = 0.7 });

            var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5 };
            doc.SetResourceReference(FlowDocument.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            foreach (var rel in releases) AddNotes(doc, S(rel, "version"), S(rel, "notes"));
            var notes = new FlowDocumentScrollViewer { Document = doc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 12, 6) };

            var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 2) };
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(12, 0, 12, 8) };
            var github = CardButton("Open on GitHub");
            github.Click += (s, e) => Open(S(latest, "url") ?? ReleasesUrl, t => state.Text = t);
            var skip = CardButton("Skip this version");
            skip.ToolTip = "No notice for this version; a newer one is shown again";
            skip.Click += async (s, e) =>
            {
                await Ask("update skip " + S(latest, "version"));
                r["notify"] = false; ShowUpdate(r); w.Close();
            };
            var later = CardButton("Later");
            later.IsCancel = true;
            later.Click += (s, e) => w.Close();
            var bar = new DockPanel { Margin = new Thickness(12, 4, 12, 10), LastChildFill = false };
            string file = null;
            if (InXaeShell)
            {
                // from the browser the ZIP keeps its download mark, and the documented way (unblock, extract, install) stays the user's
                hint.Text = "The XAE Shell installs from the ZIP: download it on GitHub, unblock it, extract it and run install-xaeshell.ps1 (docs/INSTALLATION.md).";
            }
            else
            {
                var install = CardButton("Download", true);
                hint.Text = "Installing needs Visual Studio closed. Save your work first.";
                install.Click += async (s, e) =>
                {
                    if (file != null)
                    {
                        Open(file, t => state.Text = t);       // the VSIX installer of Visual Studio: it asks and waits for the IDE to close
                        w.Close();
                        return;
                    }
                    install.IsEnabled = false; state.ClearValue(TextBlock.ForegroundProperty);
                    state.Text = "Downloading …";
                    var res = await Ask("update download --json --current " + BridgePackage.BridgeVersion);
                    install.IsEnabled = true;
                    file = S(res, "file");
                    if (file == null) { state.Foreground = ErrorBrush; state.Text = S(res, "error") ?? "The download failed. Open the release on GitHub instead."; return; }
                    state.Foreground = OkBrush;
                    state.Text = $"✓ {System.IO.Path.GetFileName(file)} downloaded, checksum matches SHA256SUMS.txt";
                    install.Content = "Install …";
                };
                DockPanel.SetDock(install, Dock.Left); bar.Children.Add(install);
            }
            DockPanel.SetDock(github, Dock.Left); bar.Children.Add(github);
            DockPanel.SetDock(later, Dock.Right); bar.Children.Add(later);
            DockPanel.SetDock(skip, Dock.Right); bar.Children.Add(skip);
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom); DockPanel.SetDock(hint, Dock.Bottom); DockPanel.SetDock(state, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(bar); root.Children.Add(hint); root.Children.Add(state); root.Children.Add(notes);
            w.Content = root;
            w.ShowDialog();
        }

        // the release notes: headings, bullets (with their continuation lines), **bold** and `code` - the rest as plain text
        internal static void AddNotes(FlowDocument doc, string version, string markdown)
        {
            doc.Blocks.Add(new Paragraph(new Run(version)) { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : 12, 0, 2) });
            List bullets = null;
            Paragraph last = null;
            foreach (var raw in (markdown ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) { bullets = null; last = null; continue; }
                if (Regex.IsMatch(line, @"^## \[?\d")) continue;                    // the version again
                var h = Regex.Match(line, @"^#+\s+(.*)$");
                if (h.Success)
                {
                    bullets = null; last = null;
                    doc.Blocks.Add(new Paragraph(new Run(h.Groups[1].Value)) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 1) });
                    continue;
                }
                if (Regex.IsMatch(line, @"^\s*[-*]\s+"))
                {
                    if (bullets == null) { bullets = new List { Margin = new Thickness(0), Padding = new Thickness(14, 0, 0, 0) }; doc.Blocks.Add(bullets); }
                    last = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                    Inline(last, Regex.Replace(line, @"^\s*[-*]\s+", ""));
                    bullets.ListItems.Add(new ListItem(last));
                    continue;
                }
                if (last != null) { Inline(last, " " + line.Trim()); continue; }
                last = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                Inline(last, line);
                doc.Blocks.Add(last);
            }
        }

        static void Inline(Paragraph p, string text)
        {
            foreach (var part in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**")) p.Inlines.Add(new Run(part.Trim('*')) { FontWeight = FontWeights.SemiBold });
                else if (part.StartsWith("`")) p.Inlines.Add(new Run(part.Trim('`')) { FontFamily = new FontFamily("Consolas") });
                else p.Inlines.Add(new Run(Regex.Replace(part, @"\[([^\]]+)\]\([^)]+\)", "$1")));
            }
        }

        // ------------------------------------------------------------------ About
        async void ShowAbout()
        {
            var w = Dialog("About TcAgent", 500, 560);
            var body = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
            body.Children.Add(new TextBlock { Text = "TcAgent " + BridgePackage.BridgeVersion, FontSize = 15, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "Agent access to TwinCAT 3: code, build, load and test", TextWrapping = TextWrapping.Wrap });
            var who = new TextBlock { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            who.Inlines.Add(new Run("Author: Thorsten Brach · MIT License · "));
            var link = new Hyperlink(new Run("github.com/ThorstenBrach/TcAgent")) { Foreground = AccentBrush };
            link.Click += (s, e) => Open(RepoUrl, t => status.Text = t);
            who.Inlines.Add(link);
            body.Children.Add(who);
            TextBlock Head(string t) { var b = new TextBlock { Text = t, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 2) }; body.Children.Add(b); return b; }
            var install = new Grid(); var comps = new Grid();
            void Rows(Grid g, IEnumerable<string[]> rows)
            {
                g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
                int cols = 0, y = 0;
                foreach (var r in rows)
                {
                    while (cols < r.Length) { g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(cols == 1 ? 120 : 90) }); cols++; }
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (int x = 0; x < r.Length; x++)
                    {
                        var c = new TextBlock { Text = r[x], Margin = new Thickness(0, 1, 8, 1), TextTrimming = TextTrimming.CharacterEllipsis };
                        Grid.SetRow(c, y); Grid.SetColumn(c, x); g.Children.Add(c);
                    }
                    y++;
                }
            }
            string ide = Regex.Match(KnowledgeControl.BridgeLines(), @"IDE \(this window\):\*\* (.*)").Groups[1].Value.Trim();
            var installed = new List<string[]> { new[] { "Extension", BridgePackage.BridgeVersion }, new[] { "Server (TcAgent.exe)", Installer.RegisteredServerVersion() ?? "not set up" }, new[] { "IDE", ide }, new[] { "TwinCAT", "…" } };
            var components = new List<string[]> { new[] { "Component", "Version", "License" } };
            Head("THIS INSTALLATION"); Rows(install, installed); body.Children.Add(install);
            Head("COMPONENTS"); Rows(comps, components); body.Children.Add(comps);
            Head("UPDATES");
            var check = new CheckBox { Content = "Look for new TcAgent versions once a day", IsChecked = true };
            var sources = new CheckBox { Content = "Look for newer versions in the repo sources once a day", IsChecked = true, Margin = new Thickness(0, 3, 0, 0) };
            var last = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            body.Children.Add(check); body.Children.Add(sources); body.Children.Add(last);
            void Switch(CheckBox box, string key)
            {
                RoutedEventHandler h = async (s, e) => await Ask($"update set {key} {(box.IsChecked == true ? "on" : "off")}");
                box.Checked += h; box.Unchecked += h;
            }
            void Show(Dictionary<string, object> r)
            {
                if (r == null) return;
                void One(CheckBox box, string key, string lockKey)
                {
                    box.IsChecked = B(r, key);
                    if (S(r, lockKey) is string by) { box.IsEnabled = false; box.ToolTip = $"Set on the {by} level (updates.json)"; }
                }
                One(check, "check", "checkLockedBy"); One(sources, "sources", "sourcesLockedBy");
                last.Text = (DateTime.TryParse(S(r, "lastCheck"), out var t) ? "Last checked " + t.ToString("g") + " · " : "") + "nothing but the request itself is sent";
            }

            var now = CardButton("Check now");
            now.Click += (s, e) => { w.Close(); CheckNow(); };
            var license = CardButton("License");
            license.Click += (s, e) => OpenPackaged("LICENSE", RepoUrl + "/blob/main/LICENSE");
            var notices3 = CardButton("Third-party notices");
            notices3.Click += (s, e) => OpenPackaged("THIRD-PARTY-NOTICES.md", RepoUrl + "/blob/main/THIRD-PARTY-NOTICES.md");
            var copy = CardButton("Copy");
            copy.Click += (s, e) =>
            {
                try
                {
                    var t = new StringBuilder($"TcAgent {BridgePackage.BridgeVersion} - Thorsten Brach - MIT License - {RepoUrl}\n");
                    foreach (var r in installed.Concat(components.Skip(1))) t.AppendLine(string.Join("  ", r));
                    Clipboard.SetText(t.ToString());
                }
                catch (Exception ex) { status.Text = "Could not copy: " + ex.Message; }
            };
            var close = CardButton("Close", true);
            close.IsCancel = true; close.IsDefault = true;
            close.Margin = new Thickness(0);
            close.Click += (s, e) => w.Close();
            var bar = new DockPanel { Margin = new Thickness(12, 4, 12, 10), LastChildFill = false };
            foreach (var b in new[] { now, license, notices3, copy }) { b.MinWidth = 0; DockPanel.SetDock(b, Dock.Left); bar.Children.Add(b); }
            DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            w.Content = root;
            w.Loaded += async (s, e) =>
            {
                try
                {
                    var about = await Ask("about --json");
                    if (about != null)
                    {
                        installed[3][1] = S(about, "twincat") ?? "?";
                        components.AddRange(List(about, "components").Select(c => new[] { S(c, "name"), S(c, "version"), S(c, "license") }));
                        Rows(install, installed); Rows(comps, components);
                    }
                    Show(await Ask("update check --json --current " + BridgePackage.BridgeVersion));
                    Switch(check, "check"); Switch(sources, "sources");
                }
                catch (Exception ex) { BridgePackage.Instance?.LogLine("About: " + ex.Message); }
            };
            w.ShowDialog();
            await Task.CompletedTask;
        }

        // the texts that came with the package; a .md may have no program here - then the page on GitHub
        void OpenPackaged(string name, string url)
        {
            var f = System.IO.Path.Combine(Installer.AppDir, name);
            if (File.Exists(f) && name == "LICENSE") { try { Process.Start("notepad.exe", "\"" + f + "\""); return; } catch { } }
            Open(File.Exists(f) && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? f : url, t => status.Text = t);
        }
    }
}
