// Unit tests: source control bindings (TFS/VSS) of a solution are found and warned about (SourceControlBindings.cs).
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class SourceControlBindingsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-scc-" + Guid.NewGuid().ToString("N"));
    string Sln => Path.Combine(dir, "Demo.sln");

    public SourceControlBindingsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Demo", "Plc"));
        Directory.CreateDirectory(Path.Combine(dir, "Demo", "_Boot"));
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void A_clean_solution_has_no_warning()
    {
        File.WriteAllText(Sln, "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal\n");
        File.WriteAllText(Path.Combine(dir, "Demo", "Plc", "Plc.plcproj"), "<Project><PropertyGroup><Name>Plc</Name></PropertyGroup></Project>");
        Assert.Empty(SourceControlBindings.Find(Sln));
        Assert.Null(SourceControlBindings.Warning(Sln));
    }

    [Fact]
    public void Tfs_section_and_SAK_entries_are_found()
    {
        File.WriteAllText(Sln, "Global\n\tGlobalSection(TeamFoundationVersionControl) = preSolution\n\t\tSccNumberOfProjects = 2\n\tEndGlobalSection\nEndGlobal\n");
        File.WriteAllText(Path.Combine(dir, "Demo", "Plc", "Plc.plcproj"),
                          "<Project><PropertyGroup><SccProjectName>SAK</SccProjectName><SccProvider>SAK</SccProvider></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Demo", "Demo.tsproj.vspscc"), "\"\"");
        // a copy in _Boot is not a project file of the solution
        File.WriteAllText(Path.Combine(dir, "Demo", "_Boot", "Copy.plcproj"), "<Project><SccProjectName>SAK</SccProjectName></Project>");
        var hits = SourceControlBindings.Find(Sln);
        Assert.Contains(hits, h => h.StartsWith("Demo.sln"));
        Assert.Contains(hits, h => h.Contains("Plc.plcproj"));
        Assert.Contains(hits, h => h.Contains(".vspscc"));
        Assert.DoesNotContain(hits, h => h.Contains("_Boot"));
        Assert.Contains("Unbind", SourceControlBindings.Warning(Sln));
    }

    [Fact]
    public void Empty_scc_elements_are_not_a_binding()
    {
        File.WriteAllText(Sln, "Global\nEndGlobal\n");
        File.WriteAllText(Path.Combine(dir, "Demo", "Plc", "Plc.plcproj"), "<Project><SccProjectName></SccProjectName></Project>");
        Assert.Empty(SourceControlBindings.Find(Sln));
    }
}
