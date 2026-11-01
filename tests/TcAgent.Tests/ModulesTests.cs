// Tests for the modules (Modules.cs) and plugins (Plugins.cs): every tool in a module, modules.json over the levels with
// locks and dependencies, plugin manifests, approval by content, the company's plugin policy and a real plugin process.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace TcAgent.Tests;

// shares the global user root / TCAGENT_COMPANY with the other knowledge tests: not in parallel
[Collection("knowledge-layers")]
public sealed class ModulesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-mod-" + Guid.NewGuid().ToString("N"));
    string Company => Path.Combine(dir, "company");
    string User => Path.Combine(dir, "user");
    string Sln => Path.Combine(dir, "MachineX");
    readonly string oldCompany = Environment.GetEnvironmentVariable("TCAGENT_COMPANY");
    readonly string oldTeam = Environment.GetEnvironmentVariable("TCAGENT_TEAM");

    public ModulesTests()
    {
        Directory.CreateDirectory(Sln);
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", Company);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", null);
        KnowledgeLayers.UserRootOverride = User;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TCAGENT_COMPANY", oldCompany);
        Environment.SetEnvironmentVariable("TCAGENT_TEAM", oldTeam);
        KnowledgeLayers.UserRootOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    void Write(string rel, string text)
    {
        var f = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    static string SrcDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "ToolRegistry.cs"))) return Path.Combine(d.FullName, "src");
        throw new Exception("src folder not found");
    }

    static Modules.State St(List<Modules.State> s, string id) => s.Single(x => x.Id == id);

    [Fact]
    public void EveryRegisteredToolIsInExactlyOneModule()
    {
        var registered = Directory.GetFiles(SrcDir(), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"^\s*Add\(""((?:tc|ads)_\w+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.True(registered.Count > 90, "tools found: " + registered.Count);
        Assert.Equal("", string.Join(", ", registered.Where(t => Modules.Of(t) == null)));             // a new tool needs its module in Modules.cs
        Assert.Equal("", string.Join(", ", Modules.KnownTools.Where(t => !registered.Contains(t))));   // no module lists a tool that is gone
        Assert.All(Modules.All, m => Assert.NotEmpty(Modules.ToolsOf(m.Id)));
    }

    [Fact]
    public void AllOnWithoutFiles()
    {
        var s = Modules.Effective(Sln);
        Assert.All(s, x => Assert.True(x.On));
        Assert.True(St(s, "core").Core);
        Assert.Null(Modules.Refusal("tc_hmi", s));
    }

    [Fact]
    public void LaterLevelWinsCoreStaysAndDependentsFollow()
    {
        Write(@"user\modules.json", "{\"off\": [\"hmi\", \"scope\", \"core\"]}");
        Write(@"MachineX\.tcagent\modules.json", "{\"on\": [\"scope\"], \"off\": [\"runtime\"]}");
        var s = Modules.Effective(Sln);
        Assert.False(St(s, "hmi").On);
        Assert.Equal("user", St(s, "hmi").By);
        Assert.True(St(s, "scope").On);                     // the project switched it on again
        Assert.True(St(s, "core").On);                      // core cannot be switched off
        Assert.False(St(s, "runtime").On);
        Assert.False(St(s, "test").On);                     // needs runtime
        Assert.Contains("needs the module runtime", St(s, "test").Reason);
        Assert.Contains("module 'hmi'", Modules.Refusal("tc_hmi_page", s));
        Assert.Null(Modules.Refusal("tc_plc_read", s));
        Assert.True(Modules.Off(s).SetEquals(new[] { "hmi", "runtime", "test" }));
        // without the solution only the user level counts
        Assert.False(St(Modules.Effective(null), "scope").On);
    }

    [Fact]
    public void CompanyLockHoldsAgainstUserAndProject()
    {
        Write(@"company\modules.json", "{\"off\": [\"motion\"], \"on\": [\"safety\"], \"locked\": [\"motion\", \"safety\", \"core\"]}");
        Write(@"user\modules.json", "{\"on\": [\"motion\"], \"locked\": [\"io\"]}");
        Write(@"MachineX\.tcagent\modules.json", "{\"off\": [\"safety\", \"io\"]}");
        var s = Modules.Effective(Sln);
        Assert.False(St(s, "motion").On);
        Assert.True(St(s, "motion").Locked);
        Assert.Contains("locked by company", St(s, "motion").Reason);
        Assert.True(St(s, "safety").On);
        Assert.False(St(s, "io").On);                       // "locked" counts on company and team only
        Assert.False(St(s, "io").Locked);
        Assert.False(St(s, "core").Locked);
    }

    [Fact]
    public void SetWritesUserAndProjectOnly()
    {
        var f = Modules.Set(Sln, "project", "hmi", false);
        Assert.Equal(Path.Combine(Sln, ".tcagent", "modules.json"), f);
        Modules.Set(Sln, "project", "HMI", true);
        Modules.Set(Sln, "project", "scope", false);
        var o = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Equal("[\"hmi\"]", o["on"]!.ToJsonString());
        Assert.Equal("[\"scope\"]", o["off"]!.ToJsonString());
        Assert.Throws<Exception>(() => Modules.Set(Sln, "company", "hmi", false));
        Assert.Throws<Exception>(() => Modules.Set(Sln, "user", "core", false));
        Assert.Throws<Exception>(() => Modules.Set(Sln, "user", "nonsense", false));
        Assert.Throws<Exception>(() => Modules.Set(null, "project", "hmi", false));
    }

    // ------------------------------------------------------------------ plugins
    void Plugin(string level, string id, string tools = "{\"count\": \"read\"}", string extra = "")
    {
        var root = level switch { "company" => @"company", "user" => @"user", _ => @"MachineX\.tcagent" };
        Write($@"{root}\plugins\{id}\plugin.json", $"{{\"id\": \"{id}\", \"name\": \"{id} plugin\", \"command\": \"tool.exe\", \"tools\": {tools}{extra}}}");
        Write($@"{root}\plugins\{id}\tool.py", "print('x')");
    }

    [Fact]
    public void PluginsFoundWithStatusAndClasses()
    {
        Plugin("user", "alpha", "{\"count\": \"read\", \"fix\": \"write\", \"reset\": \"runtime\"}");
        Plugin("project", "beta");
        Plugin("project", "alpha");                                   // same id: the user level came first
        Write(@"user\plugins\Bad\plugin.json", "{\"id\": \"Bad Id\", \"command\": \"x\"}");
        Write(@"user\plugins\nocmd\plugin.json", "{\"id\": \"nocmd\"}");
        var found = Plugins.Found(Sln);
        Assert.Equal("ready", found.Single(p => p.Id == "alpha" && p.Level == "user").Status);
        Assert.Equal("duplicate", found.Single(p => p.Id == "alpha" && p.Level == "project").Status);
        Assert.Equal("needs approval", found.Single(p => p.Id == "beta").Status);
        Assert.Equal(2, found.Count(p => p.Status == "invalid"));
        Assert.Equal("plugin:alpha", Modules.Of("x_alpha_count"));
        Assert.Equal(ToolClass.Read, Plugins.ClassOf("x_alpha_count"));
        Assert.Equal(ToolClass.ProjectWrite, Plugins.ClassOf("x_alpha_fix"));
        Assert.Equal(ToolClass.RuntimeWrite, Plugins.ClassOf("x_alpha_reset"));
        Assert.Equal(ToolClass.ProjectWrite, Plugins.ClassOf("x_alpha_undeclared"));   // not declared: changing
        Assert.False(Plugins.IsPluginTool("tc_plc_read"));
        // a plugin is a module of its own
        var s = Modules.Effective(Sln, found.Where(p => p.Ready).Select(p => (p.Id, p.Name, p.Description, p.Tools.Count)));
        Assert.Equal(3, St(s, "plugin:alpha").Tools);
        Write(@"MachineX\.tcagent\modules.json", "{\"off\": [\"plugin:alpha\"]}");
        s = Modules.Effective(Sln, found.Where(p => p.Ready).Select(p => (p.Id, p.Name, p.Description, p.Tools.Count)));
        Assert.Contains("module 'plugin:alpha'", Modules.Refusal("x_alpha_count", s));
    }

    [Fact]
    public void ProjectPluginNeedsApprovalOfExactlyThisContent()
    {
        Plugin("project", "beta");
        Assert.Equal("needs approval", Plugins.Found(Sln).Single().Status);
        Assert.Equal(0, Plugins.RunCli(new[] { "plugins", "approve", "beta", "--solution", MakeSln() }));
        Assert.Equal("ready", Plugins.Found(Sln).Single().Status);
        Write(@"MachineX\.tcagent\plugins\beta\tool.py", "print('changed')");              // changed code: approve again
        Assert.Equal("needs approval", Plugins.Found(Sln).Single().Status);
        Assert.Equal(0, Plugins.RunCli(new[] { "plugins", "approve", "beta", "--solution", MakeSln() }));
        Assert.Equal(0, Plugins.RunCli(new[] { "plugins", "revoke", "beta", "--solution", MakeSln() }));
        Assert.Equal("needs approval", Plugins.Found(Sln).Single().Status);
        Assert.True(File.Exists(Path.Combine(User, "plugins-approved.json")));          // in the (test) user root, not the real one
    }

    string MakeSln()
    {
        var f = Path.Combine(Sln, "MachineX.sln");
        if (!File.Exists(f)) File.WriteAllText(f, "");
        return f;
    }

    [Fact]
    public void CompanyDecidesAboutPlugins()
    {
        Plugin("user", "alpha");
        Plugin("user", "gamma");
        Write(@"company\modules.json", "{\"plugins\": \"none\"}");
        Assert.All(Plugins.Found(null), p => Assert.Equal("not allowed", p.Status));
        var gammaHash = Plugins.HashOf(Path.Combine(User, "plugins", "gamma"));
        Write(@"company\modules.json", $"{{\"plugins\": \"listed\", \"allowedPlugins\": [\"{gammaHash}\"]}}");
        var found = Plugins.Found(null);
        Assert.Equal("not allowed", found.Single(p => p.Id == "alpha").Status);
        Assert.Equal("ready", found.Single(p => p.Id == "gamma").Status);
        // an approval does not override the company
        Assert.Equal(Cli.Usage, Plugins.RunCli(new[] { "plugins", "approve", "alpha" }));
    }

    [Fact]
    public void HashCoversNamesAndContents()
    {
        Plugin("user", "alpha");
        var p = Path.Combine(User, "plugins", "alpha");
        var h1 = Plugins.HashOf(p);
        Assert.Equal(h1, Plugins.HashOf(p));
        File.Move(Path.Combine(p, "tool.py"), Path.Combine(p, "other.py"));
        var h2 = Plugins.HashOf(p);
        Assert.NotEqual(h1, h2);
        Directory.CreateDirectory(Path.Combine(p, ".git"));
        File.WriteAllText(Path.Combine(p, ".git", "HEAD"), "x");
        Assert.Equal(h2, Plugins.HashOf(p));                 // .git does not count
    }

    [Fact]
    public void ExamplePluginRunsAndCallsTcAgentThroughTheHost()
    {
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(ps)) return;
        var example = Path.Combine(Path.GetDirectoryName(SrcDir())!, "docs", "plugins", "todo-finder");
        var target = Path.Combine(User, "plugins", "todo-finder");
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(example)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        var p = Plugins.Found(null).Single();
        Assert.Equal("ready", p.Status);
        var calls = new List<string>();
        using var manager = new Plugins.Manager((name, args) =>
        {
            lock (calls) calls.Add(name + " " + args.ToJsonString());
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "MAIN (impl) line 3: // TODO check limits" }), ["isError"] = false };
        });
        var hosts = manager.Sync(null, Plugins.Found(null), new HashSet<string>());
        var host = Assert.Single(hosts);
        Assert.Equal("x_todo_finder_find_todos", Assert.Single(host.Tools).Name);
        var text = host.Call("x_todo_finder_find_todos", new JsonObject { ["path"] = "TIPC^Plc" });
        Assert.Contains("TODO check limits", text);
        var call = Assert.Single(calls);
        Assert.StartsWith("tc_plc_search ", call);
        Assert.Contains("TIPC^Plc", call);
        // switched off as a module: the host is stopped
        Assert.Empty(manager.Sync(null, Plugins.Found(null), new HashSet<string> { "plugin:todo-finder" }));
    }
}
