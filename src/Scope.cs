// TwinCAT Scope (TE130x Scope View) through Visual Studio: a Scope configuration is a "TwinCAT Measurement" project
// in the solution. Its elements are DTE project items whose Object implements IMeasurementScope
// (CreateChild, ChangeName, StartRecord, StopRecord, SaveSVD, ExportCSV, ExportBinary, ExportTDMS, ExportDAT, Disconnect);
// settings are the item's DTE Properties (as in the property grid). Everything is called late-bound, so TcAgent needs
// no reference to the Scope assemblies.
//
// Paths address items by name: "<measurement project>/<scope>/<chart>/<axis>/<channel>", e.g. "Measurement/Scope Project/Chart/Axis/Channel".
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using EnvDTE;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterScope()
    {
        Add("tc_scope_new", "Adds a TwinCAT Scope configuration (Measurement project, TE130x Scope View) to the solution. Then add charts and " +
                            "channels with tc_scope.",
            Schema(Req(P("name", "string", "Name of the measurement project")),
                   P("template", "string", "Path of a .tcmproj template (default: 'TwinCAT Scope Project' of the Scope installation)"),
                   P("folder", "string", "Folder (default: next to the solution)")),
            a => tc.Sta.Run(() => ScopeNew(a), 300_000));

        Add("tc_scope", "TwinCAT Scope: list (measurement projects and their elements), properties / set (settings of an element, e.g. record time), " +
                        "add_chart, add_axis, add_channel (channel for a PLC variable incl. acquisition), record_start, record_stop, " +
                        "export (file by extension: .csv, .svdx/.svd, .bin, .tdms, .dat - TDMS/DAT need a Scope license), disconnect. " +
                        "Paths: '<project>/<scope>/<chart>/<axis>/<channel>' by name.",
            Schema(Req(P("action", "string", "list, properties, set, add_chart, add_axis, add_channel, record_start, record_stop, export, disconnect")),
                   P("path", "string", "Element path, e.g. 'Measurement/Scope Project/Chart 1' (empty = the only scope)"),
                   P("name", "string", "add_*: name of the new element"),
                   P("property", "string", "set: property name (as shown by properties; a unique part of it is enough)"),
                   P("value", "string", "set: new value"),
                   P("symbol", "string", "add_channel: PLC variable, e.g. MAIN.fSine"),
                   P("netId", "string", "add_channel: AmsNetId (default: local)"),
                   P("port", "integer", "add_channel: ADS port (default 851)"),
                   P("sampleTimeMs", "number", "add_channel: sample time in ms (default 1)"),
                   P("file", "string", "export: target file"),
                   P("chartType", "integer", "add_chart: 0 = YT (default), 1 = XY, 2 = array, 3 = bar")),
            a => tc.Sta.Run(() => Scope(a), 300_000));
    }

    // ------------------------------------------------------------------ helpers
    static object ScopeObj(ProjectItem pi) { try { return pi.Object; } catch { return null; } }

    static bool IsScopeItem(object o)
    {
        try { return o != null && HasDispMember(o, "StartRecord"); } catch { return false; }
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00020400-0000-0000-C000-000000000046"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatchLite
    {
        int GetTypeInfoCount(out uint n);
        int GetTypeInfo(uint i, uint lcid, out IntPtr ti);
        [System.Runtime.InteropServices.PreserveSig]
        int GetIDsOfNames(ref Guid riid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray, ArraySubType = System.Runtime.InteropServices.UnmanagedType.LPWStr)] string[] names,
                          uint count, uint lcid, [System.Runtime.InteropServices.Out] int[] ids);
    }

    static bool HasDispMember(object o, string name)
    {
        if (o is not IDispatchLite d) return false;
        var iid = Guid.Empty; var ids = new int[1];
        return d.GetIDsOfNames(ref iid, new[] { name }, 1, 0, ids) == 0;
    }

    List<Project> MeasurementProjects()
    {
        var list = new List<Project>();
        foreach (Project p in tc.RequireDte().Solution.Projects)
        {
            string file = "";
            try { file = p.FullName ?? ""; } catch { }
            if (file.EndsWith(".tcmproj", StringComparison.OrdinalIgnoreCase)) list.Add(p);
        }
        return list;
    }

    /// <summary>Resolves "project/item/item..." to a project item; empty = the first scope of the only measurement project.</summary>
    ProjectItem ScopeItem(string path)
    {
        var projects = MeasurementProjects();
        if (projects.Count == 0) throw new Exception("No TwinCAT Measurement (Scope) project in the solution - create one with tc_scope_new.");
        var parts = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Project proj;
        int i = 0;
        if (parts.Length > 0 && projects.FirstOrDefault(p => p.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) is { } named) { proj = named; i = 1; }
        else if (projects.Count == 1) proj = projects[0];
        else throw new Exception("Several measurement projects - start the path with one of: " + string.Join(", ", projects.Select(p => p.Name)));
        ProjectItems items = proj.ProjectItems;
        ProjectItem cur = null;
        if (i >= parts.Length)
        {
            cur = items.Cast<ProjectItem>().FirstOrDefault(x => IsScopeItem(ScopeObj(x))) ?? items.Cast<ProjectItem>().FirstOrDefault()
                  ?? throw new Exception($"{proj.Name} contains no scope.");
            return cur;
        }
        for (; i < parts.Length; i++)
        {
            cur = items.Cast<ProjectItem>().FirstOrDefault(x => x.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
                  ?? throw new Exception($"'{parts[i]}' not found. Available: " + string.Join(", ", items.Cast<ProjectItem>().Select(x => x.Name)));
            items = cur.ProjectItems;
        }
        return cur;
    }

    static string ItemPath(ProjectItem pi)
    {
        var names = new List<string>();
        object o = pi;
        for (int guard = 0; guard < 12 && o != null; guard++)
        {
            if (o is ProjectItem p) { names.Add(p.Name); o = p.Collection?.Parent; }
            else if (o is Project pr) { names.Add(pr.Name); break; }
            else break;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    static string PropValue(Property p) { try { return Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture); } catch { return "?"; } }

    static List<Property> Props(ProjectItem pi)
    {
        var l = new List<Property>();
        try { foreach (Property p in pi.Properties) l.Add(p); } catch { }
        return l;
    }

    /// <summary>Sets a property by exact name or a unique part of it. Returns a report line.</summary>
    static string SetProp(ProjectItem pi, string key, object value, bool required = true)
    {
        var props = Props(pi);
        var hit = props.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (hit == null)
        {
            var cands = props.Where(p => p.Name.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cands.Count == 1) hit = cands[0];
            else if (!required) return null;
            else throw new Exception(cands.Count == 0 ? $"No property '{key}' on {pi.Name}. Properties: {string.Join(", ", props.Select(p => p.Name))}"
                                                      : $"'{key}' is ambiguous on {pi.Name}: {string.Join(", ", cands.Select(p => p.Name))}");
        }
        var before = PropValue(hit);
        object v = value;
        try
        {
            var cur = hit.Value;
            if (cur != null && value is string s && cur is not string)
                v = cur is bool ? (object)(s.Trim().ToUpperInvariant() is "TRUE" or "1")
                  : cur.GetType().IsEnum ? Enum.Parse(cur.GetType(), s, true)
                  : Convert.ChangeType(s, cur.GetType(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { }
        hit.Value = v;
        return $"{hit.Name}: {before} -> {PropValue(hit)}";
    }

    static object ScopeCreateChild(object scopeObj, string name, int elementType)
    {
        dynamic d = scopeObj;
        object child;
        d.CreateChild(out child, name ?? "", elementType);
        return child;
    }

    // ------------------------------------------------------------------ tools
    string ScopeNew(JsonObject a)
    {
        var dte = tc.RequireDte();
        var name = Str(a, "name");
        var template = Str(a, "template");
        if (string.IsNullOrEmpty(template))
        {
            foreach (var root in new[] { @"C:\TwinCAT\Functions\TE130X-Scope-View\Templates\Projects",
                                         @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE130X-Scope-View\Templates\Projects",
                                         @"C:\Program Files\Beckhoff\TwinCAT\Functions\TE130X-Scope-View\Templates\Projects" })
            {
                if (!Directory.Exists(root)) continue;
                var files = Directory.GetFiles(root, "*.tcmproj", SearchOption.AllDirectories);
                template = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("Scope", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
                if (template != null) break;
            }
            if (template == null) throw new Exception("Scope project template (*.tcmproj) not found - is TE130x Scope View installed (TwinCAT 4026: workload TE1300.ScopeViewProfessional.XAE)? Pass template=<path>.");
        }
        var folder = Str(a, "folder") ?? Path.Combine(Path.GetDirectoryName(dte.Solution.FullName) ?? ".", name);
        Directory.CreateDirectory(folder);
        dte.Solution.AddFromTemplate(template, folder, name, false);
        var proj = MeasurementProjects().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var o = new StringBuilder($"Measurement project created: {name} ({folder}), template {Path.GetFileName(template)}");
        if (proj != null) foreach (ProjectItem pi in proj.ProjectItems) o.Append("\n  ").Append(ItemPath(pi));
        o.Append("\nNext: tc_scope action=add_chart, add_axis, add_channel symbol=MAIN.x, then record_start.");
        return o.ToString();
    }

    string Scope(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        switch (action)
        {
            case "list":
            {
                var o = new StringBuilder();
                foreach (var p in MeasurementProjects())
                {
                    o.AppendLine($"{p.Name}  ({p.FullName})");
                    void Walk(ProjectItems items, int depth)
                    {
                        if (items == null || depth > 6) return;
                        foreach (ProjectItem pi in items)
                        {
                            o.Append(' ', depth * 2).AppendLine(pi.Name);
                            Walk(pi.ProjectItems, depth + 1);
                        }
                    }
                    Walk(p.ProjectItems, 1);
                }
                return o.Length > 0 ? o.ToString() : "No TwinCAT Measurement (Scope) project in the solution.";
            }
            case "properties":
            {
                var pi = ScopeItem(Str(a, "path"));
                var o = new StringBuilder($"{ItemPath(pi)}:\n");
                foreach (var p in Props(pi)) o.AppendLine($"  {p.Name} = {PropValue(p)}");
                return o.ToString();
            }
            case "set":
            {
                var pi = ScopeItem(Str(a, "path"));
                return SetProp(pi, Str(a, "property") ?? throw new Exception("property is required"), Str(a, "value") ?? "");
            }
            case "add_chart":
            {
                var scope = ScopeItem(Str(a, "path"));
                var child = (ProjectItem)ScopeCreateChild(ScopeObj(scope), Str(a, "name") ?? "", Int(a, "chartType", 0));
                return "created: " + ItemPath(child);
            }
            case "add_axis":
            {
                var chart = ScopeItem(Str(a, "path"));
                var child = (ProjectItem)ScopeCreateChild(ScopeObj(chart), Str(a, "name") ?? "", 0);
                return "created: " + ItemPath(child);
            }
            case "add_channel":
                return ScopeAddChannel(a);
            case "record_start":
            {
                dynamic s = ScopeObj(ScopeItem(Str(a, "path")));
                s.StartRecord();
                return "recording started (whole scope project). Stop with action=record_stop, then export.";
            }
            case "record_stop":
            {
                dynamic s = ScopeObj(ScopeItem(Str(a, "path")));
                s.StopRecord();
                return "recording stopped. Export with action=export file=<.csv|.svdx|.bin|.tdms|.dat>.";
            }
            case "disconnect":
            {
                dynamic s = ScopeObj(ScopeItem(Str(a, "path")));
                s.Disconnect();
                return "disconnected from the Scope Server (recorded data discarded).";
            }
            case "export":
            {
                var file = Path.GetFullPath(Str(a, "file") ?? throw new Exception("file is required"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                dynamic s = ScopeObj(ScopeItem(Str(a, "path")));
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".csv": s.ExportCSV(file); break;
                    case ".svd": case ".svdx": s.SaveSVD(file); break;
                    case ".bin": s.ExportBinary(file); break;
                    case ".tdms": s.ExportTDMS(file); break;
                    case ".dat": s.ExportDAT(file); break;
                    default: throw new Exception("file extension: .csv, .svdx/.svd, .bin, .tdms or .dat");
                }
                return $"exported: {file}" + (File.Exists(file) ? $" ({new FileInfo(file).Length / 1024} KB)" : " (file not found yet - recording stopped?)");
            }
            default:
                throw new Exception("unknown action: " + action);
        }
    }

    /// <summary>Channel below an axis group plus an ADS acquisition in the DataPool, linked via "Y-Data.Acquisition".</summary>
    string ScopeAddChannel(JsonObject a)
    {
        var symbol = Str(a, "symbol") ?? throw new Exception("symbol is required, e.g. MAIN.fSine");
        var axis = ScopeItem(Str(a, "path"));
        var name = Str(a, "name") ?? symbol;
        var o = new StringBuilder();

        // the scope node (2nd path level) holds the DataPool as first child
        var scopeNode = axis;
        for (int guard = 0; guard < 6; guard++)
        {
            if (scopeNode.Collection?.Parent is ProjectItem parent) scopeNode = parent; else break;
        }
        var dataPool = scopeNode.ProjectItems.Cast<ProjectItem>().FirstOrDefault(x => x.Name.Contains("DataPool", StringComparison.OrdinalIgnoreCase))
                       ?? scopeNode.ProjectItems.Item(1);
        var acq = (ProjectItem)ScopeCreateChild(ScopeObj(dataPool), name, 0 /* ADS */);
        o.AppendLine("acquisition: " + ItemPath(acq));
        var net = Str(a, "netId");
        foreach (var (keys, value) in new (string[] keys, object value)[]
                 {
                     (new[] { "SymbolName", "Symbol" }, symbol),
                     (new[] { "AmsNetId", "NetId", "Target" }, string.IsNullOrEmpty(net) ? "Local" : net),
                     (new[] { "TargetPort", "Port" }, Int(a, "port", 851).ToString()),
                     (new[] { "SampleTime" }, (a["sampleTimeMs"] is JsonValue st ? st.ToString() : "1")),
                 })
        {
            string done = null;
            foreach (var k in keys)
            {
                try { done = SetProp(acq, k, value, required: false); } catch (Exception ex) { done = $"{k}: ERROR {ex.InnerException?.Message ?? ex.Message}"; }
                if (done != null) break;
            }
            o.AppendLine("  " + (done ?? $"{keys[0]}: property not found - set it with action=set path={ItemPath(acq)}"));
        }

        var channel = (ProjectItem)ScopeCreateChild(ScopeObj(axis), name, 0 /* channel */);
        o.AppendLine("channel: " + ItemPath(channel));
        try
        {
            var link = Props(channel).FirstOrDefault(p => p.Name.Equals("Y-Data.Acquisition", StringComparison.OrdinalIgnoreCase))
                       ?? Props(channel).FirstOrDefault(p => p.Name.EndsWith("Acquisition", StringComparison.OrdinalIgnoreCase));
            if (link == null) throw new Exception("property Y-Data.Acquisition not found");
            link.Value = ScopeObj(acq);
            o.AppendLine("  linked: " + link.Name + " = " + PropValue(link));
        }
        catch (Exception ex) { o.AppendLine("  link FAILED: " + (ex.InnerException?.Message ?? ex.Message) + " - check with action=properties"); }
        o.Append("Check the acquisition with action=properties path=" + ItemPath(acq) + " (data type, sample time).");
        return o.ToString();
    }
}
