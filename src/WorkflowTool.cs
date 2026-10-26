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
                        if (action == "step") return Workflows.Advance(run, Str(a, "step"), Bool(a, "skip", false), Str(a, "note"));
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
    void ActivateGate()
    {
        if (Environment.GetEnvironmentVariable("TCAGENT_ACTIVATE_GATE") == "0") return;
        var (ok, report) = tc.Sta.Run(() => tc.BuildForActivate());
        if (ok) return;
        if (run != null) run.ActivateStops++;
        Log.Info("tc_activate not executed: build has errors");
        throw new Exception("NOT activated: the build has errors - fix them (tc_check / tc_build) and activate again.\n\n" + report);
    }

    // What a tool call means for the running workflow (failed checks with their error codes, checkpoints, skills used)
    void ObserveWorkflow(string name, JsonObject args, bool isError, string text)
    {
        if (run == null) return;
        if (name == "tc_skills" && !isError && string.Equals(Str(args, "action"), "show", StringComparison.OrdinalIgnoreCase) && Str(args, "name") is { } skill)
            run.SkillsUsed.Add(skill.Trim().ToLowerInvariant());
        if (name is not ("tc_check" or "tc_build" or "tc_test_run" or "tc_test_results")) return;
        bool ok = !isError && (name is "tc_test_run" or "tc_test_results" ? !TestsFailed(text) : !Lessons.HasCompileErrors(text));
        var codes = System.Text.RegularExpressions.Regex.Matches(text ?? "", @"\bC\d{4}\b").Select(m => m.Value).Distinct();
        Workflows.Observe(run, name, ok, codes);
    }

    static bool TestsFailed(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text ?? "", @"(?i)\b[1-9]\d*\s+(?:failed|failures?)\b|\bFAILED\b");
}
