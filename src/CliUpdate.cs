// TcAgent.exe update (Updates.cs) and TcAgent.exe about - for the TcAgent Chat of the extension, which shows the answers.
//
// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.Json.Nodes;

namespace TcAgent;

public static partial class Cli
{
    static int UpdateCli(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        bool json = false, force = false;
        string current = Program.ServerVersion, sln = null;
        var words = new List<string>();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--json") json = true;
                else if (a == "--force") force = true;
                else if (a == "--current") current = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--current needs a value");
                else if (a is "--solution" or "-s") sln = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--solution needs a value");
                else if (a.StartsWith('-')) throw new ArgumentException("unknown option " + a);
                else words.Add(a.ToLowerInvariant());
            }
            var dir = sln != null ? Path.GetDirectoryName(Path.GetFullPath(sln)) : null;
            switch (words.FirstOrDefault() ?? "check")
            {
                case "check":
                {
                    var r = Updates.Check(current, force);
                    Console.WriteLine(json ? r.ToJsonString() : r["error"] != null ? "Could not check: " + r["error"]
                                      : r["available"]?.GetValue<bool>() == true ? $"TcAgent {r["latest"]} is available (you have {current})."
                                      : $"TcAgent {current} is up to date.");
                    return Ok;
                }
                case "sources":
                {
                    var r = Updates.CheckSources(dir, force);
                    if (json) Console.WriteLine(r.ToJsonString());
                    else foreach (var x in r["items"].AsArray()) Console.WriteLine($"{x["item"]} ({x["level"]}): newer version in {x["source"]}");
                    return Ok;
                }
                case "download":
                {
                    var file = Updates.Download(current);
                    Console.WriteLine(json ? new JsonObject { ["file"] = file }.ToJsonString() : file);
                    return Ok;
                }
                case "skip":
                {
                    var st = Updates.LoadState();
                    st["skipped"] = words.ElementAtOrDefault(1) ?? throw new ArgumentException("update skip <version>");
                    Updates.SaveState(st);
                    return Ok;
                }
                case "set":
                {
                    if (words.ElementAtOrDefault(1) is not ("check" or "sources" or "knowledge") || words.ElementAtOrDefault(2) is not ("on" or "off"))
                        throw new ArgumentException("update set check|sources|knowledge on|off");
                    var st = Updates.LoadState();
                    var key = words[1] == "knowledge" ? Updates.KnowledgeSource : words[1];
                    if (Updates.Setting(key, st).LockedBy is { } by) throw new Exception($"locked on the {by} level");
                    st[key] = words[2] == "on";
                    Updates.SaveState(st);
                    return Ok;
                }
                default: throw new ArgumentException("update [check|sources|download|skip <version>|set check|sources|knowledge on|off]");
            }
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return Usage; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (json) Console.WriteLine(new JsonObject { ["error"] = ex.Message }.ToJsonString());
            return Failed;
        }
    }

    static int SharedCli(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        bool json = false, all = false;
        string path = null, sln = null, verb = "list";
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--json") json = true;
                else if (a == "--all") all = true;
                else if (a == "--path") path = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--path needs a value");
                else if (a is "--solution" or "-s") sln = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--solution needs a value");
                else if (a.StartsWith('-')) throw new ArgumentException("unknown option " + a);
                else verb = a.ToLowerInvariant();
            }
            var dir = sln != null ? Path.GetDirectoryName(Path.GetFullPath(sln)) : null;
            if (verb is "add" or "keep" or "remove" or "check" && string.IsNullOrEmpty(path)) throw new ArgumentException("--path is required");
            if (verb is "add" or "keep" && dir == null && !all) throw new ArgumentException("--solution or --all is required");
            var target = all ? null : dir;
            if (verb is "add" or "keep" or "remove")
            {
                KnowledgeBackup.Version = Program.ServerVersion;
                KnowledgeBackup.BeforeWrite(dir, "shared " + verb);
            }
            var r = new JsonObject();
            switch (verb)
            {
                case "list":
                    r["folders"] = new JsonArray((dir != null ? SharedFolders.Active(dir) : SharedFolders.Load()).Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["solution"] = e.Solution }).ToArray());
                    break;
                case "check":
                    if (SharedFolders.Refused(path) is { } why) r["error"] = why;
                    break;
                case "add": SharedFolders.Add(path, target); break;
                case "keep": r["file"] = SharedFolders.KeepFile(path, target); break;
                case "remove": if (!SharedFolders.Remove(path)) r["error"] = "not in the list: " + path; break;
                default: throw new ArgumentException("shared [list|check|add|keep|remove]");
            }
            Console.WriteLine(json ? r.ToJsonString() : r["error"]?.ToString() ?? r.ToJsonString());
            return r["error"] != null ? Failed : Ok;
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return Usage; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            if (json) Console.WriteLine(new JsonObject { ["error"] = ex.Message }.ToJsonString());
            return Failed;
        }
    }

    static int ResetCli(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var confirm = Array.IndexOf(args, "--confirm") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (confirm == null)
        {
            var (items, connections) = Reset.Plan();
            if (args.Contains("--json"))
                Console.WriteLine(new JsonObject
                {
                    ["items"] = new JsonArray(items.Select(x => (JsonNode)x).ToArray()),
                    ["connections"] = new JsonArray(connections.Select(x => (JsonNode)x).ToArray())
                }.ToJsonString());
            else
            {
                Console.WriteLine(items.Count + connections.Count == 0 ? "Nothing to reset." : "A reset would move: " + string.Join(", ", items));
                if (connections.Count > 0) Console.WriteLine("and remove the connections: " + string.Join(", ", connections));
            }
            return Ok;
        }
        if (confirm != Reset.Confirmation) { Console.Error.WriteLine($"--confirm needs exactly: {Reset.Confirmation}"); return Usage; }
        var (folder, moved, failed) = Reset.Run(DateTime.Now);
        Console.WriteLine($"Reset: {moved.Count} moved to {folder}. Nothing was deleted.");
        foreach (var f in failed) Console.WriteLine("Still in use, close the AI clients and reset again: " + f);
        return failed.Count == 0 ? Ok : Failed;
    }

    // versions as built in, licenses as in THIRD-PARTY-NOTICES.md
    static int AboutCli()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        static string Of(Type t)
        {
            var v = t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? t.Assembly.GetName().Version?.ToString() ?? "?";
            return v.Split('+')[0];
        }
        static JsonObject C(string name, string version, string license) => new() { ["name"] = name, ["version"] = version, ["license"] = license };
        string twincat;
        try { twincat = TwinCatVersion() ?? "not installed"; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { twincat = "?"; }
        Console.WriteLine(new JsonObject
        {
            ["version"] = Program.ServerVersion, ["twincat"] = twincat,
            ["components"] = new JsonArray(
                C(".NET runtime and libraries", Environment.Version.ToString(), "MIT"),
                C("Beckhoff.TwinCAT.Ads", Of(typeof(TwinCAT.Ads.AdsClient)), "MIT"),
                C("PdfPig", Of(typeof(UglyToad.PdfPig.PdfDocument)), "Apache 2.0"),
                C("Adobe Core 14 AFM files", "in PdfPig", "Adobe AFM"))
        }.ToJsonString());
        return Ok;
    }
}
