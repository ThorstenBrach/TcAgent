// The order of the checks on every tool call (spec MCP-020, CallOrder.cs).
//
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

namespace TcAgent.Tests;

public class CallOrderTests
{
    [Fact]
    public void TheOrderIsTheOneOfTheSpec()
    {
        Assert.Equal(new[] { CallCheck.Advisor, CallCheck.NewCode, CallCheck.SpecOpen, CallCheck.CodingRules }, CallOrder.Gates);
        Assert.Equal(new[] { CallCheck.Policy, CallCheck.Arguments, CallCheck.ActivateGate, CallCheck.Confirm, CallCheck.KnowledgeBackup },
                     CallOrder.BeforeTool);
    }

    [Fact]
    public void EveryCheckHasExactlyOnePlace()
    {
        var all = CallOrder.Gates.Concat(CallOrder.BeforeTool).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(Enum.GetValues<CallCheck>().OrderBy(c => c), all.OrderBy(c => c));
    }

    [Fact]
    public void TheFirstRefusingGateWinsAndTheLaterOnesDoNotRun()
    {
        var ran = new List<CallCheck>();
        Func<string> Pass(CallCheck c) => () => { ran.Add(c); return null; };
        Func<string> Refuse(CallCheck c) => () => { ran.Add(c); return "no: " + c; };
        // given in another order than the spec's on purpose: the order comes from CallOrder, not from the dictionary
        var refusal = CallOrder.FirstRefusal(new Dictionary<CallCheck, Func<string>>
        {
            [CallCheck.CodingRules] = Refuse(CallCheck.CodingRules),
            [CallCheck.SpecOpen] = Refuse(CallCheck.SpecOpen),
            [CallCheck.NewCode] = Pass(CallCheck.NewCode),
            [CallCheck.Advisor] = Pass(CallCheck.Advisor),
        });
        Assert.NotNull(refusal);
        Assert.Equal(CallCheck.SpecOpen, refusal.Value.Check);
        Assert.Equal("no: SpecOpen", refusal.Value.Text);
        Assert.Equal(new[] { CallCheck.Advisor, CallCheck.NewCode, CallCheck.SpecOpen }, ran);
    }

    [Fact]
    public void AnAdvisorRunIsRefusedBeforeTheNewCodeQuestion()
    {
        var asked = false;
        var refusal = CallOrder.FirstRefusal(new Dictionary<CallCheck, Func<string>>
        {
            [CallCheck.Advisor] = () => "Advisor run",
            [CallCheck.NewCode] = () => { asked = true; return "question"; },
        });
        Assert.Equal(CallCheck.Advisor, refusal?.Check);
        Assert.False(asked);
    }

    [Fact]
    public void NoRefusalLetsTheCallThroughAndMissingGatesAreSkipped()
    {
        Assert.Null(CallOrder.FirstRefusal(new Dictionary<CallCheck, Func<string>> { [CallCheck.NewCode] = () => null }));
        Assert.Null(CallOrder.FirstRefusal(new Dictionary<CallCheck, Func<string>>()));
    }

    [Fact]
    public void TheActivateGateRunsAfterThePolicyAndBeforeTheConfirmation()
    {
        var ran = new List<CallCheck>();
        var ex = Assert.Throws<Exception>(() => CallOrder.RunBeforeTool(new Dictionary<CallCheck, Action>
        {
            [CallCheck.Confirm] = () => ran.Add(CallCheck.Confirm),
            [CallCheck.KnowledgeBackup] = () => ran.Add(CallCheck.KnowledgeBackup),
            [CallCheck.ActivateGate] = () => { ran.Add(CallCheck.ActivateGate); throw new Exception("NOT activated: the build has errors"); },
            [CallCheck.Policy] = () => ran.Add(CallCheck.Policy),
        }));
        Assert.StartsWith("NOT activated", ex.Message);
        // nobody is asked to confirm an activation that would be refused anyway
        Assert.Equal(new[] { CallCheck.Policy, CallCheck.ActivateGate }, ran);
    }

    [Fact]
    public void WithoutARefusalEveryCheckBeforeTheToolRunsInOrder()
    {
        var ran = new List<CallCheck>();
        CallOrder.RunBeforeTool(Enum.GetValues<CallCheck>().Reverse().ToDictionary(c => c, c => (Action)(() => ran.Add(c))));
        Assert.Equal(CallOrder.BeforeTool, ran);
    }
}
