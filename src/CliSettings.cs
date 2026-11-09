// TcAgent.exe settings / targets: the commands behind the TcAgent settings window (vsbridge/SettingsWindow.cs) - files only,
// no IDE. A person runs them (the window or a console); there is no agent tool for them. Every change goes to the audit log and
// asks the running TcAgent servers to read their settings again (Reload.cs).
//
//   settings [--json]                         every setting with its value and where it comes from
//   settings --set KEY=VALUE [--set ...]      write to settings.json "env" (empty value = back to the default)
//   settings --export <file> | --import <file>
//   targets [--json]                          released target systems and the user mode runtimes found on this PC
//   targets add <netId> [--name <n>] [--access write|read] [--until 30m|1h|today|<date>|forever]
//   targets remove <netId>
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class CliSettings
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
    static string SettingsFile => Path.Combine(KnowledgeLayers.UserRoot, "settings.json");
    static IEnumerable<string> McpConfigs => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "mcp-*.json") : Enumerable.Empty<string>();
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static void Audit(string what, JsonObject args, string result)
    {
        try { new Policy().Audit(what, args, true, result, 0, "by a person (settings window or command line)"); } catch { }
    }

    static string Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ------------------------------------------------------------------ settings
    public static int Settings(string[] args)
    {
        try
        {
            var sets = args.Select((a, i) => (a, i)).Where(x => x.a.Equals("--set", StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length)
                           .Select(x => args[x.i + 1]).ToList();
            if (Arg(args, "--export") is { } exp)
            {
                var env = ReadEnv();
                File.WriteAllText(exp, new JsonObject { ["tcagentSettings"] = 1, ["env"] = env }.ToJsonString(Pretty), new UTF8Encoding(false));
                Console.WriteLine($"Exported {env.Count} settings to {exp}");
                return Cli.Ok;
            }
            if (Arg(args, "--import") is { } imp)
            {
                if (JsonNode.Parse(File.ReadAllText(imp))?["env"] is not JsonObject env) throw new ArgumentException("no \"env\" in " + imp);
                sets.AddRange(env.Select(kv => kv.Key + "=" + kv.Value));
            }
            if (sets.Count > 0)
            {
                var changes = new List<string>();
                foreach (var s in sets)
                {
                    var eq = s.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"--set {s}: KEY=VALUE");
                    var key = s[..eq].Trim().ToUpperInvariant();
                    var value = s[(eq + 1)..].Trim();
                    if (SettingsSchema.Internal.Contains(key)) throw new ArgumentException($"{key} is set by TcAgent itself");
                    var def = SettingsSchema.Find(key) ?? throw new ArgumentException($"{key} is not a TcAgent setting (TcAgent.exe settings lists them)");
                    if (SettingsSchema.Check(def, value) is { } bad) throw new ArgumentException($"{key}: {bad}");
                    // the default needs no entry: switches that are on, and the default value itself
                    if (value.Equals(def.Default, StringComparison.OrdinalIgnoreCase) && def.Key != "TCAGENT_ALLOW_TARGETS") value = "";
                    KnowledgeLayers.SetSetting(key, value);
                    changes.Add(value.Length == 0 ? key + " = default" : $"{key} = {value}");
                }
                Audit("settings", new JsonObject { ["set"] = string.Join("; ", changes) }, SettingsFile);
                Reload.Request();
                Console.WriteLine("Saved: " + string.Join("; ", changes) + $"\nFile: {SettingsFile}\nRunning TcAgent servers read their settings again.");
                return Cli.Ok;
            }
            var show = SettingsSchema.Show(SettingsFile, McpConfigs);
            if (args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase))) { Console.WriteLine(show.ToJsonString(Pretty)); return Cli.Ok; }
            foreach (var page in SettingsSchema.Pages)
            {
                Console.WriteLine($"\n{page.En}");
                foreach (var s in show["settings"]!.AsArray().OfType<JsonObject>().Where(x => x["page"]?.ToString() == page.Id))
                    Console.WriteLine($"  {s["key"],-24} {s["effective"],-20} ({s["from"]}{(s["outsideFrom"] is { } o ? ": " + o : "")})");
            }
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Usage; }
    }

    static JsonObject ReadEnv()
    {
        try { if (File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile))?["env"] is JsonObject e) return e.DeepClone().AsObject(); } catch { }
        return new JsonObject();
    }

    // ------------------------------------------------------------------ targets
    public static int TargetsCli(string[] args)
    {
        try
        {
            var verb = args.Length > 1 && !args[1].StartsWith("--") ? args[1].ToLowerInvariant() : "list";
            if (verb == "add")
            {
                var netId = args.Length > 2 ? args[2] : throw new ArgumentException("targets add <netId> [--name <n>] [--access write|read] [--until 1h]");
                var access = (Arg(args, "--access") ?? "write").ToLowerInvariant();
                if (access is not ("write" or "read")) throw new ArgumentException("--access write or read");
                var e = Targets.Set(netId, Arg(args, "--name"), access == "write", Targets.ParseUntil(Arg(args, "--until")), Arg(args, "--by") ?? "command line");
                Audit("targets", new JsonObject { ["action"] = "add", ["netId"] = e.NetId, ["name"] = e.Name, ["access"] = access, ["until"] = e.Until?.ToString("s") ?? "forever" }, Targets.FilePath);
                Console.WriteLine("Released: " + Targets.Describe(e, DateTime.Now));
                return Cli.Ok;
            }
            if (verb is "remove" or "delete")
            {
                var netId = args.Length > 2 ? args[2] : throw new ArgumentException("targets remove <netId>");
                if (!Targets.Remove(netId)) { Console.Error.WriteLine($"{netId} is not in the list."); return Cli.Failed; }
                Audit("targets", new JsonObject { ["action"] = "remove", ["netId"] = netId }, Targets.FilePath);
                Console.WriteLine($"Removed {netId} - the agent may only read it now.");
                return Cli.Ok;
            }
            // list
            var now = DateTime.Now;
            var list = Targets.Load();
            var policy = new Policy();
            var configured = (Environment.GetEnvironmentVariable("TCAGENT_ALLOW_TARGETS") ?? "local");
            var um = Targets.LocalUserModeRuntimes();
            if (args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
            {
                var o = new JsonObject
                {
                    ["file"] = Targets.FilePath,
                    ["targets"] = new JsonArray(list.Select(t =>
                    {
                        var j = t.ToJson();
                        j["state"] = t.Expired(now) ? "expired" : t.Write ? (t.Until != null ? "expires" : "write") : "read";
                        return (JsonNode)j;
                    }).ToArray()),
                    ["configured"] = configured,
                    ["configuredFrom"] = SettingsSchema.Show(SettingsFile, McpConfigs)["settings"]!.AsArray().OfType<JsonObject>()
                                           .First(x => x["key"]?.ToString() == "TCAGENT_ALLOW_TARGETS")["from"]?.ToString(),
                    ["writable"] = new JsonArray(policy.AllowedForWriting().Select(x => (JsonNode)x).ToArray()),
                    ["userModeRuntimes"] = new JsonArray(um.Select(u => (JsonNode)new JsonObject { ["name"] = u.Name, ["netId"] = u.NetId }).ToArray()),
                    ["routes"] = new JsonArray(Targets.Routes().Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["netId"] = r.NetId, ["address"] = r.Address, ["runtime"] = r.Runtime }).ToArray())
                };
                Console.WriteLine(o.ToJsonString(Pretty));
                return Cli.Ok;
            }
            Console.WriteLine(policy.Describe());
            foreach (var r in Targets.Routes().Where(r => !list.Any(t => t.NetId == r.NetId)))
                Console.WriteLine($"ADS route, not released: {r.Name} ({r.NetId}{(r.Address.Length > 0 ? ", " + r.Address : "")}){(r.Runtime.Length > 0 ? " - route of " + r.Runtime : "")}");
            foreach (var u in um.Where(u => !list.Any(t => t.NetId == u.NetId)))
                Console.WriteLine($"Found on this PC, not released: user mode runtime {u.Name} ({u.NetId}) - TcAgent.exe targets add {u.NetId} --name \"{u.Name}\" --until 1h");
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Usage; }
    }
}
