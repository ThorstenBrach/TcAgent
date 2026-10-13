// More of the TwinCAT Automation Interface as tools: new solutions and PLC projects, PLCopen/XTI export and import,
// library management and "save as library", static analysis (TE1200), mapping export/import, TwinCAT version
// (Remote Manager), target platform, archive, project variants, ADS routes and TcCOM module instances.
// Functions on versioned interfaces are called late-bound (ComCall) so TcAgent builds with any TwinCAT version.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterAutomation()
    {
        // ---------------------------------------------------------- projects
        Add("tc_new_solution", "Starts Visual Studio/XAE and creates a new solution with a TwinCAT XAE project (Beckhoff project template), " +
                               "then connects to it. Add a PLC project with tc_plc_project_add.",
            Schema(Req(P("folder", "string", "Parent folder; the solution goes to <folder>\\<name>\\<name>.sln")),
                   Req(P("name", "string", "Name of the solution and the TwinCAT project")),
                   P("visible", "boolean", "Show the IDE (default true)"),
                   P("template", "string", "Path of a .tsproj template (default: TwinCAT Project.tsproj of the installation)"),
                   P("progId", "string", "e.g. VisualStudio.DTE.17.0 or TcXaeShell.DTE.17.0")),
            a => tc.Sta.Run(() => tc.NewSolution(Str(a, "folder"), Str(a, "name"), Bool(a, "visible", true), Str(a, "progId"), Str(a, "template")), 300_000));

        Add("tc_plc_project_add", "Adds a PLC project: new from a template (default 'Standard PLC Template') or an existing .plcproj/.tpzip (file). " +
                                  "The PLC instance and its task appear below TIPC; check with tc_tree path=TIPC.",
            Schema(Req(P("name", "string", "Name of the PLC project (ignored with fileMode=original)")),
                   P("template", "string", "Template name or path, e.g. 'Standard PLC Template' or 'Empty PLC Template'"),
                   P("file", "string", "Existing .plcproj or .tpzip instead of a template"),
                   P("fileMode", "string", "with file: copy (default, into the solution folder), move or original (keep location)"),
                   P("dryRun", "boolean", "Only show what would happen")),
            a => tc.Sta.Run(() => PlcProjectAdd(a), 300_000));

        // ---------------------------------------------------------- export / import
        Add("tc_export", "Exports to a file: kind=plcopen (PLC objects as PLCopen XML, selection like 'POUs.MAIN;DUTs.ST_Data') or " +
                         "kind=item (any tree item with all children as .xti - I/O device, box, task, axis ...; a PLC project path TIPC^<Plc> as .tpzip).",
            Schema(Req(P("kind", "string", "plcopen or item")), Req(P("file", "string", "Target file (.xml, .xti, .tpzip)")),
                   P("selection", "string", "plcopen: objects relative to the PLC project, folders with dots, several with ';'"),
                   P("plcProjectPath", "string", "plcopen: PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = the only one)"),
                   P("path", "string", "item: tree path of the item to export, e.g. 'TIID^Device 1 (EtherCAT)^Term 1 (EK1100)'")),
            a => tc.Sta.Run(() => Export(a), 300_000));

        Add("tc_import", "Imports from files: kind=plcopen (PLCopen XML into a PLC project), kind=item (.xti below parentPath, e.g. an I/O " +
                         "device under TIID or an axis under TINC^...^Axes; variables are re-linked by name) or kind=pou_files (.TcPOU/.TcDUT/.TcGVL files).",
            Schema(Req(P("kind", "string", "plcopen, item or pou_files")), Req(P("file", "string", "File; pou_files: several separated by ';'")),
                   P("plcProjectPath", "string", "plcopen/pou_files: PLC project (empty = the only one); pou_files may also name a folder of it"),
                   P("existing", "string", "plcopen: what to do with existing objects: none (default), rename, replace or skip"),
                   P("selection", "string", "plcopen: only these objects"),
                   P("parentPath", "string", "item: parent tree item (e.g. TIID)"),
                   P("name", "string", "item / pou_files (single file): name of the new item"),
                   P("before", "string", "item: insert before this child"),
                   P("reconnect", "boolean", "item: re-link variables by name (default true)"),
                   P("dryRun", "boolean", "Only show what would happen")),
            a => tc.Sta.Run(() => Import(a), 300_000));

        // ---------------------------------------------------------- libraries
        Add("tc_library", "Library management of a PLC project: list (references), add, add_placeholder, remove, resolve (effective version of a placeholder), " +
                          "freeze (placeholder versions), repositories, add_repository, remove_repository, install, uninstall, scan (all installed libraries).",
            Schema(Req(P("action", "string", "list, add, add_placeholder, remove, resolve, freeze, repositories, add_repository, remove_repository, install, uninstall, scan")),
                   P("plcProjectPath", "string", "PLC project (empty = the only one)"),
                   P("name", "string", "Library or placeholder name, e.g. Tc2_Standard; also 'Tc2_Math, * (Beckhoff Automation GmbH)'"),
                   P("version", "string", "Version, '*' = newest (default)"),
                   P("company", "string", "Distributor, e.g. Beckhoff Automation GmbH"),
                   P("library", "string", "resolve / add_placeholder: library name the placeholder points to"),
                   P("repository", "string", "Repository name (install/uninstall/add_repository/remove_repository)"),
                   P("path", "string", "install: .library/.compiled-library file; add_repository: root folder"),
                   P("index", "integer", "add_repository: position (default 0)"),
                   P("overwrite", "boolean", "install: overwrite an installed version (default false)"),
                   P("filter", "string", "scan: only libraries containing this text")),
            a => tc.Sta.Run(() => Library(a), 300_000));

        Add("tc_library_save", "Saves a PLC project as library (.library) and optionally installs it into the default repository - e.g. as a pipeline artifact.",
            Schema(Req(P("file", "string", "Target .library file")),
                   P("plcProjectPath", "string", "PLC project (empty = the only one)"),
                   P("install", "boolean", "Also install into the repository (default false)")),
            a => tc.Sta.Run(() =>
            {
                var plc = PlcProject(Str(a, "plcProjectPath"));
                var file = Path.GetFullPath(Str(a, "file"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                ComCall.Invoke(plc, "SaveAsLibrary", file, Bool(a, "install", false));
                return $"saved: {file}" + (Bool(a, "install", false) ? " (and installed)" : "") + (File.Exists(file) ? $", {new FileInfo(file).Length / 1024} KB" : "");
            }, 600_000));

        // ---------------------------------------------------------- static analysis
        Add("tc_static_analysis", "Static code analysis (TE1200 license): coding rules, naming conventions, metrics. Results appear in the error list (tc_errors). " +
                                  "Optional: load settings (.csa) first, export results as SARIF (TwinCAT 4026.20+).",
            Schema(P("plcProjectPath", "string", "PLC project (empty = all)"),
                   P("settingsFile", "string", "Load static analysis settings (.csa) before the run (4026+)"),
                   P("sarifFile", "string", "Export the results as SARIF file (4026.20+)"),
                   P("metricsFile", "string", "Export the standard metrics as CSV (4026.4+)")),
            a => tc.Sta.Run(() => StaticAnalysis(Str(a, "plcProjectPath"), Str(a, "settingsFile"), Str(a, "sarifFile"), Str(a, "metricsFile")), 900_000));

        // ---------------------------------------------------------- mapping
        Add("tc_mapping_export", "Exports all variable links (mapping) of the configuration as XML - to a file or as text.",
            Schema(P("file", "string", "Target file (empty = return the XML, shortened)")),
            a => tc.Sta.Run(() =>
            {
                var xml = (string)ComCall.Invoke(tc.RequireSysMan(), "ProduceMappingInfo");
                var links = Count(xml, "<Link");
                if (string.IsNullOrEmpty(Str(a, "file"))) return $"{links} links\n" + (xml.Length > 20000 ? xml[..20000] + "\n... (shortened, use file=)" : xml);
                var file = Path.GetFullPath(Str(a, "file"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, xml);
                return $"{links} links exported to {file}";
            }));

        Add("tc_mapping_import", "Imports variable links from an XML file made with tc_mapping_export (optionally removes all existing links first). " +
                                 "clearOnly=true only removes all links.",
            Schema(P("file", "string", "Mapping XML file"), P("clearFirst", "boolean", "Remove all existing links first (default false)"),
                   P("clearOnly", "boolean", "Only remove all links"), P("dryRun", "boolean", "Only show what would happen")),
            a => tc.Sta.Run(() => MappingImport(a)));

        // ---------------------------------------------------------- version, platform, archive, variants
        Add("tc_twincat_version", "Shows or selects the TwinCAT version (Remote Manager) the solution is built with - for several installed TwinCAT builds.",
            Schema(P("version", "string", "Version to select, e.g. 3.1.4026.19 (empty = show)")),
            a => tc.Sta.Run(() => TwinCatVersion(Str(a, "version"))));

        Add("tc_platform", "Shows or sets the target platform of the solution, e.g. 'TwinCAT RT (x64)' or 'TwinCAT RT (x86)'.",
            Schema(P("platform", "string", "Platform to set (empty = show the current one and the available ones)")),
            a => tc.Sta.Run(() => Platform(Str(a, "platform"))));

        Add("tc_archive", "Saves the whole solution as archive (.tszip) - e.g. as pipeline artifact or for support.",
            Schema(Req(P("file", "string", "Target .tszip file"))),
            a => tc.Sta.Run(() =>
            {
                var file = Path.GetFullPath(Str(a, "file"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                ComCall.Invoke(tc.RequireSysMan(), "SaveAsArchive", file);
                return $"archived: {file}" + (File.Exists(file) ? $" ({new FileInfo(file).Length / 1024} KB)" : "");
            }, 600_000));

        Add("tc_variant", "Project variants: shows the current variant and the variant configuration, selects a variant (current, '[Group]' for a group) " +
                          "or replaces the variant configuration XML (config).",
            Schema(P("current", "string", "Variant to select, e.g. Variant1 or [Group1]"),
                   P("config", "string", "New variant configuration XML (<ProjectVariants>...)")),
            a => tc.Sta.Run(() => Variant(Str(a, "current"), Str(a, "config"))));

        // ---------------------------------------------------------- routes
        Add("tc_route_search", "Searches ADS target systems: broadcast search in the local network, or a direct search by host name/IP (4020.10+). Read only.",
            Schema(P("host", "string", "Host name or IP for a direct search (empty = broadcast)")),
            a => tc.Sta.Run(() => RouteSearch(Str(a, "host")), 120_000));

        Add("tc_route_add", "Adds an ADS route to a target system (on the target and locally), optionally also as project route. Needs the target's " +
                            "user name and password - ask the user for them, never guess. Runtime action with confirmation.",
            Schema(Req(P("name", "string", "Route name, e.g. CX-12345")), Req(P("netId", "string", "AmsNetId of the target")),
                   P("ip", "string", "IP address of the target"), P("host", "string", "Host name instead of the IP"),
                   P("user", "string", "User name on the target (e.g. Administrator)"), P("password", "string", "Password on the target"),
                   P("projectRoute", "boolean", "Also add as project route (default false)"),
                   P("noEncryption", "boolean", "Route without encryption (older targets)")),
            a => tc.Sta.Run(() => RouteAdd(a), 120_000));

        // ---------------------------------------------------------- TcCOM
        Add("tc_tccom", "TcCOM module instances: list, or add an instance of a module class (class id GUID or module name) and optionally " +
                        "let a task call it (taskPath). Parameters afterwards with tc_xml_get/tc_xml_set.",
            Schema(Req(P("action", "string", "list or add")),
                   P("classId", "string", "add: class id (GUID) of the module"),
                   P("module", "string", "add: module name instead of the class id"),
                   P("name", "string", "add: instance name"),
                   P("taskPath", "string", "add: task that calls the module, e.g. TIRT^Task 1"),
                   P("dryRun", "boolean", "Only show what would happen")),
            a => tc.Sta.Run(() => TcCom(a)));
    }

    // ================================================================== helpers
    ITcSmTreeItem PlcProject(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            var it = tc.Item(path);
            if (it.ItemType == 600) return it;
            var inner = TwinCatSession.Children(it).FirstOrDefault(c => c.ItemType == 600);
            return inner ?? throw new Exception($"{path} is not a PLC project (expected e.g. TIPC^MyPlc^MyPlc Project).");
        }
        var all = tc.PlcProjects();
        return all.Count == 1 ? all[0]
             : throw new Exception(all.Count == 0 ? "No PLC project in the solution." : "Several PLC projects - pass plcProjectPath: " + string.Join(", ", all.Select(p => p.PathName)));
    }

    /// <summary>Elements of a COM collection: IEnumerable, otherwise Count + Item(i) on any interface (0- or 1-based).</summary>
    static IEnumerable<object> ComItems(object coll)
    {
        if (coll is System.Collections.IEnumerable en)
        {
            var list = new List<object>();
            try { foreach (var x in en) list.Add(x); return list; } catch (InvalidCastException) { }
        }
        // interface with GetEnumerator (e.g. ITcModuleManager3), then late-bound foreach
        try
        {
            if (ComCall.Invoke(coll, "GetEnumerator") is System.Collections.IEnumerator e1)
            {
                var list = new List<object>();
                while (e1.MoveNext()) list.Add(e1.Current);
                return list;
            }
        }
        catch (NotSupportedException) { }
        try
        {
            var list = new List<object>();
            foreach (var x in (dynamic)coll) list.Add((object)x);
            return list;
        }
        catch (Exception) { }
        int n = Convert.ToInt32(ComCall.Get(coll, "Count"), CultureInfo.InvariantCulture);
        var items = new List<object>();
        int start = 0;
        try { ComCall.Invoke(coll, "Item", 0); } catch { start = 1; }
        for (int i = start; i < n + start; i++)
        {
            object it = null;
            try { it = ComCall.Invoke(coll, "Item", i); } catch { try { it = ComCall.Invoke(coll, "get_Item", i); } catch { } }
            if (it != null) items.Add(it);
        }
        return items;
    }

    static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    static string Xml(string s) => System.Security.SecurityElement.Escape(s ?? "");

    // ================================================================== PLC project
    string PlcProjectAdd(JsonObject a)
    {
        var tipc = tc.Item("TIPC");
        var name = Str(a, "name");
        var file = Str(a, "file");
        int sub; object info; string what;
        if (!string.IsNullOrEmpty(file))
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("not found", file);
            var mode = (Str(a, "fileMode") ?? "copy").ToLowerInvariant();
            sub = mode switch { "copy" => 0, "move" => 1, "original" => 2, _ => throw new Exception("fileMode: copy, move or original") };
            if (sub == 2) name = "";
            info = file; what = $"existing project {file} ({mode})";
        }
        else
        {
            var template = Str(a, "template") ?? "Standard PLC Template";
            if (!template.Contains('\\') && !template.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase))
                template = TwinCatSession.FindTemplate($@"Plc\PlcTemplate\Plc Templates\{template}.plcproj") ?? template;   // full path is language independent
            sub = 0; info = template; what = $"new from template {template}";
        }
        if (DryRun(a)) return $"PROPOSAL (nothing created): PLC project '{name}' - {what}";
        var created = (ITcSmTreeItem)((dynamic)tipc).CreateChild(name, sub, "", info);
        tc.InvalidatePlcIndex();
        var o = new StringBuilder($"created: {created.PathName} - {what}");
        foreach (var c in TwinCatSession.Children(created)) o.Append("\n  ").Append(c.PathName);
        o.Append("\nNext: tc_plc_create for POUs, tc_build; task assignment of the PLC instance with tc_tree/tc_xml_get on the instance.");
        return o.ToString();
    }

    // ================================================================== export / import
    string Export(JsonObject a)
    {
        var file = Path.GetFullPath(Str(a, "file"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        switch ((Str(a, "kind") ?? "").ToLowerInvariant())
        {
            case "plcopen":
            {
                var sel = Str(a, "selection") ?? throw new Exception("selection is required, e.g. 'POUs.MAIN;DUTs.ST_Data'");
                var plc = PlcProject(Str(a, "plcProjectPath"));
                ComCall.Invoke(plc, "PlcOpenExport", file, sel);
                return $"PLCopen XML exported: {file} ({sel})";
            }
            case "item":
            {
                var path = Str(a, "path") ?? throw new Exception("path is required");
                var cut = path.LastIndexOf('^');
                if (cut < 0) throw new Exception("path must be a child item, e.g. TIID^Device 1 (EtherCAT)");
                var parent = tc.Item(path[..cut]);
                var child = path[(cut + 1)..];
                ComCall.Invoke(parent, "ExportChild", child, file);
                return $"exported: {path} -> {file}" + (File.Exists(file) ? $" ({new FileInfo(file).Length / 1024} KB)" : "");
            }
            default: throw new Exception("kind: plcopen or item");
        }
    }

    /// <summary>Files under %LOCALAPPDATA% may be redirected for this process (packaged clients like Claude Desktop) and then
    /// do not exist for the IDE: such files are copied to the temp folder (same file name) before the IDE opens them.</summary>
    static string IdeVisible(string file)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!file.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return file;
        var dir = Path.Combine(Path.GetTempPath(), "tcagent-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, Path.GetFileName(file));
        File.Copy(file, copy, true);
        return copy;
    }

    string Import(JsonObject a)
    {
        var kind = (Str(a, "kind") ?? "").ToLowerInvariant();
        var files = (Str(a, "file") ?? "").Split(';').Select(f => f.Trim()).Where(f => f.Length > 0).Select(Path.GetFullPath).ToList();
        if (files.Count == 0) throw new Exception("file is required");
        foreach (var f in files) if (!File.Exists(f)) throw new FileNotFoundException("not found", f);
        files = files.Select(IdeVisible).ToList();
        switch (kind)
        {
            case "plcopen":
            {
                var plc = PlcProject(Str(a, "plcProjectPath"));
                int opt = (Str(a, "existing") ?? "none").ToLowerInvariant() switch
                {
                    "none" => 0, "rename" => 1, "replace" => 2, "skip" => 3, _ => throw new Exception("existing: none, rename, replace or skip")
                };
                if (DryRun(a)) return $"PROPOSAL (nothing imported): {files[0]} into {plc.PathName} (existing={Str(a, "existing") ?? "none"})";
                var sel = Str(a, "selection");
                if (string.IsNullOrEmpty(sel)) ComCall.Invoke(plc, "PlcOpenImport", files[0], opt);
                else ComCall.Invoke(plc, "PlcOpenImport", files[0], opt, sel, true);
                tc.InvalidatePlcIndex();
                return $"PLCopen XML imported into {plc.PathName}: {files[0]}. Check with tc_check.";
            }
            case "item":
            {
                var parent = tc.Item(Str(a, "parentPath") ?? throw new Exception("parentPath is required, e.g. TIID"));
                if (DryRun(a)) return $"PROPOSAL (nothing imported): {files[0]} below {parent.PathName}";
                var item = (ITcSmTreeItem)ComCall.Invoke(parent, "ImportChild", files[0], Str(a, "before") ?? "", Bool(a, "reconnect", true), Str(a, "name") ?? "");
                return $"imported: {item?.PathName ?? files[0]}";
            }
            case "pou_files":
            {
                ITcSmTreeItem target = !string.IsNullOrEmpty(Str(a, "plcProjectPath")) ? tc.Item(Str(a, "plcProjectPath")) : PlcProject(null);
                if (DryRun(a)) return $"PROPOSAL (nothing imported): {files.Count} file(s) into {target.PathName}";
                if (files.Count == 1) ((dynamic)target).CreateChild(Str(a, "name") ?? Path.GetFileNameWithoutExtension(files[0]), 58, null, files[0]);
                else ((dynamic)target).CreateChild(null, 58, null, files.ToArray());
                tc.InvalidatePlcIndex();
                return $"{files.Count} file(s) imported into {target.PathName}. Check with tc_check.";
            }
            default: throw new Exception("kind: plcopen, item or pou_files");
        }
    }

    // ================================================================== libraries
    string Library(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        var plc = PlcProject(Str(a, "plcProjectPath"));
        var refs = TwinCatSession.Children(plc).FirstOrDefault(c => c is ITcPlcLibraryManager)   // References (like PlcReferences)
                   ?? tc.Item(plc.PathName + "^References");
        string name = Str(a, "name"), version = Str(a, "version") ?? "*", company = Str(a, "company") ?? "";
        string Need(string v, string what) => !string.IsNullOrEmpty(v) ? v : throw new Exception(what + " is required");
        switch (action)
        {
            case "list":
                return PlcReferences(plc.PathName, false);
            case "add":
                ComCall.Invoke(refs, "AddLibrary", Need(name, "name"), version, company);
                return $"library added: {name} {version} {company}".TrimEnd();
            case "add_placeholder":
                if (string.IsNullOrEmpty(Str(a, "library"))) ComCall.Invoke(refs, "AddPlaceholder", Need(name, "name"));
                else ComCall.Invoke(refs, "AddPlaceholder", Need(name, "name"), Str(a, "library"), version, company);
                return $"placeholder added: {name}";
            case "remove":
                ComCall.Invoke(refs, "RemoveReference", Need(name, "name"), Str(a, "version") ?? "", company);
                return $"reference removed: {name}";
            case "resolve":
                ComCall.Invoke(refs, "SetEffectiveResolution", Need(name, "name (placeholder)"), Need(Str(a, "library"), "library"), version, company);
                return $"placeholder {name} -> {Str(a, "library")} {version}";
            case "freeze":
                if (string.IsNullOrEmpty(name)) ((dynamic)refs).FreezePlaceholder(); else ((dynamic)refs).FreezePlaceholder(name);
                return string.IsNullOrEmpty(name) ? "all placeholder versions frozen" : $"placeholder {name} frozen";
            case "repositories":
            {
                var o = new StringBuilder("Library repositories:\n");
                foreach (var r in (System.Collections.IEnumerable)ComCall.Get(refs, "Repositories"))
                    o.AppendLine($"  {Prop(r, "Name")}  {Prop(r, "Folder")}");
                return o.ToString();
            }
            case "add_repository":
                ComCall.Invoke(refs, "InsertRepository", Need(Str(a, "repository"), "repository"), Path.GetFullPath(Need(Str(a, "path"), "path")), Int(a, "index", 0));
                return $"repository added: {Str(a, "repository")}";
            case "remove_repository":
                ((dynamic)refs).RemoveRepository(Need(Str(a, "repository"), "repository"));
                return $"repository removed: {Str(a, "repository")}";
            case "install":
                ComCall.Invoke(refs, "InstallLibrary", Str(a, "repository") ?? "System", Path.GetFullPath(Need(Str(a, "path"), "path")), Bool(a, "overwrite", false));
                return $"installed into {Str(a, "repository") ?? "System"}: {Str(a, "path")}";
            case "uninstall":
                ComCall.Invoke(refs, "UninstallLibrary", Str(a, "repository") ?? "System", Need(name, "name"), version, company);
                return $"uninstalled: {name} {version}";
            case "scan":
            {
                var filter = Str(a, "filter") ?? "";
                var list = new List<string>();
                foreach (var r in (System.Collections.IEnumerable)ComCall.Invoke(refs, "ScanLibraries"))
                {
                    var d = DescribeLib(r);
                    if (d.Contains(filter, StringComparison.OrdinalIgnoreCase)) list.Add(d);
                }
                return $"{list.Count} installed libraries{(filter.Length > 0 ? $" containing '{filter}'" : "")}:\n  " + string.Join("\n  ", list.Take(400)) +
                       (list.Count > 400 ? "\n  ..." : "");
            }
            default: throw new Exception("unknown action: " + action);
        }
    }

    static string Prop(object o, string p)
    {
        try { return Convert.ToString(ComCall.Get(o, p), CultureInfo.InvariantCulture); } catch { }
        try { return Convert.ToString(o.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, o, null), CultureInfo.InvariantCulture); } catch { }
        return null;
    }

    // ================================================================== static analysis
    string StaticAnalysis(string plcPath, string settings, string sarif, string metrics)
    {
        var plcs = string.IsNullOrEmpty(plcPath) ? tc.PlcProjects() : new List<ITcSmTreeItem> { PlcProject(plcPath) };
        var o = new StringBuilder();
        foreach (var plc in plcs)
        {
            if (!string.IsNullOrEmpty(settings)) { ComCall.Invoke(plc, "LoadStaticAnalysisSettings", Path.GetFullPath(settings)); o.AppendLine($"{plc.Name}: settings loaded from {settings}"); }
            if (!string.IsNullOrEmpty(sarif))
            {
                var f = Path.GetFullPath(plcs.Count > 1 ? Path.Combine(Path.GetDirectoryName(sarif) ?? "", $"{Path.GetFileNameWithoutExtension(sarif)}.{plc.Name}{Path.GetExtension(sarif)}") : sarif);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                ComCall.Invoke(plc, "ExportToSarif", f);   // runs the analysis itself
                o.AppendLine($"{plc.Name}: static analysis run, SARIF: {f}");
            }
            else
            {
                ComCall.Invoke(plc, "RunStaticAnalysis", false);
                o.AppendLine($"{plc.Name}: static analysis run");
            }
            if (!string.IsNullOrEmpty(metrics)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metrics))!); ComCall.Invoke(plc, "ExportStandardMetrics", Path.GetFullPath(metrics)); o.AppendLine($"{plc.Name}: metrics: {metrics}"); }
        }
        var (compile, _) = tc.ReadErrorList(2000);
        int errs = compile.Count(e => e.Level == "ERROR"), warns = compile.Count(e => e.Level == "WARN");
        o.AppendLine($"Error list: {errs} errors, {warns} warnings");
        foreach (var e in compile.Where(e => e.Level is "ERROR" or "WARN").Take(300)) o.AppendLine(e.ToString());
        return o.ToString();
    }

    // ================================================================== mapping
    string MappingImport(JsonObject a)
    {
        var sm = tc.RequireSysMan();
        if (Bool(a, "clearOnly", false))
        {
            if (DryRun(a)) return "PROPOSAL: remove all variable links";
            ComCall.Invoke(sm, "ClearMappingInfo");
            return "all variable links removed";
        }
        var file = Path.GetFullPath(Str(a, "file") ?? throw new Exception("file is required"));
        var xml = File.ReadAllText(file);
        if (DryRun(a)) return $"PROPOSAL: import {Count(xml, "<Link")} links from {file}" + (Bool(a, "clearFirst", false) ? " after removing all existing links" : "");
        if (Bool(a, "clearFirst", false)) ComCall.Invoke(sm, "ClearMappingInfo");
        ComCall.Invoke(sm, "ConsumeMappingInfo", xml);
        return $"{Count(xml, "<Link")} links imported from {file}";
    }

    // ================================================================== version, platform, variants
    string TwinCatVersion(string version)
    {
        object rm;
        try { rm = ((dynamic)tc.RequireDte()).GetObject("TcRemoteManager"); }
        catch (Exception ex) { throw new Exception("Remote Manager not available: " + (ex.InnerException?.Message ?? ex.Message)); }
        dynamic d = rm;
        string current;
        try { current = (string)d.Version; } catch (Exception ex) { current = "unknown (" + ex.Message + ")"; }
        string versions = null;
        try { versions = string.Join(", ", ((System.Collections.IEnumerable)d.Versions).Cast<object>()); } catch { }
        if (string.IsNullOrEmpty(version))
            return $"TwinCAT version (Remote Manager): {current}" + (versions != null ? $"\nInstalled: {versions}" : "");
        d.Version = version;
        return $"TwinCAT version: {current} -> {(string)d.Version}. Rebuild afterwards (tc_build rebuild=true).";
    }

    string Platform(string platform)
    {
        var cm = ComCall.Get(tc.RequireSysMan(), "ConfigurationManager");
        var current = (string)ComCall.Get(cm, "ActiveTargetPlatform");
        string available = null;
        try { available = string.Join(", ", ((Array)ComCall.Get(cm, "TargetPlatforms")).Cast<object>()); } catch { }
        if (string.IsNullOrEmpty(platform)) return $"Target platform: {current}" + (available != null ? $"\nAvailable: {available}" : "");
        ComCall.Set(cm, "ActiveTargetPlatform", platform);
        return $"Target platform: {current} -> {(string)ComCall.Get(cm, "ActiveTargetPlatform")}";
    }

    string Variant(string current, string config)
    {
        var sm = tc.RequireSysMan();
        var o = new StringBuilder();
        if (!string.IsNullOrEmpty(config)) { ComCall.Set(sm, "ProjectVariantConfig", config); o.AppendLine("variant configuration replaced"); }
        if (!string.IsNullOrEmpty(current)) { ComCall.Set(sm, "CurrentProjectVariant", current); o.AppendLine($"current variant: {current}"); }
        if (o.Length == 0)
        {
            o.AppendLine($"Current variant: {Convert.ToString(ComCall.Get(sm, "CurrentProjectVariant"))}");
            o.AppendLine("Variant configuration:");
            o.AppendLine(Convert.ToString(ComCall.Get(sm, "ProjectVariantConfig")));
        }
        return o.ToString();
    }

    // ================================================================== routes
    string RouteSearch(string host)
    {
        var routes = tc.Item("TIRR");
        var search = string.IsNullOrEmpty(host) ? "<BroadcastSearch>true</BroadcastSearch>" : $"<Search>{Xml(host)}</Search>";
        ((dynamic)routes).ConsumeXml($"<TreeItem><RoutePrj><TargetList>{search}</TargetList></RoutePrj></TreeItem>");
        var xml = (string)((dynamic)routes).ProduceXml(false);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var targets = doc.SelectNodes("//TreeItem/RoutePrj/TargetList/Target");
        if (targets == null || targets.Count == 0) return "No target system found." + (string.IsNullOrEmpty(host) ? " (Broadcast search - firewall? different subnet? try host=)" : "");
        var o = new StringBuilder($"{targets.Count} target system(s):\n");
        foreach (XmlNode t in targets)
            o.AppendLine($"  {t.SelectSingleNode("Name")?.InnerText}  NetId={t.SelectSingleNode("NetId")?.InnerText}  IP={t.SelectSingleNode("IpAddr")?.InnerText}" +
                         $"  TwinCAT={t.SelectSingleNode("Version")?.InnerText}  OS={t.SelectSingleNode("OS")?.InnerText}");
        o.Append("Add a route with tc_route_add (needs the target's user/password - ask the user).");
        return o.ToString();
    }

    string RouteAdd(JsonObject a)
    {
        string ip = Str(a, "ip"), host = Str(a, "host");
        if (string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(host)) throw new Exception("ip or host is required");
        var addr = !string.IsNullOrEmpty(ip) ? $"<RemoteIpAddr>{Xml(ip)}</RemoteIpAddr>" : $"<RemoteHostName>{Xml(host)}</RemoteHostName>";
        var sb = new StringBuilder("<TreeItem><ItemName>Route Settings</ItemName><PathName>TIRR</PathName><RoutePrj>");
        sb.Append("<AddRoute>")
          .Append($"<RemoteName>{Xml(Str(a, "name"))}</RemoteName><RemoteNetId>{Xml(Str(a, "netId"))}</RemoteNetId>").Append(addr);
        if (!string.IsNullOrEmpty(Str(a, "user"))) sb.Append($"<UserName>{Xml(Str(a, "user"))}</UserName>");
        if (Str(a, "password") != null) sb.Append($"<Password>{Xml(Str(a, "password"))}</Password>");
        if (Bool(a, "noEncryption", false)) sb.Append("<NoEncryption></NoEncryption>");
        sb.Append("</AddRoute>");
        if (Bool(a, "projectRoute", false))
            sb.Append($"<AddProjectRoute><Name>{Xml(Str(a, "name"))}</Name><NetId>{Xml(Str(a, "netId"))}</NetId>")
              .Append(!string.IsNullOrEmpty(ip) ? $"<IpAddr>{Xml(ip)}</IpAddr>" : $"<HostName>{Xml(host)}</HostName>")
              .Append("</AddProjectRoute>");
        sb.Append("</RoutePrj></TreeItem>");
        ((dynamic)tc.Item("TIRR")).ConsumeXml(sb.ToString());
        return $"route added: {Str(a, "name")} ({Str(a, "netId")}, {ip ?? host})" + (Bool(a, "projectRoute", false) ? " + project route" : "") +
               ". Check with tc_plc_state netId=" + Str(a, "netId") + " port=10000. To act on the target it must also be in TCAGENT_ALLOW_TARGETS.";
    }

    // ================================================================== TcCOM
    string TcCom(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        if (action == "list")
        {
            var objects = tc.Item("TIRC^TcCOM Objects");
            var o = new StringBuilder($"TcCOM objects ({objects.PathName}):\n");
            foreach (var c in TwinCatSession.Children(objects)) o.AppendLine($"  {c.Name}  [type={c.ItemType} sub={c.ItemSubType}]  {c.PathName}");
            try
            {
                var mm = ComCall.Invoke(tc.RequireSysMan(), "GetModuleManager");
                o.AppendLine("Module instances (all):");
                foreach (var mi in ComItems(mm))
                    o.AppendLine($"  {Prop(mi, "ModuleInstanceName")}  type={Prop(mi, "ModuleTypeName")}  class={Prop(mi, "ClassID")}  oid={Prop(mi, "oid")}");
            }
            catch (Exception ex) { o.AppendLine("(module manager not available: " + (ex.InnerException?.Message ?? ex.Message) + ")"); }
            return o.ToString();
        }
        if (action != "add") throw new Exception("action: list or add");
        var parent = tc.Item("TIRC^TcCOM Objects");
        var name = Str(a, "name") ?? throw new Exception("name is required");
        object info; int sub;
        if (Guid.TryParse(Str(a, "classId") ?? "", out var g)) { info = g; sub = 0; }
        else if (!string.IsNullOrEmpty(Str(a, "module"))) { info = Str(a, "module"); sub = 1; }
        else throw new Exception("classId (GUID) or module is required");
        if (DryRun(a)) return $"PROPOSAL (nothing created): TcCOM instance '{name}' of {info}" + (Str(a, "taskPath") != null ? $", called by {Str(a, "taskPath")}" : "");
        var inst = (ITcSmTreeItem)((dynamic)parent).CreateChild(name, sub, "", info);
        var res = $"created: {inst.PathName}";
        var taskPath = Str(a, "taskPath");
        if (!string.IsNullOrEmpty(taskPath))
        {
            var doc = new XmlDocument();
            doc.LoadXml((string)((dynamic)tc.Item(taskPath)).ProduceXml(false));
            var oidText = doc.SelectSingleNode("TreeItem/ObjectId")?.InnerText ?? throw new Exception("ObjectId of the task not found");
            var oid = uint.Parse(oidText.Replace("#x", "").Replace("0x", ""), NumberStyles.HexNumber);
            ComCall.Invoke(inst, "SetModuleContext", 0, oid);
            res += $", called by {taskPath} (context 0)";
        }
        return res + ". Parameters: tc_xml_get path=" + inst.PathName;
    }
}
