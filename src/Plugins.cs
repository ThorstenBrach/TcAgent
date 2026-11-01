// Plugins: own tools for TcAgent as separate programs that speak MCP over stdin/stdout (any language).
//
// A plugin is a folder with a plugin.json below "plugins" on a level of the knowledge:
//   company <TCAGENT_COMPANY>\plugins, team <TCAGENT_TEAM>\plugins, user %LOCALAPPDATA%\TcAgent\plugins,
//   project <solution>\.tcagent\plugins
//   {
//     "id": "line-check",                 lower-case letters, digits, "-"
//     "name": "Line check", "description": "...", "version": "1.0.0",
//     "command": "python", "args": ["server.py"],          relative paths: in the plugin folder
//     "tools": { "count_lines": "read", "fix_header": "write", "reset_counter": "runtime" },
//     "timeoutSec": 600
//   }
// TcAgent starts the program, lists its tools (MCP tools/list) and offers them as x_<id>_<tool> ("-" becomes "_"). Every call
// goes through the policy, the confirmation and the audit log like a built-in tool, with the class declared in "tools"; a tool
// that is not declared counts as changing and is confirmed before every call. A plugin reaches TwinCAT through TcAgent: the
// request "tcagent/call" {name, arguments} runs a built-in tool with all its checks (target whitelist, guards, backups,
// confirmations) - never another plugin's tool. Nothing stops a separate program from talking to ADS itself; that is why
// plugins of the project and team levels run only after the user approved exactly this content once (TcAgent.exe plugins
// approve), and the company level can forbid plugins or allow only listed ones (modules.json, Modules.cs).
//
// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Plugins
{
    public sealed class Plugin
    {
        public string Id, Name, Description, Version, Command, Folder, Level, Hash, Status = "ready", Reason;
        public List<string> Args = new();
        public Dictionary<string, ToolClass?> Tools = new(StringComparer.OrdinalIgnoreCase);
        public int TimeoutSec = 600;
        public bool Ready => Status == "ready";
        public string Prefix => "x_" + Id.Replace('-', '_') + "_";
    }

    static readonly Regex IdRx = new(@"^[a-z][a-z0-9-]{1,30}$");

    // tool name → plugin id and declared class (filled when plugins are found or started)
    static readonly ConcurrentDictionary<string, string> toolPlugin = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, ToolClass?> toolClass = new(StringComparer.OrdinalIgnoreCase);

    public static string IdOfTool(string tool) => toolPlugin.TryGetValue(tool, out var id) ? id : null;
    public static bool IsPluginTool(string tool) => tool.StartsWith("x_", StringComparison.Ordinal) && toolPlugin.ContainsKey(tool);

    /// <summary>Declared class of a plugin tool; a tool without a declaration counts as changing (and is confirmed).</summary>
    public static ToolClass ClassOf(string tool) => toolClass.TryGetValue(tool, out var c) && c != null ? c.Value : ToolClass.ProjectWrite;
    public static bool Undeclared(string tool) => IsPluginTool(tool) && !(toolClass.TryGetValue(tool, out var c) && c != null);

    static ToolClass? ParseClass(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "read" => ToolClass.Read, "write" => ToolClass.ProjectWrite, "runtime" => ToolClass.RuntimeWrite, _ => null
    };

    // ------------------------------------------------------------------ finding
    /// <summary>All plugin folders on the levels with their status (ready, needs approval, not allowed, invalid, duplicate).
    /// The higher level wins for the same id (company before team before user before project).</summary>
    public static List<Plugin> Found(string solutionDir)
    {
        var list = new List<Plugin>();
        var policy = CompanyPolicy(out var allowed);
        foreach (var l in Modules.Levels(solutionDir))
        {
            var root = Path.Combine(l.Root, "plugins");
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var f = Path.Combine(d, "plugin.json");
                if (!File.Exists(f)) continue;
                var p = new Plugin { Folder = d, Level = l.Name, Id = Path.GetFileName(d).ToLowerInvariant(), Name = Path.GetFileName(d) };
                list.Add(p);
                try { ReadManifest(p, f); }
                catch (Exception ex) { p.Status = "invalid"; p.Reason = "plugin.json: " + ex.Message; continue; }
                if (list.Any(x => x != p && x.Id == p.Id)) { p.Status = "duplicate"; p.Reason = "a plugin with this id is already on " + list.First(x => x.Id == p.Id).Level; continue; }
                try { p.Hash = HashOf(d); } catch (Exception ex) { p.Status = "invalid"; p.Reason = "not readable: " + ex.Message; continue; }
                if (policy == "none") { p.Status = "not allowed"; p.Reason = "the company level allows no plugins"; continue; }
                if (policy == "listed" && !allowed.Contains(p.Hash)) { p.Status = "not allowed"; p.Reason = "not in the allowed plugins of the company level (sha256 " + p.Hash + ")"; continue; }
                if (l.Name is "project" or "team" && !Approved().Contains(p.Hash))
                { p.Status = "needs approval"; p.Reason = $"from the {l.Name} level - runs after TcAgent.exe plugins approve {p.Id}" + (l.Name == "project" ? " --solution <sln>" : ""); continue; }
            }
        }
        foreach (var p in list.Where(p => p.Status is not ("invalid" or "duplicate")))
            foreach (var (t, c) in p.Tools) { toolPlugin[p.Prefix + t] = p.Id; toolClass[p.Prefix + t] = c; }
        return list;
    }

    static void ReadManifest(Plugin p, string file)
    {
        var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception("not a JSON object");
        p.Id = o["id"]?.ToString()?.Trim() ?? throw new Exception("\"id\" is missing");
        if (!IdRx.IsMatch(p.Id)) throw new Exception($"id '{p.Id}': lower-case letters, digits and '-', 2-31 characters");
        p.Name = o["name"]?.ToString() ?? p.Id;
        p.Description = o["description"]?.ToString();
        p.Version = o["version"]?.ToString();
        p.Command = o["command"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(p.Command)) throw new Exception("\"command\" is missing");
        if (o["args"] is JsonArray a) p.Args = a.Select(x => x?.ToString() ?? "").ToList();
        if (o["timeoutSec"] is JsonValue tv && tv.TryGetValue<int>(out var ts) && ts > 0) p.TimeoutSec = Math.Min(ts, 3600);
        if (o["tools"] is JsonObject tools)
            foreach (var (name, cls) in tools)
            {
                if (!Regex.IsMatch(name, @"^[A-Za-z0-9_]{1,40}$")) throw new Exception($"tool '{name}': letters, digits, _");
                var c = ParseClass(cls?.ToString());
                if (c == null) throw new Exception($"tool '{name}': class read, write or runtime");
                p.Tools[name] = c;
            }
        if ((p.Prefix + "x").Length + p.Tools.Keys.DefaultIfEmpty("").Max(k => k.Length) > 64) throw new Exception("id + tool name longer than 64 characters");
    }

    /// <summary>SHA-256 over the relative paths and contents of all files of the plugin folder (without .git) - an approval
    /// counts for exactly this content.</summary>
    public static string HashOf(string folder)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false })
                             .Where(f => !f.Substring(folder.Length).Split('\\', '/').Any(s => s is ".git" or "__pycache__"))
                             .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count > 5000) throw new Exception("more than 5000 files");
        long total = 0;
        foreach (var f in files)
        {
            var rel = Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, f).Replace('\\', '/') + "\n");
            sha.TransformBlock(rel, 0, rel.Length, null, 0);
            var bytes = File.ReadAllBytes(f);
            total += bytes.Length;
            if (total > 500L * 1024 * 1024) throw new Exception("more than 500 MB");
            var len = Encoding.UTF8.GetBytes(bytes.Length + "\n");
            sha.TransformBlock(len, 0, len.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>"all" (default), "none" or "listed" - from the company level's modules.json.</summary>
    static string CompanyPolicy(out HashSet<string> allowed)
    {
        allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var company = Modules.Levels(null).FirstOrDefault(l => l.Name == "company");
        var o = company == null ? null : Modules.Read(company);
        if (o?["allowedPlugins"] is JsonArray a) foreach (var x in a) if (x?.ToString() is { } h) allowed.Add(h.Trim());
        var p = o?["plugins"]?.ToString()?.Trim().ToLowerInvariant();
        return p is "none" or "listed" ? p : "all";
    }

    // ------------------------------------------------------------------ approvals (per Windows user)
    static string ApprovalsFile => Path.Combine(KnowledgeLayers.UserRoot, "plugins-approved.json");

    static HashSet<string> Approved()
    {
        try { return (JsonNode.Parse(File.ReadAllText(ApprovalsFile)) as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToHashSet(StringComparer.OrdinalIgnoreCase)! ?? new(); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static void SaveApproved(HashSet<string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ApprovalsFile)!);
        File.WriteAllText(ApprovalsFile, new JsonArray(all.Order().Select(x => (JsonNode)x).ToArray()).ToJsonString(), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ running plugins
    /// <summary>The started plugins of one TcAgent process (MCP server); restarted when the solution (project level) changes.</summary>
    public sealed class Manager : IDisposable
    {
        readonly Func<string, JsonObject, JsonObject> callBuiltIn;
        readonly Dictionary<string, Host> hosts = new(StringComparer.OrdinalIgnoreCase);   // by hash
        public Manager(Func<string, JsonObject, JsonObject> callBuiltIn) { this.callBuiltIn = callBuiltIn; }

        public List<Plugin> Last = new();

        /// <summary>Starts the ready plugins (of Found) whose module is on (in parallel), stops the others; returns the running hosts.</summary>
        public List<Host> Sync(string solutionDir, List<Plugin> found, HashSet<string> offModules)
        {
            Last = found;
            var wanted = found.Where(p => p.Ready && !offModules.Contains("plugin:" + p.Id)).ToList();
            lock (hosts)
            {
                foreach (var h in hosts.Values.Where(h => !wanted.Any(w => w.Hash == h.Plugin.Hash)).ToList()) { h.Dispose(); hosts.Remove(h.Plugin.Hash); }
                var start = wanted.Where(w => !hosts.ContainsKey(w.Hash) || hosts[w.Hash].Exited).ToList();
                var started = start.AsParallel().Select(p =>
                {
                    var h = new Host(p, callBuiltIn, solutionDir);
                    try { h.Start(); }
                    catch (Exception ex) { h.Error = ex.Message; Log.Error($"Plugin {p.Id}: {ex.Message}"); }
                    return h;
                }).ToList();
                foreach (var h in started) { if (hosts.TryGetValue(h.Plugin.Hash, out var old)) old.Dispose(); hosts[h.Plugin.Hash] = h; }
                return hosts.Values.Where(h => h.Error == null && !h.Exited).ToList();
            }
        }

        public Host HostOf(string tool)
        {
            lock (hosts) return hosts.Values.FirstOrDefault(h => h.Tools.Any(t => t.Name == tool));
        }

        void StopAll() { lock (hosts) { foreach (var h in hosts.Values) h.Dispose(); hosts.Clear(); } }
        public void Dispose() => StopAll();
    }

    /// <summary>One plugin process: an MCP client on its stdin/stdout.</summary>
    public sealed class Host : IDisposable
    {
        public readonly Plugin Plugin;
        public string Error;
        public List<(string Name, string Description, JsonObject Schema)> Tools = new();
        readonly Func<string, JsonObject, JsonObject> callBuiltIn;
        readonly string solutionDir;
        Process proc;
        readonly object writeLock = new();
        readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
        long nextId;

        public Host(Plugin p, Func<string, JsonObject, JsonObject> callBuiltIn, string solutionDir) { Plugin = p; this.callBuiltIn = callBuiltIn; this.solutionDir = solutionDir; }
        public bool Exited { get { try { return proc == null || proc.HasExited; } catch { return true; } } }

        public void Start()
        {
            var cmd = Plugin.Command;
            var local = Path.Combine(Plugin.Folder, cmd);
            if (File.Exists(local)) cmd = local;
            var psi = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Plugin.Folder, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false)
            };
            foreach (var a in Plugin.Args) psi.ArgumentList.Add(a);
            psi.Environment["TCAGENT_PLUGIN"] = Plugin.Id;
            psi.Environment["TCAGENT_SOLUTION_DIR"] = solutionDir ?? "";
            proc = Process.Start(psi) ?? throw new Exception("could not be started: " + Plugin.Command);
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"plugin {Plugin.Id}: {e.Data}"); };
            proc.BeginErrorReadLine();
            new Thread(ReadLoop) { IsBackground = true, Name = "plugin-" + Plugin.Id }.Start();
            Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "tcagent", ["version"] = Program.ServerVersion }
            }, 20_000);
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            string cursor = null;
            do
            {
                var r = Request("tools/list", cursor == null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, 20_000);
                foreach (var t in (r["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var name = t["name"]?.ToString();
                    if (name == null || !Regex.IsMatch(name, @"^[A-Za-z0-9_]{1,40}$")) { Log.Error($"Plugin {Plugin.Id}: tool name '{name}' skipped (letters, digits, _)"); continue; }
                    var full = Plugin.Prefix + name;
                    if (full.Length > 64) { Log.Error($"Plugin {Plugin.Id}: {full} longer than 64 characters - skipped"); continue; }
                    Plugin.Tools.TryGetValue(name, out var cls);
                    toolPlugin[full] = Plugin.Id; toolClass[full] = cls;
                    Tools.Add((full, $"[plugin {Plugin.Name}] " + (t["description"]?.ToString() ?? ""),
                               t["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }));
                }
                cursor = r["nextCursor"]?.ToString();
            } while (!string.IsNullOrEmpty(cursor));
            Log.Info($"Plugin {Plugin.Id} {Plugin.Version} ({Plugin.Level}) started: {Tools.Count} tool(s)");
        }

        /// <summary>Calls a tool of the plugin (full name x_...); returns the text of the result, throws on an error result.</summary>
        public string Call(string fullName, JsonObject args)
        {
            var r = Request("tools/call", new JsonObject { ["name"] = fullName.Substring(Plugin.Prefix.Length), ["arguments"] = args.DeepClone() }, Plugin.TimeoutSec * 1000);
            var text = string.Join("\n", (r["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                                           .Select(c => c["type"]?.ToString() == "text" ? c["text"]?.ToString() : $"[{c["type"]} content]"));
            if (r["isError"] is JsonValue e && e.TryGetValue<bool>(out var isErr) && isErr) throw new Exception(text);
            return text;
        }

        JsonObject Request(string method, JsonObject prm, int timeoutMs)
        {
            var id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            try
            {
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prm });
                if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException($"plugin {Plugin.Id}: no answer to {method} within {timeoutMs / 1000} s");
                var m = tcs.Task.Result;
                if (m["error"] is JsonObject err) throw new Exception($"plugin {Plugin.Id}: {err["message"]}");
                return m["result"] as JsonObject ?? new JsonObject();
            }
            catch (AggregateException ae) { throw ae.InnerException ?? ae; }
            finally { pending.TryRemove(id, out _); }
        }

        void Send(JsonObject m)
        {
            lock (writeLock)
            {
                if (Exited) throw new Exception($"plugin {Plugin.Id} has ended");
                proc.StandardInput.WriteLine(m.ToJsonString());
                proc.StandardInput.Flush();
            }
        }

        void ReadLoop()
        {
            try
            {
                for (string line; (line = proc.StandardOutput.ReadLine()) != null;)
                {
                    JsonObject m;
                    try { m = JsonNode.Parse(line) as JsonObject; } catch { Log.Info($"plugin {Plugin.Id} (not JSON): {line}"); continue; }
                    if (m == null) continue;
                    var method = m["method"]?.ToString();
                    if (method == null)
                    {
                        if (m["id"] is JsonValue iv && iv.TryGetValue<long>(out var id) && pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(m);
                        continue;
                    }
                    if (m["id"] == null) continue;                    // a notification of the plugin (log, progress): not used
                    var reqId = m["id"]!.DeepClone();
                    var prm = m["params"] as JsonObject ?? new JsonObject();
                    // a request of the plugin: answered in the background, so its own answers keep flowing
                    Task.Run(() =>
                    {
                        var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = reqId };
                        try
                        {
                            switch (method)
                            {
                                case "ping": resp["result"] = new JsonObject(); break;
                                case "tcagent/call":
                                    var name = prm["name"]?.ToString() ?? throw new Exception("name is missing");
                                    var module = Modules.Of(name);
                                    if (module == null || module.StartsWith("plugin:")) throw new Exception($"{name}: a plugin can call built-in TcAgent tools only");
                                    Log.Info($"plugin {Plugin.Id} calls {name}");
                                    resp["result"] = callBuiltIn(name, prm["arguments"] as JsonObject ?? new JsonObject());
                                    break;
                                default: resp["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unknown method: " + method }; break;
                            }
                        }
                        catch (Exception ex) { resp.Remove("result"); resp["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message }; }
                        try { Send(resp); } catch { }
                    });
                }
            }
            catch { }
            foreach (var p in pending.Values) p.TrySetException(new Exception($"plugin {Plugin.Id} has ended"));
        }

        public void Dispose()
        {
            try { if (!Exited) { proc.StandardInput.Close(); if (!proc.WaitForExit(2000)) proc.Kill(true); } } catch { }
        }
    }

    // ------------------------------------------------------------------ command line
    // TcAgent.exe plugins [list] [--solution <sln>] [--json]
    // TcAgent.exe plugins approve|revoke <id> [--solution <sln>]
    public static int RunCli(string[] args)
    {
        string sub = "list", id = null, sln = null; bool json = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a is "--solution" or "-s") { sln = i + 1 < args.Length ? args[++i] : throw new Exception("--solution needs a value"); continue; }
                if (a == "--json") { json = true; continue; }
                if (a.StartsWith("--")) throw new Exception("unknown option " + a);
                if (i == 1 && a.ToLowerInvariant() is "list" or "approve" or "revoke") sub = a.ToLowerInvariant();
                else if (id == null) id = a.Trim().ToLowerInvariant();
                else throw new Exception("too many arguments: " + a);
            }
            if (sln != null && !File.Exists(sln)) throw new Exception("Solution not found: " + sln);
            var dir = sln != null ? Path.GetDirectoryName(Path.GetFullPath(sln)) : null;
            var found = Found(dir);
            if (sub is "approve" or "revoke")
            {
                var p = found.FirstOrDefault(x => x.Id == id && x.Hash != null)
                        ?? throw new Exception($"No plugin '{id}'" + (dir == null ? " (plugins of a project: pass --solution)" : ""));
                var all = Approved();
                if (sub == "approve")
                {
                    if (p.Status == "not allowed") throw new Exception(p.Reason);
                    all.Add(p.Hash);
                    SaveApproved(all);
                    Console.WriteLine($"Approved: {p.Id} {p.Version} ({p.Level}, {p.Folder})\n  runs: {p.Command} {string.Join(" ", p.Args)}\n  sha256: {p.Hash}\n" +
                                      "  Only this content - a changed file needs a new approval. It runs as you, on this PC.");
                }
                else
                {
                    all.Remove(p.Hash);
                    SaveApproved(all);
                    Console.WriteLine($"Approval revoked: {p.Id} ({p.Folder})");
                }
                return Cli.Ok;
            }
            if (json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["plugins"] = new JsonArray(found.Select(p => (JsonNode)new JsonObject
                    {
                        ["id"] = p.Id, ["name"] = p.Name, ["description"] = p.Description, ["version"] = p.Version, ["level"] = p.Level, ["folder"] = p.Folder,
                        ["status"] = p.Status, ["reason"] = p.Reason, ["hash"] = p.Hash, ["command"] = string.Join(" ", new[] { p.Command }.Concat(p.Args)),
                        ["tools"] = new JsonObject(p.Tools.Select(t => KeyValuePair.Create(p.Prefix + t.Key, (JsonNode)(t.Value switch
                        {
                            ToolClass.Read => "read", ToolClass.RuntimeWrite => "runtime", _ => "write"
                        }))))
                    }).ToArray()),
                    ["folders"] = new JsonArray(Modules.Levels(dir).Select(l => (JsonNode)new JsonObject { ["level"] = l.Name, ["folder"] = Path.Combine(l.Root, "plugins") }).ToArray())
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return Cli.Ok;
            }
            if (found.Count == 0) Console.WriteLine("No plugins. Folders: " + string.Join(", ", Modules.Levels(dir).Select(l => Path.Combine(l.Root, "plugins"))));
            foreach (var p in found)
                Console.WriteLine($"{p.Id,-20} {p.Status,-15} {p.Level,-8} {p.Version,-8} {p.Tools.Count} declared tool(s)  {p.Folder}" + (p.Reason != null ? "\n    " + p.Reason : ""));
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Cli.Usage; }
    }
}
