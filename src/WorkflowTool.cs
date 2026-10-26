// tc_workflow (Workflows.cs): list, show, start, step, status and finish an engineering workflow - plus the hard gate
// "tc_activate only after a build with 0 errors", which holds with and without a workflow.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    Workflows.Run run;   // the active workflow run of this session (one at a time)

    void RegisterWorkflows()
    {
        Add("tc_workflow", "Engineering workflows = how this team ALWAYS does a kind of task, with checkpoints TcAgent enforces " +
                           "(e.g. new-feature: requirements -> change plan -> code -> check -> review), from the levels built-in / company / " +
                           "user / platform / project. action=list (also shown by tc_coding_rules), show name, start name task " +
                           "(when the task fits a workflow - then follow its steps), step (the active step is done; skip=true if it does not apply; " +
                           "a checkpoint step needs its check first, e.g. tc_check with 0 errors), status, finish (abort=true to stop early).",
            Schema(Req(P("action", "string", "list, show, start, step, status or finish")),
                   P("name", "string", "show/start: workflow name"),
                   P("task", "string", "start: the task in a few words (no customer or machine names)"),
                   P("step", "string", "step: id of the step that is done (optional - the active one)"),
                   P("skip", "boolean", "step: the step does not apply to this task"),
                   P("note", "string", "step: short note (optional)"),
                   P("abort", "boolean", "finish: stop the run before all steps are done")),
            a =>
            {
                string action = (Str(a, "action") ?? "status").ToLowerInvariant();
                if (action is "step" or "status" or "finish")
                {
                    if (run == null) throw new Exception("No workflow is running - tc_workflow action=start name=<name>");
                    try
                    {
                        if (action == "status") return Workflows.Status(run);
                        if (action == "step")
                        {
                            // deterministic first: a checkpoint whose check is missing or failed - TcAgent runs the check itself instead of refusing
                            var pre = "";
                            if (!Bool(a, "skip", false) && run.Active is { Requires: "check-ok" or "build-ok" } act && Workflows.Missing(act) != null
                                && (string.IsNullOrEmpty(Str(a, "step")) || Str(a, "step")!.Equals(act.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                string Run(string tool) => (Call(tool, new JsonObject())["content"] as JsonArray)?[0]?["text"]?.ToString() ?? "";
                                // check first, build second: tc_check pre-compiles without generating code - much faster than a build,
                                // so the errors are fixed with checks and the build runs once, when the check is clean
                                var check = Run("tc_check");
                                var tool = "tc_check"; var txt = check;
                                if (act.Requires == "build-ok" && !Lessons.HasCompileErrors(check)) { tool = "tc_build"; txt = Run("tc_build"); }
                                pre = (tool == "tc_build" ? $"tc_check (run by TcAgent): {check.Split('\n')[0]}\n" : "") + $"{tool} (run by TcAgent for the checkpoint): {txt.Split('\n')[0]}\n\n";
                                if (Workflows.Missing(act) != null && ++act.GateStops > 0)
                                    throw new InvalidOperationException($"step '{act.Id}' needs {act.Requires} - {tool}: {txt.Split('\n')[0]}" +
                                                                        (tool == "tc_check" && act.Requires == "build-ok" ? " (no build started - fix these first; tc_build directly only if the errors are in code that is never called)" : "") +
                                                                        $"\n{string.Join("\n", txt.Split('\n').Skip(1).Take(25))}");
                            }
                            return pre + Workflows.Advance(run, Str(a, "step"), Bool(a, "skip", false), Str(a, "note"));
                        }
                    }
                    catch (InvalidOperationException ex) { throw new Exception("NOT done: " + ex.Message); }
                    bool abort = Bool(a, "abort", false);
                    var summary = Workflows.Finish(run, abort);
                    // the run log: local, counts and names only (RunLogs.cs) - the base for tc_improve
                    try
                    {
                        var sln = SolutionDir();
                        RunLogs.Save(RunLogs.ProjectOf(sln), sln, RunLogs.ToRecord(run, abort));
                        summary += "Run log saved (local). tc_improve action=runs shows the trend, action=analyze the proposals.\n";
                    }
                    catch (Exception ex) { Log.Error("Run log: " + ex.Message); }
                    run = null;
                    return summary;
                }
                var dir = SolutionDir();
                var entries = Workflows.Entries(dir, KnowledgeLayers.Platforms(dir).Active);
                KnowledgeLayers.Entry Find() => entries.FirstOrDefault(x => x.Active && x.Name.Equals(Str(a, "name") ?? throw new Exception("name is required"), StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception($"Workflow '{Str(a, "name")}' not found or switched off. Available: {string.Join(", ", entries.Where(x => x.Active).Select(x => x.Name))}");
                switch (action)
                {
                    case "list":
                        return Workflows.Catalog(entries) ?? "No workflows found (built-in: knowledge\\workflows next to TcAgent.exe; " +
                                                             "own: <level>\\workflows\\<name>\\WORKFLOW.md, see tc_knowledge).";
                    case "show": return Workflows.Show(Find());
                    case "start":
                        if (run != null) throw new Exception($"Workflow {run.Workflow} is still running - finish it first (tc_workflow action=finish, abort=true to stop it)");
                        try { run = Workflows.Start(Find(), Str(a, "task")); }
                        catch (FormatException ex) { throw new Exception("Workflow file invalid: " + ex.Message); }
                        Log.Info($"workflow {run.Workflow} started");
                        return Workflows.Status(run);
                    default: throw new Exception("action: list, show, start, step, status or finish");
                }
            });
    }

    // Hard gate: activating a configuration that does not build would put a broken program on the target. A build
    // (without "check all objects" - that also checks code that is never called) must have 0 errors. Saving stays allowed.
    // The build decides; the agent is told to run tc_check before (faster, no code generated) - see tc_build.
    void ActivateGate()
    {
        if (Environment.GetEnvironmentVariable("TCAGENT_ACTIVATE_GATE") == "0") return;
        var (ok, report) = tc.Sta.Run(() => tc.BuildForActivate());
        if (ok) return;
        if (run != null) run.ActivateStops++;
        Log.Info("tc_activate not executed: build has errors");
        throw new Exception("NOT activated: the build has errors. The user asked to activate, not to change the code: show the user these errors " +
                            "and ask (tc_ask_user) whether you shall fix them - do not change code on your own to get past this gate, and do not " +
                            "activate again without the user's go-ahead after the fix.\n\n" + report);
    }

    // What a tool call means for the running workflow (failed checks with their error codes, checkpoints, skills used)
    void ObserveWorkflow(string name, JsonObject args, bool isError, string text)
    {
        if (run == null) return;
        if (name == "tc_skills" && !isError && string.Equals(Str(args, "action"), "show", StringComparison.OrdinalIgnoreCase) && Str(args, "name") is { } skill)
            run.SkillsUsed.Add(skill.Trim().ToLowerInvariant());
        // the changed objects for the summary (deterministic - the agent does not have to remember them)
        if (!isError && !DryRun(args) && (name is "tc_plc_write" ? Str(args, "path") : name is "tc_plc_create" ? Str(args, "parentPath") + "^" + Str(args, "name")
                                          : name is "tc_test_skeleton" ? Str(args, "suiteName") ?? Str(args, "name") + "_Test" : null) is { } obj)
        {
            var shortName = obj.Contains(" Project^") ? obj[(obj.IndexOf(" Project^") + 9)..] : obj;
            if (!run.Changed.Contains(shortName)) run.Changed.Add(shortName);
        }
        if (name is not ("tc_check" or "tc_build" or "tc_test_run" or "tc_test_results")) return;
        bool ok = !isError && (name is "tc_test_run" or "tc_test_results" ? !TestsFailed(text) : !Lessons.HasCompileErrors(text));
        var codes = System.Text.RegularExpressions.Regex.Matches(text ?? "", @"\bC\d{4}\b").Select(m => m.Value).Distinct();
        Workflows.Observe(run, name, ok, codes);
    }

    static bool TestsFailed(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text ?? "", @"(?i)\b[1-9]\d*\s+(?:failed|failures?)\b|\bFAILED\b");
}
