// TcAgent command line: the pipeline configuration of a repository (part of Cli, see Cli.cs).
//
//   TcAgent.exe ci [--config <file>] [--only <run|name>[,...]] [--from <number|run|name>] [--dry-run] [--init]
//
// .tcagent\pipeline.json (searched upwards from the current directory, or --config) lists the steps of the pipeline:
// doctor, check, build, analyze, doc, test/sil/hil, library, archive or any other TcAgent.exe command. ci runs them in
// order, each as a child process of this TcAgent.exe - every step uses the existing command unchanged and gets a fresh
// IDE. The output of the steps is streamed as it comes, grouped on GitHub Actions / Azure DevOps; ci stops at the first
// failing step (unless continueOnError) and writes a summary: console, <results>\ci-summary.md, GITHUB_STEP_SUMMARY.
// ci itself never opens the IDE. Format: docs/PIPELINE.md, example: docs/pipeline/pipeline.example.json.
//
// Step -> command line: every key except run/name/continueOnError/args/enabled/openSolution becomes --kebab-case-key value
// (true = flag only, false/null = omitted, arrays comma-joined), "args" is appended verbatim. --solution is added for
// every command that opens a solution (doctor only with "openSolution": true), --junit <results>\<nn>-<run>.xml for
// check/build/analyze/test/sil/hil, --out <results>\<solution>.html for doc; relative junit/out/sarif/markdown paths
// are placed in the results folder.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TcAgent;

public static partial class Cli
{
    const string CiFolder = ".tcagent", CiFile = "pipeline.json";

    /// <summary>Keys of a step that are not passed to the command.</summary>
    static readonly string[] CiReservedKeys = { "run", "name", "continueOnError", "args", "enabled", "openSolution" };

    /// <summary>Commands that write JUnit: default --junit &lt;results&gt;\&lt;nn&gt;-&lt;run&gt;.xml.</summary>
    static readonly string[] CiJunitCommands = { "check", "build", "analyze", "test", "sil", "hil" };

    /// <summary>Commands that do not open a solution - no automatic --solution (doctor: only with "openSolution": true).</summary>
    static readonly string[] CiNoSolutionCommands = { "doctor", "feedback", "install", "help", "version" };

    /// <summary>Output file options: a relative path is placed in the results folder.</summary>
    static readonly string[] CiOutputKeys = { "junit", "out", "sarif", "markdown" };

    static readonly JsonDocumentOptions CiJsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    sealed class CiStep
    {
        public int Index;                                   // 1-based position in the file
        public string Command, Name;
        public bool ContinueOnError, Enabled = true;
        public readonly List<string> Args = new();          // arguments after the command
        public string Status = "pending";
        public int? ExitCode;
        public double Seconds;
        public string Title => Name ?? Command;
    }

    sealed class CiConfig
    {
        public string File, Root, Solution, Results, AllowTargets;
        public readonly List<CiStep> Steps = new();
        public readonly List<string> Warnings = new();
    }

