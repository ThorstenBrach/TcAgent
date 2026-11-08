// Unit tests: the fault signals of Advisor Live - conditions, the project file, what plugins bring.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class AdvisorWatchTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-watch-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [InlineData("true", "TRUE", true)]
    [InlineData("true", "False", false)]
    [InlineData("false", "0", true)]
    [InlineData("!= 0", "17", true)]
    [InlineData("!= 0", "0", false)]
    [InlineData("== 3", "3", true)]
    [InlineData("> 100", "100.5", true)]
    [InlineData("<= 2", "3", false)]
    [InlineData("& 128", "144", true)]
    [InlineData("& 128", "48", false)]
    [InlineData("!= 0", "not a number", false)]
    public void ConditionsAreEvaluated(string when, string value, bool fires) =>
        Assert.Equal(fires, AdvisorWatch.Evaluate(when, value, TimeSpan.Zero));

    [Fact]
    public void UnchangedFiresOnlyAfterItsTime()
    {
        var s = new AdvisorWatch.Signal("MAIN.nStep", "unchanged 30s", "hangs", new List<string>());
        var st = new AdvisorWatch.State();
        var t0 = new DateTime(2026, 11, 8, 10, 0, 0);
        Assert.False(st.Fires(s, "40", t0));
        Assert.False(st.Fires(s, "40", t0.AddSeconds(29)));
        Assert.True(st.Fires(s, "40", t0.AddSeconds(31)));
        Assert.False(st.Fires(s, "50", t0.AddSeconds(32)));     // it moved on
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("!= x")]
    [InlineData("unchanged soon")]
    public void AWrongConditionIsRefused(string when)
    {
        Assert.NotNull(AdvisorWatch.CheckWhen(when));
        Assert.Throws<ArgumentException>(() => AdvisorWatch.Add(dir, new AdvisorWatch.Signal("MAIN.b", when, "t", new List<string>())));
    }

    [Fact]
    public void SignalsAreKeptInTheProject()
    {
        AdvisorWatch.Add(dir, new AdvisorWatch.Signal("MAIN.fbStation.nFault", "!= 0", "Fault number", new List<string> { "MAIN.fbStation.nStep" }));
        AdvisorWatch.Add(dir, new AdvisorWatch.Signal("MAIN.fbStation.nFault", "!= 0", "Fault number of the station", new List<string>()));   // replaces
        AdvisorWatch.Add(dir, new AdvisorWatch.Signal("MAIN.bError", "true", "Error", new List<string>(), 852));
        var (signals, hints) = AdvisorWatch.Load(dir, null);
        Assert.Equal(2, signals.Count);
        Assert.Equal("Fault number of the station", signals[0].Text);
        Assert.Equal(852, signals[1].Port);
        Assert.Empty(hints);
        Assert.True(AdvisorWatch.Remove(dir, "main.berror"));
        Assert.Single(AdvisorWatch.Load(dir, null).signals);
    }

    [Fact]
    public void APluginBringsItsSkillAndSignals()
    {
        var plugin = Path.Combine(dir, "plugin-a");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "advisor.json"),
            "{ \"skill\": \"platform-a-diagnose\", \"signals\": [ { \"symbol\": \"GVL.nState\", \"when\": \"& 128\", \"text\": \"Error bit\" } ] }");
        var (signals, hints) = AdvisorWatch.Load(dir, new[] { ("platform-a", plugin) });
        Assert.Equal("platform-a-diagnose", Assert.Single(hints).Skill);
        Assert.Equal("plugin platform-a", Assert.Single(signals).Source);
    }
}
