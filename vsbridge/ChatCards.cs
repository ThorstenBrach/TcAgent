// TcAgent Chat - questions of the agent as cards in the conversation instead of separate dialog windows. Every card says
// what it is about: Goal (what the agent wants to do), Consequence (what happens on yes), Gate (why it asks) - and has one
// primary button (focused: Enter) and Esc for "no":
//   permission card   Claude Code asks before a tool call its rules do not allow (allow once / for this session / deny)
//   question card     the agent asks with AskUserQuestion or tc_ask_user (options, several if allowed, or a free answer)
//   gate card         TcAgent asks before a runtime action (activate, PLC start/stop, ADS write - TCAGENT_CONFIRM)
// The reader thread of the session waits until the card is answered; New chat / Stop / end of the session answer an
// open card with "deny" / "not answered". After the answer the card stays in the conversation, disabled, with the decision.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        static readonly Brush CardBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
        static readonly Brush CardBack = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x78, 0xD4)));

        // cancel actions of the open cards (New chat, Stop, end of the session)
        readonly List<Action> openCards = new List<Action>();

        void CancelOpenCards()
        {
            List<Action> all;
            lock (openCards) { all = openCards.ToList(); openCards.Clear(); }
            foreach (var a in all) a();
        }

        // Shows a card (UI thread) and waits on the calling thread (the reader of the session) for its result.
        // build gets "done": call it with the result on a click; cancelled = result if the card is closed from outside.
        T WaitForCard<T>(Func<Action<T>, Panel, FrameworkElement> build, T cancelled)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            FrameworkElement card = null;
            Panel cardBody = null;
            void Close(T result)
            {
                if (!tcs.TrySetResult(result)) return;
                Dispatcher.BeginInvoke(new Action(() => { if (card != null) card.IsEnabled = false; }));
            }
            Action cancel = () =>
            {
                if (tcs.Task.IsCompleted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // the buttons go, the reason stays
                    foreach (var w in cardBody?.Children.OfType<WrapPanel>().ToList() ?? new List<WrapPanel>()) w.Visibility = Visibility.Collapsed;
                    cardBody?.Children.Add(CardText("✖ Not answered (the chat was stopped or started anew)", 11.5, 0.8));
                }));
                Close(cancelled);
            };
            lock (openCards) openCards.Add(cancel);
            Dispatcher.Invoke(new Action(() =>
            {
                FlushAgent(); cur = null;          // the card comes after the text so far; later text starts a new bubble
                var body = new StackPanel();
                cardBody = body;
                card = build(Close, body);
                AddMessage(card);
                scroller.ScrollToEnd();
                var shown = card;
                shown.Dispatcher.BeginInvoke(new Action(() => { scroller.ScrollToEnd(); shown.BringIntoView(); }), System.Windows.Threading.DispatcherPriority.Loaded);
                status.Text = "Waiting for your answer in the chat …";
                // the primary button gets the focus: Enter answers, Esc says no
                var primary = body.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Button>()).FirstOrDefault(b => b.Tag as string == "primary");
                card.Dispatcher.BeginInvoke(new Action(() => primary?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            }));
            var value = tcs.Task.Result;
            lock (openCards) openCards.Remove(cancel);
            return value;
        }

        // Esc on the card = the "no" button (Tag "no")
        Border Card(string title, Panel body)
        {
            body.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Escape) return;
                var no = body.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Button>()).FirstOrDefault(b => b.Tag as string == "no");
                if (no != null && no.IsVisible) { no.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled = true; }
            };
            var head = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            head.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            body.Children.Insert(0, head);
            return new Border
            {
                Tag = "card", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 40, 6), Padding = new Thickness(10, 8, 10, 10),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = CardBorder, Background = CardBack, Child = body
            };
        }

        TextBlock CardText(string text, double size = 12.5, double opacity = 1, bool mono = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Opacity = opacity, Margin = new Thickness(0, 1, 0, 1) };
            if (mono) t.FontFamily = new FontFamily("Consolas");
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        // the primary button of a card: filled in the accent color; "no" is the one Esc presses
        static Button CardButton(string label, bool primary = false, bool no = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), MinWidth = 70, Tag = primary ? "primary" : no ? "no" : null };
            if (primary) { b.FontWeight = FontWeights.SemiBold; b.Background = CardBorder; b.Foreground = Brushes.White; b.BorderBrush = CardBorder; }
            if (no) b.ToolTip = "Esc";
            return b;
        }

        // Goal / Consequence / Gate: what the card is about, in three short lines
        void Facts(Panel body, params (string Label, string Text)[] facts)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var (label, text) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Text)))
            {
                var r = g.RowDefinitions.Count;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = CardText(label, 11.5, 0.7); k.Margin = new Thickness(0, 1, 10, 1);
                var v = CardText(text, 12);
                Grid.SetRow(k, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                g.Children.Add(k); g.Children.Add(v);
            }
            body.Children.Add(g);
        }

        // what a tool does if it is allowed - in words
        static string Consequence(string tool)
        {
            var t = (tool ?? "").ToLowerInvariant();
            if (t is "read" or "glob" or "grep" or "ls" || t.StartsWith("tc_plc_read") || t.EndsWith("_read")) return "Reads only - nothing is changed.";
            if (t is "write" or "edit" or "multiedit" or "notebookedit") return "Changes a file on this PC.";
            if (t == "bash" || t.Contains("powershell") || t.Contains("shell")) return "Runs a command on this PC.";
            if (t.StartsWith("ads_write") || t.Contains("activate") || t.Contains("plc_state") || t.Contains("plc_online") || t.Contains("runtime")) return "Acts on the running PLC or runtime.";
            if (t.StartsWith("tc_")) return "Changes the TwinCAT project.";
            if (t.StartsWith("web")) return "Reads from the internet.";
            return "Uses a tool your rules do not allow yet.";
        }

        // ------------------------------------------------------------------ permission card
        // Returns once, session or deny.
        string AskPermission(string tool, string what, string why, Dictionary<string, object> input, string sessionRule, string agent = "Claude") =>
            WaitForCard<string>((done, body) =>
            {
                Facts(body, ("Goal", $"{ShortName(agent)} wants to use {tool}" + (what != null ? ":" : ".")),
                            ("Consequence", Consequence(tool)),
                            ("Gate", why ?? $"The permission rules of {ShortName(agent)} do not cover this call."));
                if (what != null) body.Children.Add(CardText(what, 12, 1, true));
                if (what == null && input != null)
                    foreach (var kv in input.Take(6)) body.Children.Add(CardText($"{kv.Key} = {Short(Convert.ToString(kv.Value), 200)}", 12, 1, true));
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var result = CardText("", 11.5, 0.8);
                void Pick(string key, string text) { result.Text = text; buttons.Visibility = Visibility.Collapsed; done(key); }
                var once = CardButton("Allow once", true);
                once.Click += (s, e) => Pick("once", "✔ Allowed once");
                buttons.Children.Add(once);
                if (sessionRule != null)
                {
                    var session = CardButton("Allow for this session");
                    session.ToolTip = "Allows " + sessionRule + " until the chat session ends (New chat)";
                    session.Click += (s, e) => Pick("session", "✔ Allowed for this session: " + sessionRule);
                    buttons.Children.Add(session);
                }
                var deny = CardButton("Deny", no: true);
                deny.Click += (s, e) => Pick("deny", "✖ Denied");
                buttons.Children.Add(deny);
                body.Children.Add(buttons);
                body.Children.Add(result);
                return Card("\U0001F512 Permission", body);
            }, "deny");

        // ------------------------------------------------------------------ gate card
        /// <summary>TcAgent asks before a runtime action (TCAGENT_CONFIRM): the card in the chat instead of a Yes/No box. Call from a
        /// background thread. true = execute.
        /// Safe by default: the focus is on "Don't execute" (Enter and Esc cancel); Execute reacts only to a mouse click on it -
        /// not to Enter, Space, Tab or UI automation (an agent driving the IDE cannot confirm it).</summary>
        internal bool AskGate(string title, string text, string target = null, bool local = true) =>
            WaitForCard<bool>((done, body) =>
            {
                var tool = Regex.Match(title ?? "", @"(tc_\w+|ads_\w+)").Value;
                // the goal with the target in bold
                var goal = CardText("", 12.5);
                var what = text ?? title ?? "";
                var at = string.IsNullOrEmpty(target) ? -1 : what.IndexOf(target, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    goal.Inlines.Add(new System.Windows.Documents.Run(what.Substring(0, at)));
                    goal.Inlines.Add(new System.Windows.Documents.Run(what.Substring(at, target.Length)) { FontWeight = FontWeights.Bold });
                    goal.Inlines.Add(new System.Windows.Documents.Run(what.Substring(at + target.Length)));
                }
                else goal.Inlines.Add(new System.Windows.Documents.Run(what));
                goal.Inlines.Add(new System.Windows.Documents.LineBreak());
                goal.Inlines.Add(new System.Windows.Documents.Run("Target: ") { FontSize = 11.5 });
                goal.Inlines.Add(new System.Windows.Documents.Run(local ? (string.IsNullOrEmpty(target) ? "this PC (local runtime)" : target + " (this PC)") : target)
                                     { FontWeight = FontWeights.Bold });
                body.Children.Add(goal);
                if (!local)
                {
                    var warn = CardText($"⚠ The target {target} is NOT this PC - this acts on a remote PLC and the machine it controls.", 12.5);
                    warn.Foreground = WarnBrush; warn.FontWeight = FontWeights.SemiBold;   // a warning, not an error: orange warn.Margin = new Thickness(0, 4, 0, 4);
                    body.Children.Add(warn);
                }
                Facts(body, ("Consequence", Consequence(tool) + (tool.Length > 0 ? $" ({tool})" : "")),
                            ("Gate", "TcAgent asks before runtime actions (TCAGENT_CONFIRM). Only a mouse click on Execute confirms - Enter and Esc " +
                                     "cancel; no answer within 15 minutes is a no."));
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var result = CardText("", 11.5, 0.8);
                void Pick(bool ok) { result.Text = ok ? "✔ Executed (confirmed by mouse click)" : "✖ Not executed"; buttons.Visibility = Visibility.Collapsed; done(ok); }
                // Execute: never the default, no focus, no Click event (keyboard and UI automation raise Click) - a pressed and
                // released left mouse button over it only
                var go = CardButton("Execute", true);
                go.Tag = null; go.Focusable = false; go.IsTabStop = false;
                go.ToolTip = "Confirm with a mouse click";
                bool pressed = false;
                go.PreviewMouseLeftButtonDown += (s, e) => { pressed = true; e.Handled = true; };
                go.PreviewMouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (pressed && go.IsMouseOver) Pick(true);
                    pressed = false;
                };
                go.MouseLeave += (s, e) => pressed = false;
                var no = CardButton("Don't execute", no: true);
                no.Tag = "primary";      // gets the focus: Enter cancels
                no.Click += (s, e) => Pick(false);
                buttons.Children.Add(go); buttons.Children.Add(no);
                body.Children.Add(buttons);
                body.Children.Add(result);
                body.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Escape && buttons.IsVisible) { Pick(false); e.Handled = true; }
                };
                return Card("⛔ Confirm - " + (tool.Length > 0 ? tool : "runtime action"), body);
            }, false);

        /// <summary>Question of the TcAgent tool tc_ask_user (any AI client): the same card. Call from a background thread.</summary>
        internal Dictionary<string, object> AskFromTool(Dictionary<string, object> input) => AskQuestions(input);

        // ------------------------------------------------------------------ question card
        // Questions of AskUserQuestion: {"questions":[{"question","header","options":[{"label","description"}],"multiSelect"}]}.
        // Returns question -> answer (selected labels separated by ", ", plus the free text), or null if skipped.
        Dictionary<string, object> AskQuestions(Dictionary<string, object> input)
        {
            var questions = (input.TryGetValue("questions", out var qs) ? qs as IEnumerable : null)?.OfType<Dictionary<string, object>>().ToList()
                            ?? new List<Dictionary<string, object>>();
            if (questions.Count == 0) return null;
            return WaitForCard<Dictionary<string, object>>((done, body) =>
            {
                var readers = new List<(string question, Func<string> answer)>();
                var answer = CardButton("Answer", true);
                void Check() => answer.IsEnabled = readers.All(r => r.answer().Length > 0);
                var goal = questions.Count == 1 ? questions[0].TryGetValue("header", out var h0) ? h0 as string : null : null;
                Facts(body, ("Goal", goal ?? "The agent needs a decision to go on."),
                            ("Consequence", "The agent continues with your answer."),
                            ("Gate", "Nothing changes until you answer - Skip lets the agent go on without it."));
                int n = 0;
                foreach (var q in questions)
                {
                    var text = q.TryGetValue("question", out var qt) ? qt as string : "?";
                    var header = q.TryGetValue("header", out var hd) ? hd as string : null;
                    bool multi = q.TryGetValue("multiSelect", out var ms) && ms is bool mb && mb;
                    if (n++ > 0) body.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6), Opacity = 0.4 });
                    if (!string.IsNullOrEmpty(header) && questions.Count > 1) body.Children.Add(CardText(header, 11, 0.7));
                    var qtb = CardText(text, 13);
                    qtb.FontWeight = FontWeights.SemiBold;
                    body.Children.Add(qtb);
                    var picks = new List<(ToggleButton box, string label)>();
                    var group = "q" + Guid.NewGuid().ToString("N");
                    foreach (var o in (q.TryGetValue("options", out var op) ? op as IEnumerable : null)?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                    {
                        var label = o.TryGetValue("label", out var lb) ? lb as string : "";
                        var desc = o.TryGetValue("description", out var ds) ? ds as string : null;
                        var content = new StackPanel();
                        content.Children.Add(CardText(label));
                        if (!string.IsNullOrEmpty(desc)) content.Children.Add(CardText(desc, 11.5, 0.7));
                        ToggleButton box = multi ? (ToggleButton)new CheckBox { Content = content } : new RadioButton { Content = content, GroupName = group };
                        box.Margin = new Thickness(0, 3, 0, 1);
                        box.Click += (s, e) => Check();
                        picks.Add((box, label));
                        body.Children.Add(box);
                    }
                    var free = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 22, Margin = new Thickness(0, 6, 0, 0),
                                             ToolTip = picks.Count > 0 ? "Other answer (free text)" : "Answer" };
                    free.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                    free.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    free.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    var freeLabel = CardText(picks.Count > 0 ? "Other answer:" : "Answer:", 11.5, 0.7);
                    freeLabel.Margin = new Thickness(0, 6, 0, 0);
                    body.Children.Add(freeLabel);
                    body.Children.Add(free);
                    // a free answer to a single choice question replaces the picked option
                    if (!multi) free.TextChanged += (s, e) => { if (free.Text.Trim().Length > 0) foreach (var p in picks) p.box.IsChecked = false; };
                    free.TextChanged += (s, e) => Check();
                    if (!multi) foreach (var p in picks) p.box.Checked += (s, e) => free.Text = "";
                    readers.Add((text, () => string.Join(", ", picks.Where(p => p.box.IsChecked == true).Select(p => p.label)
                                                              .Concat(free.Text.Trim().Length > 0 ? new[] { free.Text.Trim() } : new string[0]))));
                }
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var result = CardText("", 11.5, 0.8);
                var skip = CardButton("Skip", no: true);
                Check();
                answer.Click += (s, e) =>
                {
                    var a = readers.ToDictionary(r => r.question, r => (object)r.answer());
                    if (a.Values.Any(v => ((string)v).Length == 0)) { result.Text = "Please answer every question."; return; }
                    result.Text = "✔ " + string.Join("; ", a.Values);
                    buttons.Visibility = Visibility.Collapsed;
                    done(a);
                };
                skip.Click += (s, e) => { result.Text = "✖ Not answered"; buttons.Visibility = Visibility.Collapsed; done(null); };
                buttons.Children.Add(answer); buttons.Children.Add(skip);
                body.Children.Add(buttons);
                body.Children.Add(result);
                return Card("❓ Question", body);
            }, null);
        }
    }
}
