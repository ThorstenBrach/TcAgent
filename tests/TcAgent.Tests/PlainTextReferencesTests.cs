// Tests for references.json of TcAgent.exe st export|check and tc_plaintext (PlainText.cs).
//
// SPDX-License-Identifier: MIT
using System.Text;
using Xunit;

namespace TcAgent.Tests;

public sealed class PlainTextReferencesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-tests-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "sln");
    string Out => Path.Combine(dir, "out");
    string Json => Path.Combine(Out, "MyLib", "references.json");
    static string Rel => Path.Combine("MyLib", "references.json");

    public PlainTextReferencesTests() => Directory.CreateDirectory(Path.Combine(dir, "sln", "MyLib"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    // a .plcproj as TwinCAT writes it: placeholders (with parameters), library references, namespaces
    const string PlcProj = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Name>MyLib</Name>
            <DefaultNamespace>MyLib</DefaultNamespace>
          </PropertyGroup>
          <ItemGroup>
            <PlaceholderReference Include="Tc2_System">
              <DefaultResolution>Tc2_System, * (Beckhoff Automation GmbH)</DefaultResolution>
              <Namespace>Tc2_System</Namespace>
            </PlaceholderReference>
            <PlaceholderReference Include="Standard">
              <DefaultResolution>Tc2_Standard, 3.4.5.0 (Beckhoff Automation GmbH)</DefaultResolution>
              <Namespace>Tc2_Standard</Namespace>
            </PlaceholderReference>
            <PlaceholderReference Include="TcUnit">
              <DefaultResolution>TcUnit, * (www.tcunit.org)</DefaultResolution>
              <Namespace>TcUnit</Namespace>
              <Parameters>
                <Parameter ListName="GVL_PARAM_TCUNIT">
                  <Key>XUNITBUFFERSIZE</Key>
                  <Value>4194304</Value>
                </Parameter>
              </Parameters>
            </PlaceholderReference>
            <PlaceholderReference Include="aDep">
              <DefaultResolution>aDep, 1.2.3.4 (Thorsten Brach)</DefaultResolution>
              <Namespace>aDep</Namespace>
            </PlaceholderReference>
          </ItemGroup>
          <ItemGroup>
            <LibraryReference Include="Tc3_IotBase,newest,Beckhoff Automation GmbH">
              <Namespace>Tc3_IotBase</Namespace>
            </LibraryReference>
            <LibraryReference Include="MySysDep,0.0.0.1,Thorsten Brach">
              <Namespace>SysDep</Namespace>
            </LibraryReference>
          </ItemGroup>
        </Project>
        """;

    string WritePlcProj(string text = PlcProj)
    {
        var path = Path.Combine(Sln, "MyLib", "MyLib.plcproj");
        File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(true));
        return path;
    }

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    [Fact]
    public void Placeholder_LibraryReference_Newest_Namespace_Sorting()
    {
        var refs = PlainText.ReadReferences(WritePlcProj());
        Assert.Equal(new[] { "aDep", "MySysDep", "Tc2_Standard", "Tc2_System", "Tc3_IotBase", "TcUnit" }, refs.Select(r => r.Name));

        var std = refs.Single(r => r.Name == "Tc2_Standard");               // placeholder: name from DefaultResolution, not Include
        Assert.Equal("3.4.5.0", std.Version);
        Assert.Equal("Beckhoff Automation GmbH", std.DistributorName);
        Assert.Null(std.Namespace);                                          // namespace = name -> null

        Assert.Equal("*", refs.Single(r => r.Name == "Tc2_System").Version);
        Assert.Equal("*", refs.Single(r => r.Name == "Tc3_IotBase").Version); // LibraryReference: newest -> *

        var sys = refs.Single(r => r.Name == "MySysDep");
        Assert.Equal("0.0.0.1", sys.Version);
        Assert.Equal("Thorsten Brach", sys.DistributorName);
        Assert.Equal("SysDep", sys.Namespace);                               // namespace != name -> written

        Assert.Single(refs, r => r.Name == "TcUnit");                        // parameters are not taken over
        Assert.Equal("www.tcunit.org", refs.Single(r => r.Name == "TcUnit").DistributorName);
    }

    [Fact]
    public void Json_has_the_format_of_the_SRCI_repository()
    {
        var json = PlainText.ReferencesJson(new[]
        {
            new PlainText.LibraryRef { Name = "RobotLibrarySysDep", Version = "*", DistributorName = "Thorsten Brach" },
            new PlainText.LibraryRef { Name = "Tc2_Standard", Version = "3.4.5.0", DistributorName = "Beckhoff Automation GmbH" },
        });
        const string expected = """
            [
              {
                "Name": "RobotLibrarySysDep",
                "Version": "*",
                "DistributorName": "Thorsten Brach",
                "Namespace": null
              },
              {
                "Name": "Tc2_Standard",
                "Version": "3.4.5.0",
                "DistributorName": "Beckhoff Automation GmbH",
                "Namespace": null
              }
            ]
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), json);
        Assert.Equal("[]", PlainText.ReferencesJson(Array.Empty<PlainText.LibraryRef>()));
        Assert.Contains("\"Namespace\": \"A\\\"B\"", PlainText.ReferencesJson(new[] { new PlainText.LibraryRef { Name = "X", Version = "*", Namespace = "A\"B" } }));
    }

    [Fact]
    public void Export_writes_utf8_without_bom_with_crlf_next_to_the_code_folders()
    {
        WritePlcProj();
        var rep = PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Empty(rep.Errors);
        Assert.Equal(1, rep.Written);
        var data = File.ReadAllBytes(Json);
        Assert.False(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF);
        var text = Encoding.UTF8.GetString(data);
        Assert.StartsWith("[\r\n  {\r\n    \"Name\": \"aDep\",\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.EndsWith("  }\r\n]", text);
        Assert.Contains("\"Namespace\": \"SysDep\"", text);
        Assert.DoesNotContain("GVL_PARAM_TCUNIT", text);
    }

    [Fact]
    public void Unchanged_file_is_not_rewritten()
    {
        WritePlcProj();
        PlainText.Export(Sln, null, Out, check: false, clean: false);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Json, stamp);

        var rep = PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Equal(0, rep.Written);
        Assert.Equal(1, rep.Unchanged);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Json));

        // a changed reference rewrites it
        WritePlcProj(PlcProj.Replace("Tc2_Standard, 3.4.5.0", "Tc2_Standard, 3.4.6.0"));
        rep = PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Equal(1, rep.Written);
        Assert.Contains("\"Version\": \"3.4.6.0\"", File.ReadAllText(Json));
    }

    [Fact]
    public void Check_reports_missing_and_differs_and_ignores_line_ends()
    {
        WritePlcProj();
        var rep = PlainText.Export(Sln, null, Out, check: true, clean: false);
        Assert.Contains("missing: " + Rel, rep.Differences);
        Assert.False(File.Exists(Json));                                     // check writes nothing

        PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Empty(PlainText.Export(Sln, null, Out, check: true, clean: false).Differences);

        File.WriteAllText(Json, File.ReadAllText(Json).Replace("\r\n", "\n") + "\n");   // LF + final line end: no difference
        Assert.Empty(PlainText.Export(Sln, null, Out, check: true, clean: false).Differences);

        File.WriteAllText(Json, File.ReadAllText(Json).Replace("3.4.5.0", "3.4.4.0"));
        Assert.Contains("differs: " + Rel, PlainText.Export(Sln, null, Out, check: true, clean: false).Differences);
    }

    [Fact]
    public void Clean_keeps_references_json_and_import_ignores_it()
    {
        WritePlcProj();
        PlainText.Export(Sln, null, Out, check: false, clean: false);
        var orphan = Path.Combine(Out, "MyLib", "POUs", "Gone.st");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "FUNCTION_BLOCK Gone\nEND_FUNCTION_BLOCK\n");

        var rep = PlainText.Export(Sln, null, Out, check: false, clean: true);
        Assert.Equal(1, rep.Removed);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(Json));

        var plcproj = File.ReadAllBytes(Path.Combine(Sln, "MyLib", "MyLib.plcproj"));
        var imp = PlainText.Import(Sln, null, Out);
        Assert.Empty(imp.Errors);
        Assert.Empty(imp.Warnings);
        Assert.Empty(imp.Files);                                             // nothing written back
        Assert.Equal(plcproj, File.ReadAllBytes(Path.Combine(Sln, "MyLib", "MyLib.plcproj")));
    }

    [Fact]
    public void Unreadable_placeholder_is_a_warning_not_an_entry()
    {
        WritePlcProj(PlcProj.Replace("<DefaultResolution>aDep, 1.2.3.4 (Thorsten Brach)</DefaultResolution>", ""));
        var rep = PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Contains(rep.Warnings, w => w.Contains("aDep") && w.Contains("references.json"));
        Assert.DoesNotContain("\"aDep\"", File.ReadAllText(Json));
    }

    [Fact]
    public void PlaceholderResolution_overrides_the_default_resolution()
    {
        // project-specific resolution of a placeholder (library manager > placeholder > properties): that library is used
        var text = PlcProj.Replace("</Project>",
            "  <ItemGroup>\n" +
            "    <PlaceholderResolution Include=\"Tc2_System\">\n" +
            "      <Resolution>Tc2_System, 3.10.2.0 (Beckhoff Automation GmbH)</Resolution>\n" +
            "    </PlaceholderResolution>\n" +
            "    <PlaceholderResolution Include=\"Standard\">\n" +
            "      <Resolution>Tc2_StandardNew, 3.5.0.0 (Other Vendor)</Resolution>\n" +
            "    </PlaceholderResolution>\n" +
            "    <PlaceholderResolution Include=\"aDep\">\n" +
            "      <Resolution>garbage</Resolution>\n" +
            "    </PlaceholderResolution>\n" +
            "  </ItemGroup>\n" +
            "</Project>");
        Assert.NotEqual(PlcProj, text);
        var refs = PlainText.ReadReferences(WritePlcProj(text));

        var sys = refs.Single(r => r.Name == "Tc2_System");
        Assert.Equal("3.10.2.0", sys.Version);
        Assert.Null(sys.Namespace);

        var std = refs.Single(r => r.Name == "Tc2_StandardNew");            // redirected placeholder: new library
        Assert.Equal("3.5.0.0", std.Version);
        Assert.Equal("Other Vendor", std.DistributorName);
        Assert.Equal("Tc2_Standard", std.Namespace);                         // namespace stays the one of the project
        Assert.DoesNotContain(refs, r => r.Name == "Tc2_Standard");

        Assert.Equal("1.2.3.4", refs.Single(r => r.Name == "aDep").Version); // unreadable resolution: default kept

        var rep = PlainText.Export(Sln, null, Out, check: false, clean: false);
        Assert.Contains(rep.Warnings, w => w.Contains("aDep") && w.Contains("Resolution"));
    }
}
