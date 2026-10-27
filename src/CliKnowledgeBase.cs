// TcAgent command line: backup and restore of the knowledge levels (part of Cli, see Cli.cs; the logic is in
// KnowledgeBackup.cs). Files only, no IDE.
//
//   TcAgent.exe knowledgebase list    [--level <level>] [--json] [--solution <sln>]       the levels and their backups
//   TcAgent.exe knowledgebase backup  [--level <level>] [--message <reason>] [--out <folder>]   zip now (all levels without
//                                                       --level); --out also copies the ZIP files to a folder of your choice
//   TcAgent.exe knowledgebase diff    --level <level> [--from <zip|latest>] [--item <path>]  what a restore would change
//   TcAgent.exe knowledgebase restore --level <level> [--from <zip|latest>] [--item <path>] [--dry-run]
//
// Automatic backups are made before every writing knowledge/sources command (Cli.cs) and knowledge tool (ToolRegistry.cs).
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public static partial class Cli
{
    /// <summary>Commands that change knowledge files - the levels are backed up before them.</summary>
    static bool WritesKnowledge(Options o) => o.Command switch
    {
        "knowledge" => o.Sub switch
        {
            "disable" or "enable" or "new" or "move" or "delete" => true,
            "platform" => o.Action is "new" or "set" or "auto" or "delete",
            "docs" => o.Action is "add" or "remove" or "copy",
            "git" => o.Action is "pull",
            _ => false
        },
        "sources" => o.Sub is "add" or "remove" or "install" or "uninstall" or "revert" or "detach" || o.Sub is "pull" or "sync" or "merge" && !o.DryRun,
        _ => false
    };

    static int KnowledgeBaseCli(Options o)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        KnowledgeBackup.Version = Program.ServerVersion;
        var dir = o.Solution != null ? Path.GetDirectoryName(Path.GetFullPath(o.Solution)) : null;
        try
        {
            switch (o.Sub)
            {
                case "list":
                {
                    var levels = o.Level != null ? new List<KnowledgeBackup.Level> { KnowledgeBackup.Find(dir, o.Level) } : KnowledgeBackup.Levels(dir);
                    if (o.Json)
                    {
                        var arr = new JsonArray();
                        foreach (var l in levels)
                            arr.Add(new JsonObject
                            {
                                ["level"] = l.Name, ["label"] = l.Label, ["root"] = l.Root, ["folder"] = KnowledgeBackup.FolderOf(l),
                                ["backups"] = new JsonArray(KnowledgeBackup.List(l).Select(i => (JsonNode)new JsonObject
                                {
                                    ["zip"] = i.Zip, ["created"] = i.Created, ["reason"] = i.Reason, ["files"] = i.Files.Count, ["git"] = i.Git, ["version"] = i.Version,
                                    ["paths"] = new JsonArray(i.Files.Select(f => (JsonNode)f.Path).ToArray())
                                }).ToArray())
                            });
                        Console.WriteLine(arr.ToJsonString());
                        return Ok;
                    }
                    foreach (var l in levels)
                    {
                        var b = KnowledgeBackup.List(l);
                        Console.WriteLine($"{l.Name} ({l.Root}): {b.Count} backup(s)");
                        foreach (var i in b) Console.WriteLine($"  {i.Created}  {i.Files.Count,4} file(s){(i.Git ? " (uncommitted only)" : "")}  {i.Reason}  {Path.GetFileName(i.Zip)}");
                    }
                    return Ok;
                }
                case "backup":
                {
                    var levels = o.Level != null && o.Level != "all" ? new List<KnowledgeBackup.Level> { KnowledgeBackup.Find(dir, o.Level) } : KnowledgeBackup.Levels(dir);
                    foreach (var l in levels)
                    {
                        var z = KnowledgeBackup.Backup(l, o.Message ?? "by hand", force: true);
                        if (z != null && o.Out != null)
                        {
                            Directory.CreateDirectory(o.Out);
                            var copy = Path.Combine(o.Out, KnowledgeLayers.Slug(l.Name.Replace(':', '-')) + "-" + Path.GetFileName(z));
                            File.Copy(z, copy, true);
                            z += " (copy: " + copy + ")";
                        }
                        Console.WriteLine(z != null ? $"{l.Name}: {z}" : $"{l.Name}: nothing to back up{(KnowledgeBackup.FilesOf(l.Root).Git ? " (Git working copy without uncommitted changes - Git keeps it)" : "")}");
                    }
                    return Ok;
                }
                default:   // diff / restore
                {
                    // a backup file from anywhere (a copy, another PC): its level comes from its manifest
                    var fromFile = o.From != null && File.Exists(o.From) ? KnowledgeBackup.Read(Path.GetFullPath(o.From)) : null;
                    var l = KnowledgeBackup.Find(dir, o.Level ?? fromFile?.Level);
                    var zip = fromFile?.Zip ?? KnowledgeBackup.Resolve(l, o.From);
                    var info = KnowledgeBackup.Read(zip);
                    if (o.Sub == "diff" || o.DryRun)
                    {
                        var plan = KnowledgeBackup.Plan(l, zip, o.Item);
                        Console.WriteLine($"{l.Name} <- backup {info.Created} ({info.Reason}){(o.Item != null ? ", only " + o.Item : "")}: {plan.Count} file(s) would change");
                        foreach (var c in plan) Console.WriteLine($"\n[{c.Kind}] {c.Path}\n{c.Diff.TrimEnd()}");
                        return Ok;
                    }
                    var (changes, safety) = KnowledgeBackup.Restore(l, zip, o.Item);
                    Console.WriteLine(changes.Count == 0 ? $"{l.Name}: already the same as the backup - nothing restored."
                                                         : $"{l.Name}: restored {changes.Count} file(s) from {info.Created} ({string.Join(", ", changes.Take(20).Select(c => c.Kind + " " + c.Path))})" +
                                                           (safety != null ? $"\nThe state before: {safety}" : ""));
                    return Ok;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { Console.Error.WriteLine(ex.Message); return Failed; }
    }
}
