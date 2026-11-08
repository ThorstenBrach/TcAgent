// TcAgent.exe format - the ST formatter on the command line, for the editor command of the extension and for scripts
// (StFormatter.cs, docs/FORMATTER.md). Files only, no IDE.
//
//   TcAgent.exe format --stdin [--part declaration|implementation] [--solution <sln>] [--rules <formatting.json>] [--json]
//       formats the text from stdin (UTF-8) and writes it to stdout; --json: {ok, changed, text, error, notes, part}
//   TcAgent.exe format --file <file.st> [--write] [--check] ...   a plain-text file: print it, write it back, or only check it
//   TcAgent.exe format --show-rules [--solution <sln>] [--json]   the effective rules and the level each one comes from
//   TcAgent.exe format --examples --markdown | --json             an example for every rule (docs/FORMATTER_EXAMPLES.md, the window)
//   TcAgent.exe format --save <changes.json> --level user|project|team|company [--solution <sln>]   writes rules into the
//       formatting.json of that level (backup first); a rule set to "unset" is removed there - the settings window of the IDE
//
// Exit codes: 0 = formatted (or nothing to do), 1 = --check: the file would change, 2 = wrong call, 3 = not formatted (safety net).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class CliFormat
{
    public static int Run(string[] args)
    {
        string solution = null, rules = null, file = null, part = null, save = null, level = null;
        bool stdin = false, json = false, write = false, check = false, show = false, tokens = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                string V() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
                switch (args[i].ToLowerInvariant())
                {
                    case "--solution": solution = V(); break;
                    case "--rules": rules = V(); break;
                    case "--file": file = V(); break;
                    case "--part": part = V(); break;
                    case "--stdin": stdin = true; break;
                    case "--json": json = true; break;
                    case "--tokens": tokens = true; break;
                    case "--write": write = true; break;
                    case "--check": check = true; break;
                    case "--show-rules": show = true; break;
                    case "--save": save = V(); break;
                    case "--level": level = V(); break;
                    case "--examples":
                        Console.OutputEncoding = new UTF8Encoding(false);
                        if (args.Contains("--markdown")) { Console.Write(StFormatExamples.Markdown()); return Cli.Ok; }
                        Console.WriteLine(ExamplesJson().ToJsonString());
                        return Cli.Ok;
                    case "--survey":
                        {
                            var folder = V();
                            int ri = Array.FindIndex(args, a => a.Equals("--rules", StringComparison.OrdinalIgnoreCase));
                            return Survey(folder, args.Contains("--json"), ri >= 0 && ri + 1 < args.Length ? StFormatConfig.FromFile(args[ri + 1]) : new StFormatConfig());
                        }
                    default: throw new ArgumentException("unknown option " + args[i]);
                }
            }
            if (!stdin && file == null && !show && save == null) throw new ArgumentException("--stdin, --file, --show-rules or --save is required");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            Console.Error.WriteLine("TcAgent.exe format --stdin [--part declaration|implementation] [--solution <sln>] [--json]");
            return Cli.Usage;
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        string dir = solution == null ? null : Directory.Exists(solution) ? Path.GetFullPath(solution) : Path.GetDirectoryName(Path.GetFullPath(solution));
        StFormatConfig cfg;
        try { cfg = rules != null ? StFormatConfig.FromFile(rules) : StFormatConfig.Load(dir); }
        catch (Exception ex) { Console.Error.WriteLine("Error: rules: " + ex.Message); return Cli.Usage; }

        if (save != null)
        {
            try { Console.WriteLine(Save(save, level ?? "project", dir)); return Cli.Ok; }
            catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Usage; }
        }
        if (show)
        {
            if (json) { Console.WriteLine(cfg.ToJson().ToJsonString()); return Cli.Ok; }
            foreach (var r in StFormatConfig.Rules) Console.WriteLine($"{r.Key,-22} {cfg[r.Key],-12} ({cfg.Source[r.Key]})");
            foreach (var n in cfg.Notes) Console.WriteLine("note: " + n);
            return Cli.Ok;
        }

        var p = part?.ToLowerInvariant() switch { "declaration" or "decl" => StPart.Declaration, "implementation" or "impl" => StPart.Implementation, _ => StPart.Auto };
        string text;
        Encoding enc = new UTF8Encoding(false);
        if (stdin)
        {
            using var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            text = r.ReadToEnd();
        }
        else
        {
            var bytes = File.ReadAllBytes(file);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            enc = new UTF8Encoding(bom);
            text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        var res = StFormatter.Format(text, cfg, p);
        if (json)
        {
            Console.WriteLine(new JsonObject
            {
                ["ok"] = res.Error == null, ["changed"] = res.Changed, ["text"] = res.Text, ["error"] = res.Error, ["part"] = res.Part.ToString().ToLowerInvariant(),
                ["notes"] = new JsonArray(res.Notes.Concat(cfg.Notes).Select(n => (JsonNode)n).ToArray()), ["rulesFromFiles"] = cfg.FromFiles,
                ["tokens"] = tokens ? Colored(res.Text) : null, ["inputTokens"] = tokens ? Colored(text) : null
            }.ToJsonString());
            return res.Error != null ? Cli.Infra : check && res.Changed ? Cli.Failed : Cli.Ok;
        }
        if (res.Error != null) { Console.Error.WriteLine(res.Error); if (!check && !write) Console.Write(text); return Cli.Infra; }
        if (check)
        {
            Console.WriteLine(res.Changed ? $"{file ?? "stdin"}: would change" : $"{file ?? "stdin"}: formatted");
            return res.Changed ? Cli.Failed : Cli.Ok;
        }
        if (write && file != null)
        {
            if (res.Changed) File.WriteAllText(file, res.Text, enc);
            Console.WriteLine(res.Changed ? $"{file}: formatted" : $"{file}: nothing to do");
            return Cli.Ok;
        }
        Console.Write(res.Text);
        return Cli.Ok;
    }

    /// <summary>The text as [class, text] pairs for syntax colors in the preview of the IDE: keyword, type, literal, comment,
    /// pragma, string, number, newline, text. Text that cannot be read is one "text" piece.</summary>
    static JsonArray Colored(string text)
    {
        List<StToken> t;
        try { t = StLexer.Lex(text ?? ""); }
        catch (StSyntaxException) { return new JsonArray(new JsonArray("text", text)); }
        var r = new JsonArray();
        StToken prev = null;
        foreach (var x in t)
        {
            bool member = prev != null && prev.Kind == StTok.Op && prev.Text == ".";
            string c = x.Kind switch
            {
                StTok.Newline => "newline",
                StTok.LineComment or StTok.BlockComment => "comment",
                StTok.Pragma => "pragma",
                StTok.String => "string",
                StTok.Number => "number",
                StTok.Literal => "number",
                StTok.Word when !member && StKeywords.Keywords.Contains(x.Text) => "keyword",
                StTok.Word when !member && StKeywords.Types.Contains(x.Text) => "type",
                StTok.Word when !member && StKeywords.Literals.Contains(x.Text) => "keyword",
                _ => "text"
            };
            // neighbours of the same class become one piece
            if (r.Count > 0 && r[^1] is JsonArray last && last[0]!.ToString() == c && c is "text") last[1] = last[1]!.ToString() + x.Text;
            else r.Add(new JsonArray(c, x.Text));
            if (!x.Trivia && !x.Comment) prev = x;
        }
        return r;
    }

    /// <summary>The examples of every rule for the settings window: the code and, per value, the result - with syntax classes.</summary>
    static JsonObject ExamplesJson()
    {
        var r = new JsonObject();
        foreach (var e in StFormatExamples.All)
            r[e.Rule] = new JsonObject
            {
                ["part"] = e.Part.ToString().ToLowerInvariant(),
                ["code"] = Colored(e.Code),
                ["values"] = new JsonArray(e.Values.Select(v => (JsonNode)new JsonObject { ["value"] = v, ["tokens"] = Colored(StFormatExamples.Run(e, v)) }).ToArray()),
                ["with"] = string.Join(", ", e.With.Select(w => $"{w.Key}: {w.Value}"))
            };
        return r;
    }

    /// <summary>Merges {"rules": {...}, "profile": "..."} into the formatting.json of a level (after a backup of the levels).</summary>
    static string Save(string changesFile, string level, string dir)
    {
        var changes = JsonNode.Parse(File.ReadAllText(changesFile)) as JsonObject ?? throw new ArgumentException("the changes are not a JSON object");
        string root = level.ToLowerInvariant() switch
        {
            "user" or "personal" => KnowledgeLayers.UserRoot,
            "project" => KnowledgeLayers.ProjectRoot(dir) ?? throw new ArgumentException("--solution is needed for the project level"),
            "team" => KnowledgeLayers.TeamWriteRoot(),
            "company" => KnowledgeLayers.CompanyRoot ?? throw new ArgumentException("no company level - set TCAGENT_COMPANY"),
            _ => throw new ArgumentException("--level user, project, team or company")
        };
        KnowledgeBackup.Version = Program.ServerVersion;
        KnowledgeBackup.BeforeWrite(dir, "format rules");
        var target = Path.Combine(root, "formatting.json");
        var doc = File.Exists(target) ? JsonNode.Parse(File.ReadAllText(target)) as JsonObject ?? new JsonObject() : new JsonObject();
        if (changes["profile"] is JsonNode prof)
        {
            var p = prof.ToString();
            if (p == "unset" || p.Length == 0) doc.Remove("profile"); else doc["profile"] = p;
        }
        if (doc["rules"] is not JsonObject rules) { rules = new JsonObject(); doc["rules"] = rules; }
        int n = 0;
        foreach (var kv in (changes["rules"] as JsonObject ?? new JsonObject()).ToList())
        {
            var v = kv.Value?.ToString() ?? "unset";
            if (!StFormatConfig.Rules.Any(r => r.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("unknown rule " + kv.Key);
            if (v == "unset") rules.Remove(kv.Key);
            else rules[kv.Key] = int.TryParse(v, out var i) ? JsonValue.Create(i) : v is "true" or "false" ? JsonValue.Create(v == "true") : JsonValue.Create(v);
            n++;
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(target, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        return $"{n} rule(s) saved in {target}";
    }

    static readonly System.Text.RegularExpressions.Regex Sections = new(
        @"<(Declaration|ST)><!\[CDATA\[(.*?)\]\]></\1>", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The ST sections (declaration and implementation) of a TwinCAT source file (.TcPOU, .TcGVL, .TcDUT, .TcIO).</summary>
    public static IEnumerable<(StPart Part, string Text)> SectionsOf(string xml) =>
        Sections.Matches(xml).Select(m => (m.Groups[1].Value == "ST" ? StPart.Implementation : StPart.Declaration, m.Groups[2].Value));

    /// <summary>--survey &lt;folder&gt;: formats every section of every TwinCAT source file in memory (nothing is written)
    /// and counts what would change and what could not be formatted - to test the formatter on real code.</summary>
    static int Survey(string folder, bool json, StFormatConfig cfg)
    {
        int files = 0, sections = 0, changed = 0, failed = 0;
        var errors = new Dictionary<string, int>();
        var examples = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                     .Where(x => x.EndsWith(".TcPOU", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".TcGVL", StringComparison.OrdinalIgnoreCase)
                              || x.EndsWith(".TcDUT", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".TcIO", StringComparison.OrdinalIgnoreCase)))
        {
            string xml;
            try { xml = File.ReadAllText(f); } catch { continue; }
            files++;
            foreach (var (part, text) in SectionsOf(xml))
            {
                sections++;
                var r = StFormatter.Format(text, cfg, part);
                if (r.Error != null)
                {
                    failed++;
                    var key = System.Text.RegularExpressions.Regex.Replace(r.Error, @"line \d+|token \d+", "#");
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                    if (examples.Count < 20) examples.Add($"{f} [{part}]: {r.Error}");
                }
                else if (r.Changed) changed++;
            }
        }
        if (json)
            Console.WriteLine(new JsonObject
            {
                ["files"] = files, ["sections"] = sections, ["changed"] = changed, ["failed"] = failed,
                ["errors"] = new JsonObject(errors.OrderByDescending(e => e.Value).Select(e => KeyValuePair.Create(e.Key, (JsonNode)e.Value))),
                ["examples"] = new JsonArray(examples.Select(e => (JsonNode)e).ToArray())
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"{files} files, {sections} sections: {changed} would change, {failed} not formatted");
            foreach (var e in errors.OrderByDescending(e => e.Value)) Console.WriteLine($"  {e.Value,5}  {e.Key}");
            foreach (var e in examples) Console.WriteLine("  " + e);
        }
        return failed == 0 ? Cli.Ok : Cli.Infra;
    }
}
