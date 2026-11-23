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

    [Fact]
    public void TheChainRunsFromBeckhoffToTheExe() =>
        Assert.Equal(new[] { @"C:\ProgramData\Beckhoff", @"C:\ProgramData\Beckhoff\TcPkg", @"C:\ProgramData\Beckhoff\TcPkg\bin", @"C:\ProgramData\Beckhoff\TcPkg\bin\tcpkg.exe" },
                     TcpkgTrust.ChainToCheck(@"C:\ProgramData\Beckhoff\TcPkg\bin\tcpkg.exe", ProgramData));
}