    static int Ci(Options o)
    {
        if (o.Init) return CiInit(o);

        CiConfig cfg;
        List<CiStep> run;
        try
        {
            cfg = CiLoad(o);
            run = CiSelect(cfg, o);
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Usage; }

        var self = CiSelf();
        bool gh = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        bool azure = !gh && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

        Console.WriteLine($"TcAgent {Program.ServerVersion} ci - {cfg.File}");
        Console.WriteLine($"  repository root: {cfg.Root}");
        Console.WriteLine($"  solution: {cfg.Solution ?? "(none)"}, results: {cfg.Results}" +
                          (cfg.AllowTargets != null ? $", TCAGENT_ALLOW_TARGETS={cfg.AllowTargets}" : ""));
        foreach (var w in cfg.Warnings) Console.WriteLine("Warning: " + w);
        Console.WriteLine();

        if (o.DryRun)
        {
            int n = 0;
            foreach (var s in cfg.Steps)
                Console.WriteLine(s.Status == "pending"
                    ? $"[{++n}/{run.Count}] {s.Title}{(s.ContinueOnError ? " (continueOnError)" : "")}\n    {CiCommandLine(self, s)}"
                    : $"[-] {s.Title}: {s.Status}");
            Console.WriteLine();
            Console.WriteLine($"Dry run: {run.Count} step(s) would run in {cfg.Root}" +
                              (cfg.AllowTargets != null ? $" with TCAGENT_ALLOW_TARGETS={cfg.AllowTargets}" : "") + ".");
            return Ok;
        }

        try { Directory.CreateDirectory(Path.Combine(cfg.Root, cfg.Results)); }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR (ci): results folder {cfg.Results} cannot be created: {Msg(ex)}"); return Infra; }

        var total = Stopwatch.StartNew();
        int worst = Ok;
        CiStep firstFail = null;
        bool stop = false;
        for (int k = 0; k < run.Count; k++)
        {
            var s = run[k];
            if (stop) { s.Status = "not run"; continue; }
            var header = $"[{k + 1}/{run.Count}] {s.Title}";
            if (gh) Console.WriteLine("::group::" + header);
            else if (azure) Console.WriteLine("##[group]" + header);
            Console.WriteLine($"== {header} ==");
            Console.WriteLine("> " + CiCommandLine(self, s));
            Console.Out.Flush();

            var sw = Stopwatch.StartNew();
            int rc = CiExecute(self, s, cfg);
            s.Seconds = sw.Elapsed.TotalSeconds;
            s.ExitCode = rc;
            Console.Out.Flush();
            if (gh) Console.WriteLine("::endgroup::");
            else if (azure) Console.WriteLine("##[endgroup]");

            if (rc == Ok) s.Status = "OK";
            else
            {
                s.Status = s.ContinueOnError ? "FAILED (continued)" : "FAILED";
                firstFail ??= s;
                if (!s.ContinueOnError) stop = true;
                if (CiRank(rc) > CiRank(worst)) worst = CiNormalize(rc);
                var msg = $"step {header} failed with exit code {rc} ({CiExitMeaning(rc)})";
                if (gh) Console.WriteLine($"::error title=TcAgent ci::{msg}");
                else if (azure) Console.WriteLine($"##vso[task.logissue type=error]TcAgent ci: {msg}");
            }
            Console.WriteLine(System.FormattableString.Invariant($"-- {header}: {s.Status} (exit {rc}, {s.Seconds:0.0} s)"));
            Console.WriteLine();
        }

        // ---- summary
        Console.WriteLine(CiConsoleTable(cfg));
        var md = CiMarkdown(cfg, worst, total.Elapsed.TotalSeconds);
        var mdFile = Path.Combine(cfg.Root, cfg.Results, "ci-summary.md");
        try { File.WriteAllText(mdFile, md, new UTF8Encoding(false)); Console.WriteLine("Summary: " + mdFile); }
        catch (Exception ex) { Console.WriteLine($"Warning: {mdFile} not written: {Msg(ex)}"); }
        var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(stepSummary))
            try { File.AppendAllText(stepSummary, md + "\n", new UTF8Encoding(false)); }
            catch (Exception ex) { Console.WriteLine($"Warning: GITHUB_STEP_SUMMARY not written: {Msg(ex)}"); }
        Console.WriteLine(System.FormattableString.Invariant(
            $"CI {(worst == Ok ? "OK" : "FAILED")}: exit {worst}{(firstFail != null ? $" (first failing step: {firstFail.Title})" : "")}, {total.Elapsed.TotalSeconds:0.0} s"));
        return worst;
    }

    // ------------------------------------------------------------------ exit codes
    /// <summary>Exit codes other than the TcAgent ones (crash, killed) count as infrastructure problem.</summary>
    static int CiNormalize(int rc) => rc is Ok or Failed or Usage or Infra ? rc : Infra;

    /// <summary>Which exit code wins when several steps failed (continueOnError): 2 (the pipeline definition must be fixed)
    /// before 1 (red - a retry does not help) before 3 (infrastructure - a retry may help).</summary>
    static int CiRank(int rc) => CiNormalize(rc) switch { Ok => 0, Infra => 1, Failed => 2, _ => 3 };

    static string CiExitMeaning(int rc) => CiNormalize(rc) switch
    {
        Ok => "OK",
        Failed => "compiler errors, failed tests or a failed check",
        Usage => "wrong call or refused",
        _ => "infrastructure problem"
    };

    // ------------------------------------------------------------------ running a step
    /// <summary>This TcAgent: the exe, or dotnet + TcAgent.dll when started that way.</summary>
    static (string File, string Prefix) CiSelf()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "TcAgent.exe";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, typeof(Cli).Assembly.Location);
        return (exe, null);
    }

    static int CiExecute((string File, string Prefix) self, CiStep s, CiConfig cfg)
    {
        var psi = new ProcessStartInfo(self.File) { UseShellExecute = false, WorkingDirectory = cfg.Root };
        if (self.Prefix != null) psi.ArgumentList.Add(self.Prefix);
        psi.ArgumentList.Add(s.Command);
        foreach (var a in s.Args) psi.ArgumentList.Add(a);
        if (cfg.AllowTargets != null) psi.Environment["TCAGENT_ALLOW_TARGETS"] = cfg.AllowTargets;
        try
        {
            // stdout/stderr are inherited: the step writes straight into the CI log, live and unchanged
            using var p = Process.Start(psi);
            if (p == null) { Console.Error.WriteLine($"ERROR (ci): {self.File} did not start"); return Infra; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR (ci): could not start {self.File}: {Msg(ex)}");
            return Infra;
        }
    }

    static string CiCommandLine((string File, string Prefix) self, CiStep s)
    {
        var parts = new List<string> { self.File };
        if (self.Prefix != null) parts.Add(self.Prefix);
        parts.Add(s.Command);
        parts.AddRange(s.Args);
        return string.Join(" ", parts.Select(CiQuote));
    }

    static string CiQuote(string a) =>
        a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------------ selection
    static bool CiMatches(CiStep s, string key) =>
        s.Command.Equals(key, StringComparison.OrdinalIgnoreCase) || (s.Name != null && s.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Steps to run: from --from on, only those named in --only, without "enabled": false (unless named in --only).
    /// Sets the status of the others.</summary>
    static List<CiStep> CiSelect(CiConfig cfg, Options o)
    {
        var only = (o.Only ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var key in only)
            if (!cfg.Steps.Any(s => CiMatches(s, key))) throw new Exception($"--only: no step '{key}' in {cfg.File}");
        int from = 1;
        if (o.From != null)
        {
            if (int.TryParse(o.From, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                if (n < 1 || n > cfg.Steps.Count) throw new Exception($"--from {n}: the file has steps 1..{cfg.Steps.Count}");
                from = n;
            }
            else from = cfg.Steps.FirstOrDefault(s => CiMatches(s, o.From))?.Index ?? throw new Exception($"--from: no step '{o.From}' in {cfg.File}");
        }
        var list = new List<CiStep>();
        foreach (var s in cfg.Steps)
        {
            bool named = only.Any(key => CiMatches(s, key));
            if (s.Index < from) s.Status = "skipped (--from)";
            else if (only.Length > 0 && !named) s.Status = "skipped (--only)";
            else if (!s.Enabled && !named) s.Status = "disabled";
            else { s.Status = "pending"; list.Add(s); }
        }
        if (list.Count == 0) throw new Exception("no step left to run (all disabled or not selected by --only/--from)");
        return list;
    }

    // ------------------------------------------------------------------ the file
    /// <summary>.tcagent\pipeline.json in the directory or one of its parents, or null.</summary>
    static string CiFind(string dir)
    {
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, CiFolder, CiFile);
            if (File.Exists(f)) return f;
        }
        // not above: one pipeline file up to two levels below (e.g. TwinCAT\.tcagent\pipeline.json while the workflow runs in the root)
        var below = new List<string>();
        void Walk(string d, int level)
        {
            if (level > 2 || below.Count > 1) return;
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { return; }
            foreach (var sub in subs)
            {
                var n = Path.GetFileName(sub);
                if (n.Equals(CiFolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (level > 0 && File.Exists(Path.Combine(sub, CiFile))) below.Add(Path.Combine(sub, CiFile));
                    continue;
                }
                if (n.StartsWith(".") || n is "bin" or "obj" or "_Boot" or "node_modules" or "results" or "Packages") continue;
                Walk(sub, level + 1);
            }
        }
        Walk(dir, 0);
        if (below.Count == 1) return below[0];
        if (below.Count > 1) throw new Exception($"several pipeline files below {dir} - pass --config <file>");
        return null;
    }

    /// <summary>The Git repository root (folder with .git) of the directory or one of its parents, or null.</summary>
    static string CiGitRoot(string dir)
    {
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    /// <summary>Repository root of a pipeline file: the folder containing .tcagent, otherwise the file's folder.</summary>
    static string CiRootOf(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? ".";
        return Path.GetFileName(dir).Equals(CiFolder, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir) ?? dir : dir;
    }

    static CiConfig CiLoad(Options o)
    {
        var file = o.Config != null ? Path.GetFullPath(o.Config) : CiFind(Directory.GetCurrentDirectory());
        if (file == null)
            throw new Exception($"no {CiFolder}\\{CiFile} in {Directory.GetCurrentDirectory()} or above - create one with TcAgent.exe ci --init, or pass --config <file>");
        if (!File.Exists(file)) throw new Exception("pipeline file not found: " + file);
        var cfg = new CiConfig { File = file, Root = CiRootOf(file) };

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file), CiJsonOptions); }
        catch (JsonException ex) { throw new Exception($"{file}: invalid JSON - {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new Exception($"{file}: a JSON object {{ \"solution\": ..., \"steps\": [...] }} expected");
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("solution" or "results" or "allowTargets" or "steps" or "description") && !p.Name.StartsWith("$") && !p.Name.StartsWith("_"))
                    cfg.Warnings.Add($"unknown key \"{p.Name}\" ignored");

            cfg.Solution = CiString(root, "solution", "\"solution\"");
            if (o.Solution != null) cfg.Solution = Path.GetFullPath(o.Solution);          // --solution overrides the file
            if (cfg.Solution != null && !File.Exists(Path.Combine(cfg.Root, cfg.Solution)))
                throw new Exception($"solution not found: {Path.Combine(cfg.Root, cfg.Solution)} (\"solution\" is relative to {cfg.Root})");
            cfg.Results = CiString(root, "results", "\"results\"") ?? "results";
            cfg.AllowTargets = CiString(root, "allowTargets", "\"allowTargets\"");

            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
                throw new Exception($"{file}: \"steps\" (a list like [ {{ \"run\": \"check\" }} ]) is missing or empty");
            int i = 0;
            foreach (var e in steps.EnumerateArray()) cfg.Steps.Add(CiParseStep(e, ++i, cfg));
        }
        return cfg;
    }

    static CiStep CiParseStep(JsonElement e, int index, CiConfig cfg)
    {
        var where = $"step {index}";
        if (e.ValueKind == JsonValueKind.String)            // shorthand: "check" = { "run": "check" }
            using (var shorthand = JsonDocument.Parse("{\"run\":" + JsonSerializer.Serialize(e.GetString()) + "}")) e = shorthand.RootElement.Clone();
        if (e.ValueKind != JsonValueKind.Object) throw new Exception($"{where}: an object like {{ \"run\": \"check\" }} expected");
        if (!e.TryGetProperty("run", out var runValue) || runValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(runValue.GetString()))
            throw new Exception($"{where}: \"run\" (the TcAgent.exe command, e.g. \"check\") is missing");
        var s = new CiStep { Index = index, Command = runValue.GetString().Trim().ToLowerInvariant() };
        where = $"step {index} ({s.Command})";
        if (s.Command == "ci" || s.Command.StartsWith("-") || s.Command.StartsWith("/") || !IsCommand(new[] { s.Command }))
            throw new Exception($"{where}: '{s.Command}' is not a TcAgent.exe command that a pipeline step can run (TcAgent.exe help lists them)");
        s.Name = CiString(e, "name", where + " \"name\"");
        s.ContinueOnError = CiBool(e, "continueOnError", where);
        s.Enabled = !e.TryGetProperty("enabled", out _) || CiBool(e, "enabled", where);
        bool openSolution = CiBool(e, "openSolution", where);
        bool Has(string key) => e.TryGetProperty(key, out _);

        // --solution for every command that opens one; a step may give its own "solution" (false = none) or "attach"
        bool wantsSolution = s.Command == "doctor" ? openSolution : !CiNoSolutionCommands.Contains(s.Command);
        if (wantsSolution && !Has("solution") && !Has("attach"))
        {
            if (cfg.Solution == null) throw new Exception($"{where} needs a solution: set \"solution\" in {CiFile} (or in the step)");
            s.Args.Add("--solution");
            s.Args.Add(cfg.Solution);
        }

        // every other key: --kebab-case-key value
        foreach (var p in e.EnumerateObject())
        {
            if (CiReservedKeys.Contains(p.Name)) continue;
            var option = "--" + CiKebab(p.Name);
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.True: s.Args.Add(option); break;
                case JsonValueKind.False:
                case JsonValueKind.Null: break;
                default:
                    var text = CiText(p.Value, $"{where} \"{p.Name}\"");
                    if (CiOutputKeys.Contains(CiKebab(p.Name)) && !Path.IsPathRooted(text) && !CiUnderResults(text, cfg.Results)) text = Path.Combine(cfg.Results, text);
                    s.Args.Add(option);
                    s.Args.Add(text);
                    break;
            }
        }

        // defaults: JUnit per step, documentation into the results folder
        if (CiJunitCommands.Contains(s.Command) && !Has("junit"))
        {
            s.Args.Add("--junit");
            s.Args.Add(Path.Combine(cfg.Results, $"{index:00}-{s.Command}.xml"));
        }
        if (s.Command == "doc" && !Has("out"))
        {
            s.Args.Add("--out");
            s.Args.Add(Path.Combine(cfg.Results, (cfg.Solution != null ? Path.GetFileNameWithoutExtension(cfg.Solution) : "doc") + ".html"));
        }

        // "args": appended verbatim
        if (e.TryGetProperty("args", out var extra))
        {
            if (extra.ValueKind != JsonValueKind.Array) throw new Exception($"{where}: \"args\" must be a list of strings, e.g. [\"--rebuild\"]");
            foreach (var a in extra.EnumerateArray())
                s.Args.Add(a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText());
        }
        return s;
    }

    /// <summary>camelCase -> kebab-case ("warningsAsErrors" -> "warnings-as-errors"); kebab-case stays.</summary>

    // "results/x.html" with results folder "results" stays as it is (no results/results/x.html)
    static bool CiUnderResults(string path, string results)
    {
        var a = path.Replace('/', '\\').TrimStart('.', '\\');
        var b = results.Replace('/', '\\').Trim('\\').TrimStart('.', '\\');
        return b.Length > 0 && a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string CiKebab(string key)
    {
        var sb = new StringBuilder();
        foreach (var c in key)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Option value: string, number, bool, or a list of these (comma-joined).</summary>
    static string CiText(JsonElement v, string what)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Number: return v.GetRawText();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var x in v.EnumerateArray())
                {
                    if (x.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null)
                        throw new Exception($"{what}: a list may only contain strings, numbers or booleans");
                    items.Add(CiText(x, what));
                }
                return string.Join(",", items);
            default:
                throw new Exception($"{what}: unsupported value {v.GetRawText()} (string, number, boolean or list expected)");
        }
    }

    static string CiString(JsonElement obj, string key, string what)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.String or JsonValueKind.Array) return CiText(v, what);
        throw new Exception($"{what}: a string expected, not {v.GetRawText()}");
    }

    static bool CiBool(JsonElement obj, string key, string where)
    {
        if (!obj.TryGetProperty(key, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new Exception($"{where}: \"{key}\" must be true or false")
        };
    }

    // ------------------------------------------------------------------ summary
    static string CiConsoleTable(CiConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine(" #  Step                           Exit  Duration  Result");
        foreach (var s in cfg.Steps)
        {
            var title = s.Title.Length > 30 ? s.Title.Substring(0, 29) + "~" : s.Title;
            var exit = s.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var dur = s.ExitCode != null ? s.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "-";
            sb.AppendLine($"{s.Index,2}  {title,-30} {exit,5} {dur,9}  {s.Status}");
        }
        return sb.ToString();
    }

    static string CiMarkdown(CiConfig cfg, int worst, double seconds)
    {
        static string Cell(string t) => (t ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.AppendLine(System.FormattableString.Invariant(
            $"## TcAgent ci: {(worst == Ok ? "OK" : $"FAILED (exit {worst} - {CiExitMeaning(worst)})")}, {seconds:0.0} s"));
        sb.AppendLine();
        sb.AppendLine($"Pipeline `{Cell(Path.GetRelativePath(cfg.Root, cfg.File))}`" + (cfg.Solution != null ? $", solution `{Cell(cfg.Solution)}`" : "") +
                      $", TcAgent {Program.ServerVersion}");
        sb.AppendLine();
        sb.AppendLine("| # | Step | Command | Exit code | Duration | Result |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in cfg.Steps)
        {
            var cmd = s.Command + (s.Args.Count > 0 ? " " + string.Join(" ", s.Args.Select(CiQuote)) : "");
            var exit = s.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "–";
            var dur = s.ExitCode != null ? s.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "–";
            var result = s.Status.StartsWith("FAILED") ? $"**{s.Status}**" : s.Status;
            sb.AppendLine($"| {s.Index} | {Cell(s.Title)} | `{Cell(cmd)}` | {exit} | {dur} | {Cell(result)} |");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ --init
    const string CiTemplate = """
        // TcAgent pipeline: TcAgent.exe ci runs these steps in order, each with a fresh IDE (format: docs/PIPELINE.md of TcAgent).
        // Comments and trailing commas are allowed. Try it first with: TcAgent.exe ci --dry-run
        {
          "solution": %SOLUTION%,     // relative to the folder that contains .tcagent
          "results": "results",       // JUnit, documentation, artifacts and ci-summary.md
          "allowTargets": "local",    // TCAGENT_ALLOW_TARGETS for the steps: only this PC may be activated
          "steps": [
            { "run": "doctor" },                      // build machine ready? (session, IDE, interop, runtime)
            { "run": "check" },                       // 'check all objects': compiler errors, also in unused POUs
            { "run": "build" },
            { "run": "doc", "risks": true },          // documentation (HTML) + risk list; "failOn": "high" makes it a gate
            // TcUnit on the runtime of this PC - needs the runtime, its licenses and TcUnit.RUN() in the PLC: set "enabled" to true
            { "run": "test", "timeout": 900, "enabled": false }
          ]
        }

        """;

    static int CiInit(Options o)
    {
        var cwd = Directory.GetCurrentDirectory();
        // the workflow runs "ci" in the repository root - the file belongs there, also when --init is run in a subfolder
        var git = CiGitRoot(cwd);
        if (o.Config == null && git != null && !git.Equals(cwd, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Git repository root: {git} - the pipeline file goes there (the workflow runs TcAgent.exe ci in the root).");
            cwd = git;
        }
        var file = Path.GetFullPath(o.Config ?? Path.Combine(cwd, CiFolder, CiFile));
        if (File.Exists(file))
        {
            Console.Error.WriteLine($"{file} already exists - not overwritten (edit it, or delete it first).");
            return Usage;
        }
        var root = CiRootOf(file);
        var found = CiFindSolutions(cwd, 3);
        string sln;
        if (found.Count == 0)
        {
            sln = "MyMachine.sln";
            Console.WriteLine($"No *.sln found in {cwd} (3 levels deep) - set \"solution\" in the file.");
        }
        else
        {
            sln = Path.GetRelativePath(root, found[0]).Replace('\\', '/');
            if (found.Count > 1)
                Console.WriteLine($"{found.Count} solutions found, using {sln} - others: " +
                                  string.Join(", ", found.Skip(1).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))));
        }
        var text = CiTemplate.Replace("%SOLUTION%", JsonSerializer.Serialize(sln,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text.Replace("\n", Environment.NewLine).Replace("\r\r", "\r"), new UTF8Encoding(false));
        }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR (ci): {file} not written: {Msg(ex)}"); return Infra; }
        Console.WriteLine("Written: " + file);
        Console.WriteLine("Next: check the steps, run TcAgent.exe ci --dry-run, then TcAgent.exe ci - and commit the file.");
        return Ok;
    }

    /// <summary>*.sln files in the directory and up to depth levels below (shallowest first; .git, bin, obj, ... skipped).</summary>
    static List<string> CiFindSolutions(string dir, int depth)
    {
        var found = new List<(string File, int Level)>();
        void Walk(string d, int level)
        {
            try
            {
                foreach (var sln in Directory.GetFiles(d, "*.sln").Where(x => x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
                    found.Add((sln, level));
            }
            catch { return; }
            if (level >= depth) return;
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { return; }
            foreach (var sub in subs)
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith(".") || n is "bin" or "obj" or "_Boot" or "node_modules" or "results") continue;
                Walk(sub, level + 1);
            }
        }
        Walk(dir, 0);
        return found.OrderBy(x => x.Level).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase).Select(x => x.File).ToList();
    }
}
