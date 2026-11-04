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
// Filled in the chat from the editor: {object} (open object), {selection}, {solution}. Asked in a card before inserting - one
// model (Param), two ways to write it:
//   short, in the text:  {input:Question}   {input:Question|a|b} a choice   {input:Question=value} a default
//   long, in the header: params: with - name, ask, type (text, number, choice, bool, pou = search list of POUs, GVLs and
//                        tasks), default (also {object}, {selection}), options, required, help; in the text {name}
// tcagent-send: true sends the prompt right after the card; otherwise it goes into the input.
// TcAgent.exe prompts [--solution <sln>] [--json] lists what applies, prompts objects the names for the type pou.
//
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Prompts
{
    public const string Kind = "prompts";

    public sealed record Item(string Name, string Description, string Category, List<string> Tags, string Body, string Level, string Source, string Path, bool Send,
                             List<Param> Params, List<string> Warnings);

    /// <summary>One value asked in the card; Token is the text in the prompt it replaces ({input:…} or {name}).</summary>
    public sealed class Param
    {
        public string Name, Ask, Type = "text", Default, Help, Token;
        public List<string> Options = new();
        public bool Required;
    }

    static readonly string[] Types = { "text", "number", "choice", "bool", "pou" };
    static readonly string[] BuiltIn = { "object", "selection", "solution" };
    static readonly Regex ShortForm = new(@"\{input:([^}]*)\}");

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
        var body = (fm.Body ?? "").Trim();
        var warnings = new List<string>();
        string text;
        try { text = File.ReadAllText(e.Path); } catch { text = ""; }
        return new Item(e.Name, fm.Get("description"), fm.Get("category"), tags, body, e.Layer.Label, fm.Get("source"), e.Path, fm.Flag("send"), ParamsOf(text, body, warnings), warnings);
    }

    /// <summary>The values to ask for, in the order they appear in the text: the params: of the header used as {name} and the
    /// {input:…} of the text. A param that the text does not use is not asked.</summary>
    public static List<Param> ParamsOf(string fileText, string body, List<string> warnings = null)
    {
        var declared = ParseHeaderParams(fileText, warnings);
        var found = new List<(int At, Param P)>();
        foreach (var p in declared)
        {
            p.Token = "{" + p.Name + "}";
            var at = body.IndexOf(p.Token, StringComparison.Ordinal);
            if (at >= 0) found.Add((at, p));
            else warnings?.Add($"param {p.Name}: {p.Token} is not used in the text");
        }
        var seen = new HashSet<string>();
        foreach (Match m in ShortForm.Matches(body))
            if (seen.Add(m.Value)) found.Add((m.Index, Short(m.Value, m.Groups[1].Value.Trim())));
        return found.OrderBy(f => f.At).Select(f => f.P).ToList();
    }

    static Param Short(string token, string text)
    {
        var parts = text.Split('|').Select(x => x.Trim()).ToList();
        if (parts.Count > 1) return new Param { Token = token, Name = parts[0], Ask = parts[0], Type = "choice", Options = parts.Skip(1).ToList(), Default = parts[1] };
        var eq = text.IndexOf('=');
        return eq > 0 ? new Param { Token = token, Name = text[..eq].Trim(), Ask = text[..eq].Trim(), Default = text[(eq + 1)..].Trim() }
                      : new Param { Token = token, Name = text, Ask = text };
    }

    // the block "params:" of the header: a list of "- key: value" - the general front matter reader only knows key: value
    static List<Param> ParseHeaderParams(string text, List<string> warnings)
    {
        var list = new List<Param>();
        var lines = (text ?? "").Replace("\r\n", "\n").TrimStart((char)0xFEFF).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return list;
        Param cur = null;
        bool inside = false;
        for (int i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0) continue;
            var indented = raw[0] is ' ' or '\t';
            if (!indented) { inside = Regex.IsMatch(raw, @"^params\s*:\s*$"); cur = null; continue; }
            if (!inside) continue;
            var m = Regex.Match(raw.Trim(), @"^(-\s*)?([\w-]+)\s*:\s*(.*)$");
            if (!m.Success) { warnings?.Add("params: cannot read: " + raw.Trim()); continue; }
            if (m.Groups[1].Success) list.Add(cur = new Param());
            if (cur == null) continue;
            var value = Unquote(m.Groups[3].Value.Trim());
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "name": cur.Name = value; break;
                case "ask": cur.Ask = value; break;
                case "type": cur.Type = value.ToLowerInvariant(); break;
                case "default": cur.Default = value; break;
                case "help": cur.Help = value; break;
                case "required": cur.Required = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "options": cur.Options = value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).ToList(); break;
                default: warnings?.Add($"param {cur.Name}: unknown field {m.Groups[2].Value}"); break;
            }
        }
        var good = new List<Param>();
        foreach (var p in list)
        {
            if (string.IsNullOrEmpty(p.Name) || !Regex.IsMatch(p.Name, @"^[\w-]+$")) { warnings?.Add("param without a valid name"); continue; }
            if (BuiltIn.Contains(p.Name, StringComparer.OrdinalIgnoreCase) || p.Name.Equals("input", StringComparison.OrdinalIgnoreCase)) { warnings?.Add($"param {p.Name}: reserved name"); continue; }
            if (!Types.Contains(p.Type)) { warnings?.Add($"param {p.Name}: type {p.Type} unknown - text"); p.Type = "text"; }
            if (p.Type == "choice" && p.Options.Count == 0) { warnings?.Add($"param {p.Name}: choice without options - text"); p.Type = "text"; }
            p.Ask ??= p.Name;
            good.Add(p);
        }
        return good;
    }

    static string Unquote(string v) => v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    /// <summary>The names for the type pou: POUs, GVLs and tasks from the files of the solution (no IDE needed).</summary>
    public static List<(string Name, string Kind)> Objects(string solutionDir)
    {
        var found = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return new();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12 };
        foreach (var f in Directory.EnumerateFiles(solutionDir, "*", options))
        {
            if (f.Contains(@"\_Boot\") || f.Contains(@"\_CompileInfo\") || f.Contains(@"\bin\") || f.Contains(@"\obj\")) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".tcpou" or ".tcgvl" or ".tctto") found.TryAdd(Path.GetFileNameWithoutExtension(f), ext == ".tcpou" ? "POU" : ext == ".tcgvl" ? "GVL" : "Task");
            else if (ext == ".tsproj")
                try { foreach (Match m in Regex.Matches(File.ReadAllText(f), @"<Task\b[^>]*>\s*<Name>([^<]+)</Name>")) found.TryAdd(m.Groups[1].Value, "Task"); } catch { }
        }
        return found.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // TcAgent.exe prompts [--solution <sln>] [--json]
    public static int RunCli(string[] args)
    {
        string sln = null; bool json = false, objects = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a is "--solution" or "-s") sln = i + 1 < args.Length ? args[++i] : throw new Exception("--solution needs a value");
                else if (a == "--json") json = true;
                else if (a.Equals("objects", StringComparison.OrdinalIgnoreCase)) objects = true;
                else if (!a.Equals("list", StringComparison.OrdinalIgnoreCase)) throw new Exception("unknown option " + a);
            }
            if (sln != null && !File.Exists(sln)) throw new Exception("Solution not found: " + sln);
            var dir = sln != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sln)) : null;
            if (objects)
            {
                Console.WriteLine(new JsonArray(Objects(dir).Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["kind"] = o.Kind }).ToArray()).ToJsonString());
                return Cli.Ok;
            }
            var items = Active(dir, KnowledgeLayers.Platforms(dir).Active);
            if (json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["prompts"] = new JsonArray(items.Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["description"] = p.Description, ["category"] = p.Category, ["level"] = p.Level, ["source"] = p.Source, ["path"] = p.Path, ["send"] = p.Send,
                        ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)t).ToArray()), ["body"] = p.Body,
                        ["params"] = new JsonArray(p.Params.Select(x => (JsonNode)new JsonObject
                        {
                            ["name"] = x.Name, ["ask"] = x.Ask, ["type"] = x.Type, ["default"] = x.Default, ["help"] = x.Help, ["required"] = x.Required, ["token"] = x.Token,
                            ["options"] = new JsonArray(x.Options.Select(o => (JsonNode)o).ToArray())
                        }).ToArray())
                    }).ToArray())
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return Cli.Ok;
            }
            if (items.Count == 0) Console.WriteLine("No prompts.");
            foreach (var p in items)
            {
                Console.WriteLine($"/{p.Name,-24} {p.Level,-14} {p.Description}");
                foreach (var w in p.Warnings) Console.WriteLine("    ! " + w);
            }
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Console.Error.WriteLine("TcAgent.exe help shows the usage."); return Cli.Usage; }
    }
}
