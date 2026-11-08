// Rules of the ST formatter: defaults, profiles and formatting.json on the knowledge levels (docs/FORMATTER.md).
//
//   formatting.json on a level (built-in, company, user, team, platform, project - later wins):
//     { "profile": "twincat", "rules": { "indent.size": 4, "align.decl": false }, "locked": ["indent.style"] }
//   "locked": true locks every rule the file sets. A rule set to "unset" goes back to the default.
//   Nothing is a fixed rule: every key can be changed, most also know "preserve" (leave it as it is).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed class StFormatConfig
{
    /// <summary>Every rule with its default (profile twincat) and its allowed values - the reference for docs and the window.</summary>
    public static readonly (string Key, string Default, string Values, string Description)[] Rules =
    {
        ("indent.style", "spaces", "spaces | tabs", "indent with spaces or tabs"),
        ("indent.size", "2", "number", "spaces per level (with tabs: one tab per level)"),
        ("indent.tab-width", "4", "number", "width of a tab, for columns and line length"),
        ("indent.case-labels", "true", "true | false", "CASE labels one level under CASE, their statements one more"),
        ("indent.continuation", "preserve", "preserve | indent", "continuation lines: keep their offset to the first line, or one level"),
        ("indent.from-ide", "false", "true | false", "take indent style and size from the TwinCAT editor settings (in the IDE)"),
        ("case.keywords", "upper", "upper | lower | preserve", "IF, THEN, END_IF, VAR ..."),
        ("case.types", "upper", "upper | lower | preserve", "BOOL, INT, TIME, STRING ..."),
        ("case.literals", "upper", "upper | lower | preserve", "TRUE, FALSE and literal prefixes T#, INT#"),
        ("space.operators", "one", "one | preserve", "one space around :=, =>, S=, R=, REF=, + - * /, comparisons, AND/OR"),
        ("space.comma", "one", "one | preserve", "no space before a comma, one after"),
        ("space.decl-colon", "one", "one | preserve", "one space around the : of a declaration"),
        ("space.parens", "preserve", "none | one | preserve", "spaces inside ( ) and [ ]"),
        ("space.keyword-paren", "one", "one | preserve", "IF (, AND (, NOT ("),
        ("space.call-paren", "none", "none | preserve", "no space between a name and its ("),
        ("comment.eol-spaces", "preserve", "preserve | number", "spaces before a comment at the end of a code line (implementation)"),
        ("layout.blocks", "preserve", "expand | preserve", "one statement per line; the bodies of IF, CASE, FOR, WHILE and REPEAT on their own lines"),
        ("layout.then", "preserve", "same-line | own-line | preserve", "THEN, DO and OF at the end of the condition or on their own line"),
        ("align.decl", "true", "true | false", "the : of declarations in a column"),
        ("align.decl-init", "true", "true | false", "the := of initial values in a column"),
        ("align.eol-comments", "true", "true | false", "comments at the end of declarations in a column"),
        ("align.assignments", "false", "true | false", "the := of consecutive assignments in a column"),
        ("align.named-args", "true", "true | false", "the := and => of named parameters in a column when a call has one per line"),
        ("align.max-pad", "40", "number", "no alignment of a group when a line would need more filling"),
        ("wrap.max-length", "120", "number | 0", "longest line; 0 = never wrap"),
        ("wrap.call-args", "when-long", "when-long | never", "a call too long: one parameter per line, ) on its own line"),
        ("wrap.conditions", "when-long", "when-long | never", "a condition or boolean assignment too long: one term per line at the weakest AND/OR"),
        ("wrap.operator", "first", "first | last", "where AND/OR stand when a condition is wrapped"),
        ("blank.max", "1", "number | preserve", "most blank lines in a row"),
        ("blank.body-edges", "remove", "remove | preserve", "blank lines at the start and end of a part"),
        ("blank.around-blocks", "preserve", "add | preserve", "a blank line before and after IF, CASE, FOR, WHILE and REPEAT blocks"),
        ("eol.trim-trailing", "true", "true | false", "remove spaces at the end of lines"),
        ("eol.style", "preserve", "preserve | crlf | lf", "line endings"),
        ("paren.precedence", "true", "true | false", "brackets around AND groups inside OR, so the order is visible (the meaning stays)"),
    };

    /// <summary>Short names in plain words for the settings window (the key stays in the details).</summary>
    public static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indent.style"] = "Indent with", ["indent.size"] = "Spaces per level", ["indent.tab-width"] = "Width of a tab",
        ["indent.case-labels"] = "CASE labels one level in", ["indent.continuation"] = "Continuation lines", ["indent.from-ide"] = "Take indentation from the IDE",
        ["case.keywords"] = "Keywords", ["case.types"] = "Data types", ["case.literals"] = "TRUE, FALSE, T#",
        ["space.operators"] = "Around operators", ["space.comma"] = "After commas", ["space.decl-colon"] = "Around the : of declarations",
        ["space.parens"] = "Inside brackets", ["space.keyword-paren"] = "Keyword and (", ["space.call-paren"] = "Name and (",
        ["comment.eol-spaces"] = "Before end-of-line comments",
        ["layout.blocks"] = "One statement per line", ["layout.then"] = "THEN, DO, OF",
        ["align.decl"] = "Declarations: the :", ["align.decl-init"] = "Declarations: the :=", ["align.eol-comments"] = "Declarations: comments",
        ["align.assignments"] = "Assignments: the :=", ["align.named-args"] = "Named parameters", ["align.max-pad"] = "Most filling for a column",
        ["wrap.max-length"] = "Longest line", ["wrap.call-args"] = "Long calls", ["wrap.conditions"] = "Long conditions", ["wrap.operator"] = "AND / OR stand",
        ["blank.max"] = "Blank lines in a row", ["blank.body-edges"] = "Blank lines at the edges", ["blank.around-blocks"] = "Blank lines around blocks",
        ["eol.trim-trailing"] = "Spaces at line ends", ["eol.style"] = "Line endings", ["paren.precedence"] = "Brackets for AND in OR",
    };

    public static readonly Dictionary<string, Dictionary<string, string>> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twincat"] = new(),
        ["compact"] = new() { ["align.decl"] = "false", ["align.decl-init"] = "false", ["align.eol-comments"] = "false", ["align.named-args"] = "false" },
        ["structured"] = new() { ["layout.blocks"] = "expand", ["layout.then"] = "own-line", ["blank.around-blocks"] = "add", ["align.assignments"] = "true" },
        ["preserve"] = Rules.Where(r => r.Values.Contains("preserve")).ToDictionary(r => r.Key, _ => "preserve")
            .Concat(new Dictionary<string, string> { ["align.decl"] = "false", ["align.decl-init"] = "false", ["align.eol-comments"] = "false", ["align.named-args"] = "false",
                ["wrap.max-length"] = "0", ["paren.precedence"] = "false", ["eol.trim-trailing"] = "false" }).ToDictionary(k => k.Key, k => k.Value),
    };

    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where each value comes from: "default", "profile compact", "user", "project" ...</summary>
    public readonly Dictionary<string, string> Source = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Notes = new();
    /// <summary>Rules a level locked: key → the level.</summary>
    public readonly Dictionary<string, string> Locked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A formatting.json was found on some level (the agent formats its own code only then).</summary>
    public bool FromFiles;

    public StFormatConfig()
    {
        foreach (var r in Rules) { Values[r.Key] = r.Default; Source[r.Key] = "default"; }
    }

    public string this[string key] => Values.TryGetValue(key, out var v) ? v : "";
    public bool Flag(string key) => this[key].Equals("true", StringComparison.OrdinalIgnoreCase);
    public int Int(string key, int fallback) => int.TryParse(this[key], out var v) ? v : fallback;
    public bool Preserve(string key) => this[key].Equals("preserve", StringComparison.OrdinalIgnoreCase);

    public string IndentUnit => this["indent.style"].Equals("tabs", StringComparison.OrdinalIgnoreCase) ? "\t" : new string(' ', Math.Max(0, Int("indent.size", 2)));
    public int TabWidth => Math.Max(1, Int("indent.tab-width", 4));

    public void Set(string key, string value, string source)
    {
        if (!Rules.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) { Notes.Add($"unknown rule '{key}' ({source}) - ignored"); return; }
        if (value.Equals("unset", StringComparison.OrdinalIgnoreCase))
        { Values[key] = Rules.First(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Default; Source[key] = "default (unset by " + source + ")"; return; }
        Values[key] = value; Source[key] = source;
    }

    public void ApplyProfile(string profile, string source)
    {
        if (!Profiles.TryGetValue(profile ?? "", out var p)) { Notes.Add($"unknown profile '{profile}' ({source})"); return; }
        foreach (var kv in p) Set(kv.Key, kv.Value, $"profile {profile} ({source})");
    }

    /// <summary>Applies one formatting.json. locked: rules a level before locked; they stay.</summary>
    public void Apply(JsonObject j, string source, Dictionary<string, string> locked)
    {
        var lockAll = j["locked"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
        var lockKeys = (j["locked"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToList() ?? new List<string>();
        void SetUnlessLocked(string key, string value, string src)
        {
            if (locked.TryGetValue(key, out var by)) { Notes.Add($"{key} from {source} ignored: locked by {by}"); return; }
            Set(key, value, src);
        }
        if (j["profile"]?.ToString() is { Length: > 0 } prof)
        {
            if (!Profiles.TryGetValue(prof, out var p)) Notes.Add($"unknown profile '{prof}' ({source})");
            else foreach (var kv in p) SetUnlessLocked(kv.Key, kv.Value, $"profile {prof} ({source})");
        }
        if (j["rules"] is JsonObject rules)
            foreach (var kv in rules)
            {
                SetUnlessLocked(kv.Key, kv.Value?.ToString() ?? "unset", source);
                if (lockAll && !locked.ContainsKey(kv.Key)) locked[kv.Key] = source;
            }
        foreach (var k in lockKeys) if (!locked.ContainsKey(k)) locked[k] = source;
    }

    /// <summary>The effective rules for a solution: the formatting.json of every knowledge level, later wins, locks hold.</summary>
    public static StFormatConfig Load(string solutionDir)
    {
        var c = new StFormatConfig();
        var locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> platforms = Enumerable.Empty<string>();
        try { if (solutionDir != null) platforms = KnowledgeLayers.Platforms(solutionDir)?.Active ?? platforms; } catch { }
        foreach (var layer in KnowledgeLayers.Layers(solutionDir, platforms).OrderBy(l => l.Rank))
        {
            var f = Path.Combine(layer.Root, "formatting.json");
            if (!File.Exists(f)) continue;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? throw new Exception("not a JSON object");
                c.Apply(j, layer.Label, locked);
                c.FromFiles = true;
            }
            catch (Exception ex) { c.Notes.Add($"{f}: {ex.Message} - ignored"); }
        }
        foreach (var kv in locked) c.Locked[kv.Key] = kv.Value;
        return c;
    }

    public static StFormatConfig FromFile(string file)
    {
        var c = new StFormatConfig();
        var j = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception(file + ": not a JSON object");
        c.Apply(j, Path.GetFileName(file), new Dictionary<string, string>());
        c.FromFiles = true;
        return c;
    }

    public JsonObject ToJson() => new()
    {
        ["rules"] = new JsonObject(Rules.Select(r => KeyValuePair.Create(r.Key, (JsonNode)new JsonObject
        {
            ["value"] = this[r.Key], ["from"] = Source[r.Key], ["default"] = r.Default, ["values"] = r.Values, ["description"] = r.Description,
            ["label"] = Labels.TryGetValue(r.Key, out var lab) ? lab : r.Key,
            ["locked"] = Locked.TryGetValue(r.Key, out var by) ? by : null
        }))),
        ["profiles"] = new JsonObject(Profiles.Select(p => KeyValuePair.Create(p.Key,
            (JsonNode)new JsonObject(Rules.Select(r => KeyValuePair.Create(r.Key, (JsonNode)(p.Value.TryGetValue(r.Key, out var v) ? v : r.Default))))))),
        ["fromFiles"] = FromFiles,
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray())
    };
}
