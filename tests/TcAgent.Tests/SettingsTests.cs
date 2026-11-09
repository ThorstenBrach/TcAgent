// Target systems released in the settings window (Targets.cs) and the settings schema (SettingsSchema.cs).
//
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using TcAgent;
using Xunit;

[Collection("targets")]
public class TargetsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-targets-" + Guid.NewGuid().ToString("N"));

    public TargetsTests() { Directory.CreateDirectory(dir); Targets.FileOverride = Path.Combine(dir, "targets.json"); }
    public void Dispose() { Targets.FileOverride = null; try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Released_target_may_be_written_until_the_time_is_over()
    {
        Targets.Set("5.80.123.4.1.1", "Test rig", true, DateTime.Now.AddHours(1), "test");
        Assert.True(Targets.WriteAllowed("5.80.123.4.1.1"));
        Assert.False(Targets.WriteAllowed("5.80.123.4.1.1", DateTime.Now.AddHours(2)));     // ran out: read only again
        Assert.False(Targets.WriteAllowed("5.80.123.4.1.2"));
    }

    [Fact]
    public void Read_only_entry_never_allows_writing()
    {
        Targets.Set("5.80.200.9.1.1", "Plant A", false, null, "test");
        Assert.False(Targets.WriteAllowed("5.80.200.9.1.1"));
        Assert.Single(Targets.Load());
    }

    [Fact]
    public void Same_net_id_replaces_and_remove_takes_it_away()
    {
        Targets.Set("5.1.2.3.1.1", "a", false, null, "test");
        Targets.Set("5.1.2.3.1.1", "b", true, null, "test");
        var all = Targets.Load();
        Assert.Single(all);
        Assert.Equal("b", all[0].Name);
        Assert.True(Targets.Remove("5.1.2.3.1.1"));
        Assert.Empty(Targets.Load());
        Assert.False(Targets.Remove("5.1.2.3.1.1"));
    }

    [Theory]
    [InlineData("5.80.123.4.1.1", true)]
    [InlineData("5.80.123.4.1", false)]
    [InlineData("5.80.123.4.1.256", false)]
    [InlineData("local", false)]
    public void Net_ids_are_checked(string id, bool ok) => Assert.Equal(ok, Targets.IsNetId(id));

    [Fact]
    public void Invalid_net_id_and_past_time_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Targets.Set("1.2.3", "x", true, null, "test"));
        Assert.Throws<ArgumentException>(() => Targets.Set("1.2.3.4.1.1", "x", true, DateTime.Now.AddMinutes(-1), "test"));
    }

    [Fact]
    public void Broken_file_gives_no_rights()
    {
        File.WriteAllText(Targets.FilePath, "{ not json");
        Assert.Empty(Targets.Load());
        Assert.False(Targets.WriteAllowed("5.80.123.4.1.1"));
    }

    [Fact]
    public void Until_is_parsed()
    {
        var now = new DateTime(2026, 11, 9, 17, 0, 0);
        Assert.Equal(now.AddHours(1), Targets.ParseUntil("1h", now));
        Assert.Equal(now.AddMinutes(30), Targets.ParseUntil("30m", now));
        Assert.Equal(new DateTime(2026, 11, 9, 23, 59, 59), Targets.ParseUntil("today", now));
        Assert.Null(Targets.ParseUntil("forever", now));
        Assert.Null(Targets.ParseUntil("", now));
        Assert.Throws<ArgumentException>(() => Targets.ParseUntil("soon", now));
    }

    [Fact]
    public void Routes_are_read_from_static_routes()
    {
        var f = Path.Combine(dir, "StaticRoutes.xml");
        File.WriteAllText(f, "<?xml version=\"1.0\"?><TcConfig><RemoteConnections>" +
            "<Route><Name>TestRig</Name><Address>test-rig</Address><NetId>5.80.123.4.1.1</NetId><Type>TCP_IP</Type></Route>" +
            "<Route><Name>Broken</Name><Address>x</Address><NetId>not-an-id</NetId></Route>" +
            "</RemoteConnections></TcConfig>");
        var routes = Targets.Routes(new[] { f, Path.Combine(dir, "missing.xml") });
        Assert.Single(routes);
        Assert.Equal(("TestRig", "5.80.123.4.1.1", "test-rig", ""), routes[0]);
    }

    [Fact]
    public void User_mode_runtime_is_found_with_its_net_id()
    {
        var rt = Path.Combine(dir, "Runtimes", "UmRT_Default", "3.1");
        Directory.CreateDirectory(rt);
        File.WriteAllText(Path.Combine(rt, "TcRegistry.xml"),
            "<TcRegistry><Key Name=\"System\"><Value Name=\"AmsNetId\" Type=\"BIN\">C7042AFA0101</Value></Key></TcRegistry>");
        var found = Targets.LocalUserModeRuntimes(Path.Combine(dir, "Runtimes"));
        Assert.Single(found);
        Assert.Equal(("UmRT_Default", "199.4.42.250.1.1"), found[0]);
    }
}

