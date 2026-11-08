// Advisor Live at the running plant (docs/ADVISOR.md): reading the fault signals and the state of the PLCs via ADS, and the
// watch that starts an Advisor run when something goes wrong. Only reads - it never writes a variable or changes a state.
//
// What it notices without any setup, in every project:
//   a PLC leaves Run (Stop, Error, Exception), the runtime leaves Run, a task exceeds its cycle time (_TaskInfo[n].CycleTimeExceeded)
// and what the project or a plugin declares (AdvisorWatch.cs).
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public sealed class AdvisorReader : IDisposable
{
    readonly string netId;
    readonly Dictionary<int, AdsClient> clients = new();
    readonly Dictionary<int, ISymbolCollection<ISymbol>> symbols = new();

    public AdvisorReader(string netId) => this.netId = string.IsNullOrWhiteSpace(netId) ? "local" : netId.Trim();

    public string NetId => netId;

    AdsClient Client(int port)
    {
        if (clients.TryGetValue(port, out var c) && c.IsConnected) return c;
        c = new AdsClient { Timeout = 2000 };
        c.Connect(netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId), port);
        return clients[port] = c;
    }

    /// <summary>The ADS state of a port (10000 = the runtime, 851 = the first PLC), or the error text.</summary>
    public string State(int port)
    {
        try { return Client(port).ReadState().AdsState.ToString(); }
        catch (Exception ex) { return "not readable (" + (ex.InnerException?.Message ?? ex.Message) + ")"; }
    }

    /// <summary>The value of a variable as text (invariant culture), or null with the reason in why.</summary>
    public string Read(int port, string symbol, out string why)
    {
        why = null;
        try
        {
            if (!symbols.TryGetValue(port, out var syms))
                symbols[port] = syms = SymbolLoaderFactory.Create(Client(port), new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree)).Symbols;
            if (!syms.TryGetInstance(symbol, out var s) || s is not IValueSymbol v) { why = "symbol not found"; return null; }
            var value = v.ReadValue();
            return value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();
        }
        catch (Exception ex) { why = ex.InnerException?.Message ?? ex.Message; symbols.Remove(port); return null; }
    }

    /// <summary>Tasks of the PLC that exceeded their cycle time (index 1..8 of _TaskInfo).</summary>
    public List<int> CycleExceeded(int port)
    {
        var hit = new List<int>();
        for (int i = 1; i <= 8; i++)
        {
            var v = Read(port, $"TwinCAT_SystemInfoVarList._TaskInfo[{i}].CycleTimeExceeded", out var why);
            if (v == null) break;
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) hit.Add(i);
        }
        return hit;
    }

    public void Dispose()
    {
        foreach (var c in clients.Values) try { c.Dispose(); } catch { }
        clients.Clear(); symbols.Clear();
    }
}

/// <summary>One look at the plant: what is wrong now. The watch compares two looks and reports what newly went wrong.</summary>
public static class AdvisorLook
{
    public sealed record Finding(string Key, string Text, List<string> Details);

    public static List<Finding> Look(AdvisorReader r, IEnumerable<int> plcPorts, List<AdvisorWatch.Signal> signals, AdvisorWatch.State state, DateTime now)
    {
        var list = new List<Finding>();
        var rt = r.State(10000);
        if (rt != "Run") list.Add(new Finding("runtime", $"TwinCAT runtime is {rt}", new List<string>()));
        else
            foreach (var port in plcPorts.Distinct())
            {
                var st = r.State(port);
                if (st != "Run") { list.Add(new Finding($"plc:{port}", $"PLC on port {port} is {st}", new List<string>())); continue; }
                foreach (var t in r.CycleExceeded(port))
                    list.Add(new Finding($"cycle:{port}:{t}", $"Task {t} of the PLC on port {port} exceeded its cycle time", new List<string>()));
            }
        if (rt != "Run") return list;
        foreach (var s in signals)
        {
            var v = r.Read(s.Port, s.Symbol, out var why);
            if (v == null) continue;
            if (!state.Fires(s, v, now)) continue;
            var details = s.Details.Select(d => $"{d} = {r.Read(s.Port, d, out var dw) ?? "(" + dw + ")"}").ToList();
            list.Add(new Finding($"signal:{s.Port}:{s.Symbol}:{s.When}", $"{s.Text}: {s.Symbol} = {v} ({s.When})", details));
        }
        return list;
    }
}
