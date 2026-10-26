// tc_policy: guardrails, and action=audit_check - a read-only comparison of the daily log (%LOCALAPPDATA%\TcAgent\logs\
// tcagent-<date>.log, every tool call of every TcAgent process) with the audit log (audit.jsonl). Lists changing calls
// (and failed calls - those are audited too) that have no audit entry, with the likely reason:
//   - the call never ended in that process (IDE hang/dialog, client closed, TcAgent killed) - the audit is written at the end
//   - writing the audit failed ("ERROR Audit: ..." in the log - e.g. a write conflict with another TcAgent process)
//   - the process wrote to another audit file (TCAGENT_AUDIT of that client, or %LOCALAPPDATA% redirected for a packaged
//     client such as Claude Desktop: %LOCALAPPDATA%\Packages\<package>\LocalCache\Local\TcAgent\...)
//   - the call was read-only (never audited by design) - older logs do not show the action, so read actions of changing
//     tools (tc_task list ...) cannot be told apart there
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterPolicy()
    {
        Add("tc_policy", "Shows the guardrails: approved target systems for runtime operations, read-only mode, path of the audit log. " +
                         "action=audit_check [date] (read-only) compares the day's TcAgent log (every tool call of every TcAgent process) with " +
                         "the audit log and lists changing calls that are missing in the audit, with the likely reason.",
            Schema(P("action", "string", "show (default) or audit_check"),
                   P("date", "string", "audit_check: day to check, e.g. 2026-10-12, 12.10. or yesterday (default today)"),
                   P("max", "integer", "audit_check: max. listed calls (default 100)")),
            a => (Str(a, "action") ?? "show").Trim().ToLowerInvariant() switch
            {
                "show" or "" => policy.Describe(),
                "audit_check" or "audit" => AuditCheck.Run(policy.AuditPath, Str(a, "date"), Math.Max(1, Int(a, "max", 100))),
                _ => throw new Exception("action: show (default) or audit_check")
            });
    }
}

public static class AuditCheck
{
    sealed class Call
    {
        public DateTime Start; public DateTime? End;
        public int Pid; public string Client, Tool, Attrs, Error;
        public bool NoStart, Expected, Uncertain;
        public string AuditedIn, Reason;
    }

    sealed class Proc { public string Version, AuditPath, Mode, Client; public bool LogsAttrs; public int Calls; }

    sealed class Entry { public DateTime Time; public string Tool, File; public int Pid; public bool Used; }

    static readonly Regex Line = new(@"^(\d{2}):(\d{2}):(\d{2})\.(\d{3}) \[(\d+)(?: ([^\]]*))?\] (.*)$", RegexOptions.Compiled);
    static readonly Regex Started = new(@"^((?:tc|ads)_\w+) started(?: \[([^\]]*)\])?$", RegexOptions.Compiled);
    static readonly Regex Done = new(@"^((?:tc|ads)_\w+) done \((\d+) ms\)$", RegexOptions.Compiled);
    static readonly Regex Failed = new(@"^ERROR ((?:tc|ads)_\w+): (.*)$", RegexOptions.Compiled);
    static readonly Regex AuditError = new(@"^ERROR Audit: (.*)$", RegexOptions.Compiled);
    static readonly Regex Version = new(@"^TcAgent (\S+) started \(pid (\d+)\)", RegexOptions.Compiled);
    static readonly Regex PolicyLine = new(@"^Policy: targets=\[.*?\] mode=(\S+) audit=(.+)$", RegexOptions.Compiled);
    static readonly Regex Late = new(@"^((?:tc|ads)_\w+) finished after dialog:", RegexOptions.Compiled);

