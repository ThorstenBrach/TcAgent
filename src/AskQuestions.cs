// Questions of the agent to the user (tool tc_ask_user): the format, the check of the arguments, the question as an MCP
// elicitation schema and the answer as text for the agent. Pure logic (no IDE, no client), so the unit tests link this file.
//
//   questions  [{question, header?, options: [{label, description?}], multiSelect?}]  1-4 questions, 0-6 options each
//   answers    question -> answer: the picked labels (", " if several) and/or a free text
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class AskQuestions
{
    public sealed record Option(string Label, string Description);
    public sealed record Question(string Text, string Header, IReadOnlyList<Option> Options, bool MultiSelect);

    /// <summary>The questions of a tc_ask_user call, checked. Throws ArgumentException with a message for the agent.</summary>
    public static List<Question> Parse(JsonNode questions)
    {
        if (questions is not JsonArray arr || arr.Count == 0) throw new ArgumentException("questions: a list of 1-4 questions expected");
        if (arr.Count > 4) throw new ArgumentException("questions: at most 4 questions per call - ask the most important ones first");
        var list = new List<Question>();
        foreach (var n in arr)
        {
            var q = n as JsonObject ?? throw new ArgumentException("questions: every entry is an object {question, options, multiSelect}");
            var text = (q["question"] as JsonValue)?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("questions: every entry needs \"question\"");
            if (list.Any(x => x.Text == text)) throw new ArgumentException("questions: the same question twice: " + text);
            var opts = new List<Option>();
            if (q["options"] is JsonArray oa)
                foreach (var o in oa)
                {
                    var label = o is JsonObject oo ? (oo["label"] as JsonValue)?.ToString()?.Trim() : (o as JsonValue)?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(label)) continue;
                    var desc = o is JsonObject od ? (od["description"] as JsonValue)?.ToString()?.Trim() : null;
                    if (opts.All(x => x.Label != label)) opts.Add(new Option(label, string.IsNullOrEmpty(desc) ? null : desc));
                }
            if (opts.Count > 6) throw new ArgumentException($"questions: at most 6 options per question ({text})");
            var header = (q["header"] as JsonValue)?.ToString()?.Trim();
            bool multi = q["multiSelect"] is JsonValue mv && mv.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            list.Add(new Question(text, string.IsNullOrEmpty(header) ? null : header, opts, multi));
        }
        return list;
    }

    /// <summary>The questions in the format of the TcAgent Chat cards (same as the agent CLI's own question tool).</summary>
    public static JsonObject ForCard(IEnumerable<Question> qs) => new()
    {
        ["questions"] = new JsonArray(qs.Select(q => (JsonNode)new JsonObject
        {
            ["question"] = q.Text, ["header"] = q.Header, ["multiSelect"] = q.MultiSelect,
            ["options"] = new JsonArray(q.Options.Select(o => (JsonNode)new JsonObject { ["label"] = o.Label, ["description"] = o.Description }).ToArray())
        }).ToArray())
    };

    /// <summary>MCP elicitation schema (flat, primitive fields only): per question a choice (enum) and/or a free text field.
    /// Several answers allowed: a text field, labels separated by commas.</summary>
    public static JsonObject ElicitationSchema(IReadOnlyList<Question> qs)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        for (int i = 0; i < qs.Count; i++)
        {
            var q = qs[i];
            var title = q.Header ?? $"Question {i + 1}";
            if (q.Options.Count > 0 && !q.MultiSelect)
            {
                props[$"q{i}"] = new JsonObject
                {
                    ["type"] = "string", ["title"] = title, ["description"] = q.Text,
                    ["enum"] = new JsonArray(q.Options.Select(o => (JsonNode)o.Label).ToArray()),
                    ["enumNames"] = new JsonArray(q.Options.Select(o => (JsonNode)(o.Description == null ? o.Label : $"{o.Label} – {o.Description}")).ToArray())
                };
                props[$"q{i}_other"] = new JsonObject { ["type"] = "string", ["title"] = title + " – other answer", ["description"] = "Optional: your own answer instead of a choice" };
            }
            else
            {
                var hint = q.Options.Count > 0 ? " (one or more of: " + string.Join(", ", q.Options.Select(o => o.Label)) + " – or your own text)" : "";
                props[$"q{i}_other"] = new JsonObject { ["type"] = "string", ["title"] = title, ["description"] = q.Text + hint };
                required.Add($"q{i}_other");
            }
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }

    /// <summary>Answers of an elicitation (q0, q0_other …) as question -> answer; the free text wins over the choice.</summary>
    public static Dictionary<string, string> FromElicitation(IReadOnlyList<Question> qs, JsonObject content)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < qs.Count; i++)
        {
            var other = (content?[$"q{i}_other"] as JsonValue)?.ToString()?.Trim();
            var pick = (content?[$"q{i}"] as JsonValue)?.ToString()?.Trim();
            d[qs[i].Text] = !string.IsNullOrEmpty(other) ? other : pick ?? "";
        }
        return d;
    }

    /// <summary>Answers for the agent. Empty answers are named as such - the agent must not invent them.</summary>
    public static string Format(IReadOnlyList<Question> qs, IReadOnlyDictionary<string, string> answers)
    {
        var sb = new StringBuilder("The user answered:\n");
        foreach (var q in qs)
        {
            answers.TryGetValue(q.Text, out var a);
            sb.Append("- ").Append(q.Text).Append(" → ").AppendLine(string.IsNullOrWhiteSpace(a) ? "(no answer)" : a.Trim());
        }
        sb.Append("Read the answers carefully - they may ask for something else than the options; follow what the user actually said.");
        return sb.ToString();
    }
}
