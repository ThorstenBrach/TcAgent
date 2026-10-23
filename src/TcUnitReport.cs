// HTML test report of the last TcUnit run (TcAgent.exe tcunit report, button "Report" of the TcUnit Tests window).
//
// Every tcunit run (window, command line, tc_test_run) leaves its results in %LOCALAPPDATA%\TcAgent\tcunit\<solution>-<hash>\:
//   last.xml   JUnit XML (for report tools of your own and CI systems)
//   last.json  the run: target, time, duration, outcome, filter, every test with status, message, duration, selected
// The report is made from them:
//   - TcAgent's own report (default): works for every project - suites and tests with the /// descriptions of the code
//     (TcUnitDiscovery), status, duration, failure text (EXP/ACT/MSG), code position and enable bool, summary per suite,
//     run information (target, TcAgent, git commit); search, "only failed", tests switched off for this run hidden
//   - with "report" in .tcagent\tcunit.json: that command line instead, run in the solution folder, e.g. a report of a
//     test case catalog; placeholders {junit} (JUnit file), {html} (report file to write), {solution}, {solutionDir}.
//     Runs only after the user approved it once for this solution and command text (window question or --approve <id>)
// Every text from the code or the runtime is HTML-encoded, only http(s) links are clickable, a Content-Security-Policy
// allows no external resources and no script but the report's own.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TcAgent;

