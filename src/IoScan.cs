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
        Add("tc_runtime_mode", "Schaltet die TwinCAT-Runtime des Zielsystems in den Config-Modus (noetig fuer den I/O-Scan, stoppt die PLC) " +
                               "oder startet sie mit der aktiven Konfiguration im Run-Modus neu. Laufzeit-Eingriff: nur freigegebene Ziele, mit Rueckfrage. " +
                               "Aktuellen Zustand liest tc_plc_state port=10000.",
            Schema(Req(P("mode", "string", "config oder run")),
                   P("netId", "string", "AmsNetId (leer = Ziel des Projekts)")),
            a => RuntimeMode(a));

        Add("tc_io_scan", "Sucht I/O-Geraete am Zielsystem (Netzwerkadapter mit EtherCAT, Feldbuskarten ...) - nur lesen, aendert nichts. " +
                          "Braucht den Config-Modus (tc_runtime_mode mode=config). Danach mit tc_io_scan_add uebernehmen.",
            Schema(P("raw", "boolean", "Zusaetzlich das XML der gefundenen Geraete zeigen")),
            a => tc.Sta.Run(() => IoScan(Bool(a, "raw", false))));

        Add("tc_io_scan_add", "Uebernimmt ein mit tc_io_scan gefundenes Geraet: neu anlegen oder - mit devicePath - dessen Adapter einem vorhandenen " +
                              "Geraet zuweisen (wie 'Adapter > Search' in der IDE). Danach werden die Boxen/Klemmen dahinter gescannt (scanBoxes). " +
                              "Bei offline angelegten Klemmen kann TwinCAT dabei nachfragen - dann meldet TcAgent den Dialog.",
            Schema(Req(P("index", "integer", "Nummer des Geraets aus tc_io_scan (1, 2, ...)")),
                   P("devicePath", "string", "Vorhandenes Geraet, dem der Adapter zugewiesen wird, z. B. 'TIID^Device 1 (EtherCAT)' (leer = neu anlegen)"),
                   P("name", "string", "Name des neuen Geraets (leer = 'Device n (EtherCAT)' bzw. Typname)"),
                   P("scanBoxes", "boolean", "Boxen/Klemmen dahinter scannen (Standard true)"),
                   P("dryRun", "boolean", "Nur zeigen, was passieren wuerde")),
            a => tc.Sta.Run(() => IoScanAdd(a), 300_000));
    }

    // ------------------------------------------------------------------ Runtime-Modus
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
                if (before == AdsState.Config) return $"{net}: bereits im Config-Modus.";
                c.WriteControl(new StateInfo(AdsState.Reconfig, 0));
                var state = WaitState(c, AdsState.Config, 30);
                return $"{net}: {before} -> {state}" + (state == AdsState.Config ? "" : " (Config-Modus nicht erreicht - Zustand mit tc_plc_state port=10000 pruefen)");
            }
            case "run":
                return tc.Sta.Run(() =>
                {
                    dynamic sm = tc.RequireSysMan();
                    if (!string.Equals((string)sm.GetTargetNetId(), net, StringComparison.OrdinalIgnoreCase)) sm.SetTargetNetId(net);
                    sm.StartRestartTwinCAT();
                    return $"{net}: TwinCAT wird mit der aktiven Konfiguration im Run-Modus neu gestartet.";
                });
            default:
                throw new Exception("mode: config oder run");
        }
    }

    static AdsState WaitState(AdsClient c, AdsState want, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        AdsState s = AdsState.Invalid;
        while (DateTime.Now < end)
        {
            try { s = c.ReadState().AdsState; if (s == want) return s; } catch { /* Dienst startet neu */ }
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
            return s == AdsState.Config ? "" : $"\nHinweis: Zielsystem {net} ist im Zustand {s} - der Scan braucht den Config-Modus (tc_runtime_mode mode=config).";
        }
        catch (Exception ex) { return "\nZustand des Zielsystems nicht lesbar: " + (ex.InnerException?.Message ?? ex.Message); }
    }

    string IoScan(bool raw)
    {
        List<FoundDevice> found;
        try { found = ScanDevices(); }
        catch (Exception ex) { return "Scan fehlgeschlagen: " + (ex.InnerException?.Message ?? ex.Message) + ConfigModeHint(); }
        if (found.Count == 0) return "Keine I/O-Geraete gefunden." + ConfigModeHint();
        var o = new StringBuilder($"{found.Count} Geraet(e) gefunden:\n");
        foreach (var f in found)
        {
            o.AppendLine($"  {f.Index}: {f.TypeName} (Subtyp {f.SubType}){(f.Summary.Length > 0 ? " - " + f.Summary : "")}");
            if (raw) o.AppendLine("     " + f.Xml);
        }
        o.Append("Uebernehmen: tc_io_scan_add index=<n> (neues Geraet) oder mit devicePath (Adapter eines vorhandenen Geraets setzen).");
        return o.ToString();
    }

    string IoScanAdd(JsonObject a)
    {
        var found = ScanDevices();
        int idx = Int(a, "index", 0);
        var f = found.FirstOrDefault(x => x.Index == idx)
                ?? throw new Exception($"Kein gefundenes Geraet Nr. {idx} ({found.Count} gefunden).{ConfigModeHint()}");
        if (f.Address == null) throw new Exception($"Geraet {idx} ({f.TypeName}) liefert keine Adressinformation.");
        var devicePath = Str(a, "devicePath");
        bool scanBoxes = Bool(a, "scanBoxes", true);
        var addressXml = $"<TreeItem><DeviceDef>{f.Address.OuterXml}</DeviceDef></TreeItem>";
        if (DryRun(a))
            return "VORSCHLAG (nichts geaendert): " + (string.IsNullOrEmpty(devicePath)
                       ? $"neues Geraet {f.TypeName} unter TIID anlegen"
                       : $"Adapter von {devicePath} auf {f.TypeName} ({f.Summary}) setzen") +
                   (scanBoxes ? ", danach Boxen scannen." : ".");

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
            o.Append("angelegt: ").Append(dev.PathName);
        }
        else
        {
            dev = tc.Item(devicePath);
            o.Append("Adapter gesetzt: ").Append(dev.PathName);
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
                o.Append($"\nBoxen gescannt: {boxes.Count} ({TwinCatSession.Children(dev).Count() - before} neue Elemente direkt unter dem Geraet)");
                if (boxes.Count > 0) o.Append("\n  " + string.Join("\n  ", boxes.Take(60)) + (boxes.Count > 60 ? "\n  ..." : ""));
            }
            catch (Exception ex) { o.Append("\nBoxen-Scan fehlgeschlagen: " + (ex.InnerException?.Message ?? ex.Message)); }
        }
        o.Append("\nDanach: tc_tree pruefen, Prozessdaten mit tc_link verknuepfen, tc_activate (bzw. tc_runtime_mode mode=run).");
        return o.ToString();
    }
}