public class SettingsSchemaTests
{
    static string Repo()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "TcAgent.csproj"))) d = Path.GetDirectoryName(d);
        Assert.NotNull(d);
        return d!;
    }

    [Fact]
    public void Every_variable_the_code_reads_is_in_the_schema()
    {
        var read = Directory.GetFiles(Path.Combine(Repo(), "src"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"GetEnvironmentVariable\(""(TCAGENT_[A-Z0-9_]+)""").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        var missing = read.Where(k => SettingsSchema.Find(k) == null && !SettingsSchema.Internal.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "not in SettingsSchema.All: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_setting_is_in_the_user_guide()
    {
        var guide = File.ReadAllText(Path.Combine(Repo(), "docs", "USER_GUIDE.md"));
        var missing = SettingsSchema.All.Where(s => !guide.Contains("| `" + s.Key + "`")).Select(s => s.Key).ToList();
        Assert.True(missing.Count == 0, "not in the table of docs/USER_GUIDE.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_setting_has_a_page_and_texts_in_both_languages()
    {
        foreach (var s in SettingsSchema.All)
        {
            Assert.Contains(SettingsSchema.Pages, p => p.Id == s.Page);
            Assert.False(string.IsNullOrWhiteSpace(s.Label + s.LabelDe + s.Description + s.DescriptionDe) || s.LabelDe.Length == 0 || s.DescriptionDe.Length == 0, s.Key);
            if (s.ValueLabels != null) Assert.Equal(s.Values.Length, s.ValueLabels.Length);
        }
    }

    [Fact]
    public void Values_are_checked()
    {
        Assert.Null(SettingsSchema.Check(SettingsSchema.Find("TCAGENT_MODE")!, "propose"));
        Assert.NotNull(SettingsSchema.Check(SettingsSchema.Find("TCAGENT_MODE")!, "everything"));
        Assert.Null(SettingsSchema.Check(SettingsSchema.Find("TCAGENT_JOB_WAIT")!, "90"));
        Assert.NotNull(SettingsSchema.Check(SettingsSchema.Find("TCAGENT_JOB_WAIT")!, "soon"));
        Assert.Null(SettingsSchema.Check(SettingsSchema.Find("TCAGENT_CONFIRM")!, ""));      // empty = default
    }

    [Fact]
    public void Show_tells_where_a_value_comes_from()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcagent-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sf = Path.Combine(dir, "settings.json");
            File.WriteAllText(sf, "{\"env\":{\"TCAGENT_CONFIRM\":\"all\"}}");
            var mcp = Path.Combine(dir, "mcp-test.json");
            File.WriteAllText(mcp, "{\"mcpServers\":{\"tcagent\":{\"env\":{\"TCAGENT_JOB_WAIT\":\"90\"}}}}");
            var show = SettingsSchema.Show(sf, new[] { mcp })["settings"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToList();
            var confirm = show.First(x => x["key"]!.ToString() == "TCAGENT_CONFIRM");
            Assert.Equal("saved", confirm["from"]!.ToString());
            Assert.Equal("all", confirm["effective"]!.ToString());
            var wait = show.First(x => x["key"]!.ToString() == "TCAGENT_JOB_WAIT");
            Assert.Equal("outside", wait["from"]!.ToString());
            Assert.Equal("90", wait["effective"]!.ToString());
            Assert.Equal("default", show.First(x => x["key"]!.ToString() == "TCAGENT_SPEC_GATE")["from"]!.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
