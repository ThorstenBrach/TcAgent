// Tests for the layout of the .st files of TcAgent.exe st export|check|import and tc_plaintext (PlainText.cs): the POU
// ends with END_FUNCTION_BLOCK after its own body, methods, properties and actions follow after it, implementations are
// indented by two blanks; round trip without changes, changes and new methods imported, wrong layouts refused.
//
// SPDX-License-Identifier: MIT
using System.Text;
using Xunit;

namespace TcAgent.Tests;

public sealed class PlainTextLayoutTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-tests-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "sln");
    string Out => Path.Combine(dir, "out");
    string Pou => Path.Combine(Sln, "MyPlc", "POUs", "FB_Motor.TcPOU");
    string St => Path.Combine(Out, "MyPlc", "POUs", "FB_Motor.st");
    static string Rel => Path.Combine("MyPlc", "POUs", "FB_Motor.st");

    public PlainTextLayoutTests()
    {
        Directory.CreateDirectory(Path.Combine(Sln, "MyPlc", "POUs"));
        File.WriteAllText(Path.Combine(Sln, "MyPlc", "MyPlc.plcproj"), Crlf(PlcProj), new UTF8Encoding(true));
        File.WriteAllText(Pou, Crlf(FbMotor), new UTF8Encoding(true));
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    const string PlcProj = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Name>MyPlc</Name>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="POUs\FB_Motor.TcPOU">
              <SubType>Code</SubType>
            </Compile>
          </ItemGroup>
          <ItemGroup>
            <Folder Include="POUs" />
          </ItemGroup>
        </Project>
        """;

    // a function block as TwinCAT writes it: method, property with Get/Set, action, LineIds; the body ends with a comment
    const string FbMotor = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4026.13">
          <POU Name="FB_Motor" Id="{0b1c2d3e-0000-4000-8000-000000000001}" SpecialFunc="None">
            <Declaration><![CDATA[/// A motor
        FUNCTION_BLOCK FB_Motor
        VAR_INPUT
          Enable : BOOL;
        END_VAR
        VAR
          _speed : LREAL;
        END_VAR
        ]]></Declaration>
            <Implementation>
              <ST><![CDATA[IF NOT Enable THEN
          A_Reset();
        END_IF
        // end of the body]]></ST>
            </Implementation>
            <Method Name="M_Start" Id="{0b1c2d3e-0000-4000-8000-000000000002}">
              <Declaration><![CDATA[/// Starts the motor
        METHOD PUBLIC M_Start : BOOL
        VAR_INPUT
          Speed : LREAL;
        END_VAR
        ]]></Declaration>
              <Implementation>
                <ST><![CDATA[_speed := Speed;
        M_Start := TRUE;]]></ST>
              </Implementation>
            </Method>
            <Property Name="P_Speed" Id="{0b1c2d3e-0000-4000-8000-000000000003}">
              <Declaration><![CDATA[PROPERTY PUBLIC P_Speed : LREAL]]></Declaration>
              <Get Name="Get" Id="{0b1c2d3e-0000-4000-8000-000000000004}">
                <Declaration><![CDATA[VAR
        END_VAR
        ]]></Declaration>
                <Implementation>
                  <ST><![CDATA[P_Speed := _speed;]]></ST>
                </Implementation>
              </Get>
              <Set Name="Set" Id="{0b1c2d3e-0000-4000-8000-000000000005}">
                <Declaration><![CDATA[VAR
        END_VAR
        ]]></Declaration>
                <Implementation>
                  <ST><![CDATA[_speed := P_Speed;]]></ST>
                </Implementation>
              </Set>
            </Property>
            <Action Name="A_Reset" Id="{0b1c2d3e-0000-4000-8000-000000000006}">
              <Implementation>
                <ST><![CDATA[_speed := 0;]]></ST>
              </Implementation>
            </Action>
            <LineIds Name="FB_Motor">
              <LineId Id="3" Count="2" />
            </LineIds>
            <LineIds Name="FB_Motor.M_Start">
              <LineId Id="2" Count="1" />
            </LineIds>
          </POU>
        </TcPlcObject>
        """;

    PlainText.Report Export(bool check = false) => PlainText.Export(Sln, null, Out, check: check, clean: false);

    const string Expected = """
        /// A motor
        FUNCTION_BLOCK FB_Motor
        VAR_INPUT
          Enable : BOOL;
        END_VAR
        VAR
          _speed : LREAL;
        END_VAR

          IF NOT Enable THEN
            A_Reset();
          END_IF
          // end of the body

        END_FUNCTION_BLOCK

        /// Starts the motor
        METHOD PUBLIC M_Start : BOOL
        VAR_INPUT
          Speed : LREAL;
        END_VAR

          _speed := Speed;
          M_Start := TRUE;

        END_METHOD

        PROPERTY PUBLIC P_Speed : LREAL


        GET
        VAR
        END_VAR

          P_Speed := _speed;

        END_GET

        SET
        VAR
        END_VAR

          _speed := P_Speed;

        END_SET

        ACTION A_Reset:

          _speed := 0;

        END_ACTION


        """;

    [Fact]
    public void Export_writes_the_sub_blocks_after_the_end_of_the_function_block()
    {
        var rep = Export();
        Assert.Empty(rep.Errors);
        Assert.DoesNotContain(rep.Warnings, w => w.Contains("reproduce"));
        Assert.Equal(Crlf(Expected), File.ReadAllText(St));
    }

    [Fact]
    public void Import_of_the_unchanged_export_writes_nothing()
    {
        Export();
        var before = File.ReadAllBytes(Pou);
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Empty(imp.Files);
        Assert.Equal(before, File.ReadAllBytes(Pou));
        Assert.Empty(Export(check: true).Differences);
    }

    [Fact]
    public void Methods_before_END_FUNCTION_BLOCK_are_refused()
    {
        Export();
        var text = File.ReadAllText(St);
        // layout of earlier TcAgent versions: sub-blocks inside FUNCTION_BLOCK .. END_FUNCTION_BLOCK
        var inside = text.Replace("END_FUNCTION_BLOCK\r\n\r\n", "").TrimEnd() + "\r\n\r\nEND_FUNCTION_BLOCK\r\n";
        File.WriteAllText(St, inside, new UTF8Encoding(false));
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Contains(imp.Errors, e => e.Contains("follow after the END_ line"));
        Assert.Empty(imp.Files);
    }

    [Fact]
    public void Implementation_without_indentation_is_refused()
    {
        Export();
        File.WriteAllText(St, File.ReadAllText(St).Replace("  M_Start := TRUE;", "M_Start := TRUE;"), new UTF8Encoding(false));
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Contains(imp.Errors, e => e.Contains("must be indented by two blanks"));
        Assert.Empty(imp.Files);
    }

    [Fact]
    public void Whitespace_at_line_ends_is_not_a_change()
    {
        Export();
        // an editor that removes trailing blanks turns the indented empty lines into empty lines
        File.WriteAllText(St, string.Join("\r\n", File.ReadAllText(St).Split("\r\n").Select(l => l.TrimEnd())), new UTF8Encoding(false));
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Empty(imp.Files);
    }

    [Fact]
    public void New_method_is_created()
    {
        Export();
        var text = File.ReadAllText(St).Replace("ACTION A_Reset:", "METHOD M_Stop\r\nVAR_INPUT\r\nEND_VAR\r\n\r\n  _speed := 0.0;\r\n  A_Reset();\r\n\r\nEND_METHOD\r\n\r\nACTION A_Reset:");
        File.WriteAllText(St, text, new UTF8Encoding(false));
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Contains(imp.Lines, l => l.Contains("new METHOD FB_Motor.M_Stop"));
        PlainText.WriteFiles(imp);
        var xml = File.ReadAllText(Pou);
        Assert.Contains("<Method Name=\"M_Stop\"", xml);
        Assert.Contains("_speed := 0.0;\r\nA_Reset();]]></ST>", xml);                              // indentation removed
        Export();                                                                                     // new sub-objects come last in the project
        Assert.Empty(PlainText.Import(Sln, null, Out).Files);
    }

    [Fact]
    public void Changed_method_in_the_new_layout_changes_only_that_method()
    {
        Export();
        File.WriteAllText(St, File.ReadAllText(St).Replace("M_Start := TRUE;", "M_Start := Speed > 0.0;"), new UTF8Encoding(false));

        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Single(imp.Files);
        Assert.Contains(imp.Lines, l => l.EndsWith("FB_Motor.st: changed FB_Motor.M_Start (implementation)", StringComparison.Ordinal));
        PlainText.WriteFiles(imp);

        var xml = File.ReadAllText(Pou);
        Assert.Contains("M_Start := Speed > 0.0;]]></ST>", xml);
        Assert.Contains("// end of the body]]></ST>", xml);                                          // the body stays as it was
        Assert.DoesNotContain("<LineIds Name=\"FB_Motor.M_Start\">", xml);                            // LineIds of the changed block removed
        Assert.Contains("<LineIds Name=\"FB_Motor\">", xml);
        for (int i = 1; i <= 6; i++) Assert.Contains($"{{0b1c2d3e-0000-4000-8000-00000000000{i}}}", xml);   // Ids kept
        Assert.Empty(PlainText.Import(Sln, null, Out).Files);                                        // idempotent
    }
}
