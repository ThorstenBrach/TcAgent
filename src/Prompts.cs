// Prompt library: prepared prompts for people (skills and workflows are for the agent). Every level of the knowledge can
// hold them (KnowledgeLayers.cs): built-in, company, team, user, platform, project - prompts\<name>.md, the file name is the
// name in the chat (/name); a later level replaces a prompt of the same name. They come from repositories like skills
// (tc_sources, Sources.FileKinds) and are chosen in the TcAgent Chat (vsbridge/ChatPrompts.cs).
//
//   ---
//   description: TcUnit tests for this function block
//   metadata:
//     tcagent-category: Tests
//     tcagent-tags: tcunit, test
//   ---
//   Write TcUnit tests for {object}. Cover the normal sequence, the error path and {input:extra cases}.
//
// Placeholders are filled in the chat: {object} (open object), {selection}, {solution}, {input:<question>} (asked before
// inserting; {input:<question>|a|b} offers a choice, {input:<question>=value} a default). tcagent-send: true sends the prompt
// right after it is complete, otherwise it goes into the input. TcAgent.exe prompts [--solution <sln>] [--json] lists what applies.
//
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Prompts
{
    public const string Kind = "prompts";

    public sealed record Item(string Name, string Description, string Category, List<string> Tags, string Body, string Level, string Source, string Path, bool Send);

    public static List<KnowledgeLayers.Entry> Entries(string solutionDir, IEnumerable<string> platforms) =>
        KnowledgeLayers.Resolve(Kind, solutionDir, platforms, replaceByName: true);

    /// <summary>The prompts that apply, by name.</summary>
    public static List<Item> Active(string solutionDir, IEnumerable<string> platforms) =>
        Entries(solutionDir, platforms).Where(e => e.Active).Select(ToItem).Where(i => i.Body.Length > 0)
                                       .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    static Item ToItem(KnowledgeLayers.Entry e)
    {
        var fm = e.Meta ?? new KnowledgeLayers.FrontMatter();
        var tags = (fm.Get("tags") ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new Item(e.Name, fm.Get("description"), fm.Get("category"), tags, (fm.Body ?? "").Trim(), e.Layer.Label, fm.Get("source"), e.Path, fm.Flag("send"));
    }

    // TcAgent.exe prompts [--solution <sln>] [--json]
    public static int RunCli(string[] args)
    {
        string sln = null; bool json = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a is "--solution" or "-s") sln = i + 1 < args.Length ? args[++i] : throw new Exception("--solution needs a value");
                else if (a == "--json") json = true;
                else if (!a.Equals("list", StringComparison.OrdinalIgnoreCase)) throw new Exception("unknown option " + a);
            }
            if (sln != null && !File.Exists(sln)) throw new Exception("Solution not found: " + sln);
            var dir = sln != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sln)) : null;
            var items = Active(dir, KnowledgeLayers.Platforms(dir).Active);
            if (json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["prompts"] = new JsonArray(items.Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["description"] = p.Description, ["category"] = p.Category, ["level"] = p.Level, ["source"] = p.Source, ["path"] = p.Path, ["send"] = p.Send,
                        ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)t).ToArray()), ["body"] = p.Body
                    }).ToArray())
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return Cli.Ok;
            }
            if (items.Count == 0) Console.WriteLine("No prompts.");
            foreach (var p in items) Console.WriteLine($"/{p.Name,-24} {p.Level,-14} {p.Description}");
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Cli.Usage; }
    }
}
