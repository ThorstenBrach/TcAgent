// Tests for the run logs (RunLogs.cs) and the self-improvement proposals (Improver.cs).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class ImproverTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-im-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");
    const string Project = "MachineX";

    const string Wf = "---\nname: new-feature\ndescription: x\n---\n1. **code** – Write it.\n   Keep it small.\n2. **check** (requires: check-ok) – tc_check.\n3. **save** – tc_save and tc_magic.\n";

    public ImproverTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Directory.CreateDirectory(Path.Combine(dir, @"builtin\workflows\new-feature"));
        File.WriteAllText(Path.Combine(dir, @"builtin\workflows\new-feature\WORKFLOW.md"), Wf);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    static RunLogs.Record Rec(DateTime t, int failedCheck, int corrections = 0, params string[] codes) => new()
    {
        Workflow = "new-feature", Level = "built-in", Started = t, Minutes = 5, FailedChecks = failedCheck, Corrections = corrections,
        Steps = { new RunLogs.StepLog { Id = "code", Status = "done" },
                  new RunLogs.StepLog { Id = "check", Status = "done", FailedChecks = failedCheck, ErrorCodes = codes.ToList() } }
    };

    [Fact]
    public void KeepsTheLast100RunsAndSumsUpTheRest()
    {
        var t0 = new DateTime(2026, 1, 1);
        for (int i = 0; i < 103; i++) RunLogs.Save(Project, Sln, Rec(t0.AddMinutes(i), i % 2));
        Assert.Equal(100, RunLogs.Load(Project).Count);
        var totals = RunLogs.LoadTotals(RunLogs.Root(Project));
        Assert.Equal(3, totals["new-feature"].Runs);
        Assert.Contains("103", RunLogs.Kpis(Project));     // runs incl. the summed-up ones
    }

    [Fact]
    public void ChangeIsEvaluatedAfter10RunsAndCanBeReverted()
    {
        var t0 = DateTime.Now.AddDays(-2);
        for (int i = 0; i < 10; i++) RunLogs.Save(Project, Sln, Rec(t0.AddMinutes(i), 3));
        var file = Path.Combine(dir, "wf.md");
        File.WriteAllText(file, "new");
        var c = RunLogs.Adopt(Project, "step-hint", "new-feature", "check", file, "hint", "old");
        Assert.Equal(3, c.BeforeFailedChecks);
        Assert.Null(RunLogs.Evaluate(c, RunLogs.Load(Project)).Verdict);
        var t1 = DateTime.Now.AddMinutes(1);
        for (int i = 0; i < 10; i++) RunLogs.Save(Project, Sln, Rec(t1.AddMinutes(i), 4));
        Assert.Equal("worse", RunLogs.Evaluate(c, RunLogs.Load(Project)).Verdict);
        Assert.Contains(Improver.Analyze(Input()), p => p.Kind == "revert" && p.Source == c.Id);
        RunLogs.Revert(Project, c.Id);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal("reverted", RunLogs.Changes(Project).Single().Status);
    }

    Improver.Input Input() => new()
    {
        Project = Project, Runs = RunLogs.Load(Project), Changes = RunLogs.Changes(Project),
        Workflows = Workflows.Entries(Sln, Array.Empty<string>()),
        ToolNames = new(StringComparer.OrdinalIgnoreCase) { "tc_check", "tc_save" }
    };

    [Fact]
    public void StepThatOftenFailsGetsAHintProposal()
    {
        var t0 = new DateTime(2026, 2, 1);
        for (int i = 0; i < 6; i++) RunLogs.Save(Project, Sln, Rec(t0.AddMinutes(i), 2, 0, "C0032"));
        var ps = Improver.Analyze(Input());
        var hint = Assert.Single(ps, p => p.Kind == "step-hint");
        Assert.Equal("check", hint.Step);
        Assert.Contains("C0032", hint.Evidence);
        Assert.Contains(ps, p => p.Kind == "frequent-error" && p.Title.Contains("C0032"));
        Assert.Contains(ps, p => p.Kind == "stale-tool" && p.Evidence.Contains("tc_magic"));
        // ids are stable between two analyses (apply finds the proposal again)
        Assert.Equal(hint.Id, Improver.Analyze(Input()).Single(p => p.Kind == "step-hint").Id);
    }

    [Fact]
    public void FewOrCleanRunsGiveNoHint()
    {
        var t0 = new DateTime(2026, 2, 1);
        for (int i = 0; i < 4; i++) RunLogs.Save(Project, Sln, Rec(t0.AddMinutes(i), 5));
        Assert.DoesNotContain(Improver.Analyze(Input()), p => p.Kind == "step-hint");      // fewer than 5 runs
        for (int i = 4; i < 12; i++) RunLogs.Save(Project, Sln, Rec(t0.AddMinutes(i), 0));
        Assert.DoesNotContain(Improver.Analyze(Input()), p => p.Kind == "step-hint");      // the last 10 runs: 2 × 5 + 8 × 0 = 1.0 per run
    }

    [Fact]
    public void LessonInTwoProjectsIsProposedForPromotion()
    {
        var i = Input();
        i.LessonFilesByProject["MachineX"] = new() { @"C:\a\2026-03-01-sfc-import-needs-condition.md" };
        i.LessonFilesByProject["MachineY"] = new() { @"C:\b\2026-04-11-sfc-import-needs-condition.md", @"C:\b\2026-04-12-other.md" };
        var p = Assert.Single(Improver.Analyze(i), x => x.Kind == "promote-lesson");
        Assert.Equal(@"C:\a\2026-03-01-sfc-import-needs-condition.md", p.Source);
    }

    [Fact]
    public void HintGoesBelowTheStep()
    {
        var text = Improver.InsertHint(Wf, "code", "Declare  the instance\nbefore use.");
        Assert.Contains("1. **code** – Write it.\n   Keep it small.\n   Hint: Declare the instance before use.\n2. **check**", text);
        Assert.Throws<ArgumentException>(() => Improver.InsertHint(Wf, "nope", "x"));
    }

    [Fact]
    public void SpecificNamesAreFound()
    {
        var found = Improver.Specifics("Axis 3 of MachineX stops; see machinexyz", new[] { "MachineX", "Other" });
        Assert.Equal(new[] { "MachineX" }, found);
        Assert.Contains("MachineX", Improver.PrivateTerms(Sln));
    }
}
