// tc_improve (Improver.cs, RunLogs.cs): the self-improvement loop - KPIs of the workflow runs, proposals found by fixed
// rules, applying a proposal only after the user agreed to the shown diff, the history of adopted changes with their
// effect over the next 10 runs, and the revert.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterImprove()
    {
        Add("tc_improve", "Self-improvement of the team's knowledge from the local run logs of the workflows: action=runs (KPIs per workflow: " +
                          "first-time-right, failed checks, corrections, minutes, trend), analyze (proposals found by fixed rules - a hint for a step " +
                          "that often fails, a revert of a change that did not help, moving a lesson up that several projects have, a lesson for a " +
                          "frequent compiler error, duplicate lessons, tools a skill names that no longer exist), apply id (after asking the user - " +
                          "TcAgent shows the diff and asks again; step-hint needs text, promote-lesson scope), history (adopted changes and their " +
                          "effect over the next 10 runs), revert id.",
            Schema(Req(P("action", "string", "runs, analyze, apply, history or revert")),
                   P("workflow", "string", "runs: only this workflow"),
                   P("id", "string", "apply: proposal id from analyze; revert: change id from history"),
                   P("text", "string", "apply step-hint: the hint (one or two general sentences, no customer or machine names)"),
                   P("scope", "string", "apply: step-hint project (default) / personal / platform:<name>; promote-lesson personal (default) / platform:<name> / company"),
                   P("dryRun", "boolean", "apply/revert: only show the change")),
            a =>
            {
                var dir = SolutionDir();
                var project = RunLogs.ProjectOf(dir) ?? throw new Exception("No solution open - the run logs are kept per project.");
                switch ((Str(a, "action") ?? "analyze").ToLowerInvariant())
                {
                    case "runs": return RunLogs.Kpis(project, Str(a, "workflow"));
                    case "history": return RunLogs.History(project);
                    case "analyze": return Improver.Format(Proposals(dir, project));
                    case "revert":
                    {
                        var id = Str(a, "id") ?? throw new Exception("id is required (tc_improve action=history)");
                        var c = RunLogs.Changes(project).FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception($"change {id} not found");
                        var what = $"Revert change {c.Id} ({c.Description}): restore {c.Target}";
                        if (DryRun(a)) return "PROPOSAL (nothing changed): " + what;
                        AskImprove(what);
                        return RunLogs.Revert(project, id);
                    }
                    case "apply": return ApplyProposal(a, dir, project);
                    default: throw new Exception("action: runs, analyze, apply, history or revert");
                }
            });
    }

    List<Improver.Proposal> Proposals(string dir, string project)
    {
        var active = KnowledgeLayers.Platforms(dir).Active;
        var input = new Improver.Input
        {
            Project = project, Runs = RunLogs.Load(project), Changes = RunLogs.Changes(project),
            Workflows = Workflows.Entries(dir, active), Skills = Skills.Entries(dir, active),
            ToolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase),
            LessonFolders = { Path.Combine(KnowledgeLayers.ProjectRoot(dir)!, "lessons"), Path.Combine(KnowledgeLayers.UserRoot, "lessons") },
            LessonText = string.Join("\n", Lessons.All(dir).Select(l => l.AllText))
        };
        // the project lessons of every project with run logs on this computer
        foreach (var (p, s) in KnownProjects(dir, project))
        {
            var f = Path.Combine(s, ".tcagent", "lessons");
            if (Directory.Exists(f)) input.LessonFilesByProject[p] = Directory.GetFiles(f, "*.md").ToList();
        }
        return Improver.Analyze(input);
    }

    // The projects with run logs on this computer (runs\<project>\project.json) and this one: name -> solution folder
    static Dictionary<string, string> KnownProjects(string dir, string project)
    {
        var runsRoot = Path.Combine(KnowledgeLayers.UserRoot, "runs");
        var solutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [project] = dir };
        if (Directory.Exists(runsRoot))
            foreach (var pj in Directory.GetFiles(runsRoot, "project.json", SearchOption.AllDirectories))
                try { if (JsonNode.Parse(File.ReadAllText(pj))?["solutionDir"]?.ToString() is { } s && Directory.Exists(s)) solutions.TryAdd(RunLogs.ProjectOf(s), s); } catch { }
        return solutions;
    }

    // The user decides about every change (the diff is part of the question). TCAGENT_CONFIRM=off: no question.
    void AskImprove(string what)
    {
        if (Confirm.Configured == Confirm.Level.Off) return;
        var ok = Confirm.Ask("tc_improve", what, tc.AttachedPid);
        if (ok == false && !Confirm.LastNotShown && run != null) run.Corrections++;
        if (Guards.ConfirmRefusal(ok, Confirm.LastNotShown, what) is { } refused) throw new OperationCanceledException(refused);
    }

    string ApplyProposal(JsonObject a, string dir, string project)
    {
        var id = Str(a, "id") ?? throw new Exception("id is required (tc_improve action=analyze)");
        var p = Proposals(dir, project).FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"proposal {id} not found (any more) - run tc_improve action=analyze again");
        if (!p.Applicable) throw new Exception($"NOT applied: '{p.Kind}' is not changed by TcAgent itself - {p.Apply}");
        switch (p.Kind)
        {
            case "revert":
            {
                var what = $"Revert change {p.Source}: restore {p.Target}";
                if (DryRun(a)) return "PROPOSAL (nothing changed): " + what;
                AskImprove(what);
                return RunLogs.Revert(project, p.Source);
            }
            case "step-hint":
            {
                var entry = Workflows.Entries(dir, KnowledgeLayers.Platforms(dir).Active).First(e => e.Active && e.Name == p.Workflow);
                if (entry.Locked) throw new Exception($"NOT applied: workflow {entry.Name} is locked ({entry.Layer.Label}) - only its owner can change it (pull request).");
                var hint = Str(a, "text") ?? throw new Exception("text is required: the hint for the step");
                if (Improver.Specifics(hint, Improver.PrivateTerms(dir)) is { Count: > 0 } sp)
                    throw new Exception("NOT applied: the hint names " + string.Join(", ", sp) + " - write it in general terms.");
                var target = Path.Combine(Skills.RootOf(dir, Str(a, "scope")), "workflows", entry.Name, Workflows.MainFile);
                var before = File.Exists(target) ? File.ReadAllText(target) : null;
                string after;
                try { after = Improver.InsertHint(before ?? File.ReadAllText(entry.Path), p.Step, hint); }
                catch (ArgumentException ex) { throw new Exception("NOT applied: " + ex.Message); }
                var what = $"Workflow {entry.Name}, step {p.Step}: add the line\n    Hint: {hint}\n" +
                           (before == null ? $"as a copy of the {entry.Layer.Label} workflow in {target}" : "in " + target);
                if (DryRun(a)) return "PROPOSAL (nothing changed):\n" + what;
                AskImprove(what);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, after);
                var c = RunLogs.Adopt(project, "step-hint", p.Workflow, p.Step, target, "hint: " + hint, before);
                return $"Applied ({c.Id}): {what}\nThe next {RunLogs.Window} runs of {p.Workflow} show whether it helped (tc_improve action=history).";
            }
            case "promote-lesson":
            {
                var text = File.ReadAllText(p.Source);
                var terms = Improver.PrivateTerms(dir);
                if (Improver.Specifics(text, terms.Concat(KnownProjects(dir, project).Keys.Where(k => k.Length >= 3))) is { Count: > 0 } sp)
                    throw new Exception($"NOT promoted: the lesson names {string.Join(", ", sp)} - make it general first (edit {p.Source}), then apply again.");
                string folder;
                try { folder = Improver.PromoteFolder(Str(a, "scope")); } catch (ArgumentException ex) { throw new Exception("NOT promoted: " + ex.Message); }
                var target = Path.Combine(folder, Path.GetFileName(p.Source));
                bool company = string.Equals(Str(a, "scope"), "company", StringComparison.OrdinalIgnoreCase);
                var what = $"Move the lesson {Path.GetFileName(p.Source)} up: copy to {target}" + (company ? " (company level: as a commit on a new branch for a pull request)" : "");
                if (DryRun(a)) return "PROPOSAL (nothing changed): " + what;
                AskImprove(what);
                if (company)
                    try { return Sources.CommitForPullRequest(KnowledgeLayers.CompanyRoot, p.Source, target, "Promote lesson " + Improver.LessonSlug(p.Source)); }
                    catch (InvalidOperationException ex) { throw new Exception("NOT promoted: " + ex.Message); }
                Directory.CreateDirectory(folder);
                File.Copy(p.Source, target, false);
                var c = RunLogs.Adopt(project, "promote-lesson", null, null, target, "lesson " + Improver.LessonSlug(p.Source), null);
                return $"Promoted ({c.Id}): {target}. Remove the project copies when the user agrees (tc_lessons action=remove).";
            }
        }
        throw new Exception("unknown proposal kind " + p.Kind);
    }
}
