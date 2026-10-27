// Confirmation by the user before runtime actions (activate, PLC start/stop, ADS write, runtime start mode).
// The whitelist (TCAGENT_ALLOW_TARGETS) decides WHERE the agent may act - this decides WHETHER a person agrees each time.
//
//   TCAGENT_CONFIRM   runtime (default) = ask before runtime actions
//                     all               = also before project changes (code, I/O, tasks, deleting ...)
//                     off               = never ask (pipelines, unattended agents)
//
// Channel: from the TcAgent Chat a Yes/No dialog in Visual Studio (TcAgentBridge); in other clients the client's own
// question dialog (MCP elicitation), if it supports one. Without any channel the call is refused: running without a
// question needs the deliberate setting TCAGENT_CONFIRM=off (pipelines, unattended agents).
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
        if (tool == "tc_motion" && args?["dryRun"] == null) return false;   // action=link is a dry run unless dryRun=false
        if (tool == "tc_refactor") return false;     // asks per change itself (Apply / Apply all / Reject / Cancel)
        if (tool == "tc_improve") return false;      // asks itself, with the diff of the change
        if (tool == "tc_sources" && args?["action"]?.ToString() is "install" or "pull" or "promote") return false;   // asks itself: files, scripts, license
        return Configured switch
        {
            Level.Off => false,
            Level.All => true,
            _ => cls == ToolClass.RuntimeWrite
        };
    }

    /// <summary>true after Ask returned false because the client could not show the question (not the user's "no").</summary>
    public static bool LastNotShown;

    /// <summary>true = go ahead. null = nobody could be asked (no channel).</summary>
    public static bool? Ask(string tool, string what, int idePid)
    {
        LastNotShown = false;
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
            // a foreign program behind the pipe name = no, not "nobody could be asked"
            catch (UntrustedBridgeException ex) { Log.Error("Confirmation via the bridge: " + ex.Message); return false; }
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
            // a client that offers elicitation but gets no answer/decline -> no (never run unconfirmed). If the question
            // was evidently not on screen (immediate cancel/decline or an error), say so instead of "rejected by the user".
            LastNotShown = answer == null && !(Mcp.LastElicitOutcome is "decline" or "cancel" && Mcp.LastElicitMs >= 1500)
                           && Mcp.LastElicitOutcome != "timeout";
            return answer?["confirm"] is JsonValue c && c.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>Question with the answers apply / all (apply this and every following one) / reject / cancel (stop).
    /// Returns null if nobody can be asked. In the TcAgent Chat (or when the client cannot ask) a dialog in Visual Studio,
    /// otherwise the client's own question (MCP elicitation). No answer = cancel.</summary>
    public static string Choose(string title, string text, int idePid)
    {
        bool fromChat = (Environment.GetEnvironmentVariable("TCAGENT_CLIENT") ?? "").StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                        || TwinCatSession.StartedFrom(idePid);
        string ViaBridge()
        {
            if (!BridgeClient.Available(idePid)) return null;
            try
            {
                var r = BridgeClient.Call(idePid, "choice", new JsonObject { ["title"] = title, ["text"] = text }, 15 * 60_000);
                return r["choice"]?.ToString();
            }
            catch (TimeoutException ex) { Log.Error("Choice via the bridge: " + ex.Message); return "cancel"; }
            catch (UntrustedBridgeException ex) { Log.Error("Choice via the bridge: " + ex.Message); return "cancel"; }
            catch (Exception ex) { Log.Error("Choice via the bridge: " + ex.Message); return null; }     // e.g. older bridge without 'choice'
        }
        string ViaClient()
        {
            if (!Mcp.CanElicit) return null;
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["decision"] = new JsonObject
                    {
                        ["type"] = "string", ["title"] = "Decision",
                        ["enum"] = new JsonArray("apply", "all", "reject", "cancel"),
                        ["enumNames"] = new JsonArray("Apply", "Apply all (this and all following)", "Reject", "Cancel (stop)")
                    }
                },
                ["required"] = new JsonArray("decision")
            };
            var answer = Mcp.Elicit(text, schema);
            if (answer?["decision"]?.ToString() is { Length: > 0 } d) return d;
            // decline/cancel only count as the user's answer if the question was really on screen: a client that cannot show
            // it (CLI in non-interactive mode, e.g. the TcAgent Chat backend) answers at once or with an error
            if (Mcp.LastElicitOutcome is "decline" or "cancel" && Mcp.LastElicitMs >= 1500) return "cancel";
            Log.Info($"Choice via the client not shown ({Mcp.LastElicitOutcome} after {Mcp.LastElicitMs} ms)");
            return null;
        }
        var choice = fromChat ? ViaBridge() ?? ViaClient() : ViaClient() ?? ViaBridge();
        return choice is "apply" or "all" or "reject" or "cancel" ? choice : choice == null ? null : "cancel";
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
            case "tc_backup":
                return $"Restore runtime data from {Path.GetFileName(S("file") ?? "?")}{(string.IsNullOrEmpty(target) ? " to the local PLC" : on)}, port {S("port") ?? "851"}: " +
                       "every stored value that differs is written" + (B("whileRunning") ? " - while the PLC is running" : "") + ".";
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
            case "tc_test_run":
                return $"Run TcUnit tests {(string.IsNullOrWhiteSpace(S("tests")) ? "(all)" : S("tests"))}{(string.IsNullOrEmpty(target) ? " on the local runtime" : on)}: " +
                       "build and activate if the code changed, reset and start the PLC, write the test selection via ADS.";
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
