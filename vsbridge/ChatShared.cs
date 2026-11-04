// TcAgentBridge - folders and files the agent may use in the TcAgent Chat: share a folder (Attach … or drop it on the input),
// the question "only this chat or keep it", the chips of this chat and ⋯ > Shared folders.
// What is kept lives in TcAgent.exe shared (src/SharedFolders.cs). A folder of this chat works at once in Claude Code: its
// read requests are answered here. Copilot takes folders when it starts; Gemini and ChatGPT only get the note in the prompt.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        readonly List<string> sessionFolders = new List<string>();
        readonly Dictionary<string, Border> folderChips = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        volatile string[] sharedRead = new string[0];       // read from the thread of the session

        // the kept folders of this solution (the file is small; TcAgent.exe writes it)
        static List<KeyValuePair<string, string>> KeptFolders()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var f = Path.Combine(Dir, "shared-folders.json");
                if (!File.Exists(f)) return list;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                foreach (var d in Dicts(root != null && root.TryGetValue("folders", out var o) ? o : null))
                    if (d.TryGetValue("path", out var p) && p is string path) list.Add(new KeyValuePair<string, string>(path, d.TryGetValue("solution", out var s) ? s as string : null));
            }
            catch { }
            return list;
        }

        static bool SameFolder(string a, string b) => string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        void RefreshShared(string solutionDir)
        {
            sharedRead = sessionFolders.Concat(KeptFolders().Where(k => k.Value == null || SameFolder(k.Value, solutionDir)).Select(k => k.Key))
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static bool TakesFolders(Backend be) => Regex.IsMatch(be.Exe ?? "", "claude|copilot", RegexOptions.IgnoreCase);

        string SharedArgs(Backend be, string solutionDir)
        {
            RefreshShared(solutionDir);
            return TakesFolders(be) ? string.Concat(sharedRead.Select(f => " --add-dir " + Quote(f))) : "";
        }

        string SharedNote() => sharedRead.Length == 0 ? null : "Folders the user shared for reading: " + string.Join("; ", sharedRead) + ".";

        // a read request of Claude Code for a shared folder: no card, the user decided that already
        bool SharedRead(string tool, Dictionary<string, object> input)
        {
            if (input == null || !(tool == "Read" || tool == "Grep" || tool == "Glob" || tool == "LS")) return false;
            var path = (input.TryGetValue("file_path", out var a) ? a as string : null) ?? (input.TryGetValue("path", out var b) ? b as string : null);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var full = Path.GetFullPath(path).TrimEnd('\\');
                return sharedRead.Any(f => string.Equals(full, f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        // "chat", "solution", "all"; null = cancelled
        static string AskKeep(string what, bool hasSolution)
        {
            var w = Dialog("Share with the agent", 430, 240);
            w.MinHeight = 0; w.SizeToContent = SizeToContent.Height; w.ResizeMode = ResizeMode.NoResize;
            var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 10) };
            sp.Children.Add(new TextBlock { Text = what, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = "How long should the agent use it?", Margin = new Thickness(0, 6, 0, 6) });
            var chat = new RadioButton { Content = "Only this chat", IsChecked = true };
            var sol = new RadioButton { Content = "Keep in the knowledge: this solution", IsEnabled = hasSolution, Margin = new Thickness(0, 4, 0, 0) };
            var all = new RadioButton { Content = "Keep in the knowledge: all my solutions", Margin = new Thickness(0, 4, 0, 0) };
            sp.Children.Add(chat); sp.Children.Add(sol); sp.Children.Add(all);
            sp.Children.Add(new TextBlock { Text = "What the agent reads goes to your AI provider.", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            string answer = null;
            var ok = CardButton("OK", true);
            ok.IsDefault = true; ok.MinWidth = 76;
            ok.Click += (s, e) => { answer = sol.IsChecked == true ? "solution" : all.IsChecked == true ? "all" : "chat"; w.Close(); };
            var cancel = CardButton("Cancel");
            cancel.IsCancel = true; cancel.Margin = new Thickness(0); cancel.MinWidth = 76;
            cancel.Click += (s, e) => w.Close();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            bar.Children.Add(ok); bar.Children.Add(cancel);
            sp.Children.Add(bar);
            w.Content = sp;
            w.ShowDialog();
            return answer;
        }

        static string ScopeArgs(string keep, string sln) => keep == "solution" && !string.IsNullOrEmpty(sln) ? "" : " --all";

        // TcAgent.exe shared ...: the error text, or null when it worked
        static async Task<string> Shared(string args, string sln)
        {
            var r = await Ask("shared " + args + " --json", sln);
            return r == null ? "This TcAgent.exe has no shared command." : S(r, "error");
        }

        internal async void ShareFolder(string folder = null)
        {
            if (folder == null)
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder the agent may read" })
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return; else folder = dlg.SelectedPath;
            }
            folder = folder.TrimEnd('\\');
            var sln = TcUnitControl.SolutionPath();
            // the check of the server first: a drive or the user profile never reaches the question
            var why = await Shared($"check --path {Quote(folder)}", null);
            if (why != null) { Message("Share a folder", why); return; }
            var keep = AskKeep(folder, !string.IsNullOrEmpty(sln));
            if (keep == null) return;
            if (keep == "chat") AddSessionFolder(folder);
            else
            {
                var err = await Shared($"add --path {Quote(folder)}" + ScopeArgs(keep, sln), keep == "solution" ? sln : null);
                if (err != null) { Message("Share a folder", err); return; }
                status.Text = "Kept in the knowledge: " + folder;
            }
            RefreshShared(SolutionDir());
        }

        void AddSessionFolder(string folder)
        {
            if (sessionFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
            sessionFolders.Add(folder);
            var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Stop sharing this folder" };
            close.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var label = new TextBlock { Text = "📁 " + folder, VerticalAlignment = VerticalAlignment.Center, ToolTip = "The agent may read this folder in this chat" };
            label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(label); sp.Children.Add(close);
            var chip = new Border { Child = sp, Background = AgentBubble, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 2, 1), Margin = new Thickness(0, 0, 4, 2) };
            close.Click += (s, e) => { RemoveSessionFolder(folder); RefreshShared(SolutionDir()); };
            folderChips[folder] = chip;
            chips.Children.Add(chip);
            input.Focus();
        }

        void RemoveSessionFolder(string folder)
        {
            sessionFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (folderChips.TryGetValue(folder, out var chip)) { chips.Children.Remove(chip); folderChips.Remove(folder); }
        }

        void ClearSessionFolders()
        {
            foreach (var f in sessionFolders.ToList()) RemoveSessionFolder(f);
            RefreshShared(SolutionDir());
        }

        // files from Attach … or a drop: one question for all of them; kept files are attached to this chat as well
        async void AddFiles(IEnumerable<string> paths)
        {
            var all = paths.ToList();
            foreach (var d in all.Where(Directory.Exists)) ShareFolder(d);
            var files = all.Where(File.Exists).ToList();
            if (files.Count == 0) return;
            var sln = TcUnitControl.SolutionPath();
            var keep = AskKeep(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files", !string.IsNullOrEmpty(sln));
            if (keep == null) return;
            foreach (var f in files) AddFile(f);
            if (keep == "chat") return;
            foreach (var f in files)
            {
                var err = await Shared($"keep --path {Quote(f)}" + ScopeArgs(keep, sln), keep == "solution" ? sln : null);
                if (err != null) { Message("Keep a file", err); return; }
            }
            status.Text = files.Count == 1 ? "Kept in the knowledge: " + Path.GetFileName(files[0]) : $"{files.Count} files kept in the knowledge";
            RefreshShared(SolutionDir());
        }

        void ShowAttachMenu()
        {
            var menu = new ContextMenu { PlacementTarget = attach, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            var files = new MenuItem { Header = "Files …" };
            files.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach documents for the agent" };
                if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
            };
            var folder = new MenuItem { Header = "Share a folder …", ToolTip = "The agent may read this folder without asking" };
            folder.Click += (s, e) => ShareFolder();
            menu.Items.Add(files); menu.Items.Add(folder);
            menu.IsOpen = true;
        }

        // ------------------------------------------------------------------ ⋯ > Shared folders
        void ShowSharedFolders()
        {
            var w = Dialog("Shared folders", 600, 400);
            var sln = TcUnitControl.SolutionPath();
            var dir = SolutionDir();
            var list = new ListBox { Margin = new Thickness(0, 6, 0, 0), MinHeight = 120 };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            Grid.SetIsSharedSizeScope(list, true);
            void Fill()
            {
                list.Items.Clear();
                void Row(string path, string scope, string solution)
                {
                    var g = new Grid { Tag = new[] { path, scope, solution } };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                    var a = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path };
                    var b = new TextBlock { Text = scope, Opacity = 0.8 };
                    Grid.SetColumn(b, 1);
                    g.Children.Add(a); g.Children.Add(b);
                    list.Items.Add(g);
                }
                foreach (var f in sessionFolders) Row(f, "This chat", null);
                foreach (var k in KeptFolders())
                    if (k.Value == null) Row(k.Key, "All my solutions", null);
                    else if (SameFolder(k.Value, dir)) Row(k.Key, "This solution", k.Value);
                    else Row(k.Key, "Other solution", k.Value);
                RefreshShared(dir);
            }
            string[] Selected() => (list.SelectedItem as Grid)?.Tag as string[];
            var add = CardButton("Add folder …");
            add.Click += async (s, e) => { w.IsEnabled = false; ShareFolder(); await Task.Delay(300); w.IsEnabled = true; Fill(); };
            var remove = CardButton("Remove");
            remove.Click += async (s, e) =>
            {
                var r = Selected();
                if (r == null) return;
                if (r[1] == "This chat") RemoveSessionFolder(r[0]);
                else if (await Shared($"remove --path {Quote(r[0])}", null) is string err) Message("Shared folders", err);
                Fill();
            };
            var scope = CardButton("All my solutions / This solution");
            scope.ToolTip = "Change where the selected folder applies";
            scope.Click += async (s, e) =>
            {
                var r = Selected();
                if (r == null || r[1] == "This chat") return;
                var toAll = r[1] != "All my solutions";
                if (!toAll && string.IsNullOrEmpty(sln)) return;
                if (await Shared($"add --path {Quote(r[0])}" + (toAll ? " --all" : ""), toAll ? null : sln) is string err) Message("Shared folders", err);
                Fill();
            };
            var fresh = CardButton("New chat now");
            fresh.ToolTip = "GitHub Copilot takes the folders when it starts";
            fresh.Click += (s, e) => { w.Close(); clear.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
            var ok = OkButton(w);
            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(12, 4, 12, 10) };
            foreach (var b in new[] { add, remove, scope }) { b.MinWidth = 0; DockPanel.SetDock(b, Dock.Left); bar.Children.Add(b); }
            DockPanel.SetDock(ok, Dock.Right); bar.Children.Add(ok);
            fresh.MinWidth = 0; DockPanel.SetDock(fresh, Dock.Right); bar.Children.Add(fresh);
            var head = new TextBlock { Text = "The agent may read these folders without asking. Changing a file there still asks you.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 0) };
            var foot = new StackPanel { Margin = new Thickness(12, 6, 12, 4) };
            foot.Children.Add(new TextBlock { Text = "⚠ What the agent reads goes to your AI provider. Share only what may leave this PC.", Foreground = WarnBrush, TextWrapping = TextWrapping.Wrap });
            foot.Children.Add(new TextBlock { Text = "Claude Code uses a change at once, GitHub Copilot with the next New chat. Gemini and ChatGPT reach kept folders through the search of the Reference sources.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var mid = new Border { Child = list, Margin = new Thickness(12, 0, 12, 0) };
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom); DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(bar); root.Children.Add(foot); root.Children.Add(mid);
            w.Content = root;
            Fill();
            w.ShowDialog();
        }
    }
}
