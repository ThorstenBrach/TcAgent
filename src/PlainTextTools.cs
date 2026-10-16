// tc_plaintext: PLC code as plain-text ST files and back for agents (PlainText.cs, format docs/PLAINTEXT.md).
//
// export/check work on the project files like TcAgent.exe st export/check. import plans the changes on the files and then
//  - writes them through the connected IDE when the solution is open there (never behind the IDE's back): changed
//    declarations/implementations with the same write path as tc_plc_write, new methods/properties/actions/accessors with
//    CreateChild like tc_plc_create, new objects as generated .TcPOU/.TcGVL/.TcDUT/.TcIO files imported like tc_import
//    kind=pou_files - then tc_save writes the project files;
//  - writes the files directly when the solution is not open in any IDE;
//  - refuses when the solution is open in another IDE than the connected one.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterPlainText()
    {
        Add("tc_plaintext", "PLC code as plain-text ST files (like a CODESYS/Plaincat text export) - for reviews, diffs, Git and bulk edits. " +
                            "One .st file per POU/GVL/DUT/interface in <folder>\\<PLC project>\\<folder in the project>\\<name>.st, methods, " +
                            "properties (GET/SET) and actions inside the POU before END_FUNCTION_BLOCK, each closed by END_METHOD/END_PROPERTY/END_ACTION; " +
                            "<folder>\\<PLC project>\\references.json lists the library references of the .plcproj (Name, Version, " +
                            "DistributorName, Namespace). action=export writes the files (only changed ones; clean=true removes .st files " +
                            "without object, never references.json); action=check lists the differences between the folder and the project " +
                            "(.st files and references.json); action=import (ignores references.json) takes changed .st files back into the " +
                            "EXISTING project: changed declarations/implementations are replaced (Ids kept), new methods/properties/actions and " +
                            "new .st files are created, nothing is deleted. A '// @implementation' line separates declaration and implementation " +
                            "where it is not clear (keep it); '// @implementation SFC - ...' marks a graphical body that stays as it is. " +
                            "Use import with dryRun=true first. When the solution is open in the connected IDE the import writes through the IDE " +
                            "(then tc_save and tc_check); export/check read the files on disk - call tc_save first if there are unsaved changes.",
            Schema(Req(P("action", "string", "export, check or import")),
                   P("solution", "string", "Path of the .sln (default: the solution of the connected IDE)"),
                   P("plc", "string", "Only these PLC projects, comma-separated (plcproj name or PLC project name; default: all)"),
                   P("folder", "string", "Text folder (default: <solution folder>\\st)"),
                   P("clean", "boolean", "export: also remove .st files whose object no longer exists"),
                   P("dryRun", "boolean", "import: only list the changes, write nothing")),
            a => PlainTextTool(a));
    }

    string PlainTextTool(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").Trim().ToLowerInvariant();
        if (action is not ("export" or "check" or "import")) throw new Exception("action: export, check or import");
        string attached = null;
        try { attached = tc.Sta.Run(() => (string)tc.RequireDte().Solution.FullName, 30_000); } catch { }
        if (string.IsNullOrEmpty(attached)) attached = null;
        var sln = Str(a, "solution") is { Length: > 0 } s ? Path.GetFullPath(s) : attached != null ? Path.GetFullPath(attached)
                  : throw new Exception("No solution: pass solution (path of the .sln) or connect to the IDE (tc_attach).");
        if (!File.Exists(sln)) throw new Exception("Solution not found: " + sln);
        var slnDir = Path.GetDirectoryName(sln) ?? ".";
        var folder = Path.GetFullPath(Str(a, "folder") is { Length: > 0 } f ? f : Path.Combine(slnDir, "st"));
        var plcs = (Str(a, "plc") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var o = new StringBuilder();

        if (action != "import")
        {
            var rep = PlainText.Export(slnDir, plcs, folder, action == "check", Bool(a, "clean", false));
            AppendReport(o, rep);
            if (rep.Errors.Count > 0) throw new Exception(o.ToString().TrimEnd());
            if (action == "check")
                o.AppendLine(rep.Differences.Count == 0 ? $"The text folder matches the project ({rep.Objects} objects): {folder}"
                                                        : $"{rep.Differences.Count} difference(s) between {folder} and the project (export updates the folder, import the project).");
            else
                o.AppendLine($"{rep.Objects} objects, {rep.Written} file(s) written, {rep.Unchanged} unchanged" + (rep.Removed > 0 ? $", {rep.Removed} removed" : "") +
                             (rep.NonSt > 0 ? $", {rep.NonSt} graphical implementation(s) only as marker" : "") + $": {folder}");
            return o.ToString();
        }

        var plan = PlainText.Import(slnDir, plcs, folder);
        AppendReport(o, plan);
        if (plan.Errors.Count > 0) throw new Exception("NOT imported (nothing changed):\n" + o.ToString().TrimEnd());
        if (plan.Files.Count == 0) return o.AppendLine($"Nothing to change - the project matches {folder}.").ToString();
        bool inAttached = attached != null && string.Equals(Path.GetFullPath(attached), sln, StringComparison.OrdinalIgnoreCase);
        if (DryRun(a))
            return o.AppendLine($"PROPOSAL (nothing written): {plan.Changes.Count} object(s) changed or new" +
                                (inAttached ? " - the import writes them through the connected IDE." : " - the import writes the project files.")).ToString();

        if (!inAttached)
        {
            var others = tc.Sta.Run(() => tc.ListInstances().Where(i => !string.IsNullOrEmpty(i.Solution) &&
                                                                       SamePath(i.Solution, sln))
                                                           .Select(i => i.Pid).ToList(), 60_000);
            if (others.Count > 0)
                throw new Exception($"NOT imported: the solution is open in another IDE (pid {string.Join(", ", others)}) - connect to it with tc_attach first.");
            PlainText.WriteFiles(plan);
            return o.AppendLine($"{plan.Files.Count} file(s) written (the solution is not open in an IDE).").ToString();
        }
        return o.Append(tc.Sta.Run(() => ImportThroughIde(plan), 600_000)).ToString();
    }

    static void AppendReport(StringBuilder o, PlainText.Report rep)
    {
        foreach (var l in rep.Lines) o.AppendLine(l);
        foreach (var d in rep.Differences) o.AppendLine(d);
        foreach (var w in rep.Warnings) o.AppendLine("WARNING: " + w);
        foreach (var e in rep.Errors) o.AppendLine("ERROR: " + e);
    }

    static readonly Dictionary<string, int> PlainTextSubTypes = new() { ["METHOD"] = 609, ["PROPERTY"] = 611, ["ACTION"] = 608, ["GET"] = 613, ["SET"] = 614 };

    /// <summary>Applies the planned import in the connected IDE (STA thread): writes changed blocks like tc_plc_write,
    /// creates new sub-objects like tc_plc_create and imports new object files like tc_import kind=pou_files.</summary>
    string ImportThroughIde(PlainText.Report plan)
    {
        var o = new StringBuilder("\nThrough the IDE:\n");
        var items = tc.PlcProjects().Select(p => (p.Name, p.PathName)).ToList();
        int ok = 0, failed = 0;
        string tmp = null;
        void Step(string what, Func<string> f)
        {
            try { o.AppendLine($"  {what}: {f()}"); ok++; }
            catch (Exception ex) { o.AppendLine($"  {what}: ERROR {ErrorText(ex)}"); failed++; }
        }
        foreach (var ch in plan.Changes)
        {
            string root;
            try { root = PlcTreeRoot(ch.Plc, items); }
            catch (Exception ex) { o.AppendLine($"  {ch.Name}: ERROR {ex.Message}"); failed++; continue; }
            var folderPath = root + string.Concat(ch.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(x => "^" + x));
            var objPath = folderPath + "^" + ch.Name;
            if (ch.NewKind != null)
            {
                // the generated object file (with all its methods/properties/actions) imported like tc_import kind=pou_files
                var file = plan.Files.FirstOrDefault(x => x.Path == ch.File);
                Step($"new {PlainText.KindText(ch.NewKind)} {objPath}", () =>
                {
                    if (file.Data == null) throw new Exception("generated file not found");
                    tmp ??= Path.Combine(Path.GetTempPath(), "tcagent-st-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);
                    var path = Path.Combine(tmp, Path.GetFileName(file.Path));
                    File.WriteAllBytes(path, file.Data);
                    var parent = EnsurePlcFolder(root, ch.Folder);
                    ((dynamic)parent).CreateChild(ch.Name, 58, null, path);
                    return "imported";
                });
                continue;
            }
            foreach (var e in ch.Result.Edits)
            {
                var path = objPath + string.Concat(e.Path.Select(x => "^" + x));
                Step(path, () => PlcWrite(tc.Item(path), e.Decl, e.Impl));
            }
            bool itf = ch.Result.New.Count > 0 && IsInterface(objPath);
            foreach (var n in ch.Result.New)
            {
                var parentPath = objPath + string.Concat(n.Path.Take(n.Path.Count - 1).Select(x => "^" + x));
                Step($"new {n.Kind} {parentPath}^{n.Path[^1]}", () =>
                {
                    var sub = PlainTextSubTypes[n.Kind] + (itf && n.Kind is "METHOD" or "PROPERTY" ? 1 : 0);   // 610 / 612 in interfaces
                    var parent = tc.Item(parentPath);
                    ITcSmTreeItem item = null;
                    if (n.Kind is "GET" or "SET")           // TwinCAT may have created the accessor with the property
                        try { item = tc.Item(parentPath + "^" + n.Path[^1]); } catch { }
                    if (item != null) return "exists; " + PlcWrite(item, n.Decl, n.Impl);
                    // the same CreateChild arguments as tc_plc_create (ST; the declaration written afterwards sets the real return type)
                    object vInfo = sub switch { 609 or 610 => new object[] { 1, "BOOL" }, 611 or 612 => "BOOL", _ => 1 };
                    try { item = (ITcSmTreeItem)((dynamic)parent).CreateChild(n.Path[^1], sub, "", vInfo); }
                    catch when (sub is 613 or 614) { item = (ITcSmTreeItem)((dynamic)parent).CreateChild(n.Path[^1], sub, "", null); }
                    return "created; " + PlcWrite(item, n.Decl, n.Impl);
                });
            }
        }
        if (tmp != null) try { Directory.Delete(tmp, true); } catch { }
        o.AppendLine($"{ok} step(s) done, {failed} failed. Nothing is saved yet: call tc_save, then tc_check" +
                     (failed > 0 ? "; fix the failed steps with tc_plc_write/tc_plc_create (or close the IDE and run TcAgent.exe st import)." : "."));
        return o.ToString();
    }

    bool IsInterface(string path)
    {
        try { return tc.Item(path).ItemSubType == 618; } catch { return false; }
    }

    /// <summary>Tree path of the PLC project (nested project item) that belongs to a .plcproj.</summary>
    static string PlcTreeRoot(PlainText.PlcProj plc, List<(string Name, string PathName)> items)
    {
        var names = new[] { plc.Stem, plc.Stem + " Project", plc.Name, plc.Name + " Project" };
        var hits = items.Where(i => names.Any(n => n.Equals(i.Name, StringComparison.OrdinalIgnoreCase) ||
                                                   n.Equals(i.PathName.Split('^').Skip(1).FirstOrDefault(), StringComparison.OrdinalIgnoreCase))).ToList();
        if (hits.Count == 1) return hits[0].PathName;
        if (items.Count == 1) return items[0].PathName;
        throw new Exception($"PLC project of {plc.Path} not found in the tree ({string.Join(", ", items.Select(i => i.PathName))})");
    }

    /// <summary>The folder below the PLC project, created like tc_plc_create kind=folder where it is missing.</summary>
    ITcSmTreeItem EnsurePlcFolder(string root, string folder)
    {
        var item = tc.Item(root);
        var path = root;
        foreach (var seg in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "^" + seg;
            ITcSmTreeItem next = null;
            try { next = tc.Item(path); } catch { }
            item = next ?? (ITcSmTreeItem)((dynamic)item).CreateChild(seg, 601, "", null);
        }
        return item;
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
