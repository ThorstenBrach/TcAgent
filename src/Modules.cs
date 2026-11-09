// Modules: the tools of TcAgent in groups that can be switched on and off (TcAgent.exe modules, modules.json).
//
// A switched-off module is not in the tool list of the AI client (a shorter list: fewer tokens, a better choice) and a call
// of one of its tools is refused with the name of the module. The command line stays complete (pipelines).
//
// modules.json on the levels of the knowledge (KnowledgeLayers.cs) - company (TCAGENT_COMPANY), team (TCAGENT_TEAM), user
// (%LOCALAPPDATA%\TcAgent), project (<solution>\.tcagent); the later level wins unless an earlier company or team level
// locked the module:
//   { "off": ["hmi", "scope"], "on": ["motion"], "locked": ["hmi"] }
// "locked" counts on the company and team levels only. The company level also decides about plugins (Plugins.cs):
//   { "plugins": "all" | "none" | "listed", "allowedPlugins": ["<sha256 of the plugin>", ...] }
// Plugins are modules too ("plugin:<id>").
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Modules
{
    public sealed record Def(string Id, string Name, string Description, bool Core = false, string DependsOn = null, string Requires = null);

    /// <summary>The built-in modules in the order they are shown.</summary>
    public static readonly IReadOnlyList<Def> All = new[]
    {
        new Def("core", "Core", "Connection to the IDE, project tree, reading and writing PLC code, build, coding rules, policy, feedback", Core: true),
        new Def("plc-code", "PLC code", "Plain-text ST, refactoring, editor navigation, SFC, PLCopen export/import, library signatures"),
        new Def("test", "Tests", "TcUnit runs and results, test skeletons, static analysis", DependsOn: "runtime"),
        new Def("runtime", "Runtime and ADS", "Activate, PLC login/start/stop, ADS read/write, runtime backup, run mode, boot project"),
        new Def("io", "I/O", "I/O devices, scan, variable links, mapping export/import"),
        new Def("motion", "Motion", "NC axes and their links", Requires: "TF5000 NC PTP for a run"),
        new Def("hmi", "HMI", "TwinCAT HMI pages, controls and symbols", Requires: "TE2000 HMI engineering"),
        new Def("scope", "Scope", "Scope View configurations and recordings", Requires: "TE1300 Scope View"),
        new Def("safety", "TwinSAFE (read only)", "Reads the TwinSAFE configuration - never changes it"),
        new Def("docs", "Documentation and risks", "Project documentation, user manual, static risk analysis and its rules"),
        new Def("knowledge", "Knowledge", "Documents, knowledge levels, skills, workflows, lessons learned, improvement proposals, sources"),
        new Def("packages", "Packages and libraries", "TwinCAT packages, libraries, library lock file and SBOM"),
        new Def("system", "System and project", "New solutions and PLC projects, tasks, licenses, real time, CoE, routes, TcCOM, variants, archive"),
    };

    /// <summary>Tool → module. Every tool of ToolRegistry is in here (ModulesTests checks it).</summary>
    static readonly Dictionary<string, string> ToolModule = Build(new()
    {
        ["core"] = "tc_instances tc_attach tc_open tc_status tc_version tc_tree tc_xml_get tc_xml_set tc_delete tc_plc_read tc_plc_write tc_plc_create " +
                   "tc_plc_search tc_plc_references tc_save tc_check tc_build tc_errors tc_dialogs tc_coding_rules tc_coding_rules_add tc_feedback tc_policy " +
                   "tc_ask_user tc_ide tc_editor_context",
        ["plc-code"] = "tc_plaintext tc_refactor tc_editor_open tc_goto_definition tc_find_references tc_sfc tc_lib_signatures tc_export tc_import tc_condition",
        ["test"] = "tc_test_run tc_test_results tc_test_skeleton tc_static_analysis",
        ["runtime"] = "tc_activate tc_plc_online tc_plc_state ads_read ads_write ads_symbols tc_backup tc_runtime_mode tc_runtime_startup tc_boot_project",
        ["io"] = "tc_io_add tc_io_scan tc_io_scan_add tc_io_vars tc_link tc_unlink tc_mapping_export tc_mapping_import",
        ["motion"] = "tc_motion tc_motion_add",
        ["hmi"] = "tc_hmi tc_hmi_page tc_hmi_control tc_hmi_symbol",
        ["scope"] = "tc_scope_new tc_scope",
        ["safety"] = "tc_safety",
        ["docs"] = "tc_doc tc_userdoc tc_risk_rules",
        ["knowledge"] = "tc_docs tc_docs_add tc_knowledge tc_skills tc_workflow tc_lessons tc_improve tc_sources tc_advisor",
        ["packages"] = "tc_packages tc_packages_change tc_library tc_library_save tc_plc_library_add tc_sbom tc_library_lock",
        ["system"] = "tc_new_solution tc_plc_project_add tc_task tc_licenses tc_realtime tc_coe tc_twincat_version tc_platform tc_archive tc_variant " +
                     "tc_route_search tc_route_add tc_tccom",
    });

    static Dictionary<string, string> Build(Dictionary<string, string> byModule)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (m, tools) in byModule)
            foreach (var t in tools.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d.Add(t, m);
        return d;
    }

    /// <summary>The module of a tool: built-in, "plugin:&lt;id&gt;" for a plugin tool, null if unknown.</summary>
    public static string Of(string tool) =>
        ToolModule.TryGetValue(tool, out var m) ? m : Plugins.IdOfTool(tool) is { } p ? "plugin:" + p : null;

    public static IEnumerable<string> ToolsOf(string module) => ToolModule.Where(kv => kv.Value == module).Select(kv => kv.Key);
    public static IEnumerable<string> KnownTools => ToolModule.Keys;

    // ------------------------------------------------------------------ state
    public sealed class State
    {
        public string Id, Name, Description, Requires, DependsOn, By, Reason;
        public bool Core, On = true, Locked, Plugin;
        public int Tools;
    }

    /// <summary>One level with its modules.json.</summary>
    public sealed record Level(string Name, string Root, bool MayLock)
    {
        public string File => Root == null ? null : Path.Combine(Root, "modules.json");
        public string Label => Name;
    }

    public static List<Level> Levels(string solutionDir)
    {
        var list = new List<Level> { new("company", KnowledgeLayers.CompanyRoot, true) };
        foreach (var t in KnowledgeLayers.TeamRoots) list.Add(new Level("team", t, true));
        list.Add(new Level("user", KnowledgeLayers.UserRoot, false));
        list.Add(new Level("project", KnowledgeLayers.ProjectRoot(solutionDir), false));
        return list.Where(l => l.Root != null).ToList();
    }

    public static JsonObject Read(Level l)
    {
        try { return l.File != null && File.Exists(l.File) ? JsonNode.Parse(File.ReadAllText(l.File)) as JsonObject : null; }
        catch (Exception ex) { Log.Error($"{l.File}: {ex.Message}"); return null; }
    }

    static IEnumerable<string> Names(JsonObject o, string key) =>
        o?[key] is JsonArray a ? a.Select(x => x?.ToString()?.Trim()).Where(x => !string.IsNullOrEmpty(x))! : Enumerable.Empty<string>();

    /// <summary>The state of every module (built-in and plugins found) for the solution (null = no project level).</summary>
    public static List<State> Effective(string solutionDir, IEnumerable<(string Id, string Name, string Description, int Tools)> plugins = null)
    {
        var states = All.Select(d => new State
        {
            Id = d.Id, Name = d.Name, Description = d.Description, Requires = d.Requires, DependsOn = d.DependsOn, Core = d.Core, Tools = ToolsOf(d.Id).Count()
        }).ToList();
        foreach (var p in plugins ?? Enumerable.Empty<(string, string, string, int)>())
            states.Add(new State { Id = "plugin:" + p.Id, Name = p.Name, Description = p.Description, Plugin = true, Tools = p.Tools });
        var byId = states.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var lockedBy = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in Levels(solutionDir))
        {
            var o = Read(l);
            if (o == null) continue;
            void Set(string id, bool on)
            {
                if (!byId.TryGetValue(id, out var s)) return;          // a module that is not there (yet): no error, a plugin may come later
                if (s.Core) { if (!on) s.Reason = $"core - cannot be switched off ({l.Label} modules.json)"; return; }
                if (lockedBy.TryGetValue(id, out var lk) && lk != l) { s.Reason = $"locked by {lk.Label} - {l.Label} cannot change it"; return; }
                s.On = on; s.By = l.Label; s.Reason = null;
            }
            foreach (var id in Names(o, "on")) Set(id, true);
            foreach (var id in Names(o, "off")) Set(id, false);
            if (l.MayLock)
                foreach (var id in Names(o, "locked"))
                    if (byId.TryGetValue(id, out var s) && !s.Core && !lockedBy.ContainsKey(id)) { lockedBy[id] = l; s.Locked = true; }
        }
        // a module needs the one it depends on
        foreach (var s in states.Where(s => s.On && s.DependsOn != null))
            if (byId.TryGetValue(s.DependsOn, out var dep) && !dep.On) { s.On = false; s.Reason = $"needs the module {dep.Id} (switched off)"; }
        return states;
    }

    /// <summary>The modules that are switched off (ids) - for the tool list and the refusal of a call.</summary>
    public static HashSet<string> Off(List<State> states) =>
        states.Where(s => !s.On).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why a tool cannot be called, or null if its module is on.</summary>
    public static string Refusal(string tool, List<State> states)
    {
        var m = Of(tool);
        var s = m == null ? null : states.FirstOrDefault(x => x.Id.Equals(m, StringComparison.OrdinalIgnoreCase));
        if (s == null || s.On) return null;
        return $"{tool} is part of the module '{s.Id}' ({s.Name}), which is switched off" +
               (s.Reason != null ? $" ({s.Reason})" : s.By != null ? $" in the modules.json of the {s.By} level" : "") +
               $". Tell the user; they can switch it on with TcAgent.exe modules enable {s.Id}" + (s.Locked ? " (locked - only the level that locked it can)" : "") + ".";
    }

    /// <summary>Switches a module on or off on the user or project level. Returns the file.</summary>
    public static string Set(string solutionDir, string level, string id, bool on)
    {
        var l = Levels(solutionDir).FirstOrDefault(x => x.Name.Equals(level ?? "user", StringComparison.OrdinalIgnoreCase) && !x.MayLock)
                ?? throw new Exception(level == "project" ? "No solution - the project level is <solution>\\.tcagent\\modules.json (pass --solution)"
                                                          : "level: user or project (company and team files are changed by their owners)");
        var def = All.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (def == null && !id.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Unknown module '{id}'. Modules: {string.Join(", ", All.Select(d => d.Id))}, plugin:<id>");
        if (def?.Core == true && !on) throw new Exception("core cannot be switched off");
        id = def?.Id ?? id;
        var o = Read(l) ?? new JsonObject();
        var onList = Names(o, "on").Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        var offList = Names(o, "off").Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        (on ? onList : offList).Add(id);
        o["on"] = new JsonArray(onList.Select(x => (JsonNode)x).ToArray());
        o["off"] = new JsonArray(offList.Select(x => (JsonNode)x).ToArray());
        if (onList.Count == 0) o.Remove("on");
        if (offList.Count == 0) o.Remove("off");
        Directory.CreateDirectory(l.Root);
        File.WriteAllText(l.File, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return l.File;
    }

    // ------------------------------------------------------------------ command line
    // TcAgent.exe modules [list] [--solution <sln>] [--json]
    // TcAgent.exe modules enable|disable <id> [--level user|project] [--solution <sln>]
    public static int RunCli(string[] args)
    {
        string sub = "list", id = null, level = null, sln = null; bool json = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                string Val() => i + 1 < args.Length ? args[++i] : throw new Exception(a + " needs a value");
                switch (a.ToLowerInvariant())
                {
                    case "--solution": case "-s": sln = Val(); break;
                    case "--level": level = Val().Trim().ToLowerInvariant(); break;
                    case "--json": json = true; break;
                    default:
                        if (a.StartsWith("--")) throw new Exception("unknown option " + a);
                        if (i == 1 && a.ToLowerInvariant() is "list" or "enable" or "disable") sub = a.ToLowerInvariant();
                        else if (id == null) id = a.Trim();
                        else throw new Exception("too many arguments: " + a);
                        break;
                }
            }
            if (sln != null && !File.Exists(sln)) throw new Exception("Solution not found: " + sln);
            var dir = sln != null ? Path.GetDirectoryName(Path.GetFullPath(sln)) : null;
            if (sub is "enable" or "disable")
            {
                if (id == null) throw new Exception($"modules {sub} needs the module, e.g. TcAgent.exe modules {sub} hmi");
                if (level == "project" && dir == null) throw new Exception("--level project needs --solution");
                var f = Set(dir, level ?? "user", id, sub == "enable");
                var st = Effective(dir, Plugins.Found(dir).Where(p => p.Ready).Select(p => (p.Id, p.Name, p.Description, p.Tools.Count))).FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"{(sub == "enable" ? "Switched on" : "Switched off")}: {id} ({f})" +
                                  (st != null && st.On != (sub == "enable") ? $" - still {(st.On ? "on" : "off")}: {st.Reason ?? "a later level decides (" + st.By + ")"}" : "") +
                                  ". Running AI clients see it after a restart of TcAgent.");
                return Cli.Ok;
            }
            var states = Effective(dir, Plugins.Found(dir).Where(p => p.Ready).Select(p => (p.Id, p.Name, p.Description, p.Tools.Count)));
            if (json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["modules"] = new JsonArray(states.Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = s.Id, ["name"] = s.Name, ["description"] = s.Description, ["on"] = s.On, ["core"] = s.Core, ["locked"] = s.Locked,
                        ["plugin"] = s.Plugin, ["tools"] = s.Tools, ["by"] = s.By, ["reason"] = s.Reason, ["requires"] = s.Requires, ["dependsOn"] = s.DependsOn,
                        ["toolNames"] = new JsonArray(ToolsOf(s.Id).Order().Select(t => (JsonNode)t).ToArray())
                    }).ToArray()),
                    // every level with what its modules.json says (the window shows it per module)
                    ["levels"] = new JsonArray(Levels(dir).Select(l =>
                    {
                        var o = Read(l);
                        JsonArray Ids(string k) => new(Names(o, k).Select(x => (JsonNode)x).ToArray());
                        return (JsonNode)new JsonObject
                        {
                            ["level"] = l.Name, ["file"] = l.File, ["exists"] = File.Exists(l.File), ["mayLock"] = l.MayLock,
                            ["on"] = Ids("on"), ["off"] = Ids("off"), ["locked"] = Ids("locked")
                        };
                    }).ToArray())
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return Cli.Ok;
            }
            foreach (var s in states)
                Console.WriteLine($"{(s.On ? "[x]" : "[ ]")} {s.Id,-22} {s.Tools,3} tools  {s.Name}" + (s.Core ? " (core)" : "") + (s.Locked ? " (locked)" : "") +
                                  (s.Reason != null ? " - " + s.Reason : s.By != null ? $" - set by {s.By}" : "") + (s.Requires != null ? $" - needs {s.Requires}" : ""));
            Console.WriteLine("Files: " + string.Join(", ", Levels(dir).Select(l => $"{l.Name} {l.File}{(File.Exists(l.File) ? "" : " (none)")}")));
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Cli.Usage; }
    }
}
