// Engineering workflows (docs/KNOWLEDGE_LAYERS.md): how a kind of task is ALWAYS done, with checkpoints - a folder
// <level>\workflows\<name>\ with WORKFLOW.md (front matter like a skill: name, description, TcAgent's fields under metadata).
// The steps are the numbered list of the body:
//
//   1. **requirements** – Clarify the function: load the skill requirements-interview ...
//   4. **check** (requires: check-ok) – tc_check until 0 compiler errors.
//
//   step id     the bold word at the start (otherwise step<n>)
//   requires    a checkpoint TcAgent enforces before the step may be marked done:
//               check-ok  the last tc_check / tc_build during the step had 0 compiler errors
//               build-ok  the last tc_build during the step succeeded
//               tests-ok  the last tc_test_run / tc_test_results during the step had no failed test
//   soft steps  everything else is guidance for the agent
//
// A run (tc_workflow start ... finish) counts per step what the run logs need later: failed checks, gate stops,
// user corrections. Only counts and names - no code. The hard gate "tc_activate only after a build with 0 errors" holds
// with and without a workflow (ToolRegistry.Call).
//
// Pure logic (no IDE), so the unit tests link this file.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Workflows
{
    public const string MainFile = "WORKFLOW.md";
    public static readonly string[] Checkpoints = { "check-ok", "build-ok", "tests-ok" };

    public sealed class Step
    {
        public string Id, Text, Requires;
        public string Status = "pending";             // pending, active, done, skipped
        public int FailedChecks, GateStops;
        public DateTime? Started, Finished;
        public bool? LastCheckOk, LastBuildOk, LastTestsOk; // observed during the step
        public string Note;
        public readonly List<string> ErrorCodes = new();  // compiler error codes of the failed checks (e.g. C0032) - names, no code
    }

    public sealed class Run
    {
        public string Workflow, Level, Task;
        public DateTime Started = DateTime.Now;
        public DateTime? Finished;
        public readonly List<Step> Steps = new();
        public int Current;                            // index of the active step, Steps.Count = all done
        public int Corrections;                        // the user corrected the agent / answered differently than proposed
        public int ActivateStops;                      // tc_activate refused by the build gate
        public readonly List<string> SkillsUsed = new();
        public readonly List<string> Changed = new();       // PLC objects written during the run (summary only, not in the run log)
        public Step Active => Current < Steps.Count ? Steps[Current] : null;
    }

    public static List<KnowledgeLayers.Entry> Entries(string solutionDir, IEnumerable<string> platforms) =>
        KnowledgeLayers.Resolve("workflows", solutionDir, platforms, replaceByName: true, folderFile: MainFile);

    public static string Description(KnowledgeLayers.Entry e) =>
        e.Meta.Top.TryGetValue("description", out var d) && d.Length > 0 ? d : "(no description)";

    public static string Catalog(IEnumerable<KnowledgeLayers.Entry> entries)
    {
        var active = entries.Where(e => e.Active).ToList();
        if (active.Count == 0) return null;
        var sb = new StringBuilder("Workflows (how this team ALWAYS does these tasks - start one with tc_workflow action=start name=<name> when the task fits):\n");
        foreach (var e in active) sb.AppendLine($"- {e.Name} ({e.Layer.Label}): {Description(e)}");
        return sb.ToString();
    }

    static readonly Regex StepLine = new(@"^(\d+)\.\s+(.*)$");
    static readonly Regex StepHead = new(@"^\*\*([A-Za-z0-9][A-Za-z0-9_-]*)\*\*\s*(?:\(requires:\s*([a-z-]+)\s*\))?\s*(?:[–:-]\s*)?(.*)$");

    /// <summary>The steps of a workflow body: top-level numbered list items, indented lines continue the step.</summary>
    public static List<Step> ParseSteps(string body)
    {
        var steps = new List<Step>();
        Step cur = null;
        foreach (var raw in (body ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var m = StepLine.Match(raw);
            if (m.Success)
            {
                var text = m.Groups[2].Value.Trim();
                var h = StepHead.Match(text);
                cur = new Step { Id = h.Success ? h.Groups[1].Value.ToLowerInvariant() : "step" + (steps.Count + 1),
                                 Requires = h.Success && h.Groups[2].Success ? h.Groups[2].Value : null,
                                 Text = h.Success ? h.Groups[3].Value.Trim() : text };
                if (cur.Requires != null && !Checkpoints.Contains(cur.Requires))
                    throw new FormatException($"step {cur.Id}: requires '{cur.Requires}' unknown - {string.Join(", ", Checkpoints)}");
                steps.Add(cur);
            }
            else if (cur != null && raw.Length > 0 && char.IsWhiteSpace(raw[0]) && raw.Trim().Length > 0) cur.Text += "\n" + raw.Trim();
            else if (raw.Trim().Length > 0 && !raw.TrimStart().StartsWith("#")) cur = null;   // a paragraph ends the list
        }
        var dup = steps.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new FormatException($"step id '{dup.Key}' used twice");
        return steps;
    }

    /// <summary>The text of the workflow for the agent (tc_workflow show).</summary>
    public static string Show(KnowledgeLayers.Entry e)
    {
        var sb = new StringBuilder($"Workflow {e.Name} ({e.Layer.Label}{(e.Locked ? ", locked" : "")}) – {Description(e)}\n");
        sb.AppendLine("File: " + e.Path).AppendLine().AppendLine(e.Meta.Body.Trim());
        return sb.ToString();
    }

    public static Run Start(KnowledgeLayers.Entry e, string task)
    {
        var steps = ParseSteps(e.Meta.Body);
        if (steps.Count == 0) throw new FormatException($"workflow {e.Name} has no numbered steps");
        var run = new Run { Workflow = e.Name, Level = e.Layer.Label, Task = (task ?? "").Trim() };
        run.Steps.AddRange(steps);
        run.Steps[0].Status = "active";
        run.Steps[0].Started = DateTime.Now;
        return run;
    }

    /// <summary>Records what a tool call means for the active step (failed checks, observed checkpoints).</summary>
    public static void Observe(Run run, string tool, bool ok, IEnumerable<string> errorCodes = null)
    {
        var s = run?.Active;
        if (s == null) return;
        if (!ok && errorCodes != null) s.ErrorCodes.AddRange(errorCodes);
        switch (tool)
        {
            case "tc_check": s.LastCheckOk = ok; if (!ok) s.FailedChecks++; break;
            case "tc_build": s.LastBuildOk = ok; s.LastCheckOk = ok; if (!ok) s.FailedChecks++; break;
            case "tc_test_run" or "tc_test_results": s.LastTestsOk = ok; break;
        }
    }

    /// <summary>Why the active step may not be marked done yet (null = it may).</summary>
    public static string Missing(Step s) => s.Requires switch
    {
        "check-ok" when s.LastCheckOk != true => s.LastCheckOk == null ? "no tc_check or tc_build during this step yet" : "the last tc_check / tc_build had compiler errors",
        "build-ok" when s.LastBuildOk != true => s.LastBuildOk == null ? "no tc_build during this step yet" : "the last tc_build failed",
        "tests-ok" when s.LastTestsOk != true => s.LastTestsOk == null ? "no tc_test_run during this step yet" : "the last test run had failed tests",
        _ => null
    };

    /// <summary>Completes (or skips) the active step and activates the next one. Throws when a checkpoint is missing.</summary>
    public static string Advance(Run run, string stepId, bool skip, string note)
    {
        var s = run.Active ?? throw new InvalidOperationException("all steps are done - finish the run (tc_workflow action=finish)");
        if (!string.IsNullOrEmpty(stepId) && !stepId.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the active step is '{s.Id}', not '{stepId}' - steps are done in order (skip a step with skip=true)");
        if (skip && s.Requires != null)
            throw new InvalidOperationException($"step '{s.Id}' is a checkpoint ({s.Requires}) and cannot be skipped");
        if (!skip && Missing(s) is { } why)
        {
            s.GateStops++;
            throw new InvalidOperationException($"step '{s.Id}' needs {s.Requires}, but {why}");
        }
        s.Status = skip ? "skipped" : "done";
        s.Finished = DateTime.Now;
        s.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        run.Current++;
        if (run.Active is { } next) { next.Status = "active"; next.Started = DateTime.Now; }
        return Status(run);
    }

    public static string Status(Run run)
    {
        var sb = new StringBuilder($"Workflow {run.Workflow} ({run.Level})" + (run.Task.Length > 0 ? $" – {run.Task}" : "") + "\n");
        foreach (var s in run.Steps)
        {
            var mark = s.Status switch { "done" => "[x]", "skipped" => "[-]", "active" => "[>]", _ => "[ ]" };
            sb.Append($"{mark} {s.Id}").Append(s.Requires != null ? $" (requires {s.Requires})" : "");
            if (s.FailedChecks > 0) sb.Append($" – {s.FailedChecks} failed checks");
            sb.AppendLine();
        }
        if (run.Active is { } a) sb.AppendLine().AppendLine($"Now: {a.Id}" + (a.Requires != null ? $" (requires {a.Requires})" : "")).AppendLine(a.Text)
                                   .AppendLine().AppendLine("When the step is done: tc_workflow action=step" + (a.Requires == null ? " (skip=true if it does not apply)" : ""));
        else sb.AppendLine().AppendLine("All steps done - tc_workflow action=finish.");
        return sb.ToString();
    }

    /// <summary>Closes the run and returns the summary (the run log of later versions is built from it).</summary>
    public static string Finish(Run run, bool aborted)
    {
        run.Finished = DateTime.Now;
        var open = run.Steps.Where(s => s.Status is "pending" or "active").Select(s => s.Id).ToList();
        var sb = new StringBuilder($"Workflow {run.Workflow} {(aborted ? "aborted" : open.Count > 0 ? "finished early" : "finished")} " +
                                   System.FormattableString.Invariant($"after {(run.Finished.Value - run.Started).TotalMinutes:0.0} min") + "\n");
        sb.AppendLine($"Steps: {run.Steps.Count(s => s.Status == "done")} done, {run.Steps.Count(s => s.Status == "skipped")} skipped" +
                      (open.Count > 0 ? $", open: {string.Join(", ", open)}" : ""));
        sb.AppendLine($"Failed checks: {run.Steps.Sum(s => s.FailedChecks)}, checkpoint stops: {run.Steps.Sum(s => s.GateStops)}, " +
                      $"activate stops: {run.ActivateStops}, user corrections: {run.Corrections}");
        if (run.Changed.Count > 0)
            sb.AppendLine($"Changed objects ({run.Changed.Count}): {string.Join(", ", run.Changed.Take(30))}{(run.Changed.Count > 30 ? " …" : "")}");
        if (run.Steps.Any(s => s.FailedChecks >= 3))
            sb.AppendLine("Hint: a step needed 3 or more checks - if something was hard to find, record a lesson (tc_lessons action=add).");
        return sb.ToString();
    }
}
