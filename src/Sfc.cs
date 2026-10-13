// Sequential function charts (SFC step chains) as text.
//
// The Automation Interface reads and writes only ST text directly; graphical languages go through PLCopen XML
// (Beckhoff: "SFC - possibility to implement via PLCopen XML"). tc_sfc exports the POU as PLCopen XML, turns the
// <SFC> body into a structured text form and back, deletes the POU and imports it again into its folder (the previous
// version is restored if TwinCAT rejects the import). TwinCAT specifics (verified on 4026): transition conditions are
// inVariables connected to the condition, every branch block has a convergence (jumps at branch ends connect to it),
// step names become variables (Name.x, Name.t) and must not equal a variable, action or keyword (use S_Init ...).
//
// Text form (one element per line, indentation optional):
//   STEP Init INITIAL                 step (INITIAL = initial step)
//     ACTION N A_Init                 action association: qualifier + action name (N, S, R, P, P0, P1, L t#2s, D t#2s ...)
//   TRANSITION bStart                 transition condition (ST expression) or the name of a transition object
//   STEP Run
//   ALTERNATIVE                       alternative branch after a step - every BRANCH starts with a TRANSITION
//     BRANCH
//       TRANSITION bError
//       STEP Error
//       TRANSITION bAck
//       JUMP Init
//     BRANCH
//       TRANSITION bDone
//       STEP Done
//   END_ALTERNATIVE                   followed by a TRANSITION (nothing, if every branch ends with JUMP)
//   PARALLEL                          simultaneous branch after a transition - every BRANCH starts with a STEP
//     BRANCH ... END_PARALLEL         followed by a TRANSITION
//   JUMP Init                         jump to a step (ends the chain or a branch)
// Actions and transitions with code are separate objects of the POU: tc_plc_create kind=action / kind=transition
// with parentPath = the SFC POU (or the action/transition blocks of this tool's 'code' parameter).
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
    static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    void RegisterSfc()
    {
        Add("tc_sfc", "Sequential function charts (SFC step chains): action=read returns the chain as text (steps, actions, transitions, " +
                      "ALTERNATIVE/PARALLEL branches, jumps) plus the action/transition objects with their code; action=write replaces the chain " +
                      "with the given text (same form as read: 'STEP Init INITIAL', 'ACTION N A_Init', 'TRANSITION bStart', 'ALTERNATIVE' / 'PARALLEL' " +
                      "with 'BRANCH' ... 'END_ALTERNATIVE' / 'END_PARALLEL', 'JUMP Init'); action=xml shows the raw PLCopen SFC body. " +
                      "Create the POU first with tc_plc_create language=SFC, actions/transitions with code with tc_plc_create kind=action/transition " +
                      "(parentPath = the POU). Step names become variables of the POU: never name a step like a variable, action or keyword " +
                      "(Move, Error, Done ...) - use S_Init, S_Move ... Read before writing; check with tc_check afterwards.",
            Schema(Req(P("action", "string", "read, write or xml")),
                   Req(P("path", "string", "tree path of the SFC POU, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Sequence")),
                   P("chain", "string", "write: the complete chain in the text form of action=read"),
                   P("dryRun", "boolean", "write: only show the generated PLCopen XML")),
            a => tc.Sta.Run(() => Sfc(a), 300_000));
    }

    string Sfc(JsonObject a)
    {
        var path = Str(a, "path") ?? throw new Exception("path is required");
        var parts = path.Split('^');
        var projIdx = -1;
        for (int i = 0; i < parts.Length; i++)
        {
            try { if (tc.Item(string.Join("^", parts.Take(i + 1))).ItemType == 600) { projIdx = i; break; } } catch { }
        }
        if (projIdx < 0 || projIdx == parts.Length - 1) throw new Exception("path must point to a POU inside a PLC project, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Sequence");
        var plc = tc.Item(string.Join("^", parts.Take(projIdx + 1)));
        var selection = string.Join(".", parts.Skip(projIdx + 1));
        var pouName = parts[^1];

        var tmp = Path.Combine(Path.GetTempPath(), $"tcagent-sfc-{Guid.NewGuid():N}.xml");
        try
        {
            ComCall.Invoke(plc, "PlcOpenExport", tmp, selection);
            if (!File.Exists(tmp)) throw new Exception("PLCopen export produced no file");
            var doc = XDocument.Load(tmp);
            var pou = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "pou" && (string)e.Attribute("name") == pouName)
                      ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "pou")
                      ?? throw new Exception("POU not found in the PLCopen export");
            var body = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
            var sfc = body?.Elements().FirstOrDefault(e => e.Name.LocalName == "SFC");
            var action = (Str(a, "action") ?? "read").ToLowerInvariant();
            if (sfc == null)
            {
                var lang = body?.Elements().FirstOrDefault()?.Name.LocalName ?? "?";
                throw new Exception($"{pouName} is not written in SFC (body: {lang}). Create an SFC POU with tc_plc_create language=SFC.");
            }
            switch (action)
            {
                case "xml":
                    return Shorten(sfc.ToString(), 60_000);
                case "read":
                    return $"SFC {pouName}\n" + SfcToText(sfc) + SfcObjects(pou);
                case "write":
                {
                    var chain = Str(a, "chain") ?? throw new Exception("chain is required (text form as returned by action=read)");
                    var root = ParseChain(chain);
                    CheckStepNames(root, pou);
                    var transitionObjects = pou.Descendants().Where(e => e.Name.LocalName == "transition" && e.Parent?.Name.LocalName == "transitions")
                                               .Select(e => (string)e.Attribute("name")).Where(n => n != null);
                    var original = new XDocument(doc);
                    var newSfc = BuildSfc(sfc, root, transitionObjects);
                    if (DryRun(a)) return "PROPOSAL (nothing written):\n" + newSfc;
                    sfc.ReplaceWith(newSfc);
                    FixSfcFlags(pou);
                    // TwinCAT's import puts objects without folder information into the project root and does not replace an
                    // object inside a folder: wrap the project structure in the POU's folders, delete the POU, import it new
                    var folders = parts.Skip(projIdx + 1).Take(parts.Length - projIdx - 2).ToList();
                    if (!WrapInFolders(doc, folders) || !WrapInFolders(original, folders))
                        throw new Exception("the PLCopen export has no project structure - the POU cannot be re-imported into its folder; nothing was changed");
                    doc.Save(tmp);
                    var backup = tmp + ".orig.xml";
                    original.Save(backup);
                    var parentPath = string.Join("^", parts.Take(parts.Length - 1));
                    try { ((dynamic)tc.Item(parentPath)).DeleteChild(pouName); }
                    catch { try { File.Delete(backup); } catch { } throw; }
                    string failure = null;
                    try { ComCall.Invoke(plc, "PlcOpenImport", tmp, 0); }
                    catch (Exception ex) { failure = ex.InnerException?.Message ?? ex.Message; }
                    if (failure == null && !ItemExists(path)) failure = "the POU was not created (see the TwinCAT error list)";
                    if (failure != null)
                    {
                        // put the original back
                        try { if (ItemExists(path)) ((dynamic)tc.Item(parentPath)).DeleteChild(pouName); } catch { }
                        try { ComCall.Invoke(plc, "PlcOpenImport", backup, 0); } catch { }
                        tc.InvalidatePlcIndex();
                        var restored = ItemExists(path);
                        if (restored) try { File.Delete(backup); } catch { }      // keep the backup if the restore failed
                        throw new Exception($"TwinCAT did not accept the chain: {failure}. " +
                                            (restored ? $"The previous version of {pouName} was restored."
                                                      : $"The previous version of {pouName} could NOT be restored - re-import {backup} with tc_import kind=plcopen."));
                    }
                    try { File.Delete(backup); } catch { }
                    tc.InvalidatePlcIndex();
                    var check = Sfc(new JsonObject { ["action"] = "read", ["path"] = path });
                    return $"SFC chain of {pouName} replaced. Check with tc_check.\n\n" + check;
                }
                default: throw new Exception("action: read, write or xml");
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ================================================================== PLCopen SFC -> text
    sealed class SfcGraph
    {
        public readonly Dictionary<string, XElement> Nodes = new();
        public readonly Dictionary<string, List<string>> Succ = new();
        public readonly Dictionary<string, List<string>> ActionsOf = new();   // step id -> action lines
    }

    static string LocalId(XElement e) => (string)e.Attribute("localId") ?? "";

    static IEnumerable<string> Inputs(XElement e) =>
        e.Elements().Where(c => c.Name.LocalName == "connectionPointIn")
         .SelectMany(c => c.Elements().Where(x => x.Name.LocalName == "connection"))
         .Select(x => (string)x.Attribute("refLocalId")).Where(x => x != null);

    static string StText(XElement e)
    {
        if (e == null) return null;
        var st = e.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "ST");
        var text = st != null ? st.Value : e.Value;
        return text?.Trim();
    }

    static string Condition(XElement tr)
    {
        var cond = tr.Elements().FirstOrDefault(e => e.Name.LocalName == "condition");
        if (cond == null) return "TRUE";
        var reference = cond.Descendants().FirstOrDefault(e => e.Name.LocalName == "reference");
        if (reference != null) return (string)reference.Attribute("name") ?? "?";
        // TwinCAT format: <condition><connectionPointIn><connection refLocalId=".."/> -> <inVariable><expression>
        var src = Inputs(cond).FirstOrDefault();
        if (src != null && tr.Parent != null)
        {
            var v = tr.Parent.Elements().FirstOrDefault(e => LocalId(e) == src && e.Name.LocalName is "inVariable" or "inOutVariable");
            var expr = v?.Elements().FirstOrDefault(e => e.Name.LocalName == "expression")?.Value;
            if (!string.IsNullOrWhiteSpace(expr)) return Regex.Replace(expr, @"\s+", " ").Trim();
        }
        var t = StText(cond);
        return string.IsNullOrWhiteSpace(t) ? "TRUE" : Regex.Replace(t, @"\s+", " ");
    }

    static SfcGraph Graph(XElement sfc)
    {
        var g = new SfcGraph();
        foreach (var e in sfc.Elements())
        {
            var id = LocalId(e);
            if (id.Length == 0) continue;
            // condition sources of transitions (and other FBD elements) are not part of the step chain
            if (e.Name.LocalName is "inVariable" or "outVariable" or "inOutVariable" or "block" or "comment") continue;
            if (e.Name.LocalName == "actionBlock")
            {
                foreach (var stepId in Inputs(e))
                {
                    if (!g.ActionsOf.TryGetValue(stepId, out var list)) g.ActionsOf[stepId] = list = new List<string>();
                    foreach (var act in e.Elements().Where(x => x.Name.LocalName == "action"))
                    {
                        var q = (string)act.Attribute("qualifier") ?? "N";
                        var dur = (string)act.Attribute("duration");
                        var name = (string)act.Descendants().FirstOrDefault(x => x.Name.LocalName == "reference")?.Attribute("name");
                        var inline = name == null ? StText(act.Descendants().FirstOrDefault(x => x.Name.LocalName == "inline")) : null;
                        list.Add($"ACTION {q}{(!string.IsNullOrEmpty(dur) ? " " + dur : "")} {name ?? "{" + Regex.Replace(inline ?? "", @"\s+", " ") + "}"}");
                    }
                }
                continue;
            }
            g.Nodes[id] = e;
        }
        foreach (var (id, e) in g.Nodes)
            foreach (var src in Inputs(e))
            {
                if (!g.Nodes.ContainsKey(src)) continue;
                if (!g.Succ.TryGetValue(src, out var l)) g.Succ[src] = l = new List<string>();
                l.Add(id);
            }
        return g;
    }

    static string SfcToText(XElement sfc)
    {
        var g = Graph(sfc);
        var o = new StringBuilder();
        var start = g.Nodes.Values.FirstOrDefault(e => e.Name.LocalName == "step" && ((string)e.Attribute("initialStep")) == "true")
                    ?? g.Nodes.Values.FirstOrDefault(e => !Inputs(e).Any(i => g.Nodes.ContainsKey(i)))
                    ?? g.Nodes.Values.FirstOrDefault();
        if (start == null) return "(empty chart)\n";
        var seen = new HashSet<string>();

        // emits from id until a convergence (returned) or the end
        string Emit(string id, int depth)
        {
            var ind = new string(' ', depth * 2);
            while (id != null)
            {
                if (!g.Nodes.TryGetValue(id, out var e)) return null;
                var kind = e.Name.LocalName;
                if (kind is "selectionConvergence" or "simultaneousConvergence") return id;
                if (!seen.Add(id)) { o.AppendLine($"{ind}(loop back to element {id})"); return null; }
                List<string> next = g.Succ.TryGetValue(id, out var s) ? s : new List<string>();
                switch (kind)
                {
                    case "step":
                        o.AppendLine($"{ind}STEP {(string)e.Attribute("name")}{(((string)e.Attribute("initialStep")) == "true" ? " INITIAL" : "")}");
                        if (g.ActionsOf.TryGetValue(id, out var acts)) foreach (var x in acts) o.AppendLine($"{ind}  {x}");
                        break;
                    case "transition":
                        o.AppendLine($"{ind}TRANSITION {Condition(e)}");
                        break;
                    case "jumpStep":
                        o.AppendLine($"{ind}JUMP {(string)e.Attribute("targetName")}");
                        return null;
                    case "selectionDivergence":
                    case "simultaneousDivergence":
                    {
                        var alt = kind == "selectionDivergence";
                        o.AppendLine(ind + (alt ? "ALTERNATIVE" : "PARALLEL"));
                        string conv = null;
                        foreach (var b in next)
                        {
                            o.AppendLine($"{ind}  BRANCH");
                            conv = Emit(b, depth + 2) ?? conv;
                        }
                        o.AppendLine(ind + (alt ? "END_ALTERNATIVE" : "END_PARALLEL"));
                        if (conv == null) return null;
                        seen.Add(conv);
                        next = g.Succ.TryGetValue(conv, out var cs) ? cs : new List<string>();
                        break;
                    }
                    default:
                        o.AppendLine($"{ind}({kind} {id})");
                        break;
                }
                id = next.FirstOrDefault();
            }
            return null;
        }
        Emit(LocalId(start), 0);
        var unreached = g.Nodes.Keys.Where(k => !seen.Contains(k) && g.Nodes[k].Name.LocalName is not ("selectionConvergence" or "simultaneousConvergence")).ToList();
        if (unreached.Count > 0) o.AppendLine($"(not reached from the initial step: {string.Join(", ", unreached.Select(k => g.Nodes[k].Name.LocalName + " " + ((string)g.Nodes[k].Attribute("name") ?? k)))})");
        return o.ToString();
    }

    static string SfcObjects(XElement pou)
    {
        var o = new StringBuilder();
        foreach (var kind in new[] { "action", "transition" })
            foreach (var x in pou.Descendants().Where(e => e.Name.LocalName == kind && e.Parent?.Name.LocalName == kind + "s"))
            {
                var code = StText(x.Elements().FirstOrDefault(e => e.Name.LocalName == "body"));
                o.AppendLine($"\n{kind.ToUpperInvariant()} OBJECT {(string)x.Attribute("name")}:\n{code}");
            }
        return o.Length > 0 ? "\nObjects of the POU (edit with tc_plc_read/tc_plc_write):" + o : "";
    }

    bool ItemExists(string path) { try { tc.Item(path); return true; } catch { return false; } }

    // <ProjectStructure><Object Name="FB"> -> <Folder Name="a"><Folder Name="b"><Object Name="FB">
    // returns false if the folders cannot be expressed (no ProjectStructure in the export)
    static bool WrapInFolders(XDocument doc, List<string> folders)
    {
        if (folders.Count == 0) return true;
        var ps = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProjectStructure");
        if (ps == null) return false;
        var objs = ps.Elements().Where(e => e.Name.LocalName == "Object").ToList();
        if (objs.Count == 0) return true;                          // already inside <Folder> elements
        foreach (var o in objs) o.Remove();
        XElement inner = null, outer = null;
        foreach (var f in folders)
        {
            var fe = new XElement(ps.Name.Namespace + "Folder", new XAttribute("Name", f));
            if (inner == null) outer = fe; else inner.Add(fe);
            inner = fe;
        }
        inner.Add(objs);
        ps.Add(outer);
        return true;
    }

    // step names become variables of the POU (Name.x, Name.t): they must not collide with variables or keywords
    static readonly HashSet<string> SfcReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOVE", "AND", "OR", "XOR", "NOT", "MOD", "IF", "THEN", "ELSE", "ELSIF", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO",
        "END_FOR", "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "RETURN", "EXIT", "CONTINUE", "TRUE", "FALSE", "STEP", "END_STEP",
        "TRANSITION", "END_TRANSITION", "ACTION", "END_ACTION", "FROM", "INITIAL_STEP", "SEL", "MUX", "MIN", "MAX", "LIMIT", "ABS", "SQRT",
        "THIS", "SUPER", "VAR", "END_VAR", "AT", "ARRAY", "STRING", "WSTRING", "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "INT", "DINT", "REAL",
        "LREAL", "TIME", "DATE", "TOD", "DT", "SIZEOF", "ADR", "REF", "NEW", "DELETE"
    };

    static void CheckStepNames(List<SfcNode> root, XElement pou)
    {
        var vars = new HashSet<string>(pou.Elements().Where(e => e.Name.LocalName == "interface").Descendants()
                                          .Where(e => e.Name.LocalName == "variable").Select(e => (string)e.Attribute("name")).Where(n => n != null),
                                       StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<string>(pou.Descendants().Where(e => e.Name.LocalName is "action" or "transition" && e.Parent?.Name.LocalName is "actions" or "transitions")
                                             .Select(e => (string)e.Attribute("name")).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        void Walk(List<SfcNode> seq)
        {
            foreach (var n in seq)
            {
                if (n.Kind == "step" && (vars.Contains(n.Name) || objects.Contains(n.Name) || SfcReserved.Contains(n.Name)))
                    bad.Add(n.Name + (vars.Contains(n.Name) ? " (variable)" : objects.Contains(n.Name) ? " (action/transition)" : " (keyword/operator)"));
                n.Branches.ForEach(Walk);
            }
        }
        Walk(root);
        if (bad.Count > 0)
            throw new Exception("step names become variables of the POU and must be unique: " + string.Join(", ", bad) +
                                " - rename the steps, e.g. S_" + bad[0].Split(' ')[0] + " (and the JUMP targets).");
    }

    // SFC flags that are 'used' but not declared break the build ("SFC flag variable 'SFCInit' is not defined")
    static void FixSfcFlags(XElement pou)
    {
        var flags = pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "Flags");
        if (flags == null) return;
        var vars = new HashSet<string>(pou.Elements().Where(e => e.Name.LocalName == "interface").Descendants()
                                          .Where(e => e.Name.LocalName == "variable").Select(e => (string)e.Attribute("name")).Where(n => n != null),
                                       StringComparer.OrdinalIgnoreCase);
        foreach (var f in flags.Elements())
            if ((string)f.Attribute("Use") == "true" && (string)f.Attribute("Declare") != "true" && !vars.Contains("SFC" + f.Name.LocalName))
                f.SetAttributeValue("Use", "false");
    }

    // ================================================================== text -> model
    sealed class SfcNode
    {
        public string Kind;                       // step, transition, jump, alternative, parallel
        public string Name;                       // step name, condition, jump target
        public bool Initial;
        public readonly List<(string q, string dur, string action)> Actions = new();
        public readonly List<List<SfcNode>> Branches = new();
    }

    static List<SfcNode> ParseChain(string text)
    {
        // the output of action=read can be passed back as it is: header and object listing are skipped
        text = text.Replace("\r\n", "\n");
        var cut = text.IndexOf("\nObjects of the POU", StringComparison.Ordinal);
        if (cut >= 0) text = text[..cut];
        var lines = text.Split('\n').Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("(*") && !l.StartsWith("SFC ", StringComparison.OrdinalIgnoreCase)
                                    && !l.StartsWith("(not reached", StringComparison.OrdinalIgnoreCase)).ToList();
        int i = 0;
        List<SfcNode> Seq(params string[] stop)
        {
            var list = new List<SfcNode>();
            while (i < lines.Count)
            {
                var l = lines[i];
                var kw = l.Split(' ', 2)[0].ToUpperInvariant();
                var rest = l.Length > kw.Length ? l[kw.Length..].Trim() : "";
                if (stop.Contains(kw)) return list;
                i++;
                switch (kw)
                {
                    case "STEP":
                    {
                        var m = Regex.Match(rest, @"^([A-Za-z_][A-Za-z0-9_]*)(\s+INITIAL)?$", RegexOptions.IgnoreCase);
                        if (!m.Success) throw new Exception($"line '{l}': STEP <name> [INITIAL]");
                        list.Add(new SfcNode { Kind = "step", Name = m.Groups[1].Value, Initial = m.Groups[2].Success });
                        break;
                    }
                    case "ACTION":
                    {
                        if (list.Count == 0 || list[^1].Kind != "step") throw new Exception($"line '{l}': ACTION must follow a STEP");
                        var m = Regex.Match(rest, @"^([A-Z0-9]+)(\s+(T#\S+|TIME#\S+))?\s+([A-Za-z_][A-Za-z0-9_.]*)$", RegexOptions.IgnoreCase);
                        if (!m.Success) throw new Exception($"line '{l}': ACTION <qualifier> [duration] <action name>, e.g. ACTION N A_Run or ACTION L T#2S A_Blink");
                        list[^1].Actions.Add((m.Groups[1].Value.ToUpperInvariant(), m.Groups[3].Success ? m.Groups[3].Value : null, m.Groups[4].Value));
                        break;
                    }
                    case "TRANSITION":
                        list.Add(new SfcNode { Kind = "transition", Name = rest.Length == 0 ? "TRUE" : rest.TrimEnd(';') });
                        break;
                    case "JUMP":
                        list.Add(new SfcNode { Kind = "jump", Name = rest });
                        return list;                                  // nothing may follow a jump in a sequence
                    case "ALTERNATIVE":
                    case "PARALLEL":
                    {
                        var node = new SfcNode { Kind = kw == "ALTERNATIVE" ? "alternative" : "parallel" };
                        var end = kw == "ALTERNATIVE" ? "END_ALTERNATIVE" : "END_PARALLEL";
                        while (i < lines.Count && lines[i].ToUpperInvariant().StartsWith("BRANCH"))
                        {
                            i++;
                            node.Branches.Add(Seq("BRANCH", end));
                        }
                        if (i >= lines.Count || !lines[i].ToUpperInvariant().StartsWith(end)) throw new Exception($"{kw}: every branch starts with BRANCH, the block ends with {end}");
                        i++;
                        if (node.Branches.Count < 2) throw new Exception($"{kw} needs at least two BRANCHes");
                        list.Add(node);
                        break;
                    }
                    default:
                        throw new Exception($"line '{l}': unknown keyword {kw} (STEP, ACTION, TRANSITION, ALTERNATIVE, PARALLEL, BRANCH, END_ALTERNATIVE, END_PARALLEL, JUMP)");
                }
            }
            return list;
        }
        var root = Seq();
        if (i < lines.Count) throw new Exception($"unexpected line '{lines[i]}'");
        ValidateChain(root, true);
        var steps = new List<string>();
        void Collect(List<SfcNode> seq) { foreach (var n in seq) { if (n.Kind == "step") steps.Add(n.Name); n.Branches.ForEach(Collect); } }
        Collect(root);
        var dup = steps.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new Exception($"step name {dup.Key} is used more than once");
        void Jumps(List<SfcNode> seq)
        {
            foreach (var n in seq)
            {
                if (n.Kind == "jump" && !steps.Contains(n.Name, StringComparer.OrdinalIgnoreCase)) throw new Exception($"JUMP {n.Name}: no step with this name");
                if (n.Kind == "step" && n.Initial && !ReferenceEquals(n, root[0])) throw new Exception($"only the first step is INITIAL ({n.Name})");
                n.Branches.ForEach(Jumps);
            }
        }
        Jumps(root);
        bool AllJump(SfcNode n) => n.Kind == "alternative" && n.Branches.All(b => b[^1].Kind == "jump" || AllJump(b[^1]));
        if (root[^1].Kind != "jump" && !AllJump(root[^1])) throw new Exception("the chain must end with a TRANSITION followed by JUMP <step> (usually back to the initial step)");
        return root;
    }

    // IEC 61131-3 structure rules: steps and transitions alternate, alternative branches start with a transition,
    // parallel branches start with a step.
    static void ValidateChain(List<SfcNode> seq, bool top)
    {
        if (top)
        {
            if (seq.Count == 0 || seq[0].Kind != "step") throw new Exception("the chain starts with the initial STEP");
            if (!seq[0].Initial) seq[0].Initial = true;
        }
        static bool AllJump(SfcNode n) => n.Kind == "alternative" && n.Branches.All(b => b[^1].Kind == "jump" || AllJump(b[^1]));
        string last = null;
        foreach (var n in seq)
        {
            if (last == "end") throw new Exception("nothing may follow an ALTERNATIVE whose branches all end with JUMP");
            var first = n.Kind switch { "alternative" => "transition", "parallel" => "step", "jump" => "jump", _ => n.Kind };
            var lastOf = n.Kind switch { "alternative" => "step", "parallel" => "step", _ => n.Kind };
            if (last == first && first != "jump") throw new Exception($"two {first}s in a row (at {n.Name ?? n.Kind}) - steps and transitions alternate");
            if (n.Kind == "alternative" && last != "step") throw new Exception("ALTERNATIVE must follow a STEP");
            if (n.Kind == "parallel" && last != "transition") throw new Exception("PARALLEL must follow a TRANSITION");
            if (n.Kind == "jump" && last != "transition") throw new Exception("JUMP must follow a TRANSITION");
            foreach (var b in n.Branches)
            {
                if (b.Count == 0) throw new Exception("empty BRANCH");
                var want = n.Kind == "alternative" ? "transition" : "step";
                if (b[0].Kind != want) throw new Exception($"every {n.Kind} BRANCH starts with a {want.ToUpperInvariant()}");
                if (b[^1].Kind == "transition") throw new Exception($"a {n.Kind} BRANCH ends with a STEP (or JUMP), not with a TRANSITION");
                if (n.Kind == "parallel" && b[^1].Kind == "jump") throw new Exception("a PARALLEL BRANCH cannot end with JUMP");
                ValidateChain(b, false);
            }
            if (n.Kind == "parallel" && n.Branches.Any(b => b.Count == 0)) throw new Exception("empty BRANCH");
            last = lastOf;
            if (n.Kind == "parallel") last = "step";
            if (AllJump(n)) last = "end";
        }
    }

    // ================================================================== model -> PLCopen SFC
    sealed class SfcBuilder
    {
        public XNamespace Ns;
        public int NextId = 1, Y;
        public readonly List<XElement> Out = new();
        public XElement StepTpl, TransTpl;
        public HashSet<string> TransitionObjects = new(StringComparer.OrdinalIgnoreCase);
    }

    static XElement BuildSfc(XElement oldSfc, List<SfcNode> chain, IEnumerable<string> transitionObjects = null)
    {
        var b = new SfcBuilder
        {
            TransitionObjects = new HashSet<string>(transitionObjects ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            Ns = oldSfc.Name.Namespace,
            StepTpl = oldSfc.Elements().FirstOrDefault(e => e.Name.LocalName == "step"),
            TransTpl = oldSfc.Elements().FirstOrDefault(e => e.Name.LocalName == "transition")
        };
        EmitSeq(b, chain, new List<string>(), 0);
        var sfc = new XElement(oldSfc.Name, oldSfc.Attributes(), b.Out);
        return sfc;
    }

    static XElement Pos(SfcBuilder b, int x) => new(b.Ns + "position", new XAttribute("x", x), new XAttribute("y", b.Y += 40));

    static XElement In(SfcBuilder b, IEnumerable<string> from)
    {
        var cp = new XElement(b.Ns + "connectionPointIn");
        foreach (var f in from) cp.Add(new XElement(b.Ns + "connection", new XAttribute("refLocalId", f)));
        return cp;
    }

    static XElement OutPt(SfcBuilder b) => new(b.Ns + "connectionPointOut");

    static XElement StBody(SfcBuilder b, string code) =>
        new(b.Ns + "ST", new XElement(Xhtml + "xhtml", code));

    // emits a sequence; 'from' = ids the first element connects to; returns the ids of the last element(s)
    static List<string> EmitSeq(SfcBuilder b, List<SfcNode> seq, List<string> from, int x)
    {
        var prev = from;
        foreach (var n in seq)
        {
            var id = (b.NextId++).ToString();
            switch (n.Kind)
            {
                case "step":
                {
                    var e = new XElement(b.Ns + "step", new XAttribute("localId", id), new XAttribute("name", n.Name),
                                         new XAttribute("initialStep", n.Initial ? "true" : "false"));
                    // keep extra attributes/children of the existing step format (e.g. size, addData) if present
                    if (b.StepTpl != null)
                    {
                        foreach (var at in b.StepTpl.Attributes().Where(at => at.Name.LocalName is "height" or "width")) e.SetAttributeValue(at.Name, at.Value);
                    }
                    e.Add(Pos(b, x));
                    if (prev.Count > 0) e.Add(In(b, prev));
                    e.Add(new XElement(b.Ns + "connectionPointOut", new XAttribute("formalParameter", "sfc")));
                    b.Out.Add(e);
                    if (n.Actions.Count > 0)
                    {
                        var ab = new XElement(b.Ns + "actionBlock", new XAttribute("localId", (b.NextId++).ToString()), Pos(b, x + 200), In(b, new[] { id }));
                        foreach (var (q, dur, act) in n.Actions)
                        {
                            var ae = new XElement(b.Ns + "action", new XAttribute("localId", (b.NextId++).ToString()), new XAttribute("qualifier", q));
                            if (dur != null) ae.SetAttributeValue("duration", dur);
                            ae.Add(new XElement(b.Ns + "relPosition", new XAttribute("x", 0), new XAttribute("y", 0)));
                            ae.Add(new XElement(b.Ns + "reference", new XAttribute("name", act)));
                            ae.Add(OutPt(b));
                            ab.Add(ae);
                        }
                        b.Out.Add(ab);
                    }
                    prev = new List<string> { id };
                    break;
                }
                case "transition":
                {
                    var e = new XElement(b.Ns + "transition", new XAttribute("localId", id));
                    if (b.TransTpl != null)
                        foreach (var at in b.TransTpl.Attributes().Where(at => at.Name.LocalName is "height" or "width")) e.SetAttributeValue(at.Name, at.Value);
                    e.Add(Pos(b, x));
                    if (prev.Count > 0) e.Add(In(b, prev));
                    e.Add(OutPt(b));
                    // name of a transition object of the POU -> reference; otherwise an ST expression. TwinCAT ignores inline ST in
                    // <condition>: the expression is an inVariable connected to the condition (the format of its own export)
                    if (b.TransitionObjects.Contains(n.Name))
                        e.Add(new XElement(b.Ns + "condition", new XElement(b.Ns + "reference", new XAttribute("name", n.Name))));
                    else
                    {
                        var vid = (b.NextId++).ToString();
                        b.Out.Add(new XElement(b.Ns + "inVariable", new XAttribute("localId", vid), Pos(b, x + 100), OutPt(b),
                                               new XElement(b.Ns + "expression", n.Name)));
                        e.Add(new XElement(b.Ns + "condition", In(b, new[] { vid })));
                    }
                    b.Out.Add(e);
                    prev = new List<string> { id };
                    break;
                }
                case "jump":
                {
                    var e = new XElement(b.Ns + "jumpStep", new XAttribute("localId", id), new XAttribute("targetName", n.Name), Pos(b, x));
                    if (prev.Count > 0) e.Add(In(b, prev));
                    b.Out.Add(e);
                    prev = new List<string> { id };       // inside a branch TwinCAT connects the jump to the convergence
                    break;
                }
                case "alternative":
                case "parallel":
                {
                    var alt = n.Kind == "alternative";
                    var div = new XElement(b.Ns + (alt ? "selectionDivergence" : "simultaneousDivergence"), new XAttribute("localId", id), Pos(b, x));
                    if (prev.Count > 0) div.Add(In(b, prev));
                    for (int k = 0; k < n.Branches.Count; k++) div.Add(new XElement(b.Ns + "connectionPointOut", new XAttribute("formalParameter", k.ToString())));
                    b.Out.Add(div);
                    var ends = new List<string>();
                    int bx = x;
                    foreach (var br in n.Branches)
                    {
                        ends.AddRange(EmitSeq(b, br, new List<string> { id }, bx));
                        bx += 200;
                    }
                    // TwinCAT needs the convergence also when branches end with a jump (else: "An item with the same key has
                    // already been added"); if all branches jump, nothing follows it
                    var convId = (b.NextId++).ToString();
                    var conv = new XElement(b.Ns + (alt ? "selectionConvergence" : "simultaneousConvergence"), new XAttribute("localId", convId), Pos(b, x));
                    foreach (var end in ends) conv.Add(In(b, new[] { end }));
                    conv.Add(OutPt(b));
                    b.Out.Add(conv);
                    prev = new List<string> { convId };
                    break;
                }
            }
        }
        return prev;
    }
}