public static class TcUnitReport
{
    /// <summary>Folder of the results of a solution (outside the repository, nothing to commit by mistake).</summary>
    public static string Dir(string sln)
    {
        var full = Path.GetFullPath(sln);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "tcunit",
                            Path.GetFileNameWithoutExtension(full) + "-" + hash);
    }

    public static string LastJunit(string sln) => Path.Combine(Dir(sln), "last.xml");
    public static string LastRun(string sln) => Path.Combine(Dir(sln), "last.json");
    public static string DefaultHtml(string sln) => Path.Combine(Dir(sln), "report.html");

    public static void SaveRun(string sln, JsonObject run)
    {
        Directory.CreateDirectory(Dir(sln));
        File.WriteAllText(LastRun(sln), run.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // approved report commands: ids of solution path + command text (Guards.ReportApprovalId), per Windows user
    static string ApprovalsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "tcunit-report-approved.json");

    static HashSet<string> Approvals()
    {
        try { return (JsonNode.Parse(File.ReadAllText(ApprovalsFile)) as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToHashSet() ?? new(); }
        catch { return new(); }
    }

    public static bool IsApproved(string sln, string command) => Approvals().Contains(Guards.ReportApprovalId(sln, command));

    static void Approve(string sln, string command)
    {
        var all = Approvals();
        all.Add(Guards.ReportApprovalId(sln, command));
        Directory.CreateDirectory(Path.GetDirectoryName(ApprovalsFile)!);
        File.WriteAllText(ApprovalsFile, new JsonArray(all.Order().Select(x => (JsonNode)x).ToArray()).ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>Makes the report. Returns the HTML file, throws with a readable message if it cannot be made.
    /// The command of "report" in tcunit.json comes from the solution (a cloned repository, say) and runs only after the user
    /// approved it once for this solution and exactly this command text (approveId = Guards.ReportApprovalId); until then
    /// TcAgent's own report is made and pendingApproval is the id to approve.</summary>
    public static string Make(string sln, string junit, string html, TcUnitConfig cfg, Action<string> say, string approveId, out string pendingApproval)
    {
        pendingApproval = null;
        bool lastRun = junit == null;
        junit = Path.GetFullPath(junit ?? LastJunit(sln));
        html = Path.GetFullPath(html ?? DefaultHtml(sln));
        if (!File.Exists(junit)) throw new FileNotFoundException("No results yet - run the tests first (" + junit + ").");
        Directory.CreateDirectory(Path.GetDirectoryName(html)!);
        if (!string.IsNullOrWhiteSpace(cfg.Report))
        {
            var id = Guards.ReportApprovalId(sln, cfg.Report);
            if (approveId != null && approveId != id)
                throw new UnauthorizedAccessException("The approval does not match the report command of " + cfg.File + " (changed since the question?) - nothing was run.");
            if (approveId == id) Approve(sln, cfg.Report);
            else if (!IsApproved(sln, cfg.Report))
            {
                say($"The report command of {cfg.File} is not approved yet - TcAgent's own report instead. Command: {cfg.Report}");
                say($"To run it: TcAgent.exe tcunit report --solution \"{sln}\" --approve {id} (only if you trust this solution)");
                pendingApproval = id;
                cfg = new TcUnitConfig { PlcProject = cfg.PlcProject, ReportGroupBy = cfg.ReportGroupBy, File = cfg.File };
            }
        }
        if (string.IsNullOrWhiteSpace(cfg.Report))
        {
            var run = lastRun && File.Exists(LastRun(sln)) ? JsonNode.Parse(File.ReadAllText(LastRun(sln))) as JsonObject : null;
            File.WriteAllText(html, BuiltIn(sln, junit, run, cfg), new UTF8Encoding(false));
            return html;
        }
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(sln))!;
        static string Q(string p) => "\"" + p + "\"";
        var cmd = cfg.Report.Replace("{junit}", Q(junit)).Replace("{html}", Q(html)).Replace("{solution}", Q(Path.GetFullPath(sln))).Replace("{solutionDir}", Q(slnDir));
        say("Report: " + cmd);
        var before = File.Exists(html) ? File.GetLastWriteTimeUtc(html) : DateTime.MinValue;
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + cmd + "\"")
        {
            WorkingDirectory = slnDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("The report command did not finish within 5 minutes: " + cmd); }
        foreach (var l in (outText + err.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) say("  " + l);
        // a report tool may end with exit code 1 because tests failed - what counts is the written file
        if (!File.Exists(html) || File.GetLastWriteTimeUtc(html) <= before)
            throw new Exception($"The report command wrote no file {html} (exit code {p.ExitCode}) - does it write to {{html}}? {cmd}");
        if (p.ExitCode != 0) say($"Report command ended with exit code {p.ExitCode} (report written)");
        return html;
    }

    // ------------------------------------------------------------------ TcAgent's own report
    sealed class Item
    {
        public string Instance, Name, Status, Message;
        public double? Duration;
        public bool Selected = true;
        public TcUnitDiscovery.Test Test;
    }

    static string E(string s) => WebUtility.HtmlEncode(s ?? "");
    static string Sec(double? s) => s == null ? "" : s < 1 ? (s.Value * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms" : s.Value.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>Results: from the run record (last.json), otherwise from the JUnit file.</summary>
    static List<Item> Items(string junit, JsonObject run)
    {
        if (run?["results"] is JsonArray arr)
            return arr.OfType<JsonObject>().Select(r => new Item
            {
                Instance = (string)r["instance"], Name = (string)r["test"], Status = (string)r["status"], Message = (string)r["message"],
                Duration = r["duration"] is JsonValue d && d.TryGetValue<double>(out var x) ? x : null, Selected = (bool?)r["selected"] ?? true
            }).ToList();
        var list = new List<Item>();
        foreach (var s in XDocument.Load(junit).Descendants("testsuite"))
            foreach (var c in s.Elements("testcase"))
            {
                var fail = c.Element("failure") ?? c.Element("error");
                var skip = c.Element("skipped");
                list.Add(new Item
                {
                    Instance = (string)s.Attribute("name"), Name = (string)c.Attribute("name"),
                    Status = fail != null ? "failed" : skip != null ? "skipped" : "passed",
                    Message = fail == null ? null : string.IsNullOrWhiteSpace(fail.Value) ? (string)fail.Attribute("message") : fail.Value.Trim(),
                    Duration = double.TryParse((string)c.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null,
                    Selected = !(((string)skip?.Attribute("message"))?.Contains("switched off", StringComparison.OrdinalIgnoreCase) ?? false)
                });
                if (((string)skip?.Attribute("message"))?.Contains("not run", StringComparison.OrdinalIgnoreCase) == true) list[^1].Status = "notrun";
            }
        return list;
    }

    static readonly System.Text.RegularExpressions.Regex Heading = new(@"^([A-Za-z][\w /()-]{0,30}):\s*(.*)$"), Numbered = new(@"^\d+[.)]\s+(.*)$"), Bullet = new(@"^[-*]\s+(.*)$");

    /// <summary>The details of a /// text as HTML: "Keyword:" lines as headings, "1." and "-" lines as lists, links clickable.</summary>
    static string DocHtml(string details)
    {
        var sb = new StringBuilder();
        string list = null;
        void Close() { if (list != null) { sb.Append("</").Append(list).Append('>'); list = null; } }
        void Open(string kind) { if (list == kind) return; Close(); sb.Append('<').Append(kind).Append('>'); list = kind; }
        // links: only absolute http(s) URLs, found in the raw text and encoded for the attribute and the text separately
        static string Text(string s)
        {
            var sb = new StringBuilder();
            int at = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s ?? "", @"https?://[^\s<>""']+"))
            {
                sb.Append(E(s[at..m.Index]));
                var href = Guards.SafeHref(m.Value);
                sb.Append(href != null ? "<a href=\"" + E(href) + "\" rel=\"noopener noreferrer\">" + E(m.Value) + "</a>" : E(m.Value));
                at = m.Index + m.Length;
            }
            return sb.Append(E((s ?? "")[at..])).ToString();
        }
        foreach (var raw in details.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) { Close(); continue; }
            System.Text.RegularExpressions.Match m;
            if ((m = Numbered.Match(l)).Success) { Open("ol"); sb.Append("<li>").Append(Text(m.Groups[1].Value)).Append("</li>"); continue; }
            if ((m = Bullet.Match(l)).Success) { Open("ul"); sb.Append("<li>").Append(Text(m.Groups[1].Value)).Append("</li>"); continue; }
            Close();
            if ((m = Heading.Match(l)).Success && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<h4>").Append(E(m.Groups[1].Value)).Append("</h4>");
                if (m.Groups[2].Value.Length > 0) sb.Append("<div>").Append(Text(m.Groups[2].Value)).Append("</div>");
            }
            else sb.Append("<div>").Append(Text(l)).Append("</div>");
        }
        Close();
        return sb.ToString();
    }

    static string Git(string dir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(5000) && p.ExitCode == 0 ? o : null;
        }
        catch { return null; }
    }

    static string BuiltIn(string sln, string junit, JsonObject run, TcUnitConfig cfg)
    {
        var items = Items(junit, run);
        // descriptions, code positions and enable bools from the code
        TcUnitDiscovery.Project proj = null;
        try
        {
            var d = TcUnitDiscovery.Discover(sln);
            var name = (string)run?["project"] ?? cfg.PlcProject;
            proj = d.Projects.FirstOrDefault(p => name != null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? d.Projects.FirstOrDefault(p => p.Instances.Any(i => items.Any(x => x.Instance == i.Path)));
        }
        catch { }
        var instances = proj?.Instances.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, TcUnitDiscovery.Instance>();
        foreach (var it in items)
            if (it.Instance != null && instances.TryGetValue(it.Instance, out var inst))
                it.Test = inst.Suite.Tests.FirstOrDefault(t => t.Name.Equals(it.Name, StringComparison.OrdinalIgnoreCase));

        var ran = items.Where(i => i.Selected).ToList();
        int passed = ran.Count(i => i.Status == "passed"), failed = ran.Count(i => i.Status == "failed"),
            skipped = ran.Count(i => i.Status == "skipped"), notRun = ran.Count(i => i.Status is "notrun" or "running"), off = items.Count(i => !i.Selected);
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(sln))!;
        var title = proj?.Name ?? (string)run?["project"] ?? Path.GetFileNameWithoutExtension(sln);
        var when = DateTime.TryParse((string)run?["started"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var st) ? st : File.GetLastWriteTime(junit);
        var commit = Git(slnDir, "rev-parse --short HEAD");
        var branch = Git(slnDir, "rev-parse --abbrev-ref HEAD");
        var dirty = commit != null && !string.IsNullOrEmpty(Git(slnDir, "status --porcelain --untracked-files=no"));
        var outcome = (string)run?["outcome"];
        var filter = (string)run?["filter"];

        var sb = new StringBuilder();
        // Content-Security-Policy: no external resources, only TcAgent's own script (hash) - text from the code or the runtime
        // that would get through as HTML could not run a script anyway
        var csp = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; script-src 'sha256-" +
                  Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script))) + "'";
        sb.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"").Append(csp)
          .Append("\"><meta name='viewport' content='width=device-width,initial-scale=1'><title>")
          .Append(E(title)).Append(" - TcUnit test report</title><style>")
          .Append(":root{--bg:#f7f8fa;--card:#fff;--fg:#1d2330;--muted:#5d6675;--line:#dfe3ea;--ok:#1f8a4c;--okbg:#e6f4ec;--bad:#c62f2f;--badbg:#fbe9e9;--skip:#a26b00;--skipbg:#fbf2dc;--none:#8a93a3;--accent:#2b5fb4}")
          .Append("@media(prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1c2027;--fg:#e6e9ef;--muted:#9aa3b2;--line:#2e343e;--ok:#4cc38a;--okbg:#17301f;--bad:#ff6b6b;--badbg:#3a1d1d;--skip:#e0b04a;--skipbg:#33290f;--none:#6b7482;--accent:#7aa7ff}}")
          .Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,'Segoe UI',sans-serif}")
          .Append("header{padding:24px 28px 8px}h1{margin:0 0 4px;font-size:22px}h2{font-size:18px;margin:28px 0 10px}.muted{color:var(--muted)}main{padding:0 28px 40px;max-width:1300px}")
          .Append(".cards{display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 16px;min-width:140px}")
          .Append(".card b{display:block;font-size:24px}.card.ok b{color:var(--ok)}.card.bad b{color:var(--bad)}.card.skip b{color:var(--skip)}")
          .Append(".meter{height:8px;border-radius:4px;background:var(--line);overflow:hidden;display:flex;margin:4px 0 6px}.meter i{display:block}")
          .Append(".bar{display:flex;flex-wrap:wrap;gap:12px;align-items:center;margin:10px 0 18px;position:sticky;top:0;background:var(--bg);padding:8px 0;z-index:2}")
          .Append(".bar input[type=search]{padding:7px 10px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--fg);min-width:280px}")
          .Append(".bar button{padding:6px 10px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--fg);cursor:pointer}")
          .Append("details.suite{background:var(--card);border:1px solid var(--line);border-radius:10px;margin:8px 0}")
          .Append("details.suite>summary{cursor:pointer;padding:10px 14px;display:flex;gap:10px;align-items:center;list-style:none}details.suite>summary::-webkit-details-marker{display:none}")
          .Append("details.suite>summary .name{font-weight:600}details.suite>summary .desc{flex:1;color:var(--muted)}details.suite[open]>summary{border-bottom:1px solid var(--line)}")
          .Append(".pill{border-radius:999px;padding:1px 9px;font-size:12px;font-weight:600;white-space:nowrap}.pill.passed{background:var(--okbg);color:var(--ok)}.pill.failed{background:var(--badbg);color:var(--bad)}.pill.skipped{background:var(--skipbg);color:var(--skip)}")
          .Append("details.test{border-top:1px solid var(--line)}details.test:first-of-type{border-top:0}")
          .Append("details.test>summary{cursor:pointer;display:grid;grid-template-columns:110px minmax(180px,1.2fr) 2fr 90px;gap:10px;padding:7px 14px;align-items:center;list-style:none}")
          .Append("details.test>summary::-webkit-details-marker{display:none}details.test>summary:hover{background:var(--bg)}")
          .Append(".st{font-weight:600;font-size:13px;white-space:nowrap}.st.passed{color:var(--ok)}.st.failed{color:var(--bad)}.st.skipped{color:var(--skip)}.st.notrun,.st.off{color:var(--none)}")
          .Append(".tn{font-family:ui-monospace,Consolas,monospace;font-size:12.5px;overflow-wrap:anywhere}.td{color:var(--muted)}.num{text-align:right;color:var(--muted);font-size:12.5px}")
          .Append(".body{padding:4px 18px 14px 134px}.body h4{margin:10px 0 4px;font-size:13px}.kv{display:grid;grid-template-columns:120px 1fr;gap:2px 10px;font-size:13px}.kv span:nth-child(odd){color:var(--muted)}")
          .Append("pre{white-space:pre-wrap;margin:2px 0;font:12px ui-monospace,Consolas,monospace;color:var(--bad);background:var(--badbg);padding:8px 10px;border-radius:6px}")
          .Append("table.sum{border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden;min-width:600px}")
          .Append("table.sum td,table.sum th{padding:7px 14px;border-bottom:1px solid var(--line);text-align:right}table.sum th:first-child,table.sum td:first-child{text-align:left}")
          .Append(".sdoc{padding:4px 18px 10px 18px;border-bottom:1px solid var(--line)}.body ol,.body ul,.sdoc ol,.sdoc ul{margin:4px 0;padding-left:20px}.sdoc h4{margin:8px 0 4px;font-size:13px}")
          .Append(".bar button.on{background:var(--accent);color:#fff;border-color:var(--accent)}")
          .Append(".hdr{display:grid;grid-template-columns:110px minmax(180px,1.2fr) 2fr 90px;gap:10px;padding:0 14px 4px;color:var(--muted);font-size:12px}.hidden{display:none!important}code{font-size:12.5px}")
          .Append("</style></head><body><header><h1>").Append(E(title)).Append(" &ndash; TcUnit test report</h1><div class='muted'>")
          .Append(E(when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
          .Append(run?["seconds"] is JsonValue secs ? " &middot; " + E(secs.ToString()) + " s" : "")
          .Append(" &middot; target ").Append(E((string)run?["target"] ?? "?"))
          .Append(" &middot; ").Append(E(Environment.MachineName))
          .Append(commit != null ? " &middot; git " + E(branch) + " " + E(commit) + (dirty ? " (uncommitted changes)" : "") : "")
          .Append(" &middot; TcAgent ").Append(E((string)run?["tcagent"] ?? Program.ServerVersion));
        if (proj?.RunMode != null) sb.Append(" &middot; ").Append(proj.RunMode == "sequence" ? "TcUnit.RUN_IN_SEQUENCE" : "TcUnit.RUN");
        sb.Append("</div>");
        if (!string.IsNullOrEmpty(filter)) sb.Append("<div class='muted'>Selection: <code>").Append(E(filter)).Append("</code></div>");
        if (outcome is "cancelled" or "timeout") sb.Append("<div class='st failed' style='margin-top:6px'>The run was ").Append(outcome == "cancelled" ? "cancelled" : "stopped after the timeout").Append(" - tests without a result are \"not run\".</div>");

        string Card(string cls, string label, string value) => $"<div class='card {cls}'><span class='muted'>{label}</span><b>{value}</b></div>";
        var rate = passed + failed == 0 ? "&ndash;" : (100.0 * passed / (passed + failed)).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        sb.Append("<div class='cards'>").Append(Card("", "Tests run", (passed + failed).ToString())).Append(Card("ok", "Passed", passed.ToString()))
          .Append(Card("bad", "Failed", failed.ToString())).Append(Card("skip", "Skipped / not run", (skipped + notRun).ToString()))
          .Append(Card("", "Pass rate", rate));
        if (off > 0) sb.Append(Card("", "Switched off", off.ToString()));
        sb.Append("</div>");
        string Pct(int n) => ran.Count == 0 ? "0" : (100.0 * n / ran.Count).ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append("<div class='meter'><i style='width:").Append(Pct(passed)).Append("%;background:var(--ok)'></i><i style='width:").Append(Pct(failed))
          .Append("%;background:var(--bad)'></i><i style='width:").Append(Pct(skipped + notRun)).Append("%;background:var(--skip)'></i></div></header><main>");
        sb.Append("<div class='bar'><input id='q' type='search' placeholder='Filter: suite, test, description, message ...'>")
          .Append("<label><input id='fail' type='checkbox'> only failed</label>")
          .Append(off > 0 ? $"<label><input id='off' type='checkbox'> show the {off} tests switched off for this run</label>" : "<input id='off' type='checkbox' class='hidden' checked>")
          .Append("<button id='open'>expand all</button><button id='close'>collapse all</button></div>");

        // the status goes into class names: only the known values (last.json may come from anywhere)
        string Look(Item i) => !i.Selected ? "off" : i.Status is "passed" or "failed" or "skipped" ? i.Status : "notrun";
        string Label(string s) => s switch { "passed" => "&#10004; passed", "failed" => "&#10006; failed", "skipped" => "&#8856; skipped", "off" => "&ndash; switched off", _ => "&#9675; not run" };
        TcUnitDiscovery.Instance Inst(Item i) => i.Instance != null && instances.TryGetValue(i.Instance, out var x) ? x : null;
        var summarySuites = new List<(string Name, int Run, int Ok, int Bad, int Skip, double Time)>();
        var summaryGroups = new List<(string Name, int Run, int Ok, int Bad, int Skip, double Time)>();

        // one group: a suite instance, or a value of a /// section (reportGroupBy, e.g. a function block under test)
        void Group(StringBuilder o, string name, string sub, string desc, string details, List<Item> list, List<(string, int, int, int, int, double)> summary)
        {
            var gr = list.Where(i => i.Selected).ToList();
            int gp = gr.Count(i => i.Status == "passed"), gf = gr.Count(i => i.Status == "failed"), gs = gr.Count(i => i.Status is "skipped" or "notrun" or "running");
            var gt = gr.Sum(i => i.Duration ?? 0);
            if (gr.Count > 0) summary.Add((name, gp + gf, gp, gf, gs, gt));
            o.Append("<details class='suite'").Append(gf > 0 ? " open" : "").Append("><summary><span class='name'>").Append(E(name)).Append("</span>")
             .Append(sub != null ? "<span class='muted'>" + E(sub) + "</span>" : "").Append("<span class='desc'>").Append(E(desc)).Append("</span>");
            if (gp > 0) o.Append("<span class='pill passed'>&#10004; ").Append(gp).Append("</span>");
            if (gf > 0) o.Append("<span class='pill failed'>&#10006; ").Append(gf).Append("</span>");
            if (gs > 0) o.Append("<span class='pill skipped'>&#8856; ").Append(gs).Append("</span>");
            o.Append("<span class='muted' style='font-size:12px'>").Append(Sec(gt)).Append("</span></summary>");
            // what the suite checks (/// details of the suite FB, e.g. the test case of a generated suite)
            if (details != null) o.Append("<div class='sdoc'>").Append(DocHtml(details)).Append("</div>");
            foreach (var i in list)
            {
                var look = Look(i);
                var t = i.Test;
                var inst = Inst(i);
                var sdesc = inst?.Description ?? inst?.Suite.Description;
                var text = string.Join(" ", i.Instance, inst?.Suite.Type, sdesc, i.Name, t?.Description, t?.Details, i.Message).ToLowerInvariant();
                o.Append("<details class='test").Append(look == "off" ? " off" : "").Append("' data-text='").Append(E(text)).Append("' data-fail='").Append(look == "failed" ? 1 : 0).Append(look == "failed" ? "' open>" : "'>")
                 .Append("<summary><span class='st ").Append(look).Append("'>").Append(Label(look)).Append("</span><span class='tn'>").Append(E(i.Name)).Append("</span><span class='td'>")
                 .Append(E(t?.Description)).Append("</span><span class='num'>").Append(look is "passed" or "failed" ? Sec(i.Duration) : "").Append("</span></summary><div class='body'>");
                if (t?.Description != null) o.Append("<div><b>").Append(E(t.Description)).Append("</b></div>");
                if (t?.Details != null) o.Append(DocHtml(t.Details));
                else if (t == null && inst?.Suite.Details != null) o.Append("<div class='muted'>Test case of the suite:</div>").Append(DocHtml(inst.Suite.Details));
                if (!string.IsNullOrEmpty(i.Message))
                    o.Append("<h4>Failure</h4><pre>").Append(E(string.Join("\n", i.Message.Split('\n').Distinct()).Replace(", ACT:", "\nACT:").Replace(", MSG:", "\nMSG:"))).Append("</pre>");
                o.Append("<h4>Details</h4><div class='kv'><span>Suite</span><span>").Append(E(i.Instance)).Append(inst != null ? " : " + E(inst.Suite.Type) : "").Append("</span>");
                if (sdesc != null) o.Append("<span>Suite purpose</span><span>").Append(E(sdesc)).Append("</span>");
                if (t != null)
                {
                    o.Append("<span>Code</span><span>").Append(E(Path.GetRelativePath(slnDir, t.File))).Append(':').Append(t.Line).Append(t.Method != null ? " (method " + E(t.Method) + ")" : " (suite body)").Append("</span>");
                    o.Append("<span>Enable bool</span><span>").Append(t.EnableVar != null ? "<code>" + E(i.Instance + "." + t.EnableVar) + "</code>" : "&ndash; (runs with its suite)").Append("</span>");
                    if (t.Ordered) o.Append("<span>Order</span><span>TEST_ORDERED</span>");
                }
                else o.Append("<span>Code</span><span class='muted'>test name built at run time / not found in the code</span>");
                o.Append("<span>Result</span><span>").Append(Label(look)).Append(look is "passed" or "failed" && i.Duration != null ? " in " + Sec(i.Duration) : "").Append("</span></div></div></details>");
            }
            o.Append("</details>");
        }

        void Summary(StringBuilder o, string head, List<(string Name, int Run, int Ok, int Bad, int Skip, double Time)> summary)
        {
            o.Append("<h2>Summary</h2><table class='sum'><tr><th>").Append(E(head)).Append("</th><th>Tests run</th><th>Passed</th><th>Failed</th><th>Skipped / not run</th><th>Time</th></tr>");
            foreach (var s in summary)
                o.Append("<tr><td>").Append(E(s.Name)).Append("</td><td>").Append(s.Run).Append("</td><td style='color:var(--ok)'>").Append(s.Ok)
                 .Append("</td><td style='color:var(--bad)'>").Append(s.Bad).Append("</td><td style='color:var(--skip)'>").Append(s.Skip).Append("</td><td>").Append(Sec(s.Time)).Append("</td></tr>");
            o.Append("<tr><td><b>Total</b></td><td><b>").Append(passed + failed).Append("</b></td><td style='color:var(--ok)'><b>").Append(passed).Append("</b></td><td style='color:var(--bad)'><b>")
             .Append(failed).Append("</b></td><td style='color:var(--skip)'><b>").Append(skipped + notRun).Append("</b></td><td><b>").Append(Sec(ran.Sum(i => i.Duration ?? 0))).Append("</b></td></tr></table>");
        }

        // view 1: by suite instance, in the order TcUnit runs them
        var order = proj?.Instances.Select(i => i.Path).ToList() ?? new List<string>();
        var bySuite = new StringBuilder();
        foreach (var g in items.GroupBy(i => i.Instance ?? "").OrderBy(g => order.IndexOf(g.Key) is var x && x < 0 ? int.MaxValue : x))
        {
            var inst = Inst(g.First());
            Group(bySuite, g.Key, inst?.Suite.Type, inst?.Description ?? inst?.Suite.Description, inst?.Suite.Details, g.ToList(), summarySuites);
        }
        if (items.Count == 0) bySuite.Append("<p class='muted'>The result file contains no tests.</p>");
        Summary(bySuite, "Suite", summarySuites);

        // view 2 (optional): by the values of a /// section of the tests, e.g. "Blocks: MC_GroupResetFB" - a test with
        // several values is in every group, tests without the section at the end
        var key = cfg.ReportGroupBy?.Trim().TrimEnd(':');
        StringBuilder byKey = null;
        if (!string.IsNullOrEmpty(key))
        {
            var rx = new System.Text.RegularExpressions.Regex(@"^" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
            List<string> Values(Item i)
            {
                var m = i.Test?.Details == null ? null : rx.Match(i.Test.Details);
                return m?.Success == true ? m.Groups[1].Value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList() : new List<string>();
            }
            var without = "(no " + key + ")";
            var groups = items.SelectMany(i => (Values(i) is var v && v.Count > 0 ? v : new List<string> { without }).Select(v => (v, i)))
                              .GroupBy(x => x.v).OrderBy(g => g.Key == without ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            byKey = new StringBuilder();
            foreach (var g in groups)
            {
                var list = g.Select(x => x.i).ToList();
                // the suites of the tests that ran (all suites if nothing of this group ran)
                var suitesRun = (list.Any(i => i.Selected) ? list.Where(i => i.Selected) : list).Select(i => i.Instance).Distinct().Count();
                Group(byKey, g.Key, $"{suitesRun} suite(s)", null, null, list, summaryGroups);
            }
            Summary(byKey, key, summaryGroups);
            sb.Append("<div class='bar' style='position:static;margin-top:-8px'><span class='muted'>Group by</span><button id='g-key' class='on'>").Append(E(key))
              .Append("</button><button id='g-suite'>Suite</button></div>");
        }
        sb.Append("<div class='hdr'><span>Result</span><span>Test</span><span>Description</span><span style='text-align:right'>Time</span></div>");
        if (byKey != null) sb.Append("<div id='v-key'>").Append(byKey).Append("</div><div id='v-suite' class='hidden'>").Append(bySuite).Append("</div>");
        else sb.Append(bySuite);
        sb.Append("<p class='muted'>Descriptions are the <code>///</code> comments of the test methods and suite instances in the code. Report of TcAgent ")
          .Append(E(Program.ServerVersion)).Append(" &ndash; results: ").Append(E(junit)).Append("</p></main><script>")
          .Append(Script).Append("</script></body></html>");
        return sb.ToString();
    }

    // the only script of the report (fixed text, its hash is in the Content-Security-Policy)
    const string Script =
        "const q=document.getElementById('q'),f=document.getElementById('fail'),o=document.getElementById('off');" +
        "function apply(){const s=q.value.toLowerCase();document.querySelectorAll('details.suite').forEach(b=>{let any=false;" +
        "b.querySelectorAll('details.test').forEach(c=>{const ok=(!s||c.dataset.text.includes(s))&&(!f.checked||c.dataset.fail==='1')&&(o.checked||!c.classList.contains('off'));" +
        "c.classList.toggle('hidden',!ok);any=any||ok});b.classList.toggle('hidden',!any);if(s||f.checked)b.open=any})}" +
        "q.addEventListener('input',apply);f.addEventListener('change',apply);o.addEventListener('change',apply);" +
        "document.getElementById('open').onclick=()=>document.querySelectorAll('details').forEach(d=>{if(!d.classList.contains('hidden'))d.open=true});" +
        "const gk=document.getElementById('g-key'),gs=document.getElementById('g-suite');function view(x){document.getElementById('v-key').classList.toggle('hidden',x!=='key');" +
        "document.getElementById('v-suite').classList.toggle('hidden',x!=='suite');gk.classList.toggle('on',x==='key');gs.classList.toggle('on',x==='suite')}if(gk){gk.onclick=()=>view('key');gs.onclick=()=>view('suite')}" +
        "document.getElementById('close').onclick=()=>document.querySelectorAll('details').forEach(d=>d.open=false);apply();";
}
