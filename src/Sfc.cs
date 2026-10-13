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
//   END_ALTERNATIVE                   followed by a TRANSITION (nothing, if every branch ends with JUMP);
//                                     or: every branch ends with a TRANSITION and a STEP follows (TwinCAT editor form)
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
                      "with 'BRANCH' ... 'END_ALTERNATIVE' / 'END_PARALLEL', 'JUMP Init'; under a step 'EMBEDDED _aN010_active' = its embedded action (keep " +
                      "these lines - also when renaming the step - or the action loses its step) and 'COMMENT text'); action=xml shows the raw PLCopen SFC body. " +
                      "Action objects shown as graphical or embedded are never 'empty/unused' - do not delete them. " +
                      "Create the POU first with tc_plc_create language=SFC, actions/transitions with code with tc_plc_create kind=action/transition " +
                      "(parentPath = the POU). Step names become variables of the POU: never name a step like a variable, action or keyword " +
                      "(Move, Error, Done ...) - use S_Init, S_Move ... Read before writing; check with tc_check afterwards. " +
                      "New steps get 'Duplicate on copy' (embedded actions are copied, not referenced, when the step is copied in the editor; duplicate=all " +
                      "also for existing steps, duplicate=none = TwinCAT default). " +
                      "action=renumber renames steps systematically (prefix + number: start, step, digits; default keeps the prefix, e.g. N010, N020 ...), " +
                      "optionally only 'steps' (list or range N020..N070), order=main (main sequence first, then the branches) or chain; it updates " +
                      "JUMP targets, step references in conditions and in the POU's action/transition code (N010.t), keeps embedded actions and comments " +
                      "and renames embedded actions _aN010_active -> _aN020_active (renameActions=false keeps them); always dryRun=true first and show the user the table. " +
                      "action=comments lists the steps with their comments plus the project's coding guidelines; with comments={\"N010\":\"text\"} it sets " +
                      "step comments (existing steps; write them in the language the guidelines require). " +
                      "action=flags shows the SFC flags (SFCInit, SFCReset, SFCError, SFCQuitError, SFCPause, SFCTrans, SFCCurrentStep, SFCTip, SFCTipMode, " +
                      "SFCErrorStep, SFCErrorPOU, SFCEnableLimit, SFCErrorAnalyzation ...); flags={\"SFCInit\":true} sets them - an enabled flag is also declared " +
                      "(Declare=true or a variable in the declaration), the result says which. All writes keep the previous version if TwinCAT rejects them.",
            Schema(Req(P("action", "string", "read, write, xml, renumber, comments or flags")),
                   Req(P("path", "string", "tree path of the SFC POU, e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Sequence")),
                   P("chain", "string", "write: the complete chain in the text form of action=read"),
                   P("dryRun", "boolean", "write/renumber/comments/flags: only show what would change"),
                   P("force", "boolean", "write: also when embedded step actions would lose their step; renumber: also when code outside the POU " +
                                         "references renamed steps; comments: also when they do not look like the language the guidelines require (only after asking the user)"),
                   P("duplicate", "string", "write: new (default) = new steps get 'Duplicate on copy', all = also existing steps, none = TwinCAT default"),
                   P("prefix", "string", "renumber: name prefix (default: the existing prefix of the steps, e.g. N)"),
                   P("start", "integer", "renumber: first number (default 10)"),
                   P("step", "integer", "renumber: increment (default 10)"),
                   P("digits", "integer", "renumber: minimum digits, zero-padded (default 3 -> N010)"),
                   P("steps", "string", "renumber: only these steps - comma-separated (N020,N030) or a range in the chosen order (N020..N070); default all"),
                   P("order", "string", "renumber: main (default: main sequence first, then the branches in order) or chain (order of action=read)"),
                   P("renameActions", "boolean", "renumber: rename embedded step actions _a<old>_active -> _a<new>_active (default true)"),
                   P("comments", "object", "comments: {\"N010\": \"Wait for part present\", ...} - empty text removes a comment"),
                   P("flags", "object", "flags: {\"SFCInit\": true, \"SFCReset\": false, ...}")),
            a => tc.Sta.Run(() => Sfc(a), 300_000));
    }

    // one exported SFC POU (PLCopen) with everything the write path needs
    sealed class SfcCtx
    {
        public string Path, PouName, Tmp;
        public string[] Parts;
        public int ProjIdx;
        public ITcSmTreeItem Plc;
        public XDocument Doc;
        public XElement Pou, Sfc;
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
            var ctx = new SfcCtx { Path = path, PouName = pouName, Tmp = tmp, Parts = parts, ProjIdx = projIdx, Plc = plc, Doc = doc, Pou = pou, Sfc = sfc };
            switch (action)
            {
                case "xml":
                    return Shorten(sfc.ToString(), 60_000);
                case "read":
                    return $"SFC {pouName}\n" + SfcToText(sfc) + DuplicationInfo(sfc) + SfcObjects(pou);
                case "write":
                {
                    var chain = Str(a, "chain") ?? throw new Exception("chain is required (text form as returned by action=read)");
                    var duplicate = (Str(a, "duplicate") ?? "new").ToLowerInvariant();
                    if (duplicate is not ("new" or "all" or "none")) throw new Exception("duplicate: new (default), all or none");
                    var root = ParseChain(chain);
                    CheckStepNames(root, pou);
                    var transitionObjects = pou.Descendants().Where(e => e.Name.LocalName == "transition" && e.Parent?.Name.LocalName == "transitions")
                                               .Select(e => (string)e.Attribute("name")).Where(n => n != null);
                    var original = new XDocument(doc);
                    var newSfc = BuildSfc(sfc, root, transitionObjects, out var orphans, duplicate);
                    if (orphans.Count > 0 && !Bool(a, "force", false))
                        throw new Exception($"Nothing was changed: the embedded step actions {string.Join(", ", orphans)} would no longer belong to any step. " +
                                            "Keep the 'EMBEDDED <name>' lines from action=read under the (renamed) step. If they really should go, " +
                                            "ask the user first, then write with force=true - their code is NOT deleted (action objects stay); never delete them as 'empty'.");
                    if (DryRun(a)) return "PROPOSAL (nothing written):\n" + newSfc;
                    sfc.ReplaceWith(newSfc);
                    return SfcReimport(ctx, original, $"SFC chain of {pouName} replaced.");
                }
                case "renumber": return SfcRenumber(ctx, a);
                case "comments": return SfcComments(ctx, a);
                case "flags": return SfcFlags(ctx, a);
                default: throw new Exception("action: read, write, xml, renumber, comments or flags");
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // The write path of every changing action: the modified export (c.Doc) replaces the POU. TwinCAT's import puts objects
    // without folder information into the project root and does not replace an object inside a folder: wrap the project
    // structure in the POU's folders, delete the POU, import it new; if TwinCAT rejects it, the original export is imported again.
    string SfcReimport(SfcCtx c, XDocument original, string done)
    {
        FixSfcFlags(c.Pou);
        var folders = c.Parts.Skip(c.ProjIdx + 1).Take(c.Parts.Length - c.ProjIdx - 2).ToList();
        if (!WrapInFolders(c.Doc, folders) || !WrapInFolders(original, folders))
            throw new Exception("the PLCopen export has no project structure - the POU cannot be re-imported into its folder; nothing was changed");
        c.Doc.Save(c.Tmp);
        var backup = c.Tmp + ".orig.xml";
        original.Save(backup);
        var parentPath = string.Join("^", c.Parts.Take(c.Parts.Length - 1));
        try { ((dynamic)tc.Item(parentPath)).DeleteChild(c.PouName); }
        catch { try { File.Delete(backup); } catch { } throw; }
        string failure = null;
        try { ComCall.Invoke(c.Plc, "PlcOpenImport", c.Tmp, 0); }
        catch (Exception ex) { failure = ex.InnerException?.Message ?? ex.Message; }
        if (failure == null && !ItemExists(c.Path)) failure = "the POU was not created (see the TwinCAT error list)";
        if (failure != null)
        {
            // put the original back
            try { if (ItemExists(c.Path)) ((dynamic)tc.Item(parentPath)).DeleteChild(c.PouName); } catch { }
            try { ComCall.Invoke(c.Plc, "PlcOpenImport", backup, 0); } catch { }
            tc.InvalidatePlcIndex();
            var restored = ItemExists(c.Path);
            if (restored) try { File.Delete(backup); } catch { }      // keep the backup if the restore failed
            throw new Exception($"TwinCAT did not accept the change: {failure}. " +
                                (restored ? $"The previous version of {c.PouName} was restored."
                                          : $"The previous version of {c.PouName} could NOT be restored - re-import {backup} with tc_import kind=plcopen."));
        }
        try { File.Delete(backup); } catch { }
        tc.InvalidatePlcIndex();
        var check = Sfc(new JsonObject { ["action"] = "read", ["path"] = c.Path });
        return $"{done} Check with tc_check.\n\n" + check;
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

    // element attributes in addData (TwinCAT 4026; the attribute descriptions are stored in the editor XML of the .TcPOU):
    //   38391c6d Name, 7d894980 Comment, 01580b27 Exclude (from build), bc882c11 Symbol (configuration), 6844a48e InitStep,
    //   62e1754b Duplication ("Duplicate on copy": copies the implementation of step actions/transitions with the element instead
    //   of referencing it), cacf1a68 MinTime, b693554c MaxTime, 700a583f MainAction, a6b08bd8 EntryAction, a2621e18 ExitAction,
    //   8294df19 Monitoring (transitions), 23bdaa98 Parallel (branches), e174fc0d Qualifier (action associations)
    const string NameGuid = "38391c6d-6d4a-42f8-8ee7-9f45e5adafa8", CommentGuid = "7d894980-aeea-405c-a0f6-e2b26429c58f",
                 ExcludeGuid = "01580b27-6378-448b-8ecb-0e4b795b58d6", SymbolGuid = "bc882c11-1e91-4dd8-a6b8-2075724ed18b",
                 InitStepGuid = "6844a48e-46c2-4cc8-a185-a478f3b99cc0", DuplicationGuid = "62e1754b-7629-4e63-9cec-10ae0c536f1f";
    static IEnumerable<(string Guid, string Value)> StepAttributes(XElement step) =>
        step.Elements().Where(x => x.Name.LocalName == "addData").Descendants().Where(x => x.Name.LocalName == "attribute")
            .Select(x => (((string)x.Attribute("guid") ?? "").ToLowerInvariant(), x.Value.Trim()));

    /// <summary>The attribute element with this guid in the addData of a step/element (or below an addData element), else null.</summary>
    static XElement ElementAttr(XElement container, string guid) =>
        container.Descendants().FirstOrDefault(x => x.Name.LocalName == "attribute" && string.Equals((string)x.Attribute("guid"), guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets an addData attribute, adds it to the attribute list if missing (a comment right after the name); false without a list.</summary>
    static bool SetElementAttr(XElement container, string guid, string value)
    {
        if (ElementAttr(container, guid) is { } at) { at.Value = value; return true; }
        var list = container.Descendants().FirstOrDefault(x => x.Name.LocalName == "attributes");
        if (list == null) return false;
        var ne = new XElement(list.Name.Namespace + "attribute", new XAttribute("guid", guid), value);
        var name = guid == CommentGuid ? ElementAttr(list, NameGuid) : null;
        if (name != null) name.AddAfterSelf(ne); else list.Add(ne);
        return true;
    }

    /// <summary>addData of a new step in the form of TwinCAT's own export.</summary>
    static XElement NewStepData(XNamespace ns, string name, bool initial, string comment, bool duplicate)
    {
        var list = new XElement("attributes", new XElement("attribute", new XAttribute("guid", NameGuid), name));
        if (!string.IsNullOrEmpty(comment)) list.Add(new XElement("attribute", new XAttribute("guid", CommentGuid), comment));
        list.Add(new XElement("attribute", new XAttribute("guid", ExcludeGuid), "FALSE"),
                 new XElement("attribute", new XAttribute("guid", SymbolGuid), "0"),
                 new XElement("attribute", new XAttribute("guid", InitStepGuid), initial ? "TRUE" : "FALSE"),
                 new XElement("attribute", new XAttribute("guid", DuplicationGuid), duplicate ? "TRUE" : "FALSE"));
        return new XElement(ns + "addData",
                            new XElement(ns + "data", new XAttribute("name", "http://www.3s-software.com/plcopenxml/sfc/element"),
                                         new XAttribute("handleUnknown", "implementation"), list));
    }

    // read: which steps copy their embedded actions when copied in the editor ("Duplicate on copy")
    static string DuplicationInfo(XElement sfc)
    {
        var on = new List<string>(); var off = new List<string>();
        foreach (var st in sfc.Elements().Where(e => e.Name.LocalName == "step"))
            (string.Equals(ElementAttr(st, DuplicationGuid)?.Value.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase) ? on : off).Add((string)st.Attribute("name") ?? "?");
        if (on.Count + off.Count == 0) return "";
        if (off.Count == 0) return "// Duplicate on copy: on for all steps\n";
        return "// Duplicate on copy (copying a step in the editor copies its embedded actions instead of referencing them): " +
               $"on: {(on.Count > 0 ? string.Join(", ", on) : "-")}; off: {string.Join(", ", off)} - action=write duplicate=all switches it on for all steps\n";
    }

    static HashSet<string> ActionObjectNames(XElement sfc)
    {
        var pou = sfc.Ancestors().FirstOrDefault(e => e.Name.LocalName == "pou");
        return new HashSet<string>(pou?.Descendants().Where(e => e.Name.LocalName == "action" && e.Parent?.Name.LocalName == "actions")
                                       .Select(e => (string)e.Attribute("name")).Where(n => n != null) ?? Enumerable.Empty<string>(),
                                   StringComparer.OrdinalIgnoreCase);
    }

    static string SfcToText(XElement sfc)
    {
        var g = Graph(sfc);
        var actionObjects = ActionObjectNames(sfc);
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
                        foreach (var (guid, val) in StepAttributes(e))
                        {
                            if (guid == CommentGuid && val.Length > 0) o.AppendLine($"{ind}  COMMENT {Regex.Replace(val, @"\s+", " ")}");
                            else if (guid != NameGuid && actionObjects.Contains(val)) o.AppendLine($"{ind}  EMBEDDED {val}");
                        }
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
                var body = x.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
                var lang = body?.Elements().FirstOrDefault(e => e.Name.LocalName != "addData")?.Name.LocalName ?? "?";
                string code;
                if (lang == "ST") { code = StText(body); if (string.IsNullOrWhiteSpace(code)) code = "(empty)"; }
                else code = $"(code in {lang} - graphical, not shown as text; it is NOT empty if the IDE shows elements - never delete it as unused)";
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
        public readonly List<string> Embedded = new();   // embedded step actions (main/entry/exit) - action objects like _aN010_active
        public string Comment;                            // step comment
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
                    case "EMBEDDED":
                        if (list.Count == 0 || list[^1].Kind != "step") throw new Exception($"line '{l}': EMBEDDED must follow a STEP");
                        if (!Regex.IsMatch(rest, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception($"line '{l}': EMBEDDED <action object name>, e.g. EMBEDDED _aN010_active");
                        list[^1].Embedded.Add(rest);
                        break;
                    case "COMMENT":
                        if (list.Count == 0 || list[^1].Kind != "step") throw new Exception($"line '{l}': COMMENT must follow a STEP");
                        list[^1].Comment = rest;
                        break;
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
                        throw new Exception($"line '{l}': unknown keyword {kw} (STEP, ACTION, EMBEDDED, COMMENT, TRANSITION, ALTERNATIVE, PARALLEL, BRANCH, END_ALTERNATIVE, END_PARALLEL, JUMP)");
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
            if (last == first && first != "jump") throw new Exception($"two {first}s in a row (at {n.Name ?? n.Kind}) - steps and transitions alternate");
            if (n.Kind == "alternative" && last != "step") throw new Exception("ALTERNATIVE must follow a STEP");
            if (n.Kind == "parallel" && last != "transition") throw new Exception("PARALLEL must follow a TRANSITION");
            if (n.Kind == "jump" && last != "transition") throw new Exception("JUMP must follow a TRANSITION");
            foreach (var b in n.Branches)
            {
                if (b.Count == 0) throw new Exception("empty BRANCH");
                var want = n.Kind == "alternative" ? "transition" : "step";
                if (b[0].Kind != want) throw new Exception($"every {n.Kind} BRANCH starts with a {want.ToUpperInvariant()}");
                if (n.Kind == "parallel" && b[^1].Kind == "jump") throw new Exception("a PARALLEL BRANCH cannot end with JUMP");
                ValidateChain(b, false);
            }
            last = n.Kind is "alternative" or "parallel" ? BlockEnd(n) : n.Kind;
            if (AllJump(n)) last = "end";
        }
    }

    // what a branch block ends with: alternative branches may end with a STEP (a TRANSITION follows END_ALTERNATIVE) or
    // all with a TRANSITION (a STEP follows - the form TwinCAT's editor draws); jumps do not count
    static string BlockEnd(SfcNode n)
    {
        var ends = n.Branches.Select(b => EndKind(b[^1])).Where(k => k != "jump").Distinct().ToList();
        if (ends.Count > 1) throw new Exception($"the branches of an {n.Kind.ToUpperInvariant()} must all end with a STEP or all with a TRANSITION (or JUMP)");
        if (n.Kind == "parallel" && ends.FirstOrDefault() == "transition") throw new Exception("PARALLEL branches end with a STEP");
        return ends.FirstOrDefault() ?? "end";
    }

    static string EndKind(SfcNode n) => n.Kind switch
    {
        "alternative" or "parallel" => n.Branches.All(b => b[^1].Kind == "jump") ? "jump" : BlockEnd(n),
        _ => n.Kind
    };

    // ================================================================== model -> PLCopen SFC
    sealed class SfcBuilder
    {
        public XNamespace Ns;
        public int NextId = 1, Y;
        public readonly List<XElement> Out = new();
        public XElement StepTpl, TransTpl;
        public HashSet<string> TransitionObjects = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, XElement> StepData = new(StringComparer.OrdinalIgnoreCase);   // addData of existing steps (embedded actions, comment)
        public readonly Dictionary<string, XElement> EmbeddedData = new(StringComparer.OrdinalIgnoreCase);   // embedded action name -> addData of its step
        public readonly HashSet<XElement> UsedData = new();
        public string Duplicate = "new";          // new: new steps get "Duplicate on copy"; all: also existing steps; none: TwinCAT default
    }

    static XElement BuildSfc(XElement oldSfc, List<SfcNode> chain, IEnumerable<string> transitionObjects = null) =>
        BuildSfc(oldSfc, chain, transitionObjects, out _);

    static XElement BuildSfc(XElement oldSfc, List<SfcNode> chain, IEnumerable<string> transitionObjects, out List<string> orphans, string duplicate = "new")
    {
        var b = new SfcBuilder
        {
            Duplicate = duplicate ?? "new",
            TransitionObjects = new HashSet<string>(transitionObjects ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            Ns = oldSfc.Name.Namespace,
            StepTpl = oldSfc.Elements().FirstOrDefault(e => e.Name.LocalName == "step"),
            TransTpl = oldSfc.Elements().FirstOrDefault(e => e.Name.LocalName == "transition")
        };
        foreach (var st in oldSfc.Elements().Where(e => e.Name.LocalName == "step"))
            if ((string)st.Attribute("name") is { } nm && st.Elements().FirstOrDefault(e => e.Name.LocalName == "addData") is { } ad)
                b.StepData[nm] = ad;
        var actionObjects = ActionObjectNames(oldSfc);
        foreach (var st in oldSfc.Elements().Where(e => e.Name.LocalName == "step"))
            if (st.Elements().FirstOrDefault(e => e.Name.LocalName == "addData") is { } ad)
                foreach (var (guid, val) in StepAttributes(st))
                    if (guid != NameGuid && actionObjects.Contains(val)) b.EmbeddedData[val] = ad;
        EmitSeq(b, chain, new List<string>(), 0);
        var sfc = new XElement(oldSfc.Name, oldSfc.Attributes(), b.Out);
        // embedded step actions that are no longer attached to any step would be orphaned (their code stays, but nothing runs it)
        var still = new HashSet<string>(sfc.Elements().Where(e => e.Name.LocalName == "step").SelectMany(st => StepAttributes(st)).Select(a => a.Value), StringComparer.OrdinalIgnoreCase);
        orphans = b.EmbeddedData.Keys.Where(k => !still.Contains(k)).OrderBy(k => k).ToList();
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
                    // embedded step actions and comment: follow the EMBEDDED names (also when the step was renamed), else the step name
                    XElement keep = null;
                    foreach (var emb in n.Embedded)
                    {
                        if (!b.EmbeddedData.TryGetValue(emb, out var ed))
                            throw new Exception($"STEP {n.Name}: EMBEDDED {emb} is not an embedded action of any step of this chart - attach new embedded actions in the IDE");
                        if (keep != null && ed != keep) throw new Exception($"STEP {n.Name}: its EMBEDDED actions belonged to different steps - keep them together");
                        keep = ed;
                    }
                    if (keep == null && n.Embedded.Count == 0 && b.StepData.TryGetValue(n.Name, out var byName) && !b.EmbeddedData.Values.Contains(byName)) keep = byName;
                    if (keep != null && !b.UsedData.Add(keep))
                    {
                        if (n.Embedded.Count > 0) throw new Exception($"STEP {n.Name}: the embedded actions {string.Join(", ", n.Embedded)} are already used by another step");
                        keep = null;                                                    // renamed step kept its data elsewhere
                    }
                    if (keep == null && !string.IsNullOrEmpty(n.Comment) && b.Duplicate == "none")
                        throw new Exception($"STEP {n.Name}: with duplicate=none COMMENT works only for existing steps (set comments of new steps in the IDE)");
                    if (keep != null)
                    {
                        var data = new XElement(keep);
                        foreach (var at in data.Descendants().Where(d => d.Name.LocalName == "attribute"))
                        {
                            var g = ((string)at.Attribute("guid") ?? "").ToLowerInvariant();
                            if (g == NameGuid) at.Value = n.Name;
                            else if (g == CommentGuid && n.Comment != null && Regex.Replace(at.Value.Trim(), @"\s+", " ") != n.Comment) at.Value = n.Comment;
                            else if (g == InitStepGuid) at.Value = n.Initial ? "TRUE" : "FALSE";        // the kept data may come from a former initial step
                        }
                        if (!string.IsNullOrEmpty(n.Comment) && ElementAttr(data, CommentGuid) == null) SetElementAttr(data, CommentGuid, n.Comment);
                        if (b.Duplicate == "all") SetElementAttr(data, DuplicationGuid, "TRUE");
                        e.Add(data);
                    }
                    else if (b.Duplicate != "none")
                        e.Add(NewStepData(b.Ns, n.Name, n.Initial, n.Comment, true));      // user preference: embedded actions are duplicated on copy
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
