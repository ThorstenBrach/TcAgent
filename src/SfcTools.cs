// SFC chain maintenance on top of tc_sfc's PLCopen write path (Sfc.cs):
//   renumber  systematic step names (prefix + zero-padded number), branch-aware order, edited in place in the export:
//             step names, jump targets, step references in conditions and in the POU's action/transition code, embedded
//             step actions _a<old>_xxx -> _a<new>_xxx (object name and step attribute together); everything else of the
//             chart (comments, times, embedded actions, layout data) stays as it is
//   comments  step comments with the project's coding guidelines (CodingRules) for the agent; sets the Comment attribute
//   flags     SFC flags (SFCInit, SFCReset ...): VSProperties of the POU (ProduceXml/ConsumeXml) when TwinCAT offers them,
//             otherwise the SFCSettings of the PLCopen export (re-import). An enabled flag must be declared: Declare=true
//             (TwinCAT declares it implicitly) or a variable in the declaration - else the build fails
//             ("SFC flag variable 'SFCInit' is not defined").
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    // ================================================================== step order
    /// <summary>Step names in the order used for renumbering: main = the sequence first, then its branches (recursively the same rule);
    /// chain = the order of action=read. Steps not reached from the initial step come last.</summary>
    static List<string> SfcStepOrder(XElement sfc, string order, out string note)
    {
        note = null;
        var names = new List<string>();
        try
        {
            var root = ParseChain(SfcToText(sfc));
            void Main(List<SfcNode> seq)
            {
                foreach (var n in seq) if (n.Kind == "step") names.Add(n.Name);
                foreach (var n in seq) foreach (var br in n.Branches) Main(br);
            }
            void Chain(List<SfcNode> seq)
            {
                foreach (var n in seq) { if (n.Kind == "step") names.Add(n.Name); foreach (var br in n.Branches) Chain(br); }
            }
            if (order == "chain") Chain(root); else Main(root);
        }
        catch (Exception ex)
        {
            names.Clear();
            names.AddRange(Regex.Matches(SfcToText(sfc), @"^\s*STEP\s+(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
            note = "the chart cannot be read as a structured chain (" + ex.Message + ") - steps in the order of action=read";
        }
        foreach (var st in sfc.Elements().Where(e => e.Name.LocalName == "step"))
            if ((string)st.Attribute("name") is { } nm && !names.Contains(nm, StringComparer.OrdinalIgnoreCase)) names.Add(nm);
        return names;
    }

    /// <summary>"N020,N030" or "N020..N070" (inclusive, in the given order); null/empty = all.</summary>
    static List<string> SelectSteps(List<string> all, string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return all.ToList();
        string Find(string s) => all.FirstOrDefault(x => x.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase))
                                 ?? throw new Exception($"steps: there is no step '{s.Trim()}' (steps: {string.Join(", ", all)})");
        if (spec.Contains(".."))
        {
            var r = spec.Split("..", 2);
            int i1 = all.IndexOf(Find(r[0])), i2 = all.IndexOf(Find(r[1]));
            if (i1 > i2) throw new Exception($"steps: {r[0].Trim()} comes after {r[1].Trim()} in this order ({string.Join(", ", all)})");
            return all.GetRange(i1, i2 - i1 + 1);
        }
        var picked = spec.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(Find)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(picked.Contains).ToList();        // always in the chosen order
    }

    static string CommonStepPrefix(IEnumerable<string> steps) =>
        steps.Select(s => Regex.Match(s, @"^([A-Za-z_][A-Za-z0-9_]*?)(\d+)$")).Where(m => m.Success).Select(m => m.Groups[1].Value)
             .GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    static HashSet<string> PouVariables(XElement pou) =>
        new(pou.Elements().Where(e => e.Name.LocalName == "interface").Descendants()
               .Where(e => e.Name.LocalName == "variable").Select(e => (string)e.Attribute("name")).Where(n => n != null),
            StringComparer.OrdinalIgnoreCase);

    static List<XElement> PouObjects(XElement pou) =>
        pou.Descendants().Where(e => e.Name.LocalName is "action" or "transition" && e.Parent?.Name.LocalName is "actions" or "transitions").ToList();

    // ================================================================== renumber
    string SfcRenumber(SfcCtx c, JsonObject a)
    {
        var original = new XDocument(c.Doc);
        var order = (Str(a, "order") ?? "chain").ToLowerInvariant();   // default: reading order of action=read
        if (order is not ("main" or "chain")) throw new Exception("order: main (main sequence first, then the branches) or chain (order of action=read)");
        var all = SfcStepOrder(c.Sfc, order, out var orderNote);
        if (all.Count == 0) throw new Exception("the chart has no steps");
        var sel = SelectSteps(all, Str(a, "steps"));
        if (sel.Count == 0) throw new Exception("steps: no step selected");
        // default start: the number of the first selected step (N000 stays N000), otherwise 10
        var firstNo = Regex.Match(sel[0], @"(\d+)$");
        int start = Int(a, "start", firstNo.Success && int.TryParse(firstNo.Groups[1].Value, out var fn) ? fn : 10), inc = Int(a, "step", 10), digits = Int(a, "digits", 3);
        if (start < 0 || inc < 1 || digits < 1 || digits > 9) throw new Exception("start >= 0, step >= 1, digits 1..9");
        var prefix = Str(a, "prefix");
        if (string.IsNullOrEmpty(prefix))
            prefix = CommonStepPrefix(sel) ?? throw new Exception("the steps are not named <prefix><number> - give prefix, e.g. prefix=S_ or prefix=N");
        if (!Regex.IsMatch(prefix, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception($"prefix '{prefix}' is not a valid identifier start");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sel.Count; i++)
        {
            var nn = prefix + (start + (long)i * inc).ToString("D" + digits);
            if (!nn.Equals(sel[i], StringComparison.Ordinal)) map[sel[i]] = nn;
        }
        if (map.Count == 0) return "Nothing to rename: the steps already have these names.";

        // collisions: steps that keep their names, variables, action/transition objects, keywords
        var keepNames = all.Where(s => !map.ContainsKey(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = map.Where(kv => keepNames.Contains(kv.Value)).Select(kv => $"{kv.Key} -> {kv.Value}").ToList();
        if (clash.Count > 0)
            throw new Exception($"Nothing was changed: new names collide with steps that keep their names ({string.Join(", ", clash)}) - " +
                                "include those steps (steps=...) or choose prefix/start/step differently.");
        CheckStepNames(map.Values.Select(n => new SfcNode { Kind = "step", Name = n }).ToList(), c.Pou);

        // embedded step actions _a<old>_active/_entry/_exit -> _a<new>_... (only if exactly this step uses them and the new name is free)
        var notes = new List<string>();
        var actMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objects = PouObjects(c.Pou);
        var objectNames = objects.Select(e => (string)e.Attribute("name")).Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = c.Sfc.Elements().Where(e => e.Name.LocalName == "step").ToList();
        var usedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in steps)
            foreach (var (g, v) in StepAttributes(st))
                if (g != NameGuid && g != CommentGuid && objectNames.Contains(v))
                {
                    if (!usedBy.TryGetValue(v, out var l)) usedBy[v] = l = new List<string>();
                    l.Add((string)st.Attribute("name") ?? "?");
                }
        var renameActions = Bool(a, "renameActions", true);
        foreach (var (act, users) in usedBy)
        {
            if (!users.Any(map.ContainsKey)) continue;
            if (!renameActions) { notes.Add($"{act}: name kept (renameActions=false)"); continue; }
            if (users.Count != 1) { notes.Add($"{act}: name kept - used by several steps ({string.Join(", ", users)})"); continue; }
            var pre = "_a" + users[0] + "_";
            if (!act.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { notes.Add($"{act}: name kept - it does not follow _a{users[0]}_..."); continue; }
            actMap[act] = "_a" + map[users[0]] + "_" + act[pre.Length..];
        }
        var vars = PouVariables(c.Pou);
        for (bool changed = true; changed;)
        {
            changed = false;
            var taken = objectNames.Where(n => !actMap.ContainsKey(n)).Concat(vars).Concat(keepNames).Concat(map.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (o, n) in actMap.ToList())
                if (taken.Contains(n)) { actMap.Remove(o); notes.Add($"{o}: name kept - {n} already exists"); changed = true; }
        }

        // references from outside the POU (fbSeq.N010.x) are not rewritten - with swapped numbers they would silently point to another step
        var outside = SfcOutsideReferences(c.Path, map.Keys.Concat(actMap.Keys).ToList());

        // ---- edit the export in place
        var rename = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        foreach (var (o, n) in actMap) rename[o] = n;
        var rx = new Regex(@"(?:(?<![\w.])|(?<=\^\.))(" + string.Join("|", rename.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")\b", RegexOptions.IgnoreCase);
        int refs = 0;
        string Re(string s) => rx.Replace(s, m => { refs++; return rename[m.Groups[1].Value]; });
        foreach (var st in steps)
        {
            if ((string)st.Attribute("name") is { } nm && map.TryGetValue(nm, out var nw))
            {
                st.SetAttributeValue("name", nw);
                if (ElementAttr(st, NameGuid) is { } na) na.Value = nw;
            }
            foreach (var at in st.Elements().Where(x => x.Name.LocalName == "addData").Descendants().Where(x => x.Name.LocalName == "attribute"))
            {
                var g = ((string)at.Attribute("guid") ?? "").ToLowerInvariant();
                if (g != NameGuid && g != CommentGuid && actMap.TryGetValue(at.Value.Trim(), out var na2)) at.Value = na2;
            }
        }
        foreach (var j in c.Sfc.Elements().Where(e => e.Name.LocalName == "jumpStep"))
            if ((string)j.Attribute("targetName") is { } tn && map.TryGetValue(tn, out var nt))
            {
                j.SetAttributeValue("targetName", nt);
                if (ElementAttr(j, NameGuid) is { } na) na.Value = nt;
            }
        // conditions (inVariable expressions, inline ST), inline action code and the condition text TwinCAT stores as transition name
        foreach (var x in c.Sfc.Descendants().Where(y => y.Name.LocalName is "expression" or "xhtml" && !y.HasElements).ToList())
            x.Value = Re(x.Value);
        foreach (var t in c.Sfc.Elements().Where(e => e.Name.LocalName == "transition"))
            if (ElementAttr(t, NameGuid) is { } tna) tna.Value = rx.Replace(tna.Value, m => rename[m.Groups[1].Value]);
        // code of the POU's actions and transitions (ST text; expressions of graphical bodies)
        foreach (var o in objects)
        {
            foreach (var x in o.Elements().Where(e => e.Name.LocalName == "body").Descendants().Where(y => y.Name.LocalName is "expression" or "xhtml" && !y.HasElements).ToList())
                x.Value = Re(x.Value);
            if (o.Name.LocalName == "action" && (string)o.Attribute("name") is { } an && actMap.TryGetValue(an, out var nan)) o.SetAttributeValue("name", nan);
        }

        var r = new StringBuilder();
        r.AppendLine($"Renumber {c.PouName} (order: {(order == "main" ? "main sequence first, then the branches" : "chain")}{(orderNote != null ? "; " + orderNote : "")}):");
        foreach (var s in sel) r.AppendLine(map.TryGetValue(s, out var nw) ? $"  {s,-16} -> {nw}" : $"  {s,-16}    (unchanged)");
        if (actMap.Count > 0) r.AppendLine("Embedded step actions: " + string.Join(", ", actMap.Select(kv => $"{kv.Key} -> {kv.Value}")));
        foreach (var n in notes) r.AppendLine("  " + n);
        if (refs > 0) r.AppendLine($"Step/action references updated in conditions and code of the POU: {refs}");
        if (outside.Count > 0)
        {
            r.AppendLine($"References OUTSIDE the POU (not changed{(Bool(a, "force", false) ? "" : " - the write needs force=true after asking the user")}):");
            foreach (var h in outside) r.AppendLine("  " + h);
        }

        if (DryRun(a)) return "PROPOSAL (nothing written):\n" + r + "\nNew chain:\n" + SfcToText(c.Sfc);
        if (outside.Count > 0 && !Bool(a, "force", false))
            throw new Exception("Nothing was changed: code outside the POU references steps/actions that would be renamed. Show the user the list, " +
                                "then fix those references after the renumbering (or renumber with force=true if they agree).\n" + r);
        return SfcReimport(c, original, r + $"Steps of {c.PouName} renumbered.");
    }

    /// <summary>Whole-word '.Name' hits in PLC objects outside the POU (instance accesses like fbSeq.N010.x).</summary>
    List<string> SfcOutsideReferences(string pouPath, List<string> names, int max = 50)
    {
        var hits = new List<string>();
        if (names.Count == 0) return hits;
        var rx = new Regex(@"\.\s*(" + string.Join("|", names.Select(Regex.Escape)) + @")\b", RegexOptions.IgnoreCase);
        foreach (var it in tc.PlcObjects())
        {
            if (hits.Count >= max) break;
            string p;
            try { p = it.PathName; } catch { continue; }
            if (p.Equals(pouPath, StringComparison.OrdinalIgnoreCase) || p.StartsWith(pouPath + "^", StringComparison.OrdinalIgnoreCase)) continue;
            void Scan(string part, string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int n = 0; n < lines.Length && hits.Count < max; n++)
                    if (rx.IsMatch(lines[n])) hits.Add($"{p} [{part} line {n + 1}] {Shorten(lines[n].Trim(), 160)}");
            }
            try { if (it is ITcPlcDeclaration d) Scan("declaration", d.DeclarationText); } catch { }
            try { if (it is ITcPlcImplementation i) Scan("implementation", i.ImplementationText); } catch { }
        }
        return hits;
    }

    // ================================================================== comments
    string SfcComments(SfcCtx c, JsonObject a)
    {
        var order = SfcStepOrder(c.Sfc, "chain", out _);
        var stepEls = c.Sfc.Elements().Where(e => e.Name.LocalName == "step" && e.Attribute("name") != null)
                        .GroupBy(e => (string)e.Attribute("name"), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string Current(XElement st) => (ElementAttr(st, CommentGuid)?.Value ?? "").Trim();
        var rules = CodingRules.Text(SolutionDir());
        var req = NameValueParam(a, "comments");
        if (req == null || req.Count == 0)
        {
            var o = new StringBuilder($"Step comments of {c.PouName} (chain order):\n");
            var actionObjects = ActionObjectNames(c.Sfc);
            foreach (var s in order.Where(stepEls.ContainsKey))
            {
                var st = stepEls[s];
                var acts = StepAttributes(st).Where(x => x.Guid != NameGuid && x.Guid != CommentGuid && actionObjects.Contains(x.Value)).Select(x => x.Value);
                o.AppendLine($"  {s,-16} {(((string)st.Attribute("initialStep")) == "true" ? "(initial) " : "")}" +
                             $"{(Current(st) is { Length: > 0 } cm ? Regex.Replace(cm, @"\s+", " ") : "-")}{(acts.Any() ? "   [embedded: " + string.Join(", ", acts) + "]" : "")}");
            }
            o.AppendLine("\nTo propose comments: read the chain (action=read) and the embedded/action code, write one short comment per step " +
                         "(what the step does / waits for) following the guidelines below, show them to the user, then " +
                         "tc_sfc action=comments comments={\"<step>\": \"<text>\", ...} (dryRun=true first).");
            o.AppendLine(rules != null ? "\nCoding guidelines of the project (comment language and style apply to step comments too):\n" + rules
                                       : "\nNo coding guidelines in this project (tc_coding_rules) - write comments in English unless the user wants otherwise.");
            return o.ToString();
        }
        var unknown = req.Keys.Where(k => !stepEls.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            throw new Exception($"Nothing was changed: no step {string.Join(", ", unknown)} - comments only for existing steps ({string.Join(", ", order)}).");
        // guidelines that require English: refuse obviously German comments (the agent may write in the user's language by habit)
        if (rules != null && Regex.IsMatch(rules, @"\b(english|englisch)\b", RegexOptions.IgnoreCase) && !Bool(a, "force", false))
        {
            var bad = req.Where(kv => LooksGerman(kv.Value)).Select(kv => kv.Key).ToList();
            if (bad.Count > 0)
                throw new Exception($"Nothing was changed: the coding guidelines require English, the comments of {string.Join(", ", bad)} look German. " +
                                    "Translate them (or write with force=true if the user wants them as they are).");
        }
        var original = new XDocument(c.Doc);
        var r = new StringBuilder();
        foreach (var (k, text) in req)
        {
            var st = stepEls[k];
            var old = Current(st);
            var nt = (text ?? "").Trim();
            if (old == nt) continue;
            if (!st.Elements().Any(e => e.Name.LocalName == "addData"))
                st.Add(NewStepData(c.Sfc.Name.Namespace, (string)st.Attribute("name"), ((string)st.Attribute("initialStep")) == "true", null, false));
            if (!SetElementAttr(st, CommentGuid, nt)) throw new Exception($"step {k}: no attribute data in the export - set its comment in the IDE");
            r.AppendLine($"  {((string)st.Attribute("name")),-16} {(old.Length > 0 ? Regex.Replace(old, @"\s+", " ") : "-")}  ->  {(nt.Length > 0 ? nt : "(removed)")}");
        }
        if (r.Length == 0) return "Nothing to change: the steps already have these comments.";
        var head = $"Step comments of {c.PouName}:\n" + r;
        if (DryRun(a)) return "PROPOSAL (nothing written):\n" + head;
        return SfcReimport(c, original, head + "Comments set.");
    }

    static bool LooksGerman(string s) =>
        !string.IsNullOrWhiteSpace(s) &&
        (Regex.IsMatch(s, "[äöüÄÖÜß]") ||
         Regex.IsMatch(s, @"\b(und|oder|der|das|nicht|wenn|ist|wird|werden|mit|für|fuer|auf|warten|schritt|fehler|bis|eine?|einer|zum|zur|vom)\b", RegexOptions.IgnoreCase));

    /// <summary>{name: value} parameter: a JSON object, a JSON object as text or lines/list "name: value" / "name=value".</summary>
    static Dictionary<string, string> NameValueParam(JsonObject a, string key)
    {
        var node = a[key];
        if (node == null) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obj = node as JsonObject;
        if (obj == null && node is JsonValue v)
        {
            var s = v.ToString().Trim();
            if (s.StartsWith("{")) obj = JsonNode.Parse(s) as JsonObject;
            else
            {
                var sep = key == "flags" ? new[] { '\n', ',', ';' } : new[] { '\n' };
                foreach (var line in s.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var m = Regex.Match(line, @"^([A-Za-z_][A-Za-z0-9_]*)\s*[:=]\s*(.*)$");
                    if (!m.Success) throw new Exception($"{key}: expected {{\"name\": value}} or lines 'name: value' ('{line}')");
                    d[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"');
                }
                return d;
            }
        }
        if (obj == null) throw new Exception($"{key}: expected an object {{\"name\": value}}");
        foreach (var (k, val) in obj) d[k] = val is JsonValue jv ? jv.ToString() : val?.ToJsonString() ?? "";
        return d;
    }

    // ================================================================== flags
    // PLCopen <Flags> element names; the variable is SFC<name> (the analyzation flags already start with SFC); null type = not auto-declarable
    static readonly (string Key, string Type)[] SfcFlagDefs =
    {
        ("Init", "BOOL"), ("Reset", "BOOL"), ("QuitError", "BOOL"), ("Pause", "BOOL"), ("Error", "BOOL"), ("Trans", "BOOL"),
        ("ErrorStep", "STRING"), ("ErrorPOU", "STRING"), ("CurrentStep", "STRING"), ("Tip", "BOOL"), ("TipMode", "BOOL"),
        ("EnableLimit", "BOOL"), ("SFCErrorAnalyzation", "STRING"), ("SFCErrorAnalyzationTable", null)
    };
    static string FlagVar(string key) => key.StartsWith("SFC", StringComparison.OrdinalIgnoreCase) ? key : "SFC" + key;

    sealed record VsProp(XElement El, string Name, XElement Value);

    string SfcFlags(SfcCtx c, JsonObject a)
    {
        // VSProperties of the POU tree item
        var vs = new List<VsProp>();
        string vsError = null;
        try
        {
            var px = XDocument.Parse((string)((dynamic)tc.Item(c.Path)).ProduceXml(false));
            foreach (var p in px.Descendants().Where(e => e.Name.LocalName == "VSProperty"))
            {
                var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value.Trim() ?? (string)p.Attribute("Name");
                var val = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
                if (!string.IsNullOrEmpty(name) && val != null) vs.Add(new VsProp(p, name, val));
            }
        }
        catch (Exception ex) { vsError = ex.InnerException?.Message ?? ex.Message; }
        var sfcVs = vs.Where(p => p.Name.Contains("SFC", StringComparison.OrdinalIgnoreCase)).ToList();
        VsProp FindVs((string Key, string Type) def) =>
            sfcVs.FirstOrDefault(p => p.Name.Equals(FlagVar(def.Key), StringComparison.OrdinalIgnoreCase) || p.Name.Equals(def.Key, StringComparison.OrdinalIgnoreCase));
        VsProp FindDeclareVs((string Key, string Type) def)
        {
            var v = FlagVar(def.Key);
            var cands = new[] { v + "Declare", "Declare" + v, v + "_Declare", def.Key + "Declare", "Declare" + def.Key };
            return sfcVs.Concat(vs).FirstOrDefault(p => cands.Any(x => p.Name.Equals(x, StringComparison.OrdinalIgnoreCase)));
        }
        // SFC settings of the PLCopen export
        var settings = c.Pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "SFCSettings");
        var flagsEl = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Flags");
        XElement PlcFlag(string key) => flagsEl?.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
        var declared = PouVariables(c.Pou);

        var req = NameValueParam(a, "flags");
        if (req == null || req.Count == 0)
        {
            var o = new StringBuilder($"SFC flags of {c.PouName} (PLCopen SFC settings{(sfcVs.Count > 0 ? " and VSProperties" : "")}):\n");
            o.AppendLine($"  {"variable",-26} {"Use",-6} {"Declare",-8} {"declared in POU",-16} VSProperty");
            foreach (var def in SfcFlagDefs)
            {
                var f = PlcFlag(def.Key);
                var p = FindVs(def);
                o.AppendLine($"  {FlagVar(def.Key),-26} {((string)f?.Attribute("Use") ?? "?"),-6} {((string)f?.Attribute("Declare") ?? "?"),-8} " +
                             $"{(declared.Contains(FlagVar(def.Key)) ? "yes" : "no"),-16} {(p != null ? $"{p.Name} = {p.Value.Value}" : "")}");
            }
            if ((string)settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "UseDefaults") is { } ud) o.AppendLine($"UseDefaults: {ud}");
            if (sfcVs.Count > 0)
            {
                var other = sfcVs.Where(p => !SfcFlagDefs.Any(d => FindVs(d) == p)).ToList();
                if (other.Count > 0) o.AppendLine("Other SFC VSProperties: " + string.Join(", ", other.Select(p => $"{p.Name} = {p.Value.Value}")));
            }
            else o.AppendLine($"(no SFC VSProperties in ProduceXml of the POU{(vsError != null ? ": " + vsError : "")} - flags are set through the PLCopen SFC settings)");
            o.AppendLine("Set with flags={\"SFCInit\": true, ...}. An enabled flag must be declared: Declare=true (implicit) or a variable in the declaration.");
            return o.ToString();
        }

        var wanted = new List<((string Key, string Type) Def, bool On)>();
        foreach (var (k, v) in req)
        {
            var def = SfcFlagDefs.FirstOrDefault(d => d.Key.Equals(k, StringComparison.OrdinalIgnoreCase) || FlagVar(d.Key).Equals(k, StringComparison.OrdinalIgnoreCase));
            if (def.Key == null) throw new Exception($"unknown SFC flag '{k}' - known: {string.Join(", ", SfcFlagDefs.Select(d => FlagVar(d.Key)))}");
            bool on = (v ?? "").Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "on" or "yes" => true,
                "false" or "0" or "off" or "no" => false,
                _ => throw new Exception($"{k}: true or false expected, got '{v}'")
            };
            wanted.Add((def, on));
        }
        var r = new StringBuilder();

        // ---- path 1: VSProperties (ConsumeXml with only the changed properties)
        if (sfcVs.Count > 0 && wanted.All(w => FindVs(w.Def) != null))
        {
            var changes = new List<(VsProp P, string Value)>();
            var addDecl = new List<(string Var, string Type)>();
            foreach (var (def, on) in wanted)
            {
                var p = FindVs(def);
                var nv = BoolLike(p.Value.Value, on) ?? throw new Exception($"Nothing was changed: VSProperty {p.Name} has the value '{p.Value.Value}' - " +
                                                                              "not a boolean; inspect it with tc_xml_get and set it with tc_xml_set.");
                if (nv != p.Value.Value) changes.Add((p, nv));
                var varName = FlagVar(def.Key);
                r.AppendLine($"  {p.Name}: {p.Value.Value} -> {nv}");
                if (!on || declared.Contains(varName)) { if (on) r.AppendLine($"    {varName} is declared in the POU already"); continue; }
                if (FindDeclareVs(def) is { } dp && BoolLike(dp.Value.Value, true) is { } dv)
                {
                    if (dv != dp.Value.Value) changes.Add((dp, dv));
                    r.AppendLine($"    declared by TwinCAT: {dp.Name} -> {dv}");
                }
                else if (def.Type == null)
                    throw new Exception($"Nothing was changed: {varName} cannot be declared automatically (ARRAY OF ExpressionResult) - declare it in the POU first.");
                else
                {
                    addDecl.Add((varName, def.Type));
                    r.AppendLine($"    no Declare property: '{varName} : {def.Type};' is added to the VAR block of the declaration");
                }
            }
            if (changes.Count == 0 && addDecl.Count == 0) return "Nothing to change:\n" + r;
            var xmls = changes.GroupBy(x => x.P.El.Parent).Select(g => MinimalXml(g.Key, g.Select(x =>
            {
                var cl = new XElement(x.P.El);
                cl.Elements().First(e => e.Name.LocalName == "Value").Value = x.Value;
                return cl;
            }))).ToList();
            if (DryRun(a)) return $"PROPOSAL (nothing written) - SFC flags of {c.PouName} via VSProperties:\n{r}" + string.Join("\n", xmls);
            foreach (var x in xmls) ((dynamic)tc.Item(c.Path)).ConsumeXml(x);
            if (addDecl.Count > 0) AddFlagDeclarations(c.Path, addDecl);
            // verify
            try
            {
                var after = XDocument.Parse((string)((dynamic)tc.Item(c.Path)).ProduceXml(false)).Descendants().Where(e => e.Name.LocalName == "VSProperty").ToList();
                foreach (var (p, val) in changes)
                {
                    var now = after.FirstOrDefault(e => e.Elements().Any(n => n.Name.LocalName == "Name" && n.Value.Trim().Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                                   ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
                    if (now != null && !now.Equals(val, StringComparison.OrdinalIgnoreCase)) r.AppendLine($"  WARNING: {p.Name} is still {now} after ConsumeXml");
                }
            }
            catch { }
            tc.InvalidatePlcIndex();
            return $"SFC flags of {c.PouName} set (VSProperties). Check with tc_check.\n" + r;
        }

        // ---- path 2: PLCopen SFC settings (re-import of the POU)
        if (flagsEl == null)
            throw new Exception("Nothing was changed: the POU offers neither SFC VSProperties nor SFC settings in its PLCopen export" +
                                (sfcVs.Count > 0 ? $" for all requested flags (VSProperties: {string.Join(", ", sfcVs.Select(p => p.Name))})" : "") +
                                " - set the flags in the IDE (POU properties / SFC settings).");
        var original = new XDocument(c.Doc);
        bool any = false;
        foreach (var (def, on) in wanted)
        {
            var f = PlcFlag(def.Key) ?? throw new Exception($"Nothing was changed: the SFC settings of the export have no flag {def.Key}");
            var varName = FlagVar(def.Key);
            string use = (string)f.Attribute("Use"), decl = (string)f.Attribute("Declare");
            if (on)
            {
                // Use=true with Declare=false needs a declared variable, else the build fails
                var nd = declared.Contains(varName) ? "false" : "true";
                f.SetAttributeValue("Use", "true");
                f.SetAttributeValue("Declare", nd);
                r.AppendLine($"  {varName}: Use {use} -> true, Declare {decl} -> {nd}" +
                             (nd == "true" ? " (TwinCAT declares the variable implicitly)" : " (declared in the POU already)"));
            }
            else
            {
                f.SetAttributeValue("Use", "false");
                r.AppendLine($"  {varName}: Use {use} -> false (Declare stays {decl})");
            }
            any |= use != (string)f.Attribute("Use") || decl != (string)f.Attribute("Declare");
        }
        if (settings.Elements().FirstOrDefault(e => e.Name.LocalName == "UseDefaults") is { } useDef && useDef.Value.Trim() == "true")
        {
            useDef.Value = "false";
            r.AppendLine("  UseDefaults: true -> false (else the POU uses the project defaults and ignores its own flags)");
            any = true;
        }
        if (!any) return "Nothing to change:\n" + r;
        if (DryRun(a)) return $"PROPOSAL (nothing written) - SFC flags of {c.PouName} via the PLCopen SFC settings (the POU is re-imported):\n{r}{flagsEl}";
        return SfcReimport(c, original, $"SFC flags of {c.PouName} set (PLCopen SFC settings):\n{r}");
    }

    /// <summary>The new boolean value in the spelling of the old one (True/true/TRUE/1); null if the old value is not boolean.</summary>
    static string BoolLike(string old, bool on)
    {
        var o = (old ?? "").Trim();
        if (o is "0" or "1") return on ? "1" : "0";
        if (!bool.TryParse(o, out _)) return null;
        var s = on ? "true" : "false";
        return o.All(char.IsUpper) ? s.ToUpperInvariant() : char.IsUpper(o[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    /// <summary>Only the path from the root to 'parent' plus the given children - ConsumeXml changes nothing else.</summary>
    static string MinimalXml(XElement parent, IEnumerable<XElement> children)
    {
        var cur = new XElement(parent.Name, parent.Attributes(), children);
        for (var p = parent.Parent; p != null; p = p.Parent) cur = new XElement(p.Name, p.Attributes(), cur);
        return cur.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Adds flag variables to the first plain VAR block of the POU declaration (or a new VAR block at the end).</summary>
    void AddFlagDeclarations(string pouPath, List<(string Var, string Type)> vars)
    {
        var it = tc.Item(pouPath) as ITcPlcDeclaration ?? throw new Exception("the POU has no declaration part");
        var lines = (it.DeclarationText ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var add = vars.SelectMany(v => new[] { "\t/// SFC flag (SFC settings of the POU)", $"\t{v.Var} : {v.Type};" }).ToList();
        int s = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*VAR\s*(//.*)?$", RegexOptions.IgnoreCase));
        int e = s < 0 ? -1 : lines.FindIndex(s + 1, l => Regex.IsMatch(l, @"^\s*END_VAR\b", RegexOptions.IgnoreCase));
        if (e > 0) lines.InsertRange(e, add);
        else
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add("VAR"); lines.AddRange(add); lines.Add("END_VAR");
        }
        it.DeclarationText = string.Join("\r\n", lines);
    }
}
