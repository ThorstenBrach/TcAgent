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
        public string Sub = "run", Solution, Trigger = "nightly", Since = "24 hours ago", Results, Id, Rating, Comment, Out, Target = "local";
        public int Minutes = 15, MaxTurns = 80, Interval = 2, MaxRuns;
        public List<int> Ports = new() { 851 };
        public bool Open, Json, DryRun, Once, StartRuns;
    }

    public const string Usage =
        "  TcAgent.exe advise [run] --solution <sln> [--trigger nightly|commit|pipeline] [--since \"24 hours ago\"] [--results <dir>]\n" +
        "                     [--minutes 15] [--out <log file>] [--dry-run]\n" +
        "      Advisor Shadow: an AI client without a window reads the project and records advice with an order (.tcagent\\advisor).\n" +
        "      --trigger project reviews the whole project (workflow project-review), e.g. once a month.\n" +
        "      It changes nothing (TCAGENT_MODE=ask). --dry-run prints the prompt only.\n" +
        "  TcAgent.exe advise watch --solution <sln> [--target <AmsNetId>] [--ports 851,852] [--interval 2] [--run] [--max-runs n] [--once]\n" +
        "      Advisor Live: watches the plant (runtime and PLC state, cycle time, the fault signals of .tcagent\\advisor-watch.json\n" +
        "      and of the plugins) and only reads. --run starts an Advisor run when something goes wrong, --once looks once.\n" +
        "  TcAgent.exe advise list|stats --solution <sln> [--open] [--json]\n" +
        "  TcAgent.exe advise rate --solution <sln> --id AD-... --rating right|partly|wrong|n/a [--comment <text>]\n" +
        "  TcAgent.exe advise acted|done|delete --solution <sln> --id AD-... [--comment <text>]";

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
                case "--target": o.Target = V(); break;
                case "--ports": o.Ports = V().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(); break;
                case "--interval": o.Interval = Math.Max(1, int.Parse(V())); break;
                case "--once": o.Once = true; break;
                case "--run": o.StartRuns = true; break;
                case "--max-runs": o.MaxRuns = int.Parse(V()); break;
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
        if (o.Trigger is not ("nightly" or "commit" or "pipeline" or "project" or "machine")) throw new ArgumentException("--trigger nightly, commit, pipeline or project");
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
                    foreach (var id in Ids(o.Id))       // several at once: --id a,b,c
                    {
                        Advisor.Rate(dir, id, o.Rating, Environment.UserName, o.Comment);
                        Console.WriteLine($"{id}: {o.Rating}");
                    }
                    return Cli.Ok;
                case "acted" or "done" or "delete":
                    foreach (var id in Ids(o.Id))
                    {
                        Advisor.Mark(dir, id, o.Sub, Environment.UserName, o.Comment);
                        Console.WriteLine($"{id}: {o.Sub}");
                    }
                    return Cli.Ok;
                case "run": return RunShadow(o, dir);
                case "watch": return Watch(o, dir);
                default: throw new ArgumentException("advise run, watch, list, stats, rate, acted, done or delete");
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

        return StartRun(o, dir, prompt, "Advisor Shadow");
    }

    /// <summary>Starts the AI client of the chat without a window, read only, with this prompt; the advice it records is printed.</summary>
    static int StartRun(Args o, string dir, string prompt, string what)
    {
        var (key, exe) = ChatBackend();
        if (exe == null || exe.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Error.WriteLine($"{what} without a window runs with Claude Code for now; the chat uses '{key}'. " +
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

        Console.WriteLine($"{what} ({o.Trigger}) with Claude Code, at most {o.Minutes} min, read only.");
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



    // ------------------------------------------------------------------ the watch (Advisor Live)
    static int Watch(Args o, string dir)
    {
        var plugins = new List<(string, string)>();
        try { plugins = Plugins.Usable(dir).Where(p => p.Ready).Select(p => (p.Id, p.Folder)).ToList(); } catch { }
        IEnumerable<string> platforms = Enumerable.Empty<string>();
        try { platforms = KnowledgeLayers.Platforms(dir).Active; } catch { }
        var (signals, hints) = AdvisorWatch.Load(dir, plugins, platforms);
        var ports = signals.Select(s => s.Port).Concat(o.Ports).Distinct().ToList();
        var state = new AdvisorWatch.State();
        var reported = new Dictionary<string, DateTime>();
        var active = new HashSet<string>();
        int runs = 0;
        Console.WriteLine($"Advisor Live watches {o.Target}: runtime, PLC port(s) {string.Join(", ", ports)}, {signals.Count} fault signal(s)" +
                          (hints.Count > 0 ? $", skills {string.Join(", ", hints.Select(h => h.Skill))}" : "") + ". It only reads. Ctrl+C ends it.");
        using var r = new AdvisorReader(o.Target);
        while (true)
        {
            var now = DateTime.Now;
            var found = AdvisorLook.Look(r, ports, signals, state, now);
            if (o.Once)
            {
                Console.WriteLine($"runtime {r.State(10000)}; " + string.Join("; ", ports.Select(p => $"PLC {p} {r.State(p)}")));
                Console.WriteLine(found.Count == 0 ? "Nothing is wrong now." : string.Join("\n", found.Select(f => f.Text + string.Concat(f.Details.Select(d => "\n  " + d)))));
                return found.Count == 0 ? Cli.Ok : Cli.Failed;
            }
            // only what newly went wrong; the same event at most once an hour
            var fresh = found.Where(f => !active.Contains(f.Key) && (!reported.TryGetValue(f.Key, out var t) || (now - t).TotalHours >= 1)).ToList();
            active = found.Select(f => f.Key).ToHashSet();
            foreach (var f in fresh)
            {
                reported[f.Key] = now;
                Console.WriteLine($"{now:HH:mm:ss}  {f.Text}" + string.Concat(f.Details.Select(d => "\n          " + d)));
            }
            if (fresh.Count > 0 && o.StartRuns)
            {
                var context = "## What went wrong\n" + string.Join("\n", fresh.Select(f => "- " + f.Text + string.Concat(f.Details.Select(d => "\n  - " + d)))) +
                              $"\n\nRuntime: {r.State(10000)}; " + string.Join("; ", ports.Select(p => $"PLC port {p}: {r.State(p)}")) +
                              "\n\nCode: " + string.Join("\n", ports.Select(p => AdvisorCode.Check(r, p, dir))) +
                              (hints.Count > 0 ? "\n\nPlatform: load the skill " + string.Join(" and ", hints.Select(h => h.Skill)) + " - it knows how the platform reports faults.\n" : "\n");
                o.Trigger = "machine";
                StartRun(o, dir, LivePrompt(o.Solution, o.Target, context), "Advisor Live");
                if (o.MaxRuns > 0 && ++runs >= o.MaxRuns) { Console.WriteLine($"{runs} run(s) - the watch ends (--max-runs)."); return Cli.Ok; }
            }
            Thread.Sleep(o.Interval * 1000);
        }
    }

    public static string LivePrompt(string solution, string target, string context) =>
        $"You are the TcAgent Advisor (Live, trigger: machine). Something went wrong at the running plant ({target}). You read and advise; you change " +
        "nothing - TcAgent refuses every change, and you never write a variable or start, stop or acknowledge anything: people can be at the machine.\n\n" +
        $"Solution: {solution}\n" +
        "1. Connect: tc_attach with the solution; if no IDE has it open, tc_open with visible=false. Then tc_coding_rules.\n" +
        "2. tc_advisor action=start kind=diagnose trigger=machine.\n" +
        "3. Look at the Code line below first: if the project may not match the code on the PLC, conclude only from values and say so in the finding. " +
        "Find where it hangs and why: tc_plc_state, ads_read of the signals and their details below, the runtime messages (tc_errors kind=runtime), " +
        "the code that sets the signal (tc_find_references, tc_plc_read), the step chains (tc_sfc). Load the skill plc-debugging, and the platform skill if one " +
        "is named below. Go from the top level down only as far as you need. Prove it with values and code.\n" +
        "4. tc_advisor action=record with kind=diagnose: finding, evidence (variable = value, object and line), proposal, confidence and order - a " +
        "self-contained prompt a person sends to an agent, naming the workflow (bug-fix). Say what a person at the machine should check first.\n" +
        "5. tc_advisor action=finish.\n\n# Context\n" + context;

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

    public static string Prompt(string trigger, string solution, string context) => trigger == "project" ? ProjectPrompt(solution) : ShadowPrompt(trigger, solution, context);

    /// <summary>The whole project, e.g. once a month: the workflow project-review, without a person to ask.</summary>
    static string ProjectPrompt(string solution) =>
        $"You are the TcAgent Advisor (Shadow, trigger: project). You read and advise; you change nothing - TcAgent refuses every change in this run.\n\n" +
        $"Solution: {solution}\n" +
        "1. Connect: tc_attach with the solution; if no IDE has it open, tc_open with visible=false. Then tc_coding_rules.\n" +
        "2. tc_advisor action=start kind=review trigger=project.\n" +
        "3. tc_workflow action=start name=project-review and follow its steps. Nobody can be asked in this run: the goal is a regular review of the " +
        "whole project, the scope is the code of the project without libraries; choose the hotspots yourself and say why.\n" +
        "4. Record each finding that should be acted on with tc_advisor action=record (evidence with object and line, an order naming the workflow), " +
        "and one more record with kind=report: the traffic light per area as the finding, the counts as evidence, and as the order a prompt to discuss the report.\n" +
        "5. tc_workflow action=finish, tc_advisor action=finish.\n";

    static string ShadowPrompt(string trigger, string solution, string context) =>
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

    public static string[] Ids(string id)
    {
        var ids = (id ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        return ids.Length > 0 ? ids : throw new ArgumentException("--id is required (several: --id a,b)");
    }

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
