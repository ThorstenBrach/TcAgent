// Tests for the layout of the .st files of TcAgent.exe st export|check|import and tc_plaintext (PlainText.cs):
// methods, properties and actions inside FUNCTION_BLOCK .. END_FUNCTION_BLOCK (IEC 61131-3 3rd edition), round trip
// without changes, the 0.25 layout (sub-blocks after END_FUNCTION_BLOCK) still accepted by the import.
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

    [Fact]
    public void Export_writes_methods_properties_and_actions_inside_the_function_block()
    {
        var rep = Export();
        Assert.Empty(rep.Errors);
        Assert.DoesNotContain(rep.Warnings, w => w.Contains("reproduce"));
        var text = File.ReadAllText(St);
        int end = text.LastIndexOf("END_FUNCTION_BLOCK", StringComparison.Ordinal);
        int m = text.IndexOf("METHOD PUBLIC M_Start", StringComparison.Ordinal);
        int p = text.IndexOf("PROPERTY PUBLIC P_Speed", StringComparison.Ordinal);
        int a = text.IndexOf("ACTION A_Reset:", StringComparison.Ordinal);
        Assert.True(m > 0 && m < p && p < a && a < end, $"order: METHOD {m}, PROPERTY {p}, ACTION {a}, END_FUNCTION_BLOCK {end}");
        Assert.Equal(end, text.IndexOf("END_FUNCTION_BLOCK", StringComparison.Ordinal));              // only once
        Assert.EndsWith("END_ACTION\r\n\r\nEND_FUNCTION_BLOCK\r\n", text);                              // blank line before the END_ marker
        Assert.Contains("// end of the body\r\n\r\n/// Starts the motor\r\nMETHOD PUBLIC M_Start : BOOL\r\n", text);   // blank line after the body
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
    public void Old_layout_after_END_FUNCTION_BLOCK_is_still_imported_and_check_reports_it()
    {
        Export();
        var text = File.ReadAllText(St);
        // 0.25 layout: END_FUNCTION_BLOCK right after the body, the sub-blocks after it
        int k = text.IndexOf("\r\n\r\n/// Starts the motor", StringComparison.Ordinal);
        Assert.True(k > 0);
        const string tail = "\r\nEND_FUNCTION_BLOCK\r\n";
        var old = text[..(k + 2)] + "END_FUNCTION_BLOCK\r\n" + text[(k + 2)..^tail.Length];
        Assert.True(old.IndexOf("END_FUNCTION_BLOCK", StringComparison.Ordinal) < old.IndexOf("METHOD PUBLIC M_Start", StringComparison.Ordinal));
        Assert.EndsWith("END_ACTION\r\n", old);
        File.WriteAllText(St, old, new UTF8Encoding(false));

        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Empty(imp.Files);                                                                      // same content -> no change
        Assert.Contains("differs: " + Rel, Export(check: true).Differences);                         // a re-export rewrites it once
        Export();
        Assert.Equal(text, File.ReadAllText(St));
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
