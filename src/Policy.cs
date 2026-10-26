// Guard rails: target system whitelist for runtime operations, optional read-only mode and audit log.
// Configured via environment variables (in the MCP configuration under "env"):
//   TCAGENT_ALLOW_TARGETS  Comma-separated list of AmsNetIds allowed for activate/online/ADS write.
//                          "local" (default) = this PC only (127.0.0.1.1.1 or its own NetId).
//   TCAGENT_MODE           agent (default) = everything within the whitelist
//                          propose = change nothing; tc_plc_write/tc_plc_create only return a diff (dryRun)
//                          ask     = read only (no project and no runtime changes)
//                          (German aliases also accepted: vorschlag = propose, fragen = ask; read = ask)
//   TCAGENT_READONLY       1/true = same as TCAGENT_MODE=ask (older spelling).
//   TCAGENT_AUDIT          Path of the audit log (default %LOCALAPPDATA%\TcAgent\audit.jsonl)
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TcAgent;

public enum ToolClass { Read, ProjectWrite, RuntimeWrite }
public enum AgentMode { Agent, Propose, Ask }

public sealed class Policy
{
    static readonly Dictionary<string, ToolClass> Classes = new()
    {
        ["tc_xml_set"] = ToolClass.ProjectWrite, ["tc_delete"] = ToolClass.ProjectWrite,
        ["tc_link"] = ToolClass.ProjectWrite, ["tc_unlink"] = ToolClass.ProjectWrite,
        ["tc_plc_write"] = ToolClass.ProjectWrite, ["tc_plc_create"] = ToolClass.ProjectWrite,
        ["tc_plc_library_add"] = ToolClass.ProjectWrite, ["tc_save"] = ToolClass.ProjectWrite,
        ["tc_coding_rules_add"] = ToolClass.ProjectWrite, ["tc_risk_rules"] = ToolClass.ProjectWrite, ["tc_docs_add"] = ToolClass.ProjectWrite, ["tc_io_add"] = ToolClass.ProjectWrite, ["tc_task"] = ToolClass.ProjectWrite, ["tc_boot_project"] = ToolClass.ProjectWrite, ["tc_dialogs"] = ToolClass.Read,
        ["tc_runtime_startup"] = ToolClass.RuntimeWrite, ["tc_runtime_mode"] = ToolClass.RuntimeWrite, ["tc_io_scan_add"] = ToolClass.ProjectWrite, ["tc_motion_add"] = ToolClass.ProjectWrite, ["tc_motion"] = ToolClass.ProjectWrite,
        ["tc_new_solution"] = ToolClass.ProjectWrite, ["tc_plc_project_add"] = ToolClass.ProjectWrite, ["tc_import"] = ToolClass.ProjectWrite,
        ["tc_library"] = ToolClass.ProjectWrite, ["tc_library_save"] = ToolClass.ProjectWrite, ["tc_mapping_import"] = ToolClass.ProjectWrite,
        ["tc_sbom"] = ToolClass.ProjectWrite, ["tc_library_lock"] = ToolClass.ProjectWrite, ["tc_plaintext"] = ToolClass.ProjectWrite,
        ["tc_twincat_version"] = ToolClass.ProjectWrite, ["tc_platform"] = ToolClass.ProjectWrite, ["tc_variant"] = ToolClass.ProjectWrite,
        ["tc_tccom"] = ToolClass.ProjectWrite, ["tc_scope_new"] = ToolClass.ProjectWrite, ["tc_scope"] = ToolClass.ProjectWrite,
        ["tc_hmi_page"] = ToolClass.ProjectWrite, ["tc_hmi_control"] = ToolClass.ProjectWrite, ["tc_hmi_symbol"] = ToolClass.ProjectWrite, ["tc_packages_change"] = ToolClass.RuntimeWrite,
        ["tc_realtime"] = ToolClass.ProjectWrite, ["tc_sfc"] = ToolClass.ProjectWrite, ["tc_doc"] = ToolClass.ProjectWrite, ["tc_userdoc"] = ToolClass.ProjectWrite, ["tc_refactor"] = ToolClass.ProjectWrite, ["tc_lessons"] = ToolClass.ProjectWrite, ["tc_coe"] = ToolClass.ProjectWrite, ["tc_route_add"] = ToolClass.RuntimeWrite,
        ["tc_activate"] = ToolClass.RuntimeWrite, ["tc_plc_online"] = ToolClass.RuntimeWrite,
        ["ads_write"] = ToolClass.RuntimeWrite, ["tc_test_run"] = ToolClass.RuntimeWrite,
        ["tc_knowledge"] = ToolClass.ProjectWrite,
    };

    readonly HashSet<string> allowedTargets;
    public AgentMode Mode { get; }
    readonly string auditPath;
    readonly string localNetId;

