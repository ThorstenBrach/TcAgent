// The cond.* rules of the formatter: brackets, one sub-condition per line, columns (interview 09.11., docs/FORMATTER.md).
//
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class StConditionTests
{
    static string N(string s) => s.Replace("\r\n", "\n");

    static StFormatConfig Aligned(params (string Key, string Value)[] more)
    {
        var c = new StFormatConfig();
        c.ApplyProfile("conditions-aligned", "test");
        foreach (var (k, v) in more) c.Set(k, v, "test");
        return c;
    }

    static string F(string code, StFormatConfig cfg)
    {
        var r = StFormatter.Format(code, cfg, StPart.Implementation);
        Assert.Null(r.Error);
        return N(r.Text);
    }

    [Fact]
    public void ProfileWritesTheAlignedStyle()
    {
        var t = F("IF bAuto AND NOT bError AND nPressure >= nMin OR bSetup AND bKey THEN\n  x := 1;\nEND_IF\n", Aligned());
        Assert.Equal("IF (((     bAuto             ) AND\n" +
                     "     ( NOT bError            ) AND\n" +
                     "     (     nPressure >= nMin )) OR\n" +
                     "    ((     bSetup            ) AND\n" +
                     "     (     bKey              )))\n" +
                     "THEN\n  x := 1;\nEND_IF\n", t);
    }

    [Fact]
    public void ComparisonsAreRightAlignedAndIfParenFollowsTheRule()
    {
        var t = F("IF nErrorNo <> 0 AND sPartId = '' THEN\n  x := 1;\nEND_IF\n", Aligned(("cond.keyword-paren", "none")));
        Assert.StartsWith("IF(( nErrorNo <> 0  ) AND\n   ( sPartId   = '' ))\n", t);
    }

    [Fact]
    public void NotInFrontOfAComparisonKeepsItsPlace()
    {
        // ( NOT a >= b ) would mean (NOT a) >= b
        var t = F("IF NOT ( nMode >= 3 ) AND bReady THEN\n  x := 1;\nEND_IF\n", Aligned());
        Assert.Contains("NOT ( nMode >= 3 )", t.Replace("  ", " ").Replace("  ", " "));
        // NOT a = b without brackets is one sub-condition: nothing moves
        var u = F("IF NOT bA = bB AND bC THEN\n  x := 1;\nEND_IF\n", Aligned());
        Assert.Contains("( NOT bA = bB )", u);
    }

    [Fact]
    public void BitAccessAndCallsStayOneSubCondition()
    {
        var t = F("IF NOT dwStatus.4 AND fbTimer.Q AND F_Check(nA, nB) THEN\n  x := 1;\nEND_IF\n", Aligned());
        Assert.Contains("NOT dwStatus.4", t);
        Assert.Contains("F_Check(nA, nB)", t);
    }

    [Fact]
    public void CommentsStayWithTheirSubConditionInAColumn()
    {
        var t = F("IF nState = 3 AND // in the cycle\n   bRun OR // started\n   bEmpty // nothing left\nTHEN\n  x := 1;\nEND_IF\n", Aligned());
        var lines = t.Split('\n');
        Assert.Equal(lines[0].IndexOf("//"), lines[1].IndexOf("//"));
        Assert.Equal(lines[1].IndexOf("//"), lines[2].IndexOf("//"));
        Assert.EndsWith("// nothing left", lines[2]);
    }

    [Fact]
    public void DefaultsLeaveConditionsAlone()
    {
        var src = "IF bA AND\n   bB THEN\n  x := 1;\nEND_IF\n";
        Assert.Equal(src, F(src, new StFormatConfig()));
    }

    [Fact]
    public void BracketsAreNeverRemovedAndDoubledOnesStay()
    {
        var src = "IF ((bA)) AND bB THEN\n  x := 1;\nEND_IF\n";
        var t = F(src, Aligned());
        Assert.Contains("((bA))", t);
    }

    [Fact]
    public void SubConditionOverSeveralLinesStaysAsWritten()
    {
        var src = "IF F_Run( nA := 1,\n          nB := 2 ) AND bReady\nTHEN\n  x := 1;\nEND_IF\n";
        var t = F(src, Aligned());
        Assert.Contains("nB := 2 ) AND bReady", t);
    }

    [Theory]
    [InlineData("cond.operator", "first")]
    [InlineData("cond.group-space", "spaced")]
    [InlineData("cond.not", "outside")]
    [InlineData("cond.nest-indent", "flat")]
    [InlineData("cond.continuation", "indent")]
    [InlineData("cond.lines", "when-long")]
    public void EveryValueGivesTheSameTextOnASecondRun(string key, string value)
    {
        var cfg = Aligned((key, value));
        var once = F("IF (bA AND nB > 0) OR bC OR NOT bD THEN\n  x := 1;\nELSIF bE AND NOT (nF = 2) THEN\n  x := 2;\nEND_IF\nWHILE i < 10 AND NOT bStop DO\n  i := i + 1;\nEND_WHILE\n", cfg);
        Assert.Equal(once, F(once, cfg));
    }

    [Fact]
    public void BooleanAssignmentsOnlyWhenSwitchedOn()
    {
        var src = "bRelease := bAuto AND NOT bError OR bSetup;\n";
        Assert.Single(F(src, Aligned()).TrimEnd('\n').Split('\n'));      // one line (paren.precedence may add its brackets)
        var t = F(src, Aligned(("cond.assignments", "true")));
        Assert.StartsWith("bRelease := (((     bAuto  ) AND\n", t);
        Assert.EndsWith("));\n", t);
    }
}
