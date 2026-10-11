// TcAgent - MCP-Server zur Fernsteuerung von TwinCAT 3 (XAE in Visual Studio 2022 / TcXaeShell)
// Transport: MCP ueber stdio (JSON-RPC 2.0, eine Nachricht pro Zeile). Log -> stderr.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Program
{
    public const string ServerVersion = "0.13.0";
    public static string ClientName = "unbekannt";

    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        // Kommandozeile fuer CI-Pipelines (check/build/test) - ohne Argumente MCP-Server
        if (Cli.IsCommand(args)) { Console.OutputEncoding = new UTF8Encoding(false); return Cli.Run(args); }
        Mcp.Start();
        Log.Info($"TcAgent {ServerVersion} gestartet (pid {Environment.ProcessId})");

        var sta = new StaWorker();
        var tools = new ToolRegistry(new TwinCatSession(sta));

        string line;
        while ((line = Mcp.Next()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject req;
            try { req = JsonNode.Parse(line)!.AsObject(); }
            catch (Exception ex) { Log.Error("Ungueltiges JSON: " + ex.Message); continue; }

            var id = req["id"]?.DeepClone();
            var method = req["method"]?.GetValue<string>();
            var prm = req["params"] as JsonObject ?? new JsonObject();
            if (id == null) continue; // Notification (z. B. notifications/initialized)

            JsonNode result = null; JsonObject error = null;
            try
            {
                switch (method)
                {
                    case "initialize":
                        ClientName = $"{prm["clientInfo"]?["name"]} {prm["clientInfo"]?["version"]}".Trim();
                        Mcp.CanElicit = prm["capabilities"]?["elicitation"] != null;
                        result = new JsonObject
                        {
                            ["protocolVersion"] = prm["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "tcagent", ["version"] = ServerVersion },
                            ["instructions"] = "Steuert TwinCAT 3 ueber das Automation Interface. Zuerst tc_attach (laufende Instanz) oder tc_open aufrufen. " +
                                               "Baumpfade sind TwinCAT-Pfade mit ^ als Trenner, z. B. 'TIPC^MyPlc^MyPlc Project^POUs^MAIN'. " +
                                               "Vor dem ersten Schreiben von Code tc_coding_rules aufrufen (Codierrichtlinien des Projekts) und einhalten. " +
                                               "Wenn ein Werkzeug fehlschlaegt, unerwartet reagiert oder fehlt: tc_feedback aufrufen."
                        };
                        break;
                    case "ping":
                        result = new JsonObject();
                        break;
                    case "tools/list":
                        result = new JsonObject { ["tools"] = tools.List() };
                        break;
                    case "tools/call":
                        result = tools.Call(prm["name"]!.GetValue<string>(), prm["arguments"] as JsonObject ?? new JsonObject());
                        break;
                    default:
                        error = new JsonObject { ["code"] = -32601, ["message"] = "Methode unbekannt: " + method };
                        break;
                }
            }
            catch (Exception ex)
            {
                error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
            }

            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            if (error != null) resp["error"] = error; else resp["result"] = result;
            Mcp.Send(resp);
        }
        sta.Dispose();
        return 0;
    }
}

/// <summary>stdio-Transport. Eigener Lesethread, damit ein Werkzeug waehrend seiner Ausfuehrung den Client fragen kann
/// (MCP-Elicitation, z. B. Auswahl der IDE-Instanz); dabei eintreffende andere Nachrichten werden fuer die Hauptschleife zurueckgelegt.</summary>
public static class Mcp
{
    static readonly System.Collections.Concurrent.BlockingCollection<string> inbox = new();
    static readonly Queue<string> deferred = new();
    static readonly object writeLock = new();
    static StreamWriter stdout;
    static int nextId;
    public static bool CanElicit;

    public static void Start()
    {
        stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        new Thread(() =>
        {
            string l;
            while ((l = Console.In.ReadLine()) != null) inbox.Add(l);
            inbox.CompleteAdding();
        }) { IsBackground = true, Name = "stdin" }.Start();
    }

    public static string Next()
    {
        lock (deferred) if (deferred.Count > 0) return deferred.Dequeue();
        return inbox.TryTake(out var l, Timeout.Infinite) ? l : null;
    }

    public static void Send(JsonObject msg) { lock (writeLock) stdout.WriteLine(msg.ToJsonString()); }

    /// <summary>Fragt den Benutzer ueber den Client (elicitation/create). null = nicht unterstuetzt, abgelehnt oder keine Antwort.</summary>
    public static JsonObject Elicit(string message, JsonObject schema, int timeoutMs = 300_000)
    {
        if (!CanElicit) return null;
        var id = "tcagent-elicit-" + Interlocked.Increment(ref nextId);
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "elicitation/create",
            ["params"] = new JsonObject { ["message"] = message, ["requestedSchema"] = schema }
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!inbox.TryTake(out var l, (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds))) break;
            JsonObject m = null;
            try { m = JsonNode.Parse(l) as JsonObject; } catch { }
            if (m != null && m["method"] == null && m["id"]?.ToString() == id)
            {
                if (m["error"] != null) { Log.Info("Elicitation abgelehnt: " + m["error"]?.ToJsonString()); return null; }
                var r = m["result"] as JsonObject;
                return r?["action"]?.ToString() == "accept" ? r["content"] as JsonObject : null;
            }
            lock (deferred) deferred.Enqueue(l);
        }
        Log.Info("Elicitation ohne Antwort");
        return null;
    }
}

public static class Log
{
    public static void Info(string m) => Console.Error.WriteLine($"[tcagent {DateTime.Now:HH:mm:ss}] {m}");
    public static void Error(string m) => Console.Error.WriteLine($"[tcagent {DateTime.Now:HH:mm:ss}] FEHLER {m}");
}
