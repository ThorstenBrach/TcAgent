using TcAgent;
using Xunit;

public class StFormatExamplesTests
{
    [Fact]
    public void EveryExampleShowsADifference()
    {
        var same = StFormatExamples.All
            .Where(e => e.Values.Select(v => StFormatExamples.Run(e, v).Replace("\r\n", "\n")).Distinct().Count() < 2)
            .Select(e => e.Rule).ToList();
        Assert.Empty(same);
        Assert.DoesNotContain(StFormatExamples.All, e => e.Values.Any(v => StFormatExamples.Run(e, v).StartsWith("(not formatted")));
    }

    [Fact]
    public void EveryRuleThatChangesCodeHasAnExample()
    {
        var without = StFormatConfig.Rules.Select(r => r.Key)
            .Except(StFormatExamples.All.Select(e => e.Rule))
            .Except(new[] { "indent.tab-width", "indent.from-ide", "eol.trim-trailing", "eol.style" }).ToList();
        Assert.Empty(without);
    }

    [Fact]
    public void TheExamplesInTheDocsAreCurrent()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docs", "FORMATTER.md"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var file = Path.Combine(dir!, "docs", "FORMATTER_EXAMPLES.md");
        var expected = StFormatExamples.Markdown().Replace("\r\n", "\n");
        var actual = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : "";
        Assert.True(expected == actual, "docs/FORMATTER_EXAMPLES.md is out of date - TcAgent.exe format --examples --markdown > docs/FORMATTER_EXAMPLES.md");
    }
}
