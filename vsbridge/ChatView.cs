// TcAgent Chat - the view: an info bar (one line, details on request), the start with suggestions, message bubbles (agent
// left, user right) with tool calls as compact lines (✓/✗, result, time, diff), a small Markdown renderer, the input with
// a placeholder, chips for the editor context and the attachments (paste, drag & drop, "Attach …") and prompt history (Up/Down).
// The backend logic (sessions, CLIs, prompts) is in ChatWindow.cs.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    sealed class Attachment
    {
        public string Name, Path, Kind;   // Kind: file, image, text
        public override string ToString() => Name;
    }

    public sealed partial class ChatControl
    {
        // properties, not field initializers: the order of static initializers across partial files is undefined
        static string AttachDir => System.IO.Path.Combine(Dir, "attachments");
        static string InputHistoryFile => System.IO.Path.Combine(Dir, "chat-input-history.json");
        const int MaxMessages = 300, MaxInputHistory = 500, InlineTextLimit = 150_000;

        // theme-neutral colors: translucent, so they work on the light and the dark theme
        static readonly Brush UserBubble = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x78, 0xD4)));
        static readonly Brush AgentBubble = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)));
        static readonly Brush CodeBack = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80)));
        static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0x80, 0x80)));
        static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)));
        static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
        static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x1A)));
        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        readonly StackPanel msgs = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        readonly ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly WrapPanel chips = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
        readonly Button attach = new Button { Content = "📎 Attach …", Padding = new Thickness(6, 1, 6, 1), ToolTip = "Attach documents for the agent - or paste (Ctrl+V) / drop files and screenshots into the input" };
        readonly List<Attachment> attachments = new List<Attachment>();
        readonly List<string> inputHistory = new List<string>();
        int historyPos = -1;      // -1 = editing a new prompt
        string draft = "";

        // ------------------------------------------------------------------ layout
        // the info bar below the top bar: one line (e.g. the result of "Set up clients"), the details only on request
        readonly Border infoBar = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 4, 3), Margin = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed,
                                               Background = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0xB0, 0x1A))), BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0xB0, 0x1A))) };
        readonly TextBlock infoText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBox infoDetails = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                                                     FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
                                                     MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly TextBlock infoLinks = new TextBlock { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 6, 0) };
        // the start: what to ask - clickable suggestions instead of a welcome text
        readonly StackPanel emptyState = new StackPanel { Margin = new Thickness(14, 18, 14, 8) };
        // the editor context that goes with the next question (removable chip above the input)
        readonly Border contextChip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 2, 1), Margin = new Thickness(0, 0, 4, 2), Visibility = Visibility.Collapsed };
        readonly TextBlock contextChipText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        bool contextOff;           // ✕ on the chip: the next question goes without the editor context
        readonly TextBlock placeholder = new TextBlock { Opacity = 0.5, Margin = new Thickness(7, 5, 4, 0), IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };

        static readonly string[] Suggestions =
        {
            "Explain the open object",
            "Check the project for compiler errors",
            "Write a TcUnit test for the selected function block",
            "Which coding rules apply here?",
            "Run the TcUnit tests and summarize the result"
        };

        FrameworkElement BuildView()
        {
            // top bar: backend (short name and a status dot) | (version only if something is wrong) New chat, ⋯
            var top = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
            var menuButton = new Button { Content = "⋯", Width = 28, Padding = new Thickness(0, 0, 0, 2), Margin = new Thickness(6, 0, 0, 0), FontSize = 14,
                                          ToolTip = "Chat options, the TcAgent windows, set up clients and help" };
            menuButton.Click += (s, e) => ShowMenu(menuButton);
            DockPanel.SetDock(menuButton, Dock.Right);
            top.Children.Add(menuButton);
            DockPanel.SetDock(clear, Dock.Right);
            top.Children.Add(clear);
            DockPanel.SetDock(versionLabel, Dock.Right);
            top.Children.Add(versionLabel);
            UpdateVersionLabel();
            backendBox.HorizontalAlignment = HorizontalAlignment.Left;
            backendBox.MinWidth = 150;
            top.Children.Add(backendBox);

            // info bar: text, links (Details, ✕); the details below, left-aligned
            var ib = new DockPanel();
            DockPanel.SetDock(infoLinks, Dock.Right); ib.Children.Add(infoLinks);
            var ibText = new StackPanel();
            ibText.Children.Add(infoText); ibText.Children.Add(infoDetails);
            ib.Children.Add(ibText);
            infoBar.Child = ib;

            scroller.Content = msgs;
            scroller.SizeChanged += (s, e) => UpdateBubbleWidths();
            BuildEmptyState();
            var area = new Grid();
            area.Children.Add(scroller); area.Children.Add(emptyState);

            // input in a rounded frame: chips (editor context, attachments), the text with a placeholder, the buttons; the keys below
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(stop); right.Children.Add(send);
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(attach, Dock.Left);
            row.Children.Add(right); row.Children.Add(attach);
            row.Children.Add(new FrameworkElement());
            chips.Children.Add(contextChip);
            BuildContextChip();
            var field = new Grid();
            field.Children.Add(input); field.Children.Add(placeholder);
            input.TextChanged += (s, e) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var box = new DockPanel();
            DockPanel.SetDock(chips, Dock.Top); DockPanel.SetDock(row, Dock.Bottom);
            box.Children.Add(chips); box.Children.Add(row); box.Children.Add(field);
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(6, 2, 6, 0), Child = box };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            // the keys in full, wrapped when the window is narrow (never cut off)
            var keys = new TextBlock { Text = "Enter send · Shift+Enter new line · ↑/↓ earlier prompts · Esc stop (while working) or back to the editor · Ctrl+V pastes files and screenshots",
                                       Opacity = 0.55, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 8, 0) };
            keys.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var bottom = new DockPanel();
            DockPanel.SetDock(status, Dock.Bottom);
            status.Margin = new Thickness(8, 2, 8, 4);
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            bottom.Children.Add(status);
            DockPanel.SetDock(keys, Dock.Bottom); bottom.Children.Add(keys);
            bottom.Children.Add(frame);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(infoBar, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(infoBar); root.Children.Add(bottom); root.Children.Add(area);

            input.BorderThickness = new Thickness(0);
            input.Padding = new Thickness(4);
            input.FontFamily = new FontFamily("Segoe UI");
            input.FontSize = 13;
            input.AllowDrop = true;
            input.PreviewDragOver += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
            input.PreviewDrop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) AddFile(f); e.Handled = true; }
            };
            input.GotKeyboardFocus += (s, e) => UpdateContextChip();
            attach.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach documents for the agent" };
                if (dlg.ShowDialog() == true) foreach (var f in dlg.FileNames) AddFile(f);
            };
            send.Content = "Send";
            stop.Content = "Stop";
            send.ToolTip = "Send (Enter)";
            stop.ToolTip = "Stop the answer (Esc)";
            foreach (var t in new[] { infoText, contextChipText, placeholder }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            infoDetails.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            LoadInputHistory();
            return root;
        }

        void BuildEmptyState()
        {
            var title = new TextBlock { Text = "Ask about this TwinCAT solution", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
            var sub = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 10),
                                      Text = "The agent works directly in this solution through the TcAgent tools. Paste or drop manuals, data sheets and screenshots into the input to attach them." };
            foreach (var t in new[] { title, sub }) t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            emptyState.Children.Add(title); emptyState.Children.Add(sub);
            foreach (var s in Suggestions)
            {
                var text = s;
                var b = new Button { Content = new TextBlock { Text = "› " + s, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left,
                                     Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 6), Background = AgentBubble, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                                     ToolTip = "Puts the question into the input - edit it if you like, then Enter" };
                ((TextBlock)b.Content).SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                b.Click += (o, e) => { input.Text = text; FocusInput(); };
                emptyState.Children.Add(b);
            }
            if (!File.Exists(SettingsFile) && Installer.IsVsixInstall)
            {
                var setupLink = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
                setupLink.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                setupLink.Inlines.Add(new Run("The AI clients are not set up yet: "));
                var h = new Hyperlink(new Run("Set up clients"));
                h.Click += (o, e) => setup.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                setupLink.Inlines.Add(h);
                emptyState.Children.Add(setupLink);
            }
        }

        void ShowEmptyState() => emptyState.Visibility = msgs.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ------------------------------------------------------------------ the editor context chip
        void BuildContextChip()
        {
            var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                                     Cursor = Cursors.Hand, ToolTip = "Send the next question without the editor context" };
            close.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            close.Click += (s, e) => { contextOff = true; UpdateContextChip(); input.Focus(); };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(contextChipText); sp.Children.Add(close);
            contextChip.Child = sp;
            contextChip.Background = AgentBubble;
        }

        // what goes with the next question: the open object and the cursor line - shown before it is sent
        void UpdateContextChip()
        {
            string label = null;
            if (contextBox.IsChecked == true && !contextOff)
                try { label = ContextLabel(EditorContext()); } catch { }
            contextChipText.Text = label ?? "";
            contextChip.ToolTip = "Goes with the next question: the open object, the cursor line and a selection";
            contextChip.Visibility = label != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ info bar
        /// <summary>One line in the info bar; details (left-aligned, selectable) only on "Details".</summary>
        void Info(string text, string details = null, bool warn = false)
        {
            infoText.Text = (warn ? "⚠ " : "") + text;
            infoDetails.Text = details ?? "";
            infoDetails.Visibility = Visibility.Collapsed;
            infoLinks.Inlines.Clear();
            if (!string.IsNullOrEmpty(details))
            {
                var d = new Hyperlink(new Run("Details"));
                d.Click += (s, e) => { var open = infoDetails.Visibility != Visibility.Visible; infoDetails.Visibility = open ? Visibility.Visible : Visibility.Collapsed; ((Run)d.Inlines.FirstInline).Text = open ? "Hide" : "Details"; };
                infoLinks.Inlines.Add(d);
                infoLinks.Inlines.Add(new Run("   "));
                var c = new Hyperlink(new Run("Copy"));
                c.Click += (s, e) => { try { Clipboard.SetText(text + "\n" + details); status.Text = "Copied."; } catch { } };
                infoLinks.Inlines.Add(c);
                infoLinks.Inlines.Add(new Run("   "));
            }
            var x = new Hyperlink(new Run("✕")) { TextDecorations = null, ToolTip = "Close" };
            x.Click += (s, e) => infoBar.Visibility = Visibility.Collapsed;
            infoLinks.Inlines.Add(x);
            infoBar.Visibility = Visibility.Visible;
        }

        // user folders as variables: shorter, and no user name on the screen
        static string ShortPaths(string s)
        {
            foreach (var (folder, name) in new[] { (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"), (Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
                                                   (Environment.SpecialFolder.UserProfile, "%USERPROFILE%") })
            {
                var p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p)) s = Regex.Replace(s, Regex.Escape(p), name.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            return s;
        }

        // menu of the "..." button, grouped: Chat / Windows / Setup and help - and the version
        void ShowMenu(FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            MenuItem Item(string header, Action click, string tip = null, bool? check = null)
            {
                var mi = new MenuItem { Header = header, ToolTip = tip };
                if (check != null) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
                mi.Click += (s, e) => click();
                return mi;
            }
            MenuItem Group(string header) => new MenuItem { Header = header, IsEnabled = false, FontWeight = FontWeights.SemiBold };
            menu.Items.Add(Group("Chat"));
            menu.Items.Add(Item("Send editor context", () => { contextBox.IsChecked = contextBox.IsChecked != true; contextOff = false; UpdateContextChip(); },
                                "Send the open object, cursor line and selection with each request", contextBox.IsChecked == true));
            menu.Items.Add(Item("Check after changes", () => buildBox.IsChecked = buildBox.IsChecked != true,
                                "The agent checks for compiler errors (tc_check) once after its changes", buildBox.IsChecked == true));
            menu.Items.Add(Item("New chat", () => clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), clear.ToolTip as string));
            menu.Items.Add(Item("Copy conversation", CopyConversation, "The questions and answers of this chat as text"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Group("Windows"));
            menu.Items.Add(Item("TcUnit Tests", () => tests.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), tests.ToolTip as string));
            menu.Items.Add(Item("Knowledge", () => { try { BridgePackage.Instance?.ShowKnowledge(true); } catch { } },
                                "TcAgent Knowledge window: what the agent gets (rules, skills, workflows, lessons) and how the workflows develop"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Group("Setup and help"));
            if (Installer.IsVsixInstall) menu.Items.Add(Item("Set up clients", () => setup.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), setup.ToolTip as string));
            menu.Items.Add(Item("Copy diagnostics", () => KnowledgeControl.CopyDiagnostics(s => status.Text = s),
                                "Versions of TcAgent, the extension, TwinCAT and the IDEs for the Diagnostics field of an issue"));
            menu.Items.Add(Item("Open user guide", () => { try { System.Diagnostics.Process.Start(UserGuideUrl); } catch (Exception ex) { status.Text = "Could not open the user guide: " + ex.Message; } },
                                "The user guide of TcAgent in the browser"));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = versionLabel.Text?.Length > 0 && versionLabel.Visibility == Visibility.Visible ? versionLabel.Text
                                                    : $"TcAgent {BridgePackage.BridgeVersion}" + (Installer.RegisteredServerVersion() is string srv && srv != BridgePackage.BridgeVersion ? $" (server {srv})" : ""),
                                          IsEnabled = false });
            menu.IsOpen = true;
        }

        const string UserGuideUrl = "https://github.com/ThorstenBrach/TcAgent/blob/main/docs/USER_GUIDE.md#what-the-vs-extension-tcagentbridge-adds";

        void CopyConversation()
        {
            var sb = new StringBuilder();
            foreach (var kv in history) sb.Append("You: ").AppendLine(kv.Key).AppendLine().Append("Agent: ").AppendLine(kv.Value).AppendLine();
            try { Clipboard.SetText(sb.Length > 0 ? sb.ToString() : "(empty)"); status.Text = history.Count > 0 ? $"Copied: {history.Count} question(s) and answer(s)." : "Nothing to copy yet."; }
            catch (Exception ex) { status.Text = "Could not copy: " + ex.Message; }
        }

        void ThemeView()
        {
            foreach (var c in new Control[] { input, attach })
            {
                c.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                c.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            }
            input.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }

        double BubbleMax => Math.Max(200, scroller.ActualWidth - 60);

        void UpdateBubbleWidths()
        {
            foreach (var b in msgs.Children.OfType<Border>()) if (b.Tag as string == "user") b.MaxWidth = BubbleMax;
        }

        void AddMessage(UIElement e)
        {
            msgs.Children.Add(e);
            while (msgs.Children.Count > MaxMessages) msgs.Children.RemoveAt(0);
            ShowEmptyState();
            ScrollDown();
        }

        void ScrollDown()
        {
            // only follow the output if the user is at the end (does not jump while reading further up)
            if (scroller.VerticalOffset >= scroller.ScrollableHeight - 40) scroller.ScrollToEnd();
        }

        static TextBox Selectable(string text, double size = 13, double opacity = 1) => new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"), FontSize = size, Opacity = opacity, Padding = new Thickness(0)
        };

        // ------------------------------------------------------------------ messages
        void AddUserBubble(string text, string ctx, IList<Attachment> att, bool fromCode)
        {
            var panel = new StackPanel();
            var t = Selectable(text);
            t.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            panel.Children.Add(t);
            if (att != null && att.Count > 0)
                panel.Children.Add(Selectable("📎 " + string.Join(", ", att.Select(a => a.Name)), 11, 0.75));
            var where = ContextLabel(ctx);
            if (where != null || fromCode)
                panel.Children.Add(Selectable((fromCode ? "from code" + (where != null ? " · " : "") : "") + (where ?? ""), 11, 0.6));
            foreach (var tb in panel.Children.OfType<TextBox>()) tb.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AddMessage(new Border
            {
                Tag = "user", HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = BubbleMax, Margin = new Thickness(40, 6, 0, 6),
                Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(10, 10, 2, 10), Background = UserBubble, Child = panel
            });
            scroller.ScrollToEnd();
        }

        // "Open object: MAIN\nCursor: impl, line 12, column 3" -> "📄 MAIN · line 12"; nothing if no editor was open
        static string ContextLabel(string ctx)
        {
            if (string.IsNullOrEmpty(ctx)) return null;
            var obj = Regex.Match(ctx, @"^Open object:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var line = Regex.Match(ctx, @"^Cursor:.*?line (\d+)", RegexOptions.Multiline).Groups[1].Value;
            var sel = ctx.Contains("\nSelected:\n");
            if (obj.Length == 0 || obj == "?") return null;
            return "📄 " + obj + (line.Length > 0 ? " · line " + line : "") + (sel ? " · selection" : "");
        }

        // an event of the chat (stopped, backend changed, confirmed ...): one dimmed line, left-aligned; an error with ⚠ in red
        void SystemLine(string text, bool error = false)
        {
            var t = Selectable((error ? "⚠ " : "") + text.Trim('\n'), 11.5, error ? 1 : 0.65);
            if (error) t.Foreground = ErrorBrush; else t.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            t.HorizontalAlignment = HorizontalAlignment.Stretch;
            t.TextAlignment = TextAlignment.Left;
            t.Margin = new Thickness(4, 4, 20, 4);
            AddMessage(t);
        }

        sealed class AgentMsg
        {
            public readonly StringBuilder Raw = new StringBuilder();
            public RichTextBox Box;
        }
        AgentMsg cur;

        void StartAgent(string name)
        {
            var box = new RichTextBox
            {
                IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Padding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 13
            };
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.Document = new FlowDocument { PagePadding = new Thickness(0) };
            box.Document.Blocks.Add(new Paragraph(new Run("…")) { Margin = new Thickness(0), Foreground = DimBrush });
            var m = new AgentMsg { Box = box };
            // header: the backend, "Copy" for the whole answer (without the tool lines)
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var copy = new TextBlock { FontSize = 11, Opacity = 0.7 };
            var link = new Hyperlink(new Run("Copy")) { ToolTip = "Copy this answer" };
            link.Click += (s, e) =>
            {
                var text = string.Join("\n", m.Raw.ToString().Replace("\r", "").Split('\n').Where(l => l.Length == 0 || l[0] != ToolMark)).Trim();
                try { Clipboard.SetText(text); status.Text = "Answer copied."; } catch (Exception ex) { status.Text = "Could not copy: " + ex.Message; }
            };
            copy.Inlines.Add(link);
            DockPanel.SetDock(copy, Dock.Right); head.Children.Add(copy);
            var who = new TextBlock { Text = ShortName(name), FontSize = 11, Opacity = 0.6 };
            who.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            head.Children.Add(who);
            var panel = new StackPanel();
            panel.Children.Add(head); panel.Children.Add(box);
            AddMessage(new Border
            {
                Tag = "agent", DataContext = m, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 16, 6),
                Padding = new Thickness(10, 6, 10, 8), CornerRadius = new CornerRadius(10, 10, 10, 2), Background = AgentBubble, Child = panel
            });
            cur = m;
        }

        // output of the running answer (any thread) - buffered, rendered at most every 120 ms
        readonly StringBuilder pending = new StringBuilder();
        System.Windows.Threading.DispatcherTimer flushTimer;

        void AgentAppend(string text)
        {
            lock (pending) pending.Append(text);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (flushTimer == null)
                {
                    flushTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                        { Interval = TimeSpan.FromMilliseconds(120) };
                    flushTimer.Tick += (s, e) => FlushAgent();
                }
                if (!flushTimer.IsEnabled) flushTimer.Start();
            }));
        }

        // a tool call got its result: draw the answer again (the line turns ✓ / ✗)
        void ToolChanged() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { FlushAgent(); if (cur != null) Render(cur); }));

        void FlushAgent()
        {
            string t;
            lock (pending) { t = pending.ToString(); pending.Clear(); }
            flushTimer?.Stop();
            if (t.Length == 0) return;
            if (cur == null) StartAgent(Current?.Name ?? "Agent");
            cur.Raw.Append(t);
            Render(cur);
            ScrollDown();
        }

        // ------------------------------------------------------------------ tool calls: one compact line each
        // "✓ tc_plc_read · 42 lines · 0.4 s"   "✗ tc_build · 3 errors · 2.1 s"   "◐ tc_check …" while it runs;
        // a call that writes code has "diff" (the saved file against what the agent writes), every call "details"
        sealed class ToolCall
        {
            public string Id, Name, Args, Short;
            public Dictionary<string, object> Input;
            public double Start, End = -1;
            public bool? Ok;
            public string Before, After, Target;     // for "diff"
            public bool Open;                        // "details" expanded
            public string Full;                      // the result, for "details"
        }
        readonly Dictionary<string, ToolCall> toolCalls = new Dictionary<string, ToolCall>();
        int turnTools;

        static readonly HashSet<string> CodeWriters = new HashSet<string> { "tc_plc_write", "tc_plc_create" };

        /// <summary>A tool call starts (reader thread): remembered, a marker line goes into the answer.</summary>
        string ToolStart(string id, string name, Dictionary<string, object> input, double t)
        {
            name = Regex.Replace(name ?? "tool", @"^(?:mcp__[^_]+(?:_[^_]+)*?__|tcagent[-_/:.])", "");
            var tool = Regex.Match(name, @"\b((?:tc|ads)_[a-z_]+)\b");      // an ACP title like "tc_status (MCP: tcagent)"
            name = tool.Success ? tool.Groups[1].Value : Short(name, 50);
            var call = new ToolCall { Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Name = name, Input = input, Start = t,
                                      Args = input != null && input.Count > 0 ? string.Join(", ", input.Take(4).Select(kv => kv.Key + "=" + Short(Convert.ToString(kv.Value), 60))) : "" };
            if (CodeWriters.Contains(name) && input != null) try { Snapshot(call); } catch { }
            lock (toolCalls) toolCalls[call.Id] = call;
            System.Threading.Interlocked.Increment(ref turnTools);
            lastTool = name;
            return ToolMark + "#" + call.Id + "\n";
        }

        /// <summary>The result of a tool call (reader thread): ✓ / ✗, the first line of the result, the time.</summary>
        void ToolEnd(string id, bool ok, string result, double t)
        {
            ToolCall call;
            lock (toolCalls) toolCalls.TryGetValue(id ?? "", out call);
            if (call == null) return;
            call.End = t; call.Ok = ok;
            var first = (result ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "{" && l != "[") ?? "";
            call.Short = Short(first, 90);
            call.Full = Tail((result ?? "").Trim(), 3000);
            ToolChanged();
        }

        // the object as it is saved now - before the agent writes it (the IDE writes only when the tool runs, a moment later)
        void Snapshot(ToolCall call)
        {
            string S(string k) => call.Input.TryGetValue(k, out var v) ? v as string : null;
            var newDecl = S("declaration"); var newImpl = S("implementation");
            string oldDecl = "", oldImpl = "";
            if (call.Name == "tc_plc_write")
            {
                call.Target = S("path");
                var (d, i) = SavedObject(call.Target);
                oldDecl = d ?? ""; oldImpl = i ?? "";
            }
            else call.Target = (S("parentPath") ?? "") + "^" + S("name");
            call.Before = "(* Declaration *)\n" + oldDecl.Trim() + "\n\n(* Implementation *)\n" + oldImpl.Trim() + "\n";
            call.After = "(* Declaration *)\n" + (newDecl ?? oldDecl).Trim() + "\n\n(* Implementation *)\n" + (newImpl ?? oldImpl).Trim() + "\n";
        }

        // declaration and implementation of a PLC object from its file in the solution folder (TcPOU/TcGVL/TcDUT, also a
        // method, action or property of a POU)
        static (string Decl, string Impl) SavedObject(string treePath)
        {
            var dir = SolutionDir();
            if (string.IsNullOrEmpty(treePath) || dir == null) return (null, null);
            var parts = treePath.Split('^');
            for (int i = parts.Length - 1; i >= Math.Max(1, parts.Length - 2); i--)
            {
                var name = parts[i];
                var file = Directory.EnumerateFiles(dir, name + ".Tc*", SearchOption.AllDirectories)
                                    .FirstOrDefault(f => !f.Contains("\\_Boot\\") && !f.Contains("\\_CompileInfo\\") && !f.Contains("\\_Libraries\\") &&
                                                         Regex.IsMatch(Path.GetExtension(f), @"^\.Tc(POU|GVL|DUT|IO|TTO)$", RegexOptions.IgnoreCase));
                if (file == null) continue;
                var x = System.Xml.Linq.XDocument.Load(file).Root?.Elements().FirstOrDefault();
                if (x == null) return (null, null);
                if (i < parts.Length - 1)
                {
                    var member = parts[parts.Length - 1];
                    x = x.Elements().FirstOrDefault(e => (string)e.Attribute("Name") == member) ?? x;
                }
                string Text(System.Xml.Linq.XElement e) => e == null ? null : string.Concat(e.DescendantNodesAndSelf().OfType<System.Xml.Linq.XCData>().Select(c => c.Value));
                return (Text(x.Element("Declaration")), Text(x.Element("Implementation")));
            }
            return (null, null);
        }

        // the compare window of the IDE: the saved file left, what the agent writes right
        void OpenDiff(ToolCall call)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "tcagent-diff");
                Directory.CreateDirectory(dir);
                var left = Path.Combine(dir, call.Id + "-saved.st"); var right = Path.Combine(dir, call.Id + "-agent.st");
                File.WriteAllText(left, call.Before ?? "", Encoding.UTF8); File.WriteAllText(right, call.After ?? "", Encoding.UTF8);
                var diff = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
                if (diff == null) { status.Text = "No compare window in this IDE - the files are in " + dir; return; }
                var caption = $"{call.Name}: {call.Target?.Split('^').LastOrDefault()}";
                diff.OpenComparisonWindow2(left, right, caption, call.Target, call.Name == "tc_plc_create" ? "Before (new object)" : "Saved file",
                                           "Written by the agent", caption, null,
                                           (uint)(Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
            }
            catch (Exception ex) { status.Text = "Compare: " + ex.Message; }
        }

        Paragraph ToolLine(ToolCall c)
        {
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1), FontSize = 11.5, ToolTip = c.Name + (c.Args.Length > 0 ? " " + c.Args : "") };
            var (mark, brush) = c.Ok == null ? ("◐", (Brush)CardBorder) : c.Ok == true ? ("✓", OkBrush) : ("✗", ErrorBrush);
            p.Inlines.Add(new Run(mark + " ") { Foreground = brush, FontFamily = new FontFamily("Segoe UI Symbol") });
            p.Inlines.Add(new Run(c.Name) { FontWeight = FontWeights.SemiBold });
            var rest = c.Ok == null ? (c.Args.Length > 0 ? " · " + Short(c.Args, 70) : "") + " …"
                                    : (c.Short?.Length > 0 ? " · " + c.Short : "") + $" · {Math.Max(0, c.End - c.Start):0.0} s";
            p.Inlines.Add(new Run(rest) { Foreground = c.Ok == false ? ErrorBrush : DimBrush });
            if (c.Before != null && c.After != null)
            {
                p.Inlines.Add(new Run("  "));
                var d = new Hyperlink(new Run("diff")) { ToolTip = "The saved object against what the agent writes (compare window)" };
                d.Click += (s, e) => OpenDiff(c);
                p.Inlines.Add(d);
            }
            p.Inlines.Add(new Run("  "));
            var det = new Hyperlink(new Run(c.Open ? "hide" : "details")) { ToolTip = "The parameters and the result of this call" };
            det.Click += (s, e) => { c.Open = !c.Open; if (cur != null && cur.Raw.ToString().Contains("#" + c.Id)) Render(cur); else RenderOwner(c); };
            p.Inlines.Add(det);
            return p;
        }

        // the answer that holds the call (an earlier one): drawn again for "details"
        void RenderOwner(ToolCall c)
        {
            foreach (var b in msgs.Children.OfType<Border>())
                if (b.DataContext is AgentMsg m && m.Raw.ToString().Contains("#" + c.Id)) { Render(m); return; }
        }

        string Details(ToolCall c)
        {
            var sb = new StringBuilder(c.Name).Append('\n');
            if (c.Input != null) foreach (var kv in c.Input) sb.Append("  ").Append(kv.Key).Append(" = ").Append(Short(Convert.ToString(kv.Value), 400)).Append('\n');
            if (c.Ok != null) sb.Append(c.Ok == true ? "Result:\n" : "Failed:\n").Append(c.Full ?? "");
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ Markdown (small subset)
        internal const char ToolMark = '\u0001';

        void Render(AgentMsg m)
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            var lines = m.Raw.ToString().Replace("\r", "").Split('\n');
            Paragraph para = null;
            StringBuilder code = null;
            List<string[]> table = null;
            Section tableBlock = null;
            foreach (var line in lines)
            {
                if (table != null && !line.TrimStart().StartsWith("|")) table = null;
                if (code != null)
                {
                    if (line.TrimStart().StartsWith("```")) { doc.Blocks.Add(CodeBlock(code.ToString().TrimEnd('\n'))); code = null; }
                    else code.Append(line).Append('\n');
                    continue;
                }
                if (line.TrimStart().StartsWith("```")) { para = null; code = new StringBuilder(); continue; }
                if (line.Length > 0 && line[0] == ToolMark)
                {
                    para = null;
                    ToolCall tc = null;
                    if (line.Length > 1 && line[1] == '#') lock (toolCalls) toolCalls.TryGetValue(line.Substring(2).Trim(), out tc);
                    if (tc != null)
                    {
                        doc.Blocks.Add(ToolLine(tc));
                        if (tc.Open) doc.Blocks.Add(CodeBlock(Details(tc)));
                    }
                    else doc.Blocks.Add(new Paragraph(new Run("⚙ " + line.Substring(1).Trim())) { Margin = new Thickness(0, 1, 0, 1), FontSize = 11, Foreground = DimBrush });
                    continue;
                }
                if (line.Trim().Length == 0) { para = null; continue; }
                var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    para = null;
                    var p = new Paragraph { Margin = new Thickness(0, 6, 0, 2), FontWeight = FontWeights.SemiBold, FontSize = h.Groups[1].Length <= 2 ? 15 : 13.5 };
                    AddInlines(p, h.Groups[2].Value);
                    doc.Blocks.Add(p);
                    continue;
                }
                var li = Regex.Match(line, @"^(\s*)([-*•]|\d+[.)])\s+(.*)$");
                if (li.Success)
                {
                    para = null;
                    var p = new Paragraph { Margin = new Thickness(12 + li.Groups[1].Length * 6, 1, 0, 1), TextIndent = -10 };
                    p.Inlines.Add(new Run((char.IsDigit(li.Groups[2].Value[0]) ? li.Groups[2].Value : "•") + " "));
                    AddInlines(p, li.Groups[3].Value);
                    doc.Blocks.Add(p);
                    continue;
                }
                if (line.TrimStart().StartsWith("|"))
                {
                    para = null;
                    if (table == null) { table = new List<string[]>(); doc.Blocks.Add(tableBlock = new Section()); }
                    if (!Regex.IsMatch(line, @"^\s*\|[\s:|-]+\|\s*$")) table.Add(Cells(line));   // skip the separator row
                    tableBlock.Blocks.Clear();
                    tableBlock.Blocks.Add(TableBlock(table));
                    continue;
                }
                table = null;
                if (para == null) { para = new Paragraph { Margin = new Thickness(0, 3, 0, 3) }; doc.Blocks.Add(para); }
                else para.Inlines.Add(new LineBreak());
                AddInlines(para, line);
            }
            if (code != null) doc.Blocks.Add(CodeBlock(code.ToString().TrimEnd('\n')));   // still streaming
            m.Box.Document = doc;
        }

        static string[] Cells(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(c => c.Trim()).ToArray();
        }

        static Block TableBlock(List<string[]> rows)
        {
            int cols = rows.Max(r => r.Length);
            var tbl = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 4), BorderBrush = DimBrush, BorderThickness = new Thickness(0.5) };
            // column widths by content (a short "Folder" column next to a long "Content" column), at least 8 %
            var len = Enumerable.Range(0, cols).Select(c => (double)Math.Max(4, rows.Max(r => c < r.Length ? Math.Min(r[c].Length, 80) : 0))).ToArray();
            var sum = len.Sum();
            for (int i = 0; i < cols; i++) tbl.Columns.Add(new TableColumn { Width = new GridLength(Math.Max(0.08, len[i] / sum), GridUnitType.Star) });
            var group = new TableRowGroup();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = new TableRow();
                if (r == 0) row.Background = CodeBack;
                for (int c = 0; c < cols; c++)
                {
                    var p = new Paragraph { Margin = new Thickness(0), FontSize = 12.5 };
                    if (r == 0) p.FontWeight = FontWeights.SemiBold;
                    AddInlines(p, c < rows[r].Length ? rows[r][c] : "");
                    row.Cells.Add(new TableCell(p) { Padding = new Thickness(5, 2, 5, 2), BorderBrush = DimBrush, BorderThickness = new Thickness(0.5) });
                }
                group.Rows.Add(row);
            }
            tbl.RowGroups.Add(group);
            return tbl;
        }

        static Block CodeBlock(string text) => new Paragraph(new Run(text))
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = CodeBack, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 4)
        };

        // **bold**, *italic*, `code`, [text](url)
        static void AddInlines(Paragraph p, string s)
        {
            int i = 0;
            foreach (Match m in Regex.Matches(s, @"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])"))
            {
                if (m.Index > i) p.Inlines.Add(new Run(s.Substring(i, m.Index - i)));
                if (m.Groups[1].Success) p.Inlines.Add(new Bold(new Run(m.Groups[1].Value)));
                else if (m.Groups[2].Success) p.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), Background = CodeBack });
                else if (m.Groups[3].Success)
                {
                    var link = new Hyperlink(new Run(m.Groups[3].Value)) { ToolTip = m.Groups[4].Value };
                    var url = m.Groups[4].Value;
                    link.Click += (o, e) => { try { if (Regex.IsMatch(url, "^(https?|file)://", RegexOptions.IgnoreCase)) System.Diagnostics.Process.Start(url); } catch { } };
                    p.Inlines.Add(link);
                }
                else p.Inlines.Add(new Italic(new Run(m.Groups[5].Value)));
                i = m.Index + m.Length;
            }
            if (i < s.Length) p.Inlines.Add(new Run(s.Substring(i)));
        }

        // ------------------------------------------------------------------ input: keys, history, paste
        // returns true if the key was handled
        bool InputKey(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.Enter && (mods & ModifierKeys.Shift) == 0) { Send(); return true; }
            if (e.Key == Key.V && (mods & ModifierKeys.Control) != 0 && PasteSpecial()) return true;
            if (e.Key == Key.Up && mods == ModifierKeys.None && input.GetLineIndexFromCharacterIndex(input.CaretIndex) <= 0) return HistoryStep(-1);
            if (e.Key == Key.Down && mods == ModifierKeys.None && historyPos >= 0
                && input.GetLineIndexFromCharacterIndex(input.CaretIndex) >= Math.Max(0, input.LineCount - 1)) return HistoryStep(+1);
            return false;
        }

        bool HistoryStep(int dir)
        {
            if (inputHistory.Count == 0) return false;
            if (historyPos < 0)
            {
                if (dir > 0) return false;
                draft = input.Text;
                historyPos = inputHistory.Count;
            }
            var next = historyPos + dir;
            if (next < 0) return true;                                  // oldest reached
            if (next >= inputHistory.Count) { historyPos = -1; input.Text = draft; input.CaretIndex = input.Text.Length; return true; }
            historyPos = next;
            input.Text = inputHistory[historyPos];
            input.CaretIndex = input.Text.Length;
            return true;
        }

        void RememberPrompt(string text)
        {
            historyPos = -1; draft = "";
            if (inputHistory.Count > 0 && inputHistory[inputHistory.Count - 1] == text) return;
            inputHistory.Add(text);
            if (inputHistory.Count > MaxInputHistory) inputHistory.RemoveRange(0, inputHistory.Count - MaxInputHistory);
            try { Directory.CreateDirectory(Dir); File.WriteAllText(InputHistoryFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(inputHistory), Encoding.UTF8); }
            catch { }
        }

        void LoadInputHistory()
        {
            try
            {
                if (File.Exists(InputHistoryFile))
                    inputHistory.AddRange(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<string>>(File.ReadAllText(InputHistoryFile)) ?? new List<string>());
            }
            catch { }
        }

        // files, images and long texts from the clipboard become attachments; short text is pasted normally
        bool PasteSpecial()
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) { foreach (var f in Clipboard.GetFileDropList()) AddFile(f); return true; }
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img == null) return false;
                    var file = NewAttachPath("screenshot.png");
                    using (var fs = File.Create(file)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); enc.Save(fs); }
                    AddAttachment(new Attachment { Name = System.IO.Path.GetFileName(file), Path = file, Kind = "image" });
                    return true;
                }
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (text.Length < 3000) return false;
                    var file = NewAttachPath("pasted.txt");
                    File.WriteAllText(file, text, Encoding.UTF8);
                    var lines = text.Count(c => c == '\n') + 1;
                    AddAttachment(new Attachment { Name = $"Pasted text ({lines} lines)", Path = file, Kind = "text" });
                    return true;
                }
            }
            catch (Exception ex) { status.Text = "Paste: " + ex.Message; }
            return false;
        }

        static string NewAttachPath(string name)
        {
            var dir = System.IO.Path.Combine(AttachDir, DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, $"{DateTime.Now:HHmmss-fff}_{name}");
        }

        void AddFile(string path)
        {
            if (Directory.Exists(path)) { status.Text = "Folders cannot be attached - attach the files."; return; }
            if (!File.Exists(path)) return;
            if (attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            AddAttachment(new Attachment
            {
                Name = System.IO.Path.GetFileName(path), Path = path,
                Kind = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" ? "image" : "file"
            });
        }

        void AddAttachment(Attachment a)
        {
            attachments.Add(a);
            var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            close.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var label = new TextBlock { Text = "📎 " + a.Name, VerticalAlignment = VerticalAlignment.Center, ToolTip = a.Path };
            label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(label); sp.Children.Add(close);
            var chip = new Border { Child = sp, Background = AgentBubble, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 2, 1), Margin = new Thickness(0, 0, 4, 2) };
            close.Click += (s, e) => { attachments.Remove(a); chips.Children.Remove(chip); };
            chips.Children.Add(chip);
            input.Focus();
        }

        List<Attachment> TakeAttachments()
        {
            var list = attachments.ToList();
            attachments.Clear(); chips.Children.Clear();
            return list;
        }

        // ------------------------------------------------------------------ attachments -> prompt
        // Text documents (and the text of .docx files) go into the prompt; PDFs and images are referenced by path
        // (Claude Code reads them with its Read tool - the attachments folder is added with --add-dir).
        static string AttachmentPrompt(IList<Attachment> att)
        {
            if (att == null || att.Count == 0) return null;
            var sb = new StringBuilder("[Attached documents - use them for this request]\n");
            int inlined = 0;
            foreach (var a in att)
            {
                string text = null;
                var ext = System.IO.Path.GetExtension(a.Path).ToLowerInvariant();
                try
                {
                    if (a.Kind == "text" || IsTextFile(ext)) text = File.ReadAllText(a.Path);
                    else if (ext == ".docx") text = DocxText(a.Path);
                }
                catch { }
                if (text != null && inlined + text.Length <= InlineTextLimit)
                {
                    inlined += text.Length;
                    sb.Append($"\n--- {a.Name} ({a.Path}) ---\n").Append(text.TrimEnd()).Append("\n--- end of ").Append(a.Name).Append(" ---\n");
                }
                else
                    sb.Append($"\n- {a.Name}: {a.Path}" + (a.Kind == "image" ? " (image)" : ext == ".pdf" ? " (PDF)" : "") +
                              " - read this file with your file tools.\n");
            }
            return sb.ToString();
        }

        static bool IsTextFile(string ext) => new[]
        {
            ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".ini", ".cfg", ".yaml", ".yml", ".st", ".iec",
            ".tcpou", ".tcgvl", ".tcdut", ".tcio", ".tsproj", ".tmc", ".xti", ".html", ".htm", ".js", ".ts", ".py", ".cs", ".c", ".cpp", ".h", ".ps1", ".bat", ".cmd"
        }.Contains(ext);

        // Text of a Word document (paragraphs of word/document.xml) - enough for manuals and specifications
        static string DocxText(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var e = zip.GetEntry("word/document.xml");
                if (e == null) return null;
                string xml;
                using (var r = new StreamReader(e.Open(), Encoding.UTF8)) xml = r.ReadToEnd();
                xml = Regex.Replace(xml, @"</w:p>", "\n");
                xml = Regex.Replace(xml, @"<w:tab/>", "\t");
                xml = Regex.Replace(xml, @"<w:br[^>]*/>", "\n");
                var text = Regex.Replace(xml, "<[^>]+>", "");
                return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"\n{3,}", "\n\n")).Trim();
            }
        }
    }
}
