// Unit tests: the /// text of a test as HTML in the TcUnit report (TcUnitDoc.cs) - "| a | b |" tables.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class TcUnitDocTests
{
    const string Steps = """
        Path:
        | Step | Start      | Target     | Move mode       |
        |    1 | H1         | H2         | JOINTS_BLENDED  |
        |    2 | H2         | N12 Offset | PTP_BLENDED     |
        """;

    [Fact]
    public void TableLinesBecomeOneTableWithHead()
    {
        var h = TcUnitDoc.Html(Steps.Replace("\r", ""));
        Assert.Equal(1, Count(h, "<table class='steps'>"));
        Assert.Contains("<h4>Path</h4><table class='steps'><tr><th class='n'>Step</th><th>Start</th><th>Target</th><th>Move mode</th></tr>", h);
        Assert.Contains("<tr><td class='n'>1</td><td>H1</td><td>H2</td><td>JOINTS_BLENDED</td></tr>", h);
        Assert.Contains("<td>N12 Offset</td>", h);
        Assert.Equal(3, Count(h, "<tr>"));
        Assert.DoesNotContain("<div>|", h);
    }

    [Fact]
    public void SeparatorLineIsLeftOut()
    {
        var h = TcUnitDoc.Html("| Step | Target |\n|---:|:----|\n| 1 | H2 |\n| 2 | N12 |");
        Assert.Equal(3, Count(h, "<tr>"));
        Assert.DoesNotContain("---", h);
        Assert.Contains("<th class='n'>Step</th><th>Target</th>", h);
    }

    [Fact]
    public void ListAfterTheTableEndsTheTable()
    {
        var h = TcUnitDoc.Html("| A | B |\n| x | y |\n- first\n- second");
        Assert.Contains("</table><ul><li>first</li><li>second</li></ul>", h);
        // a numbered cell is a table row, not a list
        var h2 = TcUnitDoc.Html("| 1 | start |\n| 2 | stop |");
        Assert.DoesNotContain("<ol>", h2);
        Assert.Equal(2, Count(h2, "<tr>"));
    }

    [Fact]
    public void PipeInTheMiddleOfTextIsNoTable()
    {
        var h = TcUnitDoc.Html("Either a | b or c");
        Assert.DoesNotContain("<table", h);
        Assert.Contains("<div>Either a | b or c</div>", h);
    }

    [Fact]
    public void CellsAreEncodedAndLinksStayClickable()
    {
        var h = TcUnitDoc.Html("| Value | Link |\n| <b>&x</b> | https://example.com/a |");
        Assert.Contains("<td>&lt;b&gt;&amp;x&lt;/b&gt;</td>", h);
        Assert.DoesNotContain("<b>&x", h);
        Assert.Contains("<a href=\"https://example.com/a\"", h);
    }

    [Fact]
    public void TextAroundTheTableKeepsItsLayout()
    {
        var h = TcUnitDoc.Html("Setup: robot at H1\n| Step | Target |\n| 1 | H2 |\n\nExpected: no error");
        Assert.StartsWith("<h4>Setup</h4><div>robot at H1</div><table class='steps'>", h);
        Assert.EndsWith("</table><h4>Expected</h4><div>no error</div>", h);
    }

    [Theory]
    [InlineData("| a | b |", 2)]
    [InlineData("  |a|  ", 1)]
    [InlineData("a | b |", -1)]
    [InlineData("| a | b", -1)]
    [InlineData("|", -1)]
    public void TableCells(string line, int count) =>
        Assert.Equal(count, TcUnitDoc.TableCells(line)?.Count ?? -1);

    static int Count(string s, string part) => (s.Length - s.Replace(part, "").Length) / part.Length;
}
