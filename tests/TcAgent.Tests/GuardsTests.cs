// Tests for the checks of values that reach processes, the HTML report or a confirmation decision (Guards.cs).
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

public sealed class GuardsTests
{
    [Theory]
    [InlineData("x&calc")]
    [InlineData("x|calc")]
    [InlineData("a\"b")]
    [InlineData("feed%PATH%")]
    [InlineData("x>out.txt")]
    [InlineData("x^&calc")]
    [InlineData("x\ncalc")]
    public void FeedNamesWithShellCharactersAreRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => Guards.FeedNameOf(name));
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "source", "remove", name, "-y" }));
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "install", "TF6310.TCP-IP.XAE", "-n", name, "-y" }));
    }

    [Theory]
    [InlineData("Beckhoff Stable Feed")]
    [InlineData("MyFeed")]
    [InlineData("my-feed_2.0")]
    public void FeedNamesAreAccepted(string name) => Assert.Equal(name, Guards.FeedNameOf(name));

    [Fact]
    public void PackageSpecsAllowIdAndVersionOnly()
    {
        Assert.Equal("TwinCAT.Standard.XAE=4026.22.0", Guards.PackageSpec("TwinCAT.Standard.XAE=4026.22.0"));
        Assert.Equal("TE1300.ScopeViewProfessional.XAE", Guards.PackageSpec("TE1300.ScopeViewProfessional.XAE"));
        Assert.Throws<ArgumentException>(() => Guards.PackageSpec("a=1=2"));
        Assert.Throws<ArgumentException>(() => Guards.PackageSpec("pkg&calc"));
        Assert.Throws<ArgumentException>(() => Guards.PackageSpec("pkg=1.0&calc"));
        Assert.Throws<ArgumentException>(() => Guards.PackageSpec(""));
    }

    [Fact]
    public void FeedSourcesAreUrlsOrFolders()
    {
        Assert.Equal("https://public.tcpkg.beckhoff-cloud.com/api/v2/index.json", Guards.FeedSource("https://public.tcpkg.beckhoff-cloud.com/api/v2/index.json"));
        Assert.Equal(@"C:\Feeds\Local", Guards.FeedSource(@"C:\Feeds\Local"));
        Assert.Equal(@"\\server\share\feed", Guards.FeedSource(@"\\server\share\feed"));
        Assert.Throws<ArgumentException>(() => Guards.FeedSource("https://x/a&calc"));
        Assert.Throws<ArgumentException>(() => Guards.FeedSource("file:///C:/x"));
        Assert.Throws<ArgumentException>(() => Guards.FeedSource(@"relative\folder"));
        Assert.Throws<ArgumentException>(() => Guards.FeedSource("C:\\x\" & calc"));
    }

    [Fact]
    public void TcpkgCommandLinesOfTheToolsPass()
    {
        Guards.TcpkgArgs(new[] { "list", "TE1300.ScopeViewProfessional.XAE", "--exact", "--all-versions", "-n", "Beckhoff Stable Feed", "--pre" });
        Guards.TcpkgArgs(new[] { "upgrade", "all", "--allow-downgrade", "-y" });
        Guards.TcpkgArgs(new[] { "source", "add", "-n", "Local", "-s", @"C:\Feeds", "--priority", "2", "-y" });
        Guards.TcpkgArgs(new[] { "source", "edit", "Local", "--new-name", "Local2", "--enabled", "false", "-y" });
        Guards.TcpkgArgs(new[] { "config", "list" });
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "install", "x", "--source-override", "y" }));
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "cmd", "/c", "calc" }));
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "source", "add", "-n" }));
        Assert.Throws<ArgumentException>(() => Guards.TcpkgArgs(new[] { "source", "add", "-n", "x", "--priority", "1;calc" }));
    }

    [Fact]
    public void QuotingFollowsTheWindowsRules()
    {
        Assert.Equal("list", Guards.QuoteArg("list"));
        Assert.Equal("\"Beckhoff Stable Feed\"", Guards.QuoteArg("Beckhoff Stable Feed"));
        // a trailing backslash must not escape the closing quote
        Assert.Equal("\"C:\\My Feeds\\\\\"", Guards.QuoteArg("C:\\My Feeds\\"));
        Assert.Throws<ArgumentException>(() => Guards.QuoteArg("a\"b"));
    }

    [Fact]
    public void NobodyToAskIsARefusal()
    {
        Assert.Null(Guards.ConfirmRefusal(true, false, "Activate"));
        Assert.StartsWith("Rejected by the user", Guards.ConfirmRefusal(false, false, "Activate"));
        Assert.StartsWith("NOT executed: the confirmation could not be shown", Guards.ConfirmRefusal(false, true, "Activate"));
        var none = Guards.ConfirmRefusal(null, false, "Write variable MAIN.x := 1");
        Assert.StartsWith("NOT executed", none);
        Assert.Contains("TCAGENT_CONFIRM=off", none);
        Assert.Contains("MAIN.x", none);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("vbscript:msgbox")]
    [InlineData("/relative")]
    public void OnlyHttpLinksAreClickable(string url) => Assert.Null(Guards.SafeHref(url));

    [Fact]
    public void HttpLinksStayLinks()
    {
        Assert.Equal("https://github.com/x/y#L5", Guards.SafeHref("https://github.com/x/y#L5"));
        Assert.Equal("http://example.com/", Guards.SafeHref("http://example.com"));
    }

    [Fact]
    public void ReportApprovalDependsOnSolutionAndCommand()
    {
        var a = Guards.ReportApprovalId(@"C:\P\A.sln", "python report.py {junit}");
        Assert.Equal(a, Guards.ReportApprovalId(@"c:\p\a.sln", "python report.py {junit}  "));
        Assert.NotEqual(a, Guards.ReportApprovalId(@"C:\P\A.sln", "python report.py {junit} & calc"));
        Assert.NotEqual(a, Guards.ReportApprovalId(@"C:\P\B.sln", "python report.py {junit}"));
        Assert.Matches("^[0-9a-f]{16}$", a);
    }
}
