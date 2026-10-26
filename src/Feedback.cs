// Feedback store as a control loop: agents report problems (tc_feedback), developers list, summarize and resolve them
// (tc_feedback action=list|summary|resolve, TcAgent.exe feedback [--open] [--markdown out.md]).
//
// Location (TCAGENT_FEEDBACK):
//   not set                 <install>\..\..\feedback\feedback.jsonl (relative to TcAgent.exe; VSIX: %LOCALAPPDATA%\TcAgent\feedback)
//   a .jsonl file           that single file (as before)
//   a folder or UNC path    shared/team location: every machine writes its own file per day
//                           (feedback-<yyyyMMdd>-<MACHINE>.jsonl) - no write conflicts; all *.jsonl files there are read
// Records (one JSON object per line):
//   entry   {id, time, status:"open", type, severity, tool, summary, details, expected, workaround, serverVersion, client, machine, solution}
//   status  {kind:"status", id, status: resolved|wontfix|duplicate|open, note, version, time, machine}
// Status changes are appended as own records instead of editing lines - safe with several writers. The newest status wins.
// Entries of older versions without id get a stable id derived from time, tool and summary.
//
// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Feedback
{
    static readonly JsonSerializerOptions JsonLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly string[] Fields = { "type", "severity", "tool", "summary", "details", "expected", "workaround" };
    static readonly string[] Statuses = { "open", "resolved", "wontfix", "duplicate" };

    public static string DefaultFile => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "feedback", "feedback.jsonl"));

    /// <summary>Folder = shared location with one file per day and machine; otherwise a single .jsonl file.</summary>
    public static (bool Folder, string Path) Location(string overridePath = null)
    {
        var p = (overridePath ?? Environment.GetEnvironmentVariable("TCAGENT_FEEDBACK"))?.Trim().Trim('"');
        if (string.IsNullOrEmpty(p)) return (false, DefaultFile);
        bool folder = Directory.Exists(p) || p.EndsWith('\\') || p.EndsWith('/') ||
                      !File.Exists(p) && string.IsNullOrEmpty(System.IO.Path.GetExtension(p));
        return (folder, folder ? System.IO.Path.TrimEndingDirectorySeparator(p) : p);
    }

    static string Safe(string s) => string.Concat((s ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>File new records of this machine go to.</summary>
    public static string WriteFile(string overridePath = null)
    {
        var (folder, path) = Location(overridePath);
        return folder ? System.IO.Path.Combine(path, $"feedback-{DateTime.Now:yyyyMMdd}-{Safe(Environment.MachineName)}.jsonl") : path;
    }

    static IEnumerable<string> ReadFiles(string overridePath)
    {
        var (folder, path) = Location(overridePath);
        if (!folder) return File.Exists(path) ? new[] { path } : Array.Empty<string>();
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return Directory.EnumerateFiles(path, "*.jsonl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Append(string file, JsonObject record)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        SharedFile.Append(file, record.ToJsonString(JsonLine) + Environment.NewLine);
    }

    /// <summary>Stable id for entries without one (older versions): hash of time, tool and summary.</summary>
    static string StableId(JsonObject e)
    {
        var key = $"{e["time"]}|{e["tool"]}|{e["summary"]}";
        return "FB-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..8];
    }

    static string NewId() => "FB-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    public sealed class Item
    {
        public string Id, Time, Type, Severity, Tool, Summary, Details, Expected, Workaround, Version, Client, Machine, Solution, File;
        public string Status = "open", StatusNote, StatusVersion, StatusTime;
    }

    static string S(JsonObject o, string k) => o[k] is JsonValue v ? v.ToString() : null;

    /// <summary>All entries with their current status (newest status record wins).</summary>
    public static List<Item> Load(string overridePath = null)
    {
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<JsonObject>();
        foreach (var f in ReadFiles(overridePath))
        {
            List<string> lines;
            try { lines = SharedFile.ReadLines(f); } catch (Exception ex) { Log.Error($"Feedback not readable ({f}): {ex.Message}"); continue; }
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                JsonObject o;
                try { o = JsonNode.Parse(l) as JsonObject; } catch { continue; }
                if (o == null) continue;
                if (S(o, "kind") == "status") { changes.Add(o); continue; }
                var id = S(o, "id") ?? StableId(o);
                if (items.ContainsKey(id)) continue;
                items[id] = new Item
                {
                    Id = id, Time = S(o, "time"), Type = S(o, "type"), Severity = S(o, "severity"), Tool = S(o, "tool"),
                    Summary = S(o, "summary"), Details = S(o, "details"), Expected = S(o, "expected"), Workaround = S(o, "workaround"),
                    Version = S(o, "serverVersion") ?? S(o, "version"), Client = S(o, "client"), Machine = S(o, "machine"),
                    Solution = S(o, "solution"), File = f, Status = S(o, "status") ?? "open"
                };
            }
        }
        foreach (var c in changes.OrderBy(ch => TimeOf(S(ch, "time"))))
            if (S(c, "id") is { } id && items.TryGetValue(id, out var it))
            {
                it.Status = S(c, "status") ?? "resolved";
                it.StatusNote = S(c, "note"); it.StatusVersion = S(c, "version"); it.StatusTime = S(c, "time");
            }
        return items.Values.OrderBy(i => TimeOf(i.Time)).ToList();
    }

    static DateTimeOffset TimeOf(string t) => DateTimeOffset.TryParse(t, out var d) ? d : DateTimeOffset.MinValue;

    /// <summary>New report (tc_feedback without action). Returns the confirmation text with id and file.</summary>
    public static string Report(Func<string, string> arg, string solution)
    {
        if (string.IsNullOrWhiteSpace(arg("type")) || string.IsNullOrWhiteSpace(arg("summary")))
            throw new Exception("type and summary are required for a report (action=report is the default; list/summary/resolve manage existing entries)");
        var id = NewId();
        var entry = new JsonObject
        {
            ["id"] = id, ["time"] = DateTime.Now.ToString("o"), ["status"] = "open",
            ["serverVersion"] = Program.ServerVersion, ["client"] = Program.ClientName, ["machine"] = Environment.MachineName,
            ["solution"] = solution
        };
        foreach (var k in Fields)
            if (arg(k) is { } v) entry[k] = v;
        var file = WriteFile();
        Append(file, entry);
        return $"Thanks - feedback {id} saved to {file}";
    }

    /// <summary>Sets the status of one or more entries (ids separated by ','; 'FB-' may be omitted).</summary>
    public static string Resolve(string ids, string status, string note, string version, string overridePath = null)
    {
        status = string.IsNullOrWhiteSpace(status) ? "resolved" : status.Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) throw new Exception("status: " + string.Join(", ", Statuses));
        if (string.IsNullOrWhiteSpace(ids)) throw new Exception("id is required (e.g. FB-1A2B3C4D, several separated by ',')");
        var items = Load(overridePath).ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var o = new StringBuilder();
        var file = WriteFile(overridePath);
        foreach (var raw in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = raw.StartsWith("FB-", StringComparison.OrdinalIgnoreCase) ? raw : "FB-" + raw;
            if (!items.TryGetValue(id, out var it)) { o.AppendLine($"  {raw}: not found"); continue; }
            var rec = new JsonObject
            {
                ["kind"] = "status", ["id"] = it.Id, ["status"] = status, ["time"] = DateTime.Now.ToString("o"),
                ["machine"] = Environment.MachineName, ["by"] = "TcAgent " + Program.ServerVersion
            };
            if (!string.IsNullOrWhiteSpace(note)) rec["note"] = note;
            if (!string.IsNullOrWhiteSpace(version)) rec["version"] = version;
            Append(file, rec);
            o.AppendLine($"  {it.Id}: {it.Status} -> {status}  ({it.Tool ?? "-"}: {it.Summary})");
        }
        return $"Status changes written to {file}:\n" + o;
    }

    static int SeverityRank(string s) => (s ?? "").ToLowerInvariant() switch { "blocker" => 0, "major" => 1, "minor" => 2, _ => 3 };

    static IEnumerable<Item> Filter(List<Item> items, string status, string tool)
    {
        status = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
        return items.Where(i => (status == "all" || string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase)) &&
                                (string.IsNullOrWhiteSpace(tool) || (i.Tool ?? "").Contains(tool.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Entries grouped by tool (most entries first), inside by severity.</summary>
    public static string List(string status, string tool, int max, string overridePath = null)
    {
        var all = Load(overridePath);
        var sel = Filter(all, status, tool).ToList();
        var o = new StringBuilder($"Feedback ({Describe(overridePath)}): {sel.Count} of {all.Count} entries" +
                                  $" (status={(string.IsNullOrWhiteSpace(status) ? "open" : status)}{(string.IsNullOrWhiteSpace(tool) ? "" : ", tool~" + tool)})\n");
        int n = 0;
        foreach (var g in sel.GroupBy(x => x.Tool ?? "(no tool)").OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
        {
            o.AppendLine($"\n## {g.Key} ({g.Count()})");
            foreach (var i in g.OrderBy(x => SeverityRank(x.Severity)).ThenByDescending(x => TimeOf(x.Time)))
            {
                if (++n > max) { o.AppendLine($"\n... more - narrow with tool= or raise max (max={max})"); return o.ToString(); }
                o.AppendLine($"  {i.Id}  [{i.Severity ?? "-"}/{i.Type ?? "-"}] {i.Summary}");
                o.AppendLine($"      {Short(i.Time)}  v{i.Version ?? "?"}  {i.Machine ?? ""}  {i.Client ?? ""}" +
                             (i.Status != "open" ? $"  -> {i.Status}{(i.StatusVersion != null ? " in " + i.StatusVersion : "")}{(i.StatusNote != null ? ": " + i.StatusNote : "")}" : ""));
            }
        }
        if (sel.Count > 0) o.AppendLine("\nDetails: tc_feedback action=list tool=<tool> or TcAgent.exe feedback --markdown <file>. Close: tc_feedback action=resolve id=<id> note=... version=...");
        return o.ToString();
    }

    public static string Summary(string overridePath = null)
    {
        var all = Load(overridePath);
        var open = all.Where(i => i.Status == "open").ToList();
        var o = new StringBuilder($"Feedback ({Describe(overridePath)}): {all.Count} entries - " +
                                  string.Join(", ", all.GroupBy(i => i.Status).OrderBy(g => Array.IndexOf(Statuses, g.Key)).Select(g => $"{g.Count()} {g.Key}")) + "\n");
        if (open.Count == 0) return o.Append("No open entries.").ToString();
        o.AppendLine("\nOpen by severity: " + string.Join(", ", open.GroupBy(i => i.Severity ?? "-").OrderBy(g => SeverityRank(g.Key)).Select(g => $"{g.Key} {g.Count()}")));
        o.AppendLine("Open by type: " + string.Join(", ", open.GroupBy(i => i.Type ?? "-").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        o.AppendLine("Open by tool:");
        foreach (var g in open.GroupBy(x => x.Tool ?? "(no tool)").OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Take(30))
            o.AppendLine($"  {g.Key,-24} {g.Count(),3}  " + string.Join(", ", g.GroupBy(y => y.Severity ?? "-").OrderBy(y => SeverityRank(y.Key)).Select(y => $"{y.Key} {y.Count()}")));
        o.AppendLine("Open by TcAgent version: " + string.Join(", ", open.GroupBy(i => i.Version ?? "?").OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        var last = open.OrderByDescending(i => TimeOf(i.Time)).First();
        o.AppendLine($"Newest open: {last.Id} {Short(last.Time)} {last.Tool}: {last.Summary}");
        return o.ToString();
    }

    static string Describe(string overridePath)
    {
        var (folder, path) = Location(overridePath);
        return folder ? $"folder {path}" : path;
    }

    static string Short(string time) => DateTimeOffset.TryParse(time, out var d) ? d.ToString("yyyy-MM-dd HH:mm") : time ?? "?";

    /// <summary>Markdown report - one section per tool, one block per entry, ready to paste into a GitHub issue.</summary>
    public static string Markdown(bool openOnly, string overridePath = null)
    {
        var all = Load(overridePath);
        var sel = all.Where(i => !openOnly || i.Status == "open").ToList();
        var o = new StringBuilder();
        o.AppendLine($"# TcAgent feedback{(openOnly ? " - open items" : "")}");
        o.AppendLine();
        o.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by TcAgent {Program.ServerVersion} from `{Describe(overridePath)}` - " +
                     $"{sel.Count} of {all.Count} entries" +
                     (openOnly ? "" : " (" + string.Join(", ", all.GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}")) + ")") + ".");
        if (sel.Count == 0) return o.AppendLine().AppendLine("No entries.").ToString();
        o.AppendLine();
        o.AppendLine("| Tool | Open | Blocker | Major | Minor |");
        o.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in sel.GroupBy(x => x.Tool ?? "(no tool)").OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            o.AppendLine($"| `{g.Key}` | {g.Count(i => i.Status == "open")} | {g.Count(i => SeverityRank(i.Severity) == 0)} | " +
                         $"{g.Count(i => SeverityRank(i.Severity) == 1)} | {g.Count(i => SeverityRank(i.Severity) == 2)} |");
        foreach (var g in sel.GroupBy(x => x.Tool ?? "(no tool)").OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
        {
            o.AppendLine().AppendLine($"## `{g.Key}`");
            foreach (var i in g.OrderBy(x => x.Status == "open" ? 0 : 1).ThenBy(x => SeverityRank(x.Severity)).ThenByDescending(x => TimeOf(x.Time)))
            {
                o.AppendLine().AppendLine($"### {i.Id}: {Md(i.Summary)}");
                o.AppendLine();
                o.AppendLine($"- **Status:** {i.Status}" + (i.Status != "open"
                    ? $"{(i.StatusVersion != null ? " in " + Md(i.StatusVersion) : "")}{(i.StatusNote != null ? " - " + Md(i.StatusNote) : "")}" : ""));
                o.AppendLine($"- **Type / severity:** {i.Type ?? "-"} / {i.Severity ?? "-"}");
                o.AppendLine($"- **Reported:** {Short(i.Time)}, TcAgent {i.Version ?? "?"}, client {Md(i.Client ?? "?")}" + (i.Machine != null ? $", machine {Md(i.Machine)}" : ""));
                if (!string.IsNullOrWhiteSpace(i.Solution)) o.AppendLine($"- **Solution:** {Md(i.Solution)}");
                void Block(string title, string text)
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    o.AppendLine().AppendLine($"**{title}**").AppendLine().AppendLine("```text").AppendLine(text.Replace("```", "'''").TrimEnd()).AppendLine("```");
                }
                Block("Details", i.Details);
                Block("Expected", i.Expected);
                Block("Workaround", i.Workaround);
            }
        }
        return o.ToString();
    }

    static string Md(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");

    // ------------------------------------------------------------------ sharing with the TcAgent developers
    /// <summary>Where a report is shared: settings.json "feedbackUrl" (an issue form of the repository), default the TcAgent repository.</summary>
    public static string ShareUrl()
    {
        try
        {
            var f = System.IO.Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["feedbackUrl"]?.ToString() is { Length: > 0 } u) return u;
        }
        catch { }
        return "https://github.com/ThorstenBrach/TcAgent/issues/new";
    }

    /// <summary>One report as the text of an issue - the title in the first line.</summary>
    public static string Issue(Item i)
    {
        var o = new StringBuilder($"[{i.Tool ?? "TcAgent"}] {i.Summary}\n\n");
        o.AppendLine($"- **Type / severity:** {i.Type ?? "-"} / {i.Severity ?? "-"}");
        o.AppendLine($"- **TcAgent:** {i.Version ?? "?"}, client {i.Client ?? "?"}");
        if (!string.IsNullOrWhiteSpace(i.Tool)) o.AppendLine($"- **Tool:** `{i.Tool}`");
        o.AppendLine($"- **Reported:** {Short(i.Time)} ({i.Id})");
        void Block(string title, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            o.AppendLine().AppendLine($"**{title}**").AppendLine().AppendLine("```text").AppendLine(text.Replace("```", "'''").TrimEnd()).AppendLine("```");
        }
        Block("Details", i.Details);
        Block("Expected", i.Expected);
        Block("Workaround", i.Workaround);
        return o.ToString();
    }

    /// <summary>Replaces what identifies the user, the computer, the company or the project: the private terms (names of this
    /// computer, user, solution and projects, privateTerms of the settings), machine and solution of the entry, user folders,
    /// paths of the solution, network paths and IP addresses / AmsNetIds. Returns the text and what was replaced.</summary>
    public static (string Text, List<string> Replaced) Anonymize(string text, IEnumerable<string> terms, Item i = null)
    {
        var replaced = new List<string>();
        string Sub(string t, string pattern, string with) =>
            System.Text.RegularExpressions.Regex.Replace(t, pattern, m => { if (!replaced.Contains(m.Value)) replaced.Add(m.Value); return with; },
                                                         System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var t = text ?? "";
        if (i?.Solution is { Length: > 0 } sol)
        {
            var path = sol.Contains('|') ? sol.Split('|').Select(x => x.Trim()).FirstOrDefault(x => x.Contains('\\')) ?? sol : sol;
            foreach (var p in new[] { System.IO.Path.GetDirectoryName(path), path }.Where(p => !string.IsNullOrWhiteSpace(p) && p.Length > 3).OrderByDescending(p => p!.Length))
                t = Sub(t, System.Text.RegularExpressions.Regex.Escape(p!), "<solution folder>");
        }
        t = Sub(t, @"[A-Za-z]:\\Users\\[^\\\s""']+", "%USERPROFILE%");
        t = Sub(t, @"\\\\[A-Za-z0-9._-]+\\[^\s""']+", @"\\<server>\<share>");
        const string oct = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";
        t = Sub(t, $@"(?<![\d.]){oct}(?:\.{oct}){{3}}(?:\.{oct}\.{oct})?(?![\d.])", "<address>");          // IP / AmsNetId, not a version like 3.1.4026.19
        var all = terms.Concat(new[] { i?.Machine }).Where(x => !string.IsNullOrWhiteSpace(x) && x!.Length >= 3)
                       .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x!.Length);
        foreach (var term in all)
        {
            var with = term!.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || term.Equals(i?.Machine, StringComparison.OrdinalIgnoreCase) ? "<computer>"
                     : term.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) ? "<user>" : "<name>";
            t = Sub(t, @"(?<![\w])" + System.Text.RegularExpressions.Regex.Escape(term) + @"(?![\w])", with);
        }
        return (t, replaced);
    }

    /// <summary>TcAgent.exe feedback [--open] [--markdown out.md] [--path file-or-folder] [--summary]</summary>
    public static int RunCli(string[] args)
    {
        bool open = false, summary = false; string md = null, path = null;
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            string Val() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : throw new ArgumentException($"{a} needs a value");
            try
            {
                switch (a.ToLowerInvariant())
                {
                    case "--open": open = true; break;
                    case "--summary": summary = true; break;
                    case "--markdown": case "--out": case "-o": md = Val(); break;
                    case "--path": case "--from": path = Val(); break;
                    default: throw new ArgumentException("Unknown option " + a);
                }
            }
            catch (ArgumentException ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Cli.Usage; }
        }
        try
        {
            if (summary) { Console.WriteLine(Summary(path)); return Cli.Ok; }
            var text = Markdown(open, path);
            if (md == null) { Console.WriteLine(text); return Cli.Ok; }
            var full = System.IO.Path.GetFullPath(md);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
            Console.WriteLine($"[tcagent] feedback report written: {full}");
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR (feedback): " + ex.Message); return Cli.Infra; }
    }
}

public sealed partial class ToolRegistry
{
    void RegisterFeedback()
    {
        Add("tc_feedback", "Reports a problem, a missing feature or a suggestion for improvement to the TcAgent developers. " +
                           "Please always use it when a tool fails, behaves unexpectedly, a task could only be solved via a workaround, or a tool is missing. " +
                           "Without action (= report) type and summary are required. For maintainers: action=list (open entries grouped by tool and " +
                           "severity), summary (counts), resolve (id, note, version - mark entries as fixed). The store can be a shared team folder (TCAGENT_FEEDBACK).",
            Schema(P("action", "string", "report (default), list, summary or resolve"),
                   P("type", "string", "report: bug | missing_feature | usability | docs"),
                   P("summary", "string", "report: one sentence - what is the problem / what is missing"),
                   P("tool", "string", "report: affected tool, e.g. tc_plc_create; list: only entries of tools containing this text"),
                   P("details", "string", "report: call/arguments, error message, context (TwinCAT version, project type)"),
                   P("expected", "string", "report: what should have happened"),
                   P("workaround", "string", "report: how the task was solved anyway (if at all)"),
                   P("severity", "string", "report: blocker | major | minor"),
                   P("status", "string", "list: open (default), resolved, wontfix, duplicate or all; resolve: new status (default resolved, open = reopen)"),
                   P("id", "string", "resolve: entry id(s), e.g. FB-1A2B3C4D (several separated by ',')"),
                   P("note", "string", "resolve: what was done / why"),
                   P("version", "string", "resolve: TcAgent version containing the fix, e.g. 0.21.0"),
                   P("max", "integer", "list: max. entries (default 200)")),
            a =>
            {
                switch ((Str(a, "action") ?? "report").Trim().ToLowerInvariant())
                {
                    case "report":
                    case "":
                        return Feedback.Report(k => Str(a, k), tc.InstanceInfo);
                    case "list": return Feedback.List(Str(a, "status"), Str(a, "tool"), Math.Max(1, Int(a, "max", 200)));
                    case "summary": return Feedback.Summary();
                    case "resolve": return Feedback.Resolve(Str(a, "id"), Str(a, "status"), Str(a, "note"), Str(a, "version"));
                    default: throw new Exception("action: report (default), list, summary or resolve");
                }
            });
    }
}
