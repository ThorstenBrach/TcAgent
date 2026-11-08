// Examples of the ST formatter: for every rule a short piece of code, formatted with each value of the rule while all other
// rules leave the code alone (profile preserve). The formatter itself makes them, so they always show what it really does:
// docs/FORMATTER_EXAMPLES.md is generated from here (TcAgent.exe format --examples --markdown, a test checks it is current)
// and the settings window of the IDE shows them in the details of a rule (--examples --json).
//
// SPDX-License-Identifier: MIT
using System.Text;

namespace TcAgent;

public static class StFormatExamples
{
    public sealed record Example(string Rule, StPart Part, string Code, string[] Values, (string Key, string Value)[] With);

    static Example I(string rule, string code, string[] values, params (string, string)[] with) => new(rule, StPart.Implementation, code, values, with);
    static Example D(string rule, string code, string[] values, params (string, string)[] with) => new(rule, StPart.Declaration, code, values, with);

    const string LongCall = "fbAxis(Execute := bStart, Position := rTarget, Velocity := rSpeed, Acceleration := rAcc);";
    const string LongIf = "IF bStartButton AND bDoorClosed AND NOT bEmergencyStop OR bSetupMode AND bKeySwitch THEN\n  x := 1;\nEND_IF";

    /// <summary>One example per rule that changes code (the others: indent.from-ide works in the IDE, eol.* are invisible).</summary>
    public static readonly Example[] All =
    {
        I("indent.style", "IF a THEN\n  b := 1;\nEND_IF", new[] { "spaces", "tabs" }),
        I("indent.size", "IF a THEN\n  IF b THEN\n    c := 1;\n  END_IF\nEND_IF", new[] { "2", "4" }),
        I("indent.case-labels", "CASE eState OF\nE_State.Idle:\nx := 1;\nE_State.Run:\nx := 2;\nEND_CASE", new[] { "true", "false" }),
        D("indent.underscore", "VAR\n  bStart : BOOL;\n  _localCount : INT;\n  nParts : INT;\nEND_VAR", new[] { "off", "declarations", "everywhere" }, ("align.decl", "true")),
        I("indent.continuation", "nSum := nFirst +\n          nSecond;", new[] { "preserve", "indent" }),
        I("case.keywords", "if bStart then\n  bBusy := TRUE;\nend_if", new[] { "upper", "lower", "preserve" }),
        D("case.types", "VAR\n  nCount : int;\n  sName : string;\nEND_VAR", new[] { "upper", "lower", "preserve" }),
        I("case.literals", "bDone := true;\ntWait := t#5s;", new[] { "upper", "lower", "preserve" }),
        I("space.operators", "nX:=nA+nB*2;\nbHigh:=nX>=10;", new[] { "one", "preserve" }),
        I("space.comma", "F_Sum(nA,nB ,nC);", new[] { "one", "preserve" }),
        D("space.decl-colon", "VAR\n  nCount:INT;\nEND_VAR", new[] { "one", "preserve" }),
        I("space.parens", "nX := ( nA + nB ) * (nC);", new[] { "none", "one", "preserve" }),
        I("space.keyword-paren", "bX := NOT(bA) AND(bB);", new[] { "one", "preserve" }),
        I("space.call-paren", "F_Sum (nA, nB);", new[] { "none", "preserve" }),
        I("comment.eol-spaces", "nA := 1;// first\nnLonger := 22;    // second", new[] { "preserve", "1", "2" }),
        I("layout.blocks", "IF bStart THEN nCount := 0; bBusy := TRUE; END_IF", new[] { "expand", "preserve" }),
        I("layout.then", "IF bStart THEN\n  bBusy := TRUE;\nEND_IF\nWHILE nIndex < 10\nDO\n  nIndex := nIndex + 1;\nEND_WHILE", new[] { "same-line", "own-line", "preserve" }),
        D("align.decl", "VAR\n  bStart : BOOL;\n  nCount : INT;\n  fbTimer : TON;\nEND_VAR", new[] { "true", "false" }),
        D("align.decl-init", "VAR\n  nCount : INT := 0;\n  tTimeout : TIME := T#5S;\nEND_VAR", new[] { "true", "false" }),
        D("align.eol-comments", "VAR\n  bStart : BOOL; // start the cycle\n  nCount : INT; // parts so far\nEND_VAR", new[] { "true", "false" }),
        I("align.assignments", "nA := 1;\nnLonger := 2;\nbDone := TRUE;", new[] { "true", "false" }),
        I("align.named-args", "fbAxis(\n  Execute := bStart,\n  Position := rTarget,\n  Done => bDone\n);", new[] { "true", "false" }),
        D("align.max-pad", "VAR\n  b : BOOL;\n  fbConveyorStartupMonitoring : TON;\nEND_VAR", new[] { "10", "40" }, ("align.decl", "true")),
        I("wrap.max-length", LongCall, new[] { "0", "60" }, ("wrap.call-args", "when-long")),
        I("wrap.call-args", LongCall, new[] { "when-long", "never" }, ("wrap.max-length", "60")),
        I("wrap.conditions", LongIf, new[] { "when-long", "never" }, ("wrap.max-length", "60")),
        I("wrap.operator", LongIf, new[] { "first", "last" }, ("wrap.max-length", "60"), ("wrap.conditions", "when-long")),
        I("blank.max", "nA := 1;\n\n\n\nnB := 2;", new[] { "0", "1", "preserve" }),
        I("blank.body-edges", "\n\nnA := 1;\n\n", new[] { "remove", "preserve" }),
        I("blank.around-blocks", "nA := 1;\nIF bStart THEN\n  nB := 2;\nEND_IF\nnC := 3;", new[] { "add", "preserve" }),
        I("paren.precedence", "bRelease := bAuto AND bDoorClosed OR bSetup AND bKey;", new[] { "true", "false" }),
    };

