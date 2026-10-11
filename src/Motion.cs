// Motion configuration (first step): NC tasks and NC axes.
//
//   tc_motion       lists NC tasks and axes with type and the linked drive (read only)
//   tc_motion_add   creates an NC task or NC axes; parameters afterwards with tc_xml_get / tc_xml_set
//
// Linking an axis to a drive (EtherCAT servo terminal / drive) is done in the IDE or via tc_xml_set on the axis -
// TcAgent shows the relevant XML so the agent can propose it.
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    // CreateChild subtypes below TINC (NC configuration)
    const int NcTaskSubType = 1, NcAxisSubType = 1;

    void RegisterMotion()
    {
        Add("tc_motion", "Lists the motion configuration: NC tasks and their axes (name, type, tree path). Read only.",
            Schema(), a => tc.Sta.Run(() => MotionList()));

        Add("tc_motion_add", "Creates motion configuration: kind=nc_task (NC task under MOTION) or kind=axis (NC axes under the Axes folder " +
                             "of an NC task; several at once with count). Afterwards parameters with tc_xml_get/tc_xml_set; the drive link " +
                             "is done in the IDE or via tc_xml_set on the axis. Takes effect after tc_activate.",
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
            foreach (var ax in list) o.AppendLine($"  {ax.Name}  [type={ax.ItemType} sub={ax.ItemSubType}]  {ax.PathName}");
        }
        o.Append("Parameters and drive link: tc_xml_get path=<axis path>.");
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
                o.Append("Next: link each axis to its drive (IDE: axis > Settings > Link To I/O, or tc_xml_get/tc_xml_set on the axis), then tc_activate.");
                return o.ToString();
            }
            default:
                throw new Exception("kind: nc_task or axis");
        }
    }
}
