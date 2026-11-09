// TcAgent.exe condition: a condition as logic (StLogic.cs) - for the Conditions window of the IDE and for scripts. Files only.
//
//   condition --stdin [--json]          the condition from stdin ("IF a AND NOT b THEN", "a AND b", "x := a OR b;")
//   condition --text "<condition>"      the same from the command line
//
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class CliCondition
{
    public static int Run(string[] args)
    {
        try
        {
            string text = null;
            bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            for (int i = 1; i < args.Length; i++)
                if (args[i].Equals("--text", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) text = args[++i];
                else if (args[i].Equals("--stdin", StringComparison.OrdinalIgnoreCase))
                {
                    Console.InputEncoding = new System.Text.UTF8Encoding(false);
                    text = Console.In.ReadToEnd();
                }
            if (string.IsNullOrWhiteSpace(text) && !args.Any(a => a.Equals("--instances", StringComparison.OrdinalIgnoreCase))) { Console.Error.WriteLine("Usage: TcAgent.exe condition --stdin | --text \"IF a AND NOT b THEN\" [--json]"); return Cli.Usage; }
            string O(string n) { var i = Array.FindIndex(args, a => a.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            // --instances --solution <dir> --pou <POU>: where the POU is used
            if (args.Any(a => a.Equals("--instances", StringComparison.OrdinalIgnoreCase)) && O("--pou") is { } ip)
            {
                var list = SymbolResolve.Instances(SymbolResolve.Index(O("--solution") ?? Directory.GetCurrentDirectory()), ip);
                Console.WriteLine(json ? new JsonArray(list.Select(x => (JsonNode)x).ToArray()).ToJsonString() : string.Join(Environment.NewLine, list));
                return Cli.Ok;
            }
            // --online [--netId <id>] [--port 851] [--watch <ms>]: read the values; with --watch one JSON line per reading until stdin closes
            if (args.Any(a => a.Equals("--online", StringComparison.OrdinalIgnoreCase)))
            {
                var idx = SymbolResolve.Index(O("--solution") ?? Directory.GetCurrentDirectory());
                using var w = new ConditionWatch(text, idx, O("--pou"), O("--method"), O("--instance"), O("--netId"), int.TryParse(O("--port"), out var port) ? port : 851);
                if (!int.TryParse(O("--watch"), out var ms)) { Console.WriteLine(w.Cycle().ToJsonString()); return Cli.Ok; }
                var stop = false;
                var reader = new Thread(() => { try { while (Console.In.ReadLine() != null) { } } catch { } stop = true; }) { IsBackground = true };
                reader.Start();
                while (!stop)
                {
                    Console.WriteLine(w.Cycle().ToJsonString());
                    Console.Out.Flush();
                    Thread.Sleep(Math.Max(100, ms));
                }
                return Cli.Ok;
            }
            var o = StLogic.Analyze(text);
            // --solution <dir> --pou <POU> [--method <M>] [--instance <path>]: where each variable comes from, its ADS path
            string Opt(string n) { var i = Array.FindIndex(args, a => a.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (Opt("--pou") is { } pou)
            {
                var idx = SymbolResolve.Index(Opt("--solution") ?? Directory.GetCurrentDirectory());
                var atomList = o["atoms"]!.AsArray();
                foreach (var a in atomList)
                {
                    var ops = SymbolResolve.Operands(idx, pou, Opt("--method"), Opt("--instance"), a!["text"]!.ToString());
                    a.AsObject()["operands"] = new JsonArray(ops.Select(x => (JsonNode)new JsonObject
                    {
                        ["text"] = x.Text, ["path"] = x.Path, ["constant"] = x.Constant, ["from"] = x.From, ["fromDe"] = x.FromDe, ["reason"] = x.Reason, ["reasonDe"] = x.ReasonDe,
                        ["pointerChecks"] = new JsonArray(x.PointerChecks.Select(p => (JsonNode)p).ToArray()),
                        ["indexes"] = new JsonArray(x.Indexes.Select(p => (JsonNode)new JsonObject { ["var"] = p.Var, ["path"] = p.Path }).ToArray())
                    }).ToArray());
                }
                if (!json)
                    foreach (var a in atomList)
                        foreach (var x in a!["operands"]!.AsArray())
                            Console.WriteLine($"  {x!["text"],-40} {(x["constant"]!.GetValue<bool>() ? "constant: " + x["from"] : x["path"] != null ? x["path"] + "  (" + x["from"] + ")" : "not readable: " + x["reason"])}");
            }
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (json) { Console.WriteLine(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); return Cli.Ok; }
            Console.WriteLine("Condition: " + o["text"]);
            var atoms = o["atoms"]!.AsArray().Select(a => a!["text"]!.ToString()).ToList();
            Console.WriteLine("Sub-conditions: " + string.Join(" · ", atoms.Select((a, i) => $"[{i + 1}] {a}")));
            if (o["trueRows"] is JsonArray rows)
            {
                Console.WriteLine("True when (one row each, 1 / 0 / - = any):");
                foreach (var r in rows) Console.WriteLine("  " + string.Join(" ", r!.ToString().Select(c => c)));
                if (rows.Count == 0) Console.WriteLine("  never");
            }
            foreach (var w in o["rewrites"]!.AsArray())
                Console.WriteLine($"{w!["title"]}: {w["text"]}" + (w["proven"]!.GetValue<bool>() ? $"   (proven over {w["combinations"]} combinations)" : "   (not proven: too many sub-conditions)"));
            foreach (var k in new[] { "rewriteNote", "simplifyNote" }) if (o[k] != null) Console.WriteLine("Note: " + o[k]);
            return Cli.Ok;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return Cli.Failed; }
    }
}
