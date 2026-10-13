// Motion configuration (first step): NC tasks and NC axes.
//
//   tc_motion       action=list (default, read only): NC tasks and axes with type and the linked I/O item
//                   action=link: links an NC axis with its drive (I/O box/terminal below TIID)
//   tc_motion_add   creates an NC task or NC axes; parameters afterwards with tc_xml_get / tc_xml_set
//
// Axis <-> drive link: Automation Interface sample "How to create/change a linkage of a nc axis, encoder or drive to an
// I/O device, box or terminal" (infosys tcautomationinterface/12425868683): ConsumeXml on the axis item with
//   <TreeItem><NcAxisDef><IoItem><PathName>TIID^...</PathName></IoItem></NcAxisDef></TreeItem>
// The sample is old (TwinCAT 2 era, Sercos box); on TwinCAT 3 it is not verified live yet. Therefore action=link is a
// dry run unless dryRun=false is passed, and after writing the axis XML is read back to check the link.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    // CreateChild subtypes below TINC (NC configuration)
    const int NcTaskSubType = 1, NcAxisSubType = 1;

    void RegisterMotion()
    {
        Add("tc_motion", "Motion configuration. action=list (default, read only): NC tasks and their axes (name, type, tree path, linked I/O item). " +
                         "action=link: links an NC axis with its drive - axis = axis path (from action=list), drive = I/O item of the drive below TIID " +
                         "(EtherCAT servo terminal/drive, e.g. 'TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 4 (EL7201)'; tc_tree to find it). " +
                         "Like 'Link To I/O...' in the axis settings. Default is a dry run that shows the XML; pass dryRun=false to write - the link " +
                         "is then read back from the axis. Changes the project only; takes effect with tc_activate.",
            Schema(P("action", "string", "list (default) or link"),
                   P("axis", "string", "link: NC axis, e.g. 'TINC^NC-Task 1 SAF^Axes^Axis 1'"),
                   P("drive", "string", "link: I/O item of the drive below TIID"),
                   P("dryRun", "boolean", "link: default true = only show what would be written; false = write")),
            a => tc.Sta.Run(() => (Str(a, "action") ?? "list").Trim().ToLowerInvariant() switch
            {
                "list" => MotionList(),
                "link" => MotionLink(a),
                _ => throw new Exception("action: list or link")
            }));

        Add("tc_motion_add", "Creates motion configuration: kind=nc_task (NC task under MOTION) or kind=axis (NC axes under the Axes folder " +
                             "of an NC task; several at once with count). Afterwards parameters with tc_xml_get/tc_xml_set; the drive link " +
                             "with tc_motion action=link. Takes effect after tc_activate.",
            Schema(Req(P("kind", "string", "nc_task or axis")),
                   P("name", "string", "Name (default: 'NC-Task n' or 'Axis n')"),
                   P("count", "integer", "axis: number of axes to create (default 1)"),
                   P("ncTaskPath", "string", "axis: the NC task, e.g. 'TINC^NC-Task 1 SAF' (empty = the only NC task)"),
                   P("dryRun", "boolean", "Only show what would be created")),
            a => tc.Sta.Run(() => MotionAdd(a)));
    }

    List<ITcSmTreeItem> NcTasks()
    {
        ITcSmTreeItem nc;
        try { nc = tc.Item("TINC"); } catch { return new List<ITcSmTreeItem>(); }
        return TwinCatSession.Children(nc).ToList();
    }

    static ITcSmTreeItem AxesFolder(ITcSmTreeItem ncTask) =>
        TwinCatSession.Children(ncTask).FirstOrDefault(c => c.Name.Equals("Axes", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("Achsen", StringComparison.OrdinalIgnoreCase));

    string MotionList()
    {
        var tasks = NcTasks();
        if (tasks.Count == 0) return "No NC task (MOTION is empty). Create one with tc_motion_add kind=nc_task.";
        var o = new StringBuilder();
        foreach (var t in tasks)
        {
            o.AppendLine($"{t.Name}  [type={t.ItemType} sub={t.ItemSubType}]  {t.PathName}");
            var axes = AxesFolder(t);
            if (axes == null) { o.AppendLine("  (no Axes folder)"); continue; }
            var list = TwinCatSession.Children(axes).ToList();
            if (list.Count == 0) o.AppendLine("  no axes");
            foreach (var ax in list)
            {
                string io;
                try { io = AxisIoLink(ax) ?? "-"; } catch { io = "?"; }
                o.AppendLine($"  {ax.Name}  [type={ax.ItemType} sub={ax.ItemSubType}]  {ax.PathName}  I/O: {io}");
            }
        }
        o.Append("Parameters: tc_xml_get path=<axis path>. Drive link: tc_motion action=link axis=<axis path> drive=<I/O path>.");
        return o.ToString();
    }

    string MotionAdd(JsonObject a)
    {
        var kind = (Str(a, "kind") ?? "").Trim().ToLowerInvariant();
        var name = Str(a, "name");
        switch (kind)
        {
            case "nc_task":
            {
                var nc = tc.Item("TINC");
                name ??= $"NC-Task {TwinCatSession.Children(nc).Count() + 1}";
                if (DryRun(a)) return $"PROPOSAL (nothing created): NC task '{name}' under {nc.PathName}";
                var t = (ITcSmTreeItem)((dynamic)nc).CreateChild(name, NcTaskSubType, "", null);
                return $"created: {t.PathName}" + (AxesFolder(t) is { } ax ? $"\nAxes folder: {ax.PathName}" : "");
            }
            case "axis":
            {
                ITcSmTreeItem task;
                var tp = Str(a, "ncTaskPath");
                if (!string.IsNullOrEmpty(tp)) task = tc.Item(tp);
                else
                {
                    var tasks = NcTasks();
                    task = tasks.Count == 1 ? tasks[0]
                         : throw new Exception(tasks.Count == 0 ? "No NC task - first tc_motion_add kind=nc_task."
                                                                : "Several NC tasks - pass ncTaskPath: " + string.Join(", ", tasks.Select(x => x.PathName)));
                }
                var axes = AxesFolder(task) ?? throw new Exception($"No Axes folder below {task.PathName}.");
                int count = Math.Max(1, Int(a, "count", 1));
                int existing = TwinCatSession.Children(axes).Count();
                var names = Enumerable.Range(0, count)
                    .Select(i => count == 1 && !string.IsNullOrEmpty(name) ? name
                               : !string.IsNullOrEmpty(name) ? $"{name}{i + 1}" : $"Axis {existing + i + 1}").ToList();
                if (DryRun(a)) return $"PROPOSAL (nothing created): below {axes.PathName}: {string.Join(", ", names)}";
                var o = new StringBuilder();
                foreach (var n in names)
                {
                    try { o.AppendLine("created: " + ((ITcSmTreeItem)((dynamic)axes).CreateChild(n, NcAxisSubType, "", null)).PathName); }
                    catch (Exception ex) { o.AppendLine($"{n}: ERROR {ex.InnerException?.Message ?? ex.Message}"); }
                }
                o.Append("Next: link each axis to its drive with tc_motion action=link axis=<axis> drive=<I/O path> (dry run first), then tc_activate.");
                return o.ToString();
            }
            default:
                throw new Exception("kind: nc_task or axis");
        }
    }

    /// <summary>I/O item the axis is linked to, from the axis XML (NcAxisDef/IoItem/PathName); null = none found.</summary>
    static string AxisIoLink(ITcSmTreeItem axis)
    {
        var doc = XDocument.Parse((string)((dynamic)axis).ProduceXml(false));
        var io = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "IoItem");
        var path = io?.Descendants().FirstOrDefault(e => e.Name.LocalName == "PathName")?.Value;
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    string MotionLink(JsonObject a)
    {
        var axisPath = Str(a, "axis") ?? throw new Exception("axis is required, e.g. 'TINC^NC-Task 1 SAF^Axes^Axis 1' (tc_motion lists the axes)");
        var drivePath = Str(a, "drive") ?? throw new Exception("drive is required: the I/O item of the drive below TIID (tc_tree path=TIID depth=4)");
        var axis = tc.Item(axisPath.Trim());
        if (!axis.PathName.StartsWith("TINC^", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"{axis.PathName} is not below TINC - pass an NC axis (tc_motion lists them).");
        ITcSmTreeItem drive;
        try { drive = tc.Item(drivePath.Trim()); }
        catch { throw new Exception($"I/O item not found: '{drivePath}' - look it up with tc_tree path=TIID depth=4."); }
        if (!drive.PathName.StartsWith("TIID^", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"{drive.PathName} is not an I/O item below TIID.");

        string before;
        try { before = AxisIoLink(axis); } catch (Exception ex) { before = "? (" + (ex.InnerException?.Message ?? ex.Message) + ")"; }
        var xml = new XElement("TreeItem", new XElement("NcAxisDef", new XElement("IoItem", new XElement("PathName", drive.PathName))))
                  .ToString(SaveOptions.DisableFormatting);
        var head = $"Axis: {axis.PathName}\nDrive (I/O item): {drive.PathName}  [type={drive.ItemType} sub={drive.ItemSubType}]\n" +
                   $"Current I/O link of the axis: {before ?? "none"}\n";
        bool dry = policy.ForceDryRun || Bool(a, "dryRun", true);
        if (dry)
            return head + "PROPOSAL (nothing written - repeat with dryRun=false to write). ConsumeXml on the axis:\n" + xml + "\n" +
                   "Format from the Automation Interface sample for linking an NC axis to an I/O box; not yet verified on every TwinCAT 3 " +
                   "version - the result is read back after writing.";

        ((dynamic)axis).ConsumeXml(xml);
        string after, axisXml;
        try { axisXml = (string)((dynamic)axis).ProduceXml(false); after = AxisIoLink(axis); }
        catch (Exception ex) { after = null; axisXml = "read-back failed: " + (ex.InnerException?.Message ?? ex.Message); }
        if (after != null && after.Equals(drive.PathName, StringComparison.OrdinalIgnoreCase))
            return head + $"LINKED (read back from the axis: {after}). TwinCAT maps the axis/encoder/drive process data to the I/O item; " +
                   "check with tc_io_vars on the axis or in the IDE (axis > Settings). Changes the project only; takes effect with tc_activate.";
        // not verified: show the I/O related part of the axis XML so the agent/user can see what happened
        string hint;
        try
        {
            hint = string.Join("\n", XDocument.Parse(axisXml).Descendants()
                .Where(e => e.Name.LocalName.Contains("Io", StringComparison.Ordinal) || e.Name.LocalName == "PathName")
                .Select(e => Shorten(e.ToString(SaveOptions.DisableFormatting), 600)).Take(10));
        }
        catch { hint = Shorten(axisXml, 1500); }
        return head + $"WRITTEN, BUT NOT VERIFIED: the axis XML does not show the drive as its I/O item afterwards (found: {after ?? "none"}). " +
               "Check in the IDE (axis > Settings > Link To I/O...) and report the result with tc_feedback. I/O related XML of the axis:\n" +
               (hint.Length > 0 ? hint : "(none)");
    }
}
