// TwinCAT Scope (TE130x Scope View). A Scope configuration is a "TwinCAT Measurement" project (.tcmproj) in the solution;
// the configuration itself is a .tcscopex file - plain XML of the Scope model:
//   ScopeProject/SubMember/{ DataPool/SubMember/AdsAcquisition*, YTChart/SubMember/AxisGroup/SubMember/Channel*, TriggerModule }
//   Channel/SubMember/AcquisitionInterpreter/AcquisitionGUID -> AdsAcquisition/Guid
// Charts, axes and channels are therefore edited in the file (like the HMI tools), then the measurement project is reloaded
// in Visual Studio. Recording and export use the Scope's automation object in Visual Studio (IMeasurementScope, late-bound).
// Format taken from the Scope templates installed with TwinCAT (Functions\TwinCAT Measurement\Templates).
//
// Paths: "<measurement project>/<file.tcscopex>/<chart>/<axis group>" by name; empty parts = the first one.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnvDTE;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    const string ScopeAsm = "TwinCAT.Measurement.Scope.API.Model";

    void RegisterScope()
    {
        Add("tc_scope_new", "Adds a TwinCAT Scope configuration (Measurement project, TE130x Scope View) to the solution. Then add charts and " +
                            "channels with tc_scope.",
            Schema(Req(P("name", "string", "Name of the measurement project")),
                   P("template", "string", "Path of a .tcmproj template (default: 'YT Scope Project' of the installation)"),
                   P("folder", "string", "Folder (default: next to the solution)")),
            a => tc.Sta.Run(() => ScopeNew(a), 300_000));

        Add("tc_scope", "TwinCAT Scope: list (configurations, charts, axes, channels with their PLC variables), add_config (.tcscopex from the YT template into an empty measurement project), add_chart, add_axis, " +
                        "add_channel (channel for a PLC variable: type and size read via ADS, sampled with the task cycle), remove_channel, " +
                        "record_start, record_stop, export (file by extension: .csv, .svdx/.svd, .bin, .tdms, .dat - TDMS/DAT need a Scope license). " +
                        "Configuration changes are written to the .tcscopex file and the measurement project is reloaded in Visual Studio. " +
                        "Paths: '<project>/<file.tcscopex>/<chart>/<axis>' by name, missing parts = the first one.",
            Schema(Req(P("action", "string", "list, add_config, add_chart, add_axis, add_channel, remove_channel, record_start, record_stop, export")),
                   P("path", "string", "e.g. 'TcAgentScope/YT Scope Project.tcscopex/Chart 1/Axis 1' (empty = the first scope/chart/axis)"),
                   P("name", "string", "add_config: file name (default YT Scope Project); add_chart/add_axis: name; add_channel: channel name (default: the symbol); remove_channel: channel name"),
                   P("symbol", "string", "add_channel: PLC variable, e.g. MAIN.fSine"),
                   P("netId", "string", "add_channel: AmsNetId (default 127.0.0.1.1.1)"),
                   P("port", "integer", "add_channel: ADS port (default 851)"),
                   P("dataType", "string", "add_channel: Scope data type if the PLC is not reachable: BIT, INT8, UINT8, INT16, UINT16, INT32, UINT32, INT64, UINT64, REAL32, REAL64"),
                   P("file", "string", "export: target file"),
                   P("template", "string", "add_config: .tcscopex to copy (default: YT Scope Project of the installation)")),
            a => tc.Sta.Run(() => Scope(a), 300_000));
    }

    // ================================================================== projects and files
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

    sealed record ScopeRef(Project Project, string File, string Chart, string Axis);

    ScopeRef ResolveScope(string path)
    {
        var projects = MeasurementProjects();
        if (projects.Count == 0) throw new Exception("No TwinCAT Measurement (Scope) project in the solution - create one with tc_scope_new.");
        var parts = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Project proj;
        if (parts.Count > 0 && projects.FirstOrDefault(p => p.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) is { } named) { proj = named; parts.RemoveAt(0); }
        else if (projects.Count == 1) proj = projects[0];
        else throw new Exception("Several measurement projects - start the path with one of: " + string.Join(", ", projects.Select(p => p.Name)));
        var dir = Path.GetDirectoryName(proj.FullName)!;
        var files = Directory.GetFiles(dir, "*.tcscopex", SearchOption.AllDirectories);
        string file;
        if (parts.Count > 0 && files.FirstOrDefault(f => Path.GetFileName(f).Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                                                        || Path.GetFileNameWithoutExtension(f).Equals(parts[0], StringComparison.OrdinalIgnoreCase)) is { } f1)
        { file = f1; parts.RemoveAt(0); }
        else file = files.FirstOrDefault() ?? throw new Exception($"{proj.Name} contains no .tcscopex file.");
        return new ScopeRef(proj, file, parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1));
    }

    static XElement Sub(XElement e)
    {
        var s = e.Element("SubMember");
        if (s == null) { s = new XElement("SubMember"); e.AddFirst(s); }
        return s;
    }

    static string NameOf(XElement e) => (string)e.Element("Name") ?? (string)e.Element("Title") ?? e.Name.LocalName;

    static IEnumerable<XElement> Charts(XDocument d) => Sub(d.Root!).Elements().Where(e => e.Name.LocalName.EndsWith("Chart"));

    static XElement Pick(IEnumerable<XElement> items, string name, string what)
    {
        var list = items.ToList();
        if (string.IsNullOrEmpty(name)) return list.FirstOrDefault() ?? throw new Exception($"no {what} - add one first");
        return list.FirstOrDefault(e => NameOf(e).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new Exception($"{what} '{name}' not found. Available: {string.Join(", ", list.Select(NameOf))}");
    }

    static XElement El(string name, params object[] content) => new(name, content);
    static XElement Model(string name, params object[] content) => new(name, new XAttribute("AssemblyName", ScopeAsm), content);

    /// <summary>Writes the configuration while the measurement project is unloaded in Visual Studio, then reloads it.</summary>
    string SaveScope(ScopeRef r, XDocument d)
    {
        var projFile = r.Project.FullName;
        var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backup", "scope");
        Directory.CreateDirectory(backupDir);
        // Unloading a scope that holds recorded data makes Visual Studio ask "Save dataset? Save data to local file?".
        // Keep the data: save it to the backup folder first, then answer that one question with "No" while unloading.
        string dataNote = "";
        var ops = FindScopeOps(r, out _);
        if (ops != null)
        {
            var svdx = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileNameWithoutExtension(r.File)}.svdx");
            try { ops.SaveSVD(svdx); if (File.Exists(svdx)) dataNote = $"\nRecorded data saved before unloading: {svdx}"; } catch { }
        }
        using var watcher = dataNote.Length > 0 ? AnswerSaveDatasetNo() : null;
        tc.EditUnloaded(projFile, () =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backup", "scope");
            Directory.CreateDirectory(dir);
            File.Copy(r.File, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(r.File)}"), true);
            d.Save(r.File);
            return 0;
        }, out var note);
        return note + dataNote;
    }

    /// <summary>While disposed-not: answers Visual Studio's "Save dataset?" question of the Scope with "No" (the data was saved before).</summary>
    IDisposable AnswerSaveDatasetNo()
    {
        var cts = new CancellationTokenSource();
        int pid = tc.AttachedPid;
        var t = new System.Threading.Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var dlg in IdeDialogs.Find(pid))
                    if (dlg.Title.StartsWith("Save dataset", StringComparison.OrdinalIgnoreCase) || dlg.Text.Contains("Save data to local file", StringComparison.OrdinalIgnoreCase))
                        IdeDialogs.Click(dlg, "No", "Nein");
                cts.Token.WaitHandle.WaitOne(200);
            }
        }) { IsBackground = true, Name = "scope-save-dataset" };
        t.Start();
        return new Disposer(() => { cts.Cancel(); t.Join(2000); cts.Dispose(); });
    }

    sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }

    // ================================================================== tools
    static readonly string[] ScopeTemplateRoots =
    {
        @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TwinCAT Measurement\Templates",
        @"C:\TwinCAT\Functions\TwinCAT Measurement\Templates",
        @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE130X-Scope-View\Templates",
        @"C:\TwinCAT\Functions\TE130X-Scope-View\Templates"
    };

    /// <summary>A file of the installed Scope templates, 'YT Scope Project' preferred.</summary>
    static string ScopeTemplate(string pattern)
    {
        foreach (var root in ScopeTemplateRoots)
        {
            if (!Directory.Exists(root)) continue;
            var files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
            var t = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("YT Scope Project", StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith("YT", StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("Scope", StringComparison.OrdinalIgnoreCase));
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>Adds a Scope configuration (.tcscopex from the YT template) to an existing measurement project, e.g. one created empty in VS.</summary>
    string ScopeAddConfig(JsonObject a)
    {
        var projects = MeasurementProjects();
        if (projects.Count == 0) throw new Exception("No TwinCAT Measurement (Scope) project in the solution - create one with tc_scope_new.");
        var first = (Str(a, "path") ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var proj = (first != null ? projects.FirstOrDefault(p => p.Name.Equals(first, StringComparison.OrdinalIgnoreCase)) : null)
                   ?? (projects.Count == 1 ? projects[0] : throw new Exception("Several measurement projects - pass path=<project>: " + string.Join(", ", projects.Select(p => p.Name))));
        var template = Str(a, "template") ?? ScopeTemplate("*.tcscopex")
                       ?? throw new Exception("Scope template (*.tcscopex) not found - is TE130x Scope View installed? Pass template=<path of a .tcscopex>.");
        var name = Str(a, "name") ?? "YT Scope Project";
        if (!name.EndsWith(".tcscopex", StringComparison.OrdinalIgnoreCase)) name += ".tcscopex";
        var projFile = proj.FullName;
        var projName = proj.Name;
        var target = Path.Combine(Path.GetDirectoryName(projFile)!, name);
        if (File.Exists(target)) throw new Exception($"{name} already exists in {proj.Name}.");

        // fresh GUIDs, so two configurations from the same template do not share identities
        var d = XDocument.Load(template);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in d.Descendants().Where(e => !e.HasElements && Guid.TryParse(e.Value, out _)))
        {
            if (!map.TryGetValue(e.Value, out var n)) map[e.Value] = n = Guid.NewGuid().ToString();
            e.Value = n;
        }

        tc.EditUnloaded(projFile, () =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backup", "scope");
            Directory.CreateDirectory(dir);
            File.Copy(projFile, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(projFile)}"), true);
            d.Save(target);
            var pd = XDocument.Load(projFile, LoadOptions.PreserveWhitespace);
            XNamespace ns = pd.Root!.Name.Namespace;
            var group = new XElement(ns + "ItemGroup",
                new XElement(ns + "Content", new XAttribute("Include", name), new XElement(ns + "SubType", "Content")));
            pd.Root.Add(group);
            pd.Save(projFile);
            return 0;
        }, out var note);
        return $"Scope configuration added: {projName}/{name} (from {Path.GetFileName(template)})" + note +
               "\nNext: tc_scope action=list, then add_channel symbol=MAIN.x.";
    }

    string ScopeNew(JsonObject a)
    {
        var dte = tc.RequireDte();
        var name = Str(a, "name");
        var template = Str(a, "template");
        if (string.IsNullOrEmpty(template))
        {
            template = ScopeTemplate("*.tcmproj");
            if (template == null) throw new Exception("Scope project template (*.tcmproj) not found - is TE130x Scope View installed (TwinCAT 4026: workload TE1300.ScopeViewProfessional.XAE)? Pass template=<path>.");
        }
        var folder = Str(a, "folder") ?? Path.Combine(Path.GetDirectoryName(dte.Solution.FullName) ?? ".", name);
        Directory.CreateDirectory(folder);
        dte.Solution.AddFromTemplate(template, folder, name, false);
        var o = new StringBuilder($"Measurement project created: {name} ({folder}), template {Path.GetFileName(template)}");
        foreach (var f in Directory.GetFiles(folder, "*.tcscopex")) o.Append("\n  ").Append(name).Append('/').Append(Path.GetFileName(f));
        o.Append("\nNext: tc_scope action=add_channel symbol=MAIN.x (the template already has a chart and an axis), then record_start.");
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
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p.FullName)!, "*.tcscopex", SearchOption.AllDirectories))
                    {
                        o.AppendLine($"  {Path.GetFileName(f)}");
                        var d = XDocument.Load(f);
                        var acqs = Sub(d.Root!).Element("DataPool") is { } dp ? Sub(dp).Elements("AdsAcquisition").ToDictionary(x => (string)x.Element("Guid") ?? "", x => x) : new();
                        foreach (var c in Charts(d))
                        {
                            o.AppendLine($"    {NameOf(c)} ({c.Name.LocalName})");
                            foreach (var ag in Sub(c).Elements("AxisGroup"))
                            {
                                o.AppendLine($"      {NameOf(ag)}");
                                foreach (var ch in Sub(ag).Elements("Channel"))
                                {
                                    var guid = (string)ch.Descendants("AcquisitionGUID").FirstOrDefault();
                                    var acq = guid != null && acqs.TryGetValue(guid, out var x) ? x : null;
                                    o.AppendLine($"        {NameOf(ch)}" + (acq != null ? $"  <- {(string)acq.Element("SymbolName")} ({(string)acq.Element("DataType")}, {(string)acq.Element("AmsNetId")}:{(string)acq.Element("TargetPort")})" : ""));
                                }
                            }
                        }
                    }
                }
                return o.Length > 0 ? o.ToString() : "No TwinCAT Measurement (Scope) project in the solution.";
            }
            case "add_config":
                return ScopeAddConfig(a);
            case "add_chart":
            {
                var r = ResolveScope(Str(a, "path"));
                var d = XDocument.Load(r.File);
                var name = Str(a, "name") ?? $"Chart {Charts(d).Count() + 1}";
                var chart = Model("YTChart", El("SubMember", Model("AxisGroup", El("SubMember"), El("Title", "Axis 1"), El("Name", "Axis 1"))),
                                  El("Title", name), El("Name", name));
                var root = Sub(d.Root!);
                var trigger = root.Element("TriggerModule");
                if (trigger != null) trigger.AddBeforeSelf(chart); else root.Add(chart);
                return $"chart added: {name} (with axis 'Axis 1')" + SaveScope(r, d);
            }
            case "add_axis":
            {
                var r = ResolveScope(Str(a, "path"));
                var d = XDocument.Load(r.File);
                var chart = Pick(Charts(d), r.Chart, "chart");
                var name = Str(a, "name") ?? $"Axis {Sub(chart).Elements("AxisGroup").Count() + 1}";
                Sub(chart).Add(Model("AxisGroup", El("SubMember"), El("Title", name), El("Name", name)));
                return $"axis added: {NameOf(chart)}/{name}" + SaveScope(r, d);
            }
            case "add_channel":
                return ScopeAddChannel(a);
            case "remove_channel":
            {
                var r = ResolveScope(Str(a, "path"));
                var d = XDocument.Load(r.File);
                var name = Str(a, "name") ?? throw new Exception("name (channel) is required");
                var ch = d.Descendants("Channel").FirstOrDefault(c => NameOf(c).Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? throw new Exception($"channel '{name}' not found");
                var guid = (string)ch.Descendants("AcquisitionGUID").FirstOrDefault();
                ch.Remove();
                if (guid != null && !d.Descendants("AcquisitionGUID").Any(g => (string)g == guid))
                    d.Descendants("AdsAcquisition").FirstOrDefault(x => (string)x.Element("Guid") == guid)?.Remove();
                return $"channel removed: {name}" + SaveScope(r, d);
            }
            case "record_start":
            case "record_stop":
            case "export":
                return ScopeRuntime(a, action);
            default:
                throw new Exception("unknown action: " + action);
        }
    }

    // ------------------------------------------------------------------ channels
    static readonly Dictionary<string, (string type, int size)> ScopeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = ("BIT", 1), ["BIT"] = ("BIT", 1), ["SINT"] = ("INT8", 1), ["USINT"] = ("UINT8", 1), ["BYTE"] = ("UINT8", 1),
        ["INT"] = ("INT16", 2), ["UINT"] = ("UINT16", 2), ["WORD"] = ("UINT16", 2), ["DINT"] = ("INT32", 4), ["UDINT"] = ("UINT32", 4),
        ["DWORD"] = ("UINT32", 4), ["LINT"] = ("INT64", 8), ["ULINT"] = ("UINT64", 8), ["LWORD"] = ("UINT64", 8),
        ["REAL"] = ("REAL32", 4), ["LREAL"] = ("REAL64", 8), ["TIME"] = ("UINT32", 4),
        ["INT8"] = ("INT8", 1), ["UINT8"] = ("UINT8", 1), ["INT16"] = ("INT16", 2), ["UINT16"] = ("UINT16", 2), ["INT32"] = ("INT32", 4),
        ["UINT32"] = ("UINT32", 4), ["INT64"] = ("INT64", 8), ["UINT64"] = ("UINT64", 8), ["REAL32"] = ("REAL32", 4), ["REAL64"] = ("REAL64", 8),
    };

    static (string iec, long group, long offset)? AdsSymbolInfo(string net, int port, string symbol)
    {
        try
        {
            using var c = new AdsClient();
            c.Connect(AmsNetId.Parse(net), port);
            var loader = SymbolLoaderFactory.Create(c, new TwinCAT.SymbolLoaderSettings(TwinCAT.SymbolsLoadMode.VirtualTree));
            if (!loader.Symbols.TryGetInstance(symbol, out var s)) return null;
            long g = 0, off = 0;
            if (s is IAdsSymbol ads) { g = ads.IndexGroup; off = ads.IndexOffset; }
            return (s.TypeName, g, off);
        }
        catch { return null; }
    }

    string ScopeAddChannel(JsonObject a)
    {
        var symbol = Str(a, "symbol") ?? throw new Exception("symbol is required, e.g. MAIN.fSine");
        var net = Str(a, "netId") ?? "127.0.0.1.1.1";
        int port = Int(a, "port", 851);
        var info = AdsSymbolInfo(net, port, symbol);
        string iec = info?.iec ?? Str(a, "dataType") ?? throw new Exception($"PLC symbol {symbol} not readable via ADS ({net}:{port}) - pass dataType (e.g. REAL64).");
        if (!ScopeTypes.TryGetValue(iec.Trim(), out var st))
            throw new Exception($"Type {iec} cannot be recorded as one channel - choose a member with an elementary type (BOOL, INT, DINT, REAL, LREAL ...).");

        var r = ResolveScope(Str(a, "path"));
        var d = XDocument.Load(r.File);
        var chart = Pick(Charts(d), r.Chart, "chart");
        var axis = Pick(Sub(chart).Elements("AxisGroup"), r.Axis, "axis");
        var root = Sub(d.Root!);
        var pool = root.Element("DataPool");
        if (pool == null) { pool = Model("DataPool"); root.AddFirst(pool); }
        var guid = Guid.NewGuid().ToString();
        var name = Str(a, "name") ?? symbol;

        var acq = Model("AdsAcquisition",
            info is { } i && i.group != 0 ? new object[] { El("IndexGroup", i.group), El("IndexOffset", i.offset) } : Array.Empty<object>(),
            El("SymbolName", symbol), El("SymbolBased", "true"), El("TargetPort", port), El("IsHistorical", "false"), El("Enabled", "true"),
            El("AmsNetId", net), El("DataType", st.type), El("UseTaskSampleTime", "true"), El("ContextMask", -1), El("VariableSize", st.size),
            El("DataAccess", El("DataAccessMode", El("Source", "TwinCAT"), El("Protocoll", "ADS"), El("Format", "TcBinary"), El("TimeContext", "Present"))),
            El("Area", "None"), El("Guid", guid), El("Name", symbol), El("SortPriority", 10), El("ChannelStyleInformation"));
        Sub(pool).Add(acq);

        int[] colors = { -14774017, -16776961, -65536, -16744448, -23296, -8388480, -16711681, -5952982 };
        var color = colors[d.Descendants("Channel").Count() % colors.Length];
        var channel = Model("Channel",
            El("SubMember",
                Model("AcquisitionInterpreter", El("BitMask", "18446744073709551615"), El("Usage", "Y"), El("AcquisitionGUID", guid), El("SubMember"),
                      El("Name", "Y: " + symbol), El("SortPriority", 2)),
                Model("ChannelStyle", El("Visible", "true"),
                      El("SubMember", Model("SeriesStyle", El("MarkColor", color), El("FillColor", 853385437), El("SeriesType", "Line"), El("LineWidth", 2),
                                            El("Antialias", "true"), El("DisplayColor", color), El("SortPriority", 100))),
                      El("SortPriority", 100))),
            El("Title", name), El("Name", name), El("SortPriority", -1));
        Sub(axis).Add(channel);
        return $"channel added: {NameOf(chart)}/{NameOf(axis)}/{name} <- {symbol} ({iec} as {st.type}, {net}:{port}, task cycle)" +
               (info == null ? " - type from dataType, PLC not reachable" : "") + SaveScope(r, d) +
               "\nStart a recording with tc_scope action=record_start (PLC must be running).";
    }

    // ------------------------------------------------------------------ recording / export via Visual Studio
    string ScopeRuntime(JsonObject a, string action)
    {
        var r = ResolveScope(Str(a, "path"));
        var s = FindScopeOps(r, out var problem) ?? throw new Exception(problem);
        switch (action)
        {
            case "record_start":
                s.StartRecord();
                return "recording started (whole scope configuration). Stop with action=record_stop, then export.";
            case "record_stop":
                s.StopRecord();
                return "recording stopped. Export with action=export file=<.csv|.svdx|.bin|.tdms|.dat>.";
            default:
            {
                var file = Path.GetFullPath(Str(a, "file") ?? throw new Exception("file is required"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".csv": s.ExportCSV(file); break;
                    case ".svd": case ".svdx": s.SaveSVD(file); break;
                    case ".bin": s.ExportBinary(file); break;
                    case ".tdms": s.ExportTDMS(file); break;
                    case ".dat": s.ExportDAT(file); break;
                    default: throw new Exception("file extension: .csv, .svdx/.svd, .bin, .tdms or .dat");
                }
                return $"exported: {file}" + (File.Exists(file) ? $" ({new FileInfo(file).Length / 1024} KB)" : " (file not written yet - recording stopped?)");
            }
        }
    }

    /// <summary>The Scope automation object of a configuration, or null with the reason.</summary>
    IScopeOps FindScopeOps(ScopeRef r, out string problem)
    {
        problem = null;
        ProjectItem item = null;
        try
        {
            foreach (ProjectItem pi in r.Project.ProjectItems)
                if (string.Equals(pi.Name, Path.GetFileName(r.File), StringComparison.OrdinalIgnoreCase)) item = pi;
        }
        catch { }
        if (item == null) { problem = $"{Path.GetFileName(r.File)} not found in {r.Project.Name}"; return null; }
        // candidates: the item's object, its child items, the project object
        var candidates = new List<(string where, object o)>();
        void AddCand(string where, Func<object> get) { try { var o = get(); if (o != null) candidates.Add((where, o)); } catch { } }
        AddCand("item", () => item.Object);
        try { foreach (ProjectItem c in item.ProjectItems) { var cc = c; AddCand("child " + cc.Name, () => cc.Object); } } catch { }
        AddCand("project", () => r.Project.Object);
        if (candidates.Count == 0)
        { problem = "The Scope configuration is not loaded in Visual Studio - open it once (double-click the .tcscopex), then retry."; return null; }
        // 1) the IMeasurementScope dispinterface (TwinCAT.Measurement.AutomationInterface.tlb, DISPIDs 0x60020000 ...)
        IScopeOps s = candidates.Select(c => c.o as IMeasurementScopeDisp).Where(x => x != null).Select(x => (IScopeOps)new DispOps(x)).FirstOrDefault();
        // 2) late bound by name through IDispatch, if the object's type info has StartRecord
        if (s == null)
            foreach (var c in candidates)
                if (ComTypeInfo.Members(c.o).Contains("StartRecord")) { s = new NamedOps(c.o); break; }
        if (s == null)
            problem = "No Scope automation object found (IMeasurementScope / StartRecord). Seen: " +
                      string.Join("; ", candidates.Select(c => $"{c.where}: {ComTypeInfo.Describe(c.o)}"));
        return s;
    }

}

