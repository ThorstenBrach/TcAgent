// Tests for the questions of the agent to the user (tool tc_ask_user, AskQuestions.cs).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace TcAgent.Tests;

public sealed class AskQuestionsTests
{
    static JsonNode Q(string json) => JsonNode.Parse(json);

    [Fact]
    public void QuestionsAreParsedWithOptionsAndMultiSelect()
    {
        var qs = AskQuestions.Parse(Q("""
            [ { "question": "Which cycle time?", "header": "Cycle time",
                "options": [ { "label": "1 ms", "description": "motion" }, { "label": "10 ms" }, "100 ms" ] },
              { "question": "Which modes?", "multiSelect": true, "options": [ { "label": "Auto" }, { "label": "Manual" } ] } ]
            """));
        Assert.Equal(2, qs.Count);
        Assert.Equal("Cycle time", qs[0].Header);
        Assert.Equal(new[] { "1 ms", "10 ms", "100 ms" }, qs[0].Options.Select(o => o.Label));
        Assert.Equal("motion", qs[0].Options[0].Description);
        Assert.False(qs[0].MultiSelect);
        Assert.True(qs[1].MultiSelect);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[ { \"header\": \"x\" } ]")]
    [InlineData("[ {\"question\":\"a\"}, {\"question\":\"b\"}, {\"question\":\"c\"}, {\"question\":\"d\"}, {\"question\":\"e\"} ]")]
    [InlineData("[ {\"question\":\"a\"}, {\"question\":\"a\"} ]")]
    [InlineData("[ {\"question\":\"a\", \"options\": [\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"] } ]")]
    public void InvalidQuestionsAreRefused(string json) => Assert.Throws<ArgumentException>(() => AskQuestions.Parse(Q(json)));

    [Fact]
    public void ElicitationSchemaHasAChoiceAndAFreeTextField()
    {
        var qs = AskQuestions.Parse(Q("""[ { "question": "Which cycle time?", "options": [ "1 ms", "10 ms" ] }, { "question": "Name of the FB?" } ]"""));
        var schema = AskQuestions.ElicitationSchema(qs);
        var props = schema["properties"]!.AsObject();
        Assert.Equal("1 ms", props["q0"]!["enum"]![0]!.ToString());
        Assert.NotNull(props["q0_other"]);
        Assert.NotNull(props["q1_other"]);
        Assert.Null(props["q1"]);
        Assert.Contains("q1_other", schema["required"]!.AsArray().Select(x => x!.ToString()));
        // only primitive field types (MCP elicitation)
        Assert.All(props, p => Assert.Equal("string", p.Value!["type"]!.ToString()));
    }

    [Fact]
    public void FreeTextWinsOverTheChoice()
    {
        var qs = AskQuestions.Parse(Q("""[ { "question": "Which cycle time?", "options": [ "1 ms", "10 ms" ] } ]"""));
        var a = AskQuestions.FromElicitation(qs, new JsonObject { ["q0"] = "1 ms", ["q0_other"] = "5 ms please" });
        Assert.Equal("5 ms please", a["Which cycle time?"]);
        a = AskQuestions.FromElicitation(qs, new JsonObject { ["q0"] = "10 ms" });
        Assert.Equal("10 ms", a["Which cycle time?"]);
    }

    [Fact]
    public void FormatNamesMissingAnswers()
    {
        var qs = AskQuestions.Parse(Q("""[ { "question": "A?" }, { "question": "B?" } ]"""));
        var text = AskQuestions.Format(qs, new Dictionary<string, string> { ["A?"] = "yes" });
        Assert.Contains("A? → yes", text);
        Assert.Contains("B? → (no answer)", text);
    }

    [Fact]
    public void CardFormatMatchesTheChatCards()
    {
        var qs = AskQuestions.Parse(Q("""[ { "question": "Q?", "options": [ { "label": "x", "description": "d" } ], "multiSelect": true } ]"""));
        var card = AskQuestions.ForCard(qs);
        Assert.Equal("Q?", card["questions"]![0]!["question"]!.ToString());
        Assert.Equal("x", card["questions"]![0]!["options"]![0]!["label"]!.ToString());
        Assert.True(card["questions"]![0]!["multiSelect"]!.GetValue<bool>());
    }
}
