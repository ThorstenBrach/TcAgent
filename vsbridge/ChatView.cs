// TcAgent Chat - the view: message bubbles (agent left, user right), a small Markdown renderer,
// the input box with prompt history (Up/Down) and attachments (paste, drag & drop, "+").
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
        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        readonly StackPanel msgs = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        readonly ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly WrapPanel chips = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
        readonly Button attach = new Button { Content = "+", Width = 26, Padding = new Thickness(0), ToolTip = "Attach documents (or paste / drop files into the input box)" };
        readonly List<Attachment> attachments = new List<Attachment>();
        readonly List<string> inputHistory = new List<string>();
        int historyPos = -1;      // -1 = editing a new prompt
        string draft = "";

        // ------------------------------------------------------------------ layout
        FrameworkElement BuildView()
        {
            // top bar: backend + options | setup, new
            var top = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
            DockPanel.SetDock(clear, Dock.Right);
            top.Children.Add(clear);
            if (Installer.IsVsixInstall) { DockPanel.SetDock(setup, Dock.Right); top.Children.Add(setup); }
            var left = new WrapPanel();
            left.Children.Add(backendBox); left.Children.Add(contextBox); left.Children.Add(buildBox);
            top.Children.Add(left);

            scroller.Content = msgs;
            scroller.SizeChanged += (s, e) => UpdateBubbleWidths();

            // input box in a rounded frame (VS Code style): attachments, text, buttons
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(stop); right.Children.Add(send);
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(attach, Dock.Left);
            row.Children.Add(right); row.Children.Add(attach);
            row.Children.Add(new TextBlock { Text = "Enter = send · Shift+Enter = new line · ↑/↓ = previous prompts", Opacity = 0.55, FontSize = 11,
                                             VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new DockPanel();
            DockPanel.SetDock(chips, Dock.Top); DockPanel.SetDock(row, Dock.Bottom);
            box.Children.Add(chips); box.Children.Add(row); box.Children.Add(input);
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(6, 2, 6, 2), Child = box };
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var bottom = new DockPanel();
            DockPanel.SetDock(status, Dock.Bottom);
            status.Margin = new Thickness(8, 0, 8, 4);
            bottom.Children.Add(status); bottom.Children.Add(frame);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(scroller);

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
            attach.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach documents for the agent" };
                if (dlg.ShowDialog() == true) foreach (var f in dlg.FileNames) AddFile(f);
            };
            send.Content = "Send";
            stop.Content = "Stop";
            send.ToolTip = "Send (Enter)";
            LoadInputHistory();
            return root;
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

        void SystemLine(string text, bool error = false)
        {
            var t = Selectable(text.Trim('\n'), 11.5, error ? 1 : 0.65);
            if (error) t.Foreground = ErrorBrush; else t.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextAlignment = TextAlignment.Center;
            t.Margin = new Thickness(20, 4, 20, 4);
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
            var head = new TextBlock { Text = name, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 2) };
            head.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var panel = new StackPanel();
            panel.Children.Add(head); panel.Children.Add(box);
            AddMessage(new Border
            {
                Tag = "agent", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 40, 6),
                Padding = new Thickness(10, 6, 10, 8), CornerRadius = new CornerRadius(10, 10, 10, 2), Background = AgentBubble, Child = panel
            });
            cur = new AgentMsg { Box = box };
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
                    doc.Blocks.Add(new Paragraph(new Run("⚙ " + line.Substring(1).Trim())) { Margin = new Thickness(0, 1, 0, 1), FontSize = 11, Foreground = DimBrush });
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
