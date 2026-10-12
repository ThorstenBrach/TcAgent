// Confirmation by the user before runtime actions (activate, PLC start/stop, ADS write, runtime start mode).
// The whitelist (TCAGENT_ALLOW_TARGETS) decides WHERE the agent may act - this decides WHETHER a person agrees each time.
//
//   TCAGENT_CONFIRM   runtime (default) = ask before runtime actions
//                     all               = also before project changes (code, I/O, tasks, deleting ...)
//                     off               = never ask (pipelines, unattended agents)
//
// Channel: from the TcAgent Chat a Yes/No dialog in Visual Studio (TcAgentBridge); in other clients the client's own
// question dialog (MCP elicitation), if it supports one. Without any channel the call runs as before (only the whitelist).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Confirm
{
    public enum Level { Off, Runtime, All }

    public static Level Configured => (Environment.GetEnvironmentVariable("TCAGENT_CONFIRM") ?? "runtime").Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" or "no" => Level.Off,
        "all" or "project" => Level.All,
        _ => Level.Runtime
    };

    public static bool Needed(string tool, JsonObject args)
    {
        var cls = Policy.ClassOf(tool, args);
        if (cls == ToolClass.Read) return false;
        if (args?["dryRun"] is JsonValue v && v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        if (tool == "tc_runtime_startup" && string.IsNullOrEmpty(args?["mode"]?.ToString())) return false;   // only shows the mode
        return Configured switch
        {
            Level.Off => false,
            Level.All => true,
            _ => cls == ToolClass.RuntimeWrite
        };
    }

    /// <summary>true = go ahead. null = nobody could be asked (no channel).</summary>
    public static bool? Ask(string tool, string what, int idePid)
    {
        bool fromChat = (Environment.GetEnvironmentVariable("TCAGENT_CLIENT") ?? "").StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                        || TwinCatSession.StartedFrom(idePid);
        if (fromChat && BridgeClient.Available(idePid))
        {
            try
            {
                var r = BridgeClient.Call(idePid, "confirm", new JsonObject { ["title"] = "TcAgent - " + tool, ["text"] = what }, 15 * 60_000);
                return r["confirmed"]?.GetValue<bool>() == true;
            }
            // no answer within the time limit (dialog still open / user away) = no - never run the action unconfirmed
            catch (TimeoutException ex) { Log.Error("Confirmation via the bridge: " + ex.Message); return false; }
            catch (Exception ex) { Log.Error("Confirmation via the bridge: " + ex.Message); }
        }
        if (Mcp.CanElicit)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["confirm"] = new JsonObject { ["type"] = "boolean", ["title"] = "Execute", ["description"] = what, ["default"] = false }
                },
                ["required"] = new JsonArray("confirm")
            };
            var answer = Mcp.Elicit("TcAgent wants to execute: " + what, schema);
            // a client that offers elicitation but gets no answer/decline -> no
            return answer?["confirm"] is JsonValue c && c.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>Readable description of the action for the question.</summary>
    public static string Describe(string tool, JsonObject a, string target)
    {
        string S(string k) => a?[k] is JsonValue v ? v.ToString() : null;
        bool B(string k) => S(k)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var on = string.IsNullOrEmpty(target) ? "" : $" on target system {target}";
        switch (tool)
        {
            case "tc_activate":
                return $"Activate configuration{on}" + (a?["restart"] is JsonValue r && r.ToString() == "false" ? "" : " and restart TwinCAT in Run mode") + ".";
            case "tc_plc_online":
                var cmds = new[] { "login", "stop", "start", "logout" }.Where(B).ToList();
                return $"PLC {S("plcProjectPath")}: {string.Join(", ", cmds)}{on}.";
            case "ads_write":
                return $"Write variable {S("symbol")} := {S("value")}{(string.IsNullOrEmpty(target) ? " (local PLC)" : on)}, port {S("port") ?? "851"}.";
            case "tc_runtime_mode":
                return S("mode") == "config" ? $"Switch TwinCAT{on} to Config mode - running PLC programs will be stopped."
                                             : $"Restart TwinCAT{on} in Run mode with the active configuration.";
            case "tc_route_add":
                return $"Add ADS route '{S("name")}' to {S("netId")} ({S("ip") ?? S("host")}) - also stored on the target system.";
            case "tc_packages_change":
                return $"TwinCAT Package Manager on this PC: {S("action")} {S("packages") ?? S("name")}" +
                       (B("allowDowngrade") ? " (downgrade allowed)" : "") + " - runs with administrator rights (Windows asks again).";
            case "tc_runtime_startup":
                return $"Set the start mode of the local TwinCAT runtime to '{S("mode")}' (takes effect from the next boot).";
            default:
                var args = a == null ? "" : string.Join(", ", a.Where(p => p.Value is JsonValue).Select(p =>
                {
                    var v = p.Key.Contains("password", StringComparison.OrdinalIgnoreCase) ? "***" : p.Value!.ToString();
                    return $"{p.Key}={(v.Length > 80 ? v[..80] + "..." : v)}";
                }));
                return $"{tool}({args}){on}";
        }
    }
}
