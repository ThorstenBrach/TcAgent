// Tests for the TcUnit test discovery and the selection via enable bools (TcUnitDiscovery.cs).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class TcUnitDiscoveryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-tests-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "Demo.sln");

    public TcUnitDiscoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Tests", "Suites"));
        File.WriteAllText(Sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}") = "Demo", "Demo.tsproj", "{049A701C-159F-E1F0-E044-D535B6E20AEA}"
            EndProject
            """);
        File.WriteAllText(Path.Combine(dir, "Demo.tsproj"), """
            <?xml version="1.0"?>
            <TcSmProject><Project><Plc>
              <Project GUID="{BAC1DA7E-A7E8-487A-B44D-648E1A97A716}" Name="TestPlc" PrjFilePath="Tests\Tests.plcproj" AmsPort="852"/>
            </Plc></Project></TcSmProject>
            """);
        var compile = string.Join("", new[] { "MAIN.TcPOU", "GVL_Tests.TcGVL", @"Suites\FB_Motor_Test.TcPOU", @"Suites\FB_Base_Test.TcPOU",
                                              @"Suites\FB_Derived_Test.TcPOU", @"Suites\FB_Dynamic_Test.TcPOU", @"Suites\FB_Legacy_Test.TcPOU", "FB_Helper.TcPOU" }
            .Select(f => $"<Compile Include=\"{f}\"><SubType>Code</SubType></Compile>"));
        File.WriteAllText(Path.Combine(dir, "Tests", "Tests.plcproj"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Name>Tests</Name></PropertyGroup>
              <ItemGroup>{compile}</ItemGroup>
            </Project>
            """);

        Pou("MAIN.TcPOU", "MAIN", """
            /// Test program
            PROGRAM MAIN
            VAR
                /// Motor: start and stop
                fbMotor   : FB_Motor_Test;
                fbDerived : FB_Derived_Test;
                // fbOld : FB_Motor_Test;
                (* fbOld2 : FB_Motor_Test; *)
                aDyn      : ARRAY[1..2] OF FB_Dynamic_Test;
                fbLegacy  : FB_Legacy_Test;
                fbHelper  : FB_Helper;
            END_VAR
            """, "TcUnit.RUN_IN_SEQUENCE();\n");
        File.WriteAllText(Path.Combine(dir, "Tests", "GVL_Tests.TcGVL"), """
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1"><GVL Name="GVL_Tests" Id="{1}"><Declaration><![CDATA[VAR_GLOBAL
                /// Second motor suite
                fbMotor2 : TcUnitDemo.FB_Motor_Test;
            END_VAR
            ]]></Declaration></GVL></TcPlcObject>
            """);
        Pou(@"Suites\FB_Motor_Test.TcPOU", "FB_Motor_Test", """
            /// Motor block: start, stop
            {attribute 'no_explicit_call'}
            FUNCTION_BLOCK FB_Motor_Test EXTENDS TcUnit.FB_TestSuite
            VAR
                bRunSuite                     : BOOL := TRUE;
                bRun_WhenStartedExpectRunning : BOOL := TRUE;
                bRun_WhenStopped, bUnused     : BOOL := TRUE;
            END_VAR
            """, """
            IF bRunSuite THEN
                IF bRun_WhenStartedExpectRunning THEN WhenStartedExpectRunning(); END_IF
                IF bRun_WhenStopped THEN WhenStopped(); END_IF
                // TEST('CommentedOut');
            END_IF
            """,
            ("WhenStartedExpectRunning", "/// The motor reports Running one cycle after Start\nMETHOD WhenStartedExpectRunning\n",
             "\nTEST('WhenStartedExpectRunning');\nTEST_FINISHED();\n"),
            ("WhenStopped", "METHOD PRIVATE WhenStopped : BOOL\n", "IF TEST_ORDERED('WhenStoppedExpectStandstill') THEN\n  TEST_FINISHED();\nEND_IF\n"));
        Pou(@"Suites\FB_Base_Test.TcPOU", "FB_Base_Test", """
            FUNCTION_BLOCK ABSTRACT FB_Base_Test EXTENDS FB_TestSuite
            VAR
                bRunSuite : BOOL := TRUE;
            END_VAR
            """, "");
        Pou(@"Suites\FB_Derived_Test.TcPOU", "FB_Derived_Test", """
            /// Derived suite
            FUNCTION_BLOCK FB_Derived_Test EXTENDS FB_Base_Test
            VAR
                bRun_Inline : BOOL := TRUE;
            END_VAR
            """, "IF bRunSuite THEN\n  IF bRun_Inline THEN\n    TEST('Inline');\n    TEST_FINISHED();\n  END_IF\n  TEST('NoBool');\n  TEST_FINISHED();\nEND_IF\n");
        Pou(@"Suites\FB_Dynamic_Test.TcPOU", "FB_Dynamic_Test", "FUNCTION_BLOCK FB_Dynamic_Test EXTENDS TcUnit.FB_TestSuite\nVAR\n  bRunSuite : BOOL := TRUE;\nEND_VAR\n", "",
            ("Run", "METHOD Run\nVAR_INPUT\n  Name : STRING;\nEND_VAR\n", "TEST(Name);\nTEST_FINISHED();\n"));
        Pou(@"Suites\FB_Legacy_Test.TcPOU", "FB_Legacy_Test", "FUNCTION_BLOCK FB_Legacy_Test EXTENDS TcUnit.FB_TestSuite\n", "TEST('Old');\nTEST_FINISHED();\n");
        Pou("FB_Helper.TcPOU", "FB_Helper", "FUNCTION_BLOCK FB_Helper\nVAR\nEND_VAR\n", "TEST('NotASuite');\n");
    }

    void Pou(string file, string name, string decl, string impl, params (string Name, string Decl, string Impl)[] methods)
    {
        var m = string.Concat(methods.Select(x => $"""

                <Method Name="{x.Name}" Id="{Guid.NewGuid():B}">
                  <Declaration><![CDATA[{x.Decl}]]></Declaration>
                  <Implementation>
                    <ST><![CDATA[{x.Impl}]]></ST>
                  </Implementation>
                </Method>
            """));
        File.WriteAllText(Path.Combine(dir, "Tests", file), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
              <POU Name="{name}" Id="{Guid.NewGuid():B}" SpecialFunc="None">
                <Declaration><![CDATA[{decl}]]></Declaration>
                <Implementation>
                  <ST><![CDATA[{impl}]]></ST>
                </Implementation>{m}
              </POU>
            </TcPlcObject>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    TcUnitDiscovery.Project Project() => Assert.Single(TcUnitDiscovery.Discover(Sln).Projects);

    [Fact]
    public void FindsProjectFromTsproj()
    {
        var p = Project();
        Assert.Equal("TestPlc", p.Name);
        Assert.Equal("TIPC^TestPlc^TestPlc Project", p.TreePath);
        Assert.Equal(852, p.AmsPort);
        Assert.Equal("sequence", p.RunMode);
    }

    [Fact]
    public void FindsSuitesThroughBaseClassesButNotOtherBlocks()
    {
        var p = Project();
        Assert.Equal(new[] { "FB_Base_Test", "FB_Derived_Test", "FB_Dynamic_Test", "FB_Legacy_Test", "FB_Motor_Test" }, p.Suites.Select(s => s.Type).OrderBy(x => x));
        Assert.DoesNotContain(p.Suites, s => s.Type == "FB_Helper");
    }

    [Fact]
    public void FindsInstancesInDeclarationOrderWithoutCommentedOnes()
    {
        var p = Project();
        Assert.Equal(new[] { "MAIN.fbMotor", "MAIN.fbDerived", "MAIN.aDyn[1]", "MAIN.aDyn[2]", "MAIN.fbLegacy", "GVL_Tests.fbMotor2" }, p.Instances.Select(i => i.Path));
        Assert.Equal("Motor: start and stop", p.Instances[0].Description);
        Assert.Equal("Second motor suite", p.Instances.Last().Description);
        Assert.Same(p.Instances[0].Suite, p.Instances.Last().Suite);   // namespace prefix TcUnitDemo. is ignored
    }

    [Fact]
    public void ReadsTestsWithMethodDescriptionEnableBoolAndLine()
    {
        var motor = Project().Suites.Single(s => s.Type == "FB_Motor_Test");
        Assert.Equal("Motor block: start, stop", motor.Description);
        Assert.Equal("bRunSuite", motor.RunSuiteVar);
        Assert.Equal(new[] { "WhenStartedExpectRunning", "WhenStoppedExpectStandstill" }, motor.Tests.Select(t => t.Name));
        var t1 = motor.Tests[0];
        Assert.Equal("WhenStartedExpectRunning", t1.Method);
        Assert.Equal("The motor reports Running one cycle after Start", t1.Description);
        Assert.Equal("bRun_WhenStartedExpectRunning", t1.EnableVar);
        Assert.Equal("TIPC^TestPlc^TestPlc Project^Suites^FB_Motor_Test^WhenStartedExpectRunning", t1.EditorPath);
        Assert.Equal(2, t1.PartLine);
        Assert.Equal("TEST('WhenStartedExpectRunning');", File.ReadAllLines(t1.File)[t1.Line - 1].Trim());
        // the test name differs from the method: the bool follows the method name; TEST_ORDERED is a test too
        var t2 = motor.Tests[1];
        Assert.Equal("WhenStopped", t2.Method);
        Assert.Equal("bRun_WhenStopped", t2.EnableVar);
        Assert.True(t2.Ordered);
        Assert.True(motor.TestsSelectable);
        Assert.False(motor.Dynamic);
        Assert.Equal("FUNCTION_BLOCK FB_Motor_Test EXTENDS TcUnit.FB_TestSuite", File.ReadAllLines(motor.File)[motor.Line - 1].Trim());
    }

    [Fact]
    public void InlineTestsDynamicSuitesAndLegacySuites()
    {
        var p = Project();
        var derived = p.Suites.Single(s => s.Type == "FB_Derived_Test");
        Assert.Equal("bRunSuite", derived.RunSuiteVar);                        // from the base suite
        Assert.Equal(new[] { "Inline", "NoBool" }, derived.Tests.Select(t => t.Name));
        Assert.Equal("bRun_Inline", derived.Tests[0].EnableVar);
        Assert.Null(derived.Tests[1].EnableVar);
        Assert.False(derived.TestsSelectable);

        var dyn = p.Suites.Single(s => s.Type == "FB_Dynamic_Test");
        Assert.True(dyn.Dynamic);
        Assert.Empty(dyn.Tests);

        var legacy = p.Suites.Single(s => s.Type == "FB_Legacy_Test");
        Assert.Null(legacy.RunSuiteVar);
        Assert.Equal("Old", Assert.Single(legacy.Tests).Name);
    }

    [Fact]
    public void EmptyFilterSwitchesEverythingOn()
    {
        var sel = TcUnitDiscovery.Select(Project(), null);
        Assert.Empty(sel.Errors);
        Assert.True(sel.Everything);
        Assert.All(sel.Writes, w => Assert.True(w.Value));
        Assert.Contains(("MAIN.fbMotor.bRun_WhenStopped", true), sel.Writes);
        Assert.Contains(("GVL_Tests.fbMotor2.bRunSuite", true), sel.Writes);
        Assert.Equal(6, sel.Selected.Count);
    }

    [Fact]
    public void OneTestSwitchesTheRestOff()
    {
        var sel = TcUnitDiscovery.Select(Project(), new[] { "MAIN.fbMotor@WhenStartedExpectRunning" });
        Assert.Empty(sel.Errors);
        Assert.Contains(("MAIN.fbMotor.bRunSuite", true), sel.Writes);
        Assert.Contains(("MAIN.fbMotor.bRun_WhenStartedExpectRunning", true), sel.Writes);
        Assert.Contains(("MAIN.fbMotor.bRun_WhenStopped", false), sel.Writes);
        Assert.Contains(("GVL_Tests.fbMotor2.bRunSuite", false), sel.Writes);
        Assert.Contains(("MAIN.fbDerived.bRunSuite", false), sel.Writes);
        Assert.Contains(("MAIN.aDyn[2].bRunSuite", false), sel.Writes);
        // the legacy suite cannot be switched off
        Assert.Contains(sel.AlsoRun, a => a.StartsWith("MAIN.fbLegacy"));
        Assert.Equal(new[] { "MAIN.fbMotor", "MAIN.fbLegacy" }, sel.Selected.Keys);
        Assert.Equal(new[] { "WhenStartedExpectRunning" }, sel.Selected["MAIN.fbMotor"]);
        Assert.Equal(2, sel.ExpectedCount);
    }

    [Fact]
    public void FilterByTypeTestNameAndUnknownNames()
    {
        var p = Project();
        var byType = TcUnitDiscovery.Select(p, new[] { "fb_motor_test" });
        Assert.Contains(("MAIN.fbMotor.bRunSuite", true), byType.Writes);
        Assert.Contains(("GVL_Tests.fbMotor2.bRunSuite", true), byType.Writes);
        Assert.Null(byType.Selected["MAIN.fbMotor"]);

        var byTest = TcUnitDiscovery.Select(p, new[] { "FB_Motor_Test.WhenStoppedExpectStandstill, Inline" });
        Assert.Empty(byTest.Errors);
        Assert.Equal(new[] { "WhenStoppedExpectStandstill" }, byTest.Selected["GVL_Tests.fbMotor2"]);
        // NoBool has no enable bool: it runs together with Inline
        Assert.Equal(new[] { "Inline", "NoBool" }, byTest.Selected["MAIN.fbDerived"].OrderBy(x => x));
        Assert.Contains(byTest.AlsoRun, a => a.StartsWith("MAIN.fbDerived@NoBool"));

        var bad = TcUnitDiscovery.Select(p, new[] { "Nope" });
        Assert.Contains(bad.Errors, e => e.Contains("'Nope'"));
    }

    [Fact]
    public void DynamicSuiteTestsCanBeNamedAndRunWhole()
    {
        var sel = TcUnitDiscovery.Select(Project(), new[] { "MAIN.aDyn[1]@GEN_04_X" });
        Assert.Empty(sel.Errors);
        Assert.Contains(("MAIN.aDyn[1].bRunSuite", true), sel.Writes);
        Assert.Null(sel.Selected["MAIN.aDyn[1]"]);
        Assert.Contains(sel.AlsoRun, a => a.Contains("known only at run time"));
    }

    [Fact]
    public void SavedSelectionFilesAreExpanded()
    {
        File.WriteAllText(Path.Combine(dir, "a.tcunit.json"), """{ "project": "TestPlc", "tests": [ "MAIN.fbMotor", "MAIN.fbDerived@Inline" ] }""");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "# comment\nGVL_Tests.fbMotor2@WhenStoppedExpectStandstill\n\n");
        var items = TcUnitDiscovery.ExpandSelectionFiles(new[] { "@a.tcunit.json", "@" + Path.Combine(dir, "b.txt"), "Old" }, dir);
        Assert.Equal(new[] { "MAIN.fbMotor", "MAIN.fbDerived@Inline", "GVL_Tests.fbMotor2@WhenStoppedExpectStandstill", "Old" }, items);
        var sel = TcUnitDiscovery.Select(Project(), items);
        Assert.Empty(sel.Errors);
        Assert.Contains(("MAIN.fbMotor.bRunSuite", true), sel.Writes);
        Assert.Throws<FileNotFoundException>(() => TcUnitDiscovery.ExpandSelectionFiles(new[] { "@missing.json" }, dir));
    }

    [Fact]
    public void JsonListsBoolPaths()
    {
        var json = TcUnitDiscovery.ToJson(TcUnitDiscovery.Discover(Sln)).ToJsonString();
        Assert.Contains("\"enable\":\"MAIN.fbMotor.bRun_WhenStartedExpectRunning\"", json);
        Assert.Contains("\"runSuite\":\"MAIN.fbMotor.bRunSuite\"", json);
    }

    [Fact]
    public void StripCommentsKeepsLinesAndStrings()
    {
        var s = "a (* x\ny *) b // c\nTEST('it''s // no comment');";
        var r = TcUnitDiscovery.StripComments(s);
        Assert.Equal(s.Count(c => c == '\n'), r.Count(c => c == '\n'));
        Assert.Contains("TEST('it''s // no comment');", r);
        Assert.DoesNotContain("x", r.Split('\n')[0]);
    }
}
