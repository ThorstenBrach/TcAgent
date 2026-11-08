// Unit tests: knowledge follows the modules - tcagent-requires-module and the note for tools of a switched-off module.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class ModuleRequirementsTests
{
    static readonly string[] Known = { "scope", "hmi", "test", "core" };

    [Fact]
    public void AnItemWaitsForItsModule() =>
        Assert.Equal("scope is switched off", PluginRequirements.MissingModules("scope", new[] { "scope" }, Known));

    [Fact]
    public void AnItemWithItsModulesOnTakesEffect() =>
        Assert.Null(PluginRequirements.MissingModules("scope, hmi", new[] { "test" }, Known));

    [Fact]
    public void AnUnknownModuleIsNamed() =>
        Assert.Equal("scopes does not exist", PluginRequirements.MissingModules("scopes", Array.Empty<string>(), Known));

    [Fact]
    public void WithoutTheFieldNothingIsMissing() =>
        Assert.Null(PluginRequirements.MissingModules(null, new[] { "scope" }, Known));

    [Fact]
    public void ToolsOfASwitchedOffModuleGiveANote()
    {
        var note = PluginRequirements.OffModuleNote("Run `tc_static_analysis`, then `tc_check`.", new[] { "test" }, Modules.Of);
        Assert.Equal("uses tools of switched-off modules: test (tc_static_analysis)", note);
    }

    [Fact]
    public void ToolsOfModulesThatAreOnGiveNoNote() =>
        Assert.Null(PluginRequirements.OffModuleNote("Run `tc_check` and `tc_scope`.", new[] { "hmi" }, Modules.Of));

    [Fact]
    public void AWaitingItemIsListed() =>
        Assert.True(PluginRequirements.Waiting(new KnowledgeLayers.Entry { Status = PluginRequirements.ModuleStatus }));
}
