// Runtime data backup and restore via ADS (RuntimeBackup.cs has the selection, the file and the conversion).
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace TcAgent;

public static class RuntimeBackupAds
{
    const int MaxValues = 200_000, MaxDepth = 8;

    static AdsClient Connect(string netId, int port)
    {
        var c = new AdsClient();
        c.Connect(string.IsNullOrEmpty(netId) || netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId), port);
        return c;
    }

    static ISymbolCollection<ISymbol> Symbols(AdsClient c) => SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree)).Symbols;

    static string ProjectName(ISymbolCollection<ISymbol> syms)
    {
        try { if (syms.TryGetInstance("TwinCAT_SystemInfoVarList._AppInfo.ProjectName", out var s) && s is IValueSymbol v) return v.ReadValue()?.ToString(); } catch { }
        return null;
    }

    /// <summary>Reads the selected values of a PLC (persistent, retain in the code, include patterns) into a backup object.</summary>
    public static JsonObject Backup(string netId, int port, string solutionDir, IEnumerable<string> include, IEnumerable<string> exclude, string version)
    {
        var declared = RuntimeBackup.ScanSolution(solutionDir);
        var (ci, ce) = RuntimeBackup.Config(solutionDir);
        var inc = (include ?? Enumerable.Empty<string>()).Concat(ci).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var exc = (exclude ?? Enumerable.Empty<string>()).Concat(ce).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        using var c = Connect(netId, port);
        var state = c.ReadState().AdsState;
        var syms = Symbols(c);
        var values = new JsonObject();
        var reasons = new Dictionary<string, int>();
        var skipped = new List<string>();

        void Collect(ISymbol s, string reason)
        {
            if (values.Count >= MaxValues) { skipped.Add(s.InstancePath + " (limit of " + MaxValues + " values)"); return; }
            if (exc.Any(p => RuntimeBackup.Matches(s.InstancePath, p))) return;
            if (s.Category is DataTypeCategory.Pointer or DataTypeCategory.Reference or DataTypeCategory.Interface) { skipped.Add(s.InstancePath + " (pointer/reference)"); return; }
            var subs = s.SubSymbols.Cast<ISymbol>().ToList();
            // an array of simple values as one array, everything else with sub-symbols per element
            if (subs.Count > 0 && !(s.Category == DataTypeCategory.Array && subs.All(x => x.SubSymbols.Count == 0 && x.Category is not (DataTypeCategory.Pointer or DataTypeCategory.Reference))))
            {
                foreach (var sub in subs) Collect(sub, reason);
                return;
            }
            try
            {
                var v = ((IValueSymbol)s).ReadValue();
                values[s.InstancePath] = new JsonObject { ["type"] = s.TypeName, ["value"] = RuntimeBackup.ToJson(v) };
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            }
            catch (Exception ex) { skipped.Add($"{s.InstancePath} ({ex.Message})"); }
        }

        void Walk(ISymbol s, int depth)
        {
            var path = s.InstancePath;
            if (exc.Any(p => RuntimeBackup.Matches(path, p))) return;
            string reason = s.IsPersistent ? "persistent"
                          : declared.Paths.Contains(path) || s.Parent is ISymbol parent && declared.FbVars.TryGetValue(parent.TypeName ?? "", out var vars) && vars.Contains(s.InstanceName) ? "retain/persistent in the code"
                          : inc.Any(p => RuntimeBackup.Matches(path, p)) ? "include" : null;
            if (reason != null) { Collect(s, reason); return; }
            if (depth >= MaxDepth || s.Category is DataTypeCategory.Pointer or DataTypeCategory.Reference or DataTypeCategory.Interface) return;
            // arrays of simple values hold nothing to look for
            if (s.Category == DataTypeCategory.Array && s.SubSymbols.Cast<ISymbol>().FirstOrDefault()?.SubSymbols.Count == 0) return;
            foreach (ISymbol sub in s.SubSymbols) Walk(sub, depth + 1);
        }

        foreach (ISymbol s in syms) Walk(s, 0);
        return new JsonObject
        {
            ["tcagent"] = version, ["created"] = DateTime.Now.ToString("s"), ["target"] = string.IsNullOrEmpty(netId) ? "local" : netId, ["port"] = port,
            ["project"] = ProjectName(syms), ["plcState"] = state.ToString(),
            ["selection"] = new JsonObject
            {
                ["include"] = new JsonArray(inc.Select(x => (JsonNode)x).ToArray()), ["exclude"] = new JsonArray(exc.Select(x => (JsonNode)x).ToArray()),
                ["declaredInCode"] = declared.Count, ["byReason"] = new JsonObject(reasons.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value)))
            },
            ["skipped"] = new JsonArray(skipped.Take(200).Select(x => (JsonNode)x).ToArray()),
            ["values"] = values
        };
    }

    public sealed class RestoreResult
    {
        public readonly List<string> Changed = new(), Missing = new(), TypeChanged = new(), Failed = new(), ReadOnly = new();
        public int Unchanged;
        public string PlcState, ProjectNow, ProjectInBackup;

        public string Report(bool dryRun, int max = 40)
        {
            var o = new StringBuilder();
            o.AppendLine($"{(dryRun ? "Would change" : "Changed")}: {Changed.Count} · unchanged: {Unchanged} · missing now: {Missing.Count} · type changed: {TypeChanged.Count} · " +
                         $"read-only: {ReadOnly.Count} · failed: {Failed.Count} (PLC {PlcState})");
            if (ProjectInBackup != null && ProjectNow != null && !ProjectInBackup.Equals(ProjectNow, StringComparison.OrdinalIgnoreCase))
                o.AppendLine($"NOTE: the backup is from the PLC project '{ProjectInBackup}', the PLC runs '{ProjectNow}'.");
            void Part(string title, List<string> l) { if (l.Count == 0) return; o.AppendLine(title + ":"); foreach (var x in l.Take(max)) o.AppendLine("  " + x); if (l.Count > max) o.AppendLine($"  ... {l.Count - max} more"); }
            Part(dryRun ? "Would change" : "Changed", Changed); Part("Missing in the PLC now (not written)", Missing); Part("Type changed (not written)", TypeChanged);
            Part("Read-only (not written)", ReadOnly); Part("Failed", Failed);
            return o.ToString().TrimEnd();
        }
    }

    /// <summary>Writes the values of a backup back - only existing symbols with the same type, only values that differ.</summary>
    public static RestoreResult Restore(JsonObject backup, string netId, int port, IEnumerable<string> only, bool dryRun, bool whileRunning)
    {
        var r = new RestoreResult { ProjectInBackup = backup["project"]?.ToString() };
        var filter = (only ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        using var c = Connect(netId, port);
        var state = c.ReadState().AdsState;
        r.PlcState = state.ToString();
        if (!dryRun && state == AdsState.Run && !whileRunning)
            throw new InvalidOperationException("The PLC is running - stop it first (the machine must be in a safe state), or restore with whileRunning=true " +
                                                "if writing these values during operation is safe.");
        var syms = Symbols(c);
        r.ProjectNow = ProjectName(syms);
        foreach (var (path, node) in (JsonObject)backup["values"]!)
        {
            if (filter.Count > 0 && !filter.Any(p => RuntimeBackup.Matches(path, p))) continue;
            if (!syms.TryGetInstance(path, out var s) || s is not IValueSymbol vs) { r.Missing.Add(path); continue; }
            var type = node?["type"]?.ToString();
            if (type != null && !type.Equals(s.TypeName, StringComparison.OrdinalIgnoreCase)) { r.TypeChanged.Add($"{path}: {type} -> {s.TypeName}"); continue; }
            if (s.IsReadOnly) { r.ReadOnly.Add(path); continue; }
            try
            {
                var current = vs.ReadValue();
                var value = RuntimeBackup.FromJson(node?["value"], current);
                if (RuntimeBackup.SameValue(current, value)) { r.Unchanged++; continue; }
                if (!dryRun) vs.WriteValue(value);
                r.Changed.Add($"{path}: {Short(RuntimeBackup.ToJson(current))} -> {Short(RuntimeBackup.ToJson(value))}");
            }
            catch (Exception ex) { r.Failed.Add($"{path}: {ex.Message}"); }
        }
        return r;
    }

    static string Short(JsonNode n) { var t = n?.ToJsonString() ?? "null"; return t.Length > 80 ? t[..80] + "…" : t; }
}
