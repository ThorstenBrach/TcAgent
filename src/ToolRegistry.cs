// Tools offered to the AI via MCP.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TCatSysManagerLib;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, string> Handler);

    readonly List<Tool> tools = new();
    readonly TwinCatSession tc;
    readonly Policy policy = new();

    // TwinCAT subtypes for CreateChild (PLC)
    static readonly Dictionary<string, int> PlcKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = 601, ["program"] = 602, ["function"] = 603, ["function_block"] = 604,
        ["dut_enum"] = 605, ["dut_struct"] = 606, ["dut_union"] = 607, ["action"] = 608,
        ["method"] = 609, ["interface_method"] = 610, ["property"] = 611, ["interface_property"] = 612,
        ["property_get"] = 613, ["property_set"] = 614,
        ["gvl"] = 615, ["transition"] = 616, ["interface"] = 618, ["dut_alias"] = 623,
    };
    static readonly Dictionary<string, int> Languages = new(StringComparer.OrdinalIgnoreCase)
    { ["ST"] = 1, ["IL"] = 2, ["SFC"] = 3, ["FBD"] = 4, ["CFC"] = 5, ["LD"] = 6 };

    public ToolRegistry(TwinCatSession session)
    {
        tc = session;

        // ---------------------------------------------------------- Connection
        Add("tc_instances", "Lists running Visual Studio 2022 / TcXaeShell instances with an open solution.",
            Schema(), a => tc.Sta.Run(() => string.Join("\n",
                tc.ListInstances().Select(i => $"pid={i.Pid}\t{i.Moniker}\t{i.Solution}")) is var s && s.Length > 0 ? s : "no instance"));

        Add("tc_attach", "Connects to a running IDE instance (also happens automatically on the first tool call if the choice is unambiguous). Select by pid or part of the solution path. Without arguments: the IDE TcAgent was started from (TcAgent chat), otherwise TCAGENT_SOLUTION, otherwise the only instance with a solution. If several are open, the client shows a selection list (if supported); otherwise a list is returned - then ask the user and call again with pid.",
            Schema(P("solution", "string", "Part of the solution path for selection"), P("pid", "integer", "Process ID of the IDE")),
            a => tc.Sta.Run(() => tc.Attach(Str(a, "solution"), Int(a, "pid", 0))));

        Add("tc_open", "Starts Visual Studio 2022 (or TcXaeShell) and opens a solution.",
            Schema(Req(P("solutionPath", "string", "Full path to the .sln")),
                   P("visible", "boolean", "Start the IDE visible (default true)"),
                   P("progId", "string", "e.g. VisualStudio.DTE.17.0 or TcXaeShell.DTE.17.0")),
            a => tc.Sta.Run(() => tc.Open(Str(a, "solutionPath"), Bool(a, "visible", true), Str(a, "progId"))));

        Add("tc_status", "Shows the current connection, solution and target NetId.",
            Schema(), a => tc.Sta.Run(() =>
            {
                var s = tc.InstanceInfo + $"\nMode: {policy.Mode}";
                if (tc.SysMan != null) try { s += "\nTarget NetId: " + (string)((dynamic)tc.SysMan).GetTargetNetId(); } catch { }
                return s;
            }));

        Add("tc_coding_rules", "Returns this project's coding guidelines (naming conventions, comments, structure ...). Call BEFORE first " +
                               "writing or creating code and align all changes with them.",
            Schema(), a =>
            {
                var dir = SolutionDir();
                rulesSeen = true;
                // the workflow and skill catalogs come with the rules: name + description only, the agent loads one when the task fits
                var active = KnowledgeLayers.Platforms(dir).Active;
                var tail = string.Concat(new[] { Workflows.Catalog(Workflows.Entries(dir, active)), Skills.Catalog(Skills.Entries(dir, active)) }
                                             .Where(c => c != null).Select(c => "\n\n" + c));
                return CodingRules.Text(dir) is { } t
                    ? "Coding guidelines (later sections take precedence in case of conflict):\n\n" + t + tail
                    : "No coding guidelines defined - use common TwinCAT conventions and the style of the existing code.\n" + CodingRules.Where(dir) + tail;
            });

        Add("tc_coding_rules_add", "Adds a rule to the coding guidelines - when the user says 'remember ...', 'from now on always ...', " +
                                   "'this is how I want it'. Phrase the rule itself concisely, unambiguously and generally (one sentence, with an example " +
                                   "if needed), tell the user and add it. Applies immediately and to all future sessions/agents. Before adding: compare it " +
                                   "with the current rules (tc_coding_rules) - if it contradicts a platform or company rule, tell the user which one and ask " +
                                   "with tc_ask_user whether they want it anyway; only then add it with scope=personal and force=true (a locked rule cannot be overridden).",
            Schema(Req(P("rule", "string", "the rule as one sentence, e.g. 'Internal variables start with _ (e.g. _nState).'")),
                   P("scope", "string", "project (default: Solution\\.tcagent\\coding-rules\\, in Git for the team), personal (this user only) or platform:<name> (this user's rules for that platform)"),
                   P("section", "string", "Topic = file name, e.g. Names, Structure, Formatting (default: Additions)"),
                   P("force", "boolean", "personal: keep this rule even against a platform rule (only after the user confirmed it)"),
                   P("dryRun", "boolean", "Only show what would be added")),
            a =>
            {
                var (file, res) = CodingRules.Add(SolutionDir(), Str(a, "scope"), Str(a, "rule"), Str(a, "section"), DryRun(a), Bool(a, "force", false));
                rulesSeen = true;
                return $"{res}\nFile: {file}";
            });

        Add("tc_risk_rules", "Risk rules for the static risk analysis (tc_doc action=risks, HTML page, TcAgent.exe doc --risks) - Markdown files " +
                             "like the coding rules (company TCAGENT_RISK_RULES, personal %LOCALAPPDATA%\\TcAgent\\risk-rules.md, project " +
                             "<solution>\\.tcagent\\risk-rules.md, later wins). action=show (default): files, built-in rules with their settings, own rules, " +
                             "agent checks and errors. action=test: run an own rule (find/require ...) on the project WITHOUT saving - always test " +
                             "before add. action=add: save an own text rule (## Rule <id>). action=builtin: switch a built-in rule off, change its " +
                             "severity or thresholds (rule + setting, e.g. 'off', 'severity=high', 'limit=200'). action=check: save a check the agent " +
                             "verifies by reading the code (for things a regex cannot decide). Only add/change rules when the user asks; dryRun first.",
            Schema(P("action", "string", "show (default), test, add, builtin or check"),
                   P("id", "string", "add/test: rule id, letters/digits/-, e.g. no-memcpy-without-sizeof"),
                   P("find", "string", "add/test: regular expression - every match is a finding (one per line), e.g. \\bMEMCPY\\s*\\("),
                   P("require", "string", "add/test: regular expression that MUST occur in each selected object - finding if missing (instead of find)"),
                   P("unless", "string", "add/test: regular expression - no finding if the statement of the match (its line up to the next ';') also matches, e.g. SIZEOF"),
                   P("unlessInObject", "string", "add/test: regular expression - object skipped if it matches anywhere in it"),
                   P("in", "string", "add/test: impl (default), decl or both"),
                   P("kinds", "string", "add/test: object kinds, e.g. 'FB, METHOD' (PROGRAM, FUNCTION_BLOCK/FB, FUNCTION, METHOD, PROPERTY, ACTION, TRANSITION, GVL, DUT, INTERFACE)"),
                   P("objects", "string", "add/test: object names with * and ?, e.g. 'FB_Axis*, MAIN'"),
                   P("comments", "string", "add/test: keep = match also in comments and strings (default: they are blanked, e.g. for a header comment check)"),
                   P("severity", "string", "add/test: high, medium (default) or low"),
                   P("message", "string", "add/test: text of the finding - what is wrong and what to do"),
                   P("why", "string", "add: background / reason (optional)"),
                   P("rule", "string", "builtin: id of a built-in rule, e.g. long-code, case-else"),
                   P("setting", "string", "builtin: off | on | severity=high|medium|low | parameter=number (long-code: limit, memberLimit; deep-nesting: limit, mediumLimit; magic-number: labels), comma-separated"),
                   P("text", "string", "check: the check as one sentence, e.g. 'Every MC_ function block instance: the Error output is evaluated.'"),
                   P("scope", "string", "add/builtin/check: project (default, <solution>\\.tcagent\\risk-rules.md in Git) or personal"),
                   P("max", "integer", "test: max. findings shown (default 50)"),
                   P("plcProjectPath", "string", "test: PLC project (empty = the only one)"),
                   P("dryRun", "boolean", "add/builtin/check: only show the entry")),
            a => RiskRulesTool(a));

        Add("tc_docs", "Look up documentation - read-only: Beckhoff InfoSys, other approved URLs, documents in the project folder " +
                       ".tcagent\\knowledge or %LOCALAPPDATA%\\TcAgent\\knowledge and registered folders/network drives (PDF, Word, HTML, Markdown, text). " +
                       "action=search (query) searches local documents and pages already read; action=read (url or path) returns the text " +
                       "(long texts page by page via offset); action=sources shows the sources. For questions about terminals, parameters, error codes, " +
                       "TwinCAT functions, look it up here first instead of guessing, and cite the source.",
            Schema(P("action", "string", "search (default), read or sources"),
                   P("query", "string", "search: search terms, e.g. 'EL6001 baud rate'"),
                   P("url", "string", "read: URL (InfoSys or approved domain) or file path in an approved source"),
                   P("offset", "integer", "read: start at character (for long documents)"), P("max", "integer", "read: max. characters (default 12000)")),
            a =>
            {
                var dir = SolutionDir();
                return (Str(a, "action") ?? (Str(a, "url") != null ? "read" : "search")).ToLowerInvariant() switch
                {
                    "sources" => Knowledge.Describe(dir),
                    "read" => Knowledge.Read(dir, Str(a, "url") ?? Str(a, "path"), Int(a, "offset", 0), Math.Clamp(Int(a, "max", 12000), 1000, 60000)),
                    _ => Knowledge.Search(dir, Str(a, "query"))
                };
            });

        Add("tc_docs_add", "Registers a knowledge source (URL or domain, folder or network drive) - only at the user's request. " +
                           "TcAgent only reads sources, never writes to them.",
            Schema(Req(P("source", "string", "e.g. https://infosys.beckhoff.com/content/1031/tf6340_tc3_serial_communication/ or \\\\server\\Manuals")),
                   P("scope", "string", "project (default, .tcagent\\knowledge.md in the solution) or personal"),
                   P("note", "string", "short comment on what the source is good for")),
            a =>
            {
                var (file, res) = Knowledge.Add(SolutionDir(), Str(a, "scope"), Str(a, "source"), Str(a, "note"));
                return $"{res}\nFile: {file}";
            });

        Add("tc_version", "Shows the version of the TcAgent server.", Schema(), a => $"TcAgent {Program.ServerVersion}");

        // ---------------------------------------------------------- Tree / XML
        Add("tc_tree", "Shows the TwinCAT project tree (name, type, path). Without path: all roots (TIPC=PLC, TIID=I/O, TIRT=tasks, TISC=TwinSAFE if present ...).",
            Schema(P("path", "string", "Start path, e.g. TIPC or TIID^Device 1 (EtherCAT)"), P("depth", "integer", "Depth (default 2)")),
            a => tc.Sta.Run(() => tc.Tree(Str(a, "path"), Int(a, "depth", 2))));

        Add("tc_xml_get", "Reads the XML description of a tree item (ProduceXml): parameters, I/O configuration, task settings, online settings ...",
            Schema(Req(P("path", "string", "Tree path")), P("recursive", "boolean", "Include children")),
            a => tc.Sta.Run(() => (string)((dynamic)tc.Item(Str(a, "path"))).ProduceXml(Bool(a, "recursive", false))));

        Add("tc_xml_set", "Writes XML into a tree item (ConsumeXml) - changes configuration. Read the format with tc_xml_get first.",
            Schema(Req(P("path", "string", "Tree path")), Req(P("xml", "string", "XML in the format of ProduceXml (only the parts to change)"))),
            a => tc.Sta.Run(() => { ((dynamic)tc.Item(Str(a, "path"))).ConsumeXml(Str(a, "xml")); return "OK"; }));

        Add("tc_delete", "Deletes a child item in the tree. A backup is written first (PLC objects as PLCopen XML, restorable with " +
                         "tc_import kind=plcopen). Never delete PLC objects only because they look empty (graphical code, embedded SFC actions).",
            Schema(Req(P("parentPath", "string", "Path of the parent item")), Req(P("name", "string", "Name of the child"))),
            a => tc.Sta.Run(() =>
            {
                var backup = BackupBeforeDelete(Str(a, "parentPath"), Str(a, "name"));
                ((dynamic)tc.Item(Str(a, "parentPath"))).DeleteChild(Str(a, "name"));
                return "deleted" + (backup != null ? $" - backup: {backup}" : " (no backup possible)");
            }));

        // ---------------------------------------------------------- Create I/O devices
        Add("tc_io_add", "Creates I/O configuration: devices under TIID (e.g. EtherCAT master), below them boxes/couplers/terminals by " +
                         "product name (EK1100, EL6001, EL1008 ...). With 'children' a whole station is created in one call, " +
                         "e.g. kind=box product=EK1100 children=EL1008,EL2008,EL6001. Then parameters with tc_xml_get/tc_xml_set, " +
                         "process data with tc_link. Without connected hardware it is configured offline (check later via scan).",
            Schema(Req(P("kind", "string", "ethercat_master (device under TIID), box (EtherCAT coupler/terminal/box, needs product) or raw (specify subType)")),
                   P("parentPath", "string", "Parent item: TIID for devices (default), e.g. 'TIID^Device 1 (EtherCAT)' for boxes, '...^Term 1 (EK1100)' for terminals"),
                   P("product", "string", "box: product name, e.g. EK1100 or with revision EL6001-0000-0020"),
                   P("children", "string", "box: further terminals below the new item, comma-separated in slot order, e.g. EL1008,EL2008,EL6001"),
                   P("name", "string", "Name in the tree (empty = usual TwinCAT naming, e.g. 'Term 1 (EK1100)')"),
                   P("subType", "integer", "raw: TwinCAT subtype for CreateChild"),
                   P("dryRun", "boolean", "Only show what would be created")),
            a => tc.Sta.Run(() => IoAdd(a)));

        RegisterIoScan();
        RegisterMotion();
        RegisterAutomation();
        RegisterScope();
        RegisterHmi();
        RegisterPackages();
        RegisterSystemConfig();
        RegisterSfc();
        RegisterDoc();
        RegisterLessons();
        RegisterUserDoc();
        RegisterRefactor();
        RegisterSafety();
        RegisterFeedback();
        RegisterPolicy();
        RegisterEditorNav();
        RegisterLibraryInventory();
        RegisterPlainText();
        RegisterAskUser();
        RegisterKnowledge();
        RegisterSkills();
        RegisterWorkflows();

        // ---------------------------------------------------------- I/O links
        Add("tc_link", "Links variables (PLC variable with I/O channel). Individually with source/target or many at once with pairs. " +
                       "Paths as in the tree, e.g. 'TIPC^MyPlc^MyPlc Instance^PlcTask Inputs^MAIN.bIn' and " +
                       "'TIID^Device 1 (EtherCAT)^Term 2 (EL1008)^Channel 1^Input'. Determine paths with tc_io_vars first. " +
                       "Structures/groups in one step: if both sides have sub-items (PLC STRUCT/ARRAY variable and a PDO group such as " +
                       "'...^Term 2 (EL6001)^COM Inputs', or two structs) the pair is linked element-wise - members by equal name, the rest " +
                       "in order (arrays element by element); every single link is reported, dryRun shows the pairs first. If source " +
                       "elements remain unmatched nothing is linked for that pair. " +
                       "PLC variables only appear after a real build (tc_build, not tc_check), if they are declared with AT %I*/%Q*.",
            Schema(P("source", "string", "First variable path (variable, struct/array variable or group)"), P("target", "string", "Second variable path"),
                   P("pairs", "string", "Multiple links, one per line: '<path> => <path>'"),
                   P("elementwise", "boolean", "Expand struct/group pairs into single links (default true; false = link exactly the given items)"),
                   P("dryRun", "boolean", "Only check the paths and show every single link that would be created")),
            a => tc.Sta.Run(() => Link(a)));

        Add("tc_io_vars", "Lists linkable variables below a tree item with type - e.g. all channels of a terminal " +
                          "('TIID^Device 1 (EtherCAT)^Term 2 (EL1008)') or the process image of the PLC ('TIPC^MyPlc^MyPlc Instance'). " +
                          "Returns the exact paths for tc_link.",
            Schema(Req(P("path", "string", "Tree item, e.g. a terminal, a device or the PLC instance")),
                   P("filter", "string", "only paths containing this text"), P("max", "integer", "max. entries (default 300)")),
            a => tc.Sta.Run(() => IoVars(Str(a, "path"), Str(a, "filter"), Int(a, "max", 300))));

        Add("tc_unlink", "Removes links of a variable (optionally only to target).",
            Schema(Req(P("source", "string", "Variable path")), P("target", "string", "Counterpart (empty = all)")),
            a => tc.Sta.Run(() => { ((dynamic)tc.RequireSysMan()).UnlinkVariables(Str(a, "source"), Str(a, "target") ?? ""); return "unlinked"; }));

        // ---------------------------------------------------------- PLC code
        Add("tc_plc_read", "Reads declaration and implementation of a PLC object (POU, method, property, DUT, GVL) including sub-objects.",
            Schema(Req(P("path", "string", "e.g. TIPC^MyPlc^MyPlc Project^POUs^FB_Axis"))),
            a => tc.Sta.Run(() => PlcRead(Str(a, "path"))));

        Add("tc_plc_write", "Overwrites declaration and/or implementation of a PLC object (ST text). With dryRun=true (always in 'propose' mode) nothing is written; a diff is returned instead.",
            Schema(Req(P("path", "string", "Tree path of the object")), P("declaration", "string", "complete declaration part"),
                   P("implementation", "string", "complete implementation part"),
                   P("dryRun", "boolean", "Only show diff, change nothing")),
            a => tc.Sta.Run(() =>
            {
                var item = tc.Item(Str(a, "path"));
                return DryRun(a) ? PlcDiff(item, Str(a, "declaration"), Str(a, "implementation"))
                                 : PlcWrite(item, Str(a, "declaration"), Str(a, "implementation"));
            }));

        Add("tc_plc_create", "Creates a PLC object and optionally sets declaration/implementation. kind: " + string.Join(", ", PlcKinds.Keys),
            Schema(Req(P("parentPath", "string", "Parent folder/POU, e.g. ...^POUs or ...^POUs^FB_Axis for methods")),
                   Req(P("name", "string", "Object name")), Req(P("kind", "string", "Object type")),
                   P("language", "string", "ST (default), IL, SFC, FBD, CFC, LD"),
                   P("returnType", "string", "Return type for function/method/property"),
                   P("declaration", "string", "complete declaration part"), P("implementation", "string", "implementation part (for property: the Get accessor)"),
                   P("implements", "string", "function_block only: interface(s), comma-separated - creates IMPLEMENTS and all methods/properties " +
                                              "of the interface (incl. EXTENDS) as stubs, like the TwinCAT dialog. Project and library interfaces."),
                   P("getImplementation", "string", "property: code of the Get accessor"), P("setImplementation", "string", "property: code of the Set accessor"),
                   P("dryRun", "boolean", "Only show what would be created")),
            a => tc.Sta.Run(() => PlcCreate(a)));

        Add("tc_plc_search", "Searches declarations and implementations of all PLC objects (full text or regex). Hits with tree path, part and line - e.g. to find usages of an FB, a variable or a call.",
            Schema(Req(P("pattern", "string", "Search text or regular expression")),
                   P("regex", "boolean", "pattern as regex (default false)"),
                   P("caseSensitive", "boolean", "Case-sensitive (default false)"),
                   P("part", "string", "decl, impl or all (default)"),
                   P("path", "string", "Only search below this tree path (default: all PLC projects)"),
                   P("max", "integer", "Max. hits (default 200)")),
            a => tc.Sta.Run(() => PlcSearch(a)));

        Add("tc_plc_references", "Lists the library references of the PLC projects with resolved version and vendor (including placeholders). With installed=true also the libraries installed in the repository.",
            Schema(P("plcProjectPath", "string", "Path of the PLC project (default: all)"),
                   P("installed", "boolean", "Also list installed libraries")),
            a => tc.Sta.Run(() => PlcReferences(Str(a, "plcProjectPath"), Bool(a, "installed", false))));

        Add("tc_editor_context", "Shows what the user is currently working on in the IDE: active window/document, selected items, tree paths derived from them (directly usable with tc_plc_read) and - with the VS extension TcAgentBridge - cursor and selection. Use when the user talks about 'here', 'this FB' or 'the method'.",
            Schema(), a =>
            {
                var (text, pid) = tc.Sta.Run(() => (tc.EditorContext(), tc.AttachedPid));
                if (!BridgeClient.Available(pid)) return text + "(VS extension TcAgentBridge not active - cursor/selection not available)\n";
                // Cursor/selection come from the bridge -> omit the DTE hint "not available" (contradictory)
                try { return string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("Cursor/selection: not available from the TwinCAT editor"))) + BridgeContext(BridgeClient.Call(pid, "context"), text); }
                catch (Exception ex) { return text + "\nTcAgentBridge: " + ex.Message + "\n"; }
            });

        Add("tc_goto_definition", "Go to definition: resolves a name the way TwinCAT itself does - in the scope of the object open in the editor. Without symbol the word at the cursor (or the selection) is used. For variables returns type, declaration location and the resolved type; for types/POUs the location in the project (tree path) or the library. Without an open editor, POUs/DUTs/GVLs and their members are looked up in the project tree and types (incl. library types) at project level. Requires the VS extension TcAgentBridge. " +
                                      "open=true also opens the definition in the IDE editor and puts the cursor on the declaration line (like F12).",
            Schema(P("symbol", "string", "Name, e.g. _startTime, MC_MoveLinearAbsoluteFB, GVL_Progress.Progress (empty = word at cursor)"),
                   P("open", "boolean", "Also open the resolved object in the IDE editor at the declaration line (like F12). Library objects cannot be opened.")),
            a => GotoDefinition(a));

        Add("tc_find_references", "Finds all usages of a symbol: cross references as in TwinCAT's cross reference list plus a whole-word text search as a cross-check.",
            Schema(Req(P("symbol", "string", "Name of the symbol (variable, FB, method, type)")), P("max", "integer", "Max. hits per source (default 300)")),
            a => FindReferences(a));

        Add("tc_ide", "Experimental: direct query of the VS extension TcAgentBridge in the IDE process. command: ping (is it running?), context (raw editor context), libs (installed libraries in the repository), probe (diagnostics: what is reachable in the IDE process; writes probe.txt).",
            Schema(Req(P("command", "string", "ping | context | libs | probe | lm_call | chat_show (open chat window in TwinCAT)")),
                   P("target", "string", "lm_call (diagnostics): query target lm (default), om, engine or view"),
                   P("method", "string", "lm_call: read-only method only (Get*/Find*/All*/Is*)"),
                   P("args", "string", "lm_call: arguments separated by |; $ph = project handle, $obj = object in the editor")),
            a =>
            {
                var cmd = Str(a, "command");
                if (cmd is not ("ping" or "context" or "libs" or "probe" or "lm_call" or "chat_show")) throw new Exception("command must be ping, context, libs, probe, lm_call or chat_show");
                var pid = tc.Sta.Run(() => { try { tc.RequireDte(); } catch { } return tc.AttachedPid; });
                JsonObject args = null;
                if (cmd == "lm_call")
                    args = new JsonObject { ["target"] = Str(a, "target") ?? "lm", ["method"] = Str(a, "method"),
                        ["args"] = new JsonArray((Str(a, "args") ?? "").Split('|', StringSplitOptions.None).Where((x, i) => !(i == 0 && x.Length == 0 && (Str(a, "args") ?? "").Length == 0)).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()) };
                var res = Pretty(BridgeClient.Call(pid, cmd, args, cmd == "probe" || cmd == "lm_call" ? 120000 : 30000));
                return res.Length > 60000 ? res[..60000] + "\n... (truncated)" : res;
            });

        Add("tc_lib_signatures", "Signatures of a library - also from third-party compiled libraries - (requires the VS extension TcAgentBridge). Without symbol: outline (one line per FB/function/type, with EXTENDS and number of inputs/outputs). With symbol: interface as ST declaration incl. inherited inputs/outputs, methods and doc comments. The result is cached per library version.",
            Schema(Req(P("library", "string", "Library title, e.g. RobotLibrary or Tc2_Standard")),
                   P("version", "string", "Version (default: the one resolved in the project, otherwise the newest installed)"),
                   P("symbol", "string", "Name of an FB, a function, a type; method as FB.Method"),
                   P("filter", "string", "Outline only for names containing this text (e.g. MC_ or FB)"),
                   P("source", "string", "lm (default: with EXTENDS, methods, original spelling) or doc (alternative export, fallback)"),
                   P("refresh", "boolean", "Regenerate the cache")),
            a => LibSignatures(a));

        Add("tc_plc_library_add", "Adds a library reference to a PLC.",
            Schema(Req(P("referencesPath", "string", "Path of the References node, e.g. TIPC^MyPlc^MyPlc Project^References")),
                   Req(P("library", "string", "Library name")), P("version", "string", "Version or * (default)"),
                   P("vendor", "string", "Vendor, e.g. Beckhoff Automation GmbH")),
            a => tc.Sta.Run(() =>
            {
                var lm = (ITcPlcLibraryManager)tc.Item(Str(a, "referencesPath"));
                lm.AddLibrary(Str(a, "library"), Str(a, "version") ?? "*", Str(a, "vendor") ?? "");
                return "Library added";
            }));

        Add("tc_dialogs", "Shows open modal dialogs of the IDE (title, text, buttons) - while one is open, all TwinCAT calls wait. " +
                          "action=close closes them like the X or Cancel (never Yes/OK/Overwrite). Normally let the user close them.",
            Schema(P("action", "string", "list (default) or close")),
            a =>
            {
                var d = IdeDialogs.Find(tc.AttachedPid);
                if (d.Count == 0) return tc.AttachedPid > 0 ? "No dialog open." : "Not connected to an IDE.";
                if ((Str(a, "action") ?? "list") != "close") return "Open dialogs:\n  " + string.Join("\n  ", d);
                foreach (var x in d) IdeDialogs.Close(x);
                return "Closed (Cancel/X):\n  " + string.Join("\n  ", d);
            });

        Add("tc_save", "Saves all modified files of the solution to disk (like Ctrl+Shift+S). Otherwise changes made by tools exist only in the IDE.",
            Schema(), a => tc.Sta.Run(() => { tc.RequireDte().ExecuteCommand("File.SaveAll"); return "all saved"; }));

        // ---------------------------------------------------------- Build / Activate / Online
        Add("tc_check", "Checks all PLC objects for compiler errors (\"Check all objects\", including unused POUs) - without a build, " +
                        "so it produces no executable code and no files. The default after code changes. Returns errors/warnings with file and line.",
            Schema(), a => tc.Sta.Run(() => tc.Check()));

        Add("tc_build", "Builds the solution (produces executable code and files) and returns compiler errors/warnings. Only needed before " +
                        "activating/downloading or when the user asks for it - to check for compiler errors use tc_check.",
            Schema(P("rebuild", "boolean", "Clean first"), P("checkAll", "boolean", "Additionally check all objects (default false)")),
            a => tc.Sta.Run(() => tc.Build(null, Bool(a, "rebuild", false), Bool(a, "checkAll", false))));

        Add("tc_errors", "Reads the IDE error list, separated into compiler/project messages and runtime/TcUnit messages of the PLC.",
            Schema(P("kind", "string", "compile, runtime or all (default)"), P("max", "integer", "Max. entries (default 500)")),
            a => tc.Sta.Run(() => tc.ErrorList(Int(a, "max", 500), Str(a, "kind") ?? "all")));

        Add("tc_test_results", "Returns the result of the last TcUnit run (suites, tests, successes, failures with test name and message) from the PLC runtime messages.",
            Schema(), a => tc.Sta.Run(() => tc.TestResults()));

        Add("tc_test_run", "TcUnit tests of the open solution (docs/TCUNIT_TESTS.md). action=list: suites, instances and tests from the code " +
                           "with description and enable bools (no runtime). action=run (default): runs the selection - build and activation " +
                           "only if the code changed, reset of the PLC, enable bools via ADS (TRUE = selected, FALSE = the rest), start, " +
                           "results per test, reset again; TwinCAT stays in Run mode. Runtime action: allowed targets only, with confirmation.",
            Schema(P("action", "string", "run (default) or list"),
                   P("tests", "string", "Comma-separated: instance (MAIN.fbMotor), suite type (FB_Motor_Test), MAIN.fbMotor@Test, test name or @file (saved test selection *.tcunit.json); empty = all"),
                   P("plc", "string", "PLC project name (default: the only one with tests, or plcProject in .tcagent\\tcunit.json)"),
                   P("target", "string", "local (default) or the AmsNetId of a test PLC allowed in TCAGENT_ALLOW_TARGETS"),
                   P("timeout", "integer", "Max. seconds for the run (default 300 or timeout in .tcagent\\tcunit.json)"),
                   P("download", "boolean", "Build and activate even if the runtime runs the current code")),
            a => TestRun(a));

        // ---------------------------------------------------------- Tasks, boot project, startup mode, licenses
        Add("tc_task", "Real-time tasks: list, create (name, cycleUs, priority, withImage), set (path, cycleUs, priority, autoStart, cores) or " +
                       "assign (name = TIRT task, program = POU called by it, default MAIN, plcProjectPath): creates the PLC task reference " +
                       "and the program call - without it a PLC has no process image and nothing can be linked. Takes effect only after tc_activate.",
            Schema(Req(P("action", "string", "list, create, set or assign")),
                   P("program", "string", "assign: program called by the task (default MAIN)"),
                   P("plcProjectPath", "string", "assign: PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = the only one)"),
                   P("name", "string", "create: task name"), P("path", "string", "set: tree path of the task, e.g. TIRT^PlcTask"),
                   P("cycleUs", "integer", "Cycle time in microseconds, e.g. 1000 = 1 ms"),
                   P("priority", "integer", "Priority (lower = higher)"), P("autoStart", "boolean", "Start task automatically"),
                   P("withImage", "boolean", "create: task with process image (default false)"),
                   P("cores", "string", "set: real-time core(s) the task runs on, e.g. '1' (see tc_realtime)"),
                   P("dryRun", "boolean", "Only show what would be changed")),
            a => tc.Sta.Run(() => Task(a)));

        Add("tc_boot_project", "PLC boot project: autostart on/off (autostart) and/or generate boot project (generate). " +
                               "Without parameters: show current state.",
            Schema(P("plcPath", "string", "PLC project, e.g. TIPC^MyPlc (empty = the only/first one)"),
                   P("autostart", "boolean", "Start the boot project automatically when the runtime starts"),
                   P("generate", "boolean", "Generate the boot project now (on the current target)"),
                   P("dryRun", "boolean", "Only show what would be changed")),
            a => tc.Sta.Run(() => BootProject(a)));

        Add("tc_runtime_startup", "Startup mode of the TwinCAT runtime after the computer boots: run or config. " +
                                  "Without mode: show current state. Currently only for the local target system (registry, requires administrator rights).",
            Schema(P("mode", "string", "run or config (empty = only show)")),
            a => RuntimeStartup(Str(a, "mode")));

        Add("tc_licenses", "Shows the license node of the solution (required/selected licenses) as XML. Change with tc_xml_set " +
                           "in the format shown. 7-day trial licenses can only be activated by the user (TwinCAT requires " +
                           "entering a security code for that) - then ask the user to do it.",
            Schema(), a => tc.Sta.Run(() => Licenses()));

        Add("tc_activate", "Optionally sets the target NetId, activates the configuration and restarts TwinCAT in run mode.",
            Schema(P("netId", "string", "AmsNetId of the target, e.g. 5.12.34.56.1.1 (empty = current target)"),
                   P("restart", "boolean", "Restart TwinCAT (default true)")),
            a => tc.Sta.Run(() =>
            {
                dynamic sm = tc.RequireSysMan();
                var net = Str(a, "netId");
                if (!string.IsNullOrEmpty(net)) sm.SetTargetNetId(net);
                sm.ActivateConfiguration();
                if (Bool(a, "restart", true)) sm.StartRestartTwinCAT();
                return "activated on " + (string)sm.GetTargetNetId();
            }));

        Add("tc_plc_online", "PLC online commands via the PLC project: login, download/online change, start, stop, logout.",
            Schema(Req(P("plcProjectPath", "string", "Path of the PLC project, e.g. TIPC^MyPlc^MyPlc Project")),
                   P("login", "boolean", ""), P("start", "boolean", ""), P("stop", "boolean", ""), P("logout", "boolean", "")),
            a =>
            {
                // Send commands individually one after another - TwinCAT does not reliably execute login and start in one XML
                var done = new List<string>();
                foreach (var (k, tag) in new[] { ("login", "LoginCmd"), ("stop", "StopCmd"), ("start", "StartCmd"), ("logout", "LogoutCmd") })
                {
                    if (!Bool(a, k, false)) continue;
                    var xml = $"<TreeItem><IECProjectDef><OnlineSettings><Commands><{tag}>true</{tag}></Commands></OnlineSettings></IECProjectDef></TreeItem>";
                    tc.Sta.Run(() => { ((dynamic)tc.Item(Str(a, "plcProjectPath"))).ConsumeXml(xml); return 0; });
                    done.Add(k);
                    Thread.Sleep(k == "login" ? 3000 : 1000);
                }
                string state;
                try { using var c = Connect(a); state = c.ReadState().AdsState.ToString(); } catch (Exception ex) { state = "unknown (" + ex.Message + ")"; }
                return $"executed: {string.Join(", ", done)}; PLC state (ADS): {state}";
            });

        Add("tc_plc_state", "Reads the runtime state of the PLC via ADS (Run, Stop, Config ...).",
            Schema(P("netId", "string", "AmsNetId (empty = local)"), P("port", "integer", "ADS port (default 851)")),
            a => { using var c = Connect(a); var s = c.ReadState(); return $"AdsState={s.AdsState} DeviceState={s.DeviceState}"; });

        // ---------------------------------------------------------- ADS (runtime)
        Add("ads_read", "Reads a variable at runtime via ADS (e.g. MAIN.nCounter).",
            Schema(Req(P("symbol", "string", "Symbol path")), P("netId", "string", "AmsNetId (empty = local)"), P("port", "integer", "ADS port (default 851)")),
            a => Ads(a, (sym) => Format(sym.ReadValue())));

        Add("ads_write", "Writes a variable at runtime via ADS.",
            Schema(Req(P("symbol", "string", "Symbol path")), Req(P("value", "string", "New value (text, converted to the variable type)")),
                   P("netId", "string", "AmsNetId (empty = local)"), P("port", "integer", "ADS port (default 851)")),
            a => Ads(a, (sym) =>
            {
                var current = sym.ReadValue();
                var text = Str(a, "value");
                object v = current is bool ? (object)(text.Trim().ToUpperInvariant() is "TRUE" or "1")
                         : Convert.ChangeType(text, current.GetType(), CultureInfo.InvariantCulture);
                sym.WriteValue(v);
                return $"{Format(current)} -> {Format(sym.ReadValue())}";
            }));

        Add("ads_symbols", "Lists runtime symbols via ADS (optionally filtered).",
            Schema(P("filter", "string", "Substring, e.g. MAIN."), P("netId", "string", "AmsNetId"), P("port", "integer", "ADS port (851)")),
            a =>
            {
                using var c = Connect(a);
                var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
                var f = Str(a, "filter") ?? "";
                var sb = new StringBuilder();
                void Walk(ISymbol s, int lvl)
                {
                    if (lvl > 3) return;
                    if (s.InstancePath.Contains(f, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"{s.InstancePath}\t{s.TypeName}");
                    foreach (ISymbol sub in s.SubSymbols) Walk(sub, lvl + 1);
                }
                foreach (ISymbol s in loader.Symbols) Walk(s, 0);
                return sb.Length > 0 ? sb.ToString() : "no symbols";
            });
    }

    // ------------------------------------------------------------------ TcUnit (TcUnitDiscovery.cs, TcUnitRun.cs)
    string TestRun(JsonObject a)
    {
        var sln = tc.Sta.Run(() => (string)tc.RequireDte().Solution.FullName);
        if ((Str(a, "action") ?? "run").Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var d = TcUnitDiscovery.Discover(sln);
            var sb = new StringBuilder();
            foreach (var p in d.Projects.Where(p => p.Suites.Count > 0))
            {
                var mode = p.RunMode switch { "sequence" => "TcUnit.RUN_IN_SEQUENCE", "parallel" => "TcUnit.RUN (parallel)", _ => "no TcUnit.RUN found" };
                sb.AppendLine($"{p.Name} ({p.TreePath}): {p.Instances.Count} suite instance(s), {p.TestCount} test(s) in the code, {mode}");
                foreach (var i in p.Instances)
                {
                    var s = i.Suite;
                    var desc = i.Description ?? s.Description;
                    sb.AppendLine($"  {i.Path} : {s.Type}{(desc != null ? " - " + desc : "")}" +
                                  (s.RunSuiteVar == null ? " [no bRunSuite: runs as a whole]" : "") + (s.Dynamic ? " [test names known only at run time]" : ""));
                    foreach (var t in s.Tests)
                        sb.AppendLine($"    {t.Name}{(t.Description != null ? " - " + t.Description : "")}{(t.EnableVar == null ? " [no bRun_" + (t.Method ?? t.Name) + "]" : "")}  ({Path.GetFileName(t.File)}:{t.Line})");
                }
                foreach (var n in p.Notes) sb.AppendLine("  note: " + n);
            }
            return sb.Length > 0 ? sb.ToString() : "No TcUnit test suites (FUNCTION_BLOCK ... EXTENDS TcUnit.FB_TestSuite) in the solution.";
        }

        var o = new TcUnitRun.Options { Solution = sln, Plc = Str(a, "plc"), Target = Str(a, "target"), Download = Bool(a, "download", false) };
        if (Str(a, "tests") is string tests) o.Tests.Add(tests);
        if (Int(a, "timeout", 0) is int sec && sec > 0) o.Timeout = TimeSpan.FromSeconds(Math.Max(10, sec));
        var steps = new List<string>();
        var run = new TcUnitRun(tc, e => { if ((string)e["event"] == "step") steps.Add((string)e["text"]); });
        var rc = run.Run(o);
        var res = new StringBuilder(run.Summary).AppendLine();
        foreach (var g in run.Results.Where(r => r.Selected || r.Status != "skipped").GroupBy(r => r.Instance))
        {
            res.AppendLine($"  {g.Key}: {g.Count(r => r.Status == "passed")} passed, {g.Count(r => r.Status == "failed")} failed");
            foreach (var r in g.Where(r => r.Status != "passed"))
                res.AppendLine($"    {r.Status.ToUpperInvariant()} {r.Name}{(r.Message != null ? ": " + r.Message.Replace("\n", " | ") : "")}");
        }
        var skippedOff = run.Results.Count(r => !r.Selected && r.Status == "skipped");
        if (skippedOff > 0) res.AppendLine($"  {skippedOff} test(s) switched off for this run");
        res.AppendLine("Steps: " + string.Join(" / ", steps));
        if (rc is Cli.Usage or Cli.Infra) throw new Exception(res.ToString());
        return res.ToString();
    }

    // ------------------------------------------------------------------ PLC helpers
    string PlcRead(string path)
    {
        var item = tc.Item(path);
        var sb = new StringBuilder($"# {item.Name}  [sub={item.ItemSubType}]\n");
        if (item is ITcPlcDeclaration d) sb.Append("## Declaration\n").Append(d.DeclarationText).Append('\n');
        if (item is ITcPlcImplementation i) sb.Append("## Implementation\n").Append(ImplText(i.ImplementationText, item.PathName)).Append('\n');
        foreach (var c in TwinCatSession.Children(item))
        {
            sb.Append($"\n### {c.Name}  [sub={c.ItemSubType}]  {c.PathName}\n");
            if (c is ITcPlcDeclaration cd) sb.Append(cd.DeclarationText).Append('\n');
            if (c is ITcPlcImplementation ci) sb.Append("--- Implementation ---\n").Append(ImplText(ci.ImplementationText, c.PathName)).Append('\n');
        }
        return sb.ToString();
    }

    static readonly Regex GraphicalBody = new(@"^\s*<(SFC|CFC|FBD|LD|UML\w*)\b", RegexOptions.Compiled);

    // graphical bodies come back as the internal editor XML (SFC: tens of thousands of characters) - SFC as chain text instead
    string ImplText(string text, string path)
    {
        var m = GraphicalBody.Match(text ?? "");
        if (!m.Success) return text;
        var lang = m.Groups[1].Value.ToUpperInvariant();
        if (lang == "SFC")
            try
            {
                var chain = Sfc(new JsonObject { ["action"] = "read", ["path"] = path });
                var cut = chain.IndexOf("\nObjects of the POU", StringComparison.Ordinal);
                return $"(SFC chart - as text; change it with tc_sfc action=write, never with tc_plc_write)\n{(cut >= 0 ? chain[..cut] : chain)}";
            }
            catch { }
        return $"({lang} body - graphical, not readable as text)";
    }

    static string PlcWrite(ITcSmTreeItem item, string decl, string impl)
    {
        var done = new List<string>();
        if (decl != null)
        {
            if (item is not ITcPlcDeclaration d) throw new Exception("Object has no declaration part");
            d.DeclarationText = decl; done.Add("declaration");
        }
        if (impl != null)
        {
            if (item is not ITcPlcImplementation i) throw new Exception("Object has no implementation part");
            string current = null; try { current = i.ImplementationText; } catch { }
            if (current != null && GraphicalBody.IsMatch(current) && !GraphicalBody.IsMatch(impl))
                throw new Exception("this object has a graphical body (SFC/CFC/FBD/LD) - its implementation cannot be written as text. " +
                                    "SFC: tc_sfc action=write; step actions and transitions are own objects (tc_plc_write on them).");
            i.ImplementationText = impl; done.Add("implementation");
        }
        return done.Count == 0 ? "nothing changed" : string.Join(" + ", done) + " written";
    }

    static string Pretty(JsonObject o) => o.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    string LibSignatures(JsonObject a)
    {
        var lib = Str(a, "library");
        var (pid, version) = tc.Sta.Run(() =>
        {
            string v = Str(a, "version");
            if (string.IsNullOrEmpty(v))
                try { v = ReferencedVersion(lib); } catch { }
            return (tc.AttachedPid, v);
        });
        var source = Str(a, "source") ?? "lm";
        if (source == "lm")
        {
            // Project model: cached compactly per library version
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "libcache");
            Directory.CreateDirectory(cacheDir);
            var apiFile = Path.Combine(cacheDir, $"{lib}_{(string.IsNullOrEmpty(version) ? "aktuell" : version)}.api.json");
            LibApi api = null; string origin;
            if (File.Exists(apiFile) && !Bool(a, "refresh", false)) { api = LibApi.Load(apiFile); origin = "from cache"; }
            else
            {
                var lr = BridgeClient.Call(pid, "lm_export", new JsonObject { ["library"] = lib }, 300000);
                if (lr["found"]?.GetValue<bool>() != true || lr["file"] == null)
                    return $"No project model context found for '{lib}' (is the library referenced in the project?). Contexts:\n" +
                           string.Join("\n", (lr["contexts"] as JsonArray ?? new JsonArray()).Select(x => "  " + x));
                var raw = lr["file"]!.ToString();
                api = LibApi.FromLm(File.ReadAllText(raw), lib);
                var libIds = api.Symbols.Select(x => x.LibraryId).Where(x => x != null).Distinct().ToList();
                if (libIds.Count > 0 && !libIds.Any(x => x.StartsWith(lib + ",", StringComparison.OrdinalIgnoreCase)))
                    return $"Wrong context picked up ({string.Join("; ", libIds)}) - please query with source=doc and report via tc_feedback.";
                api.Save(apiFile);
                try { File.Delete(raw); } catch { }
                origin = $"freshly read from the IDE ({libIds.FirstOrDefault()})";
            }
            var s0 = Str(a, "symbol");
            return $"{lib} {version} - {origin}\n" + (string.IsNullOrEmpty(s0) ? api.Outline(Str(a, "filter")) : api.Describe(s0));
        }
        var args = new JsonObject { ["library"] = lib, ["version"] = version ?? "*", ["refresh"] = Bool(a, "refresh", false) };
        var r = BridgeClient.Call(pid, "lib_export", args, 300000);
        var file = r["file"]!.ToString();
        var json = File.ReadAllText(file);
        var head = $"{r["title"]} {r["version"]} ({r["company"]}) - {(r["cached"]?.GetValue<bool>() == true ? "from cache" : "freshly exported")}, {json.Length} characters, {file}\n";
        var sym = Str(a, "symbol");
        return head + (string.IsNullOrEmpty(sym) ? LibJson.Outline(json, Str(a, "filter")) : LibJson.Find(json, sym));
    }

    // Version of a referenced library as resolved in the project (placeholders taken into account)
    string ReferencedVersion(string library)
    {
        foreach (var plc in tc.PlcProjects())
            foreach (var c in TwinCatSession.Children(plc))
            {
                if (c is not ITcPlcLibraryManager lm) continue;
                foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).References)
                {
                    var d = DescribeLib(r);   // "Placeholder X -> Name, Version (Vendor)" or "Name, Version (Vendor)"
                    var lib = d.Contains(" -> ") ? d[(d.IndexOf(" -> ") + 4)..] : d;
                    var parts = lib.Split(',', 2);
                    if (parts.Length == 2 && parts[0].Trim().Equals(library, StringComparison.OrdinalIgnoreCase))
                        return parts[1].Split('(')[0].Trim();
                }
            }
        return null;
    }

    // ------------------------------------------------------------------ Evaluate editor cursor (from the bridge)
    sealed record Cursor(string Path, string Part, int Line, int Col, string[] Code, string Selected, string SelText);

    // TwinCAT counts lines continuously across declaration + implementation -> convert to the part
    Cursor AnalyzeCursor(JsonObject b, string dteText)
    {
        string V(string k) => b[k]?.ToString();
        // Object resolved uniquely from the file path of the active document reported by the bridge
        // ("...\\FB.TcPOU@Method"); otherwise the first tree path of the DTE context
        string path = null;
        if (V("document") is { Length: > 0 } bdoc)
            try { path = tc.Sta.Run(() => tc.ResolveDocPath(bdoc)).FirstOrDefault(); } catch { }
        path ??= dteText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("TIPC^"));
        string decl = null, impl = null;
        if (path != null)
            try
            {
                (decl, impl) = tc.Sta.Run(() =>
                {
                    var it = tc.Item(path);
                    return ((it as ITcPlcDeclaration)?.DeclarationText, (it as ITcPlcImplementation)?.ImplementationText);
                });
            }
            catch { }
        string[] Lines(string t) { if (t == null) return Array.Empty<string>(); t = t.Replace("\r\n", "\n"); if (t.EndsWith("\n")) t = t[..^1]; return t.Split('\n'); }
        var dl = Lines(decl); var il = Lines(impl);
        // Preferred: cursor directly from the editor control of the active half (independent of VS focus)
        if (b["active"] is JsonObject act && int.TryParse(act["line"]?.ToString(), out var al))
        {
            var ap = act["part"]?.ToString();
            int.TryParse(act["column"]?.ToString(), out var acol);
            var st = act["selectedText"]?.ToString();
            var code = ap == "implementation" ? il : dl;
            return new Cursor(path, ap, al, Math.Max(1, acol), code,
                              string.IsNullOrEmpty(st) ? null : SelectionRange(ap, code, al, Math.Max(1, acol), st), string.IsNullOrEmpty(st) ? null : st);
        }
        int offset = dl.Length;
        var selFirst = (V("selectedText") ?? "").Replace("\r\n", "\n").Split('\n')[0].Trim();
        if (selFirst.Length > 0 && int.TryParse(V("selStartLine"), out var sl))
            foreach (var k in new[] { dl.Length, dl.Length + 1, dl.Length - 1 })   // Plausibility: the selected text must be on that line
            {
                var src = sl > k ? il : dl; var idx = sl > k ? sl - k : sl;
                if (idx >= 1 && idx <= src.Length && src[idx - 1].Contains(selFirst)) { offset = k; break; }
            }
        if (!int.TryParse(V("caretLine"), out var cl)) return new Cursor(path, null, 0, 0, null, null, V("selectedText"));
        var part = cl > offset ? "implementation" : "declaration";
        var line = cl > offset ? cl - offset : cl;
        string sel = null;
        if (int.TryParse(V("selStartLine"), out var s1) && int.TryParse(V("selEndLine"), out var s2) && (s1 != s2 || V("selStartColumn") != V("selEndColumn")))
        {
            int.TryParse(V("selStartColumn"), out var c1); int.TryParse(V("selEndColumn"), out var c2);
            if (s1 > s2 || (s1 == s2 && c1 > c2)) { (s1, s2) = (s2, s1); (c1, c2) = (c2, c1); }
            int L(int x) => x > offset ? x - offset : x;
            var p1 = s1 > offset ? "implementation" : "declaration";
            // selection up to column 1 of the following line = whole lines (the following line is not part of it)
            if (c2 <= 1 && s2 > s1)
                sel = $"{p1}, lines {L(s1)}-{L(s2 - 1)}" + (c1 <= 1 ? " (whole lines)" : $" (from column {c1})");
            else
                sel = $"{p1}, line {L(s1)}:{c1} to {L(s2)}:{c2}";
        }
        return new Cursor(path, part, line, int.TryParse(V("caretColumn"), out var cc) ? cc : 1, part == "implementation" ? il : dl, sel, V("selectedText"));
    }

    // Line range of a selection from cursor position and selected text. If the text ends with a line break
    // (selection up to column 1 of the next line) the lines count as whole lines. The cursor can be at the end
    // (selected downwards) or at the start (selected upwards) - decided by comparing with the code.
    static string SelectionRange(string part, string[] code, int caretLine, int caretCol, string selText)
    {
        var t = selText.Replace("\r\n", "\n");
        bool whole = t.EndsWith("\n");
        var sl = (whole ? t[..^1] : t).Split('\n');
        int n = sl.Length;
        bool Fits(int s) => s >= 1 && code != null && s + n - 1 <= code.Length
                            && code[s - 1].Contains(sl[0].Trim(), StringComparison.Ordinal)
                            && code[s + n - 2].Contains(sl[^1].Trim(), StringComparison.Ordinal);
        int down = (whole && caretCol <= 1 ? caretLine - 1 : caretLine) - n + 1;   // cursor at the end of the selection
        int up = caretLine;                                                        // cursor at the start
        int start = Fits(down) ? down : Fits(up) ? up : Math.Max(1, down);
        if (n == 1 && !whole) return $"{part}, line {start}";
        return $"{part}, lines {start}-{start + n - 1}" + (whole ? " (whole lines)" : "");
    }

    string BridgeContext(JsonObject b, string dteText)
    {
        var o = new StringBuilder("\n--- from the IDE (TcAgentBridge) ---\n");
        var c = AnalyzeCursor(b, dteText);
        if (c.Part == null) return o.AppendLine("Cursor not available. Raw: " + Pretty(b)).ToString();
        o.AppendLine($"Cursor: {c.Part}, line {c.Line}, column {c.Col}");
        var w = WordAt(c);
        if (w != null) o.AppendLine("Word at cursor: " + w);
        if (c.Selected != null) o.AppendLine("Selection: " + c.Selected);
        if (c.SelText != null) o.AppendLine("Selected text:\n" + c.SelText);
        if (c.Path != null && c.Code.Length > 0)
        {
            o.AppendLine($"Code around the cursor ({c.Path}, {c.Part}):");
            for (int i = Math.Max(1, c.Line - 3); i <= Math.Min(c.Code.Length, c.Line + 3); i++)
                o.AppendLine($"{(i == c.Line ? ">" : " ")}{i,5}: {c.Code[i - 1]}");
        }
        return o.ToString();
    }

    // Identifier (with dotted path, e.g. GVL_Progress.Progress) at the cursor position
    static string WordAt(Cursor c)
    {
        if (c.Code == null || c.Line < 1 || c.Line > c.Code.Length) return null;
        var t = c.Code[c.Line - 1];
        int i = Math.Clamp(c.Col - 1, 0, t.Length);
        bool W(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.';
        if (i == t.Length || !W(t[i])) { if (i > 0 && W(t[i - 1])) i--; else return null; }
        int a = i, e = i;
        while (a > 0 && W(t[a - 1])) a--;
        while (e < t.Length - 1 && W(t[e + 1])) e++;
        var word = t[a..(e + 1)].Trim('.');
        return word.Length == 0 || char.IsDigit(word[0]) ? null : word;
    }

    // Go to definition: resolve a name (or the word at the cursor) like TwinCAT does, in the scope of the open object
    string GotoDefinition(JsonObject a)
    {
        var (text, pid) = tc.Sta.Run(() => (tc.EditorContext(), tc.AttachedPid));
        var name = Str(a, "symbol");
        if (string.IsNullOrWhiteSpace(name))
        {
            var c = AnalyzeCursor(BridgeClient.Call(pid, "context"), text);
            // Selection: first identifier in it (e.g. "_startTime := TIME();" -> _startTime), otherwise word at cursor
            var m = Regex.Match(c.SelText ?? "", @"[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*");
            name = m.Success ? m.Value : WordAt(c);
            if (string.IsNullOrEmpty(name)) return "No identifier at the cursor - specify symbol or click on a name in the editor.";
        }
        JsonObject r;
        try { r = BridgeClient.Call(pid, "goto", new JsonObject { ["name"] = name }); }
        catch (Exception ex) { return GotoWithoutBridge(name, Bool(a, "open", false), ex.Message); }
        var ctx = r["context"]?.ToString();
        var o = new StringBuilder(string.IsNullOrEmpty(ctx)
            ? $"'{name}' (no object open in the editor - resolved at project level):\n"
            : $"'{name}' (resolved in context {ctx}):\n");
        static bool IsLib(JsonNode s) => s?["isLibrary"]?.ToString() is "true" or "True";
        List<string> PathsOf(JsonNode s)
        {
            var op = s?["objectPath"]?.ToString();
            return string.IsNullOrEmpty(op) || IsLib(s) ? new List<string>() : tc.Sta.Run(() => tc.ResolvePlcPathsPublic(op));
        }
        string Where(JsonNode s)
        {
            if (s == null) return "?";
            if (IsLib(s))
                return $"Library {s["library"]} -> tc_lib_signatures library={s["library"]?.ToString().Split(',')[0]} symbol={s["name"]}";
            var paths = PathsOf(s);
            return paths.Count > 0 ? "Project: " + string.Join(" | ", paths) + "  (tc_plc_read)" : "Project: " + s["objectPath"];
        }
        // Jump target for open=true: object that holds the declaration and the name whose declaration line is searched
        (JsonNode Owner, string Name, bool IsVar)? target = null;
        string treeTarget = null;    // tree path from the project-tree fallback (no editor open)
        bool found = false;
        if (r["variable"] is JsonObject v)
        {
            found = true;
            o.AppendLine($"Variable {v["name"]} : {v["type"]}  [{v["flags"]}]" + (string.IsNullOrWhiteSpace(v["doc"]?.ToString()) ? "" : $"  // {v["doc"]?.ToString().Trim()}"));
            o.AppendLine($"  declared in {v["declaredIn"]?["kind"]} {v["declaredIn"]?["name"]}: {Where(v["declaredIn"])}");
            target = (v["declaredIn"], v["name"]?.ToString(), true);
            if (r["type"] is JsonObject t)
                o.AppendLine($"  Type {t["name"]} ({t["kind"]}{(string.IsNullOrEmpty(t["extends"]?.ToString()) ? "" : " EXTENDS " + t["extends"])}): {Where(t)}");
        }
        else if (r["signatures"] is JsonArray sigs && sigs.Count > 0)
        {
            found = true;
            foreach (var s in sigs)
            {
                o.AppendLine($"{s?["kind"]} {s?["name"]}{(string.IsNullOrEmpty(s?["extends"]?.ToString()) ? "" : " EXTENDS " + s?["extends"])}: {Where(s)}");
                if (target == null && !IsLib(s)) target = (s, s?["name"]?.ToString(), false);
            }
            target ??= (sigs[0], sigs[0]?["name"]?.ToString(), false);
        }
        else if (string.IsNullOrEmpty(ctx) && GotoByTree(name) is { } byTree)
        {
            found = true;
            o.Append(byTree);    // no editor open: the language model has no scope - look the name up in the project tree
            treeTarget = tc.Sta.Run(() => tc.ResolvePlcPathsPublic(name.Split('.')[0])).FirstOrDefault();
        }
        else o.AppendLine("Not found (neither a variable in the current object nor a type/POU). For text search: tc_plc_search.");
        if (r["members"] is JsonArray mem)
            foreach (var mm in mem)
            {
                if (mm?["notFoundIn"] != null) { o.AppendLine($"  .{mm["name"]}: not found in {mm["notFoundIn"]?["name"]}"); continue; }
                o.AppendLine($"  .{mm?["name"]} : {mm?["type"]}  [{mm?["flags"]}]" + (string.IsNullOrWhiteSpace(mm?["doc"]?.ToString()) ? "" : $"  // {mm?["doc"]?.ToString().Trim()}"));
                o.AppendLine($"      declared in {mm?["declaredIn"]?["kind"]} {mm?["declaredIn"]?["name"]}: {Where(mm?["declaredIn"])}");
                target = (mm?["declaredIn"], mm?["name"]?.ToString(), true);
            }
        if (r["memberType"] is JsonObject mt)
            o.AppendLine($"  Type {mt["name"]} ({mt["kind"]}{(string.IsNullOrEmpty(mt["extends"]?.ToString()) ? "" : " EXTENDS " + mt["extends"])}): {Where(mt)}");
        // Nothing found: project objects with that name and cached library signatures (tc_lib_signatures)
        if (!found) o.Append(GotoFallbackText(name));
        if (Bool(a, "open", false))
        {
            if (target is not { } tg)
                o.AppendLine(treeTarget != null ? OpenAtDeclaration(treeTarget, name.Split('.')[0], false) : "Not opened: nothing resolved.");
            else if (IsLib(tg.Owner)) o.AppendLine($"Not opened: '{tg.Name}' is declared in library {tg.Owner?["library"]} - library code cannot be opened in the editor; use tc_lib_signatures.");
            else if (PathsOf(tg.Owner).FirstOrDefault() is not { } tp) o.AppendLine($"Not opened: no tree path found for '{tg.Owner?["objectPath"]}'.");
            else o.AppendLine(OpenAtDeclaration(tp, tg.Name, tg.IsVar));
        }
        return o.ToString();
    }

    // Fallback without an open editor: POU/DUT/GVL by name in the PLC tree, then members (GVL.x, FB.Method, a.b) via declarations
    string GotoByTree(string name) => tc.Sta.Run(() =>
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var objs = tc.PlcObjects().ToList();
        ITcSmTreeItem Find(string n) => objs.FirstOrDefault(it => { try { return string.Equals(it.Name, n, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        var cur = Find(parts[0]);
        if (cur == null) return null;
        var o = new StringBuilder($"(no editor open - resolved through the project tree)\n{parts[0]}: {cur.PathName}  (tc_plc_read)\n");
        for (int i = 1; i < parts.Length && cur != null; i++)
        {
            // child object (method/property/action) with this name?
            var child = TwinCatSession.Children(cur).FirstOrDefault(c => { try { return string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase); } catch { return false; } });
            if (child != null) { o.AppendLine($"  .{parts[i]}: {child.PathName}"); cur = child; continue; }
            string decl = null;
            try { if (cur is ITcPlcDeclaration d) decl = d.DeclarationText; } catch { }
            var lines = (decl ?? "").Replace("\r\n", "\n").Split('\n');
            var rx = new Regex($@"^\s*{Regex.Escape(parts[i])}\s*(AT\s+%\S+\s*)?:\s*([^;:=]+)", RegexOptions.IgnoreCase);
            int ln = Array.FindIndex(lines, l => rx.IsMatch(l));
            if (ln < 0) { o.AppendLine($"  .{parts[i]}: not found in the declaration of {cur.Name}"); break; }
            var type = rx.Match(lines[ln]).Groups[2].Value.Trim();
            var doc = ln > 0 && lines[ln - 1].TrimStart().StartsWith("///") ? "  // " + lines[ln - 1].Trim().TrimStart('/').Trim() : "";
            o.AppendLine($"  .{parts[i]} : {type}  (declaration line {ln + 1} of {cur.PathName}){doc}");
            var bare = Regex.Replace(type, @"^(ARRAY\s*\[.*?\]\s*OF\s+|POINTER\s+TO\s+|REFERENCE\s+TO\s+)+", "", RegexOptions.IgnoreCase).Split('(')[0].Trim();
            cur = Find(bare);
            if (cur == null) { if (i < parts.Length - 1) o.AppendLine($"  type {bare} is not a project object (library? -> tc_lib_signatures)"); break; }
            o.AppendLine($"  type {bare}: {cur.PathName}");
        }
        return o.ToString();
    });

    // Cross references: semantically from the project model (with access type), line/code via text search in the same object
    sealed record Hit(string Path, string Part, int Line, string Text);

    List<Hit> WordHits(string word, int max) => tc.Sta.Run(() =>
    {
        var rx = new Regex($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        var hits = new List<Hit>();
        foreach (var it in tc.PlcObjects())
        {
            if (hits.Count >= max) break;
            string path = null;
            try { path = it.PathName; } catch { continue; }
            void Scan(string part, string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int n = 0; n < lines.Length && hits.Count < max; n++)
                    if (rx.IsMatch(lines[n])) hits.Add(new Hit(path, part, n + 1, lines[n]));
            }
            try { if (it is ITcPlcDeclaration d) Scan("declaration", d.DeclarationText); } catch { }
            try { if (it is ITcPlcImplementation i) Scan("implementation", i.ImplementationText); } catch { }
        }
        return hits;
    });

    string FindReferences(JsonObject a)
    {
        var name = Str(a, "symbol");
        int max = Int(a, "max", 300);
        var plain = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        var hits = WordHits(plain, Math.Max(2000, max * 10));   // Text search wider than the display, so every reference gets its line
        var used = new HashSet<Hit>();
        var pid = tc.Sta.Run(() => { try { tc.RequireDte(); } catch { } return tc.AttachedPid; });
        var o = new StringBuilder();
        static string Access(string acc) => acc switch { "Declarative" => "declaration", "Read" => "read", "Write" => "write", "ReadWrite" => "read+write", "Call" => "call", _ => acc };
        try
        {
            var r = BridgeClient.Call(pid, "xref", new JsonObject { ["name"] = name, ["max"] = max }, 120000);
            var entries = r["entries"] as JsonArray ?? new JsonArray();
            int total = entries.Sum(e => (e?["refs"] as JsonArray)?.Count ?? 0);
            o.AppendLine($"Cross references to '{name}' (like TwinCAT's cross reference list): {total}");
            foreach (var e in entries)
                foreach (var x in (e?["refs"] as JsonArray ?? new JsonArray()).Take(max))
                {
                    var loc = x?["location"]?.ToString();
                    var paths = string.IsNullOrEmpty(loc) ? new List<string>() : tc.Sta.Run(() => tc.ResolvePlcPathsPublic(loc));
                    var path = paths.FirstOrDefault();
                    var part = x?["part"]?.ToString();
                    int.TryParse(x?["column"]?.ToString(), out var col);
                    // matching text line in the same object/part whose hit is closest to the column
                    var cand = hits.Where(h => !used.Contains(h) && h.Path == path && (part == null || h.Part == part))
                                   .OrderBy(h => Math.Abs(h.Text.IndexOf(plain, StringComparison.OrdinalIgnoreCase) + 1 - col)).FirstOrDefault();
                    if (cand != null) used.Add(cand);
                    o.AppendLine($"  {Access(x?["Access"]?.ToString()),-15} {path ?? loc ?? "(location unknown)"}" +
                                 (part != null ? $"  [{part}{(cand != null ? $" line {cand.Line}" : "")}]" : "") +
                                 (cand != null ? "  " + cand.Text.Trim() : ""));
                }
        }
        catch (Exception ex) { o.AppendLine("Cross references not available: " + ex.Message + " - text search only:"); }
        var rest = hits.Where(h => !used.Contains(h)).Take(max).ToList();
        if (rest.Count > 0)
        {
            o.AppendLine($"\nOnly in the text search ({rest.Count}, e.g. comments, other symbols with the same name or code that is not compiled):");
            foreach (var h in rest) o.AppendLine($"  {h.Path}  [{h.Part} line {h.Line}]  {h.Text.Trim()}");
        }
        return o.ToString();
    }

    bool DryRun(JsonObject a) => policy.ForceDryRun || Bool(a, "dryRun", false);

    static string PlcDiff(ITcSmTreeItem item, string decl, string impl)
    {
        var o = new StringBuilder($"PROPOSAL for {item.PathName} (nothing written):\n");
        if (decl != null)
        {
            if (item is not ITcPlcDeclaration d) throw new Exception("Object has no declaration part");
            o.Append(LineDiff.Unified(d.DeclarationText, decl, "declaration"));
        }
        if (impl != null)
        {
            if (item is not ITcPlcImplementation i) throw new Exception("Object has no implementation part");
            o.Append(LineDiff.Unified(i.ImplementationText, impl, "implementation"));
        }
        return o.ToString();
    }

    string PlcSearch(JsonObject a)
    {
        var pat = Str(a, "pattern");
        var opts = RegexOptions.CultureInvariant | (Bool(a, "caseSensitive", false) ? RegexOptions.None : RegexOptions.IgnoreCase);
        var rx = new Regex(Bool(a, "regex", false) ? pat : Regex.Escape(pat), opts, TimeSpan.FromSeconds(2));
        var part = (Str(a, "part") ?? "all").ToLowerInvariant();
        int max = Int(a, "max", 200), hits = 0, objects = 0;
        var root = Str(a, "path") is { Length: > 0 } p ? tc.Item(p) : null;
        var o = new StringBuilder();
        void Scan(string path, string label, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length && hits < max; n++)
                if (rx.IsMatch(lines[n])) { hits++; o.AppendLine($"{path}  [{label}:{n + 1}]  {lines[n].Trim()}"); }
        }
        // the given object itself (e.g. a FB) and everything below it (methods, actions ...)
        var items = root != null ? new[] { root }.Concat(tc.PlcObjects(root)) : tc.PlcObjects(null);
        foreach (var it in items)
        {
            if (hits >= max) break;
            string path = null;
            try { path = it.PathName; } catch { continue; }
            bool any = false;
            if (part != "impl" && it is ITcPlcDeclaration d) { any = true; try { Scan(path, "Decl", d.DeclarationText); } catch { } }
            if (part != "decl" && it is ITcPlcImplementation i) { any = true; try { Scan(path, "Impl", i.ImplementationText); } catch { } }
            if (any) objects++;
        }
        var head = $"{hits} hits in {objects} searched objects" + (hits >= max ? $" (truncated at max={max})" : "") + "\n";
        return head + o;
    }

    string PlcReferences(string plcPath, bool installed)
    {
        var plcs = string.IsNullOrEmpty(plcPath) ? tc.PlcProjects() : new List<ITcSmTreeItem> { tc.Item(plcPath) };
        var o = new StringBuilder();
        foreach (var plc in plcs)
            foreach (var c in TwinCatSession.Children(plc))
            {
                if (c is not ITcPlcLibraryManager lm) continue;
                o.AppendLine($"# {plc.Name}  ({c.PathName})");
                foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).References) o.AppendLine("  " + DescribeLib(r));
                if (installed)
                {
                    o.AppendLine("  -- installed in the repository --");
                    foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).ScanLibraries()) o.AppendLine("  " + DescribeLib(r));
                }
            }
        return o.Length > 0 ? o.ToString() : "no library references found";
    }

    static string DescribeLib(object r)
    {
        dynamic d = r;
        static string T(Func<object> f) { try { return f()?.ToString() ?? ""; } catch { return null; } }
        string Lib(object lo)
        {
            if (lo == null) return "-";
            dynamic l = lo;
            return $"{T(() => l.Name)}, {T(() => l.Version)} ({T(() => l.Distributor)})";
        }
        var ph = T(() => d.PlaceholderName);
        if (!string.IsNullOrEmpty(ph))
        {
            dynamic eff = null, def = null;
            try { eff = d.EffectiveResolution; } catch { }
            try { def = d.DefaultResolution; } catch { }
            object res = eff ?? def;
            return $"Placeholder {ph} -> {Lib(res)}";
        }
        return Lib(r);
    }

    string PlcCreate(JsonObject a)
    {
        var kind = Str(a, "kind");
        if (!PlcKinds.TryGetValue(kind, out var sub)) throw new Exception("Unknown kind: " + kind);
        var impls = (Str(a, "implements") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (impls.Count > 0 && sub != 604) throw new Exception("implements is only available with kind=function_block");
        var name = Str(a, "name");
        var decl = Str(a, "declaration");
        if (impls.Count > 0 && decl == null)
            decl = $"FUNCTION_BLOCK {name} IMPLEMENTS {string.Join(", ", impls)}\nVAR_INPUT\nEND_VAR\nVAR_OUTPUT\nEND_VAR\nVAR\nEND_VAR\n";
        // Determine interface members up front (also for dryRun)
        var members = new List<(string Iface, LibApi.Member M)>();
        var missing = new List<string>();
        foreach (var i in impls)
        {
            var ms = InterfaceMembersFor(i);
            if (ms == null) missing.Add(i); else members.AddRange(ms.Select(m => (i, m)));
        }
        if (missing.Count > 0)
            throw new Exception($"Interface not found: {string.Join(", ", missing)}. Project interfaces are read directly; for library " +
                                "interfaces call tc_lib_signatures library=<library> once (fills the cache), then try again.");
        if (DryRun(a))
            return $"PROPOSAL (nothing created): {kind} '{name}' under {tc.Item(Str(a, "parentPath")).PathName}" +
                   (decl is { } dd ? "\n--- Declaration ---\n" + dd : "") +
                   (Str(a, "implementation") is { } ii ? "\n--- Implementation ---\n" + ii : "") +
                   (members.Count > 0 ? "\n--- Stubs from " + string.Join(", ", impls) + " ---\n" +
                                        string.Join("\n", members.Select(m => m.M.IsProperty ? $"PROPERTY {m.M.Name} : {m.M.ReturnType} (Get/Set)" : m.M.Declaration.Split('\n').First(ln => !ln.StartsWith("///")))) : "");
        var lang = Languages.TryGetValue(Str(a, "language") ?? "ST", out var l) ? l : 1;
        var ret = Str(a, "returnType");
        object vInfo = sub switch
        {
            602 or 604 or 608 or 613 or 614 => lang,
            603 or 609 or 610 => new object[] { lang, ret ?? "BOOL" },
            611 or 612 => ret ?? "BOOL",
            _ => null
        };
        var parent = tc.Item(Str(a, "parentPath"));
        var item = (ITcSmTreeItem)((dynamic)parent).CreateChild(name, sub, "", vInfo);
        string w;
        if (sub is 611 or 612)
        {
            // Properties need Get/Set accessors - CreateChild does not create them by itself
            w = PlcWrite(item, decl, null);
            var acc = EnsureAccessors(item, lang, Str(a, "getImplementation") ?? Str(a, "implementation"), Str(a, "setImplementation"));
            w += "; " + acc;
        }
        else w = PlcWrite(item, decl, Str(a, "implementation"));
        var o = new StringBuilder($"created: {item.PathName}; {w}");
        // Stubs for the interface members
        var existing = TwinCatSession.Children(item).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (iface, m) in members)
        {
            if (existing.Contains(m.Name)) { o.Append($"\n  {m.Name}: already exists"); continue; }
            try
            {
                if (m.IsProperty)
                {
                    var pi = (ITcSmTreeItem)((dynamic)item).CreateChild(m.Name, 611, "", m.ReturnType ?? "BOOL");
                    o.Append($"\n  PROPERTY {m.Name} : {m.ReturnType} ({iface}) - {EnsureAccessors(pi, lang, null, null)}");
                }
                else
                {
                    var mi = (ITcSmTreeItem)((dynamic)item).CreateChild(m.Name, 609, "", new object[] { lang, m.ReturnType ?? "BOOL" });
                    PlcWrite(mi, m.Declaration + (m.Declaration.Contains("\nVAR\n") ? "" : "\nVAR\nEND_VAR"), "");
                    o.Append($"\n  METHOD {m.Name}{(m.ReturnType != null ? " : " + m.ReturnType : "")} ({iface})");
                }
            }
            catch (Exception ex) { o.Append($"\n  {m.Name}: ERROR {ex.Message}"); }
        }
        if (members.Count > 0) o.Append("\nStubs are empty - add the implementation with tc_plc_write, then tc_check.");
        return o.ToString();
    }

    // Ensure Get/Set below a property (TwinCAT subtypes 613/614)
    static string EnsureAccessors(ITcSmTreeItem prop, int lang, string getImpl, string setImpl)
    {
        var notes = new List<string>();
        foreach (var (nm, st, impl) in new[] { ("Get", 613, getImpl), ("Set", 614, setImpl) })
        {
            var child = TwinCatSession.Children(prop).FirstOrDefault(c => c.Name.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                Exception last = null;
                foreach (var vi in new object[] { lang, null })
                {
                    try { child = (ITcSmTreeItem)((dynamic)prop).CreateChild(nm, st, "", vi); break; }
                    catch (Exception ex) { last = ex; }
                }
                if (child == null) { notes.Add($"{nm} could NOT be created ({last?.Message})"); continue; }
                notes.Add(nm + " created");
            }
            if (impl != null)
            {
                if (child is ITcPlcImplementation ci) { ci.ImplementationText = impl; notes.Add(nm + " code written"); }
                else notes.Add(nm + " has no implementation part");
            }
        }
        return string.Join(", ", notes);
    }

    // Members of an interface: first in the project, then in the cached library interfaces
    List<LibApi.Member> InterfaceMembersFor(string iface)
    {
        var list = new List<LibApi.Member>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CollectProjectInterface(iface, list, done)) return list;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "libcache");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.GetFiles(dir, "*.api.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try { if (LibApi.Load(f).InterfaceMembers(iface) is { } ms) return ms; } catch { }
        }
        return null;
    }

    bool CollectProjectInterface(string iface, List<LibApi.Member> list, HashSet<string> done)
    {
        if (!done.Add(iface)) return true;
        ITcSmTreeItem it = null;
        foreach (var (nm, pth) in tc.PlcIndex())
        {
            if (!nm.Equals(iface, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var x = tc.Item(pth);
                if (x is ITcPlcDeclaration xd &&
                    Regex.IsMatch(xd.DeclarationText ?? "", @"^\s*(//[^\n]*\n|\(\*[\s\S]*?\*\)\s*)*\s*INTERFACE\b", RegexOptions.IgnoreCase)) { it = x; break; }
            }
            catch { }
        }
        if (it == null) return false;
        var d = ((ITcPlcDeclaration)it).DeclarationText ?? "";
        foreach (var c in TwinCatSession.Children(it))
        {
            var cd = (c as ITcPlcDeclaration)?.DeclarationText ?? "";
            if (Regex.IsMatch(cd, @"\bPROPERTY\b", RegexOptions.IgnoreCase))
            {
                var m = Regex.Match(cd, @"PROPERTY\s+(?:PUBLIC\s+|PRIVATE\s+|PROTECTED\s+|INTERNAL\s+)?\w+\s*:\s*([^\s;/(]+)", RegexOptions.IgnoreCase);
                if (!list.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new LibApi.Member(c.Name, true, m.Success ? m.Groups[1].Value : "BOOL", cd));
            }
            else if (Regex.IsMatch(cd, @"\bMETHOD\b", RegexOptions.IgnoreCase) && !list.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var m = Regex.Match(cd, @"METHOD\s+(?:PUBLIC\s+|PRIVATE\s+|PROTECTED\s+|INTERNAL\s+)?\w+\s*:\s*([^\s;/(]+)", RegexOptions.IgnoreCase);
                list.Add(new LibApi.Member(c.Name, false, m.Success ? m.Groups[1].Value : null, cd.TrimEnd()));
            }
        }
        // EXTENDS of further interfaces (project or library)
        var ext = Regex.Match(d, @"INTERFACE\s+\w+\s+EXTENDS\s+([\w\s,.]+)", RegexOptions.IgnoreCase);
        if (ext.Success)
            foreach (var e in ext.Groups[1].Value.Split(',').Select(x => x.Trim().Split('.').Last()).Where(x => x.Length > 0))
                if (!CollectProjectInterface(e, list, done) && InterfaceMembersFor(e) is { } lm)
                    foreach (var x in lm) if (!list.Any(y => y.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))) list.Add(x);
        return true;
    }

    // ------------------------------------------------------------------ Create I/O
    const int IoEtherCatMaster = 111, IoBox = 9099;

    string IoAdd(JsonObject a)
    {
        var kind = (Str(a, "kind") ?? "").ToLowerInvariant();
        var parentPath = Str(a, "parentPath") ?? "TIID";
        var product = Str(a, "product");
        var kids = (Str(a, "children") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        int sub = kind switch
        {
            "ethercat_master" => IoEtherCatMaster,
            "box" or "terminal" or "coupler" => IoBox,
            "raw" => Int(a, "subType", -1),
            _ => throw new Exception("kind: ethercat_master, box or raw")
        };
        if (sub < 0) throw new Exception("raw requires subType");
        if (sub == IoBox && string.IsNullOrEmpty(product)) throw new Exception("box requires product, e.g. EK1100");
        var parent = tc.Item(parentPath);
        if (DryRun(a))
            return $"PROPOSAL (nothing created): under {parent.PathName}: {(sub == IoBox ? product : kind)}" +
                   (kids.Count > 0 ? "\n  below it: " + string.Join(", ", kids) : "");
        var o = new StringBuilder();
        var item = CreateIo(parent, sub, product, Str(a, "name"));
        o.Append("created: ").Append(item.PathName);
        foreach (var k in kids)
        {
            try { o.Append("\n  + ").Append(CreateIo(item, IoBox, k, null).PathName); }
            catch (Exception ex) { o.Append($"\n  {k}: ERROR {ex.InnerException?.Message ?? ex.Message}"); }
        }
        o.Append("\nCheck sub-items/process data with tc_tree; parameters with tc_xml_get/tc_xml_set.");
        if (sub == IoEtherCatMaster) o.Append(" The master's network adapter is not assigned yet (in the IDE: Adapter > Search, or via tc_xml_set).");
        return o.ToString();
    }

    // Name in TwinCAT style: devices "Device n (EtherCAT)", boxes "Term n (EL1008)" with a running number across the whole device
    ITcSmTreeItem CreateIo(ITcSmTreeItem parent, int sub, string product, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            if (sub == IoEtherCatMaster)
                name = $"Device {TwinCatSession.Children(parent).Count() + 1} (EtherCAT)";
            else
            {
                // go to the device (TIID^Device n ...) and count all boxes below it
                var segs = parent.PathName.Split('^');
                var dev = segs.Length >= 2 ? tc.Item(segs[0] + "^" + segs[1]) : parent;
                int n = Descendants(dev).Count(x => Regex.IsMatch(x.Name, @"^(Term|Box) \d+ \(")) + 1;
                var shortName = product.Split('-')[0].ToUpperInvariant();
                name = $"{(shortName.StartsWith("EP") || shortName.StartsWith("EQ") ? "Box" : "Term")} {n} ({shortName})";
            }
        }
        object vInfo = sub == IoBox ? product : null;
        try { return (ITcSmTreeItem)((dynamic)parent).CreateChild(name, sub, "", vInfo); }
        catch (Exception ex) when (sub == IoBox && !product.Contains('-'))
        {
            throw new Exception($"{product} could not be created ({ex.InnerException?.Message ?? ex.Message}). If necessary, specify it with revision, " +
                                $"e.g. {product}-0000-0017, and check whether the ESI description is installed in TwinCAT.");
        }
    }

    static IEnumerable<ITcSmTreeItem> Descendants(ITcSmTreeItem it)
    {
        foreach (var c in TwinCatSession.Children(it)) { yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    // ------------------------------------------------------------------ Linking: see LinkGroups.cs

    // Leaves below an item = variables/channels; type and size from ProduceXml where available
    string IoVars(string path, string filter, int max)
    {
        var root = tc.Item(path);
        var o = new StringBuilder();
        int n = 0;
        foreach (var it in Descendants(root).Prepend(root))
        {
            if (TwinCatSession.Children(it).Any()) continue;   // leaves only
            var p = it.PathName;
            if (!string.IsNullOrEmpty(filter) && p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++n > max) { o.AppendLine($"... (truncated after {max} - use filter)"); break; }
            string type = "";
            try
            {
                var x = (string)((dynamic)it).ProduceXml(false);
                var m = Regex.Match(x, @"<VarType[^>]*>([^<]+)</VarType>");
                if (m.Success) type = m.Groups[1].Value;
                if (Regex.IsMatch(x, @"<Link[s ]|<LinkInfo", RegexOptions.IgnoreCase)) type += " [linked?]";
            }
            catch { }
            o.Append(p);
            if (type.Length > 0) o.Append("  : ").Append(type);
            o.AppendLine();
        }
        return n == 0 ? $"No variables found under {root.PathName} (the PLC process image only appears after tc_build)." : o.ToString();
    }

    // ------------------------------------------------------------------ Tasks
    string Task(JsonObject a)
    {
        var action = (Str(a, "action") ?? "list").ToLowerInvariant();
        var rt = tc.Item("TIRT");
        if (action == "list")
        {
            var o = new StringBuilder();
            foreach (var t in TwinCatSession.Children(rt))
            {
                string x = ""; try { x = (string)((dynamic)t).ProduceXml(false); } catch { }
                string V(string tag) { var m = Regex.Match(x, $@"<{tag}[^>]*>([^<]*)</{tag}>", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : "?"; }
                var ct = V("CycleTime");
                var us = long.TryParse(ct, out var ticks) ? $"{ticks / 10.0:0.###} us" : ct;
                o.AppendLine($"{t.PathName}  cycle {us}, priority {V("Priority")}, AutoStart {V("AutoStart")}");
            }
            return o.Length == 0 ? "No tasks under TIRT." : o.ToString();
        }
        if (action == "assign")
        {
            // PLC task reference (<task>.TcTTO in the PLC project, linked by name to TIRT^<task>) plus the program call
            var taskName = Str(a, "name") ?? throw new Exception("name is required: the TIRT task, e.g. PlcTask");
            try { tc.Item("TIRT^" + taskName); } catch { throw new Exception($"task TIRT^{taskName} not found - create it first with action=create"); }
            var plc = PlcProject(Str(a, "plcProjectPath"));
            var program = Str(a, "program") ?? "MAIN";
            var tref = TwinCatSession.Children(plc).FirstOrDefault(c => string.Equals(c.Name, taskName, StringComparison.OrdinalIgnoreCase));
            var hasCall = tref != null && TwinCatSession.Children(tref).Any(c => string.Equals(c.Name, program, StringComparison.OrdinalIgnoreCase));
            var plan = (tref == null ? $"create task reference {plc.PathName}^{taskName}\n" : "") + (hasCall ? "" : $"add program call {program}\n");
            if (plan.Length == 0) return $"{program} is already called by {taskName} in {plc.PathName} - nothing to do.";
            if (DryRun(a)) return "PROPOSAL (nothing changed):\n" + plan;
            if (tref == null)
            {
                ((dynamic)plc).CreateChild(taskName, 621, "", null);
                tref = TwinCatSession.Children(plc).FirstOrDefault(c => string.Equals(c.Name, taskName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new Exception($"the task reference {taskName} was not created in {plc.PathName}");
            }
            if (!hasCall)
            {
                ((dynamic)tref).CreateChild(program, 650, "", null);
                if (!TwinCatSession.Children(tref).Any(c => string.Equals(c.Name, program, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception($"the program call {program} was not created below {tref.PathName} - is {program} a PROGRAM of this PLC project?");
            }
            tc.InvalidatePlcIndex();
            return "Done:\n" + plan + $"Program calls of {taskName}: {string.Join(", ", TwinCatSession.Children(tref).Select(c => c.Name))}\n" +
                   "Build (tc_build) to generate the process image; takes effect on the target after tc_activate.";
        }
        ITcSmTreeItem task;
        if (action == "create")
        {
            var name = Str(a, "name") ?? throw new Exception("name missing");
            if (DryRun(a)) return $"PROPOSAL: task '{name}' under TIRT ({(Bool(a, "withImage", false) ? "with" : "without")} process image)";
            task = (ITcSmTreeItem)((dynamic)rt).CreateChild(name, Bool(a, "withImage", false) ? 0 : 1, null, null);
        }
        else if (action == "set") task = tc.Item(Str(a, "path") ?? throw new Exception("path missing"));
        else throw new Exception("action: list, create, set or assign");
        var res = $"{(action == "create" ? "created" : "Task")}: {task.PathName}";
        // Replace values in the task's own XML - this guarantees the tag names are correct
        var changes = new List<(string Tag, string Val)>();
        if (a["cycleUs"] != null) changes.Add(("CycleTime", (Int(a, "cycleUs", 1000) * 10L).ToString()));
        if (a["priority"] != null) changes.Add(("Priority", Int(a, "priority", 20).ToString()));
        if (a["autoStart"] != null) changes.Add(("AutoStart", Bool(a, "autoStart", true) ? "true" : "false"));
        if (changes.Count == 0 && Str(a, "cores") == null) return res;
        var xml = (string)((dynamic)task).ProduceXml(false);
        if (Str(a, "cores") is { } coreList)
        {
            var cores = coreList.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            if (cores.Count == 0 || cores.Any(c => c < 0 || c > 63)) throw new Exception("cores: 0 ... 63, e.g. '1'");
            ulong mask = cores.Aggregate(0UL, (acc, c) => acc | (1UL << c));
            var cur = Regex.Match(xml, @"<CpuAffinity[^>]*>([^<]*)</CpuAffinity>", RegexOptions.IgnoreCase);
            changes.Add(("CpuAffinity", FormatMask(mask, cur.Success ? cur.Groups[1].Value : "#x0000000000000001")));
        }
        var notes = new List<string>();
        foreach (var (tag, val) in changes)
        {
            var re = new Regex($@"(<{tag}(\s[^>]*)?>)([^<]*)(</{tag}>)", RegexOptions.IgnoreCase);
            if (!re.IsMatch(xml)) { notes.Add($"{tag}: not found in the task XML"); continue; }
            xml = re.Replace(xml, m => m.Groups[1].Value + val + m.Groups[4].Value, 1);
            notes.Add($"{tag}={val}");
        }
        if (DryRun(a)) return res + " - PROPOSAL: " + string.Join(", ", notes);
        ((dynamic)task).ConsumeXml(xml);
        return res + " - set: " + string.Join(", ", notes) + " (CycleTime in 100 ns steps). Takes effect after tc_activate.";
    }

    // ------------------------------------------------------------------ Boot project
    string BootProject(JsonObject a)
    {
        ITcSmTreeItem plc = !string.IsNullOrEmpty(Str(a, "plcPath")) ? tc.Item(Str(a, "plcPath"))
                          : TwinCatSession.Children(tc.Item("TIPC")).FirstOrDefault() ?? throw new Exception("No PLC project under TIPC");
        dynamic d = plc;
        string Now() { try { return ((bool)d.BootProjectAutostart) ? "on" : "off"; } catch (Exception ex) { return "not readable (" + ex.Message + ")"; } }
        var o = new StringBuilder($"{plc.PathName}: boot project autostart {Now()}");
        if (DryRun(a)) return o + (a["autostart"] != null ? $"\nPROPOSAL: autostart {(Bool(a, "autostart", true) ? "on" : "off")}" : "") +
                              (Bool(a, "generate", false) ? "\nPROPOSAL: generate boot project" : "");
        if (a["autostart"] != null)
        {
            try { d.BootProjectAutostart = Bool(a, "autostart", true); o.Append($" -> now {Now()}"); }
            catch (Exception ex) { o.Append($"\nAutostart could not be set: {ex.InnerException?.Message ?? ex.Message}"); }
        }
        if (Bool(a, "generate", false))
        {
            try { d.GenerateBootProject(true); o.Append("\nBoot project generated."); }
            catch (Exception ex) { o.Append($"\nBoot project not generated: {ex.InnerException?.Message ?? ex.Message}"); }
        }
        return o.ToString();
    }

    // ------------------------------------------------------------------ Runtime startup mode (local, registry)
    static string RuntimeStartup(string mode)
    {
        const string key = @"SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\System";
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key, writable: !string.IsNullOrEmpty(mode));
        if (k == null) return "TwinCAT registry not found (HKLM\\" + key + ").";
        string Show(object v) => v is int i ? (i == 5 ? "run" : i == 15 ? "config" : $"value {i}") : "not set";
        var cur = Show(k.GetValue("SysStartupState"));
        if (string.IsNullOrEmpty(mode)) return $"Local startup mode: {cur}";
        int val = mode.Equals("run", StringComparison.OrdinalIgnoreCase) ? 5 : mode.Equals("config", StringComparison.OrdinalIgnoreCase) ? 15
                : throw new Exception("mode: run or config");
        k.SetValue("SysStartupState", val, Microsoft.Win32.RegistryValueKind.DWord);
        return $"Local startup mode: {cur} -> {Show(val)} (applies from the next boot)";
    }

    // ------------------------------------------------------------------ Licenses
    string Licenses()
    {
        // The license node is located under SYSTEM; its name depends on language/version
        foreach (var p in new[] { "TIRC^License", "TIRC^Lizenz", "TIRC^Licence" })
            try { var it = tc.Item(p); return it.PathName + "\n" + (string)((dynamic)it).ProduceXml(false); } catch { }
        foreach (var root in new[] { "TIRC", "TIRS" })
            try
            {
                foreach (var c in TwinCatSession.Children(tc.Item(root)))
                    if (Regex.IsMatch(c.Name, "Lizen|Licen", RegexOptions.IgnoreCase))
                        return c.PathName + "\n" + (string)((dynamic)c).ProduceXml(false);
            }
            catch { }
        return "License node not found - search with tc_tree path=TIRC and report via tc_feedback.";
    }

    // ------------------------------------------------------------------ ADS helpers
    static AdsClient Connect(JsonObject a)
    {
        var c = new AdsClient();
        var net = Str(a, "netId");
        c.Connect(string.IsNullOrEmpty(net) ? AmsNetId.Local : AmsNetId.Parse(net), Int(a, "port", 851));
        return c;
    }

    static string Ads(JsonObject a, Func<IValueSymbol, string> f)
    {
        using var c = Connect(a);
        var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
        if (!loader.Symbols.TryGetInstance(Str(a, "symbol"), out var s) || s is not IValueSymbol vs)
            throw new Exception("Symbol not found: " + Str(a, "symbol"));
        return f(vs);
    }

    static string Format(object v) => v switch
    {
        null => "null",
        System.Collections.IEnumerable e and not string => "[" + string.Join(", ", e.Cast<object>().Select(Format)) + "]",
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    // ------------------------------------------------------------------ Registry / Schema
    void Add(string name, string desc, JsonObject schema, Func<JsonObject, string> h) => tools.Add(new Tool(name, desc, schema, h));

    // Target system of a runtime operation: explicit netId or the solution's current target
    string TargetOf(string tool, JsonObject a) => tool switch
    {
        "ads_write" => Str(a, "netId"),
        // the target of the call, otherwise the one of .tcagent\tcunit.json (TcUnitRun checks the whitelist again)
        "tc_test_run" => (Str(a, "target") ?? TcUnitConfig.Load(SolutionDir()).Target) is string t && !t.Trim().Equals("local", StringComparison.OrdinalIgnoreCase) ? t.Trim() : null,
        "tc_activate" when !string.IsNullOrEmpty(Str(a, "netId")) => Str(a, "netId"),
        "tc_runtime_mode" when !string.IsNullOrEmpty(Str(a, "netId")) => Str(a, "netId"),
        "tc_activate" or "tc_plc_online" or "tc_runtime_mode" => tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId()),
        _ => null
    };

    public JsonArray List() => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone()
    }).ToArray());

    bool rulesSeen;

    string lastSolutionDir;

    string SolutionDir(int timeoutMs = 600_000)
    {
        try
        {
            var d = tc.Sta.Run(() => Path.GetDirectoryName((string)tc.RequireDte().Solution.FullName), timeoutMs);
            if (!string.IsNullOrEmpty(d)) lastSolutionDir = d;
            return d;
        }
        catch { return null; }
    }

    public JsonObject Call(string name, JsonObject args)
    {
        var t = tools.FirstOrDefault(x => x.Name == name) ?? throw new Exception("Unknown tool: " + name);
        // Safety net: if there are coding guidelines and the agent has not read them yet, the first writing
        // code call is not executed; the guidelines are returned instead (once per session).
        if (!rulesSeen && (name is "tc_plc_write" or "tc_plc_create" || name == "tc_refactor" && string.Equals(Str(args, "action"), "apply", StringComparison.OrdinalIgnoreCase)
                           || name == "tc_plaintext" && string.Equals(Str(args, "action"), "import", StringComparison.OrdinalIgnoreCase)) && !DryRun(args)
            && Environment.GetEnvironmentVariable("TCAGENT_RULES_GATE") != "0")
        {
            rulesSeen = true;
            if (CodingRules.Text(SolutionDir()) is { } rt)
            {
                Log.Info($"{name} not executed: coding guidelines not read yet");
                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] =
                        "NOT executed: this project has coding guidelines that have not been read yet. " +
                        "Align the code with them and then repeat the call (next time call tc_coding_rules first).\n\n" + rt }),
                    ["isError"] = true
                };
            }
        }
        string text; bool isError = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // For the bridge's hang diagnostics: which tool is currently running (one file per TcAgent process)
        var busy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", $"busy.{Environment.ProcessId}.txt");
        try { File.WriteAllText(busy, name); } catch { }
        try
        {
            policy.Check(name, () => TargetOf(name, args), args);
            // values that are not allowed are refused before anybody is asked to confirm them (Packages.cs)
            if (name == "tc_packages_change") PackagesChangeArgs(args);
            // nobody is asked to confirm an activation that would be refused anyway
            if (name == "tc_activate") ActivateGate();
            if (Confirm.Needed(name, args))
            {
                string target = null;
                try { target = TargetOf(name, args); } catch { }
                var what = Confirm.Describe(name, args, target);
                var ok = Confirm.Ask(name, what, tc.AttachedPid);
                // no answer and nobody to ask are refusals too (Guards.cs) - without a question only with TCAGENT_CONFIRM=off
                if (Guards.ConfirmRefusal(ok, Confirm.LastNotShown, what) is { } refused)
                {
                    if (ok == null) Log.Info("No confirmation possible (no channel), refused - " + what);
                    throw new OperationCanceledException(refused);
                }
            }
            Log.Info($"{name} started{LogAttrs(args)}");
            // tc_refactor apply/result wait for the user themselves (job with its own dialog watch) - see RefactorJobs.cs
            text = WatchesItself(name, args) ? t.Handler(args) : RunWatchingDialogs(name, () => t.Handler(args), args);
            Log.Info($"{name} done ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) { text = "Error: " + ErrorText(ex); isError = true; Log.Error($"{name}: {ex}"); }
        finally { try { File.Delete(busy); } catch { } }
        try { ObserveWorkflow(name, isError, text); } catch (Exception ex) { Log.Error("Workflow: " + ex.Message); }
        // lessons learned: recall known solutions for this error, remind to save one after a hard-won success
        if (name != "tc_lessons" && Environment.GetEnvironmentVariable("TCAGENT_LESSONS_RECALL") != "0")
        {
            var plain = text;
            // never let the lessons lookup prevent the audit entry below
            try
            {
                // after an error the STA may still be blocked (IDE dialog, hang) or the IDE gone: short timeout, then the last known solution
                if (isError) text += Lessons.Recall(SolutionDir(2000) ?? lastSolutionDir, name, text) ?? "";
                else if ((name is "tc_check" or "tc_build" or "tc_errors") && Lessons.HasCompileErrors(text))
                    text += Lessons.Recall(SolutionDir(), name, text) ?? "";
                text += Lessons.Track(name, isError, plain) ?? "";
            }
            catch (Exception ex) { Log.Error("Lessons: " + ex.Message); }
        }
        var callClass = Policy.ClassOf(name, args);
        if (callClass != ToolClass.Read || isError) policy.Audit(name, args, !isError, text, sw.ElapsedMilliseconds);
        if (callClass != ToolClass.Read && !isError) tc.InvalidatePlcIndex();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }

    /// <summary>Runs a tool; if the IDE opens a modal dialog meanwhile, every automation interface call waits
    /// until it is closed. In that case return immediately with the dialog text instead of hanging for minutes
    /// (the call itself keeps running and completes as soon as the dialog is closed).</summary>
    string RunWatchingDialogs(string name, Func<string> handler, JsonObject args = null)
    {
        if (name == "tc_dialogs") return handler();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var task = System.Threading.Tasks.Task.Run(handler);
        int seen = 0;
        while (!task.Wait(1000))
        {
            List<IdeDialogs.Dialog> dialogs;
            try { dialogs = IdeDialogs.Find(tc.AttachedPid); } catch { dialogs = new(); }
            seen = dialogs.Count > 0 ? seen + 1 : 0;
            if (seen < 3) continue;
            task.ContinueWith(x =>
            {
                Log.Info($"{name} finished after dialog: " + (x.IsFaulted ? x.Exception?.InnerException?.Message : "ok\n" + (x.Result.Length > 2000 ? x.Result[..2000] + "..." : x.Result)));
                // the call was reported (and audited) as error, but the change happened after all - audit the real outcome too
                if (args != null && Policy.ClassOf(name, args) != ToolClass.Read)
                    policy.Audit(name, args, !x.IsFaulted, x.IsFaulted ? "Error: " + ErrorText(x.Exception!) : x.Result, started.ElapsedMilliseconds,
                                 "completed after an IDE dialog (the call itself returned an error)");
            });
            throw new Exception("The IDE is showing a dialog and waiting for input - " + name + " will only complete afterwards:\n  " +
                                string.Join("\n  ", dialogs) +
                                "\nAsk the user to read and close the dialog (or tc_dialogs action=close, which corresponds to Cancel/X). " +
                                "Further TwinCAT calls wait until then.");
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Readable error text - never empty (FileNotFoundException etc. have an empty Message).</summary>
    static string ErrorText(Exception ex)
    {
        var e = ex;
        while ((e is AggregateException || e is System.Reflection.TargetInvocationException) && e.InnerException != null) e = e.InnerException;
        if (e.InnerException != null && string.IsNullOrWhiteSpace(e.Message)) e = e.InnerException;
        var msg = e.Message;
        if (e is FileNotFoundException or FileLoadException)
        {
            var file = (e as FileNotFoundException)?.FileName ?? (e as FileLoadException)?.FileName;
            msg = $"{e.GetType().Name}: {file} {msg}".Trim();
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "TcAgent.dll")) && !File.Exists(Path.Combine(AppContext.BaseDirectory, "TcAgent.exe")) ||
                file?.StartsWith("System.", StringComparison.Ordinal) == true)
                msg += " - the files of this running TcAgent were removed or replaced (update while it was running). " +
                       "Restart the AI client: TcAgent Chat (close/reopen the chat window or restart Visual Studio), desktop apps (tray icon > Quit), CLIs (start them again).";
        }
        if (string.IsNullOrWhiteSpace(msg)) msg = e.GetType().Name;
        if (e is not IdeLostException && TwinCatSession.IsIdeLost(ex))
            msg += " - the connection to the IDE was lost (TcXaeShell/Visual Studio crashed or was closed). Tell the user; when the IDE " +
                   "is open again call tc_attach and check the last change - do not repeat changes blindly.";
        return msg;
    }

    /// <summary>Saves the item before tc_delete: PLC objects as PLCopen XML (tc_import kind=plcopen), others as ProduceXml.
    /// Returns the backup file or null.</summary>
    string BackupBeforeDelete(string parentPath, string name)
    {
        // next to the solution (visible for the IDE and the user); %LOCALAPPDATA% only without a solution
        var sol = SolutionDir(2000) ?? lastSolutionDir;
        var dir = sol != null ? Path.Combine(sol, ".tcagent", "backup", "delete")
                              : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "backup", "delete");
        Directory.CreateDirectory(dir);
        try   // backups are not part of the project's Git repository
        {
            var ign = Path.Combine(Path.GetDirectoryName(dir)!, ".gitignore");
            if (sol != null && !File.Exists(ign)) File.WriteAllText(ign, "# TcAgent backups - local only\n*\n");
        }
        catch { }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = parentPath + "^" + name;
        var parts = path.Split('^');
        try
        {
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var candidate = string.Join("^", parts.Take(i + 1));
                ITcSmTreeItem it;
                try { it = tc.Item(candidate); } catch { continue; }
                if (it.ItemType != 600) continue;                       // PLC project
                // export through the temp folder: the IDE may not see folders this process creates under %LOCALAPPDATA%
                // (packaged clients like Claude Desktop redirect new AppData folders)
                var tmp = Path.Combine(Path.GetTempPath(), $"tcagent-del-{Guid.NewGuid():N}.xml");
                var file = Path.Combine(dir, $"{stamp}_{safe}.xml");
                ComCall.Invoke(it, "PlcOpenExport", tmp, string.Join(".", parts.Skip(i + 1)));
                if (File.Exists(tmp))
                {
                    // restore into the same folder: TwinCAT imports objects without folder information into the project root
                    try
                    {
                        var x = System.Xml.Linq.XDocument.Load(tmp);
                        if (WrapInFolders(x, parts.Skip(i + 1).Take(parts.Length - i - 2).ToList())) x.Save(tmp);
                    }
                    catch (Exception ex) { Log.Info("tc_delete backup (folders): " + ex.Message); }
                    File.Copy(tmp, file, true); try { File.Delete(tmp); } catch { }
                    return file;
                }
                break;
            }
        }
        catch (Exception ex) { Log.Info("tc_delete backup (PLCopen): " + ex.Message); }
        try
        {
            // PLC object: declaration and code as text (object and all sub-objects)
            var sb = new StringBuilder();
            void Dump(ITcSmTreeItem x, int depth)
            {
                string d = null, c = null;
                try { if (x is ITcPlcDeclaration xd) d = xd.DeclarationText; } catch { }
                try { if (x is ITcPlcImplementation xi) c = xi.ImplementationText; } catch { }
                if (d != null || c != null) sb.AppendLine($"(* ===== {x.PathName} ===== *)").AppendLine(d ?? "").AppendLine("(* ----- implementation ----- *)").AppendLine(c ?? "");
                if (depth < 6) foreach (var ch in TwinCatSession.Children(x)) Dump(ch, depth + 1);
            }
            Dump(tc.Item(path), 0);
            if (sb.Length > 0)
            {
                var file = Path.Combine(dir, $"{stamp}_{safe}.st.txt");
                File.WriteAllText(file, sb.ToString());
                return file;
            }
        }
        catch (Exception ex) { Log.Info("tc_delete backup (text): " + ex.Message); }
        try
        {
            string xml = ((dynamic)tc.Item(path)).ProduceXml(true);
            var file = Path.Combine(dir, $"{stamp}_{safe}.producexml.xml");
            File.WriteAllText(file, xml);
            return file;
        }
        catch (Exception ex) { Log.Info("tc_delete backup (ProduceXml): " + ex.Message); }
        return null;
    }

    static (string name, JsonObject def, bool req) P(string name, string type, string desc) =>
        (name, new JsonObject { ["type"] = type, ["description"] = desc }, false);
    static (string, JsonObject, bool) Req((string n, JsonObject d, bool r) p) => (p.n, p.d, true);

    static JsonObject Schema(params (string name, JsonObject def, bool req)[] props)
    {
        var p = new JsonObject();
        var r = new JsonArray();
        foreach (var x in props) { p[x.name] = x.def; if (x.req) r.Add(x.name); }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = r };
    }

    static string Str(JsonObject a, string k) => a[k] is JsonValue v ? v.ToString() : null;
    static int Int(JsonObject a, string k, int d) => a[k] is JsonValue v && int.TryParse(v.ToString(), out var i) ? i : d;
    static bool Bool(JsonObject a, string k, bool d) => a[k] is JsonValue v && bool.TryParse(v.ToString(), out var b) ? b : d;
}
