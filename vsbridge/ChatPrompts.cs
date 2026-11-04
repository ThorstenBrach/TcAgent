// TcAgent Chat: the prompt library (server: Prompts.cs). "/" at the start of a line of the input, or the button Prompts, opens a
// list above the input: typing filters by name, description and tags; arrow keys or a click choose, Enter puts the prompt into
// the input (never sent without the user: it can be adapted), Ctrl+Enter inserts and sends. A preview shows the text with the
// placeholders filled from the editor - {object}, {selection}, {solution}; {input:<question>} is asked in a small form first
// ({input:<question>|a|b} a choice, {input:<question>=value} a default). A prompt with tcagent-send: true goes off when complete.
// "Save as prompt …" under an own message keeps it as a prompt (a file of the knowledge, TcAgent.exe knowledge new).
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        sealed class PromptItem
        {
            public string Name, Description, Category, Level, Source, Body;
            public bool Send;
            public List<string> Tags = new List<string>();
        }

        const int MaxRecentPrompts = 5;
        static readonly Regex InputPlaceholder = new Regex(@"\{input:([^}]*)\}");
        static readonly Regex Placeholders = new Regex(@"\{(object|selection|solution|input:[^}]*)\}");
        static string RecentPromptsFile => Path.Combine(Dir, "prompts-recent.json");

        readonly Border promptPanel = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 6, 2), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
        readonly WrapPanel promptCategories = new WrapPanel { Margin = new Thickness(6, 5, 6, 3) };
        readonly ListBox promptList = new ListBox { BorderThickness = new Thickness(0), MaxHeight = 170, Background = Brushes.Transparent, Focusable = false };
        readonly TextBlock promptOrigin = new TextBlock { FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock promptPreview = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 110, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly StackPanel promptForm = new StackPanel { Margin = new Thickness(8, 6, 8, 8), Visibility = Visibility.Collapsed };
        readonly StackPanel promptChoose = new StackPanel();
        readonly Button promptsButton = new Button { Content = "Prompts", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Prepared prompts (or type / at the start of a line)" };
        List<PromptItem> prompts = new List<PromptItem>();
        string promptCategory;          // null = all
        int promptTokenStart = -1;      // where "/…" starts in the input; -1 = opened with the button, inserted at the cursor
        bool promptFilling;             // the form for {input:…} is open
        bool PromptsOpen => promptPanel.Visibility == Visibility.Visible;

        FrameworkElement BuildPromptPanel()
        {
            promptPanel.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            promptPanel.BorderBrush = AccentBrush;
            var hint = new TextBlock { Text = "↑↓ choose · Enter insert · Ctrl+Enter insert and send · Esc close", FontSize = 11, Opacity = 0.6, Margin = new Thickness(8, 3, 8, 3), TextWrapping = TextWrapping.Wrap };
            var preview = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
            preview.Children.Add(promptOrigin); preview.Children.Add(promptPreview);
            foreach (var t in new[] { hint, promptOrigin, promptPreview }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            promptChoose.Children.Add(promptCategories);
            promptChoose.Children.Add(promptList);
            promptChoose.Children.Add(Line()); promptChoose.Children.Add(preview);
            promptChoose.Children.Add(Line()); promptChoose.Children.Add(hint);
            var all = new StackPanel();
            all.Children.Add(promptChoose); all.Children.Add(promptForm);
            promptPanel.Child = all;
            promptList.SelectionChanged += (s, e) => ShowPromptPreview();
            promptList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if ((e.OriginalSource as DependencyObject) is DependencyObject d && ItemsControl.ContainerFromElement(promptList, d) is ListBoxItem i && i.Tag is PromptItem p) { promptList.SelectedItem = i; ChoosePrompt(p, false); e.Handled = true; }
            };
            promptsButton.Click += (s, e) => { if (PromptsOpen) ClosePrompts(); else OpenPrompts(-1, ""); input.Focus(); };
            // after the change is complete: the cursor is where the typing left it
            input.TextChanged += (s, e) => Dispatcher.BeginInvoke(new Action(PromptTextChanged), System.Windows.Threading.DispatcherPriority.Input);
            return promptPanel;
        }

        static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)));

        static Border Line()
        {
            var l = new Border { Height = 1 };
            l.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return l;
        }

        // "/te" before the cursor at the start of a line: the list follows the typing; a space or a second "/" ends it
        void PromptTextChanged()
        {
            if (promptFilling) return;
            var caret = input.CaretIndex;
            var lineStart = caret == 0 ? 0 : input.Text.LastIndexOf('\n', caret - 1) + 1;
            var token = input.Text.Substring(lineStart, caret - lineStart);
            if (Regex.IsMatch(token, @"^/[\w-]*$")) OpenPrompts(lineStart, token.Substring(1));
            else if (PromptsOpen && promptTokenStart >= 0) ClosePrompts();
        }

        void OpenPrompts(int tokenStart, string query)
        {
            var first = !PromptsOpen;
            promptTokenStart = tokenStart;
            if (first) { promptCategory = null; LoadPrompts(); }
            FillPrompts(query);
            if (promptList.Items.OfType<ListBoxItem>().Any(i => i.Tag is PromptItem) || prompts.Count == 0 && first)
            {
                promptChoose.Visibility = Visibility.Visible; promptForm.Visibility = Visibility.Collapsed;
                promptPanel.Visibility = Visibility.Visible;
            }
            else ClosePrompts();                                   // nothing matches: "/usr" stays normal text, Enter sends it
        }

        void ClosePrompts()
        {
            promptPanel.Visibility = Visibility.Collapsed;
            promptFilling = false;
        }

        // the list from TcAgent.exe prompts --json - in the background, the list shown so far stays until it is there
        void LoadPrompts()
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) return;
            var sln = TcUnitControl.SolutionPath();
            System.Threading.Tasks.Task.Run(() =>
            {
                var list = new List<PromptItem>();
                try
                {
                    var psi = new ProcessStartInfo(exe, "prompts --json" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardInput.Close();
                        var err = p.StandardError.ReadToEndAsync();
                        var json = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(15000);
                        var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                        foreach (var d in (root?["prompts"] as System.Collections.IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>())
                            list.Add(new PromptItem
                            {
                                Name = d["name"] as string, Description = d["description"] as string, Category = d["category"] as string, Level = d["level"] as string,
                                Source = d["source"] as string, Body = d["body"] as string, Send = d.TryGetValue("send", out var snd) && snd is bool sb && sb,
                                Tags = (d["tags"] as System.Collections.IEnumerable)?.Cast<object>().Select(x => x.ToString()).ToList() ?? new List<string>()
                            });
                    }
                }
                catch { return; }                                  // an older TcAgent.exe without prompts: no list
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    prompts = list;
                    if (PromptsOpen || promptTokenStart >= 0) { if (!promptFilling) PromptTextChanged(); if (promptTokenStart < 0 && PromptsOpen) FillPrompts(""); }
                }));
            });
        }

        void FillPrompts(string query)
        {
            var keep = (promptList.SelectedItem as ListBoxItem)?.Tag as PromptItem;
            promptCategories.Children.Clear();
            promptCategories.Children.Add(CategoryChip("All", null));
            foreach (var c in prompts.Select(p => p.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c)) promptCategories.Children.Add(CategoryChip(c, c));
            var q = (query ?? "").Trim();
            bool Match(PromptItem p) =>
                (promptCategory == null || string.Equals(p.Category, promptCategory, StringComparison.OrdinalIgnoreCase)) &&
                (q.Length == 0 || $"{p.Name} {p.Description} {string.Join(" ", p.Tags)}".IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var shown = prompts.Where(Match).ToList();
            var recent = RecentPrompts().Select(n => shown.FirstOrDefault(p => p.Name == n)).Where(p => p != null).ToList();
            var rest = shown.Except(recent).OrderBy(p => p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Name).ToList();
            promptList.Items.Clear();
            if (recent.Count > 0) promptList.Items.Add(PromptHeader("Recently used"));
            foreach (var p in recent) promptList.Items.Add(PromptRow(p));
            if (recent.Count > 0 && rest.Count > 0 || q.Length > 0 && rest.Count > 0) promptList.Items.Add(PromptHeader(q.Length > 0 ? $"Matching \"{q}\"" : "All prompts"));
            foreach (var p in rest) promptList.Items.Add(PromptRow(p));
            var rows = promptList.Items.OfType<ListBoxItem>().Where(i => i.Tag is PromptItem).ToList();
            promptList.SelectedItem = rows.FirstOrDefault(i => i.Tag == keep) ?? rows.FirstOrDefault();
            ShowPromptPreview();
        }

        UIElement CategoryChip(string text, string category)
        {
            var on = string.Equals(promptCategory, category, StringComparison.OrdinalIgnoreCase);
            var t = new TextBlock { Text = text, FontSize = 11 };
            if (on) t.Foreground = Brushes.White; else t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(0, 0, 4, 2), Background = on ? AccentBrush : AgentBubble, Child = t, Cursor = Cursors.Hand };
            b.MouseLeftButtonUp += (s, e) =>
            {
                promptCategory = category;
                FillPrompts(promptTokenStart >= 0 && input.CaretIndex > promptTokenStart ? input.Text.Substring(promptTokenStart + 1, input.CaretIndex - promptTokenStart - 1) : "");
                input.Focus();
            };
            return b;
        }

        static ListBoxItem PromptHeader(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Opacity = 0.65, Margin = new Thickness(2, 3, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return new ListBoxItem { Content = t, IsEnabled = false, Focusable = false };
        }

        static ListBoxItem PromptRow(PromptItem p)
        {
            var name = new TextBlock { Text = "/" + p.Name, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 8, 0) };
            var level = new TextBlock { Text = p.Level, FontSize = 11, Opacity = 0.65, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var desc = new TextBlock { Text = p.Description ?? "", Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
            foreach (var t in new[] { name, level, desc }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var row = new DockPanel();
            DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(level, Dock.Right);
            row.Children.Add(name); row.Children.Add(level); row.Children.Add(desc);
            return new ListBoxItem { Content = row, Tag = p, HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false, ToolTip = p.Description };
        }

        PromptItem SelectedPrompt => (promptList.SelectedItem as ListBoxItem)?.Tag as PromptItem;

        // what the placeholders stand for right now; a value that is not there stays visible as {selection}
        Dictionary<string, string> PromptValues()
        {
            var v = new Dictionary<string, string>();
            var ctx = EditorContext() ?? "";
            var obj = Regex.Match(ctx, @"^Open object:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            if (obj.Length > 0 && obj != "?") v["object"] = obj;
            var sel = Regex.Match(ctx, @"\nSelected:\n(.*?)(\nCode around the cursor|\n\(This context)", RegexOptions.Singleline);
            if (sel.Success) v["selection"] = sel.Groups[1].Value.Trim();
            var sln = TcUnitControl.SolutionPath();
            if (!string.IsNullOrEmpty(sln)) v["solution"] = Path.GetFileNameWithoutExtension(sln);
            return v;
        }

        void ShowPromptPreview()
        {
            promptPreview.Inlines.Clear();
            var p = SelectedPrompt;
            if (p == null) { promptOrigin.Text = ""; return; }
            promptOrigin.Text = "Preview · " + p.Level + (string.IsNullOrEmpty(p.Source) ? "" : " · from " + p.Source);
            var values = PromptValues();
            int i = 0;
            foreach (Match m in Placeholders.Matches(p.Body))
            {
                if (m.Index > i) promptPreview.Inlines.Add(new Run(p.Body.Substring(i, m.Index - i)));
                var known = values.TryGetValue(m.Groups[1].Value, out var v);
                promptPreview.Inlines.Add(new Run(known ? v : m.Value) { Foreground = known ? OkBrush : WarnBrush });
                i = m.Index + m.Length;
            }
            if (i < p.Body.Length) promptPreview.Inlines.Add(new Run(p.Body.Substring(i)));
        }

        // Up/Down choose, Enter inserts, Ctrl+Enter inserts and sends, Esc closes; true if the key was handled
        bool PromptKey(KeyEventArgs e)
        {
            if (!PromptsOpen || promptFilling) return false;
            var rows = promptList.Items.OfType<ListBoxItem>().Where(i => i.Tag is PromptItem).ToList();
            if (e.Key == Key.Escape) { ClosePrompts(); return true; }
            if (rows.Count == 0) return false;
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                var at = rows.IndexOf(promptList.SelectedItem as ListBoxItem) + (e.Key == Key.Down ? 1 : -1);
                promptList.SelectedItem = rows[Math.Max(0, Math.Min(rows.Count - 1, at))];
                promptList.ScrollIntoView(promptList.SelectedItem);
                return true;
            }
            if ((e.Key == Key.Enter || e.Key == Key.Tab) && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && SelectedPrompt is PromptItem p)
            {
                ChoosePrompt(p, e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                return true;
            }
            return false;
        }

        void ChoosePrompt(PromptItem p, bool send)
        {
            send |= p.Send;
            var questions = InputPlaceholder.Matches(p.Body).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            if (questions.Count == 0) { InsertPrompt(p, new Dictionary<string, string>(), send); return; }
            // the values to ask for: a short form in the panel; Enter in the last field or Insert goes on, Esc cancels
            promptFilling = true;
            promptChoose.Visibility = Visibility.Collapsed; promptForm.Visibility = Visibility.Visible;
            promptForm.Children.Clear();
            var head = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
            head.Inlines.Add(new Bold(new Run("/" + p.Name)));
            head.Inlines.Add(new Run(questions.Count == 1 ? " needs one value" : $" needs {questions.Count} values"));
            head.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            promptForm.Children.Add(head);
            var boxes = new List<Control>();
            foreach (var q in questions)
            {
                // "Target|local|test PLC": a choice; "Timeout=30 s": a default
                var parts = q.Split('|').Select(x => x.Trim()).ToArray();
                var eq = parts.Length == 1 ? q.IndexOf('=') : -1;
                var l = new TextBlock { Text = eq > 0 ? q.Substring(0, eq).Trim() : parts[0], Margin = new Thickness(0, 2, 0, 1), TextWrapping = TextWrapping.Wrap };
                l.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                Control b = parts.Length > 1
                    ? new ComboBox { ItemsSource = parts.Skip(1).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 4), Tag = q }
                    : (Control)new TextBox { Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 4), Tag = q, Text = eq > 0 ? q.Substring(eq + 1).Trim() : "" };
                promptForm.Children.Add(l); promptForm.Children.Add(b); boxes.Add(b);
            }
            void Done()
            {
                var values = boxes.ToDictionary(b => (string)b.Tag, b => b is ComboBox c ? c.SelectedItem as string : ((TextBox)b).Text.Trim());
                promptFilling = false;
                InsertPrompt(p, values, send);
            }
            void Cancel() { promptFilling = false; ClosePrompts(); input.Focus(); }
            var ok = new Button { Content = "Insert", MinWidth = 70, Margin = new Thickness(0, 2, 6, 0), Background = AccentBrush, Foreground = Brushes.White };
            var no = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(0, 2, 0, 0) };
            ok.Click += (s, e) => Done();
            no.Click += (s, e) => Cancel();
            foreach (var b in boxes)
                b.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
                    else if (e.Key == Key.Enter) { e.Handled = true; if (b == boxes[boxes.Count - 1]) Done(); else boxes[boxes.IndexOf(b) + 1].Focus(); }
                };
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(ok); bar.Children.Add(no);
            promptForm.Children.Add(bar);
            promptPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => boxes[0].Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        void InsertPrompt(PromptItem p, Dictionary<string, string> answers, bool send)
        {
            var values = PromptValues();
            var text = Placeholders.Replace(p.Body, m =>
            {
                var key = m.Groups[1].Value;
                if (key.StartsWith("input:")) return answers.TryGetValue(key.Substring(6).Trim(), out var a) ? a : m.Value;
                return values.TryGetValue(key, out var v) ? v : m.Value;
            });
            // replaces "/te"; opened with the button: at the cursor
            var start = promptTokenStart >= 0 ? promptTokenStart : input.CaretIndex;
            var end = promptTokenStart >= 0 ? Math.Max(start, input.CaretIndex) : input.CaretIndex + input.SelectionLength;
            promptFilling = true;                                  // the change of the text is not typing
            input.Text = input.Text.Substring(0, start) + text + input.Text.Substring(Math.Min(end, input.Text.Length));
            input.CaretIndex = start + text.Length;
            promptFilling = false;
            ClosePrompts();
            RememberRecentPrompt(p.Name);
            input.Focus();
            // something still open ({selection} without a selection): the user decides, it is not sent like that
            if (send && Placeholders.IsMatch(text)) status.Text = "Not sent: the prompt still has an open placeholder - complete it, then Enter.";
            else if (send) Send();
        }

        List<string> RecentPrompts()
        {
            try { return File.Exists(RecentPromptsFile) ? new JavaScriptSerializer().Deserialize<List<string>>(File.ReadAllText(RecentPromptsFile)) ?? new List<string>() : new List<string>(); }
            catch { return new List<string>(); }
        }

        void RememberRecentPrompt(string name)
        {
            var list = RecentPrompts();
            list.Remove(name); list.Insert(0, name);
            try { Directory.CreateDirectory(Dir); File.WriteAllText(RecentPromptsFile, new JavaScriptSerializer().Serialize(list.Take(MaxRecentPrompts).ToList()), Encoding.UTF8); } catch { }
        }

        // ------------------------------------------------------------------ save an own message as a prompt
        void SaveAsPrompt(string text)
        {
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { status.Text = "TcAgent.exe not found."; return; }
            var sln = TcUnitControl.SolutionPath();
            var w = KnowledgeControl.Dialog("Save as prompt", 420);
            var sp = new StackPanel { Margin = new Thickness(14, 8, 14, 12) };
            var name = new TextBox { MinWidth = 400, Padding = new Thickness(2) };
            var desc = new TextBox { MinWidth = 400, Padding = new Thickness(2), Text = text.Split('\n')[0].Trim() };
            var level = new ComboBox { MinWidth = 400, ItemsSource = string.IsNullOrEmpty(sln) ? new[] { "user" } : new[] { "user", "project" }, SelectedIndex = 0 };
            foreach (var (label, el) in new (string, Control)[] { ("Name (used as /name)", name), ("Description", desc), ("Level", level) })
            { sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) }); sp.Children.Add(el); }
            var ok = new Button { Content = "Save", IsEnabled = false };
            name.TextChanged += (s, e) => ok.IsEnabled = name.Text.Trim().Length > 0;
            ok.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(KnowledgeControl.Buttons(w, ok));
            w.Content = sp;
            w.Loaded += (s, e) => name.Focus();
            if (w.ShowDialog() != true) return;
            var slug = Regex.Replace(name.Text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) { status.Text = "Save as prompt: use letters or digits in the name."; return; }
            var lvl = (string)level.SelectedItem;
            var description = desc.Text.Replace("\"", "'");
            System.Threading.Tasks.Task.Run(() =>
            {
                string said; bool done = false;
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "tcagent-prompt-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, slug + ".md");
                    File.WriteAllText(file, "---\ndescription: \"" + description + "\"\n---\n" + text + "\n", new UTF8Encoding(false));
                    var args = $"knowledge new --key \"prompts/{slug}\" --path \"{file}\" --level {lvl}" + (string.IsNullOrEmpty(sln) ? "" : $" --solution \"{sln}\"");
                    var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                                                                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardInput.Close();
                        var err = p.StandardError.ReadToEndAsync();
                        var o = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(30000);
                        done = p.ExitCode == 0;
                        said = done ? $"Saved as /{slug} ({lvl})." : (o + " " + err.Result).Trim();
                    }
                    try { Directory.Delete(dir, true); } catch { }
                }
                catch (Exception ex) { said = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { if (done) status.Text = said; else Info("Save as prompt failed", said, true); }));
            });
        }
    }
}
