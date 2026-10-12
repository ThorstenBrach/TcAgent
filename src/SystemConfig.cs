// System configuration beyond tasks: real-time cores and the CoE startup list of EtherCAT devices.
//
//   tc_realtime   real-time settings of the project (node TIRS): available cores, cores for TwinCAT (shared/isolated),
//                 which cores run real time (affinity), base time, load limit and latency warning per core.
//                 Format: Automation Interface "Configuring CPU cores" - always the complete configuration (MaxCPUs + Affinity).
//                 Only the project is changed; isolating cores on the target needs "Set on target" + reboot (done by the user).
//   tc_coe        CoE startup list (InitCmds) of an EtherCAT device: list, add, remove. The list is written back as a whole
//                 (/TreeItem/EtherCAT/Slave/Mailbox/CoE/InitCmds), in the format the device itself produces.
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterSystemConfig()
    {
        Add("tc_realtime", "Real-time settings (cores): action=show (available cores, cores for TwinCAT, isolated cores, which cores run real time, " +
                           "base time / load limit / latency warning per core) or set: maxCpus (cores TwinCAT may use), isolated (how many of them " +
                           "are taken from Windows), cores (real-time cores, e.g. '1' or '1,2'), baseTimeUs, loadLimit (%), latencyWarning (value as shown) " +
                           "- applied to the listed cores. Changes the project; takes effect with tc_activate. Isolating cores additionally needs " +
                           "'Set on target' in the IDE's real-time settings and a reboot of the target (ask the user). Task cores: tc_task cores=...",
            Schema(Req(P("action", "string", "show or set")),
                   P("maxCpus", "integer", "set: number of cores TwinCAT may use"),
                   P("isolated", "integer", "set: how many of them are isolated from Windows (0 = shared only)"),
                   P("cores", "string", "set: real-time cores, e.g. '1' or '1,2' (core 0 = first core)"),
                   P("baseTimeUs", "integer", "set: base time in microseconds for the listed cores, e.g. 1000"),
                   P("loadLimit", "integer", "set: CPU limit in percent for the listed cores"),
                   P("latencyWarning", "integer", "set: latency warning for the listed cores (unit as shown by action=show)"),
                   P("dryRun", "boolean", "only show the XML")),
            a => tc.Sta.Run(() => Realtime(a)));

        Add("tc_coe", "CoE startup list of an EtherCAT device (parameters written at the state transition, e.g. PS): action=list, " +
                      "add (index, subIndex, value + dataType or data as hex bytes, transition default PS, comment) or remove (index, subIndex). " +
                      "path = tree path of the terminal/drive (tc_tree under TIID). The list is written back as a whole. Takes effect with tc_activate. " +
                      "Look up index/subindex in the device documentation (tc_docs) - never guess.",
            Schema(Req(P("action", "string", "list, add or remove")), Req(P("path", "string", "e.g. TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 2 (EL3102)")),
                   P("index", "string", "add/remove: object index, e.g. 0x8000"),
                   P("subIndex", "string", "add/remove: subindex, e.g. 0x06 or 6"),
                   P("value", "string", "add: value, e.g. 1000, TRUE, 0.5"),
                   P("dataType", "string", "add: BOOL, USINT/UINT8, UINT/UINT16, UDINT/UINT32, ULINT/UINT64, SINT/INT8, INT/INT16, DINT/INT32, LINT/INT64, REAL/REAL32, LREAL/REAL64"),
                   P("data", "string", "add: raw bytes as hex instead of value/dataType, e.g. e803"),
                   P("transition", "string", "add/remove: PS (default), SO, SP, OP, IP ..."),
                   P("comment", "string", "add: comment shown in the startup list"),
                   P("dryRun", "boolean", "only show the resulting XML")),
            a => tc.Sta.Run(() => Coe(a)));
    }

    // ================================================================== real-time cores
    static XElement Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    string Realtime(JsonObject a)
    {
        var rs = tc.Item("TIRS");
        var xml = (string)((dynamic)rs).ProduceXml(false);
        var doc = XDocument.Parse(xml);
        var def = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RTimeSetDef")
                  ?? throw new Exception("RTimeSetDef not found in the real-time settings (TIRS) - XML:\n" + Shorten(xml, 3000));
        var action = (Str(a, "action") ?? "show").ToLowerInvariant();
        if (action == "show") return DescribeRealtime(def);
        if (action != "set") throw new Exception("action: show or set");

        var maxEl = Child(def, "MaxCPUs");
        var affEl = Child(def, "Affinity");
        int max = a["maxCpus"] != null ? Int(a, "maxCpus", 1) : int.TryParse(maxEl?.Value, out var m0) ? m0 : 1;
        int? isolated = a["isolated"] != null ? Int(a, "isolated", 0) : int.TryParse(maxEl?.Attribute("NonWindowsCPUs")?.Value, out var i0) ? i0 : null;
        var cores = Str(a, "cores") is { } cs
            ? cs.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).Distinct().OrderBy(x => x).ToList()
            : CoresFromAffinity(affEl?.Value);
        if (cores.Count == 0) throw new Exception("cores: at least one real-time core, e.g. cores='1'");
        if (cores.Any(c => c < 0 || c >= 64)) throw new Exception("cores: 0 ... 63");
        if (isolated is int iso && (iso < 0 || iso > max)) throw new Exception("isolated must be between 0 and maxCpus");
        var avail = int.TryParse((Child(def, "AvailableCPUs") ?? Child(def, "AvailabeCPUs"))?.Value, out var av) ? av : 0;
        if (avail > 0 && max > avail) throw new Exception($"maxCpus {max} > available cores {avail}");

        // new complete RTimeSetDef: MaxCPUs (+NonWindowsCPUs), Affinity, CPUs for the real-time cores
        var nd = new XElement(def.Name);
        var maxNew = new XElement(maxEl?.Name ?? "MaxCPUs", max);
        if (isolated is int isoV) maxNew.SetAttributeValue("NonWindowsCPUs", isoV);
        nd.Add(maxNew);
        ulong mask = cores.Aggregate(0UL, (acc, c) => acc | (1UL << c));
        var affText = affEl?.Value ?? "#x0000000000000001";
        nd.Add(new XElement(affEl?.Name ?? "Affinity", FormatMask(mask, affText)));
        var cpusOld = Child(def, "CPUs");
        var cpus = new XElement(cpusOld?.Name ?? "CPUs");
        foreach (var c in cores)
        {
            var old = cpusOld?.Elements().FirstOrDefault(e => e.Attribute("id")?.Value == c.ToString(CultureInfo.InvariantCulture));
            var cpu = old != null ? new XElement(old) : new XElement("CPU", new XAttribute("id", c));
            void SetCpu(string n, string v) { var e = Child(cpu, n); if (e != null) e.Value = v; else cpu.Add(new XElement(n, v)); }
            if (a["baseTimeUs"] != null) SetCpu("BaseTime", (Int(a, "baseTimeUs", 1000) * 10L).ToString(CultureInfo.InvariantCulture));   // 100 ns units
            if (a["loadLimit"] != null) SetCpu("LoadLimit", Int(a, "loadLimit", 80).ToString(CultureInfo.InvariantCulture));
            if (a["latencyWarning"] != null) SetCpu("LatencyWarning", Int(a, "latencyWarning", 0).ToString(CultureInfo.InvariantCulture));
            cpus.Add(cpu);
        }
        nd.Add(cpus);
        var root = new XElement(doc.Root!.Name, nd);
        var outXml = root.ToString();
        if (DryRun(a)) return "Current:\n" + DescribeRealtime(def) + "\n\nPROPOSAL (nothing written):\n" + outXml;
        ((dynamic)rs).ConsumeXml(outXml);
        var after = XDocument.Parse((string)((dynamic)rs).ProduceXml(false)).Descendants().First(e => e.Name.LocalName == "RTimeSetDef");
        return "Real-time settings changed (project). Takes effect with tc_activate" +
               (isolated is > 0 ? "; isolated cores additionally need 'Set on target' in the IDE and a reboot of the target" : "") + ".\n" + DescribeRealtime(after);
    }

    static List<int> CoresFromAffinity(string aff)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(aff)) return list;
        var t = aff.Trim().TrimStart('#').TrimStart('x', 'X');
        if (aff.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = aff.Trim()[2..];
        if (!ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask)) return list;
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) list.Add(i);
        return list;
    }

    // keep the notation of the existing value (#x0000000000000002, 0x2, 2)
    static string FormatMask(ulong mask, string like)
    {
        var l = like.Trim();
        if (l.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) return l[..2] + mask.ToString("X").PadLeft(Math.Max(1, l.Length - 2), '0');
        if (l.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return "0x" + mask.ToString("X");
        return mask.ToString(CultureInfo.InvariantCulture);
    }

    static string DescribeRealtime(XElement def)
    {
        var o = new StringBuilder();
        var avail = (Child(def, "AvailableCPUs") ?? Child(def, "AvailabeCPUs"))?.Value;
        var max = Child(def, "MaxCPUs");
        var aff = Child(def, "Affinity")?.Value;
        o.AppendLine($"Available cores: {avail ?? "?"}");
        o.AppendLine($"Cores for TwinCAT (MaxCPUs): {max?.Value ?? "?"}" + (max?.Attribute("NonWindowsCPUs") is { } nw ? $", isolated from Windows: {nw.Value}" : ""));
        o.AppendLine($"Real-time cores (Affinity {aff ?? "?"}): {string.Join(", ", CoresFromAffinity(aff))}");
        if (Child(def, "CPUs") is { } cpus)
            foreach (var c in cpus.Elements())
            {
                var parts = c.Elements().Select(e => e.Name.LocalName == "BaseTime" && long.TryParse(e.Value, out var bt)
                    ? $"BaseTime {e.Value} (= {bt / 10.0:0.###} us)" : $"{e.Name.LocalName} {e.Value}");
                o.AppendLine($"  core {c.Attribute("id")?.Value}: {string.Join(", ", parts)}");
            }
        if (def.Descendants().FirstOrDefault(e => e.Name.LocalName == "CpuConfigValidOnTarget") is { } v)
            o.AppendLine($"Configuration valid on target: {v.Value}");
        return o.ToString().TrimEnd();
    }

    static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    // ================================================================== CoE startup list
    string Coe(JsonObject a)
    {
        var path = Str(a, "path") ?? throw new Exception("path is required");
        var item = tc.Item(path);
        var xml = (string)((dynamic)item).ProduceXml(false);
        var doc = XDocument.Parse(xml);
        var coe = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CoE" && e.Parent?.Name.LocalName == "Mailbox")
                  ?? throw new Exception($"{path} has no CoE mailbox (no EtherCAT device with CoE?) - check the path with tc_tree.");
        var cmds = Child(coe, "InitCmds");
        var action = (Str(a, "action") ?? "list").ToLowerInvariant();
        if (action == "list")
        {
            if (cmds == null || !cmds.Elements().Any()) return $"{path}: CoE startup list is empty.";
            var o = new StringBuilder($"{path}: CoE startup list ({cmds.Elements().Count()} entries)\n");
            int n = 0;
            foreach (var c in cmds.Elements())
                o.AppendLine($"  {++n,3}. " + DescribeInitCmd(c));
            return o.ToString().TrimEnd();
        }
        if (cmds == null) { cmds = new XElement("InitCmds"); coe.Add(cmds); }
        var existing = cmds.Elements().ToList();
        var index = ParseNum(Str(a, "index") ?? throw new Exception("index is required, e.g. 0x8000"));
        var sub = ParseNum(Str(a, "subIndex") ?? "0");
        var transition = (Str(a, "transition") ?? "PS").ToUpperInvariant();
        bool Matches(XElement c) => ParseNum(Child(c, "Index")?.Value ?? "-1") == index && ParseNum(Child(c, "SubIndex")?.Value ?? "-1") == sub
                                    && (Str(a, "transition") == null || string.Equals(Child(c, "Transition")?.Value, transition, StringComparison.OrdinalIgnoreCase));
        string done;
        if (action == "remove")
        {
            var hits = existing.Where(Matches).ToList();
            if (hits.Count == 0) throw new Exception($"no entry {Hex(index)}:{sub:X2} in the startup list");
            hits.ForEach(h => h.Remove());
            done = $"removed {hits.Count} entr{(hits.Count == 1 ? "y" : "ies")} {Hex(index)}:{sub:X2}";
        }
        else if (action == "add")
        {
            var data = Str(a, "data") is { } raw ? raw.Replace(" ", "").Replace("0x", "").ToLowerInvariant()
                     : EncodeValue(Str(a, "value") ?? throw new Exception("value (with dataType) or data is required"),
                                   Str(a, "dataType") ?? throw new Exception("dataType is required, e.g. UINT16"));
            if (data.Length == 0 || data.Length % 2 != 0 || !data.All(Uri.IsHexDigit)) throw new Exception("data: hex bytes, e.g. e803");
            // template: an existing entry of the device (keeps its element names, order and number format)
            var tpl = existing.FirstOrDefault();
            XElement cmd;
            if (tpl != null)
            {
                cmd = new XElement(tpl);
                cmd.Attributes().Where(x => x.Name.LocalName is "Fixed" or "fixed").Remove();
                SetChild(cmd, "Transition", transition);
                SetChild(cmd, "Index", FormatLike(index, Child(tpl, "Index")?.Value));
                SetChild(cmd, "SubIndex", FormatLike(sub, Child(tpl, "SubIndex")?.Value));
                SetChild(cmd, "Data", data);
                SetChild(cmd, "Comment", Str(a, "comment") ?? "");
                foreach (var flag in cmd.Elements().Where(e => e.Name.LocalName is "Fixed" or "Disabled").ToList()) flag.Remove();
            }
            else
                cmd = new XElement("InitCmd",
                    new XElement("Transition", transition), new XElement("Comment", Str(a, "comment") ?? ""), new XElement("Timeout", 0),
                    new XElement("Ccs", 1), new XElement("Index", index), new XElement("SubIndex", sub), new XElement("Data", data));
            existing.Where(Matches).ToList().ForEach(h => h.Remove());     // replace an existing entry for the same object
            cmds.Add(cmd);
            done = $"added {transition} {Hex(index)}:{sub:X2} = {data}" + (tpl == null ? " (no existing entry as template - generic format, check the result)" : "");
        }
        else throw new Exception("action: list, add or remove");

        // write back the whole startup list with its ancestors only
        XElement Wrap(XElement e)
        {
            XElement cur = new XElement(e);
            for (var p = e.Parent; p != null; p = p.Parent) cur = new XElement(p.Name, cur);
            return cur;
        }
        var outXml = Wrap(cmds).ToString();
        if (DryRun(a)) return "PROPOSAL (nothing written): " + done + "\n" + outXml;
        ((dynamic)item).ConsumeXml(outXml);
        return $"{path}: {done}. Takes effect with tc_activate.\n" + Coe(new JsonObject { ["action"] = "list", ["path"] = path });
    }

    static void SetChild(XElement parent, string name, string value)
    {
        var e = Child(parent, name);
        if (e != null) e.Value = value; else parent.Add(new XElement(name, value));
    }

    static string DescribeInitCmd(XElement c)
    {
        string V(string n) => Child(c, n)?.Value ?? "";
        var idx = V("Index"); var sub = V("SubIndex");
        var head = $"{V("Transition"),-3} {(idx.Length > 0 ? Hex(ParseNum(idx)) : "?")}:{(sub.Length > 0 ? ParseNum(sub).ToString("X2") : "??")} = {V("Data")}";
        var rest = c.Elements().Where(e => e.Name.LocalName is not ("Transition" or "Index" or "SubIndex" or "Data") && e.Value.Length > 0)
                    .Select(e => $"{e.Name.LocalName}={e.Value}");
        var attrs = c.Attributes().Select(x => $"@{x.Name.LocalName}={x.Value}");
        return head + "  " + string.Join(", ", attrs.Concat(rest));
    }

    static long ParseNum(string s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase)) return long.Parse(s[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return long.Parse(s, CultureInfo.InvariantCulture);
    }

    static string Hex(long v) => "0x" + v.ToString("X4");

    static string FormatLike(long v, string like)
    {
        like = (like ?? "").Trim();
        if (like.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) return "#x" + v.ToString("X").PadLeft(Math.Max(1, like.Length - 2), '0');
        if (like.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return "0x" + v.ToString("X");
        return v.ToString(CultureInfo.InvariantCulture);
    }

    // value -> little-endian hex bytes as in the startup list
    static string EncodeValue(string value, string type)
    {
        var v = value.Trim();
        byte[] b;
        long L() => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                                                                         : long.Parse(v, CultureInfo.InvariantCulture);
        switch (type.Trim().ToUpperInvariant())
        {
            case "BOOL": b = new[] { (byte)(v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? 1 : 0) }; break;
            case "USINT": case "UINT8": case "BYTE": case "SINT": case "INT8": b = new[] { unchecked((byte)L()) }; break;
            case "UINT": case "UINT16": case "WORD": case "INT": case "INT16": b = BitConverter.GetBytes(unchecked((ushort)L())); break;
            case "UDINT": case "UINT32": case "DWORD": case "DINT": case "INT32": b = BitConverter.GetBytes(unchecked((uint)L())); break;
            case "ULINT": case "UINT64": case "LWORD": case "LINT": case "INT64": b = BitConverter.GetBytes(L()); break;
            case "REAL": case "REAL32": b = BitConverter.GetBytes(float.Parse(v, CultureInfo.InvariantCulture)); break;
            case "LREAL": case "REAL64": b = BitConverter.GetBytes(double.Parse(v, CultureInfo.InvariantCulture)); break;
            default: throw new Exception("dataType: BOOL, USINT, UINT, UDINT, ULINT, SINT, INT, DINT, LINT, REAL, LREAL (or UINT8/16/32/64, INT8/16/32/64, REAL32/64)");
        }
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}
