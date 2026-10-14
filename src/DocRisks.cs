// Static risk analysis of the ST code of a PLC project (tc_doc action=risks, section "Risks" of the HTML page,
// summary in tc_doc action=outline, CLI: TcAgent.exe doc --risks [--fail-on high]).
//
// Text based like a reviewer's first pass over the code that PlcDoc already has (declarations, implementations, call
// graph, tasks) - no compiler. The rules are tuned for few false positives: a rule reports only what it can resolve
// (declared variables of the object, its FB, base FBs or GVLs); unknown identifiers (library constants, enum values) are
// never reported. Every rule is documented in RiskRules (shown with the results and in docs/TOOLS.md).
// Line numbers: line in the implementation of the object (as tc_plc_read shows it), "(decl)" = in the declaration.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed partial class PlcDoc
{
    public sealed class Risk
    {
        public string Severity, Object, Rule, Text;
        public int Line;                                          // 1-based; 0 = the whole object
        public string Part = "impl";                              // impl or decl
        public int Rank => SeverityRank(Severity);
        public override string ToString() =>
            $"{Severity.ToUpperInvariant(),-6} {Object}{(Line > 0 ? $" line {Line}{(Part == "decl" ? " (decl)" : "")}" : "")}  [{Rule}] {Text}";
    }

    public static int SeverityRank(string s) => (s ?? "").ToLowerInvariant() switch { "high" => 0, "medium" => 1, "low" => 2, _ => 3 };

    public static string RiskCounts(List<Risk> rk) =>
        $"{rk.Count(x => x.Severity == "high")} high, {rk.Count(x => x.Severity == "medium")} medium, {rk.Count(x => x.Severity == "low")} low";

    public static readonly (string Id, string Severity, string Detail)[] RiskRules =
    {
        ("div-zero", "high/medium", "a / b or a MOD b where b is a declared variable (no literal, no constant) and the object neither compares b " +
                                    "(b <> 0, b > 0, IF b = 0 ...) nor assigns it from MAX/LIMIT/SEL. High for integer and TIME types (division by zero " +
                                    "raises an exception and stops the PLC), medium for REAL/LREAL and members of structures (INF/NaN spread)."),
        ("array-index", "high/medium", "arr[i] where arr is declared as ARRAY and the index contains a variable that is no FOR loop variable, no " +
                                       "constant and is neither compared (i < .., i <= ..) nor limited (LIMIT/MIN/MAX/MOD) anywhere in the object. High for " +
                                       "writes (arr[i] := ..: overwrites foreign memory), medium for reads. Off when the project has CheckBounds."),
        ("pointer-deref", "high/medium", "p^ where p is POINTER TO and the FB/object never checks p <> 0 / p = 0 (high); a REFERENCE TO variable used " +
                                         "without __ISVALIDREF in the FB/object (medium). Invalid pointers cause page faults (PLC stop)."),
        ("loop-exit", "medium", "WHILE/REPEAT loop whose body has no EXIT/RETURN and neither assigns nor calls a variable of the loop condition " +
                                "(WHILE TRUE without EXIT always). Endless loops stop the task (watchdog / cycle overrun). Conditions with function calls are skipped."),
        ("global-multi-task", "high", "A global variable (GVL or variable of a PROGRAM) is written by objects reached from more than one task " +
                                      "(task -> program -> calls through FBs, methods, functions). Unsynchronized writes from tasks with different priority " +
                                      "give inconsistent data: write it in one task only or use a lock / data exchange."),
        ("output-multi-write", "medium", "A physical output (AT %Q in a PROGRAM or GVL) is written in more than one object (program, action, method, FB): " +
                                         "the last writer in the cycle wins. Write each output in one place."),
        ("unused-var", "low", "Local variables (VAR, VAR_TEMP, VAR_STAT, VAR_INST) never used and inputs (VAR_INPUT, VAR_IN_OUT) never read in the object - " +
                              "for PROGRAM/FB including its methods, actions, properties and derived FBs. Skipped: AT-mapped variables, variables with " +
                              "an {attribute}, instances of FBs that extend another FB (e.g. TcUnit suites), graphical code. Program variables may be " +
                              "used by HMI/ADS."),
        ("timer-in-method", "high", "Timer/counter/edge FB (TON, TOF, TP, R_TRIG, F_TRIG, CTU ...) declared in VAR/VAR_TEMP of a METHOD or FUNCTION: " +
                                    "re-initialized on every call, it never elapses or detects an edge. Declare it in the FB or in VAR_INST."),
        ("timer-conditional", "medium", "Timer/edge FB instance that is only called inside IF/CASE/loops: while it is not called it keeps its state " +
                                        "(a TON stays Q=TRUE, an R_TRIG misses edges). Call it every cycle and put the condition into IN/CLK."),
        ("case-else", "low", "CASE without ELSE - unexpected values are ignored silently."),
        ("magic-number", "low", "CASE with 3 or more numeric labels (state machine with numbers) - use an ENUM for the states."),
        ("long-code", "low/medium", "Implementation longer than 300 code lines (POU) or 150 (method, action, property): low; more than twice: medium."),
        ("deep-nesting", "low/medium", "IF/CASE/FOR/WHILE/REPEAT nested deeper than 5 levels (low) or 7 levels (medium)."),
    };

    List<Risk> risks;
    HashSet<string> constants, pointerSeen;

    /// <summary>All risks, most severe first (computed once).</summary>
    public List<Risk> Risks()
    {
        if (risks != null) return risks;
        var r = new List<Risk>();
        var code = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values) code[p.Key] = Code(p.Impl);
        constants = Constants();
        pointerSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundsCheck = Pous.ContainsKey("CheckBounds");
        foreach (var p in Pous.Values)
        {
            var c = code[p.Key];
            if (p.Kind is not ("DUT" or "GVL" or "INTERFACE") && c.Trim().Length > 0)
            {
                try
                {
                    DivZero(p, c, r);
                    if (!boundsCheck) ArrayIndex(p, c, r);
                    Pointers(p, c, code, r);
                    Loops(p, c, r);
                    CaseRules(p, c, r);
                    Size(p, c, r);
                }
                catch (Exception ex) { Log.Info($"risks {p.Key}: {ex.Message}"); }   // one odd object must not stop the analysis
            }
            TimerDecl(p, r);
            try { TimerCalls(p, code, r); } catch (Exception ex) { Log.Info($"risks {p.Key}: {ex.Message}"); }
        }
        try { UnusedVars(code, r); } catch (Exception ex) { Log.Info("risks unused-var: " + ex.Message); }
        var writes = new Dictionary<string, List<(string Target, int Line)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values) { try { writes[p.Key] = Writes(p, code[p.Key]); } catch { writes[p.Key] = new(); } }
        MultiTask(writes, r);
        OutputWriters(writes, r);
        ApplyRiskConfig(r);
        risks = r.OrderBy(x => x.Rank).ThenBy(x => x.Object, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Line).ToList();
        return risks;
    }

    /// <summary>Text for tc_doc action=risks.</summary>
    public string RisksText(string minSeverity, int max, string filter)
    {
        var all = Risks();
        var min = string.IsNullOrEmpty(minSeverity) ? 1 : Math.Min(2, SeverityRank(minSeverity));   // default: high + medium, low only on request
        var list = all.Where(x => x.Rank <= min && (string.IsNullOrEmpty(filter) || x.Object.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                    x.Rule.Equals(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        var o = new StringBuilder($"Risks in {Project}: {RiskCounts(all)} (static, text based rules - verify each one in the code with tc_plc_read; " +
                                  "line = line in the implementation, (decl) = declaration)\n");
        if (Pous.ContainsKey("CheckBounds") && !(RiskConfig ?? RiskConfig.Empty).IsOff("array-index")) o.AppendLine("Rule array-index is off: the project has an implicit CheckBounds function.");
        if (list.Count == 0) o.AppendLine(all.Count == 0 ? "Nothing found." : "Nothing matches the filter.");
        foreach (var x in list.Take(Math.Max(1, max))) o.AppendLine(x.ToString());
        if (list.Count > max) o.AppendLine($"... {list.Count - max} more (max, minSeverity or filter=<object or rule>)");
        if (string.IsNullOrEmpty(minSeverity) && all.Any(x => x.Rank == 2)) o.AppendLine($"({all.Count(x => x.Rank == 2)} low risks not listed - minSeverity=low shows them)");
        var cfg = RiskConfig ?? RiskConfig.Empty;
        var fired = new HashSet<string>(list.Select(x => x.Rule), StringComparer.OrdinalIgnoreCase);
        if (fired.Count > 0)
        {
            o.AppendLine("\nRules:");
            foreach (var rule in RiskRules.Where(x => fired.Contains(x.Id))) o.AppendLine($"  {rule.Id} ({rule.Severity}): {rule.Detail}");
            foreach (var rule in cfg.Customs.Values.Where(x => fired.Contains(x.Id)))
                o.AppendLine($"  {rule.Id} ({rule.Severity}, own rule from {Path.GetFileName(rule.Source)}): {rule.Message}{(rule.Why != null ? " - " + rule.Why : "")}");
        }
        if (cfg.Checks.Count > 0)
        {
            o.AppendLine("\nChecks for the agent from the risk rules (not machine checked - verify each one by reading the code, report violations like findings):");
            foreach (var (t, _) in cfg.Checks) o.AppendLine("  - " + t);
        }
        var off = RiskRules.Where(x => cfg.IsOff(x.Id)).Select(x => x.Id).ToList();
        if (off.Count > 0) o.AppendLine($"\nSwitched off by the risk rules: {string.Join(", ", off)}");
        if (cfg.Errors.Count > 0) o.AppendLine($"\nRisk rule files have {cfg.Errors.Count} errors (entries ignored) - tc_risk_rules shows them.");
        return o.ToString();
    }

    void RisksHtml(StringBuilder o)
    {
        var rk = Risks();
        o.Append("<section id=\"risks\"><h2>Risks</h2>");
        o.Append($"<p>{H(RiskCounts(rk))} <span class=\"muted\">– static, text based rules on the ST code; check each one in the code. " +
                 "Line = line in the implementation, (decl) = declaration.</span></p>");
        if (Pous.ContainsKey("CheckBounds") && !(RiskConfig ?? RiskConfig.Empty).IsOff("array-index")) o.Append("<p class=\"muted\">Rule array-index is off: the project has an implicit CheckBounds function.</p>");
        if (rk.Count > 0)
        {
            o.Append("<table class=\"risks\"><tr><th>Severity</th><th>Object</th><th>Line</th><th>Rule</th><th>Explanation</th></tr>");
            foreach (var x in rk.Take(1000))
                o.Append($"<tr><td><span class=\"sev {x.Severity}\">{x.Severity}</span></td><td>{Link(x.Object)}</td>" +
                         $"<td>{(x.Line > 0 ? x.Line + (x.Part == "decl" ? " (decl)" : "") : "")}</td><td><code>{H(x.Rule)}</code></td><td>{H(x.Text)}</td></tr>");
            o.Append("</table>");
            if (rk.Count > 1000) o.Append($"<p class=\"muted\">{rk.Count - 1000} more – tc_doc action=risks.</p>");
        }
        var cfg = RiskConfig ?? RiskConfig.Empty;
        o.Append("<details><summary>Rules</summary><ul>");
        foreach (var rule in RiskRules)
            o.Append($"<li><code>{H(rule.Id)}</code> ({H(cfg.IsOff(rule.Id) ? "off" : cfg.Builtins.TryGetValue(rule.Id, out var bs) && bs.Severity != null ? bs.Severity : rule.Severity)}): {H(rule.Detail)}</li>");
        foreach (var rule in cfg.Customs.Values.Where(x => !x.Off))
            o.Append($"<li><code>{H(rule.Id)}</code> ({H(rule.Severity)}, own rule): {H(rule.Message)}{(rule.Why != null ? " – " + H(rule.Why) : "")}</li>");
        o.Append("</ul></details>");
        if (cfg.Checks.Count > 0)
        {
            o.Append("<details><summary>Additional checks (reviewed by reading the code)</summary><ul>");
            foreach (var (t, _) in cfg.Checks) o.Append($"<li>{H(t)}</li>");
            o.Append("</ul></details>");
        }
        o.Append("</section>");
    }

    // ================================================================== helpers
    // code without comments, strings and pragmas - line structure is kept, so indexes map to lines of the implementation
    static string Code(string impl)
    {
        var s = StripStrings(StripComments((impl ?? "").Replace("\r\n", "\n")));
        return Regex.Replace(s, @"\{[^}]*\}", m => new string('\n', m.Value.Count(ch => ch == '\n')));
    }

    static int LineAt(string s, int idx)
    {
        int n = 1;
        for (int i = 0; i < idx && i < s.Length; i++) if (s[i] == '\n') n++;
        return n;
    }

    // fast line lookup for many positions in the same text
    static Func<int, int> LineMap(string s)
    {
        var nl = new List<int>();
        for (int i = 0; i < s.Length; i++) if (s[i] == '\n') nl.Add(i);
        return idx => { var k = nl.BinarySearch(idx); return (k < 0 ? ~k : k) + 1; };
    }

    static int DeclLine(string decl, string name)
    {
        var lines = (decl ?? "").Replace("\r\n", "\n").Split('\n');
        var rx = new Regex($@"^\s*(?:[A-Za-z_]\w*\s*,\s*)*{Regex.Escape(name)}\s*(?:,|:|AT\b)", RegexOptions.IgnoreCase);
        for (int i = 0; i < lines.Length; i++) if (rx.IsMatch(lines[i])) return i + 1;
        return 0;
    }

    static void AddRisk(List<Risk> r, string severity, string obj, int line, string rule, string text, string part = "impl") =>
        r.Add(new Risk { Severity = severity, Object = obj, Line = line, Rule = rule, Text = text, Part = part });

    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "XOR", "NOT", "MOD", "TRUE", "FALSE", "AND_THEN", "OR_ELSE", "THIS", "SUPER", "IF", "THEN", "ELSE", "ELSIF", "END_IF", "DO", "TO", "BY"
    };

    // identifiers (with dots) in an expression - no keywords, no typed literals (INT#5, E_State#Idle), no calls
    static List<string> Identifiers(string expr, bool withCalls = false)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(expr, @"(?<![\w.#])[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*"))
        {
            var end = m.Index + m.Length;
            while (end < expr.Length && char.IsWhiteSpace(expr[end])) end++;
            if (end < expr.Length && expr[end] == '#') continue;
            if (!withCalls && end < expr.Length && expr[end] == '(') continue;
            var id = Regex.Replace(m.Value, @"\s+", "");
            if (!Keywords.Contains(id)) list.Add(id);
        }
        return list;
    }

    // names declared in VAR CONSTANT / VAR_GLOBAL CONSTANT blocks (any object)
    HashSet<string> Constants()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values)
            foreach (Match b in Regex.Matches(StripComments(p.Decl), @"\bVAR(?:_GLOBAL)?\s+CONSTANT\b(.*?)\bEND_VAR\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                foreach (Match v in Regex.Matches(b.Groups[1].Value, @"^\s*([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*:", RegexOptions.Multiline))
                    foreach (var n in v.Groups[1].Value.Split(',')) set.Add(n.Trim());
        return set;
    }

    Pou Self(Pou p) => p.Owner != null && Pous.TryGetValue(p.Owner, out var o) ? o : p;

    // variable visible in an object: own declaration, its FB with base FBs, then globals -> (variable, key of the declaring object)
    (Var V, string Container) Lookup(Pou p, string name)
    {
        var v = p.Vars.FirstOrDefault(x => x.Section != "RETURN" && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (v != null) return (v, p.Key);
        foreach (var t in Chain(Self(p)))
        {
            v = t.Vars.FirstOrDefault(x => x.Section != "RETURN" && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (v != null) return (v, t.Key);
        }
        if (globals.TryGetValue(name, out var g) && Pous.TryGetValue(g.Gvl, out var gp))
        {
            v = gp.Vars.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (v != null) return (v, gp.Key);
        }
        return (null, null);
    }

    // GVL.var or PROGRAM.var
    (Var V, string Container) LookupQualified(string container, string name)
    {
        if (Pous.TryGetValue(container, out var c) && c.Owner == null && c.Kind is "GVL" or "PROGRAM")
        {
            var v = c.Vars.FirstOrDefault(x => x.Section != "RETURN" && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (v != null) return (v, c.Key);
        }
        return (null, null);
    }

    // root variable of an access path "a", "a.b", "a[i].b", "GVL.a.b" -> (variable, declaring object, whole path is the variable)
    (Var V, string Container, bool Exact) ResolveRoot(Pou p, string path)
    {
        var parts = Regex.Replace(path, @"\s*\[[^\]]*\]", "").Replace(" ", "").Replace("^", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[0].Equals("THIS", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];     // THIS^.x = own variable
        if (parts.Length == 0) return (null, null, false);
        if (parts.Length >= 2)
        {
            var q = LookupQualified(parts[0], parts[1]);
            if (q.V != null) return (q.V, q.Container, parts.Length == 2);
        }
        var l = Lookup(p, parts[0]);
        return (l.V, l.Container, parts.Length == 1);
    }

    // id is compared somewhere (id <> 0, id > n, 0 < id, IF id = 0 ...) - assignments (:=) and output bindings (=>) do not count
    static bool Compared(string code, string id)
    {
        var e = Regex.Escape(id).Replace(@"\.", @"\s*\.\s*");
        return Regex.IsMatch(code, $@"(?<![\w.]){e}(?![\w.])\s*(?:<>|<=|>=|<|>|=)(?!>)|(?:<>|<=|>=|<|[^=:<>\-]>|[^:<>]=)\s*{e}(?![\w.(])", RegexOptions.IgnoreCase);
    }

    static bool LimitedAssign(string code, string id)
    {
        var e = Regex.Escape(id).Replace(@"\.", @"\s*\.\s*");
        return Regex.IsMatch(code, $@"(?<![\w.]){e}\s*:=\s*(?:LIMIT|MIN|MAX|SEL)\s*\(|(?<![\w.]){e}\s*:=[^;]*\bMOD\b", RegexOptions.IgnoreCase);
    }

    // end of a block (index of the matching close keyword) - nested blocks of the same kind are counted
    static int MatchEnd(string code, int from, string open, string close)
    {
        int depth = 1;
        foreach (Match t in Regex.Matches(code[from..], $@"\b(?:{open}|{close})\b", RegexOptions.IgnoreCase))
        {
            depth += t.Value.Equals(close, StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            if (depth == 0) return from + t.Index;
        }
        return -1;
    }

    static readonly Regex ControlRx = new(@"\b(?:END_IF|END_CASE|END_FOR|END_WHILE|END_REPEAT|IF|CASE|FOR|WHILE|REPEAT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // number of open IF/CASE/FOR/WHILE/REPEAT blocks at idx
    static int ControlDepth(string code, int idx)
    {
        int depth = 0;
        foreach (Match t in ControlRx.Matches(code[..Math.Min(idx, code.Length)]))
            depth = t.Value.StartsWith("END_", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, depth - 1) : depth + 1;
        return depth;
    }

    static readonly Regex TimerType = new(@"^(?:L?TON|L?TOF|L?TP|L?TONR|R_TRIG|F_TRIG|L?CTU|L?CTD|L?CTUD)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static bool IsArray(string type) => Regex.IsMatch(type ?? "", @"^\s*ARRAY\b", RegexOptions.IgnoreCase);

    // ================================================================== rules
    static readonly Regex DivRx = new(@"(?:(?<![/*])/(?![/*])|\bMOD\b)\s*([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    void DivZero(Pou p, string c, List<Risk> r)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DivRx.Matches(c))
        {
            var end = m.Index + m.Length;
            while (end < c.Length && char.IsWhiteSpace(c[end])) end++;
            if (end < c.Length && (c[end] == '(' || c[end] == '#')) continue;           // function call, typed literal
            var op = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            if (Keywords.Contains(op) || constants.Contains(op.Split('.').Last())) continue;
            var (v, _, exact) = ResolveRoot(p, op);
            if (v == null || !seen.Add(op)) continue;                                      // only declared variables
            if (Compared(c, op) || LimitedAssign(c, op)) continue;
            var t = BareType(v.Type);
            var integer = exact && Regex.IsMatch(t, @"^(?:U?[SDL]?INT|BYTE|WORD|DWORD|LWORD|__U?XINT|L?TIME)$", RegexOptions.IgnoreCase);
            if (exact && !integer && !Regex.IsMatch(t, @"^L?REAL$", RegexOptions.IgnoreCase)) continue;   // structure, enum ... unknown
            AddRisk(r, integer ? "high" : "medium", p.Key, LineAt(c, m.Index), "div-zero",
                    $"{(m.Value.TrimStart().StartsWith("/") ? "Division" : "MOD")} by {op}{(exact ? $" ({v.Type})" : "")} without a zero check in the object" +
                    (integer ? " - division by zero stops the PLC" : " - result INF/NaN"));
        }
    }

    static readonly Regex IndexRx = new(@"(?<![\w.])([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)?)\s*\[([^\[\]]+)\]", RegexOptions.Compiled);
    static readonly Regex WriteAfterRx = new(@"\G\s*(?:\.\s*[A-Za-z_]\w*\s*(?:\[[^\]]*\]\s*)?)*(?::=|S=|R=)", RegexOptions.Compiled);

    void ArrayIndex(Pou p, string c, List<Risk> r)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in IndexRx.Matches(c))
        {
            var arr = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            var parts = arr.Split('.');
            var v = parts.Length == 2 ? LookupQualified(parts[0], parts[1]).V : Lookup(p, arr).V;
            if (v == null || !IsArray(v.Type)) continue;
            var idx = m.Groups[2].Value;
            if (Regex.IsMatch(idx, @"\b(?:LIMIT|MIN|MAX|MOD|SEL)\b", RegexOptions.IgnoreCase)) continue;
            var isWrite = WriteAfterRx.Match(c, m.Index + m.Length).Success;
            foreach (var id in Identifiers(idx))
            {
                var ip = id.Split('.');
                if (constants.Contains(ip.Last())) continue;
                if (ip.Length > 1 && Pous.TryGetValue(ip[0], out var dut) && dut.Kind == "DUT") continue;          // E_Axis.X
                var iv = ip.Length == 2 ? LookupQualified(ip[0], ip[1]).V : ip.Length == 1 ? Lookup(p, id).V : null;
                if (iv == null) continue;                                                                           // unknown: enum value, library constant
                if (Regex.IsMatch(c, $@"\bFOR\s+{Regex.Escape(id)}\s*:=", RegexOptions.IgnoreCase)) continue;
                if (Compared(c, id) || LimitedAssign(c, id)) continue;
                if (!seen.Add(arr + "|" + id + "|" + isWrite)) continue;
                AddRisk(r, isWrite ? "high" : "medium", p.Key, LineAt(c, m.Index), "array-index",
                        $"{arr}[{Regex.Replace(idx.Trim(), @"\s+", " ")}]{(isWrite ? " := (write)" : "")}: index {id} is not checked against the bounds of " +
                        $"{v.Type.Trim()} (no IF/LIMIT guard, no FOR loop variable)");
            }
        }
    }

    void Pointers(Pou p, string c, Dictionary<string, string> code, List<Risk> r)
    {
        var self = Self(p);
        var vars = p.Vars.Select(v => (v, p.Key)).Concat(Chain(self).SelectMany(t => t.Vars.Select(v => (v, t.Key))))
                         .Where(x => x.v.Section != "RETURN" && Regex.IsMatch(x.v.Type ?? "", @"^\s*(?:POINTER|REFERENCE)\s+TO\b", RegexOptions.IgnoreCase))
                         .GroupBy(x => x.v.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (vars.Count == 0) return;
        // a check anywhere in the FB (body, methods, actions, properties, base FBs) or in the object counts
        string family = null;
        string Family()
        {
            if (family != null) return family;
            var sb = new StringBuilder(c);
            foreach (var t in Chain(self))
            {
                sb.Append('\n').Append(code.TryGetValue(t.Key, out var x) ? x : "");
                foreach (var mk in t.Members) sb.Append('\n').Append(code.TryGetValue(mk, out var y) ? y : "");
            }
            return family = sb.ToString();
        }
        foreach (var (v, _) in vars)
        {
            var e = Regex.Escape(v.Name);
            if (Regex.IsMatch(v.Type, @"^\s*POINTER", RegexOptions.IgnoreCase))
            {
                var d = Regex.Match(c, $@"(?<![\w.]){e}\s*\^", RegexOptions.IgnoreCase);
                if (!d.Success) continue;
                if (Regex.IsMatch(Family(), $@"(?<![\w.]){e}\s*(?:<>|=|>)\s*0\b|\b0\s*(?:<>|=|<)\s*{e}(?![\w.])|__ISVALIDREF\s*\(\s*{e}\b", RegexOptions.IgnoreCase)) continue;
                if (!pointerSeen.Add(self.Key + "." + v.Name)) continue;                     // once per FB, not per method
                AddRisk(r, "high", p.Key, LineAt(c, d.Index), "pointer-deref", $"{v.Name}^: {v.Name} ({v.Type.Trim()}) is dereferenced without a check {v.Name} <> 0");
            }
            else
            {
                var u = Regex.Matches(c, $@"(?<![\w.]){e}(?!\w)(?!\s*REF=)", RegexOptions.IgnoreCase).Cast<Match>()
                             .FirstOrDefault(x => !Regex.IsMatch(c[..x.Index], @"__ISVALIDREF\s*\(\s*$", RegexOptions.IgnoreCase));
                if (u == null) continue;
                if (Regex.IsMatch(Family(), $@"__ISVALIDREF\s*\(\s*{e}\s*\)", RegexOptions.IgnoreCase)) continue;
                if (!pointerSeen.Add(self.Key + "." + v.Name)) continue;
                AddRisk(r, "medium", p.Key, LineAt(c, u.Index), "pointer-deref", $"{v.Name} ({v.Type.Trim()}) is used without __ISVALIDREF({v.Name})");
            }
        }
    }

    void Loops(Pou p, string c, List<Risk> r)
    {
        foreach (Match m in Regex.Matches(c, @"\bWHILE\b(.*?)\bDO\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var end = MatchEnd(c, m.Index + m.Length, "WHILE", "END_WHILE");
            if (end < 0) continue;
            LoopCheck(p, c, m.Groups[1].Value, c[(m.Index + m.Length)..end], m.Index, "WHILE", r);
        }
        foreach (Match m in Regex.Matches(c, @"\bREPEAT\b", RegexOptions.IgnoreCase))
        {
            var end = MatchEnd(c, m.Index + m.Length, "REPEAT", "END_REPEAT");
            if (end < 0) continue;
            var seg = c[(m.Index + m.Length)..end];
            var u = Regex.Matches(seg, @"\bUNTIL\b", RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
            if (u == null) continue;
            LoopCheck(p, c, seg[(u.Index + u.Length)..], seg[..u.Index], m.Index, "REPEAT", r);
        }
    }

    static void LoopCheck(Pou p, string c, string cond, string body, int at, string kind, List<Risk> r)
    {
        if (Regex.IsMatch(body, @"\b(?:EXIT|RETURN)\b", RegexOptions.IgnoreCase)) return;
        if (Regex.Matches(cond, @"([A-Za-z_]\w*)\s*\(").Cast<Match>().Any(x => !Keywords.Contains(x.Groups[1].Value))) return;   // function call: unknown
        foreach (var id in Identifiers(cond, true))
        {
            var first = Regex.Escape(id.Split('.')[0]);
            if (Regex.IsMatch(body, $@"(?<![\w.]){first}(?:\s*(?:\^|\[[^\]]*\]|\.\s*\w+))*\s*(?::=|S=|R=|REF=)" +     // assigned
                                    $@"|(?<![\w.]){first}(?:\s*\.\s*\w+)*\s*\(" +                                  // called (instance, method)
                                    $@"|=>\s*{first}\b" +                                                          // output binding
                                    $@"|\w\s*\([^;]*(?<![\w.]){first}\b", RegexOptions.IgnoreCase))                // passed to a call
                return;
        }
        var cnd = Regex.Replace(cond, @"\s+", " ").Trim();
        AddRisk(r, "medium", p.Key, LineAt(c, at), "loop-exit",
                $"{kind} loop (condition {Trim(cnd, 60)}) without EXIT/RETURN and without a change of its condition in the loop body - possible endless loop (watchdog / cycle overrun)");
    }

    void TimerDecl(Pou p, List<Risk> r)
    {
        if (p.Kind is not ("METHOD" or "FUNCTION")) return;
        foreach (var v in p.Vars.Where(v => v.Section is "VAR" or "VAR_TEMP" && TimerType.IsMatch(BareType(v.Type))))
            AddRisk(r, "high", p.Key, DeclLine(p.Decl, v.Name), "timer-in-method",
                    $"{v.Name} : {v.Type.Trim()} is declared in VAR of a {p.Kind.ToLowerInvariant()} - re-initialized on every call; declare it in the FB or in VAR_INST", "decl");
    }

    void TimerCalls(Pou p, Dictionary<string, string> code, List<Risk> r)
    {
        if (p.Owner != null) return;                                                  // handled with the owner (all calls of the FB)
        var family = new List<Pou> { p };
        family.AddRange(p.Members.Select(k => Pous.TryGetValue(k, out var m) ? m : null).Where(m => m != null));
        var timers = p.Vars.Where(v => v.Section is "VAR" or "VAR_STAT" or "VAR_INST" && !IsArray(v.Type) && TimerType.IsMatch(BareType(v.Type))).ToList();
        foreach (var v in timers)
        {
            var rx = new Regex($@"(?<![\w.]){Regex.Escape(v.Name)}\s*\(", RegexOptions.IgnoreCase);
            Pou firstObj = null; int firstLine = 0; bool any = false, unconditional = false;
            foreach (var f in family)
            {
                if (!code.TryGetValue(f.Key, out var fc)) continue;
                foreach (Match m in rx.Matches(fc))
                {
                    any = true;
                    if (ControlDepth(fc, m.Index) == 0) { unconditional = true; break; }
                    if (firstObj == null) { firstObj = f; firstLine = LineAt(fc, m.Index); }
                }
                if (unconditional) break;
            }
            if (!any || unconditional || firstObj == null) continue;
            AddRisk(r, "medium", firstObj.Key, firstLine, "timer-conditional",
                    $"{v.Name} ({v.Type.Trim()}) is only called inside IF/CASE/loops - it keeps its state while not called; call it every cycle and put the condition into IN/CLK");
        }
    }

    static readonly Regex LabelRx = new(@"^[ \t]*((?:[A-Za-z_][\w.#]*|-?\d+(?:\.\.-?\d+)?)(?:\s*,\s*(?:[A-Za-z_][\w.#]*|-?\d+(?:\.\.-?\d+)?))*)\s*:(?!=)", RegexOptions.Multiline | RegexOptions.Compiled);

    void CaseRules(Pou p, string c, List<Risk> r)
    {
        foreach (Match m in Regex.Matches(c, @"\bCASE\b(.+?)\bOF\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var start = m.Index + m.Length;
            var end = MatchEnd(c, start, "CASE", "END_CASE");
            if (end < 0) continue;
            // nested CASE blocks blanked (their labels and ELSE belong to them)
            var body = new StringBuilder(c[start..end]);
            int depth = 0, nestStart = 0;
            foreach (Match t in Regex.Matches(c[start..end], @"\b(?:CASE|END_CASE)\b", RegexOptions.IgnoreCase))
            {
                if (t.Value.Equals("CASE", StringComparison.OrdinalIgnoreCase)) { if (depth++ == 0) nestStart = t.Index; }
                else if (depth > 0 && --depth == 0)
                    for (int i = nestStart; i < t.Index + t.Length; i++) if (body[i] != '\n') body[i] = ' ';
            }
            var masked = body.ToString();
            var line = LineAt(c, m.Index);
            var sel = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
            if (CaseElse(masked) >= masked.Length)
                AddRisk(r, "low", p.Key, line, "case-else", $"CASE {Trim(sel, 40)} has no ELSE - unexpected values are ignored silently");
            var numeric = LabelRx.Matches(masked).Cast<Match>().Count(l => Regex.IsMatch(l.Groups[1].Value, @"^-?\d"));
            if (numeric >= (RiskConfig ?? RiskConfig.Empty).Param("magic-number", "labels"))
                AddRisk(r, "low", p.Key, line, "magic-number", $"CASE {Trim(sel, 40)} uses {numeric} numeric state labels - use an ENUM for the states");
        }
    }

    void Size(Pou p, string c, List<Risk> r)
    {
        var lines = c.Split('\n').Count(l => l.Trim().Length > 0);
        var cfg = RiskConfig ?? RiskConfig.Empty;
        var limit = p.Owner == null ? cfg.Param("long-code", "limit") : cfg.Param("long-code", "memberLimit");
        if (lines > limit)
            AddRisk(r, lines > 2 * limit ? "medium" : "low", p.Key, 0, "long-code",
                    $"{lines} code lines (limit {limit}) - hard to review and test; split into methods/actions");
        int depth = 0, max = 0, at = 0;
        foreach (Match t in ControlRx.Matches(c))
        {
            if (t.Value.StartsWith("END_", StringComparison.OrdinalIgnoreCase)) depth = Math.Max(0, depth - 1);
            else if (++depth > max) { max = depth; at = t.Index; }
        }
        int nestLow = cfg.Param("deep-nesting", "limit"), nestMedium = cfg.Param("deep-nesting", "mediumLimit");
        if (max > nestLow)
            AddRisk(r, max > nestMedium ? "medium" : "low", p.Key, LineAt(c, at), "deep-nesting", $"nesting depth {max} (IF/CASE/FOR/WHILE/REPEAT) - extract methods or use early RETURN");
    }

    void UnusedVars(Dictionary<string, string> code, List<Risk> r)
    {
        var derived = Pous.Values.Where(x => x.Owner == null && x.Extends != null)
                          .GroupBy(x => x.Extends, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var allCode = string.Join("\n", code.Values);
        foreach (var p in Pous.Values.Where(x => x.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "METHOD"))
        {
            if (p.Owner != null && Pous.TryGetValue(p.Owner, out var ow) && ow.Kind == "INTERFACE") continue;
            // the object, for PROGRAM/FB with members and derived FBs
            var family = new List<Pou> { p };
            if (p.Owner == null)
            {
                var queue = new Queue<Pou>(new[] { p });
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p.Key };
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    foreach (var mk in t.Members) if (Pous.TryGetValue(mk, out var m)) family.Add(m);
                    if (derived.TryGetValue(t.Name, out var ds)) foreach (var d in ds) if (seen.Add(d.Key)) { family.Add(d); queue.Enqueue(d); }
                }
            }
            // graphical code (FBD, LD, CFC) is not readable: variables may be used there
            if (family.Any(f => f.Kind is not ("PROPERTY" or "TRANSITION") && f.SfcText == null && (f.Lang is not "ST") && (code.TryGetValue(f.Key, out var fc) ? fc : "").Trim().Length == 0))
                continue;
            if (family.All(f => (code.TryGetValue(f.Key, out var fc2) ? fc2 : "").Trim().Length == 0 && f.SfcText == null)) continue;   // no code at all (abstract, empty)
            var text = string.Join("\n", family.Select(f => (code.TryGetValue(f.Key, out var x) ? x : "") + "\n" + (f.SfcText ?? "")));
            var decl = StripComments(p.Decl ?? "");
            var declLines = (p.Decl ?? "").Replace("\r\n", "\n").Split('\n');
            var sections = p.Owner == null ? new[] { "VAR", "VAR_TEMP", "VAR_STAT", "VAR_INST", "VAR_INPUT", "VAR_IN_OUT" } : new[] { "VAR", "VAR_TEMP", "VAR_STAT", "VAR_INST" };
            var unusedLocals = new List<string>(); var unreadInputs = new List<string>(); int firstLine = 0;
            foreach (var v in p.Vars.Where(v => sections.Contains(v.Section)))
            {
                var e = Regex.Escape(v.Name);
                if (Regex.IsMatch(text, $@"\b{e}\b", RegexOptions.IgnoreCase)) continue;                      // also THIS^.x, other.x
                if (Regex.Matches(decl, $@"(?<![\w.]){e}\b", RegexOptions.IgnoreCase).Count > 1) continue;             // used in another declaration
                if (Regex.IsMatch(decl, $@"(?<![\w.]){e}\s+AT\s+%", RegexOptions.IgnoreCase)) continue;                // mapped to I/O
                if (p.Kind == "PROGRAM" && Regex.IsMatch(allCode, $@"(?<![\w.]){Regex.Escape(p.Name)}\s*\.\s*{e}\b", RegexOptions.IgnoreCase)) continue;
                if (TypeOf(v.Type) is { Kind: "FUNCTION_BLOCK" } ft && ft.Extends != null) continue;                  // TcUnit suites, framework FBs
                if (Regex.IsMatch(v.Type ?? "", "TestSuite", RegexOptions.IgnoreCase)) continue;
                var ln = DeclLine(p.Decl, v.Name);
                if (ln > 1 && Regex.IsMatch(declLines[ln - 2], @"^\s*\{", RegexOptions.None)) continue;                // {attribute ...} above
                if (firstLine == 0) firstLine = ln;
                (v.Section is "VAR_INPUT" or "VAR_IN_OUT" ? unreadInputs : unusedLocals).Add(v.Name);
            }
            if (unusedLocals.Count + unreadInputs.Count == 0) continue;
            var parts = new List<string>();
            if (unusedLocals.Count > 0) parts.Add($"never used: {string.Join(", ", unusedLocals.Take(15))}{(unusedLocals.Count > 15 ? " ..." : "")}");
            if (unreadInputs.Count > 0) parts.Add($"inputs never read: {string.Join(", ", unreadInputs.Take(15))}{(unreadInputs.Count > 15 ? " ..." : "")}");
            AddRisk(r, "low", p.Key, firstLine, "unused-var", string.Join("; ", parts) + (p.Kind == "PROGRAM" ? " (may be used by HMI/ADS)" : ""), "decl");
        }
    }

    // ------------------------------------------------------------------ writes, tasks, outputs
    static readonly Regex AssignRx = new(@"(?<![\w.#])([A-Za-z_]\w*(?:\s*\[[^\]]*\])?(?:\s*(?:\^\s*)?\.\s*[A-Za-z_]\w*(?:\s*\[[^\]]*\])?)*)\s*(?::=|\bS=|\bR=|\bREF=)", RegexOptions.Compiled);
    static readonly Regex OutBindRx = new(@"=>\s*([A-Za-z_]\w*(?:\s*\[[^\]]*\])?(?:\s*\.\s*[A-Za-z_]\w*(?:\s*\[[^\]]*\])?)*)", RegexOptions.Compiled);

    // variables written by an object, as "<declaring object>.<variable>" (only the root variable of a structure/array access)
    List<(string Target, int Line)> Writes(Pou p, string c)
    {
        var list = new List<(string, int)>();
        var lineOf = LineMap(c);
        void Hit(string path, int idx)
        {
            var (v, cont, _) = ResolveRoot(p, path);
            if (v != null && v.Section != "VAR_TEMP") list.Add((cont + "." + v.Name, lineOf(idx)));
        }
        foreach (Match m in AssignRx.Matches(c))
        {
            int k = m.Index - 1;
            while (k >= 0 && char.IsWhiteSpace(c[k])) k--;
            if (k >= 0 && (c[k] == '(' || c[k] == ',')) continue;                    // named parameter in a call: fb(IN := x)
            Hit(m.Groups[1].Value, m.Index);
        }
        foreach (Match m in OutBindRx.Matches(c)) Hit(m.Groups[1].Value, m.Index);
        return list;
    }

    // objects executed by a task: its programs and everything they call (FB bodies, methods, actions, functions)
    HashSet<string> Reach(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            if (!Pous.TryGetValue(k, out var p) || !seen.Add(p.Key)) continue;
            foreach (var c in p.Calls) stack.Push(c);
        }
        return seen;
    }

    void MultiTask(Dictionary<string, List<(string Target, int Line)>> writes, List<Risk> r)
    {
        if (Tasks.Count < 2) return;
        var byTarget = new Dictionary<string, SortedDictionary<string, (string Obj, int Line)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tasks)
            foreach (var k in Reach(t.Value).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                foreach (var (target, line) in writes.TryGetValue(k, out var w) ? w : new List<(string Target, int Line)>())
                {
                    var cont = target[..target.LastIndexOf('.')];
                    if (!Pous.TryGetValue(cont, out var cp) || cp.Kind is not ("GVL" or "PROGRAM")) continue;
                    if (!byTarget.TryGetValue(target, out var tasks)) byTarget[target] = tasks = new(StringComparer.OrdinalIgnoreCase);
                    if (!tasks.ContainsKey(t.Key)) tasks[t.Key] = (k, line);
                }
        foreach (var (target, tasks) in byTarget.Where(x => x.Value.Count > 1).OrderBy(x => x.Key))
        {
            var first = tasks.First().Value;
            AddRisk(r, "high", first.Obj, first.Line, "global-multi-task",
                    $"{target} is written in {tasks.Count} tasks: {string.Join(", ", tasks.Select(x => $"{x.Key} ({x.Value.Obj})"))} - " +
                    "unsynchronized writes from tasks with different priority; write it in one task only or protect the access");
        }
    }

    void OutputWriters(Dictionary<string, List<(string Target, int Line)>> writes, List<Risk> r)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pous.Values.Where(p => p.Owner == null && p.Kind is "PROGRAM" or "GVL"))
            foreach (Match m in Regex.Matches(StripComments(p.Decl), @"^\s*([A-Za-z_]\w*)\s+AT\s+%Q", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (p.Vars.FirstOrDefault(v => v.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { } ov) outputs.Add(p.Key + "." + ov.Name);
        if (outputs.Count == 0) return;
        var writers = new Dictionary<string, SortedDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (obj, list) in writes)
            foreach (var (target, line) in list)
            {
                if (!outputs.Contains(target)) continue;
                if (!writers.TryGetValue(target, out var objs)) writers[target] = objs = new(StringComparer.OrdinalIgnoreCase);
                if (!objs.ContainsKey(obj)) objs[obj] = line;
            }
        foreach (var (target, objs) in writers.Where(x => x.Value.Count > 1).OrderBy(x => x.Key))
        {
            var first = objs.First();
            AddRisk(r, "medium", first.Key, first.Value, "output-multi-write",
                    $"output {target} (AT %Q) is written in {objs.Count} objects: {string.Join(", ", objs.Keys)} - the last writer in the cycle wins; write it in one place");
        }
    }
}
