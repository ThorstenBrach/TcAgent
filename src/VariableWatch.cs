// The history of variables for the Mini-Scope of the IDE: TcAgent.exe watch reads the variables over ADS at a fixed interval
// (default 20 ms) and writes their samples with a time stamp as JSON lines (every 100 ms one line with the samples since the
// last one) until stdin closes. Each variable is followed to its ADS path like a condition (SymbolResolve: inherited, reference,
// pointer, array index, GVL). Only reads; reading is allowed on every target.
//
//   TcAgent.exe watch --vars "nPressure;bError" [--solution <dir> --pou <POU> --method <M> --instance <path>] [--netId <id>]
//                     [--port 851] [--interval 20]
//   first line:  {"vars":[{"name","path","from","error"}], "interval":20, "state":"Run"}
//   then:        {"s":[[ms, v1, v2 ...], ...]}       ms since the start; a value is a number, 0/1 for BOOL, or the enum name
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json.Nodes;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public static class VariableWatch
{
    public static int Run(string[] args)
    {
        string O(string n) { var i = Array.FindIndex(args, a => a.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var names = (O("--vars") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (names.Count == 0) { Console.Error.WriteLine("Usage: TcAgent.exe watch --vars \"a;b\" [--solution <dir> --pou <POU> --method <M> --instance <path>] [--netId <id>] [--interval 20]"); return Cli.Usage; }
        int interval = Math.Max(5, int.TryParse(O("--interval"), out var iv) ? iv : 20);
        var idx = O("--pou") != null ? SymbolResolve.Index(O("--solution") ?? Directory.GetCurrentDirectory()) : new ProjectIndex();
        // the paths: through the declarations, or the name as it is (MAIN.nCount)
        var vars = names.Select(n =>
        {
            var op = O("--pou") != null ? SymbolResolve.Operands(idx, O("--pou"), O("--method"), O("--instance"), n).FirstOrDefault() : null;
            return (Name: n, Path: op == null ? n : op.Path, From: op?.From ?? "", Error: op != null && op.Path == null ? op.Reason : null, Op: op);
        }).ToList();
        var netId = O("--netId");
        using var c = new AdsClient();
        c.Connect(string.IsNullOrEmpty(netId) || netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId), int.TryParse(O("--port"), out var port) ? port : 851);
        string state;
        try { state = c.ReadState().AdsState.ToString(); } catch (Exception ex) { state = "not reachable: " + ex.Message; }
        var syms = new ISymbol[vars.Count];
        var errors = vars.Select(v => v.Error).ToArray();
        try
        {
            var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree)).Symbols;
            for (int i = 0; i < vars.Count; i++)
            {
                if (errors[i] != null) continue;
                var path = vars[i].Path;
                // array indexes read once at the start (the Mini-Scope follows one element)
                if (vars[i].Op != null)
                    for (int k = 0; k < vars[i].Op.Indexes.Count; k++)
                        if (loader.TryGetInstance(vars[i].Op.Indexes[k].Path, out var ix) && ix is IValueSymbol ixv)
                            path = path.Replace("{" + k + "}", Convert.ToString(ixv.ReadValue(), System.Globalization.CultureInfo.InvariantCulture));
                if (loader.TryGetInstance(path, out var s) && s is IValueSymbol) syms[i] = s; else errors[i] = "not found in the PLC: " + path;
            }
        }
        catch (Exception ex) { for (int i = 0; i < vars.Count; i++) errors[i] ??= ex.Message; }
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.WriteLine(new JsonObject
        {
            ["vars"] = new JsonArray(vars.Select((v, i) => (JsonNode)new JsonObject
            {
                ["name"] = v.Name, ["path"] = v.Path, ["from"] = v.From, ["fromDe"] = v.Op?.FromDe, ["error"] = errors[i], ["errorDe"] = v.Op != null && v.Op.Path == null ? v.Op.ReasonDe : null, ["type"] = syms[i]?.TypeName,
                ["enum"] = syms[i]?.DataType is IEnumType et ? new JsonArray(et.EnumValues.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["value"] = Convert.ToDouble(e.Primitive) }).ToArray()) : null
            }).ToArray()),
            ["interval"] = interval, ["state"] = state
        }.ToJsonString());
        Console.Out.Flush();
        var stop = false;
        new Thread(() => { try { while (Console.In.ReadLine() != null) { } } catch { } stop = true; }) { IsBackground = true }.Start();
        var sw = Stopwatch.StartNew();
        var batch = new JsonArray();
        long lastOut = 0, next = 0;
        while (!stop)
        {
            var row = new JsonArray { JsonValue.Create(sw.ElapsedMilliseconds) };
            for (int i = 0; i < syms.Length; i++)
            {
                object v = null;
                try { v = (syms[i] as IValueSymbol)?.ReadValue(); } catch { }
                row.Add(v switch
                {
                    null => null,
                    bool b => JsonValue.Create(b ? 1 : 0),
                    string s => JsonValue.Create(s),
                    IConvertible cv => JsonValue.Create(Convert.ToDouble(cv, System.Globalization.CultureInfo.InvariantCulture)),
                    _ => JsonValue.Create(v.ToString())
                });
            }
            batch.Add(row);
            if (sw.ElapsedMilliseconds - lastOut >= 100)
            {
                Console.WriteLine(new JsonObject { ["s"] = batch }.ToJsonString());
                Console.Out.Flush();
                batch = new JsonArray();
                lastOut = sw.ElapsedMilliseconds;
            }
            next += interval;
            var wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait); else next = sw.ElapsedMilliseconds;     // behind: no catching up, the next sample now
        }
        return Cli.Ok;
    }
}