interface IScopeOps { void StartRecord(); void StopRecord(); void SaveSVD(string f); void ExportCSV(string f); void ExportBinary(string f); void ExportTDMS(string f); void ExportDAT(string f); }

sealed class DispOps(IMeasurementScopeDisp d) : IScopeOps
{
    public void StartRecord() => d.StartRecord();
    public void StopRecord() => d.StopRecord();
    public void SaveSVD(string f) => d.SaveSVD(f);
    public void ExportCSV(string f) => d.ExportCSV(f);
    public void ExportBinary(string f) => d.ExportBinary(f);
    public void ExportTDMS(string f) => d.ExportTDMS(f);
    public void ExportDAT(string f) => d.ExportDAT(f);
}

/// <summary>Calls by name through IDispatch (GetIDsOfNames + Invoke).</summary>
sealed class NamedOps(object o) : IScopeOps
{
    void Call(string m, params object[] args)
    {
        try { o.GetType().InvokeMember(m, System.Reflection.BindingFlags.InvokeMethod, null, o, args); }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    public void StartRecord() => Call("StartRecord");
    public void StopRecord() => Call("StopRecord");
    public void SaveSVD(string f) => Call("SaveSVD", f);
    public void ExportCSV(string f) => Call("ExportCSV", f);
    public void ExportBinary(string f) => Call("ExportBinary", f);
    public void ExportTDMS(string f) => Call("ExportTDMS", f);
    public void ExportDAT(string f) => Call("ExportDAT", f);
}

/// <summary>Reads the type information of a COM object (IDispatch.GetTypeInfo) - only reads, never calls members.</summary>
static class ComTypeInfo
{
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00020400-0000-0000-C000-000000000046"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatchInfo
    {
        [System.Runtime.InteropServices.PreserveSig] int GetTypeInfoCount(out uint count);
        [System.Runtime.InteropServices.PreserveSig] int GetTypeInfo(uint index, uint lcid, out System.Runtime.InteropServices.ComTypes.ITypeInfo info);
    }

