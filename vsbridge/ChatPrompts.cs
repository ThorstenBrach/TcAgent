// TcAgent Chat: the prompt library (server: Prompts.cs). "/" at the start of a line of the input, or the button Prompts, opens a
// list above the input: typing filters by name, description and tags; arrow keys or a click choose, Enter puts the prompt into
// the input (never sent without the user: it can be adapted), Ctrl+Enter inserts and sends. A preview shows the text with the
// placeholders filled from the editor - {object}, {selection}, {solution}. The values of a prompt (Prompts.Param: {input:…} in the
// text or params: in its header - text, number, choice, bool, pou) are asked in a card. tcagent-send: true sends after the card.
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
            public string Name, Description, Category, Level, Source, Body, Path;
            public bool Send;
            public List<string> Tags = new List<string>();
            public List<PromptParam> Params = new List<PromptParam>();
        }

        sealed class PromptParam
        {
            public string Name, Ask, Type, Default, Help, Token;
            public bool Required;
            public List<string> Options = new List<string>();
        }

        const int MaxRecentPrompts = 5;
        static readonly Regex Editor = new Regex(@"\{(object|selection|solution)\}");
        static string RecentPromptsFile => Path.Combine(Dir, "prompts-recent.json");

        readonly Border promptPanel = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 6, 2), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
        readonly WrapPanel promptCategories = new WrapPanel { Margin = new Thickness(6, 5, 6, 3) };
        readonly ListBox promptList = new ListBox { BorderThickness = new Thickness(0), MaxHeight = 200, Background = Brushes.Transparent, Focusable = false };
        readonly TextBlock promptOrigin = new TextBlock { FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock promptPreview = new TextBlock { TextWrapping = TextWrapping.Wrap, Height = 84, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly StackPanel promptForm = new StackPanel { Margin = new Thickness(8, 6, 8, 8), Visibility = Visibility.Collapsed };
        readonly StackPanel promptChoose = new StackPanel();
        readonly Button promptsButton = new Button { Content = "Prompts", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Prepared prompts (or type / at the start of a line)" };
        List<PromptItem> prompts = new List<PromptItem>();
        string promptCategory;          // null = all
        int promptTokenStart = -1;      // where "/…" starts in the input; -1 = opened with the button, inserted at the cursor
        bool promptFilling;             // the card with the values is open
        List<string> promptObjects;     // the names for the type pou
        string promptObjectsOf;         // ... of this solution
        bool PromptsOpen => promptPanel.Visibility == Visibility.Visible;

        FrameworkElement BuildPromptPanel()
        {
            promptPanel.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            promptPanel.BorderBrush = AccentBrush;
            var hint = new TextBlock { Text = "↑↓ choose · Enter insert · Ctrl+Enter insert and send · Esc close", FontSize = 11, Opacity = 0.6, Margin = new Thickness(8, 3, 8, 3), TextWrapping = TextWrapping.Wrap };
            var previewText = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
            previewText.Children.Add(promptOrigin); previewText.Children.Add(promptPreview);
            var preview = new Border { Background = AgentBubble, Child = previewText };
            foreach (var t in new[] { hint, promptOrigin, promptPreview }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            // many categories: they wrap, from the fourth row on they scroll - the list stays where it is
            promptChoose.Children.Add(new ScrollViewer { Content = promptCategories, MaxHeight = 68, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
            promptChoose.Children.Add(promptList);
            promptChoose.Children.Add(Line()); promptChoose.Children.Add(preview);
            promptChoose.Children.Add(Line()); promptChoose.Children.Add(hint);
            var all = new StackPanel();
            all.Children.Add(promptChoose); all.Children.Add(promptForm);
            promptPanel.Child = all;
            Grid.SetIsSharedSizeScope(promptList, true);          // the descriptions start in one column
            ScrollViewer.SetHorizontalScrollBarVisibility(promptList, ScrollBarVisibility.Disabled);
            promptList.SelectionChanged += (s, e) => { MarkPromptRows(); ShowPromptPreview(); };
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
            if (first)
            {
                promptCategory = null; LoadPrompts();
                // one height while the list is open, the height of "All": changing the category must not move the chips under the mouse
                promptList.Height = Math.Min(promptList.MaxHeight, prompts.Count * 22 + 44);
            }
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

        // TcAgent.exe prompts ... - files only, stdin closed
        static string PromptsCli(string exe, string args, string sln)
        {
            var psi = new ProcessStartInfo(exe, args + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""))
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
                return json;
            }
        }

        static IEnumerable<Dictionary<string, object>> Dicts(object o) => (o as System.Collections.IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>();
        static List<string> Strings(object o) => (o as System.Collections.IEnumerable)?.Cast<object>().Select(x => x.ToString()).ToList() ?? new List<string>();

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
                    var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(PromptsCli(exe, "prompts --json", sln)) as Dictionary<string, object>;
                    foreach (var d in Dicts(root?["prompts"]))
                        list.Add(new PromptItem
                        {
                            Name = d["name"] as string, Description = d["description"] as string, Category = d["category"] as string, Level = d["level"] as string,
                            Source = d["source"] as string, Body = d["body"] as string, Path = d.TryGetValue("path", out var pp) ? pp as string : null, Send = d.TryGetValue("send", out var snd) && snd is bool sb && sb, Tags = Strings(d["tags"]),
                            Params = Dicts(d.TryGetValue("params", out var ps) ? ps : null).Select(x => new PromptParam
                            {
                                Name = x["name"] as string, Ask = x["ask"] as string, Type = x["type"] as string, Default = x["default"] as string, Help = x["help"] as string,
                                Token = x["token"] as string, Required = x["required"] is bool rq && rq, Options = Strings(x["options"])
                            }).ToList()
                        });
                }
                catch { return; }                                  // an older TcAgent.exe without prompts: no list
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    prompts = list;
                    // the list came after the panel opened (the first time there was nothing yet): the height of "All" now
                    if (PromptsOpen) promptList.Height = Math.Min(promptList.MaxHeight, prompts.Count * 22 + 44);
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
            foreach (var r in rows) r.ContextMenu = PromptMenu(r);
            promptList.SelectedItem = rows.FirstOrDefault(i => i.Tag == keep) ?? rows.FirstOrDefault();
            ShowPromptPreview();
        }

        // right click on a prompt. Your own file opens as it is; the others (built-in, company, team) belong to their level: you edit
        // a copy on your level, which replaces the prompt of the same name for you
        static bool OwnPrompt(PromptItem p) => p.Level == "user" || p.Level == "project";

        ContextMenu PromptMenu(ListBoxItem row)
        {
            var p = (PromptItem)row.Tag;
            var edit = new MenuItem { Header = OwnPrompt(p) ? "Edit" : "Edit my copy",
                                      ToolTip = OwnPrompt(p) ? p.Path : $"The {p.Level} prompt stays as it is: a copy in your folder replaces it for you" };
            edit.Click += (s, e) => EditPrompt(p);
            var menu = new ContextMenu();
            menu.Items.Add(edit);
            menu.Opened += (s, e) => promptList.SelectedItem = row;
            return menu;
        }

        void EditPrompt(PromptItem p)
        {
            ClosePrompts();
            if (string.IsNullOrEmpty(p.Path)) { status.Text = "This TcAgent.exe does not name the file of the prompt - update it first."; return; }
            void Open(string file)
            {
                try { (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ItemOperations.OpenFile(file); status.Text = "Opened: " + file; }
                catch (Exception ex) { Info("Edit prompt", ex.Message, true); }
            }
            if (OwnPrompt(p)) { Open(p.Path); return; }
            var mine = Path.Combine(Dir, "prompts", p.Name + ".md");
            if (File.Exists(mine)) { Open(mine); return; }
            var exe = TcUnitControl.TcAgentExe();
            var sln = TcUnitControl.SolutionPath();
            System.Threading.Tasks.Task.Run(() =>
            {
                string said = null;
                try
                {
                    var args = $"knowledge new --key \"prompts/{p.Name}\" --path \"{p.Path}\" --level user" + (string.IsNullOrEmpty(sln) ? "" : $" --solution \"{sln}\"");
                    var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                                                                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                    using (var pr = Process.Start(psi))
                    {
                        pr.StandardInput.Close();
                        var err = pr.StandardError.ReadToEndAsync();
                        var o = pr.StandardOutput.ReadToEnd();
                        pr.WaitForExit(30000);
                        if (pr.ExitCode != 0 || !File.Exists(mine)) said = (o + " " + err.Result).Trim();
                    }
                }
                catch (Exception ex) { said = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { if (said == null) Open(mine); else Info("Edit prompt", said, true); }));
            });
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

        // a row without the theme's pale selection: the selected row is filled, so it stands out while the focus stays in the input
        static readonly ControlTemplate PromptRowTemplate = MakePromptRowTemplate();

        static ControlTemplate MakePromptRowTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        }

        static ListBoxItem PromptRow(PromptItem p)
        {
            var name = new TextBlock { Text = "/" + p.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            var desc = new TextBlock { Text = p.Description ?? "", TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "PromptName" });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(desc, 1);
            row.Children.Add(name); row.Children.Add(desc);
            // the level only where it says something: built-in is the normal case, the preview names it anyway
            if (p.Level != "built-in")
            {
                var level = new TextBlock { Text = p.Level, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(level, 2);
                row.Children.Add(level);
            }
            var item = new ListBoxItem { Content = row, Tag = p, Template = PromptRowTemplate, Padding = new Thickness(8, 3, 8, 3), Focusable = false, ToolTip = p.Description, Background = Brushes.Transparent };
            TintPromptRow(item, false);
            return item;
        }

        void MarkPromptRows()
        {
            foreach (var i in promptList.Items.OfType<ListBoxItem>().Where(x => x.Tag is PromptItem)) TintPromptRow(i, i == promptList.SelectedItem);
        }

        static void TintPromptRow(ListBoxItem item, bool selected)
        {
            item.Background = selected ? AccentBrush : Brushes.Transparent;
            foreach (var t in ((Grid)item.Content).Children.OfType<TextBlock>())
            {
                if (selected) t.Foreground = Brushes.White; else t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                t.Opacity = selected || Grid.GetColumn(t) == 0 ? 1 : 0.8;
            }
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

        // a default may hold {object} / {selection}
        static string Expand(string text, Dictionary<string, string> values) =>
            string.IsNullOrEmpty(text) ? text : Editor.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        void ShowPromptPreview()
        {
            promptPreview.Inlines.Clear();
            var p = SelectedPrompt;
            if (p == null) { promptOrigin.Text = ""; return; }
            promptOrigin.Text = "Preview · " + p.Level + (string.IsNullOrEmpty(p.Source) ? "" : " · from " + p.Source);
            var values = PromptValues();
            var tokens = string.Join("|", p.Params.Select(x => Regex.Escape(x.Token)).Concat(new[] { Editor.ToString() }));
            int i = 0;
            foreach (Match m in Regex.Matches(p.Body, tokens))
            {
                if (m.Index > i) promptPreview.Inlines.Add(new Run(p.Body.Substring(i, m.Index - i)));
                var param = p.Params.FirstOrDefault(x => x.Token == m.Value);
                var value = param != null ? Expand(param.Default, values) : Expand(m.Value, values);
                var known = !string.IsNullOrEmpty(value) && !Editor.IsMatch(value);
                promptPreview.Inlines.Add(new Run(known ? value : m.Value) { Foreground = known ? AccentBrush : WarnBrush, FontWeight = FontWeights.SemiBold });
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
            if (p.Params.Count == 0) { InsertPrompt(p, new Dictionary<string, string>(), send); return; }
            // the card: one field per value; Enter goes on, Insert completes, Esc cancels
            promptFilling = true;
            promptChoose.Visibility = Visibility.Collapsed; promptForm.Visibility = Visibility.Visible;
            promptForm.Children.Clear();
            var head = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
            head.Inlines.Add(new Bold(new Run("/" + p.Name)));
            head.Inlines.Add(new Run(p.Params.Count == 1 ? " needs one value" : $" needs {p.Params.Count} values"));
            head.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            promptForm.Children.Add(head);
            var values = PromptValues();
            var ok = new Button { Content = send ? "Send" : "Insert", MinWidth = 70, Margin = new Thickness(0, 2, 6, 0), Background = AccentBrush, Foreground = Brushes.White };
            var no = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(0, 2, 0, 0) };
            var fields = new List<(PromptParam P, Control C, Func<string> Get)>();
            bool Valid() => fields.All(f => (!f.P.Required || f.Get().Length > 0) &&
                                            (f.P.Type != "number" || f.Get().Length == 0 || double.TryParse(f.Get().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)));
            void Check() => ok.IsEnabled = Valid();
            foreach (var q in p.Params)
            {
                var value = Expand(q.Default, values);
                if (value != null && Editor.IsMatch(value)) value = "";          // {selection} without a selection: empty, the user fills it
                if (q.Type != "bool")
                {
                    var l = new TextBlock { Text = q.Ask + (q.Required ? " *" : ""), Margin = new Thickness(0, 2, 0, 1), TextWrapping = TextWrapping.Wrap };
                    l.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    promptForm.Children.Add(l);
                }
                Control c; Func<string> get;
                if (q.Type == "choice")
                {
                    var b = new ComboBox { ItemsSource = q.Options, SelectedItem = q.Options.Contains(value ?? "") ? value : q.Options.FirstOrDefault(), Margin = new Thickness(0, 0, 0, 4) };
                    c = b; get = () => b.SelectedItem as string ?? "";
                }
                else if (q.Type == "bool")
                {
                    var b = new CheckBox { Content = q.Ask, IsChecked = (value ?? "").Equals("true", StringComparison.OrdinalIgnoreCase), Margin = new Thickness(0, 3, 0, 5) };
                    b.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    c = b; get = () => b.IsChecked == true ? "true" : "false";
                }
                else
                {
                    var b = new TextBox { Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 4), Text = value ?? "" };
                    b.TextChanged += (s, e) => Check();
                    c = b; get = () => b.Text.Trim();
                }
                promptForm.Children.Add(c);
                if (q.Type == "pou") promptForm.Children.Add(ObjectList((TextBox)c));
                if (!string.IsNullOrEmpty(q.Help))
                {
                    var h = new TextBlock { Text = q.Help, FontSize = 11, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
                    h.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    promptForm.Children.Add(h);
                }
                fields.Add((q, c, get));
            }
            void Done()
            {
                if (!Valid()) return;
                var answers = fields.ToDictionary(f => f.P.Token, f => f.Get());
                promptFilling = false;
                InsertPrompt(p, answers, send);
            }
            void Cancel() { promptFilling = false; ClosePrompts(); input.Focus(); }
            ok.Click += (s, e) => Done();
            no.Click += (s, e) => Cancel();
            foreach (var f in fields)
            {
                var c = f.C;
                c.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
                    else if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        var at = fields.FindIndex(x => x.C == c);
                        if (at == fields.Count - 1) Done(); else fields[at + 1].C.Focus();
                    }
                };
            }
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(ok); bar.Children.Add(no);
            promptForm.Children.Add(bar);
            Check();
            promptPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => fields[0].C.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        // the type pou: the names of POUs, GVLs and tasks below the field, filtered by what is typed; a click takes the name
        ListBox ObjectList(TextBox box)
        {
            var list = new ListBox { MaxHeight = 96, Margin = new Thickness(0, 0, 0, 4), Focusable = false };
            list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            void Fill()
            {
                var q = box.Text.Trim();
                list.ItemsSource = (promptObjects ?? new List<string>()).Where(o => q.Length == 0 || o.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(100).ToList();
            }
            box.TextChanged += (s, e) => Fill();
            list.SelectionChanged += (s, e) => { if (list.SelectedItem is string n && n != box.Text) { box.Text = n; box.CaretIndex = n.Length; } };
            var sln = TcUnitControl.SolutionPath();
            var exe = TcUnitControl.TcAgentExe();
            if (promptObjects != null && promptObjectsOf == sln) { Fill(); return list; }
            if (exe != null)
                System.Threading.Tasks.Task.Run(() =>
                {
                    var names = new List<string>();
                    try { names = Dicts(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(PromptsCli(exe, "prompts objects", sln))).Select(d => d["name"] as string).ToList(); } catch { }
                    Dispatcher.BeginInvoke(new Action(() => { promptObjects = names; promptObjectsOf = sln; Fill(); }));
                });
            return list;
        }

        void InsertPrompt(PromptItem p, Dictionary<string, string> answers, bool send)
        {
            var text = p.Body;
            foreach (var a in answers) text = text.Replace(a.Key, a.Value);
            text = Expand(text, PromptValues());
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
            if (send && Editor.IsMatch(text)) status.Text = "Not sent: the prompt still has an open placeholder - complete it, then Enter.";
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
