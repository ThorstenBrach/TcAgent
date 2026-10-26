// Tests for the engineering workflows (Workflows.cs): steps, checkpoints, levels, the built-in new-feature workflow.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class WorkflowsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-wf-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");

    const string Body = "Intro text.\n\n1. **plan** – Show a plan.\n   Wait for agreement.\n2. **code** – Write it.\n" +
                        "3. **check** (requires: check-ok) – tc_check until 0 errors.\n4. Save.\n";

    public WorkflowsTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Path.Combine(dir, "company"));
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        KnowledgeLayers.BuiltInRootOverride = Path.Combine(dir, "builtin");
        Wf(@"builtin\workflows\new-feature", "new-feature", "Built-in.");
        Wf(@"company\workflows\new-driver", "new-driver", "Company driver workflow.");
        Wf(@"MachineX\.tcagent\workflows\new-feature", "new-feature", "Project variant.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        KnowledgeLayers.UserRootOverride = null;
        KnowledgeLayers.BuiltInRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Wf(string rel, string name, string desc)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, Workflows.MainFile), $"---\nname: {name}\ndescription: {desc}\n---\n{Body}");
    }

    [Fact]
    public void ParsesStepsIdsAndCheckpoints()
    {
        var s = Workflows.ParseSteps(Body);
        Assert.Equal(new[] { "plan", "code", "check", "step4" }, s.Select(x => x.Id));
        Assert.Equal("Show a plan.\nWait for agreement.", s[0].Text);
        Assert.Equal("check-ok", s[2].Requires);
        Assert.Null(s[1].Requires);
    }

    [Fact]
    public void UnknownCheckpointAndDuplicateIdsAreRejected()
    {
        Assert.Throws<FormatException>(() => Workflows.ParseSteps("1. **a** (requires: magic) – x\n"));
        Assert.Throws<FormatException>(() => Workflows.ParseSteps("1. **a** – x\n2. **a** – y\n"));
    }

    [Fact]
    public void LaterLevelReplacesWorkflowWithSameName()
    {
        var e = Workflows.Entries(Sln, Array.Empty<string>());
        var nf = e.Single(x => x.Active && x.Name == "new-feature");
        Assert.Equal("project", nf.Layer.Level);
        Assert.Contains(e, x => x.Active && x.Name == "new-driver");
        Assert.Contains("new-driver", Workflows.Catalog(e));
    }

    [Fact]
    public void CheckpointNeedsCleanCheck()
    {
        var run = Workflows.Start(Workflows.Entries(Sln, Array.Empty<string>()).First(x => x.Active && x.Name == "new-feature"), "conveyor");
        Workflows.Advance(run, "plan", false, null);
        Workflows.Observe(run, "tc_check", false);                     // during "code": counted for that step
        Workflows.Advance(run, null, false, null);
        Assert.Equal(1, run.Steps[1].FailedChecks);
        // the check step itself: no check yet, then a failed one, then a clean one
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, false, null));
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, true, null));   // cannot be skipped
        Workflows.Observe(run, "tc_check", false);
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, false, null));
        Workflows.Observe(run, "tc_build", true);
        Workflows.Advance(run, "check", false, null);
        Assert.Equal(2, run.Steps[2].GateStops);
        Assert.Equal(1, run.Steps[2].FailedChecks);
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, "code", false, null));  // wrong step id
        Workflows.Advance(run, null, true, null);
        Assert.Null(run.Active);
        var sum = Workflows.Finish(run, false);
        Assert.Contains("Failed checks: 2", sum);
        Assert.Contains("1 skipped", sum);
    }

    [Fact]
    public void ShippedWorkflowIsValid()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "knowledge", "workflows", "new-feature", Workflows.MainFile);
        var fm = KnowledgeLayers.ParseFrontMatter(File.ReadAllText(file));
        Assert.Equal("new-feature", fm.Top["name"]);
        var steps = Workflows.ParseSteps(fm.Body);
        Assert.Equal(new[] { "requirements", "change-plan", "code", "check", "tests", "review", "save" }, steps.Select(s => s.Id));
        Assert.Equal("check-ok", steps.Single(s => s.Id == "check").Requires);
    }
}
