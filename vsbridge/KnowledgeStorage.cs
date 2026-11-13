// TcAgent Knowledge - tab Storage: what TcAgent keeps under %LOCALAPPDATA%\TcAgent, the rule of each area and the clean-up
// (server: src/Storage.cs, TcAgent.exe storage). Areas: size, items, oldest, rule, what the rule would remove, whether the
// daily run of the extension cleans it. "Clean up …" shows every item first and moves them to the recycle bin. Projects:
// chats, TcUnit runs and workflow runs per project; "Delete everything of this project …"; a project whose solution is gone
// is marked. Settings: settings.json "retention" (docs/USER_GUIDE.md, Storage).
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TcAgentBridge
{
    public sealed partial class KnowledgeControl
    {
        readonly ListView areaList = NewList(), projectList = NewList();
        readonly TextBlock storageHeading = Heading(""), projectsHeading = Heading("");
        FrameworkElement storagePage;
        Dictionary<string, object> storageData;

        static string Mb(object bytes)
        {
            double b = 0;
            try { b = Convert.ToDouble(bytes, CultureInfo.InvariantCulture); } catch { }
            return b < 1024 * 1024 ? (b / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB" : (b / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        static long L(Dictionary<string, object> d, string k) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; } catch { return 0; } }

        FrameworkElement StoragePage()
        {
            if (storagePage != null) { LoadStorage(); return storagePage; }
            Columns(areaList, ("Area", 230, nameof(Line.C1)), ("Size", 80, nameof(Line.C2)), ("Items", 60, nameof(Line.C3)), ("Oldest", 90, nameof(Line.C4)),
                    ("Rule", 300, nameof(Line.C5)), ("To remove", 130, "!" + nameof(Line.C6)), ("Daily", 60, nameof(Line.C7)));
            Columns(projectList, ("Project", 220, nameof(Line.C1)), ("Size", 80, nameof(Line.C2)), ("Last used", 90, nameof(Line.C3)), ("Kept", 220, nameof(Line.C4)),
                    ("Solution", 360, "!" + nameof(Line.C5)));
            var areaActs = new[] {
                Main("Clean up …", "Remove what the rules say - every item is shown first and goes to the recycle bin (selected areas only, if any are selected)", () => CleanStorage()),
                Sep,
                A("Open folder", "The folder of TcAgent's files (%LOCALAPPDATA%\\TcAgent)", () => ShowInExplorer(S(storageData, "root"))),
                A("Rules …", "The rules are set in settings.json \"retention\" - open it", OpenRetention),
                A("Refresh", "Read the folders again", LoadStorage) };
            var projActs = new[] {
                A("Delete everything of this project …", "Chats, TcUnit runs and workflow runs of the selected projects (recycle bin; Shift/Ctrl selects several)", DeleteStorageProjects, () => AnySelected(projectList), "Del"),
                A("Open folder", "The folders kept for the selected project", () => { foreach (var p in Arr((projectList.SelectedItem as Line)?.D?["parts"]).Take(3)) ShowInExplorer(S(p, "path")); }, () => OneSelected(projectList)) };
            areaList.ContextMenu = MenuOf(areaActs);
            projectList.ContextMenu = MenuOf(projActs);
            projectList.PreviewKeyDown += (s, e) => { if (e.Key == Key.Delete && AnySelected(projectList)) { DeleteStorageProjects(); e.Handled = true; } };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(8, 0, 8, 4),
                                       Text = "Never touched: backups, your knowledge files, settings, the audit log, sources and plugins. Once a day the extension cleans the areas marked Daily in the background; chats and TcUnit runs only when you clean up here." };
            LoadStorage();
            return storagePage = Rows((storageHeading, AutoH, 0), (Bar(areaActs), AutoH, 0), (note, AutoH, 0), (WithEmpty(areaList, "Reading …"), Star(1.2), 80),
                                      (projectsHeading, AutoH, 0), (Bar(projActs), AutoH, 0), (WithEmpty(projectList, "Nothing is kept per project yet."), Star(1), 60));
        }

        async void LoadStorage()
        {
            var (ok, output) = await Cli("storage --json", "Reading the storage");
            if (!ok) { Report(false, output, "Storage"); return; }
            try { storageData = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output); }
            catch { status.Text = "Storage: this TcAgent.exe cannot list its files yet - update it."; return; }
            Heading(storageHeading, $"What TcAgent keeps on this PC ({Mb(storageData["total"])})", UserPath(S(storageData, "root")));
            areaList.ItemsSource = Arr(storageData["areas"]).Select(a => new Line
            {
                D = a, C1 = S(a, "title"), C2 = Mb(a["bytes"]), C3 = Convert.ToString(a["count"]), C4 = S(a, "oldest") ?? "–",
                C5 = S(a, "rule") + (B(a, "on") ? "" : " (switched off)"),
                C6 = L(a, "removable") > 0 ? $"↻ {a["removable"]} · {Mb(a["removableBytes"])}" : "✓ nothing",
                C7 = B(a, "auto") && B(storageData, "auto") ? "✓ daily" : "on request",
                Mark = L(a, "removable") > 0 ? Orange : Green, Tip = S(a, "rule")
            }).ToList();
            var projects = Arr(storageData["projects"]).ToList();
            Heading(projectsHeading, $"Per project ({projects.Count})", "chats, TcUnit runs and workflow runs");
            projectList.ItemsSource = projects.Select(p => new Line
            {
                D = p, C1 = S(p, "name"), C2 = Mb(p["bytes"]), C3 = S(p, "last"),
                C4 = string.Join(", ", Arr(p["parts"]).Select(x => S(x, "area")).Distinct()),
                C5 = S(p, "solutionDir") == null ? "–" : B(p, "exists") ? "✓ " + UserPath(S(p, "solutionDir")) : "✗ not found: " + UserPath(S(p, "solutionDir")),
                Mark = B(p, "exists") ? Green : Orange, Tip = string.Join("\n", Arr(p["parts"]).Select(x => UserPath(S(x, "path"))))
            }).ToList();
            Summary();
        }

        // every item first, then the recycle bin
        async void CleanStorage()
        {
            var areas = Selected(areaList).Select(l => S(l.D, "key")).ToList();
            var parts = new List<Dictionary<string, object>>();
            foreach (var a in areas.Count > 0 ? areas.Cast<string>() : new string[] { null })
            {
                var (ok, o) = await Cli("storage clean --dry-run --json" + (a != null ? " --area " + a : ""), "Looking for what to remove");
                if (!ok) { Report(false, o, "Clean up"); return; }
                try { parts.AddRange(Arr(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(o)["items"])); }
                catch { Report(false, o, "Clean up"); return; }
            }
            if (parts.Count == 0) { Info("Nothing to clean up - every area is within its rule."); return; }
            var text = string.Join("\n", parts.Select(i => $"{S(i, "area"),-12} {Mb(i["bytes"]),9}  {UserPath(S(i, "path"))}  ({S(i, "why")})"));
            if (!Confirm("Clean up", $"Remove {parts.Count} item(s), {Mb(parts.Sum(i => L(i, "bytes")))}? They go to the recycle bin; a log over the limit is renamed to .old.", text, "Clean up")) return;
            var results = new List<string>(); bool all = true;
            foreach (var a in areas.Count > 0 ? areas.Cast<string>() : new string[] { null })
            {
                var (ok, o) = await Cli("storage clean" + (a != null ? " --area " + a : ""), "Cleaning up");
                all &= ok; results.Add(o.Trim());
            }
            Report(all, string.Join("\n", results), "Clean up");
            LoadStorage();
        }

        async void DeleteStorageProjects()
        {
            var sel = Selected(projectList);
            if (sel.Count == 0) return;
            var text = string.Join("\n", sel.SelectMany(l => Arr(l.D["parts"]).Select(p => $"{S(l.D, "name")}: {UserPath(S(p, "path"))} ({Mb(p["bytes"])})")));
            if (!Confirm("Delete project files", $"Delete everything TcAgent keeps for {(sel.Count == 1 ? S(sel[0].D, "name") : sel.Count + " projects")} - chats, TcUnit runs, workflow runs? They go to the recycle bin; the projects themselves are not touched.",
                         text, "Delete", true)) return;
            await RunBatch("Delete project files", null, sel, l => S(l.D, "name"), l => $"storage project-delete --name {Quote(S(l.D, "name"))}");
            LoadStorage();
        }

        void OpenRetention()
        {
            var f = Path.Combine(S(storageData, "root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent"), "settings.json");
            Info("The rules are in settings.json, \"retention\" - for example\n{ \"retention\": { \"chats\": { \"days\": 60, \"keep\": 30 }, \"logs\": { \"days\": 14 }, \"tcunit\": false, \"auto\": false }\n" +
                 "days / keep per area, false switches an area off, \"auto\": false the daily run.", "Open settings.json", () => OpenInEditor(f));
        }
    }
}