    static System.Runtime.InteropServices.ComTypes.ITypeInfo Info(object o)
    {
        try
        {
            if (o is not IDispatchInfo d || d.GetTypeInfoCount(out var n) != 0 || n == 0) return null;
            return d.GetTypeInfo(0, 0, out var ti) == 0 ? ti : null;
        }
        catch { return null; }
    }

    static string Name(System.Runtime.InteropServices.ComTypes.ITypeInfo ti)
    {
        try { ti.GetDocumentation(-1, out var name, out _, out _, out _); return name; } catch { return "?"; }
    }

    public static HashSet<string> Members(object o)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var ti = Info(o);
        if (ti == null) return set;
        try
        {
            ti.GetTypeAttr(out var pAttr);
            var attr = System.Runtime.InteropServices.Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.TYPEATTR>(pAttr);
            ti.ReleaseTypeAttr(pAttr);
            for (int i = 0; i < attr.cFuncs; i++)
            {
                ti.GetFuncDesc(i, out var pFunc);
                var fd = System.Runtime.InteropServices.Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.FUNCDESC>(pFunc);
                ti.ReleaseFuncDesc(pFunc);
                var names = new string[1];
                ti.GetNames(fd.memid, names, 1, out var got);
                if (got > 0 && names[0] != null) set.Add(names[0]);
            }
        }
        catch { }
        return set;
    }

