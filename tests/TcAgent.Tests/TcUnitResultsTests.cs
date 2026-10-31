// Unit tests: the TcUnit result of the last run only, and the reserved-name hint of tc_plc_create.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class TcUnitResultsTests
{
    static readonly string[] Run1 =
    {
        "FAILED TEST 'PRG_TESTS.fbCalc@Adds', EXP: 5, ACT: 6, MSG: sum",
        "| ==========TESTS FINISHED RUNNING==========",
        "| Test suites: 1", "| Tests: 2", "| Successful tests: 1", "| Failed tests: 1", "| Duration: 0.010",
    };

    static readonly string[] Run2 =
    {
        "| ==========TESTS FINISHED RUNNING==========",
        "| Test suites: 1", "| Tests: 2", "| Successful tests: 2", "| Failed tests: 0", "| Duration: 0.008",
    };

    [Fact]
    public void FailuresOfAnEarlierRunAreNotShown()
    {
        var r = TcUnitResults.Parse(Run1.Concat(Run2));
        Assert.True(r.Finished);
        Assert.Equal(0, r.Failed);
        Assert.DoesNotContain(r.Suites.SelectMany(s => s.Cases), c => c.Failed);
    }

    [Fact]
    public void FailuresOfTheLastRunAreKept()
    {
        var r = TcUnitResults.Parse(Run2.Concat(Run1));
        Assert.Equal(1, r.Failed);
        Assert.Contains(r.Suites.SelectMany(s => s.Cases), c => c.Failed && c.Name == "Adds");
    }

    [Fact]
    public void PerTestLinesOfTheLastRunDecide()
    {
        // FAILED line of an earlier run after its end, then a run where every test passed (extended results)
        var lines = Run1.Concat(new[] { "FAILED TEST 'PRG_TESTS.fbCalc@Adds', EXP: 5, ACT: 6, MSG: late" }).Concat(Run2).Concat(new[]
        {
            "| Test suite ID=0 'PRG_TESTS.fbCalc'",
            "| Test name=Adds, test class name=PRG_TESTS.fbCalc, test status=SUCCESS, number of asserts=1, duration=0.001",
        });
        var r = TcUnitResults.Parse(lines);
        Assert.DoesNotContain(r.Suites.SelectMany(s => s.Cases), c => c.Failed);
    }

    [Fact]
    public void OneRunIsReadAsItIs()
    {
        Assert.Equal(Run1.Length, TcUnitResults.LastRun(Run1).Count);
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("mod")]
    [InlineData("STRING")]
    public void ReservedNamesGetAHint(string name)
    {
        var hint = Guards.ReservedNameHint(name);
        Assert.NotNull(hint);
        Assert.Contains("reserved word", hint);
    }

    [Fact]
    public void NormalNamesGetNoHint()
    {
        Assert.Null(Guards.ReservedNameHint("AddValue"));
        Assert.Null(Guards.ReservedNameHint("FB_Motor"));
    }
}
