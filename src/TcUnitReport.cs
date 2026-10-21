// HTML test report of the last TcUnit run (TcAgent.exe tcunit report, button "Report" of the TcUnit Tests window).
//
// Every tcunit run (window, command line, tc_test_run) also writes its results as JUnit XML to
// %LOCALAPPDATA%\TcAgent\tcunit\<solution>-<hash>\last.xml. The report is made from that file:
//   - with "report" in .tcagent\tcunit.json: that command line, run in the solution folder, e.g.
//       "report": "python ..\\..\\SRCI\\SRCI_TEST_CASES\\tools\\report.py --tcunit-xml {junit} --name TwinCAT -o {html}"
//     placeholders: {junit} (JUnit file), {html} (report file to write), {solution} (.sln), {solutionDir}
//   - otherwise TcAgent's own report: one self-contained HTML file (summary, suites, tests, failure texts).
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    public static string DefaultHtml(string sln) => Path.Combine(Dir(sln), "report.html");

    /// <summary>Makes the report. Returns the HTML file, throws with a readable message if it cannot be made.</summary>
    public static string Make(string sln, string junit, string html, TcUnitConfig cfg, Action<string> say)
    {
        junit = Path.GetFullPath(junit ?? LastJunit(sln));
        html = Path.GetFullPath(html ?? DefaultHtml(sln));
        if (!File.Exists(junit)) throw new FileNotFoundException("No results yet - run the tests first (" + junit + ").");
        Directory.CreateDirectory(Path.GetDirectoryName(html)!);
        if (string.IsNullOrWhiteSpace(cfg.Report))
        {
            File.WriteAllText(html, BuiltIn(junit, Path.GetFileNameWithoutExtension(sln)), new UTF8Encoding(false));
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
    static string E(string s) => WebUtility.HtmlEncode(s ?? "");
    static double D(XElement e, string a) => double.TryParse((string)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    static string BuiltIn(string junit, string title)
    {
        var doc = XDocument.Load(junit);
        var suites = doc.Descendants("testsuite").Where(s => s.Elements("testcase").Any()).ToList();
        string Status(XElement tc) => tc.Element("failure") != null || tc.Element("error") != null ? "failed" : tc.Element("skipped") != null ? "skipped" : "passed";
        var all = suites.SelectMany(s => s.Elements("testcase")).ToList();
        int passed = all.Count(t => Status(t) == "passed"), failed = all.Count(t => Status(t) == "failed"), skipped = all.Count(t => Status(t) == "skipped");
        var when = File.GetLastWriteTime(junit).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>")
          .Append("<title>").Append(E(title)).Append(" - TcUnit test report</title><style>")
          .Append(":root{--bg:#f7f8fa;--card:#fff;--fg:#1d2330;--muted:#5d6675;--line:#dfe3ea;--ok:#1f8a4c;--okbg:#e6f4ec;--bad:#c62f2f;--badbg:#fbe9e9;--skip:#8a6a00;--skipbg:#fbf2dc}")
          .Append("@media(prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1c2027;--fg:#e6e9ef;--muted:#9aa3b2;--line:#2e343e;--ok:#4cc38a;--okbg:#17301f;--bad:#ff6b6b;--badbg:#3a1d1d;--skip:#e0b04a;--skipbg:#33290f}}")
          .Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 'Segoe UI',system-ui,sans-serif}")
          .Append("main{max-width:1200px;margin:0 auto;padding:24px 28px 40px}h1{margin:0 0 4px;font-size:22px}h2{font-size:16px;margin:26px 0 8px}.muted{color:var(--muted)}")
          .Append(".cards{display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 16px;min-width:130px}")
          .Append(".card b{display:block;font-size:24px}.ok b{color:var(--ok)}.bad b{color:var(--bad)}.skip b{color:var(--skip)}")
          .Append(".meter{height:8px;border-radius:4px;background:var(--line);overflow:hidden;display:flex;margin:4px 0 18px}.meter i{display:block}")
          .Append("table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}")
          .Append("th,td{text-align:left;padding:6px 10px;border-top:1px solid var(--line);vertical-align:top}th{font-weight:600;color:var(--muted);border-top:0}")
          .Append(".st{font-weight:600;border-radius:4px;padding:0 6px}.st.passed{color:var(--ok);background:var(--okbg)}.st.failed{color:var(--bad);background:var(--badbg)}.st.skipped{color:var(--skip);background:var(--skipbg)}")
          .Append("pre{margin:4px 0 0;white-space:pre-wrap;font:12px Consolas,monospace;color:var(--bad)}td.num{text-align:right;white-space:nowrap}</style></head><body><main>")
          .Append("<h1>").Append(E(title)).Append(" &ndash; TcUnit test report</h1><div class='muted'>Run of ").Append(E(when))
          .Append(" &middot; ").Append(E(Environment.MachineName)).Append(" &middot; TcAgent ").Append(E(Program.ServerVersion)).Append("</div>")
          .Append("<div class='cards'><div class='card'><b>").Append(all.Count).Append("</b>tests</div><div class='card ok'><b>").Append(passed)
          .Append("</b>passed</div><div class='card bad'><b>").Append(failed).Append("</b>failed</div><div class='card skip'><b>").Append(skipped)
          .Append("</b>skipped / not run</div><div class='card'><b>").Append((all.Count - skipped) == 0 ? "-" : (100.0 * passed / (all.Count - skipped)).ToString("0", CultureInfo.InvariantCulture) + " %")
          .Append("</b>pass rate</div></div>");
        string Pct(int n) => all.Count == 0 ? "0" : (100.0 * n / all.Count).ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append("<div class='meter'><i style='width:").Append(Pct(passed)).Append("%;background:var(--ok)'></i><i style='width:").Append(Pct(failed))
          .Append("%;background:var(--bad)'></i><i style='width:").Append(Pct(skipped)).Append("%;background:var(--skip)'></i></div>");
        foreach (var s in suites)
        {
            var cases = s.Elements("testcase").ToList();
            sb.Append("<h2>").Append(E((string)s.Attribute("name"))).Append(" <span class='muted'>&ndash; ")
              .Append(cases.Count(c => Status(c) == "passed")).Append(" passed, ").Append(cases.Count(c => Status(c) == "failed")).Append(" failed, ")
              .Append(cases.Count(c => Status(c) == "skipped")).Append(" skipped</span></h2><table><tr><th>Test</th><th>Result</th><th class='num'>Duration</th></tr>");
            foreach (var c in cases)
            {
                var st = Status(c);
                var msg = (string)(c.Element("failure") ?? c.Element("error"))?.Attribute("message");
                var body = (c.Element("failure") ?? c.Element("error"))?.Value;
                sb.Append("<tr><td>").Append(E((string)c.Attribute("name")));
                if (st == "failed") sb.Append("<pre>").Append(E(string.IsNullOrWhiteSpace(body) ? msg : body)).Append("</pre>");
                sb.Append("</td><td><span class='st ").Append(st).Append("'>").Append(st).Append("</span></td><td class='num'>")
                  .Append(st == "skipped" ? "" : D(c, "time").ToString("0.000", CultureInfo.InvariantCulture) + " s").Append("</td></tr>");
            }
            sb.Append("</table>");
        }
        if (suites.Count == 0) sb.Append("<p class='muted'>The result file contains no tests.</p>");
        return sb.Append("</main></body></html>").ToString();
    }
}
