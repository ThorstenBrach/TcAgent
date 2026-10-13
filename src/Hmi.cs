// TwinCAT HMI (TE2000) on the file system. The HMI engineering has no automation API, but a project consists of text files:
//   <name>.hmiproj                       MSBuild project - every file must be a <Content Include> item, otherwise it is not built
//   Properties\tchmiconfig.json          startupView, views[], content[], userControls[], themes, languages ...
//   *.view / *.content                   HTML fragments: a root <div data-tchmi-type="TcHmi.Controls.System.TcHmiView|TcHmiContent">
//                                        with child controls <div id=".." data-tchmi-type=".." data-tchmi-<attribute>="..">,
//                                        complex values and events as <script data-tchmi-target-attribute=".." type="application/json">
//   Server\ADS\ADS.Config.default.json   ADS runtimes (PLC1 -> NETID/PORT)
//   Server\TcHmiSrv\TcHmiSrv.Config.default.json   mapped symbols (SYMBOLS) - bindings use %s%<name>%/s%
// The tools edit these files in place (only the parts they touch; backups in %LOCALAPPDATA%\TcAgent\backup\hmi). Create the HMI project itself once in Visual Studio
// (Add > New Project > TwinCAT HMI Project); afterwards the agent builds pages, controls and symbol mappings.
// Formats from Beckhoff's TE2000 documentation and sample projects (github.com/Beckhoff/TE2000_Client_Samples).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterHmi()
    {
        Add("tc_hmi", "TwinCAT HMI project (file based): info (pages, startup view, ADS runtimes, mapped symbols), read (a view/content file), " +
                      "controls (available control types and their attributes from the installed control package), build (via Visual Studio). " +
                      "Create the HMI project once in Visual Studio; pages/controls/symbols then with tc_hmi_page, tc_hmi_control, tc_hmi_symbol.",
            Schema(Req(P("action", "string", "info, read, controls or build")),
                   P("project", "string", "Path of the .hmiproj or its name (empty = the only HMI project of the solution)"),
                   P("file", "string", "read: file relative to the project, e.g. Desktop.view or Content/Main.content"),
                   P("filter", "string", "controls: only types containing this text, e.g. Button")),
            a => Hmi(a));

        Add("tc_hmi_page", "Creates a view (.view) or content (.content) in the HMI project and registers it in the .hmiproj and tchmiconfig.json. " +
                           "Contents are shown in a TcHmiRegion (tc_hmi_control type=TcHmiRegion attributes={\"target-content\":\"Content/X.content\"}).",
            Schema(Req(P("name", "string", "Name without extension, e.g. Overview")),
                   P("kind", "string", "view or content (default content)"),
                   P("folder", "string", "Folder relative to the project (default: views in the root, contents in Content)"),
                   P("width", "integer", "content: width in px (default 1024)"), P("height", "integer", "content: height in px (default 600)"),
                   P("startup", "boolean", "view: make it the start view"),
                   P("project", "string", "HMI project (empty = the only one)"),
                   P("reload", "boolean", "If the HMI project is open in Visual Studio it is unloaded for the change and reloaded automatically (default true). " +
                                          "false = leave it unloaded - then ask the user to reload it (right-click > Reload Project). Use false with a 32-bit TcXaeShell and a large solution."),
                   P("dryRun", "boolean", "Only show what would be created")),
            a => HmiWrite(a, () => HmiPage(a)));

        Add("tc_hmi_control", "Adds, changes or removes a control in a view/content. Attributes without the data-tchmi- prefix, e.g. " +
                              "{\"text\":\"Start\",\"state-symbol\":\"%s%PLC1.MAIN.bStart%/s%\"}; bindings %s%<mapped symbol>%/s%, two-way: " +
                              "%s%PLC1.MAIN.x|BindingMode=TwoWay%/s%. Object values (colors ...) as JSON objects. Events: writeSymbol/writeValue for a " +
                              "button press, or events = trigger JSON array. Check attribute names with tc_hmi action=controls.",
            Schema(Req(P("action", "string", "add, update or remove")), Req(P("file", "string", "View/content relative to the project, e.g. Desktop.view")),
                   Req(P("id", "string", "Unique control id, e.g. btnStart")),
                   P("type", "string", "add: control type, e.g. TcHmiButton, TcHmiTextblock, TcHmiCheckbox, TcHmiRegion or full TcHmi.Controls.Beckhoff.TcHmiButton"),
                   P("left", "number", "Position/size in px"), P("top", "number", ""), P("width", "number", ""), P("height", "number", ""),
                   P("attributes", "object", "Further attributes {name: value}; null removes an attribute"),
                   P("parent", "string", "add: id of a container control to insert into (default: the root)"),
                   P("writeSymbol", "string", "Shortcut: on press write writeValue to this symbol, e.g. %s%PLC1.MAIN.bStart%/s% or PLC1.MAIN.bStart"),
                   P("writeValue", "string", "Value for writeSymbol (true/false/number/text, default true)"),
                   P("events", "array", "Trigger list as in the file: [{\"event\":\"<id>.onPressed\",\"actions\":[...]}] (replaces existing)"),
                   P("project", "string", "HMI project (empty = the only one)"),
                   P("reload", "boolean", "If the HMI project is open in Visual Studio it is unloaded for the change and reloaded automatically (default true). " +
                                          "false = leave it unloaded - then ask the user to reload it (right-click > Reload Project). Use false with a 32-bit TcXaeShell and a large solution."),
                   P("dryRun", "boolean", "Only show the resulting HTML")),
            a => HmiWrite(a, () => HmiControl(a)));

        Add("tc_hmi_symbol", "Server symbols of the HMI: list, map (PLC variables -> HMI symbols in TcHmiSrv.Config.default.json, type read via ADS " +
                             "if not given), unmap, runtimes (ADS runtimes PLC1 ...), add_runtime. Alternative without mapping by hand: PLC attribute " +
                             "{attribute 'TcHmiSymbol.AddSymbol'} + 'Map Automap Symbols' in Visual Studio. Best edit while the HMI project is not open; if it is open " +
                             "it is unloaded and reloaded (reload=false: left unloaded for the user to reload).",
            Schema(Req(P("action", "string", "list, map, unmap, runtimes or add_runtime")),
                   P("symbols", "string", "map/unmap: PLC variables separated by ',' e.g. MAIN.bStart,MAIN.nSpeed (optionally with type: MAIN.nSpeed:INT)"),
                   P("runtime", "string", "ADS runtime name (default PLC1)"),
                   P("type", "string", "map: data type for all symbols without own type, e.g. BOOL, INT, LREAL, STRING(80)"),
                   P("netId", "string", "add_runtime: AmsNetId (default 127.0.0.1.1.1)"), P("port", "integer", "add_runtime: ADS port (default 851)"),
                   P("project", "string", "HMI project (empty = the only one)"),
                   P("reload", "boolean", "If the HMI project is open in Visual Studio it is unloaded for the change and reloaded automatically (default true). " +
                                          "false = leave it unloaded - then ask the user to reload it (right-click > Reload Project). Use false with a 32-bit TcXaeShell and a large solution."),
                   P("dryRun", "boolean", "Only show what would change")),
            a => (Str(a, "action") ?? "").ToLowerInvariant() is "list" or "runtimes" ? HmiSymbol(a) : HmiWrite(a, () => HmiSymbol(a)));
    }

    /// <summary>File changes to an HMI project only while it is unloaded in Visual Studio - the HMI engineering keeps
    /// tchmiconfig.json and open views in memory and overwrites (or crashes on) changes made from outside.</summary>
    string HmiWrite(JsonObject a, Func<string> write)
    {
        if (DryRun(a)) return write();
        var p = FindHmi(Str(a, "project"));
        var reload = Bool(a, "reload", true);
        return tc.Sta.Run(() =>
        {
            pinnedHmi = p;   // while unloaded, Visual Studio no longer reports the project - keep the resolved one
            try
            {
                // the 32-bit memory guard runs before the unload and again before the reload (TwinCatSession.EditUnloaded)
                var r = tc.EditUnloaded(p.ProjFile, write, out var note, reload);
                return r + note;
            }
            catch (Exception ex) when (ex is not IdeLostException && TwinCatSession.IsIdeLost(ex))
            {
                // e.g. RPC 0x800706BE while saving/closing the HMI documents: the IDE process is gone
                tc.MarkLost();
                throw new IdeLostException($"The IDE was lost while changing the HMI project {p.Name} ({ex.Message}): TcXaeShell/Visual Studio " +
                                           "crashed or was closed. Tell the user to restart the IDE and open the solution, then call tc_attach and check " +
                                           $"the change (tc_hmi action=info) - do not repeat it blindly. Backups: %LOCALAPPDATA%\\TcAgent\\backup\\hmi. " +
                                           "With a 32-bit TcXaeShell and a large solution use reload=false.", ex);
            }
            finally { pinnedHmi = null; }
        }, 300_000);
    }

    HmiProject pinnedHmi;

    // ================================================================== project files
    sealed record HmiProject(string ProjFile, string Dir)
    {
        public string Config => Path.Combine(Dir, "Properties", "tchmiconfig.json");
        public string ServerConfig => Path.Combine(Dir, "Server", "TcHmiSrv", "TcHmiSrv.Config.default.json");
        public string AdsConfig => Path.Combine(Dir, "Server", "ADS", "ADS.Config.default.json");
        public string Name => Path.GetFileNameWithoutExtension(ProjFile);
        public string Full(string rel) => Path.GetFullPath(Path.Combine(Dir, rel.Replace('/', '\\')));
    }

    HmiProject FindHmi(string hint)
    {
        if (pinnedHmi != null) return pinnedHmi;
        if (!string.IsNullOrEmpty(hint) && hint.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase) && File.Exists(hint))
            return new HmiProject(Path.GetFullPath(hint), Path.GetDirectoryName(Path.GetFullPath(hint))!);
        var found = new List<string>();
        try
        {
            tc.Sta.Run(() =>
            {
                foreach (EnvDTE.Project p in tc.RequireDte().Solution.Projects)
                {
                    try { if ((p.FullName ?? "").EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase)) found.Add(p.FullName); } catch { }
                }
                if (found.Count == 0)
                {
                    var dir = Path.GetDirectoryName(tc.RequireDte().Solution.FullName);
                    if (!string.IsNullOrEmpty(dir))
                        found.AddRange(Directory.EnumerateFiles(dir, "*.hmiproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }));
                    // HMI projects are often created next to the TwinCAT folder (..\HmiProject)
                    var parent = string.IsNullOrEmpty(dir) ? null : Path.GetDirectoryName(dir);
                    if (found.Count == 0 && !string.IsNullOrEmpty(parent))
                        found.AddRange(Directory.EnumerateFiles(parent, "*.hmiproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }));
                }
                return 0;
            });
        }
        catch (Exception ex) { if (found.Count == 0) throw new Exception("No HMI project found (pass project=<path to .hmiproj>): " + ex.Message); }
        if (!string.IsNullOrEmpty(hint)) found = found.Where(f => Path.GetFileNameWithoutExtension(f).Equals(hint, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) throw new Exception("No TwinCAT HMI project (.hmiproj) found - create one in Visual Studio (Add > New Project > TwinCAT HMI Project) or pass project=<path>.");
        if (found.Count > 1) throw new Exception("Several HMI projects - pass project=: " + string.Join(", ", found));
        return new HmiProject(found[0], Path.GetDirectoryName(found[0])!);
    }

    static readonly JsonDocumentOptions JsonRead = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonObject ReadJson(string file) =>
        JsonNode.Parse(File.ReadAllText(file), documentOptions: JsonRead)?.AsObject() ?? throw new Exception("empty JSON: " + file);

    /// <summary>Backup before every change - outside the project, so it does not end up in the HMI build or in Git.</summary>
    static void Backup(string file)
    {
        if (!File.Exists(file)) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backup", "hmi");
        Directory.CreateDirectory(dir);
        File.Copy(file, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(file)}"), true);
    }

    static void WriteJson(string file, JsonNode node)
    {
        var bom = HasBom(file);
        Backup(file);
        File.WriteAllText(file, node.ToJsonString(JsonWrite), new UTF8Encoding(bom));
    }

    /// <summary>Keeps the byte order mark as it was (Visual Studio writes HMI files without BOM); new files without BOM.</summary>
    static bool HasBom(string file)
    {
        if (!File.Exists(file)) return false;
        using var f = File.OpenRead(file);
        var b = new byte[3];
        return f.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }

    static void WriteText(string file, string text) { var bom = HasBom(file); Backup(file); File.WriteAllText(file, text, new UTF8Encoding(bom)); }

    /// <summary>Adds &lt;Content Include="rel"&gt; to the .hmiproj if missing (the HMI build only sees Content items).</summary>
    static bool RegisterContent(HmiProject p, string rel)
    {
        rel = rel.Replace('/', '\\');
        var doc = XDocument.Load(p.ProjFile, LoadOptions.PreserveWhitespace);
        var ns = doc.Root!.Name.Namespace;
        if (doc.Descendants(ns + "Content").Any(e => string.Equals((string)e.Attribute("Include"), rel, StringComparison.OrdinalIgnoreCase))) return false;
        var group = doc.Root.Elements(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Content").Any());
        if (group == null) { group = new XElement(ns + "ItemGroup"); doc.Root.Add(group); }
        group.Add(new XElement(ns + "Content", new XAttribute("Include", rel), new XElement(ns + "SubType", "Content")));
        var bom = HasBom(p.ProjFile);
        Backup(p.ProjFile);
        using (var w = System.Xml.XmlWriter.Create(p.ProjFile, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(bom), Indent = false }))
            doc.Save(w);
        return true;
    }

    // ================================================================== tc_hmi
    string Hmi(JsonObject a)
    {
        var p = FindHmi(Str(a, "project"));
        switch ((Str(a, "action") ?? "").ToLowerInvariant())
        {
            case "info":
            {
                var o = new StringBuilder($"HMI project {p.Name}: {p.ProjFile}\n");
                if (File.Exists(p.Config))
                {
                    var cfg = ReadJson(p.Config);
                    o.AppendLine($"Start view: {cfg["startupView"]}");
                    foreach (var key in new[] { "views", "content", "userControls" })
                        if (cfg[key] is JsonArray arr) o.AppendLine($"{key}: " + string.Join(", ", arr.Select(x => x?["url"]?.ToString())));
                    if (cfg["languages"] is JsonObject langs) o.AppendLine("languages: " + string.Join(", ", langs.Select(kv => kv.Key)));
                }
                else o.AppendLine("tchmiconfig.json not found: " + p.Config);
                if (File.Exists(p.AdsConfig) && ReadJson(p.AdsConfig)["RUNTIMES"] is JsonObject rts)
                    o.AppendLine("ADS runtimes: " + string.Join(", ", rts.Select(kv => $"{kv.Key} ({kv.Value?["NETID"]}:{kv.Value?["PORT"]})")));
                if (File.Exists(p.ServerConfig) && ReadJson(p.ServerConfig)["SYMBOLS"] is JsonObject syms)
                {
                    var plc = syms.Where(kv => IsPlcSymbol(kv.Value)).Select(kv => kv.Key).ToList();
                    o.AppendLine($"Mapped PLC symbols: {plc.Count}" + (plc.Count > 0 ? " - " + string.Join(", ", plc.Take(40)) + (plc.Count > 40 ? " ..." : "") : "") +
                                 $" ({syms.Count - plc.Count} server/system symbols not shown)");
                }
                var pages = Directory.EnumerateFiles(p.Dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".view", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".content", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                    .Select(f => Path.GetRelativePath(p.Dir, f).Replace('\\', '/')).ToList();
                o.AppendLine("Files: " + string.Join(", ", pages));
                return o.ToString();
            }
            case "read":
            {
                var file = p.Full(Str(a, "file") ?? throw new Exception("file is required"));
                var text = File.ReadAllText(file);
                var ids = Regex.Matches(text, @"<div\s+id=""([^""]+)""\s+data-tchmi-type=""([^""]+)""").Select(m => $"{m.Groups[1].Value} ({m.Groups[2].Value.Split('.').Last()})");
                return $"{Path.GetRelativePath(p.Dir, file)} - controls: {string.Join(", ", ids)}\n\n" + (text.Length > 30000 ? text[..30000] + "\n... (shortened)" : text);
            }
            case "controls":
                return HmiControlTypes(p, Str(a, "filter"));
            case "build":
                return tc.Sta.Run(() =>
                {
                    var dte = tc.RequireDte();
                    EnvDTE.Project proj = null;
                    foreach (EnvDTE.Project x in dte.Solution.Projects)
                        try { if (string.Equals(x.FullName, p.ProjFile, StringComparison.OrdinalIgnoreCase)) proj = x; } catch { }
                    if (proj == null) throw new Exception("The HMI project is not part of the open solution - add it in Visual Studio.");
                    var sb = dte.Solution.SolutionBuild;
                    sb.BuildProject(sb.ActiveConfiguration.Name, proj.UniqueName, true);
                    var (compile, _) = tc.ReadErrorList(1000);
                    var mine = compile.Where(e => (e.Project ?? "").Contains(p.Name, StringComparison.OrdinalIgnoreCase) || (e.File ?? "").StartsWith(p.Dir, StringComparison.OrdinalIgnoreCase)).ToList();
                    return $"HMI build {(sb.LastBuildInfo == 0 ? "OK" : "FAILED")}: {mine.Count(e => e.Level == "ERROR")} errors, {mine.Count(e => e.Level == "WARN")} warnings\n" +
                           string.Join("\n", mine.Take(200)) + "\nPublish to the HMI server in Visual Studio (TwinCAT HMI > Publish).";
                }, 600_000);
            default: throw new Exception("action: info, read, controls or build");
        }
    }

    /// <summary>A mapped PLC variable (not one of the server's own functions/system symbols).</summary>
    static bool IsPlcSymbol(JsonNode v) =>
        v?["DOMAIN"]?.ToString() == "ADS" && (v?["MAPPING"]?.ToString() ?? "").Contains("::") && v?["SCHEMA"]?["function"] == null;

    /// <summary>Control types and attribute names from Description.json files of the installed control packages.</summary>
    static string HmiControlTypes(HmiProject p, string filter)
    {
        var roots = new List<string>();
        for (var d = new DirectoryInfo(p.Dir); d != null && roots.Count == 0; d = d.Parent)
        {
            var pk = Path.Combine(d.FullName, "Packages");
            if (Directory.Exists(pk)) roots.AddRange(Directory.GetDirectories(pk, "Beckhoff.TwinCAT.HMI.Controls.*"));
        }
        // packages.config restores into <solution folder>\Packages - the HMI project's tsconfig.json points there
        // (e.g. "../../TwinCAT/Packages/Beckhoff.TwinCAT.HMI.Framework.14.2.110/...")
        if (roots.Count == 0)
            try
            {
                var ts = Path.Combine(p.Dir, "tsconfig.json");
                if (File.Exists(ts))
                    foreach (Match m in Regex.Matches(File.ReadAllText(ts), @"""([^""]*?[\\/]Packages)[\\/]Beckhoff\.TwinCAT\.HMI\.", RegexOptions.IgnoreCase))
                    {
                        var pk = Path.GetFullPath(Path.Combine(p.Dir, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                        if (Directory.Exists(pk)) { roots.AddRange(Directory.GetDirectories(pk, "Beckhoff.TwinCAT.HMI.Controls.*")); break; }
                    }
            }
            catch { }
        // NuGet global package folder (packages restored there, e.g. %USERPROFILE%\.nuget\packages\beckhoff.twincat.hmi.controls\14.2.110)
        var global = Path.Combine(Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                                  "beckhoff.twincat.hmi.controls");
        if (roots.Count == 0 && Directory.Exists(global)) roots.AddRange(Directory.GetDirectories(global));
        if (roots.Count == 0) return "Control package not found (Packages\\Beckhoff.TwinCAT.HMI.Controls.* or the NuGet cache) - build the HMI project once in Visual Studio. " +
                                     "Common types: TcHmiButton, TcHmiTextblock, TcHmiInput, TcHmiSpinboxInput, TcHmiCheckbox, TcHmiToggleButton, TcHmiImage, " +
                                     "TcHmiRadialGauge, TcHmiLinearGauge, TcHmiCombobox, TcHmiDatagrid, TcHmiContainer, TcHmiGrid; System: TcHmiRegion.";
        var root = roots.OrderByDescending(r => r).First();
        var o = new StringBuilder($"From {root}:\n");
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(root, "Description.json", SearchOption.AllDirectories))
        {
            JsonObject d;
            try { d = ReadJson(file); } catch { continue; }
            var name = d["name"]?.ToString() ?? Path.GetFileName(Path.GetDirectoryName(file));
            var ns = d["namespace"]?.ToString();
            var full = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            if (!string.IsNullOrEmpty(filter) && !full.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = (d["attributes"] as JsonArray)?.Select(x => x?["name"]?.ToString()).Where(x => x != null).ToList() ?? new List<string>();
            var events = (d["events"] as JsonArray)?.Select(x => x?["name"]?.ToString()).Where(x => x != null).ToList() ?? new List<string>();
            o.AppendLine($"{full}");
            if (!string.IsNullOrEmpty(filter) || attrs.Count < 25)
            {
                if (attrs.Count > 0) o.AppendLine("  attributes: " + string.Join(", ", attrs));
                if (events.Count > 0) o.AppendLine("  events: " + string.Join(", ", events));
            }
            if (++n >= 200) { o.AppendLine("..."); break; }
        }
        if (string.IsNullOrEmpty(filter)) o.Append("Details of a type: tc_hmi action=controls filter=<type>.");
        return o.ToString();
    }

    // ================================================================== pages
    string HmiPage(JsonObject a)
    {
        var p = FindHmi(Str(a, "project"));
        var name = Str(a, "name") ?? throw new Exception("name is required");
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new Exception("name: letters, digits and _ only");
        bool view = (Str(a, "kind") ?? "content").Equals("view", StringComparison.OrdinalIgnoreCase);
        var folder = Str(a, "folder") ?? (view ? "" : "Content");
        var rel = (folder.Length > 0 ? folder.TrimEnd('/', '\\') + "/" : "") + name + (view ? ".view" : ".content");
        var file = p.Full(rel);
        if (File.Exists(file)) throw new Exception("exists already: " + rel);
        int w = Int(a, "width", 1024), h = Int(a, "height", 600);
        var html = view
            ? $"<div id=\"{name}\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiView\" data-tchmi-top=\"0\" data-tchmi-left=\"0\" data-tchmi-width-mode=\"Content\" " +
              "data-tchmi-min-width=\"100\" data-tchmi-min-width-unit=\"%\" data-tchmi-height-mode=\"Content\" data-tchmi-min-height=\"100\" data-tchmi-min-height-unit=\"%\">\r\n\r\n</div>\r\n"
            : $"<div id=\"{name}\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiContent\" data-tchmi-top=\"0\" data-tchmi-left=\"0\" data-tchmi-width=\"{w}\" " +
              $"data-tchmi-width-unit=\"px\" data-tchmi-height=\"{h}\" data-tchmi-height-unit=\"px\">\r\n\r\n</div>\r\n";
        if (DryRun(a)) return $"PROPOSAL (nothing written): {rel}\n{html}";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        WriteText(file, html);
        var o = new StringBuilder($"created: {rel}");
        if (RegisterContent(p, rel)) o.Append("\n  registered in " + Path.GetFileName(p.ProjFile));
        if (File.Exists(p.Config))
        {
            var cfg = ReadJson(p.Config);
            var key = view ? "views" : "content";
            var arr = cfg[key] as JsonArray ?? new JsonArray();
            cfg[key] = arr;
            if (!arr.Any(x => string.Equals(x?["url"]?.ToString(), rel, StringComparison.OrdinalIgnoreCase)))
                arr.Add(view ? new JsonObject { ["url"] = rel, ["preload"] = false, ["keepAlive"] = false, ["preloadBindings"] = false }
                             : new JsonObject { ["url"] = rel, ["preload"] = false, ["keepAlive"] = false, ["loadSync"] = false, ["preloadBindings"] = false });
            if (view && Bool(a, "startup", false)) cfg["startupView"] = rel;
            WriteJson(p.Config, cfg);
            o.Append($"\n  registered in tchmiconfig.json ({key})" + (view && Bool(a, "startup", false) ? ", start view" : ""));
        }
        o.Append(view ? "\nAdd controls with tc_hmi_control file=" + rel
                      : $"\nShow it with a region: tc_hmi_control action=add file=Desktop.view type=TcHmiRegion id=region{name} attributes={{\"target-content\":\"{rel}\"}}");
        return o.ToString();
    }

    // ================================================================== controls
    static string ControlType(string t)
    {
        if (string.IsNullOrEmpty(t)) throw new Exception("type is required, e.g. TcHmiButton");
        if (t.Contains('.')) return t;
        return t is "TcHmiRegion" or "TcHmiView" or "TcHmiContent" or "TcHmiUserControl" or "TcHmiUserControlHost" or "TcHmiPartial"
            ? "TcHmi.Controls.System." + t : "TcHmi.Controls.Beckhoff." + t;
    }

    // only what HTML attributes need - umlauts stay readable
    static string Attr(string s) => (s ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Start index and end index (exclusive) of the &lt;div id="id"&gt; element, counting nested divs.</summary>
    static (int start, int end) FindElement(string html, string id)
    {
        var m = Regex.Match(html, $@"<div\b[^>]*\sid=""{Regex.Escape(id)}""[^>]*>", RegexOptions.IgnoreCase);
        if (!m.Success) return (-1, -1);
        int depth = 0, i = m.Index;
        var tag = new Regex(@"<div\b|</div\s*>|<script\b[\s\S]*?</script\s*>", RegexOptions.IgnoreCase);
        for (var t = tag.Match(html, i); t.Success; t = t.NextMatch())
        {
            if (t.Value.StartsWith("<script", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Value.StartsWith("</", StringComparison.Ordinal)) { depth--; if (depth == 0) return (m.Index, t.Index + t.Length); }
            else depth++;
        }
        throw new Exception($"element {id}: closing </div> not found");
    }

    static string RootId(string html) => Regex.Match(html, @"<div\b[^>]*\sid=""([^""]+)""").Groups[1].Value;

    /// <summary>The element's own &lt;script data-tchmi-target-attribute="data-tchmi-name"&gt; in its body - not the one of a nested control.</summary>
    static System.Text.RegularExpressions.Match OwnScript(string body, string name)
    {
        int depth = 0;
        var tag = new Regex($@"(?<own><script\s+data-tchmi-target-attribute=""data-tchmi-{Regex.Escape(name)}""[\s\S]*?</script>\s*)|<div\b|</div\s*>|<script\b[\s\S]*?</script\s*>", RegexOptions.IgnoreCase);
        for (var t = tag.Match(body); t.Success; t = t.NextMatch())
        {
            if (t.Groups["own"].Success) { if (depth == 0) return t; }
            else if (t.Value.StartsWith("<script", StringComparison.OrdinalIgnoreCase)) continue;
            else if (t.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else depth++;
        }
        return null;
    }

    static string ValueText(JsonNode v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";

    string HmiControl(JsonObject a)
    {
        var p = FindHmi(Str(a, "project"));
        var rel = Str(a, "file") ?? throw new Exception("file is required");
        var file = p.Full(rel);
        var html = File.ReadAllText(file);
        var id = Str(a, "id") ?? throw new Exception("id is required");
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        var (start, end) = FindElement(html, id);

        if (action == "remove")
        {
            if (start < 0) throw new Exception($"control {id} not found in {rel}");
            if (id == RootId(html)) throw new Exception("the root element cannot be removed");
            var cut = html.Remove(start, end - start);
            if (DryRun(a)) return $"PROPOSAL: remove {id} from {rel}";
            WriteText(file, Regex.Replace(cut, @"(\r?\n){3,}", "\r\n\r\n"));
            return $"removed: {id} from {rel}";
        }

        // collect attributes: plain ones go into the tag, objects/arrays into <script> children
        var plain = new List<(string name, string value)>();
        var scripts = new List<(string name, string json)>();
        var removeAttrs = new List<string>();
        void Put(string n, JsonNode v)
        {
            n = n.StartsWith("data-tchmi-") ? n["data-tchmi-".Length..] : n;
            if (v == null) { removeAttrs.Add(n); return; }
            if (v is JsonObject || v is JsonArray) scripts.Add((n, v.ToJsonString(JsonWrite)));
            else plain.Add((n, ValueText(v)));
        }
        foreach (var k in new[] { "left", "top", "width", "height" })
            if (a[k] is JsonValue jv) { Put(k, JsonValue.Create(ValueText(jv))); plain.Add((k + "-unit", "px")); }
        if (a["attributes"] is JsonObject attrs) foreach (var kv in attrs) Put(kv.Key, kv.Value);

        JsonArray trigger = a["events"] as JsonArray;
        if (!string.IsNullOrEmpty(Str(a, "writeSymbol")))
        {
            var sym = Str(a, "writeSymbol")!;
            if (!sym.StartsWith("%")) sym = $"%s%{sym}%/s%";
            var raw = Str(a, "writeValue") ?? "true";
            JsonNode val; string vt;
            if (bool.TryParse(raw, out var b)) { val = b; vt = "tchmi:general#/definitions/BOOL"; }
            else if (long.TryParse(raw, out var l)) { val = l; vt = "tchmi:general#/definitions/DINT"; }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { val = d; vt = "tchmi:general#/definitions/LREAL"; }
            else { val = raw; vt = "tchmi:general#/definitions/String"; }
            trigger ??= new JsonArray();
            trigger.Add(new JsonObject
            {
                ["event"] = $"{id}.onPressed",
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["objectType"] = "WriteToSymbol", ["active"] = true, ["symbolExpression"] = sym,
                    ["value"] = new JsonObject { ["objectType"] = "StaticValue", ["valueType"] = vt, ["value"] = val },
                    ["asyncWait"] = true
                })
            });
        }
        if (trigger != null) scripts.Add(("trigger", trigger.ToJsonString(JsonWrite)));

        string Script((string name, string json) s) =>
            $"<script data-tchmi-target-attribute=\"data-tchmi-{s.name}\" type=\"application/json\">\r\n{s.json}\r\n</script>\r\n";

        string result;
        if (action == "add")
        {
            if (start >= 0) throw new Exception($"id {id} exists already in {rel} - use action=update");
            var type = ControlType(Str(a, "type"));
            var sb = new StringBuilder($"<div id=\"{Attr(id)}\" data-tchmi-type=\"{Attr(type)}\"");
            foreach (var (n, v) in plain.GroupBy(x => x.name).Select(g => g.Last())) sb.Append($" data-tchmi-{n}=\"{Attr(v)}\"");
            sb.Append(">\r\n");
            foreach (var s in scripts) sb.Append(Script(s));
            sb.Append("</div>\r\n");
            var parentId = Str(a, "parent") ?? RootId(html);
            var (ps, pe) = FindElement(html, parentId);
            if (ps < 0) throw new Exception($"parent {parentId} not found");
            int insertAt = html.LastIndexOf("</div", pe - 1, pe - ps, StringComparison.OrdinalIgnoreCase);
            result = html.Insert(insertAt, "\r\n" + sb);
        }
        else if (action == "update")
        {
            if (start < 0) throw new Exception($"control {id} not found in {rel}");
            var element = html[start..end];
            var openEnd = element.IndexOf('>') + 1;
            var open = element[..openEnd];
            var body = element[openEnd..];
            foreach (var (n, v) in plain)
            {
                var rx = new Regex($@"\sdata-tchmi-{Regex.Escape(n)}=""[^""]*""");
                var repl = $" data-tchmi-{n}=\"{Attr(v)}\"";   // evaluator: '$' in values is no substitution
                open = rx.IsMatch(open) ? rx.Replace(open, _ => repl, 1) : open.Insert(open.Length - 1, repl);
            }
            foreach (var n in removeAttrs)
            {
                open = Regex.Replace(open, $@"\sdata-tchmi-{Regex.Escape(n)}(-unit)?=""[^""]*""", "");
                var own = OwnScript(body, n);
                if (own != null) body = body.Remove(own.Index, own.Length);
            }
            foreach (var s in scripts)
            {
                var own = OwnScript(body, s.name);
                body = own != null ? body[..own.Index] + Script(s) + body[(own.Index + own.Length)..] : "\r\n" + Script(s) + body.TrimStart('\r', '\n');
            }
            result = html[..start] + open + body + html[end..];
        }
        else throw new Exception("action: add, update or remove");

        if (DryRun(a)) { var (s2, e2) = FindElement(result, id); return "PROPOSAL (nothing written):\n" + result[s2..e2]; }
        WriteText(file, result);
        var unmapped = Regex.Matches(result[FindElement(result, id).start..FindElement(result, id).end], @"%s%([^%|]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var hint = "";
        if (unmapped.Count > 0 && File.Exists(p.ServerConfig) && ReadJson(p.ServerConfig)["SYMBOLS"] is JsonObject syms)
        {
            var missing = unmapped.Where(u => !syms.ContainsKey(u)).ToList();
            if (missing.Count > 0) hint = "\nNot mapped yet: " + string.Join(", ", missing) + " - map with tc_hmi_symbol action=map.";
        }
        return $"{action}: {id} in {rel}" + hint;
    }

    // ================================================================== server symbols
    static string SchemaRef(string type)
    {
        type = (type ?? "").Trim().ToUpperInvariant();
        var m = Regex.Match(type, @"^(W?STRING)\s*\(\s*(\d+)\s*\)$");
        if (m.Success) return $"tchmi:general#/definitions/{m.Groups[1].Value}%28{m.Groups[2].Value}%29";
        if (type is "STRING" or "WSTRING") return $"tchmi:general#/definitions/{type}%2880%29";
        return "tchmi:general#/definitions/" + type;
    }

    string HmiSymbol(JsonObject a)
    {
        var p = FindHmi(Str(a, "project"));
        var runtime = Str(a, "runtime") ?? "PLC1";
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        switch (action)
        {
            case "runtimes":
            {
                if (!File.Exists(p.AdsConfig)) return "ADS configuration not found: " + p.AdsConfig;
                var rts = ReadJson(p.AdsConfig)["RUNTIMES"] as JsonObject;
                return rts == null || rts.Count == 0 ? "No ADS runtimes." :
                    "ADS runtimes:\n" + string.Join("\n", rts.Select(kv => $"  {kv.Key}: NETID={kv.Value?["NETID"]} PORT={kv.Value?["PORT"]} ENABLED={kv.Value?["ENABLED"]}"));
            }
            case "add_runtime":
            {
                if (!File.Exists(p.AdsConfig)) throw new Exception("ADS configuration not found: " + p.AdsConfig);
                var cfg = ReadJson(p.AdsConfig);
                var rts = cfg["RUNTIMES"] as JsonObject ?? new JsonObject();
                cfg["RUNTIMES"] = rts;
                var entry = rts[runtime] as JsonObject ?? new JsonObject { ["ENABLED"] = true, ["READ_ONLY"] = false, ["SYMBOLS"] = new JsonObject(), ["USE_WHITELISTING"] = false };
                entry["NETID"] = Str(a, "netId") ?? "127.0.0.1.1.1";
                entry["PORT"] = Int(a, "port", 851);
                rts[runtime] = entry;
                if (DryRun(a)) return $"PROPOSAL: runtime {runtime} = {entry.ToJsonString()}";
                WriteJson(p.AdsConfig, cfg);
                return $"ADS runtime {runtime}: {entry["NETID"]}:{entry["PORT"]}";
            }
            case "list":
            {
                if (!File.Exists(p.ServerConfig)) return "Server configuration not found: " + p.ServerConfig;
                var syms = ReadJson(p.ServerConfig)["SYMBOLS"] as JsonObject;
                var plc = syms?.Where(kv => IsPlcSymbol(kv.Value)).ToList() ?? new();
                int hidden = (syms?.Count ?? 0) - plc.Count;
                return (plc.Count == 0 ? "No mapped PLC symbols." :
                        $"{plc.Count} mapped PLC symbols:\n" + string.Join("\n", plc.Select(kv => $"  {kv.Key}  <- {kv.Value?["MAPPING"]}  {kv.Value?["SCHEMA"]?["$ref"]}"))) +
                       (hidden > 0 ? $"\n({hidden} server/system symbols not shown)" : "");
            }
            case "map":
            case "unmap":
            {
                if (!File.Exists(p.ServerConfig)) throw new Exception("Server configuration not found: " + p.ServerConfig);
                var list = (Str(a, "symbols") ?? throw new Exception("symbols is required, e.g. MAIN.bStart,MAIN.nSpeed:INT"))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var cfg = ReadJson(p.ServerConfig);
                var syms = cfg["SYMBOLS"] as JsonObject ?? new JsonObject();
                cfg["SYMBOLS"] = syms;
                var o = new StringBuilder();
                foreach (var item in list)
                {
                    var parts = item.Split(':', 2);
                    var plcName = parts[0].Trim();
                    if (plcName.StartsWith(runtime + ".", StringComparison.OrdinalIgnoreCase)) plcName = plcName[(runtime.Length + 1)..];
                    var hmiName = $"{runtime}.{plcName}";
                    if (action == "unmap")
                    {
                        o.AppendLine(syms.Remove(hmiName) ? $"  removed {hmiName}" : $"  {hmiName}: not mapped");
                        continue;
                    }
                    var type = parts.Length > 1 ? parts[1] : Str(a, "type") ?? AdsTypeOf(p, runtime, plcName);
                    if (string.IsNullOrEmpty(type)) { o.AppendLine($"  {hmiName}: type unknown - give it as {plcName}:<TYPE> (PLC not reachable via ADS)"); continue; }
                    syms[hmiName] = new JsonObject
                    {
                        ["ACCESS"] = 3, ["CUSTOMERDATA"] = "", ["DOMAIN"] = "ADS", ["DYNAMIC"] = true, ["HIDDEN"] = false,
                        ["MAPPING"] = $"{runtime}::{plcName.Replace(".", "::")}", ["OPTIONS"] = new JsonObject(),
                        ["SCHEMA"] = new JsonObject { ["$ref"] = SchemaRef(type) }, ["SUBSYMBOL_MAPPING"] = "", ["USEMAPPING"] = true
                    };
                    o.AppendLine($"  {hmiName} ({type}) -> binding %s%{hmiName}%/s%");
                }
                if (DryRun(a)) return "PROPOSAL (nothing written):\n" + o;
                WriteJson(p.ServerConfig, cfg);
                return (action == "map" ? "Mapped in TcHmiSrv.Config.default.json:\n" : "Unmapped:\n") + o +
                       "Structures/FBs: map single members, or use {attribute 'TcHmiSymbol.AddSymbol'} + 'Map Automap Symbols' in Visual Studio.";
            }
            default: throw new Exception("action: list, map, unmap, runtimes or add_runtime");
        }
    }

    /// <summary>IEC type of a PLC variable via ADS (runtime NETID/PORT from the HMI's ADS configuration); null if not reachable.</summary>
    static string AdsTypeOf(HmiProject p, string runtime, string plcName)
    {
        try
        {
            string net = "127.0.0.1.1.1"; int port = 851;
            if (File.Exists(p.AdsConfig) && ReadJson(p.AdsConfig)["RUNTIMES"]?[runtime] is JsonObject rt)
            {
                net = rt["NETID"]?.ToString() ?? net;
                if (int.TryParse(rt["PORT"]?.ToString(), out var pp)) port = pp;
            }
            using var c = new AdsClient();
            c.Connect(AmsNetId.Parse(net), port);
            var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
            return loader.Symbols.TryGetInstance(plcName, out var s) ? s.TypeName : null;
        }
        catch { return null; }
    }
}
