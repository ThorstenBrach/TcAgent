// Unit tests: when the shipped TcUnit is installed (missing or older only) and how the shipped version is found.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class TcUnitInstallTests
{
    [Theory]
    [InlineData("1.3.2", "1.3.2.0")]
    [InlineData("v1.3", "1.3.0.0")]
    [InlineData("1.3.2.0", "1.3.2.0")]
    public void VersionsWithFewerPartsAreEqual(string s, string expected) =>
        Assert.Equal(Version.Parse(expected), TcUnitInstall.ParseVersion(s));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("1.2.3.4.5")]
    public void NoVersionGivesNull(string s) => Assert.Null(TcUnitInstall.ParseVersion(s));

    [Fact]
    public void InstallsWhenMissing() =>
        Assert.True(TcUnitInstall.NeedsInstall(Array.Empty<string>(), new Version(1, 3, 2, 0)));

    [Fact]
    public void InstallsWhenOnlyOlderVersionsAreThere() =>
        Assert.True(TcUnitInstall.NeedsInstall(new[] { "1.2.0.0", "1.3.1.0" }, new Version(1, 3, 2, 0)));

    [Theory]
    [InlineData("1.3.2")]     // the same version, written differently
    [InlineData("1.3.2.0")]
    [InlineData("1.4.0.0")]   // newer one installed: never replaced by the older shipped one
    public void KeepsSameOrNewerVersion(string installed) =>
        Assert.False(TcUnitInstall.NeedsInstall(new[] { "1.0.0.0", installed }, TcUnitInstall.ParseVersion("1.3.2")));

    [Fact]
    public void UnreadableInstalledVersionCountsAsMissing() =>
        Assert.True(TcUnitInstall.NeedsInstall(new[] { (string)null, "?" }, new Version(1, 3, 2, 0)));

    [Theory]
    [InlineData("TcUnit, 1.3.2.0 (www.tcunit.org)")]
    [InlineData("TcUnit, * (www.tcunit.org)")]
    [InlineData("Placeholder TcUnit -> TcUnit, 1.3.2.0 (www.tcunit.org)")]
    [InlineData("Placeholder MyTests -> TcUnit, 1.3.2.0 (www.tcunit.org)")]
    public void ReferenceIsFound(string reference) =>
        Assert.True(TcUnitInstall.IsReferenced(new[] { "Tc2_Standard, * (Beckhoff Automation GmbH)", reference }));

    [Fact]
    public void OtherLibrariesAreNoReference() =>
        Assert.False(TcUnitInstall.IsReferenced(new[] { "Tc2_Standard, * (Beckhoff Automation GmbH)", "TcUnitHelpers, 1.0 (Example)" }));

    [Fact]
    public void HighestShippedVersionFolderWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "tcagent-tcunit-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var v in new[] { "1.2.0", "1.10.0", "1.3.2" })
            {
                Directory.CreateDirectory(Path.Combine(root, v));
                File.WriteAllText(Path.Combine(root, v, "TcUnit.library"), "x");
            }
            Directory.CreateDirectory(Path.Combine(root, "9.9.9"));            // no library inside: ignored
            Directory.CreateDirectory(Path.Combine(root, "notes"));            // no version: ignored
            File.WriteAllText(Path.Combine(root, "notes", "TcUnit.library"), "x");
            var b = TcUnitInstall.Bundled(root);
            Assert.NotNull(b);
            Assert.Equal(new Version(1, 10, 0, 0), b.Value.Version);
            Assert.EndsWith(Path.Combine("1.10.0", "TcUnit.library"), b.Value.File);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void NoBundleFolderGivesNull() =>
        Assert.Null(TcUnitInstall.Bundled(Path.Combine(Path.GetTempPath(), "tcagent-missing-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void ShippedTcUnitHasItsLicense()
    {
        // the repository's own bundle: a library in a version folder and the MIT license next to it
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "thirdparty", "TcUnit"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var root = Path.Combine(dir, "thirdparty", "TcUnit");
        Assert.NotNull(TcUnitInstall.Bundled(root));
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(root, "LICENSE")));
    }
}
