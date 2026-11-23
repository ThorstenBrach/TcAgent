// Unit tests: the checkpoint spec-ok - the specification comes before the code, and only the user can go past it.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class SpecCheckpointTests
{
    static Workflows.Run NewRun()
    {
        var run = new Workflows.Run { Workflow = "new-feature", Level = "built-in", Task = "" };
        run.Steps.AddRange(Workflows.ParseSteps("1. **requirements** (requires: spec-ok) – Clarify.\n2. **code** – Write it.\n"));
        run.Steps[0].Status = "active";
        return run;
    }

    [Fact]
    public void TheAgentCannotGoOnAlone()
    {
        var run = NewRun();
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, false, null));
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, true, null));
        Assert.Equal("requirements", run.Active.Id);
    }

    [Fact]
    public void AConfirmedSpecificationGoesOn()
    {
        var run = NewRun();
        run.Steps[0].SpecDecided = true;
        Workflows.Advance(run, null, false, null);
        Assert.Equal("done", run.Steps[0].Status);
        Assert.Equal("code", run.Active.Id);
    }

    [Fact]
    public void TheUserCanSkipIt()
    {
        var run = NewRun();
        run.Steps[0].SpecDecided = true;
        Workflows.Advance(run, null, true, null);
        Assert.Equal("skipped", run.Steps[0].Status);
    }

    [Fact]
    public void CodeWaitsForTheDecision()
    {
        var run = NewRun();
        Assert.NotNull(Workflows.SpecPending(run));
        run.Steps[0].SpecDecided = true;
        Assert.Null(Workflows.SpecPending(run));
        Assert.Null(Workflows.SpecPending(null));
    }

    [Fact]
    public void AnAbortedStepStopsNothing()
    {
        var run = NewRun();
        run.Steps[0].Status = "skipped";
        Assert.Null(Workflows.SpecPending(run));
    }

    [Fact]
    public void OtherCheckpointsStayUnskippable()
    {
        var run = new Workflows.Run();
        run.Steps.AddRange(Workflows.ParseSteps("1. **build** (requires: build-ok) – Build.\n"));
        run.Steps[0].Status = "active";
        Assert.Throws<InvalidOperationException>(() => Workflows.Advance(run, null, true, null));
    }

    [Theory]
    [InlineData("function_block", true)]
    [InlineData("Method", true)]
    [InlineData("dut_struct", false)]
    [InlineData("folder", false)]
    public void OnlyNewFunctionsAsk(string kind, bool asks) => Assert.Equal(asks, Workflows.IsNewFunction(kind));

    [Theory]
    [InlineData("Ohne Spezifikation weiter", true)]
    [InlineData("Go on without a specification", true)]
    [InlineData("Erst klären", false)]
    [InlineData("Clarify first", false)]
    [InlineData("hmm", null)]
    public void TheNewCodeAnswerDecides(string answer, bool? expected) => Assert.Equal(expected, Workflows.NewCodeAnswer(answer));

    [Theory]
    [InlineData("Confirmed", true)]
    [InlineData("Confirmed, but name it FB_Conveyor", true)]
    [InlineData("Skip the specification", false)]
    [InlineData("Not yet", null)]
    [InlineData("Bestätigt", true)]
    [InlineData("Spezifikation überspringen", false)]
    [InlineData("Noch nicht", null)]
    [InlineData("add a timeout first", null)]
    [InlineData(null, null)]
    public void TheAnswerDecides(string answer, bool? expected) => Assert.Equal(expected, Workflows.SpecAnswer(answer));

    // ADR 0023, GAP-005: the new-code gate asks only where a person of this client sees the question at once
    [Fact]
    public void ANewCodeQuestionFromAClientWithoutElicitationAsksNobody() =>
        Assert.Empty(AskQuestions.Channels(fromChat: false, canElicit: false, ideCardFallback: false));

    [Fact]
    public void ANewCodeQuestionFromAClientWithElicitationUsesOnlyItsDialog() =>
        Assert.Equal(new[] { "client" }, AskQuestions.Channels(fromChat: false, canElicit: true, ideCardFallback: false));

    [Fact]
    public void ANewCodeQuestionFromTheChatUsesTheCard() =>
        Assert.Equal(new[] { "bridge" }, AskQuestions.Channels(fromChat: true, canElicit: false, ideCardFallback: false));

    [Fact]
    public void OtherQuestionsKeepTheIdeCardAsFallback()
    {
        Assert.Equal(new[] { "client", "bridge" }, AskQuestions.Channels(fromChat: false, canElicit: true, ideCardFallback: true));
        Assert.Equal(new[] { "bridge" }, AskQuestions.Channels(fromChat: false, canElicit: false, ideCardFallback: true));
        Assert.Equal(new[] { "bridge", "client" }, AskQuestions.Channels(fromChat: true, canElicit: true, ideCardFallback: true));
    }
}
