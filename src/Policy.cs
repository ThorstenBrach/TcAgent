// Leitplanken: Zielsystem-Whitelist fuer Laufzeit-Eingriffe, optionaler Nur-Lese-Modus und Audit-Log.
// Konfiguration ueber Umgebungsvariablen (in der MCP-Konfiguration unter "env"):
//   TCAGENT_ALLOW_TARGETS  Komma-Liste erlaubter AmsNetIds fuer Aktivieren/Online/ADS-Schreiben.
//                          "local" (Standard) = nur dieser PC (127.0.0.1.1.1 bzw. eigene NetId).
//   TCAGENT_MODE           agent (Standard) = alles im Rahmen der Whitelist
//                          propose = nichts aendern; tc_plc_write/tc_plc_create liefern nur einen Diff (dryRun)
//                          ask     = nur lesen (keine Projekt- und keine Laufzeitaenderungen)
//   TCAGENT_READONLY       1/true = wie TCAGENT_MODE=ask (aeltere Schreibweise).
//   TCAGENT_AUDIT          Pfad des Audit-Logs (Standard %LOCALAPPDATA%\TcAgent\audit.jsonl)
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
        Log.Info($"Policy: Ziele=[{string.Join(", ", allowedTargets)}] modus={Mode} audit={auditPath}");
    }

    public static ToolClass ClassOf(string tool) => Classes.TryGetValue(tool, out var c) ? c : ToolClass.Read;

    public string Describe() =>
        $"Modus: {Mode} ({ModeText})\nErlaubte Zielsysteme: {string.Join(", ", allowedTargets)}\nAudit-Log: {auditPath}";

    string ModeText => Mode switch
    {
        AgentMode.Ask => "nur lesen",
        AgentMode.Propose => "nur Vorschlaege: tc_plc_write/tc_plc_create liefern einen Diff, nichts wird geaendert",
        _ => "Aenderungen erlaubt, Laufzeit nur gegen freigegebene Ziele"
    };

    /// <summary>Im Vorschlagsmodus werden Code-Aenderungen nur als Diff geliefert.</summary>
    public bool ForceDryRun => Mode == AgentMode.Propose;

    /// <summary>Wirft eine Exception, wenn der Aufruf nicht erlaubt ist. targetNetId = Ziel bei Laufzeit-Eingriffen.</summary>
    public void Check(string tool, Func<string> targetNetId)
    {
        var cls = ClassOf(tool);
        if (cls != ToolClass.Read && Mode == AgentMode.Ask)
            throw new UnauthorizedAccessException($"{tool} blockiert: TcAgent laeuft im Modus 'ask' (nur lesen).");
        if (cls != ToolClass.Read && Mode == AgentMode.Propose && tool is not ("tc_plc_write" or "tc_plc_create"))
            throw new UnauthorizedAccessException($"{tool} blockiert: TcAgent laeuft im Modus 'propose' - Aenderungen nur als Vorschlag ueber tc_plc_write/tc_plc_create (Diff).");
        if (cls == ToolClass.RuntimeWrite)
        {
            var target = targetNetId();
            if (string.IsNullOrEmpty(target) || target.Equals("local", StringComparison.OrdinalIgnoreCase)) target = "127.0.0.1.1.1";
            if (!allowedTargets.Contains(target))
                throw new UnauthorizedAccessException(
                    $"{tool} blockiert: Zielsystem {target} ist nicht freigegeben. Erlaubt: {string.Join(", ", allowedTargets)}. " +
                    "Freigabe ueber TCAGENT_ALLOW_TARGETS in der MCP-Konfiguration.");
        }
    }

    public void Audit(string tool, JsonObject args, bool ok, string resultOrError, long ms)
    {
        try
        {
            var a = args.DeepClone().AsObject();
            foreach (var k in a.Select(p => p.Key).ToList())   // lange Texte (Code, XML) kuerzen
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

    // Eigene AmsNetId aus der TwinCAT-Registry (6 Bytes)
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
