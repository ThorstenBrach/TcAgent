// Unit tests: what is new comes from CHANGELOG.md - every version between the installed and the newest one.
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using TcAgent;
using Xunit;

public class UpdatesChangelogTests
{
    const string Changelog = "# Changelog\n\nIntro.\n\n## [Unreleased]\n\n- not yet\n\n## [0.53.0] – 2026-11-20\n\n### Added\n- **Advisor**\n\n" +
                             "## [0.52.1] – 2026-11-08\n\n### Fixed\n- tc_test_results\n\n## [0.52.0] – 2026-11-04\n\n- older\n";

    [Fact]
    public void EachVersionHasItsSection()
    {
        var s = Updates.ChangelogSections(Changelog);
        Assert.Equal(new[] { "0.53.0", "0.52.1", "0.52.0" }, s.Keys);
        Assert.StartsWith("### Added", s["0.53.0"]);
        Assert.DoesNotContain("0.52.1", s["0.53.0"]);
    }

    [Fact]
    public void NewerReleasesShowTheirChangelog()
    {
        var releases = JsonNode.Parse("[{\"tag_name\":\"v0.53.0\",\"body\":\"short\"},{\"tag_name\":\"v0.52.1\",\"body\":\"short\"},{\"tag_name\":\"v0.52.0\",\"body\":\"short\"}]");
        var newer = Updates.WithChangelog(Updates.Newer(releases, "0.52.0"), Changelog);
        Assert.Equal(new[] { "0.53.0", "0.52.1" }, newer.Select(r => r.Version));
        Assert.Contains("**Advisor**", newer[0].Notes);
        Assert.Contains("tc_test_results", newer[1].Notes);
    }

    [Fact]
    public void AVersionNeverReleasedOnItsOwnIsShownToo()
    {
        var releases = JsonNode.Parse("[{\"tag_name\":\"v0.53.0\",\"body\":\"short\"}]");
        var newer = Updates.WithChangelog(Updates.Newer(releases, "0.52.0"), Changelog, "0.52.0");
        Assert.Equal(new[] { "0.53.0", "0.52.1" }, newer.Select(r => r.Version));
    }

    [Fact]
    public void WithoutAChangelogTheReleaseTextStays()
    {
        var releases = JsonNode.Parse("[{\"tag_name\":\"v0.53.0\",\"body\":\"short\"}]");
        Assert.Equal("short", Updates.WithChangelog(Updates.Newer(releases, "0.52.0"), null).Single().Notes);
    }
}
