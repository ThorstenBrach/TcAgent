// Search in the project files (tc_find_references): hits with tree path, part and line, the index of the objects.
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using Xunit;

namespace TcAgent.Tests;

public class PlainTextSearchTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-search-" + Guid.NewGuid().ToString("N"));

    public PlainTextSearchTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Plc", "POUs", "Axis"));
        File.WriteAllText(Path.Combine(dir, "Plc", "Plc.plcproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Name>Plc</Name></PropertyGroup>
              <ItemGroup>
                <Compile Include="POUs\Axis\FB_Axis.TcPOU"><SubType>Code</SubType></Compile>
                <Compile Include="POUs\Missing.TcPOU"><SubType>Code</SubType></Compile>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Plc", "POUs", "Axis", "FB_Axis.TcPOU"), """
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1">
              <POU Name="FB_Axis" Id="{00000000-0000-0000-0000-000000000001}" SpecialFunc="None">
                <Declaration><![CDATA[FUNCTION_BLOCK FB_Axis
            VAR
              nError : INT := ERR_NONE;
            END_VAR]]></Declaration>
                <Implementation><ST><![CDATA[nError := 0;
            nError := ERR_LIMIT;]]></ST></Implementation>
                <Method Name="Reset" Id="{00000000-0000-0000-0000-000000000002}">
                  <Declaration><![CDATA[METHOD Reset : BOOL]]></Declaration>
                  <Implementation><ST><![CDATA[nError := ERR_LIMIT_2;
            Reset := nError = ERR_LIMIT;]]></ST></Implementation>
                </Method>
                <Property Name="Speed" Id="{00000000-0000-0000-0000-000000000003}">
                  <Declaration><![CDATA[PROPERTY Speed : LREAL]]></Declaration>
                  <Get Name="Get" Id="{00000000-0000-0000-0000-000000000004}">
                    <Declaration><![CDATA[VAR
            END_VAR]]></Declaration>
                    <Implementation><ST><![CDATA[Speed := ERR_LIMIT;]]></ST></Implementation>
                  </Get>
                </Property>
              </POU>
            </TcPlcObject>
            """);
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    PlainText.FileSearch Search(string word, int max = 100, int seconds = 30) =>
        PlainText.SearchFiles(dir, new Regex($@"\b{word}\b", RegexOptions.IgnoreCase), max, TimeSpan.FromSeconds(seconds), _ => "TIPC^Plc^Plc Project");

    [Fact]
    public void Finds_whole_words_with_tree_path_part_and_line()
    {
        var r = Search("ERR_LIMIT");
        Assert.Equal(new[]
        {
            "TIPC^Plc^Plc Project^POUs^Axis^FB_Axis|implementation|2",
            "TIPC^Plc^Plc Project^POUs^Axis^FB_Axis^Reset|implementation|2",
            "TIPC^Plc^Plc Project^POUs^Axis^FB_Axis^Speed^Get|implementation|1"
        }, r.Hits.Select(h => $"{h.Path}|{h.Part}|{h.Line}"));
        Assert.Equal(1, r.Objects);
        Assert.False(r.OutOfTime);
    }

    [Fact]
    public void Declarations_are_searched_too() =>
        Assert.Equal("declaration", Search("ERR_NONE").Hits.Single().Part);

    [Fact]
    public void Max_limits_the_hits_but_not_the_index()
    {
        var r = Search("ERR_LIMIT", max: 1);
        Assert.Single(r.Hits);
        Assert.Contains(r.Index, x => x.Name == "Get");
    }

    [Fact]
    public void A_used_up_budget_says_so() => Assert.True(Search("ERR_LIMIT", seconds: -1).OutOfTime);

    [Fact]
    public void No_plc_project_gives_null()
    {
        var empty = Directory.CreateDirectory(Path.Combine(dir, "empty")).FullName;
        Assert.Null(PlainText.SearchFiles(empty, new Regex("x"), 10, TimeSpan.FromSeconds(5), _ => ""));
    }

    [Fact]
    public void Names_resolve_through_the_index()
    {
        var index = Search("ERR_LIMIT").Index;
        Assert.Equal("TIPC^Plc^Plc Project^POUs^Axis^FB_Axis^Reset", PlainText.ResolveNames(new[] { "FB_Axis.Reset" }, index).Single());
        Assert.Equal("TIPC^Plc^Plc Project^POUs^Axis^FB_Axis", PlainText.ResolveNames(new[] { "FB_Axis.TcPOU" }, index).Single());
        Assert.Empty(PlainText.ResolveNames(new[] { "(no name)" }, index));
    }
}