    /// <summary>The example formatted with one value: profile preserve, the extra settings of the example, then the value.</summary>
    public static string Run(Example e, string value)
    {
        var c = new StFormatConfig();
        c.ApplyProfile("preserve", "example");
        foreach (var (k, v) in e.With) c.Set(k, v, "example");
        c.Set(e.Rule, value, "example");
        var r = StFormatter.Format(e.Code, c, e.Part);
        return r.Error != null ? "(not formatted: " + r.Error + ")" : r.Text;
    }

    /// <summary>docs/FORMATTER_EXAMPLES.md.</summary>
    public static string Markdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ST formatter: an example for every rule\n\n");
        sb.Append("Generated by `TcAgent.exe format --examples --markdown` - the formatter made every result below, so they show what it\n");
        sb.Append("really does. Each example changes only the rule it is about; all other rules leave the code alone (profile `preserve`).\n");
        sb.Append("The rules, their defaults and the configuration: [FORMATTER.md](FORMATTER.md).\n");
        string group = null;
        foreach (var e in All)
        {
            var g = e.Rule.Split('.')[0];
            if (g != group) { sb.Append("\n## ").Append(Group(g)).Append('\n'); group = g; }
            var rule = StFormatConfig.Rules.First(r => r.Key == e.Rule);
            sb.Append("\n### ").Append(StFormatConfig.Labels.TryGetValue(e.Rule, out var l) ? l : e.Rule).Append(" (`").Append(e.Rule).Append("`)\n\n");
            sb.Append(char.ToUpperInvariant(rule.Description[0])).Append(rule.Description[1..]).Append(". Default: `").Append(rule.Default).Append('`');
            if (e.With.Length > 0) sb.Append("; shown with ").Append(string.Join(", ", e.With.Select(w => $"`{w.Key}: {w.Value}`")));
            sb.Append(".\n\nWritten as:\n\n```\n").Append(e.Code.TrimEnd('\n')).Append("\n```\n");
            foreach (var v in e.Values)
                sb.Append("\n`").Append(v).Append("`:\n\n```\n").Append(Run(e, v).Replace("\r\n", "\n").TrimEnd('\n')).Append("\n```\n");
        }
        return sb.ToString();
    }

    static string Group(string g) => g switch
    {
        "indent" => "Indentation", "case" => "Case", "space" => "Spaces", "comment" => "Comments", "layout" => "Layout", "align" => "Columns",
        "wrap" => "Long lines", "blank" => "Blank lines", "paren" => "Brackets in conditions", _ => g
    };
}
