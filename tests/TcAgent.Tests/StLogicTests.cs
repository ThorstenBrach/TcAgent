// Conditions as logic (StLogic.cs): reading, truth table, rewrites and their proof.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using TcAgent;
using Xunit;

public class StLogicTests
{
    static List<string> Rewrites(string c, out JsonObject o)
    {
        o = StLogic.Analyze(c);
        return o["rewrites"]!.AsArray().Select(x => x!["id"] + ": " + x["text"]).ToList();
    }

    [Fact]
    public void ReadsAtomsAndTheEqualTrueForm()
    {
        var e = StLogic.Parse("IF bReady = TRUE AND NOT bBusy AND nState = 3 AND fbTimer.Q THEN");
        Assert.Equal(new[] { "bReady", "bBusy", "nState = 3", "fbTimer.Q" }, e.Atoms);
        Assert.Equal("IF", e.Keyword);
        Assert.True(e.HasCompare);
        var f = StLogic.Parse("bRelease := bA OR bB = FALSE;");
        Assert.Equal(new[] { "bA", "bB" }, f.Atoms);
        Assert.Equal("NOT", f.Root.Kids[1].Op);
    }

    [Fact]
    public void NotInFrontOfAComparisonIsOneAtom()
    {
        var e = StLogic.Parse("NOT a = b AND c");
        Assert.Equal("NOT a = b", e.Atoms[0]);
    }

    [Fact]
    public void DeMorganInwardIsProvenTheSame()
    {
        var r = Rewrites("IF bAuto AND NOT (bError OR bFault) THEN", out var o);
        Assert.Contains("not-inward: bAuto AND NOT bError AND NOT bFault", r);
        Assert.True(o["rewrites"]!.AsArray().All(x => x!["proven"]!.GetValue<bool>()));
    }

    [Fact]
    public void DeMorganOutward()
    {
        Assert.Contains("not-outward: bAuto AND NOT (bError OR bFault)", Rewrites("bAuto AND NOT bError AND NOT bFault", out _).Select(x => x.Replace("NOT (bError OR bFault)", "NOT (bError OR bFault)")));
    }

    [Fact]
    public void SimplifiesAndWritesOut()
    {
        var r = Rewrites("a AND b OR a AND NOT b", out var o);
        Assert.Contains("simplify: a", r);
        Assert.Equal(new[] { "1-" }, o["trueRows"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("write-out: a AND NOT b OR a AND b", r);
    }

    [Fact]
    public void ACallIsNeverSimplifiedAndShortCircuitIsNotRewritten()
    {
        var r = Rewrites("F_Check(1) AND bA OR F_Check(1) AND NOT bA", out var o);
        Assert.DoesNotContain(r, x => x.StartsWith("simplify"));
        Assert.NotNull(o["simplifyNote"]);
        Rewrites("bA AND_THEN NOT (bB OR bC)", out var p);
        Assert.Empty(p["rewrites"]!.AsArray());
        Assert.NotNull(p["rewriteNote"]);
    }

    [Fact]
    public void EqualTrueUniformAndRemoved()
    {
        var r = Rewrites("IF bReady = TRUE AND nState = 3 AND NOT bBusy THEN", out _);
        Assert.Contains("bool-uniform: bReady = TRUE AND nState = 3 AND bBusy = FALSE", r);
        Assert.Contains("bool-remove: bReady AND nState = 3 AND NOT bBusy", r);
    }

    [Fact]
    public void OnlineValuesGiveTheResultAndWhatIsMissing()
    {
        var e = StLogic.Parse("IF bAuto AND NOT bError AND nPressure >= nMin OR bSetup AND eState = E_State.Run THEN");
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            { ["bAuto"] = true, ["bError"] = true, ["nPressure"] = 8.2, ["nMin"] = 6.0, ["bSetup"] = false, ["eState"] = "E_State.Idle" };
        bool? Atom(int i) => StLogic.EvalAtom(e.Atoms[i], n => values.TryGetValue(n, out var v) ? v : null);
        Assert.True(Atom(2));                       // 8.2 >= 6
        Assert.False(Atom(4));                      // Idle is not Run
        Assert.False(StLogic.Eval3(e.Root, Atom));
        var missing = StLogic.Missing(e.Root, Atom);
        Assert.Equal(new[] { (1, false) }, missing[0]);        // the shortest way: bError must be FALSE
        values.Remove("bSetup");                               // not readable
        Assert.False(StLogic.Eval3(e.Root, Atom));             // still FALSE: the second branch needs eState = Run, which is FALSE
        values["bError"] = false;
        Assert.True(StLogic.Eval3(e.Root, Atom));
        Assert.Empty(StLogic.Missing(e.Root, Atom)[0]);
    }

    [Fact]
    public void ProofFindsADifference()
    {
        var e = StLogic.Parse("a AND b");
        var wrong = LNode.Of("OR", new[] { new LNode("atom") { Atom = 0 }, new LNode("atom") { Atom = 1 } });
        Assert.False(StLogic.Same(e.Root, wrong, 2));
        Assert.True(StLogic.Same(e.Root, StLogic.NotInward(LNode.Not(LNode.Not(e.Root))), 2));
    }
}
