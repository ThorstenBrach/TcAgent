// Unit tests: findings of the agent tests on a local runtime - TcUnit details in one message per line, library versions,
// the report of a login, and what belongs to a solution.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class ToolFixesTests
{
    // as the runtime messages of TcUnit look today: the details of a test are separate messages
    static readonly string[] OneMessagePerLine =
    {
        "FAILED TEST 'MAIN.fbCalc@FailsOnPurpose', EXP: 7, ACT: 6, MSG: fails on purpose",
        "| ==========TESTS FINISHED RUNNING==========",
        "| Test suites: 2", "| Tests: 3", "| Successful tests: 2", "| Failed tests: 1", "| Duration: 0.010", "| ======================================",
        "| Test suite ID=0 'MAIN.fbCalc'",
        "| ID=0 number of tests=2, number of failed tests=1, duration=4.15e-5",
        "| Test name=AddsTwoNumbers", "| Test class name=MAIN.fbCalc", "| Test status=PASS, number of asserts=2, duration=1.79e-5",
        "| Test name=FailsOnPurpose", "| Test class name=MAIN.fbCalc", "| Test status=FAIL, number of asserts=1, duration=8.8e-6",
        "| Test assert message=fails on purpose", "| Test assert type=DINT",
        "| Test suite ID=1 'MAIN.fbOrdered'",
        "| ID=1 number of tests=1, number of failed tests=0, duration=2.02e-5",
        "| Test name=FirstStep", "| Test class name=MAIN.fbOrdered", "| Test status=PASS, number of asserts=1, duration=2.7e-6",
    };

    [Fact]
    public void DetailsInSeveralMessagesBelongToTheirTest()
    {
        var r = TcUnitResults.Parse(OneMessagePerLine);
        var calc = r.Suites.Single(s => s.Name == "MAIN.fbCalc");
        Assert.Equal(2, calc.Cases.Count);
        var failed = Assert.Single(calc.Cases, c => c.Failed);
        Assert.Equal("FailsOnPurpose", failed.Name);
        Assert.Contains("EXP: 7", failed.Failure);
        Assert.Equal(1, failed.Asserts);
        Assert.False(r.Suites.Single(s => s.Name == "MAIN.fbOrdered").Cases.Single().Failed);
    }

    [Fact]
    public void ARunThatWasCutOffDoesNotRepeatAFailure()
    {
        var r = TcUnitResults.Parse(OneMessagePerLine.Prepend(OneMessagePerLine[0]));
        Assert.Equal("EXP: 7, ACT: 6, MSG: fails on purpose", r.Suites[0].Cases.Single(c => c.Failed).Failure);
    }

    [Fact]
    public void TheAssertMessageIsUsedWithoutAFailedLine()
    {
        var r = TcUnitResults.Parse(OneMessagePerLine.Skip(1));
        Assert.Equal("fails on purpose", r.Suites[0].Cases.Single(c => c.Failed).Failure);
    }

    [Fact]
    public void TheClassNameLineDecidesTheSuite()
    {
        var r = TcUnitResults.Parse(new[]
        {
            "| ==========TESTS FINISHED RUNNING==========", "| Test suite ID=0 'MAIN.fbA'",
            "| Test name=Moved", "| Test class name=MAIN.fbB", "| Test status=FAIL, number of asserts=1, duration=0.1",
        });
        Assert.Empty(r.Suites.Single(s => s.Name == "MAIN.fbA").Cases);
        Assert.True(r.Suites.Single(s => s.Name == "MAIN.fbB").Cases.Single().Failed);
    }

    [Theory]
    [InlineData("3.4.5.0", "3.4.7.0", false)]
    [InlineData("3.4.7.0", "3.4.7.0", true)]
    [InlineData("", "3.4.7.0", true)]
    [InlineData(null, "3.4.7.0", true)]
    [InlineData("*", "3.4.7.0", true)]
    [InlineData("3.4.5.0", null, true)]
    public void AVersionIsOnlyOtherWhenBothAreNamed(string a, string b, bool same) => Assert.Equal(same, LibApi.SameVersion(a, b));

    [Theory]
    [InlineData("tc2_standard, 3.4.7.0 (beckhoff automation gmbh)", "3.4.7.0")]
    [InlineData("Tc2_Standard, * (Beckhoff Automation GmbH)", "*")]
    [InlineData("no version", null)]
    [InlineData(null, null)]
    public void TheVersionComesFromTheLibraryId(string id, string version) => Assert.Equal(version, LibApi.VersionOf(id));

    [Fact]
    public void ACachedApiOfAnotherVersionIsNotUsed()
    {
        var api = new LibApi { Symbols = { new LibApi.Sym(0, -1, "F", "", null, null, new(), "tc2_standard, 3.4.7.0 (beckhoff automation gmbh)") } };
        Assert.True(api.IsVersion("3.4.7.0"));
        Assert.False(api.IsVersion("3.4.5.0"));
    }

    [Theory]
    [InlineData(0u, 100u, 0u, 100u, "Loaded: nothing")]
    [InlineData(0u, 100u, 1u, 100u, "Loaded: online change")]
    [InlineData(3u, 100u, 0u, 200u, "Loaded: full download")]
    public void ALoginSaysWhatWasLoaded(uint c0, uint t0, uint c1, uint t1, string start) =>
        Assert.StartsWith(start, ToolReports.LoadReport((c0, t0), (c1, t1)));

    [Fact]
    public void AnUnreadablePlcIsNotReportedAsNothingLoaded()
    {
        Assert.StartsWith("Loaded: unknown", ToolReports.LoadReport((null, null), (0u, 100u)));
        Assert.StartsWith("Loaded: unknown", ToolReports.LoadReport((0u, 100u), null));
    }

    [Fact]
    public void HmiProjectsComeFromTheSolutionFile()
    {
        var sln = "Project(\"{GUID}\") = \"Hmi\", \"..\\Hmi\\Hmi.hmiproj\", \"{ID}\"\nProject(\"{GUID}\") = \"Plc\", \"Plc\\Plc.tsproj\", \"{ID}\"";
        var found = ToolReports.HmiProjectsOf(sln, @"C:\Work\Machine\Twincat").ToList();
        Assert.Equal(new[] { @"C:\Work\Machine\Hmi\Hmi.hmiproj" }, found);
    }

    [Theory]
    [InlineData("\"Plc\\Plc.tsproj\"", true)]
    [InlineData("\"Plc\\Plc.TSPPROJ\"", true)]
    [InlineData("\"App\\App.csproj\"", false)]
    public void ASolutionMentionsItsTwinCatProject(string text, bool expected) =>
        Assert.Equal(expected, ToolReports.MentionsTwinCatProject(text));
}
