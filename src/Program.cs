// TcAgent - MCP server for remote control of TwinCAT 3 (XAE in Visual Studio 2022 / TcXaeShell)
// Transport: MCP over stdio (JSON-RPC 2.0, one message per line). Log -> stderr.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Program
{
    public const string ServerVersion = "0.23.2";
    public static string ClientName = "unknown";

    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        // Command line for CI pipelines (check/build/test) - without arguments: MCP server
        if (Cli.IsCommand(args)) { Console.OutputEncoding = new UTF8Encoding(false); return Cli.Run(args); }
        Mcp.Start();
        Log.Info($"TcAgent {ServerVersion} started (pid {Environment.ProcessId})");

        var sta = new StaWorker();
        var tools = new ToolRegistry(new TwinCatSession(sta));

        string line;
        while ((line = Mcp.Next()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject req;
            try { req = JsonNode.Parse(line)!.AsObject(); }
            catch (Exception ex) { Log.Error("Invalid JSON: " + ex.Message); continue; }

            var id = req["id"]?.DeepClone();
            var method = req["method"]?.GetValue<string>();
            var prm = req["params"] as JsonObject ?? new JsonObject();
            if (id == null) continue; // Notification (e.g. notifications/initialized)
            if (method == null) continue; // late answer to a request of ours (e.g. an elicitation that timed out) - not a request

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
                            ["instructions"] = "Controls TwinCAT 3 via the Automation Interface. Call tc_attach (running instance) or tc_open first. " +
                                               "Tree paths are TwinCAT paths with ^ as separator, e.g. 'TIPC^MyPlc^MyPlc Project^POUs^MAIN'. " +
                                               "Before writing code for the first time, call tc_coding_rules (the project's coding guidelines) and follow them. " +
                                               "If a tool fails, behaves unexpectedly or is missing: call tc_feedback."
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
                        error = new JsonObject { ["code"] = -32601, ["message"] = "Unknown method: " + method };
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

/// <summary>stdio transport. Separate reader thread so that a tool can ask the client during its execution
/// (MCP elicitation, e.g. selecting the IDE instance); other messages arriving meanwhile are set aside for the main loop.</summary>
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

    // answers to our own elicitation requests: whoever reads a line from stdin (main loop or a waiting Elicit, e.g. of a
    // background tc_refactor job) hands an answer over to the thread that waits for it
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> waiting = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> answers = new();

    static bool RouteAnswer(string line)
    {
        if (waiting.IsEmpty || line.IndexOf("tcagent-elicit-", StringComparison.Ordinal) < 0) return false;
        JsonObject m = null;
        try { m = JsonNode.Parse(line) as JsonObject; } catch { }
        var id = m?["id"]?.ToString();
        if (m == null || m["method"] != null || id == null || !waiting.ContainsKey(id)) return false;
        answers[id] = m;
        return true;
    }

    public static string Next()
    {
        while (true)
        {
            lock (deferred) if (deferred.Count > 0) return deferred.Dequeue();
            // short timeout: lines set aside by a concurrently waiting Elicit (deferred) are picked up as well
            if (!inbox.TryTake(out var l, 500)) { if (inbox.IsCompleted) { lock (deferred) return deferred.Count > 0 ? deferred.Dequeue() : null; } continue; }
            if (!RouteAnswer(l)) return l;
        }
    }

    public static void Send(JsonObject msg) { lock (writeLock) stdout.WriteLine(msg.ToJsonString()); }

    /// <summary>Outcome of the last Elicit call: accept, decline, cancel, error, timeout or unsupported - and how long it took.
    /// A client that cannot show the question (e.g. a CLI in non-interactive mode) answers at once with cancel/decline or an
    /// error; callers must not take that for a decision of the user.</summary>
    public static string LastElicitOutcome = "unsupported";
    public static long LastElicitMs;

    /// <summary>Asks the user via the client (elicitation/create). null = not supported, declined or no answer.
    /// Works from the tool call itself and from a background job (the main loop then forwards the answer).</summary>
    public static JsonObject Elicit(string message, JsonObject schema, int timeoutMs = 300_000)
    {
        LastElicitOutcome = "unsupported"; LastElicitMs = 0;
        if (!CanElicit) return null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try { return ElicitCore(message, schema, timeoutMs); }
        finally { LastElicitMs = started.ElapsedMilliseconds; }
    }

    static JsonObject ElicitCore(string message, JsonObject schema, int timeoutMs)
    {
        var id = "tcagent-elicit-" + Interlocked.Increment(ref nextId);
        waiting[id] = 0;
        try
        {
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "elicitation/create",
                ["params"] = new JsonObject { ["message"] = message, ["requestedSchema"] = schema }
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (answers.TryRemove(id, out var a)) return Evaluate(a);
                if (!inbox.TryTake(out var l, 200)) { if (inbox.IsCompleted) break; continue; }
                if (RouteAnswer(l)) continue;
                lock (deferred) deferred.Enqueue(l);
            }
            if (answers.TryRemove(id, out var late)) return Evaluate(late);
            Log.Info("Elicitation without answer");
            LastElicitOutcome = "timeout";
            return null;
        }
        finally { waiting.TryRemove(id, out _); answers.TryRemove(id, out _); }

        static JsonObject Evaluate(JsonObject m)
        {
            if (m["error"] != null) { Log.Info("Elicitation failed: " + m["error"]?.ToJsonString()); LastElicitOutcome = "error"; return null; }
            var r = m["result"] as JsonObject;
            LastElicitOutcome = r?["action"]?.ToString() ?? "error";
            return LastElicitOutcome == "accept" ? r["content"] as JsonObject : null;
        }
    }
}

/// <summary>stderr (shown by the MCP client) and a daily file %LOCALAPPDATA%\TcAgent\logs\tcagent-yyyyMMdd.log
/// (all TcAgent processes, marked with their pid) - so problems can be traced afterwards, e.g. after an IDE crash.</summary>
public static class Log
{
    static readonly object gate = new();
    static readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "logs");
    static readonly string client = Environment.GetEnvironmentVariable("TCAGENT_CLIENT") ?? "";

    public static void Info(string m) => Write(m);
    public static void Error(string m) => Write("ERROR " + m);

    static void Write(string m)
    {
        var now = DateTime.Now;
        Console.Error.WriteLine($"[tcagent {now:HH:mm:ss}] {m}");
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(dir);
                // shared append with retries: several TcAgent processes (chat, other clients, CLI) write the same daily file
                SharedFile.Append(Path.Combine(dir, $"tcagent-{now:yyyyMMdd}.log"),
                    $"{now:HH:mm:ss.fff} [{Environment.ProcessId}{(client.Length > 0 ? " " + client : "")}] {m}{Environment.NewLine}", 5);
            }
        }
        catch { /* file still locked after the retries - stderr has it */ }
    }
}
