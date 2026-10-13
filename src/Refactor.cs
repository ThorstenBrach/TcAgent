// Context-based refactoring: meaningful names and comments for variables, struct fields, enum values, function blocks,
// functions and methods - every single change is confirmed by the user (Apply / Apply all / Reject / Cancel).
//
//   tc_refactor action=analyze [target=selection|name|path]
//       what is selected/open in the editor (a marked range, a method, FB, function, struct or enum) with numbered code,
//       every declaration with its comment, findings (no comment, unclear name) and where a name is used outside
//       (callers, I/O links, persistent data, HMI) - the agent then proposes changes from the context
//   tc_refactor action=apply changes=[...]
//       { "object": "FB_Axis", "kind": "comment" | "rename", "target": "variable" | "member" | "pou" | "method",
//         "name": "x1", "newName": "bDoorClosed", "comment": "Door closed (sensor S12)", "reason": "..." }
//       each change is shown with its reason, the affected objects and warnings and only applied after the user's answer.
//       dryRun=true shows all edits as diffs without asking or writing.
//
// Renames are whole-word edits outside string literals: locals in the object (+ methods/actions/properties of an FB and
// derived FBs for FB variables); names visible outside (inputs/outputs, program/GVL variables, FBs, methods, functions,
// types) additionally in every object TwinCAT's cross references list (needs the VS extension TcAgentBridge).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterRefactor()
    {
        Add("tc_refactor", "Context-based refactoring of names and comments - ALWAYS confirmed by the user per change (dialog: Apply / Apply all / " +
                           "Reject / Cancel). action=analyze target=selection (default: the marked range or the object open/clicked in the editor - " +
                           "method, FB, function, struct, enum) or a name/tree path: numbered code, declarations with comments, findings (missing " +
                           "comments, unclear names) and external use (callers, I/O links, persistent, HMI). Then propose from the context: " +
                           "comments where missing (what it means, unit, range), and better names only where the name is really unclear - keep the " +
                           "project's naming convention (tc_coding_rules) - and call action=apply changes=[{object, kind: comment|rename, target: " +
                           "variable|member|pou|method, name, newName, comment, reason}]. Each change is asked separately; respect a rejection. " +
                           "Renames update all usages (outside the object via TwinCAT's cross references). Run tc_check afterwards.",
            Schema(Req(P("action", "string", "analyze or apply")),
                   P("target", "string", "analyze: 'selection' (default) or object name / tree path"),
                   P("changes", "array", "apply: [{object, kind: comment|rename, target: variable|member|pou|method, name, newName, comment, reason}]"),
                   P("dryRun", "boolean", "apply: only show the edits as diffs, ask nothing, write nothing")),
            a => (Str(a, "action") ?? "analyze").ToLowerInvariant() switch
            {
                "analyze" => RefactorAnalyze(a),
                "apply" => RefactorApply(a),
                _ => throw new Exception("action: analyze or apply")
            });
    }

    // ================================================================== analyze
    static readonly Regex UnclearName = new(@"^(?:[a-z]{1,2}\d*|tmp\w*|temp\w*|aux\d*|help\d*|hlp\d*|hilf\w*|var\d+|test\d*|dummy\d*|foo|bar|xx+|merker\d*|m\d+|flag\d*|x\d+|y\d+|b\d+|n\d+|i\d+)$",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

    string RefactorAnalyze(JsonObject a)
    {
        var target = Str(a, "target") ?? "selection";
        string selText = null, selPart = null;
        if (target.Equals("selection", StringComparison.OrdinalIgnoreCase))
        {
            var (text, pid) = tc.Sta.Run(() => (tc.EditorContext(), tc.AttachedPid));
            target = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("TIPC^"))
                     ?? throw new Exception("Nothing open/selected in the editor - click into the object or pass target=<name or path>");
            if (BridgeClient.Available(pid))
                try
                {
                    var c = AnalyzeCursor(BridgeClient.Call(pid, "context"), text);
                    if (!string.IsNullOrWhiteSpace(c.SelText)) { selText = c.SelText; selPart = c.Part; }
                }
                catch { }
        }
        var doc = BuildDoc(null);
        var pou = doc.Pous.Values.FirstOrDefault(p => p.Path.Equals(target, StringComparison.OrdinalIgnoreCase))
                  ?? (doc.Pous.TryGetValue(target, out var byName) ? byName : null)
                  ?? throw new Exception($"Object '{target}' not found");
        // a clicked FB covers its methods/properties/actions; a clicked method only itself
        var scope = new List<PlcDoc.Pou> { pou };
        if (pou.Owner == null) scope.AddRange(pou.Members.Select(k => doc.Pous[k]).Where(m => m.Kind is not "TRANSITION"));
        var o = new StringBuilder();
        o.AppendLine($"Refactoring scope: {pou.Key} [{pou.Kind}] {pou.Path}" + (selText != null ? $" - marked range in the {selPart}" : ""));
        var selWords = selText == null ? null : new HashSet<string>(Regex.Matches(selText, @"[A-Za-z_]\w*").Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
        var dir = SolutionDir();
        var hmiText = HmiTexts(dir);
        int nComment = 0, nName = 0;
        foreach (var p in scope)
        {
            o.AppendLine($"\n### {p.Key} [{p.Kind}]  {p.Path}");
            o.AppendLine("Description: " + (p.Doc.Length > 0 ? p.Doc : "(none - propose kind=comment target=" + (p.Owner == null ? "pou" : "method") + ")"));
            if (p.Doc.Length == 0) nComment++;
            if (p.Kind == "DUT")
            {
                var t = UserDoc.ParseDut(p.Name, p.Decl);
                o.AppendLine($"{t.Kind}{(t.BaseType.Length > 0 ? " " + t.BaseType : "")}:");
                foreach (var m in t.Members.Where(m => selWords == null || selWords.Contains(m.Name)))
                {
                    var flags = new List<string>();
                    if (string.IsNullOrWhiteSpace(m.Comment)) { flags.Add("no comment"); nComment++; }
                    if (UnclearName.IsMatch(m.Name)) { flags.Add("unclear name"); nName++; }
                    o.AppendLine($"  {m.Name}{(m.Type != null ? " : " + m.Type : "")}{(m.Value.Length > 0 ? " := " + m.Value : "")}  // {m.Comment}" +
                                 (flags.Count > 0 ? $"   <- {string.Join(", ", flags)}" : ""));
                }
            }
            else
            {
                var lines = p.Decl.Replace("\r\n", "\n").Split('\n');
                foreach (var v in p.Vars.Where(v => v.Section != "RETURN" && (selWords == null || selWords.Contains(v.Name))))
                {
                    var flags = new List<string>();
                    if (string.IsNullOrWhiteSpace(v.Comment)) { flags.Add("no comment"); nComment++; }
                    if (UnclearName.IsMatch(v.Name)) { flags.Add("unclear name"); nName++; }
                    var declLine = lines.FirstOrDefault(l => Regex.IsMatch(l, $@"^\s*{Regex.Escape(v.Name)}\b"));
                    if (declLine != null && Regex.IsMatch(declLine, @"\bAT\s+%", RegexOptions.IgnoreCase)) flags.Add("I/O (AT %) - links use the name");
                    if (Regex.IsMatch(p.Decl, $@"TcLinkTo[^}}]*\}}\s*\n\s*{Regex.Escape(v.Name)}\b", RegexOptions.IgnoreCase)) flags.Add("TcLinkTo attribute");
                    if (VarSectionHeader(p.Decl, v.Name) is { } hdr && Regex.IsMatch(hdr, @"PERSISTENT|RETAIN", RegexOptions.IgnoreCase)) flags.Add("persistent/retain - renaming loses the stored value");
                    var visible = IsVisibleOutside(p, v.Section);
                    if (visible) flags.Add("visible outside (callers/ADS)");
                    if (visible && hmiText.Any(h => Regex.IsMatch(h, $@"\.{Regex.Escape(v.Name)}\b"))) flags.Add("name found in HMI files");
                    o.AppendLine($"  {SecShort(v.Section),-7} {v.Name} : {v.Type}  // {v.Comment}" + (flags.Count > 0 ? $"   <- {string.Join(", ", flags)}" : ""));
                }
            }
            if (selText == null || p == pou)
            {
                o.AppendLine("Code:");
                var n = 0;
                foreach (var l in (p.Decl + "\n----\n" + (p.SfcText ?? p.Impl)).Replace("\r\n", "\n").Split('\n').Take(400)) o.AppendLine($"{++n,4}: {l}");
            }
        }
        if (selText != null) o.AppendLine("\nMarked text:\n" + selText);
        o.AppendLine($"\n{nComment} missing comments/descriptions, {nName} possibly unclear names. Propose changes with action=apply " +
                     "changes=[{object, kind, target, name, newName, comment, reason}] - only where it improves understanding; " +
                     "the user confirms each one. Names flagged 'visible outside', I/O, persistent or HMI need a strong reason.");
        if (CodingRules.Text(SolutionDir()) is { } rules)
        {
            rulesSeen = true;
            o.AppendLine("\nCoding guidelines of this project - names, comment language and comment placement MUST follow them " +
                         $"(variable comments are written {(CommentAbove(null) ? "as /// lines above the variable" : "at the end of the line")}; " +
                         "override per change with style=above|trailing):\n" + rules);
        }
        return o.ToString();
    }

    static string SecShort(string s) => s switch { "VAR_INPUT" => "in", "VAR_OUTPUT" => "out", "VAR_IN_OUT" => "in/out", "VAR_GLOBAL" => "global", "VAR_STAT" => "stat", "VAR_TEMP" => "temp", "VAR_INST" => "inst", _ => "var" };

    static bool IsVisibleOutside(PlcDoc.Pou p, string section) =>
        section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_GLOBAL" || p.Kind is "PROGRAM" or "GVL";

    // header line of the VAR block a variable is declared in (VAR RETAIN, VAR PERSISTENT ...)
    static string VarSectionHeader(string decl, string name)
    {
        string header = null;
        foreach (var raw in (decl ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.Trim();
            if (Regex.IsMatch(l, @"^VAR(_(INPUT|OUTPUT|IN_OUT|GLOBAL|STAT|TEMP|INST|EXTERNAL|CONFIG))?\b", RegexOptions.IgnoreCase)) header = l;
            else if (Regex.IsMatch(l, @"^END_VAR\b", RegexOptions.IgnoreCase)) header = null;
            else if (header != null && Regex.IsMatch(l, $@"^{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return header;
        }
        return null;
    }

    // texts of the HMI project files of the solution (symbol bindings) - only for warnings
    static List<string> HmiTexts(string dir)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                                       .Where(f => f.EndsWith(".content", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ||
                                                   f.EndsWith(".usercontrol", StringComparison.OrdinalIgnoreCase))
                                       .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Packages" + Path.DirectorySeparatorChar)).Take(2000))
                try { if (new FileInfo(f).Length < 5_000_000) list.Add(File.ReadAllText(f)); } catch { }
        }
        catch { }
        return list;
    }

    // ================================================================== apply
    sealed record Edit(string Path, string Part, string Old, string New);

    sealed class RefactorPlan
    {
        public string Title, Summary;
        public readonly List<Edit> Edits = new();
        public readonly List<string> Warnings = new();
        public string TreeRenamePath, TreeRenameTo;
    }

    string RefactorApply(JsonObject a)
    {
        var changes = a["changes"] as JsonArray ?? throw new Exception("changes is required: [{object, kind, target, name, newName, comment, reason}]");
        bool dry = DryRun(a);
        int pid = tc.Sta.Run(() => tc.AttachedPid);
        var o = new StringBuilder();
        bool all = false;
        int applied = 0, rejected = 0, failed = 0;
        var dir = SolutionDir();
        var hmi = HmiTexts(dir);
        for (int i = 0; i < changes.Count; i++)
        {
            if (changes[i] is not JsonObject c) continue;
            string S(string k) => c[k] is JsonValue v ? v.ToString().Trim() : null;
            RefactorPlan plan;
            try { plan = PlanChange(c, pid, hmi); }
            catch (Exception ex) { o.AppendLine($"[{i + 1}] NOT possible: {ex.Message}"); failed++; continue; }
            if (plan.Edits.Count == 0 && plan.TreeRenamePath == null) { o.AppendLine($"[{i + 1}] {plan.Title}: nothing to change"); continue; }
            var diff = new StringBuilder();
            foreach (var e in plan.Edits) diff.Append($"--- {e.Path} ({e.Part})\n").Append(LineDiff.Unified(e.Old, e.New, e.Part));
            if (dry) { o.AppendLine($"[{i + 1}] {plan.Title}\n{plan.Summary}\n{string.Join("\n", plan.Warnings.Select(w => "WARNING: " + w))}\n{diff}"); continue; }
            if (!all)
            {
                var question = new StringBuilder($"Change {i + 1} of {changes.Count}: {plan.Title}\n\n{plan.Summary}\n");
                if (!string.IsNullOrEmpty(S("reason"))) question.AppendLine("\nReason: " + S("reason"));
                foreach (var w in plan.Warnings) question.AppendLine("WARNING: " + w);
                var d = diff.ToString();
                question.AppendLine("\n" + (d.Length > 3000 ? d[..3000] + "\n... (" + plan.Edits.Count + " objects)" : d));
                var choice = Confirm.Choose("TcAgent refactoring - " + plan.Title, question.ToString(), pid);
                if (choice == null)
                    return o + $"\nNo way to ask the user (no dialog in Visual Studio, client cannot ask). Nothing more was changed. " +
                           "Show the proposals to the user in the chat and apply them only after the user agreed.";
                if (choice == "cancel") { o.AppendLine($"[{i + 1}] {plan.Title}: cancelled by the user - stopped, the remaining changes were not applied"); break; }
                if (choice == "reject") { o.AppendLine($"[{i + 1}] {plan.Title}: REJECTED by the user"); rejected++; continue; }
                if (choice == "all") all = true;
            }
            try
            {
                tc.Sta.Run(() =>
                {
                    if (plan.TreeRenamePath != null) ((dynamic)tc.Item(plan.TreeRenamePath)).Name = plan.TreeRenameTo;
                    foreach (var e in plan.Edits.OrderBy(x => x.Part == "sfc" ? 1 : 0))     // SFC chart last: its re-import takes the new declaration
                    {
                        var path = e.Path;
                        if (plan.TreeRenamePath != null && path.Equals(plan.TreeRenamePath, StringComparison.OrdinalIgnoreCase))
                            path = path[..(path.LastIndexOf('^') + 1)] + plan.TreeRenameTo;
                        else if (plan.TreeRenamePath != null && path.StartsWith(plan.TreeRenamePath + "^", StringComparison.OrdinalIgnoreCase))
                            path = plan.TreeRenamePath[..(plan.TreeRenamePath.LastIndexOf('^') + 1)] + plan.TreeRenameTo + path[plan.TreeRenamePath.Length..];
                        if (e.Part == "sfc") { Sfc(new JsonObject { ["action"] = "write", ["path"] = path, ["chain"] = e.New }); continue; }
                        var it = tc.Item(path);
                        if (e.Part == "declaration") ((ITcPlcDeclaration)it).DeclarationText = e.New;
                        else ((ITcPlcImplementation)it).ImplementationText = e.New;
                    }
                    return 0;
                });
                tc.InvalidatePlcIndex();
                applied++;
                o.AppendLine($"[{i + 1}] {plan.Title}: applied ({plan.Edits.Count} object part(s))");
            }
            catch (Exception ex) { failed++; o.AppendLine($"[{i + 1}] {plan.Title}: FAILED while writing: {ex.InnerException?.Message ?? ex.Message} - check the objects listed above"); }
        }
        return (dry ? "PROPOSAL (nothing written, nothing asked):\n" : $"{applied} applied, {rejected} rejected, {failed} not possible.\n") + o +
               (applied > 0 ? "\nCheck with tc_check." : "");
    }

    RefactorPlan PlanChange(JsonObject c, int pid, List<string> hmi)
    {
        string S(string k) => c[k] is JsonValue v ? v.ToString().Trim() : null;
        var kind = (S("kind") ?? "comment").ToLowerInvariant();
        var target = (S("target") ?? "variable").ToLowerInvariant();
        var objName = S("object") ?? throw new Exception("object is required");
        var name = S("name");
        var doc = BuildDoc(null);
        var pou = doc.Pous.Values.FirstOrDefault(p => p.Path.Equals(objName, StringComparison.OrdinalIgnoreCase))
                  ?? (doc.Pous.TryGetValue(objName, out var bn) ? bn : null) ?? throw new Exception($"object {objName} not found");
        var plan = new RefactorPlan();
        if (kind == "comment")
        {
            var comment = S("comment") ?? throw new Exception("comment is required");
            if (target is "pou" or "method")
            {
                var t = target == "method" && name != null && doc.Pous.TryGetValue((pou.Owner ?? pou.Name) + "." + name, out var m) ? m : pou;
                plan.Title = $"Description of {t.Key}";
                plan.Summary = $"/// {comment}";
                if (t.Decl.Trim().Length > 0) plan.Edits.Add(new Edit(t.Path, "declaration", t.Decl, SetDocComment(t.Decl, comment)));
                else if (t.Lang == "ST") plan.Edits.Add(new Edit(t.Path, "implementation", t.Impl, SetTopComment(t.Impl, comment)));   // action/transition: no declaration part
                else throw new Exception($"{t.Key} has no declaration and a graphical body ({t.Lang}) - no place for a description");
            }
            else
            {
                if (string.IsNullOrEmpty(name)) throw new Exception("name is required");
                plan.Title = $"Comment for {pou.Key}.{name}";
                bool above = CommentAbove(S("style"));
                plan.Summary = above ? $"/// {comment}\n{name}" : $"{name}  // {comment}";
                var nd = above ? SetAboveComment(pou.Decl, name, comment, pou.Kind == "DUT") : SetLineComment(pou.Decl, name, comment, pou.Kind == "DUT");
                if (nd == pou.Decl) throw new Exception($"declaration of {name} not found in {pou.Key}");
                plan.Edits.Add(new Edit(pou.Path, "declaration", pou.Decl, nd));
            }
            return plan;
        }
        if (kind != "rename") throw new Exception("kind: comment or rename");
        var newName = S("newName") ?? throw new Exception("newName is required");
        if (!Regex.IsMatch(newName, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception($"'{newName}' is no valid identifier");
        var comment2 = S("comment");
        if (target is "variable" or "member")
        {
            if (string.IsNullOrEmpty(name)) throw new Exception("name is required");
            plan.Title = $"Rename {pou.Key}.{name} -> {newName}";
            var v = pou.Vars.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var isDutMember = pou.Kind == "DUT";
            if (v == null && !isDutMember) throw new Exception($"{name} is not declared in {pou.Key}");
            if (v != null && (v.Section == "RETURN")) throw new Exception("the return value has the name of the method/function");
            if (pou.Vars.Any(x => x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new Exception($"{newName} is already declared in {pou.Key}");
            var hdr = VarSectionHeader(pou.Decl, name);
            if (hdr != null && Regex.IsMatch(hdr, @"PERSISTENT|RETAIN", RegexOptions.IgnoreCase)) plan.Warnings.Add("persistent/retain variable: the stored value is lost after the rename");
            if (Regex.IsMatch(pou.Decl, $@"^\s*{Regex.Escape(name)}\b[^;\n]*\bAT\s+%", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                plan.Warnings.Add("I/O variable (AT %): links in the I/O configuration use the name - check them after the rename");
            // local scope: the object, its members, and for FB variables also derived FBs and their members
            var local = new List<PlcDoc.Pou> { pou };
            var owner = pou.Owner != null ? doc.Pous[pou.Owner] : pou;
            bool fbVar = pou.Owner == null && pou.Kind is "FUNCTION_BLOCK" or "PROGRAM";
            if (fbVar)
            {
                local.AddRange(pou.Members.Select(k => doc.Pous[k]));
                foreach (var d in doc.Pous.Values.Where(x => x.Owner == null && Derives(doc, x, pou.Name)))
                { local.Add(d); local.AddRange(d.Members.Select(k => doc.Pous[k])); }
            }
            foreach (var p in local)
            {
                if (p != pou && p.Vars.Any(x => x.Section != "RETURN" && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { plan.Warnings.Add($"{p.Key} declares its own '{name}' - not changed there"); continue; }
                AddRenameEdits(plan, p, name, newName, RenameMode.Local);
            }
            if (isDutMember && !plan.Edits.Any(e => e.Path == pou.Path)) throw new Exception($"{name} is not declared in {pou.Key}");
            if (comment2 != null && plan.Edits.FirstOrDefault(e => e.Path == pou.Path && e.Part == "declaration") is { } de)
            { plan.Edits.Remove(de); plan.Edits.Add(de with { New = CommentAbove(S("style")) ? SetAboveComment(de.New, newName, comment2, isDutMember) : SetLineComment(de.New, newName, comment2, isDutMember) }); }
            // visible outside: callers (inst.name, named arguments), program/GVL access, struct members (x.name)
            bool outside = isDutMember || (v != null && IsVisibleOutside(pou, v.Section)) ||
                           (pou.Kind is "METHOD" or "PROPERTY" && v?.Section is "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT");
            if (outside)
            {
                var refObjs = XrefObjects(pid, (pou.Owner != null ? pou.Owner + "." : "") + pou.Name + "." + name, name, doc, local);
                foreach (var p in refObjs)
                {
                    if (pou.Kind == "GVL" && p.Vars.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    { plan.Warnings.Add($"{p.Key} declares its own '{name}' - only {pou.Name}.{name} is changed there"); AddRenameEdits(plan, p, name, newName, RenameMode.Qualified, qualifier: pou.Name); continue; }
                    if (pou.Kind is "GVL") AddRenameEdits(plan, p, name, newName, RenameMode.Local, qualifier: pou.Name);
                    else if (pou.Kind is "PROGRAM" && v?.Section is not ("VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT")) AddRenameEdits(plan, p, name, newName, RenameMode.Qualified, qualifier: pou.Name);
                    else AddRenameEdits(plan, p, name, newName, RenameMode.Member);
                }
                if (isDutMember && refObjs.Count > 0) plan.Warnings.Add($"every '.{name}' in the referencing objects is renamed - check the diff for members of other types with the same name");
                if (isDutMember && Regex.IsMatch(pou.Decl, @":\s*\(")) plan.Warnings.Add("enum value: uses without the type prefix (E_Type.Value) are not renamed - tc_check will show them");
                if (hmi.Any(h => Regex.IsMatch(h, $@"\.{Regex.Escape(name)}\b"))) plan.Warnings.Add($"'{name}' appears in HMI files - bindings to this symbol must be adapted by hand");
                plan.Warnings.Add("visible outside: ADS clients, HMI or other PLCs that use the symbol name are not changed");
            }
            plan.Summary = $"{name} -> {newName}" + (comment2 != null ? $"  // {comment2}" : "") + $"\nAffected: {string.Join(", ", plan.Edits.Select(e => e.Path.Split('^').Last() + " (" + (e.Part.Length > 4 ? e.Part[..4] : e.Part) + ")").Distinct())}";
            return plan;
        }
        if (target is "pou" or "method")
        {
            var t = target == "method" ? (name != null && doc.Pous.TryGetValue((pou.Owner ?? pou.Name) + "." + name, out var mm) ? mm : pou) : pou;
            var oldName = t.Name;
            plan.Title = $"Rename {t.Kind.ToLowerInvariant()} {t.Key} -> {newName}";
            if (doc.Pous.ContainsKey(t.Owner != null ? t.Owner + "." + newName : newName)) throw new Exception($"{newName} already exists");
            plan.TreeRenamePath = t.Path;
            plan.TreeRenameTo = newName;
            // header of the object itself
            var hd = Regex.Replace(t.Decl, $@"^(\s*(?:\{{[^}}]*\}}\s*)*(?:FUNCTION_BLOCK|FUNCTION|PROGRAM|METHOD|PROPERTY|INTERFACE|TYPE)\b(?:\s+(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|ABSTRACT|FINAL))*\s+){Regex.Escape(oldName)}\b",
                                   m => m.Groups[1].Value + newName, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (comment2 != null) hd = SetDocComment(hd, comment2);
            if (hd != t.Decl) plan.Edits.Add(new Edit(t.Path, "declaration", t.Decl, hd));
            // the return value of a function/method has its name
            if (t.Kind is "FUNCTION" or "METHOD" or "PROPERTY") AddRenameEdits(plan, t, oldName, newName, RenameMode.Local, implOnly: true);
            var refObjs = XrefObjects(pid, t.Owner != null ? t.Owner + "." + oldName : oldName, oldName, doc, new List<PlcDoc.Pou> { t });
            if (t.Owner != null && doc.Pous.TryGetValue(t.Owner, out var ownerPou))   // calls inside the FB itself (Method(), THIS^.Method())
                foreach (var p in ownerPou.Members.Select(k => doc.Pous[k]).Append(ownerPou).Where(p => p != t)) AddRenameEdits(plan, p, oldName, newName, RenameMode.Plain);
            if (t.Owner != null && doc.Pous.TryGetValue(t.Owner, out var op))
            {
                foreach (var d in doc.Pous.Values.Where(x => x.Owner == null && Derives(doc, x, t.Owner)))
                    foreach (var p in d.Members.Select(k => doc.Pous[k]).Append(d))
                        if (p.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase) && p.Owner != null) plan.Warnings.Add($"{p.Key} overrides it - rename it separately");
                        else AddRenameEdits(plan, p, oldName, newName, RenameMode.Plain);
                var related = doc.Pous.Values.Where(x => x != t && x.Owner != null && x.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase) &&
                                                         doc.Pous.TryGetValue(x.Owner, out var xo) &&
                                                         (xo.Implements.Contains(t.Owner, StringComparer.OrdinalIgnoreCase) || Derives(doc, op, xo.Name) ||
                                                          op.Implements.Contains(xo.Name, StringComparer.OrdinalIgnoreCase)))
                                         .Select(x => x.Key).ToList();
                if (related.Count > 0) plan.Warnings.Add($"same method in base/interface/implementing objects ({string.Join(", ", related)}) - rename them separately or the build fails");
            }
            foreach (var p in refObjs) AddRenameEdits(plan, p, oldName, newName, t.Owner != null ? RenameMode.Member : RenameMode.Plain);
            plan.Warnings.Add("the object is renamed in the project tree; libraries/other projects that use the old name are not changed");
            if (t.Kind is "FUNCTION_BLOCK" or "PROGRAM" && hmi.Any(h => h.Contains("." + oldName + ".") || h.Contains(oldName + ".")))
                plan.Warnings.Add($"'{oldName}' appears in HMI files - bindings must be adapted by hand");
            plan.Summary = $"{oldName} -> {newName}\nAffected: {string.Join(", ", plan.Edits.Select(e => e.Path.Split('^').Last() + " (" + (e.Part.Length > 4 ? e.Part[..4] : e.Part) + ")").Distinct())}";
            return plan;
        }
        throw new Exception("target: variable, member, pou or method");
    }

    static bool Derives(PlcDoc doc, PlcDoc.Pou p, string baseName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var t = p; t?.Extends != null && seen.Add(t.Name); t = doc.Pous.TryGetValue(t.Extends, out var b) ? b : null)
            if (t.Extends.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // objects that reference a symbol according to TwinCAT's cross references (bridge); without the bridge renaming is refused
    List<PlcDoc.Pou> XrefObjects(int pid, string qualified, string plain, PlcDoc doc, List<PlcDoc.Pou> exclude)
    {
        if (!BridgeClient.Available(pid)) throw new Exception("renaming a name that is visible outside needs TwinCAT's cross references (VS extension TcAgentBridge) - not available");
        var result = new List<PlcDoc.Pou>();
        foreach (var name in new[] { qualified, plain }.Distinct())
        {
            JsonObject r;
            try { r = BridgeClient.Call(pid, "xref", new JsonObject { ["name"] = name, ["max"] = 2000 }, 120_000); }
            catch (Exception ex) { throw new Exception("cross references not available: " + ex.Message); }
            foreach (var e in r["entries"] as JsonArray ?? new JsonArray())
                foreach (var x in e?["refs"] as JsonArray ?? new JsonArray())
                {
                    var loc = x?["location"]?.ToString();
                    if (string.IsNullOrEmpty(loc)) continue;
                    foreach (var path in tc.Sta.Run(() => tc.ResolvePlcPathsPublic(loc)))
                    {
                        var p = doc.Pous.Values.FirstOrDefault(q => q.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (p != null && !exclude.Contains(p) && !result.Contains(p)) result.Add(p);
                    }
                }
            if (result.Count > 0) break;     // the qualified name was found - do not widen to every symbol with the same name
        }
        return result;
    }

    // Local:  the name as a plain identifier (not after '.', except THIS^./SUPER^.) - inside the declaring object
    // Member: only after '.' (inst.name) or as named argument '(name :=' / ', name =>' - in other objects
    // Plain:  every whole-word occurrence - type and function names
    // Qualified: only after 'qualifier.' (PRG.name, GVL.name); with Local mode a qualifier is accepted in addition (GVL variables)
    enum RenameMode { Local, Member, Plain, Qualified }

    // whole-word replacement outside string literals and comments
    void AddRenameEdits(RefactorPlan plan, PlcDoc.Pou p, string name, string newName, RenameMode mode, bool implOnly = false, string qualifier = null)
    {
        var parts = new List<(string Path, string Part, string Text)>();
        if (!implOnly) parts.Add((p.Path, "declaration", p.Decl));
        if (p.Kind == "PROPERTY")      // the model joins the Get/Set code - every accessor is written on its own
            parts.AddRange(tc.Sta.Run(() =>
            {
                var acc = new List<(string, string, string)>();
                foreach (var ch in TwinCatSession.Children(tc.Item(p.Path)))
                    if (ch is ITcPlcImplementation ci) acc.Add((ch.PathName, "implementation", ci.ImplementationText));
                return acc;
            }));
        else parts.Add((p.Path, "implementation", p.Impl));
        // SFC: transition conditions and embedded code live in the chart - rewritten through tc_sfc write (after the declaration)
        if (p.Lang == "SFC" && !string.IsNullOrEmpty(p.SfcText) && !p.SfcText.StartsWith("(chart")) parts.Add((p.Path, "sfc", p.SfcText));
        else if (p.Lang != "ST" && string.IsNullOrEmpty(p.Impl) && p.Kind is "PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION" or "METHOD" or "ACTION")
            plan.Warnings.Add($"{p.Key} has a graphical body ({p.Lang}) - it is not changed; check it by hand");
        foreach (var (path, part, text) in parts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var existing = plan.Edits.FirstOrDefault(e => e.Path == path && e.Part == part);
            var src = existing?.New ?? text;
            var res = part == "sfc"      // chart text: COMMENT lines are prose, not code
                ? string.Join("\n", src.Split('\n').Select(l => l.TrimStart().StartsWith("COMMENT ") ? l : ReplaceIdentifier(l, name, newName, mode, qualifier)))
                : ReplaceIdentifier(src, name, newName, mode, qualifier);
            if (res == src) continue;
            if (existing != null) plan.Edits.Remove(existing);
            plan.Edits.Add(new Edit(path, part, existing?.Old ?? text, res));
        }
    }

    static readonly Regex Tokens = new(@"\(\*.*?\*\)|//[^\n]*|'(?:\$.|[^'])*'|""(?:\$.|[^""])*""|[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Singleline | RegexOptions.Compiled);

    static string ReplaceIdentifier(string text, string name, string newName, RenameMode mode, string qualifier = null)
    {
        return Tokens.Replace(text, m =>
        {
            var tok = m.Value;
            if (!tok.Equals(name, StringComparison.OrdinalIgnoreCase)) return tok;
            if (mode == RenameMode.Plain) return newName;
            int i = m.Index - 1;
            while (i >= 0 && text[i] is ' ' or '\t') i--;
            bool afterDot = i >= 0 && text[i] == '.';
            bool qualified = afterDot && qualifier != null && Regex.IsMatch(text[..i], $@"(^|[^\w.^]){Regex.Escape(qualifier)}\s*$", RegexOptions.IgnoreCase);
            if (mode == RenameMode.Qualified) return qualified ? newName : tok;
            if (mode == RenameMode.Local)
            {
                if (!afterDot || qualified) return newName;
                return Regex.IsMatch(text[..(i + 1)].TrimEnd(), @"\b(THIS|SUPER)\s*\^\s*\.$", RegexOptions.IgnoreCase) ? newName : tok;
            }
            // Member
            if (afterDot) return newName;
            int j = i;
            while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
            var after = text[(m.Index + m.Length)..].TrimStart();
            bool namedArg = j >= 0 && text[j] is '(' or ',' && (after.StartsWith(":=") || after.StartsWith("=>"));
            return namedArg ? newName : tok;
        });
    }

    // '/// comment' lines directly above the header keyword (replacing an existing /// block)
    internal static string SetDocComment(string decl, string comment)
    {
        var nl = decl.Contains("\r\n") ? "\r\n" : "\n";
        var lines = decl.Replace("\r\n", "\n").Split('\n').ToList();
        int h = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*(FUNCTION_BLOCK|FUNCTION|PROGRAM|METHOD|PROPERTY|INTERFACE|TYPE)\b", RegexOptions.IgnoreCase));
        if (h < 0) h = 0;
        int start = h;
        while (start > 0 && (lines[start - 1].TrimStart().StartsWith("///") || lines[start - 1].TrimStart().StartsWith("{"))) start--;
        var keep = lines.Skip(start).Take(h - start).Where(l => !l.TrimStart().StartsWith("///")).ToList();     // pragmas stay
        var docLines = Wrap(comment.Replace("\r\n", "\n"), 100).Select(l => "/// " + l).ToList();
        lines.RemoveRange(start, h - start);
        lines.InsertRange(start, docLines.Concat(keep));
        return string.Join(nl, lines);
    }

    // /// block at the top of an implementation (actions, transitions) - replaces an existing one
    internal static string SetTopComment(string impl, string comment)
    {
        var nl = impl.Contains("\r\n") ? "\r\n" : "\n";
        var lines = impl.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].TrimStart().StartsWith("///")) lines.RemoveAt(0);
        lines.InsertRange(0, Wrap(comment.Replace("\r\n", "\n"), 100).Select(l => "/// " + l));
        return string.Join(nl, lines);
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var para in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var w in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + w.Length + 1 > width) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            yield return line.ToString();
        }
    }

    // '// comment' at the end of the declaration line of a variable, struct field or enum value (replacing a trailing comment)
    // aligns a new trailing comment with the comments of the neighbouring declarations in the same VAR block
    static string CommentPad(string[] lines, int at, int codeLen)
    {
        foreach (var dir in new[] { -1, 1 })
            for (int j = at + dir; j >= 0 && j < lines.Length; j += dir)
            {
                var l = lines[j];
                if (l == null) continue;
                if (Regex.IsMatch(l, @"^\s*(END_)?(VAR\w*|STRUCT|TYPE)\b", RegexOptions.IgnoreCase)) break;
                var m = Regex.Match(l, @"^(\s*\w[^/]*?;)(\s*)//");
                if (m.Success && !l.Contains('\t'))
                {
                    int col = m.Groups[1].Length + m.Groups[2].Length;
                    if (col > codeLen) return new string(' ', col - codeLen);
                }
            }
        return "   ";
    }

    /// <summary>Placement of variable comments: style=above|trailing, otherwise from the coding guidelines (/// mentioned = above).</summary>
    bool CommentAbove(string style) => style?.ToLowerInvariant() switch
    {
        "above" or "doc" => true,
        "trailing" or "line" => false,
        _ => CodingRules.Text(SolutionDir())?.Contains("///") == true
    };

    // '/// comment' line(s) above the declaration of a variable, struct field or enum value (replacing /// lines there
    // and a trailing comment on the line)
    internal static string SetAboveComment(string decl, string name, string comment, bool dut)
    {
        var nl = decl.Contains("\r\n") ? "\r\n" : "\n";
        var lines = decl.Replace("\r\n", "\n").Split('\n').ToList();
        comment = comment.Replace("\r\n", "\n");
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!Regex.IsMatch(l, $@"^\s*{Regex.Escape(name)}\s*(,|:|AT\b|\(|$)", RegexOptions.IgnoreCase) &&
                !(dut && Regex.IsMatch(l, $@"^\s*(\(\s*)?{Regex.Escape(name)}\s*(:=|,|\)|$)", RegexOptions.IgnoreCase))) continue;
            var (code, old) = UserDoc.SplitComment(l);
            if (old != null && Regex.Replace(l[code.Length..], @"\(\*.*?\*\)|//.*", "").Trim().Length > 0) code = l;
            if (Regex.IsMatch(code, $@"^\s*{Regex.Escape(name)}\s*,", RegexOptions.IgnoreCase) && Regex.IsMatch(code, @":(?!=)")) continue;
            var indent = Regex.Match(l, @"^\s*").Value;
            lines[i] = code.TrimEnd();
            int start = i;
            while (start > 0 && lines[start - 1].TrimStart().StartsWith("///")) start--;
            lines.RemoveRange(start, i - start);
            lines.InsertRange(start, Wrap(comment, 100).Select(c => indent + "/// " + c));
            return string.Join(nl, lines);
        }
        return decl;
    }

    internal static string SetLineComment(string decl, string name, string comment, bool dut)
    {
        var nl = decl.Contains("\r\n") ? "\r\n" : "\n";
        var lines = decl.Replace("\r\n", "\n").Split('\n');
        comment = Regex.Replace(comment, @"\s+", " ").Replace("*)", "* )");
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (!Regex.IsMatch(l, $@"^\s*{Regex.Escape(name)}\s*(,|:|AT\b|\(|$)", RegexOptions.IgnoreCase) &&
                !(dut && Regex.IsMatch(l, $@"^\s*(\(\s*)?{Regex.Escape(name)}\s*(:=|,|\)|$)", RegexOptions.IgnoreCase))) continue;
            // the line must be code (not inside a comment) - strip a trailing // or (* *) comment
            var (code, old) = UserDoc.SplitComment(l);
            if (old != null && Regex.Replace(l[code.Length..], @"\(\*.*?\*\)|//.*", "").Trim().Length > 0) code = l;   // code after an inline (* *) - keep it
            if (Regex.IsMatch(code, $@"^\s*{Regex.Escape(name)}\s*,", RegexOptions.IgnoreCase) && Regex.IsMatch(code, @":(?!=)")) continue;   // 'a, b : INT;' - skip multi-declarations
            // a /// line directly above documents the variable as well - drop it, the new comment replaces it
            if (i > 0 && lines[i - 1].TrimStart().StartsWith("///")) lines[i - 1] = null;
            lines[i] = code.TrimEnd() + CommentPad(lines, i, code.TrimEnd().Length) + "// " + comment;
            return string.Join(nl, lines.Where(x => x != null));
        }
        return decl;
    }
}
