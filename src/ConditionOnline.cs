// A condition online: the variables of its sub-conditions read over ADS - through the paths SymbolResolve found (inherited,
// reference, pointer after its check, array index after it was read) - the value of every sub-condition, the result and what
// is missing for TRUE. Only reads: no write, no method or property getter is called (a property is read only when it has
// {attribute 'monitoring' := 'call'}, which TwinCAT itself offers for monitoring). Reading is allowed on every target.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public sealed class ConditionWatch : IDisposable
{
    readonly AdsClient client = new();
    readonly ISymbolCollection<ISymbol> symbols;
    readonly LExpr expr;
    readonly List<List<ResolvedOperand>> operands;      // per atom
    public readonly string OnlineProject;

    public ConditionWatch(string text, ProjectIndex idx, string pou, string method, string instance, string netId, int port)
    {
        expr = StLogic.Parse(text);
        operands = expr.Atoms.Select(a => SymbolResolve.Operands(idx, pou, method, instance, a)).ToList();
        client.Connect(string.IsNullOrEmpty(netId) || netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId), port);
        symbols = SymbolLoaderFactory.Create(client, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree)).Symbols;
        try { if (symbols.TryGetInstance("TwinCAT_SystemInfoVarList._AppInfo.ProjectName", out var s) && s is IValueSymbol v) OnlineProject = v.ReadValue()?.ToString(); } catch { }
    }

    public void Dispose() => client.Dispose();

    /// <summary>The state of the PLC runtime ("Run", "Stop", "Config" ...) or "not reachable: ...".</summary>
    string State()
    {
        try { return client.ReadState().AdsState.ToString(); }
        catch (Exception ex) { return "not reachable: " + ex.Message; }
    }

    object Read(string path, out string error)
    {
        error = null;
        try
        {
            if (!symbols.TryGetInstance(path, out var s) || s is not IValueSymbol vs) { error = "not found in the PLC: " + path; return null; }
            var v = vs.ReadValue();
            // an enum: its name, so it compares with Type.VALUE
            if (s.DataType is IEnumType et && v != null)
                foreach (var ev in et.EnumValues)
                    if (Equals(Convert.ToInt64(ev.Primitive), Convert.ToInt64(v))) return ev.Name;
            return v;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>One reading of every operand and the result.</summary>
    public JsonObject Cycle()
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var ops = new JsonArray();
        for (int a = 0; a < operands.Count; a++)
            foreach (var o in operands[a])
            {
                var j = new JsonObject { ["atom"] = a, ["text"] = o.Text, ["from"] = o.From, ["fromDe"] = o.FromDe, ["comment"] = o.Comment };
                if (o.Constant) { j["constant"] = true; ops.Add(j); continue; }
                if (o.Path == null) { values[o.Text] = StLogic.Unknown; j["error"] = o.Reason; j["errorDe"] = o.ReasonDe; ops.Add(j); continue; }
                string path = o.Path, err = null;
                // array indexes first, then the pointers must not be 0
                for (int k = 0; k < o.Indexes.Count && err == null; k++)
                {
                    var iv = Read(o.Indexes[k].Path, out err);
                    if (err == null) path = path.Replace("{" + k + "}", Convert.ToString(iv, System.Globalization.CultureInfo.InvariantCulture));
                }
                foreach (var p in o.PointerChecks)
                {
                    if (err != null) break;
                    var pv = Read(p.Replace("{0}", ""), out err);
                    if (err == null && Convert.ToUInt64(pv) == 0) { err = "pointer is 0 (not set)"; j["errorDe"] = "Zeiger ist 0 (nicht gesetzt)"; }
                }
                object v = err == null ? Read(path, out err) : null;
                j["path"] = path;
                if (err != null) { values[o.Text] = StLogic.Unknown; j["error"] = err; ops.Add(j); continue; }
                values[o.Text] = v;
                j["value"] = v switch { null => null, bool b => b, string s => s, IConvertible c => JsonValue.Create(Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture)), _ => v.ToString() };
                ops.Add(j);
            }
        bool? Atom(int i) => StLogic.EvalAtom(expr.Atoms[i], n => values.TryGetValue(n, out var x) ? x : null);
        var atoms = new JsonArray(expr.Atoms.Select((t, i) => (JsonNode)new JsonObject { ["text"] = t, ["value"] = Atom(i) }).ToArray());
        JsonObject Tree(LNode n) => new()
        {
            ["op"] = n.Op, ["atom"] = n.Op == "atom" ? n.Atom : null, ["value"] = StLogic.Eval3(n, Atom),
            ["kids"] = new JsonArray(n.Kids.Select(k => (JsonNode)Tree(k)).ToArray())
        };
        var result = StLogic.Eval3(expr.Root, Atom);
        return new JsonObject
        {
            ["time"] = DateTime.Now.ToString("HH:mm:ss.fff"),
            ["result"] = result,
            ["atoms"] = atoms,
            ["operands"] = ops,
            ["tree"] = Tree(expr.Root),
            ["missing"] = result == true ? new JsonArray() : new JsonArray(StLogic.Missing(expr.Root, Atom)
                .Select(w => (JsonNode)new JsonArray(w.Select(x => (JsonNode)new JsonObject { ["atom"] = x.Atom, ["want"] = x.Want }).ToArray())).ToArray()),
            ["onlineProject"] = OnlineProject,
            ["state"] = State()
        };
    }
}
