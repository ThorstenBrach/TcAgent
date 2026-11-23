// Tests for the decision whether a tcpkg.exe may run with administrator rights (TcpkgTrust.cs, internal#414).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class TcpkgTrustTests
{
    const string ProgramData = @"C:\ProgramData";
    const string Exe = @"C:\ProgramData\Beckhoff\TcPkg\tcpkg.exe";
    static readonly string[] Roots = { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\TwinCAT" };

    const int FullControl = 0x1F01FF, ReadExecute = 0x1200A9, UsersWrite = 0x116;   // Write = WriteData|AppendData|WriteEA|WriteAttributes
    static readonly TcpkgTrust.Ace System = new("S-1-5-18", @"NT AUTHORITY\SYSTEM", FullControl);
    static readonly TcpkgTrust.Ace Admins = new("S-1-5-32-544", @"BUILTIN\Administrators", FullControl);
    static readonly TcpkgTrust.Ace UsersRead = new("S-1-5-32-545", @"BUILTIN\Users", ReadExecute);

    static List<TcpkgTrust.PathAcl> Chain(params TcpkgTrust.Ace[] folderExtra) => new()
    {
        new(@"C:\ProgramData\Beckhoff", "S-1-5-18", "SYSTEM", new[] { System, Admins, UsersRead }.Concat(folderExtra).ToList()),
        new(@"C:\ProgramData\Beckhoff\TcPkg", "S-1-5-18", "SYSTEM", new[] { System, Admins, UsersRead }.Concat(folderExtra).ToList()),
        new(Exe, "S-1-5-18", "SYSTEM", new[] { System, Admins, UsersRead }.ToList())
    };

    [Fact]
    public void TheProgramFoldersAreTrustedAsBefore() =>
        Assert.Null(TcpkgTrust.Refusal(@"C:\Program Files\Beckhoff\TcPkg\tcpkg.exe", Roots, ProgramData, null, false, "not checked"));

    [Fact]
    public void ProgramDataWithAnAdminOnlyAclAndASignatureIsTrusted() =>
        Assert.Null(TcpkgTrust.Refusal(Exe, Roots, ProgramData, Chain(), true, "valid"));

    [Fact]
    public void ProgramDataWhereUsersMayWriteIsRefused()
    {
        var why = TcpkgTrust.Refusal(Exe, Roots, ProgramData, Chain(new TcpkgTrust.Ace("S-1-5-32-545", @"BUILTIN\Users", UsersWrite)), true, "valid");
        Assert.Contains(@"BUILTIN\Users may write to C:\ProgramData\Beckhoff", why);
    }

    [Fact]
    public void AnUnsignedExeIsRefused() =>
        Assert.Contains("no valid Authenticode signature (not signed)", TcpkgTrust.Refusal(Exe, Roots, ProgramData, Chain(), false, "not signed"));

    [Fact]
    public void AnOwnerOtherThanTheAdministratorsIsRefused()
    {
        var chain = Chain();
        chain[2] = chain[2] with { OwnerSid = "S-1-5-21-1-2-3-1001", OwnerName = @"PC\user" };
        Assert.Contains(@"owned by PC\user", TcpkgTrust.Refusal(Exe, Roots, ProgramData, chain, true, "valid"));
    }

    [Fact]
    public void AnUnreadableAclIsRefused() =>
        Assert.Contains("could not be read", TcpkgTrust.Refusal(Exe, Roots, ProgramData, Chain().Take(2).ToList(), true, "valid"));

    [Theory]
    [InlineData(@"C:\Users\me\tools\tcpkg.exe")]
    [InlineData(@"C:\ProgramData\Other\tcpkg.exe")]
    [InlineData(@"C:\ProgramData\Beckhoff\tcpkg.exe")]
    public void OtherFoldersAreRefused(string exe) =>
        Assert.Contains("is not in the program folders", TcpkgTrust.Refusal(exe, Roots, ProgramData, Chain(), true, "valid"));

    // ---------------------------------------------------------------- opt-in TCAGENT_TCPKG_ALLOW_USER_WRITABLE
    static readonly TcpkgTrust.Ace UsersCreate = new("S-1-5-32-545", @"BUILTIN\Users", UsersWrite);

    [Fact]
    public void WithoutTheOptInUserWritableFoldersAreRefusedAndTheWayOutIsNamed()
    {
        var d = TcpkgTrust.Decide(Exe, Roots, ProgramData, Chain(UsersCreate), true, "valid", allowUserWritable: false);
        Assert.Contains("may write to", d.Refusal);
        Assert.Contains(TcpkgTrust.AllowSwitch, d.Refusal);
        Assert.Contains("icacls", d.Refusal);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void WithTheOptInUserWritableFoldersAreAllowedWithAWarning()
    {
        var d = TcpkgTrust.Decide(Exe, Roots, ProgramData, Chain(UsersCreate), true, "valid", allowUserWritable: true);
        Assert.Null(d.Refusal);
        Assert.Contains(@"BUILTIN\Users on C:\ProgramData\Beckhoff\TcPkg", d.Warning);
        Assert.Contains(TcpkgTrust.AllowSwitch, d.Warning);
    }

    [Fact]
    public void WithTheOptInAWritableExeIsStillRefused()
    {
        var chain = Chain(UsersCreate);
        chain[2] = chain[2] with { Allow = chain[2].Allow.Append(UsersCreate).ToList() };
        Assert.Contains(@"may write to C:\ProgramData\Beckhoff\TcPkg\tcpkg.exe", TcpkgTrust.Decide(Exe, Roots, ProgramData, chain, true, "valid", true).Refusal);
    }

    [Fact]
    public void WithTheOptInAnUnsignedExeIsStillRefused() =>
        Assert.Contains("no valid Authenticode signature", TcpkgTrust.Decide(Exe, Roots, ProgramData, Chain(UsersCreate), false, "not signed", true).Refusal);

    [Fact]
    public void WithTheOptInAForeignOwnerIsStillRefused()
    {
        var chain = Chain(UsersCreate);
        chain[1] = chain[1] with { OwnerSid = "S-1-5-21-1-2-3-1001", OwnerName = @"PC\user" };
        Assert.Contains(@"owned by PC\user", TcpkgTrust.Decide(Exe, Roots, ProgramData, chain, true, "valid", true).Refusal);
    }

    [Fact]
    public void WithTheOptInMoreThanCreatingFilesIsStillRefused()
    {
        // Users with delete child / full control on the folder, or Everyone: not what the opt-in tolerates
        Assert.NotNull(TcpkgTrust.Decide(Exe, Roots, ProgramData, Chain(new TcpkgTrust.Ace("S-1-5-32-545", @"BUILTIN\Users", FullControl)), true, "valid", true).Refusal);
        Assert.NotNull(TcpkgTrust.Decide(Exe, Roots, ProgramData, Chain(new TcpkgTrust.Ace("S-1-1-0", "Everyone", UsersWrite)), true, "valid", true).Refusal);
    }

    [Fact]
    public void TheOptInIsOnlyForExactlyTheTcpkgFolder()
    {
        const string sub = @"C:\ProgramData\Beckhoff\TcPkg\bin\tcpkg.exe";
        var chain = new List<TcpkgTrust.PathAcl>(Chain(UsersCreate).Take(2))
        {
            new(@"C:\ProgramData\Beckhoff\TcPkg\bin", "S-1-5-18", "SYSTEM", new[] { System, Admins, UsersRead, UsersCreate }.ToList()),
            new(sub, "S-1-5-18", "SYSTEM", new[] { System, Admins, UsersRead }.ToList())
        };
        Assert.NotNull(TcpkgTrust.Decide(sub, Roots, ProgramData, chain, true, "valid", true).Refusal);
    }

    [Fact]
    public void TheOptInIsReadFromTheUserSettingsAndTheWindowsEnvironmentOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcagent-tcpkgtrust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "user"));
        Directory.CreateDirectory(Path.Combine(dir, "solution", ".tcagent"));
        var userSettings = Path.Combine(dir, "user", "settings.json");
        var on = "{\"env\": {\"" + TcpkgTrust.AllowSwitch + "\": \"1\"}}";
        var old = Environment.GetEnvironmentVariable(TcpkgTrust.AllowSwitch);
        try
        {
            // a project (its .tcagent folder or an MCP configuration that sets the process environment) cannot switch it on
            File.WriteAllText(Path.Combine(dir, "solution", ".tcagent", "settings.json"), on);
            File.WriteAllText(Path.Combine(dir, "solution", ".tcagent", "config.json"), on);
            Environment.SetEnvironmentVariable(TcpkgTrust.AllowSwitch, "1");
            Assert.False(TcpkgTrust.AllowUserWritable(userSettings, null, null));
            // the user's own settings.json can
            File.WriteAllText(userSettings, on);
            Assert.True(TcpkgTrust.AllowUserWritable(userSettings, null, null));
            // the Windows environment wins over settings.json, the user's variable over the machine's
            Assert.False(TcpkgTrust.AllowUserWritable(userSettings, "0", null));
            Assert.True(TcpkgTrust.AllowUserWritable(Path.Combine(dir, "missing.json"), null, "1"));
            Assert.False(TcpkgTrust.AllowUserWritable(Path.Combine(dir, "missing.json"), "0", "1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TcpkgTrust.AllowSwitch, old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TheChainRunsFromBeckhoffToTheExe() =>
        Assert.Equal(new[] { @"C:\ProgramData\Beckhoff", @"C:\ProgramData\Beckhoff\TcPkg", @"C:\ProgramData\Beckhoff\TcPkg\bin", @"C:\ProgramData\Beckhoff\TcPkg\bin\tcpkg.exe" },
                     TcpkgTrust.ChainToCheck(@"C:\ProgramData\Beckhoff\TcPkg\bin\tcpkg.exe", ProgramData));
}
