// EtherCAT / I/O scan and runtime mode switch (config <-> run).
//
//   tc_runtime_mode   switch the TwinCAT system service of the target to config or run mode (runtime action)
//   tc_io_scan        list the I/O devices (network adapters with EtherCAT etc.) the target finds - read only
//   tc_io_scan_add    add a found device to the project, or assign its adapter to an existing device,
//                     then scan the boxes/terminals behind it
//
// A device scan needs the target in config mode. Assigning an adapter is what the IDE does with "Adapter > Search".
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using TCatSysManagerLib;
using TwinCAT.Ads;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    const int SystemServicePort = 10000;

    void RegisterIoScan()
    {
        Add("tc_runtime_mode", "Switches the TwinCAT runtime of the target system to Config mode (needed for the I/O scan, stops the PLC) " +
                               "or restarts it in Run mode with the active configuration. Runtime action: allowed targets only, with confirmation. " +
                               "tc_plc_state port=10000 reads the current state.",
            Schema(Req(P("mode", "string", "config or run")),
                   P("netId", "string", "AmsNetId (empty = target of the project)")),
            a => RuntimeMode(a));

        Add("tc_io_scan", "Searches for I/O devices on the target system (network adapters with EtherCAT, fieldbus cards ...) - read only, changes nothing. " +
                          "Needs Config mode (tc_runtime_mode mode=config). Then add them with tc_io_scan_add.",
            Schema(P("raw", "boolean", "Also show the XML of the devices found")),
            a => tc.Sta.Run(() => IoScan(Bool(a, "raw", false))));

        Add("tc_io_scan_add", "Adds a device found with tc_io_scan: create it, or - with devicePath - assign its adapter to an existing " +
                              "device (like 'Adapter > Search' in the IDE). Then the boxes/terminals behind it are scanned (scanBoxes). " +
                              "If terminals were created offline, TwinCAT may ask a question - TcAgent then reports the dialog.",
            Schema(Req(P("index", "integer", "Number of the device from tc_io_scan (1, 2, ...)")),
                   P("devicePath", "string", "Existing device to assign the adapter to, e.g. 'TIID^Device 1 (EtherCAT)' (empty = create new)"),
                   P("name", "string", "Name of the new device (empty = 'Device n (EtherCAT)' or the type name)"),
                   P("scanBoxes", "boolean", "Scan the boxes/terminals behind it (default true)"),
                   P("dryRun", "boolean", "Only show what would happen")),
            a => tc.Sta.Run(() => IoScanAdd(a), 300_000));
    }

    // ------------------------------------------------------------------ Runtime mode
    string RuntimeMode(JsonObject a)
    {
        var mode = (Str(a, "mode") ?? "").Trim().ToLowerInvariant();
        var net = Str(a, "netId");
        if (string.IsNullOrEmpty(net)) net = tc.Sta.Run(() => (string)((dynamic)tc.RequireSysMan()).GetTargetNetId());
        switch (mode)
        {
            case "config":
            {
                using var c = new AdsClient();
                c.Connect(AmsNetId.Parse(net), SystemServicePort);
                var before = c.ReadState().AdsState;
                if (before == AdsState.Config) return $"{net}: already in Config mode.";
                c.WriteControl(new StateInfo(AdsState.Reconfig, 0));
                var state = WaitState(c, AdsState.Config, 30);
                return $"{net}: {before} -> {state}" + (state == AdsState.Config ? "" : " (Config mode not reached - check the state with tc_plc_state port=10000)");
            }
            case "run":
                return tc.Sta.Run(() =>
                {
                    dynamic sm = tc.RequireSysMan();
                    if (!string.Equals((string)sm.GetTargetNetId(), net, StringComparison.OrdinalIgnoreCase)) sm.SetTargetNetId(net);
                    sm.StartRestartTwinCAT();
                    return $"{net}: TwinCAT is being restarted in Run mode with the active configuration.";
                });
            default:
                throw new Exception("mode: config or run");
        }
    }

    static AdsState WaitState(AdsClient c, AdsState want, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        AdsState s = AdsState.Invalid;
        while (DateTime.Now < end)
        {
            try { s = c.ReadState().AdsState; if (s == want) return s; } catch { /* service is restarting */ }
            Thread.Sleep(500);
        }
        return s;
    }

    // ------------------------------------------------------------------ Scan
    sealed record FoundDevice(int Index, int SubType, string TypeName, string Summary, XmlNode Address, string Xml);

    List<FoundDevice> ScanDevices()
    {
        var xml = (string)((dynamic)tc.Item("TIID")).ProduceXml(false);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var list = new List<FoundDevice>();
        int i = 0;
        foreach (XmlNode d in doc.SelectNodes("TreeItem/DeviceGrpDef/FoundDevices/Device") ?? (XmlNodeList)new XmlDocument().ChildNodes)
        {
            int.TryParse(d.SelectSingleNode("ItemSubType")?.InnerText, out var sub);
            var typeName = d.SelectSingleNode("ItemSubTypeName")?.InnerText ?? "";
            var addr = d.SelectSingleNode("AddressInfo");
            var summary = addr == null ? "" : string.Join(", ", addr.SelectNodes(".//*[not(*)]")!.Cast<XmlNode>()
                .Where(n => n.InnerText.Trim().Length > 0 && n.InnerText.Length < 120)
                .Select(n => $"{n.Name}={n.InnerText.Trim()}").Distinct().Take(6));
            list.Add(new FoundDevice(++i, sub, typeName, summary, addr, d.OuterXml));
        }
        return list;
    }

    string ConfigModeHint()
    {
        try
        {
            var net = (string)((dynamic)tc.RequireSysMan()).GetTargetNetId();
            using var c = new AdsClient();
            c.Connect(AmsNetId.Parse(net), SystemServicePort);
            var s = c.ReadState().AdsState;
            return s == AdsState.Config ? "" : $"\nNote: target system {net} is in state {s} - the scan needs Config mode (tc_runtime_mode mode=config).";
        }
        catch (Exception ex) { return "\nState of the target system not readable: " + (ex.InnerException?.Message ?? ex.Message); }
    }

    string IoScan(bool raw)
    {
        List<FoundDevice> found;
        try { found = ScanDevices(); }
        catch (Exception ex) { return "Scan failed: " + (ex.InnerException?.Message ?? ex.Message) + ConfigModeHint(); }
        if (found.Count == 0) return "No I/O devices found." + ConfigModeHint();
        var o = new StringBuilder($"{found.Count} device(s) found:\n");
        foreach (var f in found)
        {
            o.AppendLine($"  {f.Index}: {f.TypeName} (subtype {f.SubType}){(f.Summary.Length > 0 ? " - " + f.Summary : "")}");
            if (raw) o.AppendLine("     " + f.Xml);
        }
        o.Append("To add: tc_io_scan_add index=<n> (new device) or with devicePath (set the adapter of an existing device).");
        return o.ToString();
    }

    string IoScanAdd(JsonObject a)
    {
        var found = ScanDevices();
        int idx = Int(a, "index", 0);
        var f = found.FirstOrDefault(x => x.Index == idx)
                ?? throw new Exception($"No found device no. {idx} ({found.Count} found).{ConfigModeHint()}");
        if (f.Address == null) throw new Exception($"Device {idx} ({f.TypeName}) provides no address information.");
        var devicePath = Str(a, "devicePath");
        bool scanBoxes = Bool(a, "scanBoxes", true);
        var addressXml = $"<TreeItem><DeviceDef>{f.Address.OuterXml}</DeviceDef></TreeItem>";
        if (DryRun(a))
            return "PROPOSAL (nothing changed): " + (string.IsNullOrEmpty(devicePath)
                       ? $"create new device {f.TypeName} under TIID"
                       : $"set adapter of {devicePath} to {f.TypeName} ({f.Summary})") +
                   (scanBoxes ? ", then scan boxes." : ".");

        var o = new StringBuilder();
        ITcSmTreeItem dev;
        if (string.IsNullOrEmpty(devicePath))
        {
            var tiid = tc.Item("TIID");
            var name = Str(a, "name");
            if (string.IsNullOrEmpty(name))
                name = f.SubType == IoEtherCatMaster ? $"Device {TwinCatSession.Children(tiid).Count() + 1} (EtherCAT)"
                                                     : $"Device {TwinCatSession.Children(tiid).Count() + 1} ({f.TypeName})";
            dev = (ITcSmTreeItem)((dynamic)tiid).CreateChild(name, f.SubType, "", null);
            o.Append("created: ").Append(dev.PathName);
        }
        else
        {
            dev = tc.Item(devicePath);
            o.Append("adapter set: ").Append(dev.PathName);
        }
        ((dynamic)dev).ConsumeXml(addressXml);
        o.Append($" <- {f.TypeName}{(f.Summary.Length > 0 ? " (" + f.Summary + ")" : "")}");

        if (scanBoxes)
        {
            int before = TwinCatSession.Children(dev).Count();
            try
            {
                ((dynamic)dev).ConsumeXml("<TreeItem><DeviceDef><ScanBoxes>1</ScanBoxes></DeviceDef></TreeItem>");
                var boxes = Descendants(dev).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, @"^(Term|Box) \d+ \(")).Select(x => x.Name).ToList();
                o.Append($"\nBoxes scanned: {boxes.Count} ({TwinCatSession.Children(dev).Count() - before} new items directly under the device)");
                if (boxes.Count > 0) o.Append("\n  " + string.Join("\n  ", boxes.Take(60)) + (boxes.Count > 60 ? "\n  ..." : ""));
            }
            catch (Exception ex) { o.Append("\nBox scan failed: " + (ex.InnerException?.Message ?? ex.Message)); }
        }
        o.Append("\nNext: check tc_tree, link process data with tc_link, tc_activate (or tc_runtime_mode mode=run).");
        return o.ToString();
    }
}
