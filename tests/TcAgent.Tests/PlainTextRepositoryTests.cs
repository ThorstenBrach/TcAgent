// Tests of the .st text of TcAgent.exe st export|check|import and tc_plaintext (PlainText.cs) with objects of a real
// library (function block with property and methods, interface, function, alias DUT, GVL): the expected texts are
// reference text files of these objects - byte-identical (CRLF, UTF-8 without BOM) - and importing them changes nothing.
//
// SPDX-License-Identifier: MIT
using System.Text;
using Xunit;

namespace TcAgent.Tests;

public sealed class PlainTextRepositoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-tests-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "sln");
    string Out => Path.Combine(dir, "out");

    static readonly (string Folder, string Name, string Ext, string Xml, string St)[] Objects =
    {
        (@"POUs\_internal\Recv", "MyLibraryResponseDataFB", ".TcPOU", ResponseDataFbXml, ResponseDataFbSt),
        (@"Interfaces", "IMyLibraryCommandFB", ".TcIO", CommandItfXml, CommandItfSt),
        (@"Functions\Swap", "SwapIecTime", ".TcPOU", SwapFunctionXml, SwapFunctionSt),
        (@"Enumerations\Flag", "SequenceFlagEnum", ".TcDUT", AliasDutXml, AliasDutSt),
        (@"Constants", "MyLibraryDefines", ".TcGVL", DefinesGvlXml, DefinesGvlSt),
    };

    public PlainTextRepositoryTests()
    {
        Directory.CreateDirectory(Path.Combine(Sln, "MyLibrary"));
        File.WriteAllText(Path.Combine(Sln, "MyLibrary", "MyLibrary.plcproj"), Crlf(PlcProj), new UTF8Encoding(true));
        foreach (var o in Objects)
        {
            var path = Path.Combine(Sln, "MyLibrary", "Library", o.Folder.Replace('\\', Path.DirectorySeparatorChar), o.Name + o.Ext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Crlf(o.Xml), new UTF8Encoding(true));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    string StPath((string Folder, string Name, string Ext, string Xml, string St) o) =>
        Path.Combine(Out, "MyLibrary", "Library", o.Folder.Replace('\\', Path.DirectorySeparatorChar), o.Name + ".st");

    [Fact]
    public void Export_writes_the_reference_text_byte_identical()
    {
        var rep = PlainText.Export(Sln, new List<string>(), Out, check: false, clean: false);
        Assert.Empty(rep.Errors);
        Assert.Empty(rep.Warnings);
        Assert.Equal(Objects.Length, rep.Objects);
        foreach (var o in Objects)
        {
            var expected = new UTF8Encoding(false).GetBytes(Crlf(o.St + "\n"));
            Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(File.ReadAllBytes(StPath(o))));
            Assert.Equal(expected, File.ReadAllBytes(StPath(o)));
        }
        Assert.Empty(PlainText.Export(Sln, new List<string>(), Out, check: true, clean: false).Differences);
    }

    [Fact]
    public void Import_of_the_reference_files_changes_nothing()
    {
        foreach (var o in Objects)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StPath(o))!);
            File.WriteAllText(StPath(o), Crlf(o.St + "\n"), new UTF8Encoding(false));
        }
        var imp = PlainText.Import(Sln, new List<string>(), Out);
        Assert.Empty(imp.Errors);
        Assert.Empty(imp.Files);
        Assert.Equal(Objects.Length, imp.Objects);
    }

    [Fact]
    public void Changed_property_getter_is_imported_without_the_indentation()
    {
        var o = Objects[0];
        Directory.CreateDirectory(Path.GetDirectoryName(StPath(o))!);
        File.WriteAllText(StPath(o), Crlf(o.St + "\n").Replace("PayloadPtr < UDINT_TO_DINT(PayloadLen );", "PayloadPtr < UDINT_TO_DINT(PayloadLen);"), new UTF8Encoding(false));
        var imp = PlainText.Import(Sln, new List<string>(), Out);
        Assert.Empty(imp.Errors);
        Assert.Contains(imp.Lines, l => l.Contains("changed MyLibraryResponseDataFB.IsPayloadRemaining.Get (implementation)"));
        PlainText.WriteFiles(imp);
        var xml = File.ReadAllText(Path.Combine(Sln, "MyLibrary", "Library", "POUs", "_internal", "Recv", "MyLibraryResponseDataFB.TcPOU"));
        Assert.Contains("<![CDATA[IsPayloadRemaining := PayloadPtr < UDINT_TO_DINT(PayloadLen);]]>", xml);
    }

    const string PlcProj = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Name>MyLibrary</Name>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Library\POUs\_internal\Recv\MyLibraryResponseDataFB.TcPOU">
              <SubType>Code</SubType>
            </Compile>
            <Compile Include="Library\Interfaces\IMyLibraryCommandFB.TcIO">
              <SubType>Code</SubType>
            </Compile>
            <Compile Include="Library\Functions\Swap\SwapIecTime.TcPOU">
              <SubType>Code</SubType>
            </Compile>
            <Compile Include="Library\Enumerations\Flag\SequenceFlagEnum.TcDUT">
              <SubType>Code</SubType>
            </Compile>
            <Compile Include="Library\Constants\MyLibraryDefines.TcGVL">
              <SubType>Code</SubType>
            </Compile>
          </ItemGroup>
        </Project>
        """;

    const string ResponseDataFbXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
          <POU Name="MyLibraryResponseDataFB" Id="{66a5c2ed-51ca-48e8-9dd7-cc594336593c}" SpecialFunc="None">
            <Declaration><![CDATA[{attribute 'pack_mode' := '1'} 
        FUNCTION_BLOCK MyLibraryResponseDataFB EXTENDS MyLibraryRecvDataBaseFB
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      MyLibraryResponseDataFB                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2025-03-29
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2025 Thorsten Brach. All rights reserved                                                                                                                                            
        //             Licensed under the MIT License.
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------

        VAR_INPUT
          /// Payload
          Payload    : ARRAY [0..MyLibraryParameter.RESPONSE_PAYLOAD_MAX] OF BYTE;
        END_VAR]]></Declaration>
            <Implementation>
              <ST><![CDATA[]]></ST>
            </Implementation>
            <Property Name="IsPayloadRemaining" Id="{c823621a-be34-4995-986e-d57faa8a2eaa}">
              <Declaration><![CDATA[PROPERTY IsPayloadRemaining : BOOL]]></Declaration>
              <Get Name="Get" Id="{89e215ee-922d-4b89-92b3-ed0bb9211f49}">
                <Declaration><![CDATA[VAR
        END_VAR
        ]]></Declaration>
                <Implementation>
                  <ST><![CDATA[IsPayloadRemaining := PayloadPtr < UDINT_TO_DINT(PayloadLen );]]></ST>
                </Implementation>
              </Get>
            </Property>
            <Method Name="Reset" Id="{29498e8a-1ee9-49da-a6d3-e8e1c2152f38}">
              <Declaration><![CDATA[/// Resets the payload buffer, the payload pointer and the payload length
        METHOD Reset
        VAR_INPUT
        END_VAR
        ]]></Declaration>
              <Implementation>
                <ST><![CDATA[SUPER^.Reset();

        // Reset payload buffer
        SysDepMemSet(pDest := ADR(Payload), Value := 0 , DataLen := SIZEOF(Payload));
        ]]></ST>
              </Implementation>
            </Method>
            <Method Name="UpdatePointer" Id="{73d45786-c4c8-463a-b16b-859e2a58e1e4}">
              <Declaration><![CDATA[METHOD UpdatePointer
        VAR_INPUT
        END_VAR
        ]]></Declaration>
              <Implementation>
                <ST><![CDATA[pPayload     := ADR(Payload);
        _payloadSize := SIZEOF(Payload); // ST-FIX F22]]></ST>
              </Implementation>
            </Method>
            <LineIds Name="MyLibraryResponseDataFB">
              <LineId Id="2" Count="0" />
            </LineIds>
            <LineIds Name="MyLibraryResponseDataFB.IsPayloadRemaining.Get">
              <LineId Id="2" Count="0" />
            </LineIds>
            <LineIds Name="MyLibraryResponseDataFB.Reset">
              <LineId Id="3" Count="3" />
              <LineId Id="2" Count="0" />
            </LineIds>
            <LineIds Name="MyLibraryResponseDataFB.UpdatePointer">
              <LineId Id="3" Count="0" />
              <LineId Id="2" Count="0" />
            </LineIds>
          </POU>
        </TcPlcObject>
        """;

    const string ResponseDataFbSt = """
        {attribute 'pack_mode' := '1'} 
        FUNCTION_BLOCK MyLibraryResponseDataFB EXTENDS MyLibraryRecvDataBaseFB
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      MyLibraryResponseDataFB                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2025-03-29
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2025 Thorsten Brach. All rights reserved                                                                                                                                            
        //             Licensed under the MIT License.
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------

        VAR_INPUT
          /// Payload
          Payload    : ARRAY [0..MyLibraryParameter.RESPONSE_PAYLOAD_MAX] OF BYTE;
        END_VAR


        END_FUNCTION_BLOCK

        PROPERTY IsPayloadRemaining : BOOL


        GET
        VAR
        END_VAR

          IsPayloadRemaining := PayloadPtr < UDINT_TO_DINT(PayloadLen );

        END_GET

        /// Resets the payload buffer, the payload pointer and the payload length
        METHOD Reset
        VAR_INPUT
        END_VAR

          SUPER^.Reset();
          
          // Reset payload buffer
          SysDepMemSet(pDest := ADR(Payload), Value := 0 , DataLen := SIZEOF(Payload));

        END_METHOD

        METHOD UpdatePointer
        VAR_INPUT
        END_VAR

          pPayload     := ADR(Payload);
          _payloadSize := SIZEOF(Payload); // ST-FIX F22

        END_METHOD

        """;

    const string CommandItfXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
          <Itf Name="IMyLibraryCommandFB" Id="{8b2f97cd-d701-45d4-a2bd-620934b85f71}">
            <Declaration><![CDATA[INTERFACE IMyLibraryCommandFB
        ]]></Declaration>
            <Method Name="CallBack" Id="{b9b72c53-4f8f-4deb-b3a9-0f787d160a79}">
              <Declaration><![CDATA[METHOD PUBLIC CallBack : DINT
        VAR_INPUT
          /// Response data
          RspData : AxesGroupAcyclicAcrEntryRspBuffer;  
        END_VAR
        ]]></Declaration>
            </Method>
            <Property Name="CommandData" Id="{d98170c8-da07-4589-932b-e8f8018d0eb7}">
              <Declaration><![CDATA[PROPERTY CommandData : MyLibrarySendDataFB]]></Declaration>
              <Get Name="Get" Id="{de1c9b03-9057-4022-8b3d-cdee2645e380}">
                <Declaration><![CDATA[]]></Declaration>
              </Get>
              <Set Name="Set" Id="{6d94f01a-a439-48c4-a162-7b70546f079e}">
                <Declaration><![CDATA[]]></Declaration>
              </Set>
            </Property>
          </Itf>
        </TcPlcObject>
        """;

    const string CommandItfSt = """
        INTERFACE IMyLibraryCommandFB


        METHOD PUBLIC CallBack : DINT
        VAR_INPUT
          /// Response data
          RspData : AxesGroupAcyclicAcrEntryRspBuffer;  
        END_VAR

        END_METHOD

        PROPERTY CommandData : MyLibrarySendDataFB


        GET


        END_GET

        SET


        END_SET

        """;

    const string SwapFunctionXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
          <POU Name="SwapIecTime" Id="{8694cd86-88d3-490b-810f-fafc403858d5}" SpecialFunc="None">
            <Declaration><![CDATA[FUNCTION SwapIecTime : TOD
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      SwapTime                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2024-06-01
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2024 Thorsten Brach. All rights reserved                                                                                                                                            
        //             Licensed under the MIT License.
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------
        VAR_INPUT
          /// Value
          Value : TOD;
        END_VAR]]></Declaration>
            <Implementation>
              <ST><![CDATA[SwapBytes(ADR(Value),SIZEOF(Value));

        SwapIecTime := Value;]]></ST>
            </Implementation>
            <LineIds Name="SwapIecTime">
              <LineId Id="3" Count="1" />
              <LineId Id="2" Count="0" />
            </LineIds>
          </POU>
        </TcPlcObject>
        """;

    const string SwapFunctionSt = """
        FUNCTION SwapIecTime : TOD
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      SwapTime                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2024-06-01
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2024 Thorsten Brach. All rights reserved                                                                                                                                            
        //             Licensed under the MIT License.
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------
        VAR_INPUT
          /// Value
          Value : TOD;
        END_VAR

          SwapBytes(ADR(Value),SIZEOF(Value));
          
          SwapIecTime := Value;

        END_FUNCTION

        """;

    const string AliasDutXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
          <DUT Name="SequenceFlagEnum" Id="{2e3f5350-8c35-4414-8561-22a699ad7dc5}">
            <Declaration><![CDATA[TYPE SequenceFlagEnum : SequenceFlag; END_TYPE
        ]]></Declaration>
          </DUT>
        </TcPlcObject>
        """;

    const string AliasDutSt = """
        TYPE SequenceFlagEnum : SequenceFlag; END_TYPE


        """;

    const string DefinesGvlXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.16">
          <GVL Name="MyLibraryDefines" Id="{e3ec4c2f-778a-420e-9aaf-7206eca366c1}">
            <Declaration><![CDATA[{attribute 'qualified_only'}
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      MyLibraryDefines                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2024-06-01
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2024 Thorsten Brach. All rights reserved           
        //             Licensed under the MIT License.                                                                                                                                 
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------
        VAR_GLOBAL
          /// Maximal length of type name
          MaxTypeNameLength  : INT;
        END_VAR]]></Declaration>
          </GVL>
        </TcPlcObject>
        """;

    const string DefinesGvlSt = """
        {attribute 'qualified_only'}
        // ------------------------------------------------------------------------- 
        //  Robot Library                                                
        // ------------------------------------------------------------------------- 
        //                                                                           
        //  Object:      MyLibraryDefines                                       
        //  Author:      Thorsten Brach                                                
        //  Date:        2024-06-01
        //
        //  Description:                                              
        //   
        //                                                                           
        //  Copyright: 
        //    (C) 2024 Thorsten Brach. All rights reserved           
        //             Licensed under the MIT License.                                                                                                                                 
        //                                                                           
        //  Disclaimer:                                                                           
        //    This project is provided without any guarantee and can be used for   
        //    private and commercial purposes. Any use is at the user’s 
        //    own risk and responsibility.
        //
        // -------------------------------------------------------------------------
        VAR_GLOBAL
          /// Maximal length of type name
          MaxTypeNameLength  : INT;
        END_VAR


        """;
}
