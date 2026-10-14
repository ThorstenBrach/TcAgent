// Risk rules: Markdown files that configure the static risk analysis (tc_doc action=risks, "Risks" section of the HTML page,
// TcAgent.exe doc --risks) - the same principle as the coding rules:
//   1. TCAGENT_RISK_RULES                       files/folders, ';'-separated (e.g. company rules on a network drive)
//   2. %LOCALAPPDATA%\TcAgent\risk-rules.md     personal rules
//   3. <Solution>\.tcagent\risk-rules.md        project rules (versioned with Git)
//   4. <Solution>\.tcagent\risk-rules\*.md      further project files
// Later sources take precedence (same rule id or same built-in setting: the later one wins).
//
// A file can contain three kinds of sections (see docs/risk-rules.example.md):
//   ## Built-in rules          - <rule id>: off | severity=high | limit=200 ...   (switch off / tune the 13 built-in rules)
//   ## Rule <id>               - own text rule: find (regex, every match is a finding) or require (regex that must occur),
//                                unless, unlessInObject, in (impl/decl), kinds, objects, comments, severity, message, why
//   ## Checks                  - free-text checks the AI agent verifies by reading the code (not machine checked)
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed class RiskConfig
{
    public sealed class Builtin
    {
        public bool Off;
        public string Severity;                                            // null = computed by the rule
        public readonly Dictionary<string, int> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Source;
    }

    public sealed class Custom
    {
        public string Id, Severity = "medium", Message, Why, Source;
        public Regex Find, Require, Unless, UnlessInObject;
        public string In = "impl";                                         // impl, decl or both
        public bool KeepComments;                                          // false: comments and strings are blanked before matching
        public readonly List<string> Kinds = new();                        // PROGRAM, FUNCTION_BLOCK, METHOD ... (empty = all with code)
        public readonly List<Regex> Objects = new();                       // name patterns (* and ?), empty = all
        public bool Off;
        public string Describe() =>
            (Find != null ? $"find {Find}" : $"require {Require}") + (Unless != null ? $", unless {Unless}" : "") +
            (UnlessInObject != null ? $", unlessInObject {UnlessInObject}" : "") + $", in {In}" +
            (Kinds.Count > 0 ? $", kinds {string.Join("/", Kinds)}" : "") + (Objects.Count > 0 ? ", objects filtered" : "") +
            (KeepComments ? ", with comments" : "");
    }

    public readonly Dictionary<string, Builtin> Builtins = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Custom> Customs = new(StringComparer.OrdinalIgnoreCase);   // insertion order = file order
    public readonly List<(string Text, string Source)> Checks = new();
    public readonly List<string> Sources = new();
    public readonly List<string> Errors = new();

    public static readonly RiskConfig Empty = new();
    public bool IsEmpty => Builtins.Count == 0 && Customs.Count == 0 && Checks.Count == 0 && Errors.Count == 0;

    public static readonly string[] Kinds = { "PROGRAM", "FUNCTION_BLOCK", "FUNCTION", "METHOD", "PROPERTY", "ACTION", "TRANSITION", "INTERFACE", "DUT", "GVL" };

    // ------------------------------------------------------------------ files
    public static List<string> Files(string solutionDir)
    {
        var files = new List<string>();
        void AddPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = Environment.ExpandEnvironmentVariables(p.Trim());
            if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) files.Add(p);
        }
        foreach (var p in (Environment.GetEnvironmentVariable("TCAGENT_RISK_RULES") ?? "").Split(';')) AddPath(p);
        AddPath(PersonalFile);
        if (!string.IsNullOrEmpty(solutionDir))
        {
            AddPath(Path.Combine(solutionDir, ".tcagent", "risk-rules.md"));
            AddPath(Path.Combine(solutionDir, ".tcagent", "risk-rules"));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string PersonalFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "risk-rules.md");

    public static string TargetFile(string solutionDir, string scope) =>
        string.Equals(scope, "personal", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(solutionDir)
            ? PersonalFile : Path.Combine(solutionDir, ".tcagent", "risk-rules.md");

    public static string Where(string solutionDir) =>
        "Locations: " + (string.IsNullOrEmpty(solutionDir) ? "<solution folder>" : solutionDir) + "\\.tcagent\\risk-rules.md (project, in Git) " +
        "and .tcagent\\risk-rules\\*.md, %LOCALAPPDATA%\\TcAgent\\risk-rules.md (personal), TCAGENT_RISK_RULES (files/folders, ';'-separated).";

    public static RiskConfig Load(string solutionDir)
    {
        var c = new RiskConfig();
        foreach (var f in Files(solutionDir))
        {
            string text;
            try { text = File.ReadAllText(f); } catch (Exception ex) { c.Errors.Add($"{f}: not readable ({ex.Message})"); continue; }
            c.Sources.Add(f);
            c.Parse(text, f);
        }
        return c;
    }

    // ------------------------------------------------------------------ parser
    static readonly Regex Heading = new(@"^#{2,3}\s+(.+?)\s*$", RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex IdRx = new(@"^[A-Za-z][\w-]*$", RegexOptions.Compiled);

    public void Parse(string text, string source)
    {
        string mode = null;               // builtin, rule, checks or null (other sections: ignored, e.g. explanations)
        Custom rule = null;
        Dictionary<string, string> fields = null;
        int ruleLine = 0;
        // HTML comments (explanations in the file) are blanked, line numbers stay the same
        var clean = Regex.Replace((text ?? "").Replace("\r\n", "\n"), @"<!--.*?-->", m => new string('\n', m.Value.Count(ch => ch == '\n')), RegexOptions.Singleline);
        var lines = clean.Split('\n');

        void FinishRule()
        {
            if (rule != null) BuildRule(rule, fields, $"{Path.GetFileName(source)} line {ruleLine}");
            rule = null; fields = null;
        }

        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }   // code examples in the file are not rules
            if (inFence) continue;
            var h = Heading.Match(l);
            if (h.Success)
            {
                FinishRule();
                var t = h.Groups[1].Value.Trim();
                if (Regex.IsMatch(t, @"^(built-?in(\s+rules)?|eingebaute\s+regeln)$", RegexOptions.IgnoreCase)) mode = "builtin";
                else if (Regex.IsMatch(t, @"^(checks?|agent\s+checks?|checks\s+for\s+the\s+agent|pr(ü|ue)fungen(\s+f(ü|ue)r\s+den\s+agenten)?)\b", RegexOptions.IgnoreCase)) mode = "checks";
                else if (Regex.Match(t, @"^(?:rule|regel)\s+`?([^`\s]+)`?\s*$", RegexOptions.IgnoreCase) is { Success: true } rm)
                {
                    mode = "rule";
                    var id = rm.Groups[1].Value;
                    if (!IdRx.IsMatch(id)) { Errors.Add($"{Path.GetFileName(source)} line {i + 1}: invalid rule id '{id}' (letters, digits, - and _)"); mode = null; continue; }
                    if (PlcDoc.RiskRules.Any(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    { Errors.Add($"{Path.GetFileName(source)} line {i + 1}: '{id}' is a built-in rule - tune it under '## Built-in rules'"); mode = null; continue; }
                    rule = new Custom { Id = id, Source = source };
                    fields = new(StringComparer.OrdinalIgnoreCase);
                    ruleLine = i + 1;
                }
                else mode = null;
                continue;
            }
            var b = Bullet.Match(l);
            if (!b.Success || mode == null) continue;
            var item = b.Groups[1].Value.Trim();
            if (mode == "checks") { if (item.Length > 0) Checks.Add((item, source)); continue; }
            var colon = item.IndexOf(':');
            if (colon <= 0) { Errors.Add($"{Path.GetFileName(source)} line {i + 1}: expected '- key: value'"); continue; }
            var key = item[..colon].Trim().Trim('`');
            var value = Unquote(item[(colon + 1)..].Trim());
            if (mode == "builtin") ParseBuiltin(key, value, $"{Path.GetFileName(source)} line {i + 1}", source);
            else if (fields != null) fields[key] = value;
        }
        FinishRule();
    }

    static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '`' && v[^1] == '`') v = v[1..^1];
        return v;
    }

    void ParseBuiltin(string id, string value, string where, string source)
    {
        var def = PlcDoc.RiskRules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (def.Id == null) { Errors.Add($"{where}: unknown built-in rule '{id}' (known: {string.Join(", ", PlcDoc.RiskRules.Select(r => r.Id))})"); return; }
        if (!Builtins.TryGetValue(def.Id, out var s)) Builtins[def.Id] = s = new Builtin();
        s.Source = source;
        foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            if (Regex.IsMatch(part, @"^(off|aus|disabled?)$", RegexOptions.IgnoreCase)) { s.Off = true; continue; }
            if (Regex.IsMatch(part, @"^(on|an|enabled?)$", RegexOptions.IgnoreCase)) { s.Off = false; continue; }
            var kv = part.Split('=', 2);
            if (kv.Length != 2) { Errors.Add($"{where}: '{part}' - expected off, on, severity=... or <parameter>=<number>"); continue; }
            var k = kv[0].Trim(); var v = kv[1].Trim();
            if (k.Equals("severity", StringComparison.OrdinalIgnoreCase))
            {
                if (PlcDoc.SeverityRank(v) > 2) { Errors.Add($"{where}: severity must be high, medium or low"); continue; }
                s.Severity = v.ToLowerInvariant();
            }
            else if (!BuiltinParams.TryGetValue(def.Id, out var allowed) || !allowed.Any(a => a.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
                Errors.Add($"{where}: '{k}' is no parameter of {def.Id}" + (BuiltinParams.TryGetValue(def.Id, out var al) ? $" (parameters: {string.Join(", ", al.Select(a => a.Name))})" : " (no parameters)"));
            else if (!int.TryParse(v, out var n) || n < 1) Errors.Add($"{where}: {k} must be a positive number");
            else s.Params[allowed.First(a => a.Name.Equals(k, StringComparison.OrdinalIgnoreCase)).Name] = n;
        }
    }

    /// <summary>Tunable thresholds of the built-in rules with their defaults.</summary>
    public static readonly Dictionary<string, (string Name, int Default, string Meaning)[]> BuiltinParams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["long-code"] = new[] { ("limit", 300, "code lines of a POU before 'low' (twice = medium)"), ("memberLimit", 150, "code lines of a method/action/property") },
        ["deep-nesting"] = new[] { ("limit", 5, "nesting depth before 'low'"), ("mediumLimit", 7, "nesting depth before 'medium'") },
        ["magic-number"] = new[] { ("labels", 3, "numeric CASE labels before a finding") },
    };

    public int Param(string rule, string name)
    {
        if (Builtins.TryGetValue(rule, out var s) && s.Params.TryGetValue(name, out var v)) return v;
        return BuiltinParams.TryGetValue(rule, out var ps) ? ps.First(p => p.Name == name).Default : 0;
    }

    public bool IsOff(string rule) => Builtins.TryGetValue(rule, out var s) && s.Off;

    void BuildRule(Custom r, Dictionary<string, string> f, string where)
    {
        string Get(string k) => f.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        Regex Rx(string k)
        {
            var v = Get(k);
            if (v == null) return null;
            try { return new Regex(v, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { Errors.Add($"{where}: {k} is no valid regular expression ({ex.Message})"); return null; }
        }
        foreach (var k in f.Keys.Where(k => !KnownFields.Contains(k)))
            Errors.Add($"{where}: unknown field '{k}' (fields: {string.Join(", ", KnownFields)})");
        r.Find = Rx("find"); r.Require = Rx("require"); r.Unless = Rx("unless"); r.UnlessInObject = Rx("unlessInObject");
        if ((r.Find == null) == (r.Require == null)) { Errors.Add($"{where}: rule {r.Id} needs exactly one of 'find' or 'require'"); return; }
        if (Get("severity") is { } sev)
        {
            if (PlcDoc.SeverityRank(sev) > 2) { Errors.Add($"{where}: severity must be high, medium or low"); return; }
            r.Severity = sev.ToLowerInvariant();
        }
        r.Message = Get("message") ?? (r.Find != null ? $"matches {r.Find}" : $"does not contain {r.Require}");
        r.Why = Get("why");
        var part = (Get("in") ?? "impl").ToLowerInvariant();
        r.In = part switch { "impl" or "implementation" => "impl", "decl" or "declaration" => "decl", "both" or "all" => "both", _ => null };
        if (r.In == null) { Errors.Add($"{where}: in must be impl, decl or both, not '{part}'"); return; }
        r.KeepComments = Get("comments") is { } cm && Regex.IsMatch(cm, @"^(keep|yes|true|include)$", RegexOptions.IgnoreCase);
        if (Get("kinds") is { } kinds)
            foreach (var k in kinds.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = k.Trim().ToUpperInvariant() switch { "FB" => "FUNCTION_BLOCK", "PRG" => "PROGRAM", "FUN" or "FC" => "FUNCTION", var x => x };
                if (!Kinds.Contains(n)) { Errors.Add($"{where}: unknown kind '{k}' (kinds: {string.Join(", ", Kinds)}, FB, PRG)"); continue; }
                r.Kinds.Add(n);
            }
        if (Get("objects") is { } objs)
            foreach (var o in objs.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                r.Objects.Add(new Regex("^" + Regex.Escape(o.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase));
        r.Off = Get("enabled") is { } en && Regex.IsMatch(en, @"^(false|no|off)$", RegexOptions.IgnoreCase);
        Customs[r.Id] = r;     // a later file with the same id replaces the rule
    }

    static readonly string[] KnownFields = { "find", "require", "unless", "unlessInObject", "in", "kinds", "objects", "comments", "severity", "message", "why", "enabled" };

    // ------------------------------------------------------------------ text for tc_risk_rules action=show
    public string Show(string solutionDir)
    {
        var o = new StringBuilder();
        o.AppendLine(Sources.Count == 0 ? "No risk rule files - the 13 built-in rules run with their defaults." : "Risk rule files (later ones take precedence):");
        foreach (var s in Sources) o.AppendLine("  " + s);
        o.AppendLine(Where(solutionDir));
        if (Errors.Count > 0)
        {
            o.AppendLine($"\nERRORS ({Errors.Count}) - these entries are ignored:");
            foreach (var e in Errors) o.AppendLine("  " + e);
        }
        o.AppendLine("\nBuilt-in rules:");
        foreach (var (id, sev, _) in PlcDoc.RiskRules)
        {
            Builtins.TryGetValue(id, out var s);
            var ps = BuiltinParams.TryGetValue(id, out var p) ? "  " + string.Join(", ", p.Select(x => $"{x.Name}={Param(id, x.Name)}")) : "";
            o.AppendLine($"  {id,-20} {(s?.Off == true ? "OFF" : (s?.Severity ?? sev))}{ps}{(s != null ? "   (set in " + Path.GetFileName(s.Source) + ")" : "")}");
        }
        if (Customs.Count > 0)
        {
            o.AppendLine("\nOwn rules:");
            foreach (var r in Customs.Values)
                o.AppendLine($"  {r.Id,-20} {(r.Off ? "OFF" : r.Severity)}  {r.Message}\n  {"",-20} {r.Describe()}   ({Path.GetFileName(r.Source)})");
        }
        if (Checks.Count > 0)
        {
            o.AppendLine("\nChecks for the agent (verify by reading the code):");
            foreach (var (t, s) in Checks) o.AppendLine($"  - {t}");
        }
        return o.ToString();
    }

    // ------------------------------------------------------------------ adding
    /// <summary>Appends an own rule (## Rule id), a built-in setting or an agent check to the rule file. Returns (file, text).</summary>
    public static (string File, string Result) Add(string solutionDir, string scope, string section, string body, bool dryRun)
    {
        var file = TargetFile(solutionDir, scope);
        body = (body ?? "").Replace("\r\n", "\n");      // RuleBody uses AppendLine (\r\n on Windows) - avoid \r\r\n in the file
        var text = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : "# Risk rules\n\nSee docs/risk-rules.example.md for the format.\n";
        var lines = text.TrimEnd('\n').Split('\n').ToList();
        int head = lines.FindIndex(l => Heading.Match(l) is { Success: true } h && h.Groups[1].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (section.StartsWith("Rule ", StringComparison.OrdinalIgnoreCase) && head >= 0)
            throw new Exception($"'{section}' already exists in {file} - choose another id or edit the file");
        if (head < 0)
        {
            lines.Add(""); lines.Add("## " + section);
            lines.AddRange(body.Split('\n'));
        }
        else
        {
            int at = head + 1;
            for (int i = head + 1; i < lines.Count && !Heading.IsMatch(lines[i]) && !lines[i].StartsWith("# "); i++)
                if (lines[i].Trim().Length > 0) at = i + 1;
            lines.InsertRange(at, body.Split('\n'));
        }
        var result = string.Join("\r\n", lines) + "\r\n";
        // the new content must parse without errors before it is written
        var probe = new RiskConfig();
        probe.Parse(result, file);
        if (probe.Errors.Count > 0) throw new Exception("not written - the entry is invalid:\n  " + string.Join("\n  ", probe.Errors));
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, result, new UTF8Encoding(false));
        }
        return (file, (dryRun ? "PROPOSAL (not written):\n" : "added:\n") + $"## {section}\n{body}");
    }
}

public sealed partial class PlcDoc
{
    /// <summary>Risk rule configuration (risk-rules.md files); set before Risks() is called.</summary>
    public RiskConfig RiskConfig = RiskConfig.Empty;

    /// <summary>Applies the configuration to the built-in findings (off, severity) and adds the own rules.</summary>
    void ApplyRiskConfig(List<Risk> r)
    {
        var c = RiskConfig ?? RiskConfig.Empty;
        r.RemoveAll(x => c.IsOff(x.Rule));
        foreach (var x in r)
            if (c.Builtins.TryGetValue(x.Rule, out var s) && s.Severity != null) x.Severity = s.Severity;
        foreach (var rule in c.Customs.Values.Where(x => !x.Off))
            try { r.AddRange(CustomRisks(rule)); }
            catch (RegexMatchTimeoutException) { c.Errors.Add($"rule {rule.Id}: regular expression too slow (timeout) - simplify it"); }
    }

    /// <summary>Findings of one own rule (also used by tc_risk_rules action=test).</summary>
    public List<Risk> CustomRisks(RiskConfig.Custom rule)
    {
        var r = new List<Risk>();
        foreach (var p in Pous.Values)
        {
            if (rule.Kinds.Count > 0 ? !rule.Kinds.Contains(p.Kind) : p.Kind is "DUT" or "GVL" or "INTERFACE" && rule.In == "impl") continue;
            if (rule.Objects.Count > 0 && !rule.Objects.Any(o => o.IsMatch(p.Key) || o.IsMatch(p.Name))) continue;
            var parts = rule.In switch { "decl" => new[] { "decl" }, "both" => new[] { "decl", "impl" }, _ => new[] { "impl" } };
            var texts = parts.Select(pt =>
            {
                var raw = (pt == "decl" ? p.Decl : p.Impl) ?? "";
                return (Part: pt, Text: rule.KeepComments ? raw.Replace("\r\n", "\n") : Code(raw));
            }).ToList();
            if (texts.All(t => t.Text.Trim().Length == 0)) continue;
            var whole = string.Join("\n", texts.Select(t => t.Text));
            if (rule.UnlessInObject != null && rule.UnlessInObject.IsMatch(whole)) continue;
            if (rule.Require != null)
            {
                if (!rule.Require.IsMatch(whole)) AddRisk(r, rule.Severity, p.Key, 0, rule.Id, rule.Message, texts[0].Part);
                continue;
            }
            foreach (var (part, text) in texts)
            {
                var lines = text.Split('\n');
                var lineOf = LineMap(text);
                var seen = new HashSet<int>();
                foreach (Match m in rule.Find.Matches(text))
                {
                    var line = lineOf(m.Index);
                    if (!seen.Add(line)) continue;                                   // one finding per line
                    // unless: the line of the match and the whole statement (to the next ';') - calls often span several lines
                    if (rule.Unless != null)
                    {
                        var stmtStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                        var semi = text.IndexOf(';', m.Index);
                        var lineEnd = text.IndexOf('\n', m.Index);
                        var stmtEnd = Math.Max(semi < 0 ? text.Length : semi + 1, lineEnd < 0 ? text.Length : lineEnd);
                        if (rule.Unless.IsMatch(text[stmtStart..stmtEnd])) continue;
                    }
                    AddRisk(r, rule.Severity, p.Key, line, rule.Id, rule.Message, part);
                }
            }
        }
        return r;
    }
}

public sealed partial class ToolRegistry
{
    static readonly Regex IdRxTool = new(@"^[A-Za-z][\w-]*$");

    string RiskRulesTool(System.Text.Json.Nodes.JsonObject a)
    {
        var action = (Str(a, "action") ?? "show").ToLowerInvariant();
        var dir = SolutionDir();
        if (action == "show") return RiskConfig.Load(dir).Show(dir);

        // own rule from the arguments, as the lines of a "## Rule <id>" section
        string RuleBody()
        {
            var b = new StringBuilder();
            void Line(string key, string arg)
            {
                var v = Str(a, arg);
                if (string.IsNullOrWhiteSpace(v)) return;
                v = v.Replace("\r", " ").Replace("\n", " ").Trim();
                b.Append("- ").Append(key).Append(": ").AppendLine(key is "find" or "require" or "unless" or "unlessInObject" ? "`" + v + "`" : v);
            }
            Line("severity", "severity"); Line("find", "find"); Line("require", "require"); Line("unless", "unless");
            Line("unlessInObject", "unlessInObject"); Line("in", "in"); Line("kinds", "kinds"); Line("objects", "objects");
            Line("comments", "comments"); Line("message", "message"); Line("why", "why");
            return b.ToString().TrimEnd();
        }
        string Id() => Str(a, "id") is { } id && id.Trim().Length > 0
            ? (IdRxTool.IsMatch(id.Trim()) ? id.Trim() : throw new Exception($"invalid rule id '{id.Trim()}' (letters, digits, - and _, starting with a letter)"))
            : throw new Exception("id is missing (e.g. no-memcpy-without-sizeof)");

        switch (action)
        {
            case "test":
            {
                var probe = new RiskConfig();
                var id = Str(a, "id") is { } i && i.Trim().Length > 0 ? Id() : "test-rule";
                probe.Parse($"## Rule {id}\n{RuleBody()}\n", "(test)");
                if (probe.Errors.Count > 0) return "Rule invalid:\n  " + string.Join("\n  ", probe.Errors);
                var rule = probe.Customs.Values.FirstOrDefault() ?? throw new Exception("rule could not be parsed (check id, find/require)");
                var doc = BuildDoc(Str(a, "plcProjectPath"));
                List<PlcDoc.Risk> hits;
                try { hits = doc.CustomRisks(rule); }
                catch (RegexMatchTimeoutException) { return "Regular expression too slow (timeout) - simplify it."; }
                var max = Math.Max(1, Int(a, "max", 50));
                var o = new StringBuilder($"TEST {id} ({rule.Describe()}) on {doc.Project}: {hits.Count} findings - nothing saved\n");
                foreach (var h in hits.OrderBy(x => x.Object, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Line).Take(max)) o.AppendLine(h.ToString());
                if (hits.Count > max) o.AppendLine($"... {hits.Count - max} more");
                o.AppendLine(hits.Count == 0
                    ? "No findings - check the pattern against code that should match (tc_plc_search with the same regex)."
                    : "Check some findings with tc_plc_read: false positives -> narrow with unless/kinds/objects; then action=add with the same arguments.");
                return o.ToString();
            }
            case "add":
            {
                var (file, res) = RiskConfig.Add(dir, Str(a, "scope"), "Rule " + Id(), RuleBody(), DryRun(a));
                return $"{res}\nFile: {file}\nIt applies to the next tc_doc action=risks / doc --risks.";
            }
            case "builtin":
            {
                var rule = Str(a, "rule") ?? throw new Exception("rule is missing (built-in rule id, e.g. long-code)");
                var setting = Str(a, "setting") ?? throw new Exception("setting is missing (off, on, severity=..., limit=...)");
                var (file, res) = RiskConfig.Add(dir, Str(a, "scope"), "Built-in rules", $"- {rule.Trim()}: {setting.Trim()}", DryRun(a));
                return $"{res}\nFile: {file}" +
                       "\nIf the rule was set before, the later line in the file wins - remove the older one in the file if it is confusing.";
            }
            case "check":
            {
                var text = Str(a, "text") ?? throw new Exception("text is missing (the check as one sentence)");
                var (file, res) = RiskConfig.Add(dir, Str(a, "scope"), "Checks", "- " + text.Replace("\r", " ").Replace("\n", " ").Trim(), DryRun(a));
                return $"{res}\nFile: {file}";
            }
            default: throw new Exception("action: show, test, add, builtin or check");
        }
    }
}
