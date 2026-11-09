// TcAgentBridge - "Formatter settings": every rule of the ST formatter in plain words, with a live preview (before | after)
// that looks like the TwinCAT editor, saved into formatting.json of a knowledge level; "Wish a rule or feature ..." as a
// GitHub issue. Built after the approved mockup and docs/UI_GUIDELINES.md.
//
//   Tools > Formatter settings (TcAgent) ...
//   Search (Ctrl+F), "Start from profile", the rules grouped with value and level (✎ changed, not saved · ⊘ locked by …),
//   the details of the selected rule, the preview with the changed lines marked, a status line.
//   The preview takes the font and the background of the TwinCAT editor and syntax colors for the light or dark theme
//   (colors found in the editor are used, otherwise the TwinCAT defaults). TcAgent.exe runs in the background.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        const int CmdFormatSettings = 0x0104;

        const string SampleDeclaration =
"FUNCTION_BLOCK FB_Motor\r\nVAR_INPUT\r\nbStart:BOOL; // start the cycle\r\nnParts : INT:=0; // parts so far\r\ntTimeout:TIME:=t#5s;\r\nEND_VAR\r\nVAR\r\nfbTimer   :   TON;\r\neState : E_State;\r\nEND_VAR\r\n";

        const string SampleImplementation =
