// Tests for test builds in a folder (LocalBuilds.cs): the newest package of a kind, a newer version, the same version built
// again (another TcAgentBridge.dll), skip.
//
// SPDX-License-Identifier: MIT
using System.IO.Compression;
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class LocalBuildsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-lb-" + Guid.NewGuid().ToString("N"));
    string Folder => Path.Combine(dir, "bin");
    string Running => Path.Combine(dir, "ext", "TcAgentBridge.dll");

    public LocalBuildsTests()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Path.GetDirectoryName(Running)!);
        KnowledgeLayers.UserRootOverride = Path.Combine(dir, "user");
        File.WriteAllText(Running, "bridge build A");
    }

    public void Dispose()
    {
        KnowledgeLayers.UserRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Package(string name, params (string Entry, string Text)[] entries)
    {
        using var zip = ZipFile.Open(Path.Combine(Folder, name), ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
            using (var w = new StreamWriter(zip.CreateEntry(entry).Open())) w.Write(text);
    }

    [Fact]
    public void The_same_build_is_no_update_another_build_of_the_same_version_is()
    {
        Package("TcAgent-0.55.0.vsix", ("TcAgentBridge.dll", "bridge build A"), ("extension.vsixmanifest", "<x/>"));
        var r = LocalBuilds.Check(Folder, "vsix", Running, "0.55.0");
        Assert.Equal("0.55.0", r["version"]?.ToString());
        Assert.False(r["available"]!.GetValue<bool>());

        File.Delete(Path.Combine(Folder, "TcAgent-0.55.0.vsix"));
        Package("TcAgent-0.55.0.vsix", ("TcAgentBridge.dll", "bridge build B"));
        r = LocalBuilds.Check(Folder, "vsix", Running, "0.55.0");
        Assert.True(r["rebuilt"]!.GetValue<bool>());
        Assert.True(r["notify"]!.GetValue<bool>());

        LocalBuilds.Skip(r["sha"]!.ToString());
        r = LocalBuilds.Check(Folder, "vsix", Running, "0.55.0");
        Assert.True(r["available"]!.GetValue<bool>());
        Assert.False(r["notify"]!.GetValue<bool>());
    }

    [Fact]
    public void The_newest_version_wins_and_the_shell_takes_its_own_bridge()
    {
        Package("TcAgent-0.54.0.vsix", ("TcAgentBridge.dll", "old"));
        Package("TcAgent-0.56.0.vsix", ("TcAgentBridge.dll", "new"));
        var r = LocalBuilds.Check(Folder, "vsix", Running, "0.55.0");
        Assert.Equal("0.56.0", r["version"]?.ToString());
        Assert.True(r["newer"]!.GetValue<bool>());

        // the ZIP: the bridge of the shell base (32-bit shell = vs15); a ZIP without it is not offered to that shell
        Package("TcAgent-XaeShell-0.55.0.zip", ("bridge-vs17/TcAgentBridge.dll", "x64"), ("install-xaeshell.ps1", "#"));
        Assert.Null(LocalBuilds.Find(Folder, "xaeshell-vs15"));
        Assert.Equal("0.55.0", LocalBuilds.Find(Folder, "xaeshell-vs17")?.Version);
    }

    [Fact]
    public void Without_a_folder_nothing_is_offered()
    {
        var r = LocalBuilds.Check(Path.Combine(dir, "missing"), "vsix", Running, "0.55.0");
        Assert.False(r["available"]!.GetValue<bool>());
        Assert.NotNull(r["error"]);
    }
}
