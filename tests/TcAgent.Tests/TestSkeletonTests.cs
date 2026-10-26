// Tests for the deterministic TcUnit test skeleton (TestSkeleton.cs).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class TestSkeletonTests
{
    const string Fb = "/// Conveyor drive\nFUNCTION_BLOCK FB_Conveyor EXTENDS FB_Base\nVAR_INPUT\n    bStart : BOOL; // start\n    fSpeed : LREAL := 0.5;\nEND_VAR\n" +
                      "VAR_OUTPUT\n    bRunning : BOOL;\n    nState : INT := 10;\n    sText : STRING(80) := 'idle';\n    stInfo : ST_Info;\n    eMode : E_Mode := E_Mode.Auto;\nEND_VAR\n" +
                      "VAR_IN_OUT\n    stIo : ST_ConveyorIo;\nEND_VAR\nVAR\n    _timer : TON;\nEND_VAR\n";

    static readonly (string, string)[] Methods =
    {
        ("M_Start", "METHOD PUBLIC M_Start : BOOL\nVAR_INPUT\n    fRamp : REAL;\nEND_VAR"),
        ("M_Reset", "METHOD M_Reset\nVAR_IN_OUT\n    nCount : DINT;\nEND_VAR"),
        ("_Calc", "METHOD PRIVATE _Calc : LREAL"),
        ("FB_init", "METHOD FB_init : BOOL\nVAR_INPUT\n    bInitRetains : BOOL;\nEND_VAR"),
    };

    [Fact]
    public void ParsesSectionsTypesAndInitialValues()
    {
        var v = TestSkeleton.ParseVars(Fb);
        Assert.Equal(new[] { "bStart", "fSpeed", "bRunning", "nState", "sText", "stInfo", "eMode", "stIo", "_timer" }, v.Select(x => x.Name));
        Assert.Equal("VAR_IN_OUT", v.Single(x => x.Name == "stIo").Section);
        Assert.Equal("STRING(80)", v.Single(x => x.Name == "sText").Type);
        Assert.Equal("'idle'", v.Single(x => x.Name == "sText").Init);
        var m = TestSkeleton.ParseMethod("M_Start", Methods[0].Item2);
        Assert.Equal(("PUBLIC", "BOOL"), (m.Access, m.ReturnType));
    }

    [Fact]
    public void DefaultsAndAsserts()
    {
        Assert.Equal("FALSE", TestSkeleton.Default("BOOL", null));
        Assert.Equal("10", TestSkeleton.Default("INT", "10"));
        Assert.Null(TestSkeleton.Default("INT", "nMax * 2"));                   // not a literal: no expected value
        Assert.Null(TestSkeleton.Default("ST_Info", null));
        Assert.Contains("Delta := 0.0001", TestSkeleton.Assert("LREAL", "0.0", "x", "m"));
        Assert.StartsWith("AssertEquals_STRING(", TestSkeleton.Assert("STRING(80)", "''", "x", "m"));
        Assert.Null(TestSkeleton.Assert("E_Mode", "E_Mode.Auto", "x", "m"));
    }

    [Fact]
    public void SuiteHasEnableBoolsCompleteInitialTestAndFailingTodos()
    {
        var s = TestSkeleton.Generate("FB_Conveyor", Fb, Methods);
        Assert.Equal("FB_Conveyor_Test", s.Fb.Name);
        Assert.Contains("EXTENDS TcUnit.FB_TestSuite", s.Fb.Declaration);
        Assert.Contains("bRun_InitialOutputs : BOOL := TRUE;", s.Fb.Declaration);
        Assert.Contains("IF bRun_Test_M_Start THEN Test_M_Start(); END_IF", s.Fb.Implementation);
        Assert.Equal(new[] { "InitialOutputs", "Test_M_Start", "Test_M_Reset" }, s.Methods.Select(m => m.Name));   // private and FB_init skipped

        var init = s.Methods[0];
        Assert.Contains("stIo_ : ST_ConveyorIo;", init.Declaration);                // VAR_IN_OUT gets a local
        Assert.Contains("fbUnderTest(stIo := stIo_);", init.Implementation);
        Assert.Contains("AssertEquals_BOOL(Expected := FALSE, Actual := fbUnderTest.bRunning", init.Implementation);
        Assert.Contains("AssertEquals_INT(Expected := 10, Actual := fbUnderTest.nState", init.Implementation);
        Assert.Contains("AssertEquals_STRING(Expected := 'idle'", init.Implementation);
        Assert.Contains("// stInfo : ST_Info - compare by hand", init.Implementation);
        Assert.Contains("// eMode : E_Mode - compare by hand", init.Implementation);
        Assert.EndsWith("TEST_FINISHED();", init.Implementation);

        var start = s.Methods[1];
        Assert.Contains("result : BOOL;", start.Declaration);
        Assert.Contains("result := fbUnderTest.M_Start(fRamp := fRamp_);", start.Implementation);   // every parameter is passed
        Assert.Contains("fRamp_ : REAL;", start.Declaration);
        Assert.Contains("AssertTrue(Condition := FALSE, Message := 'TODO", start.Implementation);
        Assert.Contains("TEST('Test_M_Start');", start.Implementation);              // the test name is the method name
        Assert.Contains("fbUnderTest.M_Reset(nCount := nCount_);", s.Methods[2].Implementation);
        Assert.Contains("_Calc: private", Assert.Single(s.Notes));
    }

    [Fact]
    public void InstanceNameCanFollowTheCodingRules()
    {
        var s = TestSkeleton.Generate("FB_Conveyor", Fb, Methods, null, "UnderTest");
        Assert.Contains("UnderTest : FB_Conveyor;", s.Methods[0].Declaration);
        Assert.Contains("UnderTest(stIo := stIo_);", s.Methods[0].Implementation);
        Assert.DoesNotContain("fbUnderTest", string.Concat(s.Methods.Select(m => m.Declaration + m.Implementation)));
    }

    [Theory]
    [InlineData("MC_ReadActualPositionFB", new[] { "TelegramTestSuiteFB", "AxisTestSuiteFB" }, "MC_ReadActualPositionTestSuiteFB")]
    [InlineData("FB_Motor", new[] { "FB_Valve_Test" }, "FB_Motor_Test")]
    [InlineData("FB_Motor", new string[0], "FB_Motor_Test")]
    public void SuiteNameFollowsTheExistingSuites(string fb, string[] existing, string expected) => Assert.Equal(expected, TestSkeleton.SuiteName(fb, existing));

    [Fact]
    public void PlainTextIsImportable()
    {
        var text = TestSkeleton.ToPlainText(TestSkeleton.Generate("FB_Conveyor", Fb, Methods, "FB_ConveyorTest"));
        Assert.StartsWith("/// Tests of FB_Conveyor", text);
        Assert.Contains("FUNCTION_BLOCK FB_ConveyorTest EXTENDS TcUnit.FB_TestSuite", text);
        Assert.Contains("\n  IF bRunSuite THEN\n", text);                     // implementation indented by two blanks
        Assert.Contains("\nEND_FUNCTION_BLOCK\n\n/// After the first call", text);
        Assert.Equal(3, text.Split("\nEND_METHOD\n").Length - 1);
    }
}