"// cycle of the motor\r\nif bStart and not bBusy or bSetup and bKey then\r\nbBusy:=true;\r\nend_if\r\n\r\n\r\ncase eState of\r\nE_State.Idle:\r\nif bStart then eState:=E_State.Run; end_if\r\nE_State.Run:\r\nfbTimer(IN:=bBusy AND NOT bDone, PT:=tTimeout);\r\nfbAxis(Execute := bBusy, Position := rTarget, Velocity := rVelocity, Acceleration := rAcc, Deceleration := rAcc, Jerk := rJerk);\r\nelse\r\n;\r\nend_case\r\n";

        // ------------------------------------------------------------------ the look of the TwinCAT editor
        sealed class EditorLook
        {
            public FontFamily Font = new FontFamily("Consolas");
            public double Size = 13;
            public Brush Back, Fore;
            public readonly Dictionary<string, Brush> Colors = new Dictionary<string, Brush>();
            public bool Dark;
        }

        static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static SolidColorBrush B(System.Drawing.Color c) => B(c.R, c.G, c.B);

        /// <summary>Font and background of the open TwinCAT editor; syntax colors found in it, otherwise the TwinCAT defaults of
        /// the theme. What was found is logged, so the defaults can be checked.</summary>
        EditorLook ReadEditorLook(System.Windows.Forms.Control syn)
        {
            var look = new EditorLook();
            System.Drawing.Color back = System.Drawing.Color.Empty, fore = System.Drawing.Color.Empty;
            try
            {
                if (syn != null)
                {
                    var f = syn.Font;
                    if (f != null) { look.Font = new FontFamily(f.FontFamily.Name); look.Size = f.SizeInPoints * 96.0 / 72.0; }
                    // the control's font is not always the font of the text (columns only line up with a fixed-width font)
                    if (!Monospaced(look.Font)) { look.Font = new FontFamily("Consolas"); look.Size = Math.Max(look.Size, 13); }
                    back = syn.BackColor; fore = syn.ForeColor;
                }
            }
            catch { }
            if (back.IsEmpty || back.A == 0)
            {
                try { var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey); back = c; } catch { back = System.Drawing.Color.White; }
            }
            look.Dark = back.GetBrightness() < 0.4;
            if (fore.IsEmpty || Math.Abs(fore.GetBrightness() - back.GetBrightness()) < 0.3) fore = look.Dark ? System.Drawing.Color.FromArgb(220, 220, 220) : System.Drawing.Color.Black;
            look.Back = B(back); look.Fore = B(fore);
            // TwinCAT defaults
            if (look.Dark)
            {
                look.Colors["keyword"] = B(86, 156, 214); look.Colors["type"] = B(86, 156, 214); look.Colors["comment"] = B(87, 166, 74);
                look.Colors["string"] = B(214, 157, 133); look.Colors["number"] = B(181, 206, 168); look.Colors["pragma"] = B(155, 155, 155);
            }
            else
            {
                look.Colors["keyword"] = B(0, 0, 255); look.Colors["type"] = B(0, 0, 255); look.Colors["comment"] = B(0, 128, 0);
                look.Colors["string"] = B(163, 21, 21); look.Colors["number"] = B(0, 0, 0); look.Colors["pragma"] = B(128, 128, 128);
            }
            // colors of the editor itself, where its objects name them
            try
            {
                var found = new Dictionary<string, System.Drawing.Color>();
                FindColors(syn, 0, found, new HashSet<object>(ReferenceEqualityComparer.Instance), "");
                foreach (var kv in found)
                {
                    var n = kv.Key.ToLowerInvariant();
                    string cls = n.Contains("keyword") ? "keyword" : n.Contains("comment") ? "comment" : n.Contains("string") ? "string"
                               : n.Contains("number") || n.Contains("literal") ? "number" : n.Contains("pragma") ? "pragma" : n.Contains("type") ? "type" : null;
                    if (cls != null && !kv.Value.IsEmpty && kv.Value.A > 0) look.Colors[cls] = B(kv.Value);
                }
                if (found.Count > 0) Log("Formatter preview colors found in the editor: " + string.Join(", ", found.Select(k => k.Key + "=" + k.Value.Name).Take(40)));
            }
            catch (Exception ex) { Log("Formatter preview colors: " + Inner(ex)); }
            return look;
        }

        /// <summary>Do "iiiiii" and "WWWWWW" have the same width in this font?</summary>
        static bool Monospaced(FontFamily font)
        {
            try
            {
                var tf = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double W(string s) => new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 13, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                return Math.Abs(W("iiiiii") - W("WWWWWW")) < 0.5 && Math.Abs(W("      ") - W("iiiiii")) < 0.5;
            }
            catch { return false; }
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        static void FindColors(object o, int depth, Dictionary<string, System.Drawing.Color> found, HashSet<object> seen, string path)
        {
            if (o == null || depth > 3 || found.Count > 60 || o is string || o.GetType().IsPrimitive || !seen.Add(o)) return;
            if (o is System.Windows.Forms.Control c && depth > 0 && !(c.GetType().Name.Contains("SynEd") || c.GetType().Name.Contains("TextArea"))) return;
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = o.GetType(); t != null && t != typeof(object) && t != typeof(System.Windows.Forms.Control) && t != typeof(System.ComponentModel.Component); t = t.BaseType)
                foreach (var f in t.GetFields(bf | BindingFlags.DeclaredOnly))
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v is System.Drawing.Color col) { if (!found.ContainsKey(path + f.Name)) found[path + f.Name] = col; }
                    else if (v != null && !(v is Delegate) && (f.FieldType.Namespace ?? "").StartsWith("_3S")) FindColors(v, depth + 1, found, seen, path + f.Name + ".");
                }
        }

        /// <summary>A read-only code view in the look of the editor: [class, text] pieces, marked lines tinted.</summary>
        static FrameworkElement CodeView(EditorLook look, ArrayList pieces, HashSet<int> marked)
        {
            var lines = new List<List<(string Cls, string Text)>> { new List<(string, string)>() };
            foreach (var p in pieces.OfType<ArrayList>())
            {
                string cls = p[0] as string, text = p[1] as string ?? "";
                if (cls == "newline") { lines.Add(new List<(string, string)>()); continue; }
                var parts = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) lines.Add(new List<(string, string)>());
                    if (parts[i].Length > 0) lines[lines.Count - 1].Add((cls, parts[i]));
                }
            }
            var mark = new SolidColorBrush(Color.FromArgb(look.Dark ? (byte)70 : (byte)55, 0xE0, 0x8A, 0x00));
            var bar = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00));
            var stack = new StackPanel { Background = look.Back };
            for (int n = 0; n < lines.Count; n++)
            {
                var tb = new TextBlock { FontFamily = look.Font, FontSize = look.Size, Foreground = look.Fore, Padding = new Thickness(6, 0, 8, 0) };
                foreach (var (cls, text) in lines[n])
                    tb.Inlines.Add(new Run(text.Replace("\t", "    ")) { Foreground = look.Colors.TryGetValue(cls, out var br) ? br : look.Fore });
                if (lines[n].Count == 0) tb.Text = " ";
                bool m = marked != null && marked.Contains(n);
                stack.Children.Add(new Border
                {
                    Child = tb, Background = m ? mark : Brushes.Transparent,
                    BorderBrush = bar, BorderThickness = new Thickness(m ? 3 : 0, 0, 0, 0), Padding = new Thickness(m ? 0 : 3, 0, 0, 0)
                });
            }
            return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = look.Back };
        }

        static List<string> LinesOf(ArrayList pieces)
        {
            var sb = new StringBuilder();
            foreach (var p in pieces.OfType<ArrayList>()) sb.Append(p[0] as string == "newline" ? "\n" : (p[1] as string ?? "").Replace("\r\n", "\n"));
            return sb.ToString().Split('\n').ToList();
        }

        /// <summary>Lines of b that are not in a longest common sequence with a - the changed lines.</summary>
        static HashSet<int> Changed(List<string> a, List<string> b)
        {
            int n = a.Count, m = b.Count;
            if ((long)n * m > 4_000_000) return new HashSet<int>();
            var l = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    l[i, j] = a[i].TrimEnd() == b[j].TrimEnd() ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
            var keep = new HashSet<int>();
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (a[i].TrimEnd() == b[j].TrimEnd()) { keep.Add(j); i++; j++; }
                else if (l[i + 1, j] >= l[i, j + 1]) i++; else j++;
            }
            var r = new HashSet<int>();
            for (int j = 0; j < m; j++) if (!keep.Contains(j)) r.Add(j);
            return r;
        }

        // ------------------------------------------------------------------ language: German when the chat answers in German, English otherwise
        [ThreadStatic] static bool fmtGerman;
        static string T(string en, string de) => fmtGerman ? de : en;

        static bool UiGerman()
        {
            var lang = ChatSetting("answerLanguage") as string;
            if (string.IsNullOrWhiteSpace(lang) || lang.Equals("request", StringComparison.OrdinalIgnoreCase)) lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
            return lang.StartsWith("de", StringComparison.OrdinalIgnoreCase) || lang.StartsWith("German", StringComparison.OrdinalIgnoreCase) || lang.StartsWith("Deutsch", StringComparison.OrdinalIgnoreCase);
        }

        static string LevelName(string l) => l switch { "project" => T("project", "Projekt"), "user" => T("user", "Benutzer"), "team" => "Team", "company" => T("company", "Firma"), _ => l };

        /// <summary>Where a value comes from, in the language of the window.</summary>
        static string FromName(string f) => !fmtGerman || f == null ? f : f.Replace("default", "Standard").Replace("unset by", "zurückgesetzt von").Replace("profile", "Profil")
            .Replace("user", "Benutzer").Replace("project", "Projekt").Replace("company", "Firma").Replace("built-in", "eingebaut").Replace("platform", "Plattform");

        // ------------------------------------------------------------------ examples and before/after: one below the other or side by side
        static string LayoutFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "bridge", "formatter-window.json");

        static bool ReadStacked()
        {
            try { return !File.Exists(LayoutFile) || !File.ReadAllText(LayoutFile).Contains("\"stacked\": false"); } catch { return true; }
        }

        static void WriteStacked(bool stacked)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)); File.WriteAllText(LayoutFile, "{ \"stacked\": " + (stacked ? "true" : "false") + " }\n"); } catch { }
        }

        /// <summary>Code views with a title each, one below the other (the page scrolls) or side by side.</summary>
        static UIElement Arrange(List<(string Title, FrameworkElement View, bool Now)> items, bool stacked)
        {
            Border Frame(FrameworkElement v, bool now, Thickness m)
            {
                var b = new Border { Child = v, BorderThickness = new Thickness(now ? 2 : 1), Margin = m };
                if (now) b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xD8));
                else b.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                return b;
            }
            TextBlock Title((string Title, FrameworkElement View, bool Now) x, Thickness m) =>
                new TextBlock { Text = x.Title, Margin = m, Opacity = x.Now ? 1 : 0.7, FontWeight = x.Now ? FontWeights.SemiBold : FontWeights.Normal };
            if (stacked)
            {
                var sp = new StackPanel();
                for (int i = 0; i < items.Count; i++)
                {
                    sp.Children.Add(Title(items[i], new Thickness(0, i == 0 ? 2 : 10, 0, 2)));
                    sp.Children.Add(Frame(items[i].View, items[i].Now, new Thickness(0)));
                }
                return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < items.Count; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 160 });
                var h = Title(items[c], new Thickness(c == 0 ? 0 : 6, 2, 0, 2));
                var b = Frame(items[c].View, items[c].Now, new Thickness(c == 0 ? 0 : 6, 0, 0, 0));
                Grid.SetColumn(h, c); Grid.SetColumn(b, c); Grid.SetRow(b, 1);
                g.Children.Add(h); g.Children.Add(b);
            }
            return g;
        }

        // ------------------------------------------------------------------ the window
        internal void ShowFormatSettings()
        {
            fmtGerman = UiGerman();
            var exe = TcUnitControl.TcAgentExe();
            if (exe == null) { FormatMessage(T("Formatter settings: TcAgent.exe was not found - set it up in the TcAgent Chat first.", "Formatierer-Einstellungen: TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
            var sln = TcUnitControl.SolutionPath();
            // values from the IDE are read on the UI thread before anything runs in the background
            string openDecl = null, openImpl = null, openName = null;
            System.Windows.Forms.Control anySyn = null;
            try
            {
                var dv = ActiveDocView(out var doc);
                var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
                foreach (var pn in new[] { "InterfaceEditorView", "ImplementationEditorView" })
                {
                    if (!(SafeGet(cds, pn) is System.Windows.Forms.Control ctl)) continue;
                    var syn = ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl
                            : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0);
                    if (syn == null) continue;
                    anySyn = anySyn ?? syn;
                    var t = SafeGet(syn, "Text") as string;
                    if (pn == "InterfaceEditorView") openDecl = t; else openImpl = t;
                }
                if (anySyn != null && !string.IsNullOrEmpty(doc)) openName = Path.GetFileNameWithoutExtension(doc.Split('|').Last().Trim());
            }
            catch { }
            var look = ReadEditorLook(anySyn);

            var w = new Window
            {
                Title = T("Formatter settings (TcAgent)", "Formatierer-Einstellungen (TcAgent)"), Icon = AppIcon, Width = 1280, Height = 780, MinWidth = 820, MinHeight = 520,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            try { w.Owner = Application.Current?.MainWindow; } catch { }
            if (Application.Current != null) ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(w, true);
            w.SetResourceReference(Control.BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            w.SetResourceReference(Control.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

            // ---- frame: heading, search/profile/level, body, buttons, status line
            var head = new TextBlock { Margin = new Thickness(12, 10, 12, 6), TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new Run(T("Formatter settings", "Formatierer-Einstellungen")) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
            head.Inlines.Add(new Run(T(" – every rule of the ST formatter; a change shows at once in the preview", " – jede Regel des ST-Formatierers; eine Änderung zeigt sich sofort in der Vorschau")) { Foreground = Brushes.Gray });
            var search = new TextBox { Width = 300, Padding = new Thickness(3, 2, 3, 2), ToolTip = T("Search rules by name, key or description (Ctrl+F)", "Regeln nach Name, Schlüssel oder Beschreibung suchen (Strg+F)") };
            var hint = new TextBlock { Text = T("Search rules (Ctrl+F)", "Regeln suchen (Strg+F)"), Opacity = 0.55, IsHitTestVisible = false, Margin = new Thickness(6, 3, 0, 0) };
            var clear = new Button { Content = "✕", Width = 22, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Visibility = Visibility.Collapsed, ToolTip = T("Clear the search", "Suche leeren") };
            var searchBox = new Grid { Width = 300 };
            searchBox.Children.Add(search); searchBox.Children.Add(hint);
            clear.HorizontalAlignment = HorizontalAlignment.Right; searchBox.Children.Add(clear);
            var profile = new ComboBox { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0), ToolTip = T("Puts the values of a profile into the window (not locked rules); Save writes the changed ones", "Übernimmt die Werte eines Profils ins Fenster (nicht gesperrte Regeln); Speichern schreibt die geänderten") };
            var level = new ComboBox { MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), ToolTip = T("The knowledge level whose formatting.json gets the changed rules", "Die Wissens-Ebene, deren formatting.json die geänderten Regeln bekommt") };
            ComboBoxItem Lv(string v) => new ComboBoxItem { Content = LevelName(v), Tag = v };
            if (!string.IsNullOrEmpty(sln)) level.Items.Add(Lv("project"));
            level.Items.Add(Lv("user"));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TCAGENT_TEAM"))) level.Items.Add(Lv("team"));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TCAGENT_COMPANY"))) level.Items.Add(Lv("company"));
            level.SelectedIndex = 0;
            var row = new DockPanel { Margin = new Thickness(12, 0, 12, 8), LastChildFill = false };
            var lp = new TextBlock { Text = T("Start from profile:", "Starten mit Profil:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var ll = new TextBlock { Text = T("Save to level:", "Speichern in Ebene:"), VerticalAlignment = VerticalAlignment.Center };
            foreach (var x in new UIElement[] { searchBox, lp, profile }) { DockPanel.SetDock(x, Dock.Left); row.Children.Add(x); }
            foreach (var x in new UIElement[] { level, ll }) { DockPanel.SetDock(x, Dock.Right); row.Children.Add(x); }

            var statusLeft = new TextBlock { Text = T("Reading the rules …", "Lese die Regeln …"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var statusRight = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            var progress = new ProgressBar { Height = 2, IsIndeterminate = true, BorderThickness = new Thickness(0), Visibility = Visibility.Visible };
            var status = new DockPanel { Margin = new Thickness(0) };
            var statusInner = new DockPanel { Margin = new Thickness(12, 3, 12, 3) };
            DockPanel.SetDock(statusRight, Dock.Right); statusInner.Children.Add(statusRight); statusInner.Children.Add(statusLeft);
            DockPanel.SetDock(progress, Dock.Top); status.Children.Add(progress); status.Children.Add(statusInner);
            status.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.StatusBarDefaultBrushKey);
            statusLeft.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);
            statusRight.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarDefaultTextBrushKey);

            var wish = new Button { Content = T("Wish a rule or feature …", "Regel oder Funktion wünschen …"), Padding = new Thickness(10, 3, 10, 3), ToolTip = T("Opens a new issue on GitHub with the example of the preview - you check it there and send it yourself", "Öffnet ein neues Issue auf GitHub mit dem Beispiel der Vorschau - du prüfst es dort und schickst es selbst ab") };
            var undo = new Button { Content = T("Undo changes", "Änderungen verwerfen"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = T("Back to the rules as they apply now", "Zurück zu den Regeln, wie sie jetzt gelten") };
            var save = new Button { Content = T("Save", "Speichern"), Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false, ToolTip = T("Writes the changed rules into formatting.json of the chosen level", "Schreibt die geänderten Regeln in die formatting.json der gewählten Ebene") };
            save.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.SystemHighlightBrushKey);
            save.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemHighlightTextBrushKey);
            var close = new Button { Content = T("Close", "Schließen"), Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            close.Click += (s, e) => w.Close();
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = false };
            DockPanel.SetDock(wish, Dock.Left); bar.Children.Add(wish);
            foreach (var x in new UIElement[] { close, save, undo }) { DockPanel.SetDock(x, Dock.Right); bar.Children.Add(x); }

            var body = new Grid { Margin = new Thickness(12, 0, 12, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(640), MinWidth = 420 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 300 });
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            root.Children.Add(body);
            w.Content = root;
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; } };

            w.Loaded += async (s, e) =>
            {
                Dictionary<string, object> state;
                try
                {
                    var rulesTask = Task.Run(() => FmtJson(FmtRun(exe, "format --show-rules --json" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), null)));
                    var exTask = Task.Run(() => { try { return FmtJson(FmtRun(exe, "format --examples --json", null)); } catch { return null; } });
                    state = await rulesTask;
                    state["__examples"] = await exTask;
                }
                catch (Exception ex) { FormatMessage(T("Formatter settings: the rules could not be read.", "Formatierer-Einstellungen: Die Regeln konnten nicht gelesen werden.") + "\n\n" + Inner(ex) + "\n\n" + T("TcAgent 0.54 or newer is needed.", "TcAgent 0.54 oder neuer wird gebraucht."), true); w.Close(); return; }
                progress.Visibility = Visibility.Collapsed;
                BuildSettings(w, state, body, search, hint, clear, profile, level, wish, undo, save, statusLeft, statusRight, progress, look, exe, sln, openDecl, openImpl, openName);
            };
            w.ShowDialog();
        }

        sealed class RuleRow
        {
            public string Key, Label, Description, Default, Values, From, Locked;
            public Dictionary<string, string> ToValue;
            public FrameworkElement Row;
            public ComboBox Box;
            public TextBlock State;
            public TextBlock Header;
        }

        void BuildSettings(Window w, Dictionary<string, object> state, Grid body, TextBox search, TextBlock hint, Button clear, ComboBox profile, ComboBox level,
                           Button wish, Button undo, Button save, TextBlock statusLeft, TextBlock statusRight, ProgressBar progress, EditorLook look,
                           string exe, string sln, string openDecl, string openImpl, string openName)
        {
            var rules = state["rules"] as Dictionary<string, object> ?? new Dictionary<string, object>();
            var profiles = state.TryGetValue("profiles", out var po) ? po as Dictionary<string, object> : null;
            var examples = state.TryGetValue("__examples", out var eo0) ? eo0 as Dictionary<string, object> : null;   // replaced by the preview with the rules of the window
            var valueLabels = state.TryGetValue("valueLabels", out var vl0) ? vl0 as Dictionary<string, object> : null;
            // a value in plain words (a number stays a number; 0 of the line length means never)
            string Disp(string key, string v)
            {
                if (v == null) return "";
                if (int.TryParse(v, out var num)) return num == 0 && key == "wrap.max-length" ? T("0 = never wrap", "0 = nie umbrechen") : v;
                return valueLabels != null && valueLabels.TryGetValue(v, out var o) && o is Dictionary<string, object> d2 ? FmtStr(d2, fmtGerman ? "de" : "en") ?? v : v;
            }
            var loaded = rules.ToDictionary(k => k.Key, k => FmtStr(k.Value as Dictionary<string, object>, "value") ?? "", StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            var rows = new List<RuleRow>();
            Action changed = null;
            RuleRow selected = null;

            // ---- left: the rules (group headings, Rule | Value | From), the details below
            var list = new Grid();
            // the value column as wide as the longest value in plain words, so every choice can be read in full
            double valueWidth = 120;
            try
            {
                var tf = new Typeface(w.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                foreach (var kv in rules)
                    foreach (var v in (FmtStr(kv.Value as Dictionary<string, object>, "values") ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "number"))
                    {
                        var ft = new FormattedText(Disp(kv.Key, v), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, w.FontSize, Brushes.Black, 1.0);
                        valueWidth = Math.Max(valueWidth, ft.WidthIncludingTrailingWhitespace + 40);
                    }
            }
            catch { valueWidth = 240; }
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Ceiling(valueWidth)) });
            list.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 });
            var selBrush = new SolidColorBrush(Color.FromArgb(70, 0x3B, 0x6F, 0xD8));
            var detail = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            var folded = new HashSet<TextBlock>();
            bool stacked = ReadStacked();
            var exampleTab = new TabItem { Header = T("Example of the rule", "Beispiel der Regel"), ToolTip = T("The selected rule alone: the code as written and the result for every value", "Die gewählte Regel allein: der Code, wie er geschrieben ist, und das Ergebnis für jeden Wert") };
            Action<RuleRow> showDetail = null;
            int r0 = 0;
            void AddRow(UIElement e, int col, int span = 1) { Grid.SetRow(e, r0); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span); list.Children.Add(e); }
            TextBlock header = null;
            string lastGroup = null;
            foreach (var kv in rules)
            {
                var d = kv.Value as Dictionary<string, object>;
                var rr = new RuleRow
                {
                    Key = kv.Key, Label = (fmtGerman ? FmtStr(d, "labelDe") : null) ?? FmtStr(d, "label") ?? kv.Key,
                    Description = (fmtGerman ? FmtStr(d, "descriptionDe") : null) ?? FmtStr(d, "description") ?? "", Default = FmtStr(d, "default"),
                    Values = FmtStr(d, "values") ?? "", From = FmtStr(d, "from"), Locked = FmtStr(d, "locked")
                };
                string group = kv.Key.Split('.')[0];
                if (group != lastGroup)
                {
                    list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    header = new TextBlock { Text = "▾ " + GroupName(group), Tag = GroupName(group), FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, r0 == 0 ? 2 : 10, 0, 2),
                                             Cursor = Cursors.Hand, ToolTip = T("Click to fold or unfold the rules of this group", "Klick klappt die Regeln dieser Gruppe ein oder aus"), Background = Brushes.Transparent };
                    var h0 = header;
                    h0.MouseLeftButtonDown += (s, e) => { if (!folded.Add(h0)) folded.Remove(h0); Filter(); e.Handled = true; };
                    AddRow(header, 0, 3); r0++;
                    lastGroup = group;
                }
                rr.Header = header;
                list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var back = new Border { Background = Brushes.Transparent };
                AddRow(back, 0, 3);
                var lab = new TextBlock { Text = rr.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 2, 6, 2), ToolTip = rr.Description, TextTrimming = TextTrimming.CharacterEllipsis };
                var values = rr.Values.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                bool number = values.Contains("number");
                var box = new ComboBox { IsEditable = number, Margin = new Thickness(0, 1, 6, 1), MinWidth = 100, ToolTip = rr.Description };
                box.SelectionChanged += (s2, e2) => { if (box.SelectedItem is string sv) box.ToolTip = sv + " – " + rr.Description; };
                var toValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in values.Where(v => v != "number")) { var dv = Disp(rr.Key, v); toValue[dv] = v; toValue[v] = v; box.Items.Add(dv); }
                rr.ToValue = toValue;
                box.Text = Disp(rr.Key, current[rr.Key]);
                if (!number) box.SelectedItem = Disp(rr.Key, current[rr.Key]);
                var st = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                rr.Box = box; rr.State = st;
                AddRow(lab, 0); AddRow(box, 1); AddRow(st, 2);
                rr.Row = back;
                var grp = new[] { (UIElement)back, lab, box, st };
                rr.Row.Tag = grp;
                if (rr.Locked != null) { box.IsEnabled = false; }
                void Select() { selected = rr; foreach (var x in rows) ((Border)x.Row).Background = Brushes.Transparent; back.Background = selBrush; showDetail(rr); }
                foreach (var el in new FrameworkElement[] { lab, st, back }) el.MouseLeftButtonDown += (s, e) => Select();
                box.GotKeyboardFocus += (s, e) => Select();
                void Pick()
                {
                    var shown = (box.IsEditable ? box.Text : box.SelectedItem as string ?? box.Text)?.Trim() ?? "";
                    var v = toValue.TryGetValue(shown, out var tv) ? tv : shown;
                    if (v.Length == 0 || v == current[rr.Key]) return;
                    if (box.IsEditable && !int.TryParse(v, out _) && !values.Contains(v))
                    {
                        box.Text = Disp(rr.Key, current[rr.Key]);
                        statusLeft.Text = $"{rr.Label}: " + T("a number or ", "eine Zahl oder ") + string.Join(", ", values.Where(x => x != "number").Select(x => Disp(rr.Key, x)));
                        return;
                    }
                    current[rr.Key] = v;
                    changed?.Invoke();
                }
                box.SelectionChanged += (s, e) => { if (!box.IsEditable) Pick(); else if (box.SelectedItem is string si) { box.Text = si; Pick(); } };
                box.LostFocus += (s, e) => Pick();
                box.KeyUp += (s, e) => { if (e.Key == Key.Enter) { Pick(); e.Handled = true; } };
                rows.Add(rr);
                r0++;
            }
            void States()
            {
                foreach (var rr in rows)
                {
                    bool ch = current[rr.Key] != loaded[rr.Key];
                    rr.State.Text = rr.Locked != null ? T("⊘ locked by ", "⊘ gesperrt von ") + FromName(rr.Locked) : ch ? T("✎ changed, not saved", "✎ geändert, nicht gespeichert") : FromName(rr.From);
                    rr.State.Foreground = ch ? new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00)) : Brushes.Gray;
                    rr.State.ToolTip = rr.Locked != null ? T("A level above locked this rule - it can only be changed there", "Eine Ebene darüber hat diese Regel gesperrt - sie kann nur dort geändert werden")
                                     : ch ? T("Changed in this window - Save writes it", "In diesem Fenster geändert - Speichern schreibt sie") : T("Comes from: ", "Kommt von: ") + FromName(rr.From);
                    var want = Disp(rr.Key, current[rr.Key]);
                    if ((rr.Box.IsEditable ? rr.Box.Text : rr.Box.SelectedItem as string) != want) { rr.Box.Text = want; if (!rr.Box.IsEditable) rr.Box.SelectedItem = want; }
                }
                int n = rows.Count(x => current[x.Key] != loaded[x.Key]);
                save.IsEnabled = n > 0;
                statusRight.Text = $"{rows.Count} " + T("rules", "Regeln") + $" · {rows.Count(x => x.Locked != null)} " + T("locked", "gesperrt");
                if (selected != null) showDetail(selected);
            }
            showDetail = rr =>
            {
                detail.Children.Clear();
                var name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                name.Inlines.Add(new Run(rr.Label));
                bool ch = current[rr.Key] != loaded[rr.Key];
                if (rr.Locked != null || ch)
                    name.Inlines.Add(new Run("   " + (rr.Locked != null ? T("⊘ locked by ", "⊘ gesperrt von ") + FromName(rr.Locked) : T("✎ changed, not saved", "✎ geändert, nicht gespeichert"))) { FontSize = 11, FontWeight = FontWeights.Normal, Foreground = ch ? new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00)) : Brushes.Gray });
                detail.Children.Add(name);
                var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                int i = 0;
                void Fact(string a, string b)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var ta = new TextBlock { Text = a, Opacity = 0.65, Margin = new Thickness(0, 1, 8, 1) }; var tb = new TextBlock { Text = b, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                    Grid.SetRow(ta, i); Grid.SetRow(tb, i); Grid.SetColumn(tb, 1); g.Children.Add(ta); g.Children.Add(tb); i++;
                }
                var lvName = (level.SelectedItem as ComboBoxItem)?.Content as string;
                Fact(T("Rule", "Regel"), rr.Key + " (formatting.json)");
                Fact(T("What it does", "Was sie tut"), rr.Description);
                Fact(T("Values", "Werte"), string.Join(" · ", rr.Values.Split('|').Select(x => x.Trim()).Select(x => x == "number" ? T("a number", "eine Zahl") : Disp(rr.Key, x))));
                Fact(T("Default", "Standard"), Disp(rr.Key, rr.Default));
                Fact(T("Now applies", "Gilt jetzt"), Disp(rr.Key, loaded[rr.Key]) + T(" – from ", " – aus ") + FromName(rr.From)
                     + (ch ? "; " + Disp(rr.Key, current[rr.Key]) + T(" after Save on level ", " nach dem Speichern in Ebene ") + lvName : ""));
                detail.Children.Add(g);
                // the tab "Example of the rule": the code as written and the result for every value, side by side
                var page = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var title = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                title.Inlines.Add(new Run(rr.Label) { FontWeight = FontWeights.SemiBold });
                DockPanel.SetDock(title, Dock.Top); page.Children.Add(title);
                if (examples != null && examples.TryGetValue(rr.Key, out var eo) && eo is Dictionary<string, object> ex)
                {
                    title.Inlines.Add(new Run(T(" – with the rules as set in this window, only this one changes", " – mit den Regeln, wie sie im Fenster eingestellt sind; nur diese wechselt") + (FmtStr(ex, "with") is string with && with.Length > 0 ? T(", shown with ", ", gezeigt mit ") + with : "")) { Foreground = Brushes.Gray });
                    var samples = new List<(string Title, ArrayList Tokens, bool Now)> { (T("Written as", "So geschrieben"), ex["code"] as ArrayList, false) };
                    foreach (var v in (ex["values"] as ArrayList ?? new ArrayList()).OfType<Dictionary<string, object>>())
                    {
                        var val = FmtStr(v, "value");
                        samples.Add((Disp(rr.Key, val) + (val == current[rr.Key] ? T("  ← set here", "  ← hier eingestellt") : ""), v["tokens"] as ArrayList, val == current[rr.Key]));
                    }
                    page.Children.Add(Arrange(samples.Select(x => (x.Title, (FrameworkElement)CodeView(look, x.Tokens ?? new ArrayList(), null), x.Now)).ToList(), stacked));
                }
                else page.Children.Add(new TextBlock { Text = T("This rule has no example: its effect cannot be seen in the text (tab width, line ends) or it works in the IDE.", "Diese Regel hat kein Beispiel: Ihre Wirkung ist im Text nicht sichtbar (Tab-Breite, Zeilenenden) oder sie wirkt in der IDE."), Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
                exampleTab.Content = page;
            };
            var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var listBorder = new Border { Child = listScroll, BorderThickness = new Thickness(1) };
            listBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var detailBorder = new Border
            {
                Child = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Height = 170
            };
            detailBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            detail.Children.Add(new TextBlock { Text = T("Select a rule to see what it does, its values and where it comes from.", "Wähle eine Regel, um zu sehen, was sie tut, welche Werte sie hat und woher sie kommt."), Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            var left = new DockPanel();
            DockPanel.SetDock(detailBorder, Dock.Bottom); left.Children.Add(detailBorder); left.Children.Add(listBorder);

            // ---- search
            void Filter()
            {
                var q = search.Text.Trim();
                hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                // a folded group shows only its heading; while searching, every match shows
                var match = rows.ToDictionary(rr => rr, rr => q.Length == 0 || (rr.Label + " " + rr.Key + " " + rr.Description + " " + Disp(rr.Key, current[rr.Key])).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var rr in rows)
                {
                    bool show = match[rr] && (q.Length > 0 || !folded.Contains(rr.Header));
                    foreach (UIElement el in (UIElement[])rr.Row.Tag) el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
                foreach (var h in rows.Select(x => x.Header).Distinct())
                {
                    bool any = rows.Any(x => x.Header == h && match[x]);
                    bool open = q.Length > 0 || !folded.Contains(h);
                    h.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                    int n = rows.Count(x => x.Header == h);
                    h.Text = (open ? "▾ " : "▸ ") + h.Tag + (open ? "" : $"  ({n})");
                }
            }
            search.TextChanged += (s, e) => Filter();
            clear.Click += (s, e) => { search.Text = ""; search.Focus(); };

            // ---- profile
            profile.Items.Add(T("(as it applies now)", "(wie jetzt gültig)"));
            if (profiles != null) foreach (var p in profiles.Keys) profile.Items.Add(p);
            profile.SelectedIndex = 0;
            profile.SelectionChanged += (s, e) =>
            {
                if (profile.SelectedIndex <= 0) { foreach (var k in loaded.Keys.ToList()) current[k] = loaded[k]; }
                else if (profiles?[profile.SelectedItem as string] is Dictionary<string, object> pv)
                    foreach (var rr in rows.Where(x => x.Locked == null)) if (pv.TryGetValue(rr.Key, out var v) && v != null) current[rr.Key] = v.ToString();
                changed?.Invoke();
            };

            // ---- right: the preview
            var tabs = new TabControl { Margin = new Thickness(0) };
            var declTab = new TabItem { Header = T("Declaration", "Deklaration") }; var implTab = new TabItem { Header = T("Implementation", "Implementierung") };
            tabs.Items.Add(declTab); tabs.Items.Add(implTab); tabs.Items.Add(exampleTab); tabs.SelectedIndex = 1;
            bool haveOpen = openImpl != null || openDecl != null;
            var useOpen = new CheckBox { Content = T("Preview the object open in the editor", "Vorschau mit dem Objekt im Editor") + (openName != null ? $" ({openName})" : ""), IsChecked = haveOpen, IsEnabled = haveOpen, VerticalAlignment = VerticalAlignment.Center };
            var marks = new TextBlock { Text = T("changed lines are marked", "geänderte Zeilen sind markiert"), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var layout = new Button { Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0) };
            void LayoutText()
            {
                layout.Content = stacked ? T("⇆ Side by side", "⇆ Nebeneinander") : T("⇅ One below the other", "⇅ Untereinander");
                layout.ToolTip = T("Show before/after and the examples ", "Vorher/Nachher und die Beispiele ") + (stacked ? T("side by side", "nebeneinander") : T("one below the other", "untereinander")) + T(" - the window remembers it", " zeigen - das Fenster merkt es sich");
            }
            LayoutText();
            var opts = new DockPanel { Margin = new Thickness(0, 6, 0, 4), LastChildFill = false };
            DockPanel.SetDock(useOpen, Dock.Left); opts.Children.Add(useOpen);
            DockPanel.SetDock(marks, Dock.Right); opts.Children.Add(marks); DockPanel.SetDock(layout, Dock.Right); opts.Children.Add(layout);
            var right = new DockPanel();
            DockPanel.SetDock(opts, Dock.Top);
            right.Children.Add(opts); right.Children.Add(tabs);
            string lastBeforeDecl = "", lastBeforeImpl = "", lastAfterDecl = "", lastAfterImpl = "";
            UIElement Pair(FrameworkElement a, FrameworkElement b) =>
                Arrange(new List<(string, FrameworkElement, bool)> { (T("Before", "Vorher"), a, false), (T("After", "Nachher"), b, false) }, stacked);
            Dictionary<string, object> lastDr = null, lastIr = null;
            bool haveResult = false;

            int version = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                int my = ++version;
                bool open = useOpen.IsChecked == true;
                string d = open ? openDecl ?? "" : SampleDeclaration, i = open ? openImpl ?? "" : SampleImplementation;
                var rulesFile = Path.Combine(Path.GetTempPath(), "tcagent-format-preview-" + Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(rulesFile, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["rules"] = current.ToDictionary(k => k.Key, k => (object)k.Value) }), new UTF8Encoding(false));
                progress.Visibility = Visibility.Visible;
                var sw = Stopwatch.StartNew();
                try
                {
                    var dt = Task.Run(() => d.Length == 0 ? null : FmtJson(FmtRun(exe, $"format --stdin --json --tokens --part declaration --rules \"{rulesFile}\"" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), d)));
                    var it = Task.Run(() => i.Length == 0 ? null : FmtJson(FmtRun(exe, $"format --stdin --json --tokens --part implementation --rules \"{rulesFile}\"" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), i)));
                    var et = Task.Run(() => { try { return FmtJson(FmtRun(exe, $"format --examples --json --rules \"{rulesFile}\"", null)); } catch { return null; } });
                    var dr = await dt; var ir = await it; var er = await et;
                    if (my == version && er != null) { examples = er; if (selected != null) showDetail(selected); }
                    if (my != version) return;
                    lastDr = dr; lastIr = ir; haveResult = true;
                    FillBoth();
                    lastBeforeDecl = d; lastBeforeImpl = i; lastAfterDecl = FmtStr(dr, "text") ?? d; lastAfterImpl = FmtStr(ir, "text") ?? i;
                    var errs = new[] { (T("Declaration", "Deklaration"), FmtStr(dr, "error")), (T("Implementation", "Implementierung"), FmtStr(ir, "error")) }.Where(x => !string.IsNullOrEmpty(x.Item2)).Select(x => x.Item1 + ": " + x.Item2).ToList();
                    int n = current.Count(k => k.Value != loaded[k.Key]);
                    statusLeft.Text = errs.Count > 0 ? string.Join("  ·  ", errs) : n == 0 ? T("The preview shows the rules as they apply now.", "Die Vorschau zeigt die Regeln, wie sie jetzt gelten.")
                                    : n + T(n == 1 ? " rule changed – not saved" : " rules changed – not saved", n == 1 ? " Regel geändert – nicht gespeichert" : " Regeln geändert – nicht gespeichert");
                    statusRight.Text = $"{rows.Count} " + T("rules", "Regeln") + $" · {rows.Count(x => x.Locked != null)} " + T("locked", "gesperrt") + T(" · preview ", " · Vorschau ") + $"{sw.Elapsed.TotalSeconds:0.0} s";
                }
                catch (Exception ex) { if (my == version) FormatMessage(T("The preview could not be formatted:", "Die Vorschau konnte nicht formatiert werden:") + "\n\n" + Inner(ex), true); }
                finally { if (my == version) progress.Visibility = Visibility.Collapsed; }
            };
            void Fill(TabItem tab, Dictionary<string, object> res)
            {
                if (res == null) { tab.Content = new TextBlock { Text = T("This object has no " + tab.Header.ToString().ToLowerInvariant() + ".", "Dieses Objekt hat keine " + tab.Header + "."), Opacity = 0.65, Margin = new Thickness(6) }; return; }
                var bt = res["inputTokens"] as ArrayList ?? new ArrayList(); var at = res["tokens"] as ArrayList ?? new ArrayList();
                tab.Content = Pair(CodeView(look, bt, null), CodeView(look, at, Changed(LinesOf(bt), LinesOf(at))));
            }
            void FillBoth() { Fill(declTab, lastDr); Fill(implTab, lastIr); }
            layout.Click += (s, e) =>
            {
                stacked = !stacked; WriteStacked(stacked); LayoutText();
                if (haveResult) FillBoth();
                if (selected != null) showDetail(selected);
            };
            changed = () => { States(); timer.Stop(); timer.Start(); };
            useOpen.Checked += (s, e) => changed(); useOpen.Unchecked += (s, e) => changed();
            level.SelectionChanged += (s, e) => { if (selected != null) showDetail(selected); };

            // ---- buttons
            wish.Click += (s, e) =>
            {
                bool decl = tabs.SelectedIndex == 0;
                WishFormatterRule(decl ? lastBeforeDecl : lastBeforeImpl, decl ? lastAfterDecl : lastAfterImpl,
                                  rows.Where(x => current[x.Key] != x.Default).Select(x => x.Key + " = " + current[x.Key]).ToList());
            };
            undo.Click += (s, e) => { foreach (var k in loaded.Keys.ToList()) current[k] = loaded[k]; profile.SelectedIndex = 0; changed(); };

            // ---- export and import: a formatting.json to pass on, or one to take over into the window
            var export = new Button { Content = T("Export …", "Exportieren …"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0),
                                      ToolTip = T("Saves the rules as they are set in this window as a formatting.json file - for a colleague, a team or another project",
                                                  "Speichert die Regeln, wie sie in diesem Fenster eingestellt sind, als formatting.json - für Kollegen, ein Team oder ein anderes Projekt") };
            var import = new Button { Content = T("Import …", "Importieren …"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0),
                                      ToolTip = T("Takes the rules of a formatting.json into this window (locked rules stay); Save writes them into the chosen level",
                                                  "Übernimmt die Regeln einer formatting.json in dieses Fenster (gesperrte Regeln bleiben); Speichern schreibt sie in die gewählte Ebene") };
            if (wish.Parent is DockPanel wbar)
            {
                DockPanel.SetDock(export, Dock.Left); DockPanel.SetDock(import, Dock.Left);
                wbar.Children.Insert(wbar.Children.IndexOf(wish) + 1, export);
                wbar.Children.Insert(wbar.Children.IndexOf(export) + 1, import);
            }
            export.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "formatting.json", Filter = "formatting.json|*.json", Title = T("Export the formatter rules", "Formatierer-Regeln exportieren") };
                if (dlg.ShowDialog(w) != true) return;
                try
                {
                    var all = rows.ToDictionary(x => x.Key, x => (object)(int.TryParse(current[x.Key], out var n) ? (object)n : current[x.Key] == "true" ? (object)true : current[x.Key] == "false" ? (object)false : current[x.Key]));
                    var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["rules"] = all });
                    // one rule per line, readable and easy to compare
                    json = json.Replace("{\"rules\":{", "{\n  \"rules\": {\n    ").Replace(",\"", ",\n    \"").Replace("}}", "\n  }\n}\n");
                    File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                    statusLeft.Text = "✓ " + T("Exported: ", "Exportiert: ") + dlg.FileName;
                }
                catch (Exception ex) { FormatMessage(T("The rules were not exported:", "Die Regeln wurden nicht exportiert:") + "\n\n" + ex.Message, true); }
            };
            import.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "formatting.json|*.json|*.*|*.*", Title = T("Import formatter rules", "Formatierer-Regeln importieren") };
                if (dlg.ShowDialog(w) != true) return;
                try
                {
                    var doc = FmtJson(File.ReadAllText(dlg.FileName));
                    int taken = 0, kept = 0;
                    var unknown = new List<string>();
                    // a profile first, then the single rules - as on a knowledge level
                    if (FmtStr(doc, "profile") is string prof && profiles?.TryGetValue(prof, out var pobj) == true && pobj is Dictionary<string, object> pv)
                        foreach (var rr in rows) if (pv.TryGetValue(rr.Key, out var v) && v != null) { if (rr.Locked != null) kept++; else { current[rr.Key] = v.ToString(); taken++; } }
                    if (doc.TryGetValue("rules", out var ro) && ro is Dictionary<string, object> rules2)
                        foreach (var kv in rules2)
                        {
                            var rr = rows.FirstOrDefault(x => x.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                            if (rr == null) { unknown.Add(kv.Key); continue; }
                            if (rr.Locked != null) { kept++; continue; }
                            var v = kv.Value is bool bv ? (bv ? "true" : "false") : kv.Value?.ToString() ?? "";
                            if (v == "unset") v = rr.Default;
                            current[rr.Key] = v; taken++;
                        }
                    profile.SelectedIndex = 0;
                    changed();
                    statusLeft.Text = "✓ " + T($"{taken} rule(s) taken over", $"{taken} Regel(n) übernommen") + (kept > 0 ? T($", {kept} locked ones kept", $", {kept} gesperrte behalten") : "")
                                      + T(" – Save writes them into the chosen level", " – Speichern schreibt sie in die gewählte Ebene");
                    if (unknown.Count > 0) FormatMessage(T("Unknown rules in the file, not taken over:", "Unbekannte Regeln in der Datei, nicht übernommen:") + "\n\n" + string.Join("\n", unknown), true);
                }
                catch (Exception ex) { FormatMessage(T("The file could not be read:", "Die Datei konnte nicht gelesen werden:") + "\n\n" + ex.Message, true); }
            };
            save.Click += async (s, e) =>
            {
                var diff = current.Where(k => k.Value != loaded[k.Key]).ToDictionary(k => k.Key, k => (object)k.Value);
                if (diff.Count == 0) return;
                var lv = (level.SelectedItem as ComboBoxItem)?.Tag as string ?? "project";
                var file = Path.Combine(Path.GetTempPath(), "tcagent-format-save-" + Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(file, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["rules"] = diff }), new UTF8Encoding(false));
                save.IsEnabled = false; progress.Visibility = Visibility.Visible; statusLeft.Text = T("Saving …", "Speichere …");
                try
                {
                    var res = await Task.Run(() => FmtRun(exe, $"format --save \"{file}\" --level {lv}" + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\""), null));
                    foreach (var k in diff.Keys) { loaded[k] = current[k]; var rr = rows.First(x => x.Key == k); rr.From = lv; }
                    States();
                    statusLeft.Text = "✓ " + diff.Count + T(diff.Count == 1 ? " rule saved on level " : " rules saved on level ", diff.Count == 1 ? " Regel gespeichert in Ebene " : " Regeln gespeichert in Ebene ")
                                      + LevelName(lv) + T(" – Format ST code uses them from now on", " – ST-Code formatieren nutzt sie ab jetzt");
                }
                catch (Exception ex) { save.IsEnabled = true; statusLeft.Text = T("Not saved.", "Nicht gespeichert."); FormatMessage(T("The rules were not saved:", "Die Regeln wurden nicht gespeichert:") + "\n\n" + Inner(ex), true); }
                finally { progress.Visibility = Visibility.Collapsed; }
            };

            var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
            body.Children.Add(left); body.Children.Add(split); body.Children.Add(right);
            States();
            Filter();
            if (rows.Count > 0) { selected = rows[0]; ((Border)rows[0].Row).Background = selBrush; showDetail(rows[0]); }
            changed();
        }

        static string GroupName(string g) => g switch
        {
            "indent" => T("Indentation", "Einrückung"), "case" => T("Case", "Schreibweise"), "space" => T("Spaces", "Leerzeichen"), "comment" => T("Comments", "Kommentare"),
            "layout" => "Layout", "align" => T("Columns", "Spalten"), "wrap" => T("Long lines", "Lange Zeilen"), "blank" => T("Blank lines", "Leerzeilen"),
            "eol" => T("Line ends", "Zeilenenden"), "paren" => T("Brackets in conditions", "Klammern in Bedingungen"), "cond" => T("Conditions", "Bedingungen"), _ => g
        };

        static string FmtStr(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;

        static Dictionary<string, object> FmtJson(string s) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(s);

        /// <summary>TcAgent.exe with arguments and optionally text on stdin; the output, or an exception with the error text.</summary>
        static string FmtRun(string exe, string args, string input)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(psi))
            {
                if (input != null)
                {
                    var bytes = new UTF8Encoding(false).GetBytes(input);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                }
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new Exception("TcAgent.exe did not answer within 30 s"); }
                if (string.IsNullOrWhiteSpace(o.Result) || p.ExitCode == 2) throw new Exception((e.Result + o.Result).Trim());
                return o.Result;
            }
        }

        /// <summary>A new issue on GitHub: the wished rule or feature with the example of the preview. The person checks and sends it.</summary>
        void WishFormatterRule(string before, string after, List<string> settings)
        {
            string Cut(string s, int n) { s = (s ?? "").Replace("\r\n", "\n").TrimEnd(); return s.Length <= n ? s : s.Substring(0, n) + "\n..."; }
            var body = new StringBuilder();
            body.AppendLine("### Rule or feature I wish for the ST formatter").AppendLine().AppendLine("(What should the formatter do? Which construct?)").AppendLine();
            body.AppendLine("### Example").AppendLine().AppendLine("Code (as written):").AppendLine("```").AppendLine(Cut(before, 1200)).AppendLine("```").AppendLine();
            body.AppendLine("The formatter makes of it today:").AppendLine("```").AppendLine(Cut(after, 1200)).AppendLine("```").AppendLine();
            body.AppendLine("What I would like instead:").AppendLine("```").AppendLine().AppendLine("```").AppendLine();
            body.AppendLine("### Context").AppendLine().AppendLine($"TcAgent {BridgeVersion}" + (settings.Count > 0 ? "; rules other than the default: " + string.Join(", ", settings) : "; default rules"));
            body.AppendLine().AppendLine("Please remove names of customers, plants or projects from the example before you send it.");
            const string url = "https://github.com/ThorstenBrach/TcAgent/issues/new";
            var full = url + "?labels=" + Uri.EscapeDataString("enhancement,formatter") + "&title=" + Uri.EscapeDataString("Formatter: ") + "&body=" + Uri.EscapeDataString(body.ToString());
            bool tooLong = full.Length > 7500;
            if (tooLong) { try { System.Windows.Forms.Clipboard.SetText(body.ToString()); } catch { } full = url + "?labels=" + Uri.EscapeDataString("enhancement,formatter") + "&title=" + Uri.EscapeDataString("Formatter: "); }
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
            if (MessageBox.Show(owner, T("Open a new issue on GitHub in the browser?\n\nIt contains the example of the preview - check it there, remove names of customers or plants, and send it yourself.",
                                        "Ein neues Issue auf GitHub im Browser öffnen?\n\nEs enthält das Beispiel der Vorschau - prüfe es dort, entferne Namen von Kunden oder Anlagen und schicke es selbst ab.") +
                                (tooLong ? T("\n\nThe text is long: it is in the clipboard - paste it into the issue.", "\n\nDer Text ist lang: Er ist in der Zwischenablage - füge ihn ins Issue ein.") : ""),
                                T("Wish a rule or feature", "Regel oder Funktion wünschen"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
            catch (Exception ex) { FormatMessage(T("Could not open the browser: ", "Der Browser konnte nicht geöffnet werden: ") + ex.Message, true); }
        }
    }
}
