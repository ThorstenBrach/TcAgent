// TcAgent.exe format - the ST formatter on the command line, for the editor command of the extension and for scripts
// (StFormatter.cs, docs/FORMATTER.md). Files only, no IDE.
//
//   TcAgent.exe format --stdin [--part declaration|implementation] [--solution <sln>] [--rules <formatting.json>] [--json]
//       formats the text from stdin (UTF-8) and writes it to stdout; --json: {ok, changed, text, error, notes, part}
//   TcAgent.exe format --file <file.st> [--write] [--check] ...   a plain-text file: print it, write it back, or only check it
//   TcAgent.exe format --show-rules [--solution <sln>] [--json]   the effective rules and the level each one comes from
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
        string solution = null, rules = null, file = null, part = null;
        bool stdin = false, json = false, write = false, check = false, show = false;
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
                    case "--write": write = true; break;
                    case "--check": check = true; break;
                    case "--show-rules": show = true; break;
                    case "--survey": return Survey(V(), json: args.Contains("--json"));
                    default: throw new ArgumentException("unknown option " + args[i]);
                }
            }
            if (!stdin && file == null && !show) throw new ArgumentException("--stdin, --file or --show-rules is required");
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
                ["notes"] = new JsonArray(res.Notes.Concat(cfg.Notes).Select(n => (JsonNode)n).ToArray()), ["rulesFromFiles"] = cfg.FromFiles
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

    static readonly System.Text.RegularExpressions.Regex Sections = new(
        @"<(Declaration|ST)><!\[CDATA\[(.*?)\]\]></\1>", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The ST sections (declaration and implementation) of a TwinCAT source file (.TcPOU, .TcGVL, .TcDUT, .TcIO).</summary>
    public static IEnumerable<(StPart Part, string Text)> SectionsOf(string xml) =>
        Sections.Matches(xml).Select(m => (m.Groups[1].Value == "ST" ? StPart.Implementation : StPart.Declaration, m.Groups[2].Value));

    /// <summary>--survey &lt;folder&gt;: formats every section of every TwinCAT source file in memory (nothing is written)
    /// and counts what would change and what could not be formatted - to test the formatter on real code.</summary>
    static int Survey(string folder, bool json)
    {
        var cfg = new StFormatConfig();
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
