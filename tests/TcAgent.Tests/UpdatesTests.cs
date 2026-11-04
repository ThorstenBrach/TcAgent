// New versions (Updates.cs): which releases count, the switches and their locks, the checksum list. No network here.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

public class UpdatesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-updates-" + Guid.NewGuid().ToString("N"));

    public UpdatesTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static JsonNode Releases(params (string Tag, bool Draft, bool Pre)[] list) =>
        new JsonArray(list.Select(r => (JsonNode)new JsonObject
        {
            ["tag_name"] = r.Tag, ["draft"] = r.Draft, ["prerelease"] = r.Pre, ["body"] = "notes " + r.Tag, ["html_url"] = "u", ["published_at"] = "2026-11-02T22:17:28Z"
        }).ToArray());

    [Fact]
    public void Newer_releases_newest_first_compared_as_numbers()
    {
        var r = Updates.Newer(Releases(("v0.9.0", false, false), ("v0.10.0", false, false), ("v0.8.0", false, false)), "0.8.0");
        Assert.Equal(new[] { "0.10.0", "0.9.0" }, r.Select(x => x.Version));
        Assert.Equal("notes v0.10.0", r[0].Notes);
    }

    [Fact]
    public void Drafts_prereleases_and_odd_tags_do_not_count() =>
        Assert.Empty(Updates.Newer(Releases(("v2.0.0", true, false), ("v2.1.0", false, true), ("v3.0-rc1", false, false), ("latest", false, false)), "1.0.0"));

    [Fact]
    public void A_preview_newer_than_the_latest_release_is_up_to_date() =>
        Assert.Empty(Updates.Newer(Releases(("v0.51.0", false, false)), "0.52.0"));

    [Fact]
    public void No_answer_yet_means_nothing_new() => Assert.Empty(Updates.Newer(null, "1.0.0"));

    [Fact]
    public void Skipped_version_is_available_but_gives_no_notice()
    {
        var st = new JsonObject { ["releases"] = Releases(("v1.1.0", false, false)), ["skipped"] = "1.1.0" };
        var r = Updates.Describe(st, new Updates.Switch(true, null), "1.0.0", null);
        Assert.True(r["available"]!.GetValue<bool>());
        Assert.False(r["notify"]!.GetValue<bool>());
        st["releases"] = Releases(("v1.2.0", false, false), ("v1.1.0", false, false));
        Assert.True(Updates.Describe(st, new Updates.Switch(true, null), "1.0.0", null)["notify"]!.GetValue<bool>());
    }

    [Fact]
    public void Switches_are_on_by_default_and_the_user_can_switch_them_off()
    {
        var none = Array.Empty<(string, string)>();
        Assert.True(Updates.Setting("check", new JsonObject(), none).On);
        Assert.False(Updates.Setting("check", new JsonObject { ["check"] = false }, none).On);
        Assert.Null(Updates.Setting("check", new JsonObject { ["check"] = false }, none).LockedBy);
    }

    [Fact]
    public void A_level_with_a_value_wins_and_locks()
    {
        File.WriteAllText(Path.Combine(dir, "updates.json"), """{ "check": false }""");
        var levels = new[] { ("company", dir) };
        var s = Updates.Setting("check", new JsonObject { ["check"] = true }, levels);
        Assert.False(s.On);
        Assert.Equal("company", s.LockedBy);
        Assert.Null(Updates.Setting("sources", new JsonObject(), levels).LockedBy);     // not mentioned there: stays the user's
    }

    [Fact]
    public void A_broken_updates_json_locks_nothing()
    {
        File.WriteAllText(Path.Combine(dir, "updates.json"), "{ nonsense");
        Assert.True(Updates.Setting("check", new JsonObject(), new[] { ("team", dir) }).On);
    }

    [Fact]
    public void Due_once_a_day()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Updates.Due(null, now));
        Assert.True(Updates.Due("nonsense", now));
        Assert.False(Updates.Due(now.AddHours(-3).ToString("o"), now));
        Assert.True(Updates.Due(now.AddHours(-21).ToString("o"), now));
    }

    [Fact]
    public void Checksum_list()
    {
        var h = new string('A', 64);
        var sums = $"{new string('b', 64)}  TcAgent-XaeShell-1.0.0.zip\r\n{h} *TcAgent-1.0.0.vsix\r\n";
        Assert.Equal(new string('a', 64), Updates.ExpectedHash(sums, "TcAgent-1.0.0.vsix"));
        Assert.Null(Updates.ExpectedHash(sums, "other.vsix"));
        Assert.Null(Updates.ExpectedHash("short  TcAgent-1.0.0.vsix", "TcAgent-1.0.0.vsix"));
    }

    [Fact]
    public void Sha256_of_a_file()
    {
        var f = Path.Combine(dir, "x.txt");
        File.WriteAllText(f, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updates.Sha256(f));
    }
}