    public static DateTime ParseDay(string s)
    {
        var today = DateTime.Today;
        s = (s ?? "").Trim();
        if (s.Length == 0 || s.Equals("today", StringComparison.OrdinalIgnoreCase) || s.Equals("heute", StringComparison.OrdinalIgnoreCase)) return today;
        if (s.Equals("yesterday", StringComparison.OrdinalIgnoreCase) || s.Equals("gestern", StringComparison.OrdinalIgnoreCase)) return today.AddDays(-1);
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyyMMdd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy" },
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d.Date;
        var m = Regex.Match(s, @"^(\d{1,2})\.(\d{1,2})\.?$");          // 12.10. - this year, or last year if that is in the future
        if (m.Success)
        {
            var day = new DateTime(today.Year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value));
            return day > today ? day.AddYears(-1) : day;
        }
        throw new Exception($"date '{s}' not understood - use e.g. 2026-10-12, 12.10. or yesterday");
    }

    /// <summary>%LOCALAPPDATA%\TcAgent and the redirected copies of packaged clients (Packages\*\LocalCache\Local\TcAgent).</summary>
    static List<string> Roots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string> { Path.Combine(local, "TcAgent") };
        try
        {
            var pk = Path.Combine(local, "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.EnumerateDirectories(pk))
                {
                    var r = Path.Combine(d, "LocalCache", "Local", "TcAgent");
                    if (Directory.Exists(r)) roots.Add(r);
                }
        }
        catch { }
        return roots;
    }

    public static string Run(string ownAuditPath, string date, int max)
    {
        var day = ParseDay(date);
        var roots = Roots();
        var o = new StringBuilder($"Audit check for {day:yyyy-MM-dd} (read-only)\n");

        // ---- log: the day and the next one (calls running over midnight end there)
        var calls = new List<Call>();
        var procs = new Dictionary<int, Proc>();
        var auditErrors = new List<(DateTime Time, int Pid, string Msg)>();
        int late = 0;
        var logFiles = new List<string>();
        foreach (var root in roots)
            for (int k = 0; k < 2; k++)
            {
                var f = Path.Combine(root, "logs", $"tcagent-{day.AddDays(k):yyyyMMdd}.log");
                if (!File.Exists(f)) continue;
                if (k == 0) logFiles.Add(f);
                List<string> lines;
                try { lines = SharedFile.ReadLines(f); } catch (Exception ex) { o.AppendLine($"Log not readable: {f} ({ex.Message})"); continue; }
                ParseLog(lines, day.AddDays(k), k == 0, calls, procs, auditErrors, ref late);
            }
        if (logFiles.Count == 0)
            return o.Append($"No TcAgent log for this day (looked in {string.Join(", ", roots.Select(r => Path.Combine(r, "logs")))}).").ToString();

        // ---- audit files: own, defaults of every root, and what each process logged as its audit path
        var auditFiles = new List<string> { ownAuditPath };
        auditFiles.AddRange(roots.Select(r => Path.Combine(r, "audit.jsonl")));
        auditFiles.AddRange(procs.Values.Select(p => p.AuditPath).Where(p => !string.IsNullOrWhiteSpace(p)));
        auditFiles = auditFiles.Where(f => f != null).Select(f => { try { return Path.GetFullPath(f.Trim()); } catch { return f; } })
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = new List<Entry>();
        var auditInfo = new List<string>();
        foreach (var f in auditFiles)
        {
            if (!File.Exists(f)) { auditInfo.Add($"  {f}: not found"); continue; }
            int n = 0;
            try
            {
                foreach (var l in SharedFile.ReadLines(f))
                {
                    if (l.Length < 20) continue;
                    JsonObject j;
                    try { j = JsonNode.Parse(l) as JsonObject; } catch { continue; }
                    if (j?["time"] is not JsonValue tv || !DateTimeOffset.TryParse(tv.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    var clock = t.DateTime;               // wall clock as written - the log uses the same local time
                    if (clock < day.AddMinutes(-5) || clock > day.AddDays(1).AddHours(2)) continue;
                    int.TryParse(j["pid"]?.ToString(), out var entryPid);
                    entries.Add(new Entry { Time = clock, Tool = j["tool"]?.ToString(), File = f, Pid = entryPid });
                    if (clock.Date == day) n++;
                }
                auditInfo.Add($"  {f}: {n} entries that day");
            }
            catch (Exception ex) { auditInfo.Add($"  {f}: not readable ({ex.Message})"); }
        }

        // ---- which calls need an audit entry
        foreach (var c in calls)
        {
            procs.TryGetValue(c.Pid, out var p);
            if (c.Error != null) { c.Expected = true; continue; }        // failed calls are always audited
            var args = new JsonObject();
            var attrs = c.Attrs ?? "";
            var am = Regex.Match(attrs, @"(?:^|\s)action=(\S+)");
            if (am.Success) args["action"] = am.Groups[1].Value;
            if (Regex.IsMatch(attrs, @"(?:^|\s)dryRun(?:\s|$)")) args["dryRun"] = true;
            var cls = Policy.ClassOf(c.Tool, args);
            if (cls != ToolClass.Read) c.Expected = true;
            else if (p?.LogsAttrs != true && c.Attrs == null && Policy.ClassOf(c.Tool) != ToolClass.Read) { c.Expected = true; c.Uncertain = true; }
        }

        // ---- match: same tool (and pid if the entry has one), written at the end of the call
        foreach (var c in calls.Where(y => y.Expected).OrderBy(y => y.End ?? y.Start))
        {
            var at = c.End ?? c.Start;
            var hit = entries.Where(e => !e.Used && e.Tool == c.Tool && (e.Pid == 0 || e.Pid == c.Pid) &&
                                         e.Time >= at.AddSeconds(-2) && e.Time <= (c.End == null ? at.AddHours(2) : at.AddSeconds(30)))
                             .OrderBy(e => Math.Abs((e.Time - at).TotalMilliseconds)).FirstOrDefault();
            if (hit != null)
            {
                hit.Used = true;
                procs.TryGetValue(c.Pid, out var p0);
                var own = p0?.AuditPath != null ? SafeFull(p0.AuditPath) : SafeFull(ownAuditPath);
                if (!string.Equals(hit.File, own, StringComparison.OrdinalIgnoreCase)) c.AuditedIn = hit.File;
                continue;
            }
            c.Reason = Reason(c, procs, auditErrors);
        }

        // ---- report
        o.AppendLine("Log: " + string.Join(", ", logFiles));
        o.AppendLine("Audit files:\n" + string.Join("\n", auditInfo));
        o.AppendLine("Processes:");
        foreach (var (pid, p) in procs.Where(kv => kv.Value.Calls > 0).OrderBy(kv => kv.Key))
            o.AppendLine($"  pid {pid}: TcAgent {p.Version ?? "? (started before this day)"}{(string.IsNullOrEmpty(p.Client) ? "" : ", client " + p.Client)}" +
                         $", {p.Calls} calls, audit {p.AuditPath ?? "? (not in this log)"}{(p.Mode != null ? ", mode " + p.Mode : "")}");
        var mine = calls;
        o.AppendLine($"Calls: {mine.Count} - {mine.Count(c => !c.Expected)} read-only (never audited by design), " +
                     $"{mine.Count(c => c.Expected && c.Error == null)} changing, {mine.Count(c => c.Error != null)} failed (audited as errors)" +
                     (late > 0 ? $", {late} completed after an IDE dialog" : ""));
        var missing = calls.Where(c => c.Reason != null && !c.Uncertain).OrderBy(c => c.Start).ToList();
        var unsure = calls.Where(c => c.Reason != null && c.Uncertain).OrderBy(c => c.Start).ToList();
        var elsewhere = calls.Where(c => c.AuditedIn != null).ToList();
        o.AppendLine(missing.Count == 0 ? "\nNo changing or failed call is missing in the audit." : $"\nMISSING in the audit: {missing.Count}");
        foreach (var c in missing.Take(max)) o.AppendLine("  " + Describe(c));
        if (missing.Count > max) o.AppendLine($"  ... {missing.Count - max} more (max=)");
        if (unsure.Count > 0)
        {
            o.AppendLine($"\nNot in the audit, action unknown ({unsure.Count}) - the log of this TcAgent version does not show the action; " +
                         "read-only actions (list/show/read ...) are not audited by design:");
            foreach (var c in unsure.Take(max)) o.AppendLine("  " + Describe(c));
        }
        if (elsewhere.Count > 0)
        {
            o.AppendLine($"\nAudited, but in another file than expected ({elsewhere.Count}):");
            foreach (var g in elsewhere.GroupBy(c => c.AuditedIn)) o.AppendLine($"  {g.Key}: {g.Count()} calls (pids {string.Join(", ", g.Select(c => c.Pid).Distinct())})");
        }
        if (auditErrors.Count > 0)
        {
            o.AppendLine($"\nAudit write errors in the log ({auditErrors.Count}):");
            foreach (var (t, pid, msg) in auditErrors.Take(20)) o.AppendLine($"  {t:HH:mm:ss} pid {pid}: {msg}");
        }
        if (missing.Count + unsure.Count > 0)
            o.AppendLine("\nPossible causes: the audit is written when a call ENDS (a call that hangs in an IDE dialog or whose process is killed " +
                         "leaves none); older TcAgent versions lost entries when two TcAgent processes wrote audit.jsonl at the same moment " +
                         "(now shared append with retries); a different TCAGENT_AUDIT per client; packaged clients (desktop apps from the Store) " +
                         "write %LOCALAPPDATA% into %LOCALAPPDATA%\\Packages\\<package>\\LocalCache\\Local - those copies are included above when " +
                         "readable; calls after midnight are in the next day's log.");
        return o.ToString();
    }

    static string SafeFull(string p) { try { return Path.GetFullPath(p.Trim()); } catch { return p; } }

    static string Describe(Call c) =>
        $"{c.Start:HH:mm:ss.fff} pid {c.Pid}{(string.IsNullOrEmpty(c.Client) ? "" : " " + c.Client)} {c.Tool}" +
        (string.IsNullOrEmpty(c.Attrs) ? "" : $" [{c.Attrs}]") +
        (c.Error != null ? $" ERROR {(c.Error.Length > 120 ? c.Error[..120] + "..." : c.Error)}" : c.End != null ? $" (ended {c.End.Value:HH:mm:ss})" : "") +
        $" - {c.Reason}";

    static string Reason(Call c, Dictionary<int, Proc> procs, List<(DateTime Time, int Pid, string Msg)> auditErrors)
    {
        if (c.End == null)
            return "no end in the log: the call never finished in this process (IDE dialog/hang, client closed or TcAgent killed) - the audit is written when a call ends";
        var at = c.End.Value;
        var err = auditErrors.FirstOrDefault(e => e.Pid == c.Pid && e.Time >= at.AddSeconds(-1) && e.Time <= at.AddSeconds(30));
        if (err.Msg != null) return "writing the audit failed: " + err.Msg;
        procs.TryGetValue(c.Pid, out var p);
        if (p?.AuditPath != null && !File.Exists(p.AuditPath))
            return $"this process wrote its audit to {p.AuditPath} - not readable from here (other user/redirected %LOCALAPPDATA%)";
        if (p?.Version == null)
            return "no audit entry found; the process started before this day (audit path and version unknown) - check its TCAGENT_AUDIT";
        return "no audit entry found - likely lost in a write conflict with another TcAgent process (older versions), or the audit went to a redirected/other file";
    }

    static void ParseLog(List<string> lines, DateTime day, bool primary, List<Call> calls, Dictionary<int, Proc> procs,
                         List<(DateTime Time, int Pid, string Msg)> auditErrors, ref int late)
    {
        var open = new Dictionary<int, List<Call>>();
        foreach (var c in calls.Where(y => y.End == null && !y.NoStart))     // calls of the previous day still running
        {
            if (!open.TryGetValue(c.Pid, out var lst)) open[c.Pid] = lst = new List<Call>();
            lst.Add(c);
        }
        foreach (var l in lines)
        {
            var m = Line.Match(l);
            if (!m.Success) continue;                                        // e.g. stack trace lines of an error
            var time = day.Add(new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
            int pid = int.Parse(m.Groups[5].Value);
            var client = m.Groups[6].Success ? m.Groups[6].Value : null;
            var msg = m.Groups[7].Value;
            if (!procs.TryGetValue(pid, out var p)) procs[pid] = p = new Proc { Client = client };
            Match x;
            if ((x = Version.Match(msg)).Success) { p.Version = x.Groups[1].Value; continue; }
            if ((x = PolicyLine.Match(msg)).Success) { p.Mode = x.Groups[1].Value; p.AuditPath = x.Groups[2].Value.Trim(); continue; }
            if ((x = AuditError.Match(msg)).Success) { auditErrors.Add((time, pid, x.Groups[1].Value)); continue; }
            if (Late.IsMatch(msg)) { late++; continue; }
            if ((x = Started.Match(msg)).Success)
            {
                if (!primary) continue;                                      // only calls that start on the checked day
                var c = new Call { Start = time, Pid = pid, Client = client, Tool = x.Groups[1].Value, Attrs = x.Groups[2].Success ? x.Groups[2].Value : null };
                if (c.Attrs != null) p.LogsAttrs = true;
                p.Calls++;
                calls.Add(c);
                if (!open.TryGetValue(pid, out var lst)) open[pid] = lst = new List<Call>();
                lst.Add(c);
                continue;
            }
            bool isDone = (x = Done.Match(msg)).Success;
            if (!isDone && !(x = Failed.Match(msg)).Success) continue;
            var tool = x.Groups[1].Value;
            Call hit = null;
            if (open.TryGetValue(pid, out var cands))
            {
                hit = cands.LastOrDefault(cc => cc.Tool == tool);
                if (hit != null) cands.Remove(hit);
            }
            if (hit != null) { hit.End = time; if (!isDone) hit.Error = x.Groups[2].Value; continue; }
            // an error without start: blocked by the policy, rejected confirmation ... (audited as error as well)
            if (!isDone && primary)
            {
                p.Calls++;
                calls.Add(new Call { Start = time, End = time, Pid = pid, Client = client, Tool = tool, Error = x.Groups[2].Value, NoStart = true });
            }
        }
    }
}
