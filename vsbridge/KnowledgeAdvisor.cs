// TcAgent Knowledge - tab Advisor: the advice of the project (server: Advisor.cs, CliAdvisor.cs; docs/ADVISOR.md).
//
//   list     newest first: time, kind and trigger, the finding, the confidence, the rating
//   details  finding, evidence, proposal, the order (the prompt for an agent) and a diff if there is one
//   rate     Right / Partly / Wrong / n/a - one click, stored with the name of the person (TcAgent.exe advise rate)
//   act      "Act on it …" opens the TcAgent Chat with the order in the input - the person reads it, changes it and sends it;
//            the Advisor itself never changes anything. The advice is then marked "sent"; the agent that did the work marks it
//            done (tc_advisor action=done), or the person does (Mark as done).
//   delete   removes an advice from the list (Delete …, after a question) - it stays in the Git history of the project
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    public sealed partial class KnowledgeControl
    {
        readonly ListView adviceList = NewList();
        readonly ComboBox adviceShow = new ComboBox { MinWidth = 110, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                                                      ItemsSource = new[] { "open", "not rated", "done", "all" }, SelectedIndex = 0 };
        readonly TextBox adviceText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
                                                    BorderThickness = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        readonly TextBlock adviceHeading = Heading("");
        List<Dictionary<string, object>> advice = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> adviceStats = new List<Dictionary<string, object>>();
        FrameworkElement advisorPage;

        Dictionary<string, object> SelectedAdvice => (adviceList.SelectedItem as Line)?.D;

        FrameworkElement AdvisorPage()
        {
            if (advisorPage != null) return advisorPage;
            bool Sel() => SelectedAdvice != null;
            var acts = new[] {
                A("Act on it …", "Opens the TcAgent Chat with the order of the selected advice - read it, change it, send it", ActOnAdvice, Sel),
                A("Mark as done", "The finding is dealt with", () => MarkAdvice("done"), () => Sel() && S(SelectedAdvice, "state") != "done"),
                A("Delete …", "Removes the selected advice from the list (it stays in the Git history of the project)", DeleteAdvice, Sel, "Del"),
                Sep,
                A("Right", "The advice is right", () => RateAdvice("right"), Sel),
                A("Partly", "The advice is partly right", () => RateAdvice("partly"), Sel),
                A("Wrong", "The advice is wrong - say why in the comment, it becomes a proposal for tc_improve", () => RateAdvice("wrong"), Sel),
                A("n/a", "Cannot be rated", () => RateAdvice("n/a"), Sel),
                Sep,
                A("Copy order", "Copy the order (the prompt for an agent)", () => { try { Clipboard.SetText(S(SelectedAdvice, "order") ?? ""); status.Text = "Copied."; } catch (Exception ex) { Error(ex.Message); } }, Sel),
                A("Refresh", "Read the advice again", LoadAdvice) };
            Columns(adviceList, ("Time", 120, nameof(Line.C1)), ("Kind", 130, nameof(Line.C2)), ("Finding", 420, nameof(Line.C3)), ("Confidence", 90, nameof(Line.C4)),
                    ("Rating", 110, "!" + nameof(Line.C5)), ("State", 90, nameof(Line.C6)));
            adviceList.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Delete && Sel()) { DeleteAdvice(); e.Handled = true; } };
            adviceList.ContextMenu = MenuOf(acts);
            adviceList.SelectionChanged += (s, e) => { ShowAdvice(); UpdateGates(); };
            adviceList.MouseDoubleClick += (s, e) => { if (Sel()) ActOnAdvice(); };
            adviceShow.SelectionChanged += (s, e) => FillAdvice();
            adviceText.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            adviceText.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var show = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
            show.Children.Add(Label("Show:"));
            show.Children.Add(adviceShow);
            var list = WithEmpty(adviceList, "No advice - start an Advisor run in the chat with /advise, or in the pipeline with TcAgent.exe advise.");
            Heading(adviceHeading, "Advisor", "advice with an order - the Advisor reads and advises, it changes nothing");
            LoadAdvice();
            return advisorPage = Rows((adviceHeading, AutoH, 0), (show, AutoH, 0), (Bar(acts), AutoH, 0), (list, Star(2), 80), (Splitter(), AutoH, 0), (adviceText, Star(2), 80));
        }

        async void LoadAdvice()
        {
            var (ok, output) = await Cli("advise list --json", "Reading the advice");
            if (!ok) { status.Text = "Advisor: " + output.Trim().Split('\n')[0] + " (TcAgent.exe of version 0.52.1 or newer is needed)"; return; }
            var (okS, outS) = await Cli("advise stats --json");
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                advice = ser.Deserialize<List<Dictionary<string, object>>>(JsonArray(output)) ?? new List<Dictionary<string, object>>();
                adviceStats = okS ? ser.Deserialize<List<Dictionary<string, object>>>(JsonArray(outS)) ?? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>>();
                FillAdvice();
                status.Text = "Ready.";
            }
            catch (Exception ex) { Error("Advisor: " + ex.Message); }
        }

        static string JsonArray(string output)
        {
            int a = output.IndexOf('['), b = output.LastIndexOf(']');
            return a >= 0 && b > a ? output.Substring(a, b - a + 1) : "[]";
        }

        void FillAdvice()
        {
            var keep = S(SelectedAdvice, "id");
            var show = adviceShow.SelectedItem as string ?? "open";
            var lines = advice.Where(a => show == "all" || show == "open" && S(a, "state") != "done" || show == "not rated" && S(a, "rating") == null
                                         || show == "done" && S(a, "state") == "done").Select(a => new Line
            {
                D = a,
                C1 = DateTime.TryParse(S(a, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                C2 = $"{S(a, "kind")} · {S(a, "trigger")}", C3 = S(a, "finding"), C4 = S(a, "confidence"),
                C5 = S(a, "rating") is string r ? (r == "right" ? "✓ right" : r == "partly" ? "◐ partly" : r == "wrong" ? "✗ wrong" : "– n/a") : "● not rated",
                C6 = S(a, "state") == "done" ? "✓ done" : S(a, "state") == "acted" ? "➜ sent" : "● open",
                Mark = S(a, "state") == "done" ? Gray : S(a, "rating") switch { "right" => Green, "partly" => Orange, "wrong" => Red, null => Blue, _ => Gray },
                Tip = S(a, "order")
            }).ToList();
            adviceList.ItemsSource = lines;
            adviceList.SelectedItem = lines.FirstOrDefault(l => S(l.D, "id") == keep);
            var rates = adviceStats.Select(q => $"{S(q, "kind")}: " + (N(q, "rate") is double r ? $"{r * 100:0} % right of {S(q, "rated")}" : $"{S(q, "rated")} of 10 ratings") + $", {S(q, "open")} open");
            Heading(adviceHeading, $"Advisor ({advice.Count})", adviceStats.Count == 0 ? "advice with an order - the Advisor reads and advises, it changes nothing" : string.Join(" · ", rates));
            ShowAdvice();
            Summary();
        }

        void ShowAdvice()
        {
            var a = SelectedAdvice;
            if (a == null) { adviceText.Text = "Select an advice above."; return; }
            var sb = new StringBuilder();
            sb.AppendLine(S(a, "finding")).AppendLine();
            sb.AppendLine("Evidence:");
            foreach (var e in (a.TryGetValue("evidence", out var ev) ? ev as System.Collections.IEnumerable : null)?.Cast<object>() ?? Enumerable.Empty<object>()) sb.AppendLine("  " + e);
            if (S(a, "proposal") is string p) sb.AppendLine().AppendLine("Proposal: " + p);
            sb.AppendLine().AppendLine($"Confidence: {S(a, "confidence")}" + (S(a, "why") is string w ? " - " + w : ""));
            sb.AppendLine().AppendLine("Order" + (S(a, "workflow") is string wf ? $" (workflow {wf})" : "") + ":").AppendLine(S(a, "order"));
            if (S(a, "diff") is string d) sb.AppendLine().AppendLine("Diff:").AppendLine(d);
            if (S(a, "rating") is string r) sb.AppendLine().AppendLine($"Rated {r} by {S(a, "ratedBy")}" + (S(a, "comment") is string c ? ": " + c : ""));
            if (S(a, "state") == "done") sb.AppendLine($"Done by {S(a, "doneBy")} ({S(a, "doneAt")})" + (S(a, "doneComment") is string dc ? ": " + dc : ""));
            else if (S(a, "state") == "acted") sb.AppendLine($"Sent to an agent ({S(a, "actedAt")})");
            sb.AppendLine().AppendLine($"{S(a, "id")} · {S(a, "time")}");
            adviceText.Text = sb.ToString();
        }

        async void RateAdvice(string rating)
        {
            var id = S(SelectedAdvice, "id");
            if (id == null) return;
            string comment = null;
            if (rating == "wrong")
            {
                comment = AskComment();
                if (comment == null) return;
            }
            var (ok, output) = await Cli($"advise rate --id {id} --rating {rating}" + (string.IsNullOrWhiteSpace(comment) ? "" : " --comment " + Quote(comment)), "Rating");
            if (!ok) { Report(false, output, "Advisor"); return; }
            status.Text = $"{id}: {rating}";
            LoadAdvice();
        }

        // the comment of a "wrong": optional, it becomes the base of a proposal for tc_improve (null = cancelled)
        string AskComment()
        {
            var w = Dialog("Wrong advice", 480);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            sp.Children.Add(new TextBlock { Text = "What is wrong? (optional - it helps to improve the next runs)", Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Padding = new Thickness(2) };
            sp.Children.Add(box);
            var ok = new Button { Content = "Rate as wrong" };
            ok.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(Buttons(w, ok));
            w.Content = sp;
            box.Focus();
            return w.ShowDialog() == true ? box.Text.Trim() : null;
        }

        async void MarkAdvice(string what)
        {
            var id = S(SelectedAdvice, "id");
            if (id == null) return;
            var (ok, output) = await Cli($"advise {what} --id {id}", "Saving");
            if (!ok) { Report(false, output, "Advisor"); return; }
            status.Text = $"{id}: {what}";
            LoadAdvice();
        }

        void DeleteAdvice()
        {
            var a = SelectedAdvice;
            if (a == null) return;
            if (MessageBox.Show($"Remove this advice from the list?\n\n{S(a, "finding")}\n\nIt stays in the Git history of the project.", "TcAgent Knowledge",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            MarkAdvice("delete");
        }

        async void ActOnAdvice()
        {
            var id = S(SelectedAdvice, "id");
            var order = S(SelectedAdvice, "order");
            if (string.IsNullOrWhiteSpace(order)) return;
            // the agent that does the work closes the advice when it is done
            order = order.TrimEnd() + $"\n\nWhen it is done and proven: tc_advisor action=done id={id}.";
            if (S(SelectedAdvice, "state") == "open") { await Cli($"advise acted --id {id}"); LoadAdvice(); }
            try
            {
                BridgePackage.Instance?.ShowChat(true);
                if (ChatControl.Instance == null) { Error("The TcAgent Chat could not be opened."); return; }
                ChatControl.Instance.Prefill(order);
                status.Text = "The order is in the chat input - read it, change it, send it.";
            }
            catch (Exception ex) { Error(ex.Message); }
        }
    }
}
