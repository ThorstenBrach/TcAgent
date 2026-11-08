// TcAgent.exe advise: Advisor Shadow without a window (Advisor.cs). It starts the AI client chosen in the chat
// (chat-backends.json "default") with its own MCP configuration: this TcAgent.exe with TCAGENT_MODE=ask and TCAGENT_ADVISOR,
// so the run can only read - the policy enforces it, not the prompt. The context (what changed, what failed) is collected
// here, without an AI: git log and diff, the results of the pipeline.
//
//   TcAgent.exe advise [run] --solution <sln> [--trigger nightly|commit|pipeline] [--since "24 hours ago"] [--results <dir>] [--minutes 15]
//   TcAgent.exe advise list  --solution <sln> [--open] [--json]
//   TcAgent.exe advise rate  --solution <sln> --id AD-… --rating right|partly|wrong|n/a [--comment …]
//   TcAgent.exe advise stats --solution <sln> [--json]
//
// For now only Claude Code runs without a window: its --strict-mcp-config lets TcAgent set the read-only mode itself.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class CliAdvisor
{
    sealed class Args
    {
        public string Sub = "run", Solution, Trigger = "nightly", Since = "24 hours ago", Results, Id, Rating, Comment, Out;
        public int Minutes = 15, MaxTurns = 80;
        public bool Open, Json, DryRun;
    }

    public const string Usage =
        "  TcAgent.exe advise [run] --solution <sln> [--trigger nightly|commit|pipeline] [--since \"24 hours ago\"] [--results <dir>]\n" +
        "                     [--minutes 15] [--out <log file>] [--dry-run]\n" +
        "      Advisor Shadow: an AI client without a window reads the project and records advice with an order (.tcagent\\advisor).\n" +
        "      It changes nothing (TCAGENT_MODE=ask). --dry-run prints the prompt only.\n" +
        "  TcAgent.exe advise list|stats --solution <sln> [--open] [--json]\n" +
        "  TcAgent.exe advise rate --solution <sln> --id AD-... --rating right|partly|wrong|n/a [--comment <text>]";

    static Args Parse(string[] a)
    {
        var o = new Args();
        int i = 1;
        if (i < a.Length && !a[i].StartsWith("--")) o.Sub = a[i++].ToLowerInvariant();
        for (; i < a.Length; i++)
        {
            string V() => i + 1 < a.Length ? a[++i] : throw new ArgumentException(a[i] + " needs a value");
            switch (a[i].ToLowerInvariant())
            {
                case "--solution": o.Solution = V(); break;
                case "--trigger": o.Trigger = V().ToLowerInvariant(); break;
                case "--since": o.Since = V(); break;
                case "--results": o.Results = V(); break;
                case "--minutes": o.Minutes = int.Parse(V()); break;
                case "--max-turns": o.MaxTurns = int.Parse(V()); break;
                case "--out": o.Out = V(); break;
                case "--id": o.Id = V(); break;
                case "--rating": o.Rating = V(); break;
                case "--comment": o.Comment = V(); break;
                case "--open": o.Open = true; break;
                case "--json": o.Json = true; break;
                case "--dry-run": o.DryRun = true; break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        // in a pipeline: the only solution of the repository (as TcAgent.exe ci finds it)
        if (string.IsNullOrEmpty(o.Solution))
        {
            var found = Directory.EnumerateFiles(Environment.CurrentDirectory, "*.sln", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
                                 .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")).Take(2).ToList();
            o.Solution = found.Count == 1 ? found[0] : throw new ArgumentException("--solution <sln> is required" + (found.Count > 1 ? " (more than one solution found)" : ""));
        }
        o.Solution = Path.GetFullPath(o.Solution);
        if (!File.Exists(o.Solution)) throw new ArgumentException("solution not found: " + o.Solution);
        if (o.Trigger is not ("nightly" or "commit" or "pipeline")) throw new ArgumentException("--trigger nightly, commit or pipeline");
        return o;
    }

    public static int Run(string[] args)
    {
        Args o;
        try { o = Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine(Usage); return Cli.Usage; }
        var dir = Path.GetDirectoryName(o.Solution)!;
        try
        {
            switch (o.Sub)
            {
                case "list":
                {
                    var items = Advisor.Load(dir).Where(i => !o.Open || i.Rating == null).OrderByDescending(i => i.Time).ToList();
                    Console.WriteLine(o.Json ? new JsonArray(items.Select(i => (JsonNode)Advisor.ToJson(i)).ToArray()).ToJsonString() : Advisor.List(items, false, 200));
                    return Cli.Ok;
                }
                case "stats":
                {
                    var q = Advisor.Quotas(Advisor.Load(dir), DateTime.Now);
                    Console.WriteLine(o.Json
                        ? new JsonArray(q.Select(x => (JsonNode)new JsonObject { ["kind"] = x.Kind, ["rated"] = x.Rated, ["right"] = x.Right, ["open"] = x.Open, ["rate"] = x.Rate }).ToArray()).ToJsonString()
                        : q.Count == 0 ? "No advice yet." : string.Join("\n", q.Select(Advisor.FormatQuota)));
                    return Cli.Ok;
                }
                case "rate":
                    Advisor.Rate(dir, o.Id ?? throw new ArgumentException("--id is required"), o.Rating, Environment.UserName, o.Comment);
                    Console.WriteLine($"{o.Id}: {o.Rating}");
                    return Cli.Ok;
                case "run": return RunShadow(o, dir);
                default: throw new ArgumentException("advise run, list, stats or rate");
            }
        }
        catch (ArgumentException ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Usage; }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Infra; }
    }

    // ------------------------------------------------------------------ the run
    static int RunShadow(Args o, string dir)
    {
        var context = Context(o, dir);
        // deterministic first: nothing changed, nothing to review - no AI, no tokens
        if (o.Trigger is "nightly" or "commit" && context.Contains("\n(none)"))
        {
            Console.WriteLine($"Nothing was committed since {o.Since} - no Advisor run.");
            return Cli.Ok;
        }
        var prompt = Prompt(o, context);
        if (o.DryRun) { Console.WriteLine(prompt); return Cli.Ok; }

        var (key, exe) = ChatBackend();
        if (exe == null || exe.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Error.WriteLine($"Advisor Shadow without a window runs with Claude Code for now; the chat uses '{key}'. " +
                                    "Choose Claude in the chat, or start the run in the chat with /advise.");
            return Cli.Usage;
        }
        var claude = Resolve("claude") ?? throw new Exception("Claude Code (claude) not found in PATH");

        // the MCP server of this run: this TcAgent.exe, read only, for this solution
        var mcp = Path.Combine(Path.GetTempPath(), $"tcagent-advisor-{Environment.ProcessId}.json");
        var server = new JsonObject
        {
            ["command"] = Environment.ProcessPath,
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject { ["TCAGENT_MODE"] = "ask", ["TCAGENT_ADVISOR"] = o.Trigger, ["TCAGENT_SOLUTION"] = o.Solution, ["TCAGENT_CLIENT"] = "advisor" },
        };
        File.WriteAllText(mcp, new JsonObject { ["mcpServers"] = new JsonObject { ["tcagent"] = server } }.ToJsonString());

        var started = DateTime.Now;
        var ideBefore = Process.GetProcessesByName("devenv").Select(p => p.Id).ToHashSet();
        var claudeArgs = $"-p --output-format text --strict-mcp-config --mcp-config \"{mcp}\" --allowedTools mcp__tcagent --max-turns {o.MaxTurns}";
        var psi = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{claude}\" {claudeArgs}\"")
            : new ProcessStartInfo(claude, claudeArgs);
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = dir;
        psi.EnvironmentVariables["TCAGENT_CLIENT"] = "advisor";

        Console.WriteLine($"Advisor Shadow ({o.Trigger}) with Claude Code, at most {o.Minutes} min, read only.");
        var log = new StringBuilder();
        bool timedOut;
        using (var p = Process.Start(psi)!)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine("! " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            p.StandardInput.Write(prompt); p.StandardInput.Close();
            timedOut = !p.WaitForExit(o.Minutes * 60_000);
            if (timedOut) try { p.Kill(true); } catch { }
            p.WaitForExit(10_000);
        }
        try { File.Delete(mcp); } catch { }
        // an IDE the run opened without a window is closed again - never one that was open before
        foreach (var p in Process.GetProcessesByName("devenv").Where(p => !ideBefore.Contains(p.Id)))
            try { if (p.StartTime >= started) { p.Kill(true); Console.WriteLine($"closed the IDE the run opened ({p.Id})"); } } catch { }

        if (!string.IsNullOrEmpty(o.Out)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!); File.WriteAllText(o.Out, log.ToString()); }
        var advice = Advisor.Load(dir).Where(i => i.Time >= started).ToList();
        Console.WriteLine(timedOut ? $"Stopped after {o.Minutes} min." : "Run finished.");
        Console.WriteLine(advice.Count == 0 ? "No advice recorded." : $"{advice.Count} advice recorded:\n" + Advisor.List(advice, false, 50));
        return timedOut && advice.Count == 0 ? Cli.Infra : Cli.Ok;
    }

    /// <summary>What the run looks at, collected without an AI: the commits and the diff since --since, the results of the pipeline.</summary>
    static string Context(Args o, string dir)
    {
        var sb = new StringBuilder();
        if (o.Trigger is "nightly" or "commit")
        {
            var gitLog = Git(dir, $"log --since=\"{o.Since}\" --format=\"%h %ad %an: %s\" --date=short --stat");
            var baseRev = Git(dir, $"rev-list -1 --before=\"{o.Since}\" HEAD").Trim();
            var diff = baseRev.Length > 0 ? Git(dir, $"diff {baseRev} HEAD -- . \":(exclude).tcagent/advisor\"") : "";
            sb.AppendLine($"## Commits since {o.Since}").AppendLine(gitLog.Length == 0 ? "(none)" : Cut(gitLog, 8000));
            if (diff.Length > 0) sb.AppendLine("## Diff").AppendLine(Cut(diff, 40000));
        }
        if (o.Trigger == "pipeline" && !string.IsNullOrEmpty(o.Results) && Directory.Exists(o.Results))
        {
            foreach (var f in Directory.GetFiles(o.Results, "*.md").Concat(Directory.GetFiles(o.Results, "*.xml")).Take(10))
                sb.AppendLine($"## {Path.GetFileName(f)}").AppendLine(Cut(File.ReadAllText(f), 12000));
        }
        return sb.ToString();
    }

    public static string Prompt(string trigger, string solution, string context) =>
        $"You are the TcAgent Advisor (Shadow, trigger: {trigger}). You read and advise; you change nothing - TcAgent refuses every change in this run.\n\n" +
        $"Solution: {solution}\n" +
        "1. Connect: tc_attach with the solution; if no IDE has it open, tc_open with visible=false. Then tc_coding_rules.\n" +
        "2. tc_advisor action=start kind=" + (trigger == "pipeline" ? "cause" : "review") + $" trigger={trigger}.\n" +
        (trigger == "pipeline"
            ? "3. Find the cause of the failure below: read the failing code, the errors (tc_check, tc_errors), the tests (tc_test_results). Prove it with tool outputs.\n"
            : "3. Review the changes below: risks a compiler cannot see, broken coding rules, missing tests, side effects on other callers (tc_find_references). " +
              "Load the skill plc-code-review. Prove each finding with tool outputs.\n") +
        "4. For each real finding: tc_advisor action=record with finding (one sentence), evidence (quotes with object and line), proposal, " +
        "confidence and order - a self-contained prompt a person sends to an agent to act on it, naming the workflow (bug-fix, new-feature, refactoring). " +
        "Few findings that matter beat many small ones; record nothing you cannot prove.\n" +
        "5. tc_advisor action=finish, then one line: how many findings.\n\n" +
        (string.IsNullOrWhiteSpace(context) ? "There is no context from git or the pipeline - look at the project as it is.\n" : "# Context\n" + context);

    static string Prompt(Args o, string context) => Prompt(o.Trigger, o.Solution, context);

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + $"\n… ({s.Length - max} more characters cut)";

    static string Git(string dir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{dir}\" {args}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return p.ExitCode == 0 ? output : "";
        }
        catch { return ""; }
    }

    /// <summary>The AI client chosen in the chat (chat-backends.json "default", next to settings.json).</summary>
    static (string key, string exe) ChatBackend()
    {
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "chat-backends.json");
            var o = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
            var key = o?["default"]?.ToString() ?? "claude";
            return (key, o?["backends"]?[key]?["exe"]?.ToString() ?? (key == "claude" ? "claude" : null));
        }
        catch { return ("claude", "claude"); }
    }

    static string Resolve(string name)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                try { var f = Path.Combine(d.Trim('"'), name + ext); if (File.Exists(f)) return f; } catch { }
            }
        return null;
    }
}
