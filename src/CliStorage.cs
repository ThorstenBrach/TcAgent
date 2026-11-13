// TcAgent.exe storage (Storage.cs) - what TcAgent keeps under %LOCALAPPDATA%\TcAgent and the clean-up. Files only, no IDE.
//
//   TcAgent.exe storage [--json]                                    the areas (size, files, oldest, rule) and the projects
//   TcAgent.exe storage clean [--area <a>] [--dry-run] [--permanent] [--json]   remove what the rules say (recycle bin
//                                                                   unless --permanent, e.g. in a pipeline)
//   TcAgent.exe storage auto [--force] [--json]                     the daily run of the extension (safe areas only)
//   TcAgent.exe storage project-delete --name <project> [--permanent]   everything kept for one project (chats, TcUnit, runs)
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json.Nodes;

namespace TcAgent;

public static partial class Cli
{
    static int StorageCli(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        bool json = false, dry = false, permanent = false, force = false;
        string area = null, name = null;
        var words = new List<string>();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--json") json = true;
                else if (a == "--dry-run") dry = true;
                else if (a == "--permanent") permanent = true;
                else if (a == "--force") force = true;
                else if (a == "--area") area = i + 1 < args.Length ? args[++i].ToLowerInvariant() : throw new ArgumentException("--area needs a value");
                else if (a == "--name") name = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--name needs a value");
                else if (a.StartsWith('-')) throw new ArgumentException("unknown option " + a);
                else words.Add(a.ToLowerInvariant());
            }
            static string Mb(long b) => (b / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            static JsonArray Items(IEnumerable<Storage.Item> l) => new(l.Select(i => (JsonNode)new JsonObject
            {
                ["area"] = i.Area, ["path"] = i.Path, ["bytes"] = i.Bytes, ["time"] = i.Time.ToString("yyyy-MM-ddTHH:mm:ss"), ["why"] = i.Why, ["rotate"] = i.Rotate
            }).ToArray());
            switch (words.FirstOrDefault() ?? "list")
            {
                case "list":
                {
                    var plan = Storage.Plan();
                    var areas = new JsonArray();
                    foreach (var (d, on) in Storage.Rules())
                    {
                        var c = Storage.Contents(d.Key);
                        var p = plan.Where(x => x.Area == d.Key).ToList();
                        areas.Add(new JsonObject
                        {
                            ["key"] = d.Key, ["title"] = d.Title, ["rule"] = Storage.RuleText(d), ["on"] = on, ["auto"] = d.Auto,
                            ["bytes"] = c.Sum(x => x.Bytes), ["count"] = c.Count, ["oldest"] = c.Count == 0 ? null : c.Min(x => x.Time).ToString("yyyy-MM-dd"),
                            ["removable"] = p.Count, ["removableBytes"] = p.Sum(x => x.Bytes)
                        });
                    }
                    var projects = new JsonArray(Storage.Projects().Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["solutionDir"] = p.SolutionDir, ["exists"] = p.Exists, ["bytes"] = p.Bytes, ["last"] = p.Last.ToString("yyyy-MM-dd"),
                        ["parts"] = new JsonArray(p.Parts.Select(x => (JsonNode)new JsonObject { ["area"] = x.Area, ["path"] = x.Path, ["bytes"] = x.Bytes }).ToArray())
                    }).ToArray());
                    var total = Directory.Exists(Storage.Root) ? Directory.EnumerateFiles(Storage.Root, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0;
                    if (json) { Console.WriteLine(new JsonObject { ["root"] = Storage.Root, ["total"] = total, ["auto"] = Storage.AutoOn(), ["areas"] = areas, ["projects"] = projects }.ToJsonString()); return Ok; }
                    Console.WriteLine($"{Storage.Root}: {Mb(total)} in total");
                    foreach (var a in areas)
                        Console.WriteLine($"  {a!["title"],-36} {Mb(a["bytes"]!.GetValue<long>()),9}  {a["count"],5} item(s)  rule: {a["rule"]}{(a["on"]!.GetValue<bool>() ? "" : " (off)")}" +
                                          (a["removable"]!.GetValue<int>() > 0 ? $"  - {a["removable"]} to remove ({Mb(a["removableBytes"]!.GetValue<long>())})" : ""));
                    Console.WriteLine("Projects:");
                    foreach (var p in projects)
                        Console.WriteLine($"  {p!["name"],-36} {Mb(p["bytes"]!.GetValue<long>()),9}  last {p["last"]}{(p["exists"]!.GetValue<bool>() ? "" : "  (solution not found)")}");
                    return Ok;
                }
                case "clean":
                {
                    var plan = Storage.Plan(area);
                    if (dry || plan.Count == 0)
                    {
                        if (json) Console.WriteLine(new JsonObject { ["dryRun"] = true, ["items"] = Items(plan), ["bytes"] = plan.Sum(x => x.Bytes) }.ToJsonString());
                        else if (plan.Count == 0) Console.WriteLine("Nothing to clean up.");
                        else { foreach (var i in plan) Console.WriteLine($"  {i.Area,-12} {Mb(i.Bytes),9}  {i.Path}  ({i.Why})"); Console.WriteLine($"{plan.Count} item(s), {Mb(plan.Sum(x => x.Bytes))} - without --dry-run they go {(permanent ? "for good" : "to the recycle bin")}."); }
                        return Ok;
                    }
                    var r = Storage.Clean(plan, permanent);
                    if (json) Console.WriteLine(new JsonObject { ["removed"] = r.Removed, ["bytes"] = r.Bytes, ["errors"] = new JsonArray(r.Errors.Select(e => (JsonNode)e).ToArray()) }.ToJsonString());
                    else { Console.WriteLine($"Removed {r.Removed} item(s), {Mb(r.Bytes)}{(permanent ? "" : " (recycle bin)")}."); foreach (var e in r.Errors) Console.WriteLine("  not removed: " + e); }
                    return r.Errors.Count == 0 ? Ok : Failed;
                }
                case "auto":
                {
                    var r = Storage.Auto(force);
                    Console.WriteLine(json ? r.ToJsonString() : r["ran"]!.GetValue<bool>() ? $"Cleaned up: {r["removed"]} item(s), {Mb(r["bytes"]!.GetValue<long>())}." : "Not run: " + r["reason"]);
                    return Ok;
                }
                case "project-delete":
                {
                    var r = Storage.DeleteProject(name ?? throw new ArgumentException("--name <project>"), permanent);
                    Console.WriteLine(json ? new JsonObject { ["removed"] = r.Removed, ["bytes"] = r.Bytes, ["errors"] = new JsonArray(r.Errors.Select(e => (JsonNode)e).ToArray()) }.ToJsonString()
                                           : $"Removed {r.Removed} folder(s) of {name}, {Mb(r.Bytes)}.");
                    return r.Errors.Count == 0 ? Ok : Failed;
                }
                default: throw new ArgumentException("storage [list|clean [--area A] [--dry-run] [--permanent]|auto [--force]|project-delete --name P] [--json]");
            }
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return Usage; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(ex.Message); return Failed; }
    }
}