    public static string Describe(object o)
    {
        var ti = Info(o);
        if (ti == null) return "no type info";
        var m = Members(o).Where(x => !x.StartsWith("get_") && !x.StartsWith("set_")).Take(40);
        return $"{Name(ti)} [{string.Join(", ", m)}]";
    }
}

/// <summary>IMeasurementScope from TwinCAT.Measurement.AutomationInterface.tlb (dispinterface, DISPIDs from the type library).</summary>
[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("8fbf6b86-2b51-4b09-8045-d071f2663878"),
 System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIDispatch)]
public interface IMeasurementScopeDisp
{
    [System.Runtime.InteropServices.DispId(0x60020000)] void StartRecord();
    [System.Runtime.InteropServices.DispId(0x60020001)] void StopRecord();
    [System.Runtime.InteropServices.DispId(0x60020002)] void Disconnect();
    [System.Runtime.InteropServices.DispId(0x60020003)] void SaveSVD([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string file);
    [System.Runtime.InteropServices.DispId(0x60020004)] void ExportCSV([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string file);
    [System.Runtime.InteropServices.DispId(0x60020005)] void ExportBinary([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string file);
    [System.Runtime.InteropServices.DispId(0x60020006)] void ExportTDMS([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string file);
    [System.Runtime.InteropServices.DispId(0x60020007)] void ExportDAT([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string file);
}