    public Policy()
    {
        localNetId = ReadLocalNetId();
        var cfg = Environment.GetEnvironmentVariable("TCAGENT_ALLOW_TARGETS");
        allowedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in (string.IsNullOrWhiteSpace(cfg) ? "local" : cfg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (t.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                allowedTargets.Add("127.0.0.1.1.1");
                if (localNetId != null) allowedTargets.Add(localNetId);
            }
            else allowedTargets.Add(t);
        }
        var ro = Environment.GetEnvironmentVariable("TCAGENT_READONLY");
        var m = (Environment.GetEnvironmentVariable("TCAGENT_MODE") ?? "agent").Trim().ToLowerInvariant();
        Mode = m switch { "propose" or "vorschlag" => AgentMode.Propose, "ask" or "read" or "fragen" => AgentMode.Ask, _ => AgentMode.Agent };
        if (ro == "1" || string.Equals(ro, "true", StringComparison.OrdinalIgnoreCase)) Mode = AgentMode.Ask;
        auditPath = Environment.GetEnvironmentVariable("TCAGENT_AUDIT") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
        Log.Info($"Policy: targets=[{string.Join(", ", allowedTargets)}] mode={Mode} audit={auditPath}");
    }

    /// <summary>Command line only (TcAgent.exe hil --allow): targets a person allowed explicitly in the pipeline definition,
    /// in addition to TCAGENT_ALLOW_TARGETS. Never used by the MCP server, so an agent cannot widen the whitelist.</summary>
    public Policy(IEnumerable<string> extraTargets) : this()
    {
        var added = new List<string>();
        foreach (var t in extraTargets ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Trim().Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                allowedTargets.Add("127.0.0.1.1.1");
                if (localNetId != null) allowedTargets.Add(localNetId);
            }
            else allowedTargets.Add(t.Trim());
            added.Add(t.Trim());
        }
        if (added.Count > 0) Log.Info($"Policy: additionally allowed by the command line (--allow): {string.Join(", ", added)}");
    }

    /// <summary>true for the runtime of this PC: empty, "local", 127.0.0.1.1.1 or the own AmsNetId.</summary>
    public bool IsLocal(string netId) =>
        string.IsNullOrWhiteSpace(netId) || netId.Trim().Equals("local", StringComparison.OrdinalIgnoreCase) || netId.Trim() == "127.0.0.1.1.1" ||
        (localNetId != null && netId.Trim().Equals(localNetId, StringComparison.OrdinalIgnoreCase));

    public static ToolClass ClassOf(string tool) => Classes.TryGetValue(tool, out var c) ? c : ToolClass.Read;

    // read-only actions of otherwise changing tools (tc_task list, tc_realtime show, tc_coe list, tc_scope list ...)
    static readonly HashSet<string> ReadActions = new(StringComparer.OrdinalIgnoreCase) { "list", "show", "info", "read", "runtimes", "controls", "xml", "outline", "memory", "pending", "risks", "analyze", "data", "templates", "template", "result", "search", "platforms", "effective", "why" };

    /// <summary>Class of a concrete call: read-only actions count as Read (no confirmation, allowed in ask/propose mode).</summary>
    public static ToolClass ClassOf(string tool, JsonObject args)
    {
        var cls = ClassOf(tool);
        if (cls == ToolClass.ProjectWrite && args?["action"] is JsonValue v && ReadActions.Contains(v.ToString())) return ToolClass.Read;
        // tc_test_run action=list only reads the code
        if (tool == "tc_test_run" && args?["action"] is JsonValue ta && ta.ToString().Trim().Equals("list", StringComparison.OrdinalIgnoreCase)) return ToolClass.Read;
        if (cls == ToolClass.ProjectWrite && args?["action"] == null && tool is "tc_userdoc" or "tc_refactor" or "tc_motion" or "tc_lessons") return ToolClass.Read;   // default action: data / analyze / list / search
        // tc_risk_rules: only add/builtin/check write a file; show and test only read
        if (cls == ToolClass.ProjectWrite && tool == "tc_risk_rules" && !(args?["action"] is JsonValue ra && ra.ToString().ToLowerInvariant() is "add" or "builtin" or "check"))
            return ToolClass.Read;
        // tc_library_lock: only action=write writes the lock file; check (the default) only reads
        if (cls == ToolClass.ProjectWrite && tool == "tc_library_lock" && !(args?["action"] is JsonValue la && la.ToString().Trim().ToLowerInvariant() == "write"))
            return ToolClass.Read;
        // tc_sfc comments/flags without values only list the step comments / SFC flags
        if (cls == ToolClass.ProjectWrite && tool == "tc_sfc" && args?["action"] is JsonValue sa && sa.ToString().ToLowerInvariant() is "comments" or "flags"
            && args["comments"] == null && args["flags"] == null) return ToolClass.Read;
        // tc_plaintext: export/check only write the text folder (like tc_export); an import with dryRun only plans
        if (cls == ToolClass.ProjectWrite && tool == "tc_plaintext" &&
            !(args?["action"] is JsonValue pa && pa.ToString().Trim().ToLowerInvariant() == "import" &&
              !(args["dryRun"] is JsonValue pd && pd.ToString().Equals("true", StringComparison.OrdinalIgnoreCase))))
            return ToolClass.Read;
        // a static analysis run only reads - loading a settings file (.csa) changes the PLC project's settings
        if (tool == "tc_static_analysis" && args?["settingsFile"] is JsonValue sf && sf.ToString().Trim().Length > 0) return ToolClass.ProjectWrite;
        return cls;
    }

    /// <summary>File the audit entries of this process go to (TCAGENT_AUDIT or %LOCALAPPDATA%\TcAgent\audit.jsonl).</summary>
    public string AuditPath => auditPath;

    public string Describe() =>
        $"Mode: {Mode} ({ModeText})\nAllowed target systems: {string.Join(", ", allowedTargets)}\nAudit log: {auditPath}";

    string ModeText => Mode switch
    {
        AgentMode.Ask => "read only",
        AgentMode.Propose => "proposals only: tc_plc_write/tc_plc_create return a diff, nothing is changed",
        _ => "changes allowed, runtime operations only against allowed targets"
    };

    /// <summary>In propose mode, code changes are only returned as a diff.</summary>
    public bool ForceDryRun => Mode == AgentMode.Propose;

    /// <summary>Throws an exception if the call is not allowed. targetNetId = target for runtime operations.</summary>
    public void Check(string tool, Func<string> targetNetId, JsonObject args = null)
    {
        var cls = ClassOf(tool, args);
        if (cls != ToolClass.Read && Mode == AgentMode.Ask)
            throw new UnauthorizedAccessException($"{tool} blocked: TcAgent is running in mode 'ask' (read only).");
        // TwinSAFE configuration is never changed by TcAgent (tc_safety is read only): no write tool may address the TISC subtree
        if (cls != ToolClass.Read && args != null &&
            args.Any(kv => kv.Value is JsonValue jv && System.Text.RegularExpressions.Regex.IsMatch(jv.ToString(), @"(?:^|\n|=>)\s*TISC(?:\^|\s*$)",
                                                                                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)))
            throw new UnauthorizedAccessException($"{tool} blocked: the TwinSAFE configuration (TISC) is read only in TcAgent - change, verify and download it in the TwinSAFE editor.");
        if (cls != ToolClass.Read && Mode == AgentMode.Propose && tool is not ("tc_plc_write" or "tc_plc_create" or "tc_plaintext"))   // these only return a proposal (DryRun)
            throw new UnauthorizedAccessException($"{tool} blocked: TcAgent is running in mode 'propose' - changes only as a proposal via tc_plc_write/tc_plc_create (diff).");
        if (cls == ToolClass.RuntimeWrite)
        {
            var target = targetNetId();
            if (string.IsNullOrEmpty(target) || target.Equals("local", StringComparison.OrdinalIgnoreCase)) target = "127.0.0.1.1.1";
            if (!allowedTargets.Contains(target))
                throw new UnauthorizedAccessException(
                    $"{tool} blocked: target system {target} is not allowed. Allowed: {string.Join(", ", allowedTargets)}. " +
                    "Allow it via TCAGENT_ALLOW_TARGETS in the MCP configuration.");
        }
    }

    /// <summary>One line per changing call (and per failed call). note = e.g. "completed after an IDE dialog", "job ... finished".</summary>
    public void Audit(string tool, JsonObject args, bool ok, string resultOrError, long ms, string note = null)
    {
        try
        {
            var a = (args ?? new JsonObject()).DeepClone().AsObject();
            foreach (var k in a.Select(p => p.Key).ToList())   // shorten long texts (code, XML), never log secrets
                if (k.Contains("password", StringComparison.OrdinalIgnoreCase) || k.Contains("secret", StringComparison.OrdinalIgnoreCase)) a[k] = "***";
                else if (a[k] is JsonValue v && v.ToString().Length > 300) a[k] = v.ToString()[..300] + "...";
            var line = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("o"), ["tool"] = tool, ["class"] = ClassOf(tool, args).ToString(),
                ["ok"] = ok, ["ms"] = ms, ["args"] = a,
                ["result"] = resultOrError == null ? "" : resultOrError.Length > 300 ? resultOrError[..300] + "..." : resultOrError,
                ["pid"] = Environment.ProcessId, ["client"] = Program.ClientName
            };
            if (note != null) line["note"] = note;
            // shared append with retries: several TcAgent processes (chat, Claude, CLI) write the same audit file
            SharedFile.Append(auditPath, line.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        }
        catch (Exception ex) { Log.Error("Audit: " + ex.Message); }
    }

    // Own AmsNetId from the TwinCAT registry (6 bytes)
    static string ReadLocalNetId()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\System", @"SOFTWARE\Beckhoff\TwinCAT3\System" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("AmsNetId") is byte[] b && b.Length == 6) return string.Join(".", b);
            }
            catch { }
        }
        return null;
    }
}
