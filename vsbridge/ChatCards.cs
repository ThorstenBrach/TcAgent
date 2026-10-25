// TcAgent Chat - questions of the agent as cards in the conversation instead of separate dialog windows:
//   permission card   Claude Code asks before a tool call its rules do not allow (allow once / for this session / deny)
//   question card     the agent asks with AskUserQuestion (options to click, several if allowed, or a free answer)
// The reader thread of the session waits until the card is answered; New chat / Stop / end of the session answer an
// open card with "deny" / "not answered". After the answer the card stays in the conversation, disabled, with the decision.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
            void Close(T result)
            {
                if (!tcs.TrySetResult(result)) return;
                Dispatcher.BeginInvoke(new Action(() => { if (card != null) card.IsEnabled = false; }));
            }
            Action cancel = () => Close(cancelled);
            lock (openCards) openCards.Add(cancel);
            Dispatcher.Invoke(new Action(() =>
            {
                FlushAgent(); cur = null;          // the card comes after the text so far; later text starts a new bubble
                var body = new StackPanel();
                card = build(Close, body);
                AddMessage(card);
                scroller.ScrollToEnd();
                status.Text = "Waiting for your answer in the chat ...";
            }));
            var value = tcs.Task.Result;
            lock (openCards) openCards.Remove(cancel);
            return value;
        }

        Border Card(string title, Panel body)
        {
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

        static Button CardButton(string label, bool primary = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), MinWidth = 70 };
            if (primary) b.FontWeight = FontWeights.SemiBold;
            return b;
        }

        // ------------------------------------------------------------------ permission card
        // Returns once, session or deny.
        string AskPermission(string tool, string what, string why, Dictionary<string, object> input, string sessionRule) =>
            WaitForCard<string>((done, body) =>
            {
                body.Children.Add(CardText($"Claude wants to use {tool}" + (what != null ? ":" : ".")));
                if (what != null) body.Children.Add(CardText(what, 12, 1, true));
                if (what == null && input != null)
                    foreach (var kv in input.Take(6)) body.Children.Add(CardText($"{kv.Key} = {Short(Convert.ToString(kv.Value), 200)}", 12, 1, true));
                if (why != null) body.Children.Add(CardText(why, 11.5, 0.7));
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
                var deny = CardButton("Deny");
                deny.Click += (s, e) => Pick("deny", "✖ Denied");
                buttons.Children.Add(deny);
                body.Children.Add(buttons);
                body.Children.Add(result);
                return Card("\U0001F512 Permission", body);
            }, "deny");

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
                    if (!multi) foreach (var p in picks) p.box.Checked += (s, e) => free.Text = "";
                    readers.Add((text, () => string.Join(", ", picks.Where(p => p.box.IsChecked == true).Select(p => p.label)
                                                              .Concat(free.Text.Trim().Length > 0 ? new[] { free.Text.Trim() } : new string[0]))));
                }
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var result = CardText("", 11.5, 0.8);
                var answer = CardButton("Answer", true);
                var skip = CardButton("Skip");
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
