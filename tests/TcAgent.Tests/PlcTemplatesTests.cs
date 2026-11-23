// Tests for finding the templates of new PLC projects (PlcTemplates.cs): classic folder and the folders of newer builds.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class PlcTemplatesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "tcagent-plctemplates-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    string Template(string relativeFolder, string name)
    {
        var dir = Path.Combine(root, relativeFolder);
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, name + ".plcproj");
        File.WriteAllText(f, "<Project />");
        return f;
    }

    [Fact]
    public void TheClassicFolderComesFirst()
    {
        var classic = Template(@"Plc\PlcTemplate\Plc Templates", "Standard PLC Template");
        Template(@"Plc\Build_4026.10\PlcTemplates\1.0.0.0\Plc Templates", "Standard PLC Template");
        Assert.Equal(classic, PlcTemplates.Find("Standard PLC Template", PlcTemplates.Folders(new[] { root })));
    }

    [Fact]
    public void WithoutTheClassicFolderTheNewestBuildIsUsed()
    {
        Template(@"Plc\Build_4024.78\PlcTemplates\1.0.0.0\Plc Templates", "Standard PLC Template");
        var newest = Template(@"Plc\Build_4026.10\PlcTemplates\1.0.0.0\Plc Templates", "Standard PLC Template");
        Template(@"Plc\Build_4026.10\PlcTemplates\0.9.0.0\Plc Templates", "Standard PLC Template");
        var folders = PlcTemplates.Folders(new[] { root });
        Assert.Equal(newest, PlcTemplates.Find("Standard PLC Template", folders));
        Assert.Equal(newest, PlcTemplates.Find("standard plc template.plcproj", folders), ignoreCase: true);
    }

    [Fact]
    public void AMissingTemplateIsNullAndTheAvailableOnesAreListed()
    {
        Template(@"Plc\Build_4024.78\PlcTemplates\1.0.0.0\Plc Templates", "Empty PLC Template");
        Template(@"Plc\PlcTemplate\Plc Templates", "Standard PLC Template");
        var folders = PlcTemplates.Folders(new[] { root });
        Assert.Null(PlcTemplates.Find("Does Not Exist", folders));
        Assert.Equal(new[] { "Empty PLC Template", "Standard PLC Template" }, PlcTemplates.Available(folders));
    }

    [Fact]
    public void NoTemplateFoldersNoFolders() => Assert.Empty(PlcTemplates.Folders(new[] { Path.Combine(root, "missing") }));
}
