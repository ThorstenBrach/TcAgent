// Tests for the runtime data backup (RuntimeBackup.cs): RETAIN/PERSISTENT declarations in the code, patterns, the
// conversion of values to JSON and back, the backup file. The ADS part needs a PLC and is not tested here.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public sealed class RuntimeBackupTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-rb-" + Guid.NewGuid().ToString("N"));

    public RuntimeBackupTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void FindsRetainAndPersistentInGvlProgramAndFunctionBlock()
    {
        var d = new RuntimeBackup.Declared();
        RuntimeBackup.Scan("{attribute 'qualified_only'}\nVAR_GLOBAL PERSISTENT\n  nSpeed, nAccel : INT; // comment\n  stRecipe : ST_Recipe;\nEND_VAR\n" +
                           "VAR_GLOBAL\n  bRun : BOOL;\nEND_VAR\n", "GVL_Machine", d);
        RuntimeBackup.Scan("PROGRAM MAIN\nVAR RETAIN\n  nCycles : UDINT;\nEND_VAR\nVAR\n  nTemp : INT;\nEND_VAR\n", null, d);
        RuntimeBackup.Scan("FUNCTION_BLOCK PUBLIC FB_Axis EXTENDS FB_Base\nVAR_INPUT\n  bEnable : BOOL;\nEND_VAR\nVAR RETAIN\n  fOffset : LREAL; (* homing offset *)\nEND_VAR\n", null, d);
        Assert.Equal(new[] { "GVL_Machine.nAccel", "GVL_Machine.nSpeed", "GVL_Machine.stRecipe", "MAIN.nCycles" }, d.Paths.OrderBy(x => x));
        Assert.Equal(new[] { "fOffset" }, d.FbVars["FB_Axis"]);
        Assert.Equal(5, d.Count);
    }

    [Fact]
    public void ScansTheFilesOfASolution()
    {
        File.WriteAllText(Path.Combine(dir, "GVL_Recipe.TcGVL"),
            "<TcPlcObject><GVL Name=\"GVL_Recipe\" Id=\"{1}\"><Declaration><![CDATA[VAR_GLOBAL PERSISTENT\n  aRecipes : ARRAY[1..10] OF ST_Recipe;\nEND_VAR\n]]></Declaration></GVL></TcPlcObject>");
        Assert.Contains("GVL_Recipe.aRecipes", RuntimeBackup.ScanSolution(dir).Paths);
    }

    [Theory]
    [InlineData("GVL_Recipe.aRecipes[1].fSpeed", "GVL_Recipe", true)]
    [InlineData("GVL_Recipe.aRecipes[1].fSpeed", "GVL_Recipe.aRecipes", true)]
    [InlineData("GVL_RecipeOld.x", "GVL_Recipe", false)]
    [InlineData("MAIN.fbAxis1.stConfig", "*.stConfig", true)]
    [InlineData("MAIN.fbAxis1.stConfig.fMax", "*.stConfig", false)]
    [InlineData("MAIN.fbAxis1.stConfig", "main.FBAXIS?.stconfig", true)]
    public void Patterns(string path, string pattern, bool match) => Assert.Equal(match, RuntimeBackup.Matches(path, pattern));

    [Fact]
    public void ValuesRoundTrip()
    {
        Assert.Equal((short)-5, RuntimeBackup.FromJson(RuntimeBackup.ToJson((short)-5), (short)0));
        Assert.Equal(1.5f, RuntimeBackup.FromJson(RuntimeBackup.ToJson(1.5f), 0f));
        Assert.Equal(0.1, RuntimeBackup.FromJson(RuntimeBackup.ToJson(0.1), 0.0));
        Assert.Equal(true, RuntimeBackup.FromJson(RuntimeBackup.ToJson(true), false));
        Assert.Equal("Recipe A", RuntimeBackup.FromJson(RuntimeBackup.ToJson("Recipe A"), ""));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), RuntimeBackup.FromJson(RuntimeBackup.ToJson(TimeSpan.FromMilliseconds(1500)), TimeSpan.Zero));
        Assert.Equal(new[] { 1, 2, 3 }, (int[])RuntimeBackup.FromJson(RuntimeBackup.ToJson(new[] { 1, 2, 3 }), new int[3]));
        Assert.Throws<FormatException>(() => RuntimeBackup.FromJson(RuntimeBackup.ToJson(new[] { 1, 2 }), new int[3]));   // the array size changed
        Assert.True(RuntimeBackup.SameValue(new[] { 1, 2 }, new[] { 1, 2 }));
        Assert.False(RuntimeBackup.SameValue(1.0, 1.5));
    }

    [Fact]
    public void FileAndList()
    {
        var f = Path.Combine(dir, "backups", RuntimeBackup.FileName("5.1.2.3.1.1", 851));
        RuntimeBackup.Save(f, new JsonObject { ["target"] = "5.1.2.3.1.1", ["created"] = "2026-10-27T20:00:00",
                                               ["values"] = new JsonObject { ["GVL.n"] = new JsonObject { ["type"] = "INT", ["value"] = 3 } } });
        var l = Assert.Single(RuntimeBackup.List(Path.Combine(dir, "backups")));
        Assert.Equal(("5.1.2.3.1.1", 1), (l.Target, l.Values));
        Assert.Equal(3, RuntimeBackup.Load(f)["values"]!["GVL.n"]!["value"]!.GetValue<decimal>());
        File.WriteAllText(Path.Combine(dir, "other.json"), "{\"a\":1}");
        Assert.Throws<InvalidOperationException>(() => RuntimeBackup.Load(Path.Combine(dir, "other.json")));
    }

    [Fact]
    public void ConfigOfTheSolution()
    {
        Directory.CreateDirectory(Path.Combine(dir, ".tcagent"));
        File.WriteAllText(Path.Combine(dir, ".tcagent", "backup.json"), "{\"include\": [\"GVL_Recipe\"], \"exclude\": [\"*.aTrace\"]}");
        var (inc, exc) = RuntimeBackup.Config(dir);
        Assert.Equal(new[] { "GVL_Recipe" }, inc);
        Assert.Equal(new[] { "*.aTrace" }, exc);
    }
}
