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
        ["tc_coding_rules_add"] = ToolClass.ProjectWrite, ["tc_docs_add"] = ToolClass.ProjectWrite, ["tc_io_add"] = ToolClass.ProjectWrite, ["tc_task"] = ToolClass.ProjectWrite, ["tc_boot_project"] = ToolClass.ProjectWrite, ["tc_dialogs"] = ToolClass.Read,
        ["tc_runtime_startup"] = ToolClass.RuntimeWrite, ["tc_runtime_mode"] = ToolClass.RuntimeWrite, ["tc_io_scan_add"] = ToolClass.ProjectWrite,
        ["tc_activate"] = ToolClass.RuntimeWrite, ["tc_plc_online"] = ToolClass.RuntimeWrite,
        ["ads_write"] = ToolClass.RuntimeWrite,
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

    public static ToolClass ClassOf(string tool) => Classes.TryGetValue(tool, out var c) ? c : ToolClass.Read;

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
    public void Check(string tool, Func<string> targetNetId)
    {
        var cls = ClassOf(tool);
        if (cls != ToolClass.Read && Mode == AgentMode.Ask)
            throw new UnauthorizedAccessException($"{tool} blocked: TcAgent is running in mode 'ask' (read only).");
        if (cls != ToolClass.Read && Mode == AgentMode.Propose && tool is not ("tc_plc_write" or "tc_plc_create"))
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

    public void Audit(string tool, JsonObject args, bool ok, string resultOrError, long ms)
    {
        try
        {
            var a = args.DeepClone().AsObject();
            foreach (var k in a.Select(p => p.Key).ToList())   // shorten long texts (code, XML)
                if (a[k] is JsonValue v && v.ToString().Length > 300) a[k] = v.ToString()[..300] + "...";
            var line = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("o"), ["tool"] = tool, ["class"] = ClassOf(tool).ToString(),
                ["ok"] = ok, ["ms"] = ms, ["args"] = a,
                ["result"] = resultOrError.Length > 300 ? resultOrError[..300] + "..." : resultOrError
            };
            File.AppendAllText(auditPath, line.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
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
