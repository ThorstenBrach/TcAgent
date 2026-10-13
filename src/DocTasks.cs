// Tasks of a PLC project for the program documentation (tc_doc): which task calls which programs, with cycle time and
// priority.
//
//   1. real-time tasks under TIRT: name, CycleTime (100 ns ticks) and Priority from their ProduceXml
//   2. PLC task references (<task>.TcTTO, direct children of the PLC project, linked by name to TIRT^<task>) and their
//      program calls (children, e.g. ...^MyPlc Project^PlcTask^MAIN; also <PouCall><Name> in the reference's XML)
//   3. fallback (older layouts): any node in the PLC tree without declaration whose children are program calls
//
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;
using TCatSysManagerLib;

namespace TcAgent;

public sealed partial class PlcDoc
{
    public sealed class TaskInfo
    {
        public string Name, RtPath, RefPath, CycleTicks, Priority;
        public string Cycle => CycleText(CycleTicks);
    }

    /// <summary>Task references of this PLC project (same keys as <see cref="Tasks"/>).</summary>
    public readonly SortedDictionary<string, TaskInfo> TaskInfos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>TIRT tasks without a reference in this PLC project (other PLC projects, NC, C++, I/O tasks).</summary>
    public readonly List<TaskInfo> OtherRtTasks = new();
    bool rtRead;                                                   // TIRT could be read (otherwise no "missing TIRT task" findings)

    void CollectTasks(TwinCatSession tc, ITcSmTreeItem plcProject, List<ITcSmTreeItem> items, Dictionary<string, Pou> byPath)
    {
        // 1. real-time tasks
        var rt = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in TwinCatSession.Children(tc.Item("TIRT")))
            {
                string name, path, x = "";
                try { name = t.Name; path = t.PathName; } catch { continue; }
                try { x = (string)((dynamic)t).ProduceXml(false); } catch { }
                rt[name] = new TaskInfo { Name = name, RtPath = path, CycleTicks = XmlValue(x, "CycleTime"), Priority = XmlValue(x, "Priority") };
            }
            rtRead = true;
        }
        catch { }

        // 2. task references: direct children of the PLC project that are no folder, no PLC object and no library manager
        List<ITcSmTreeItem> direct;
        try { direct = TwinCatSession.Children(plcProject).ToList(); } catch { direct = new(); }
        foreach (var c in direct)
        {
            string name, path; int type;
            try { name = c.Name; path = c.PathName; type = c.ItemType; } catch { continue; }
            if (type == 601 || byPath.ContainsKey(path) || c is ITcPlcLibraryManager) continue;
            var progs = new List<string>();
            try { foreach (var k in TwinCatSession.Children(c)) AddProgram(progs, k.Name); } catch { }
            string x = "";
            if (progs.Count == 0 || !rt.ContainsKey(name)) try { x = (string)((dynamic)c).ProduceXml(false); } catch { }
            foreach (Match m in Regex.Matches(x, @"<PouCall\b[^>]*>.*?<Name>([^<]+)</Name>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                AddProgram(progs, m.Groups[1].Value);
            var isRef = progs.Count > 0 || rt.ContainsKey(name) || Regex.IsMatch(x, @"TcTTO|<PouCall\b", RegexOptions.IgnoreCase);
            if (!isRef) continue;
            AddTask(name, path, progs, rt, x);
        }

        // 3. fallback: a node without declaration whose children are program calls (folders are skipped and a child must not
        //    be the program object itself - a folder "POUs" holding MAIN is no task)
        if (Tasks.Count == 0)
            foreach (var it in items)
            {
                string path, name; try { path = it.PathName; name = it.Name; if (it.ItemType == 601) continue; } catch { continue; }
                if (byPath.ContainsKey(path)) continue;
                var progs = new List<string>();
                List<ITcSmTreeItem> kids;
                try { kids = TwinCatSession.Children(it).ToList(); } catch { continue; }
                foreach (var k in kids)
                {
                    try { if (Pous.TryGetValue(k.Name, out var p) && p.Kind == "PROGRAM" && !p.Path.Equals(k.PathName, StringComparison.OrdinalIgnoreCase)) progs.Add(p.Key); } catch { }
                }
                if (progs.Count > 0) AddTask(name, path, progs, rt, "");
            }

        foreach (var t in rt.Values.Where(q => !TaskInfos.ContainsKey(q.Name)).OrderBy(q => q.Name)) OtherRtTasks.Add(t);
    }

    void AddTask(string name, string refPath, List<string> progs, Dictionary<string, TaskInfo> rt, string refXml)
    {
        Tasks[name] = progs;
        var info = new TaskInfo { Name = name, RefPath = refPath };
        if (rt.TryGetValue(name, out var r)) { info.RtPath = r.RtPath; info.CycleTicks = r.CycleTicks; info.Priority = r.Priority; }
        else { info.CycleTicks = XmlValue(refXml, "CycleTime"); info.Priority = XmlValue(refXml, "Priority"); }   // values stored in the TcTTO
        TaskInfos[name] = info;
    }

    // program call "MAIN", "MAIN (PRG)" or "MyPlc.MAIN" -> key of the PROGRAM
    void AddProgram(List<string> progs, string callName)
    {
        var n = Regex.Replace(callName ?? "", @"\s*\(.*$", "").Trim();
        n = n.Split('.').Last();
        if (Pous.TryGetValue(n, out var p) && p.Kind == "PROGRAM" && p.Owner == null && !progs.Contains(p.Key, StringComparer.OrdinalIgnoreCase)) progs.Add(p.Key);
    }

    static string XmlValue(string xml, string tag)
    {
        var m = Regex.Match(xml ?? "", $@"<{tag}\b[^>]*>([^<]*)</{tag}>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // CycleTime in 100 ns ticks -> "10 ms" / "500 us"
    static string CycleText(string ticks)
    {
        if (string.IsNullOrEmpty(ticks)) return null;
        if (!long.TryParse(ticks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) return ticks;
        var us = t / 10.0;
        return us >= 1000 ? (us / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms" : us.ToString("0.###", CultureInfo.InvariantCulture) + " us";
    }

    /// <summary>"PlcTask (cycle 10 ms, priority 20)" - plain name when nothing is known.</summary>
    public string TaskLabel(string name)
    {
        if (!TaskInfos.TryGetValue(name, out var t)) return name;
        var parts = new List<string>();
        if (t.Cycle != null) parts.Add("cycle " + t.Cycle);
        if (!string.IsNullOrEmpty(t.Priority)) parts.Add("priority " + t.Priority);
        return parts.Count == 0 ? name : $"{name} ({string.Join(", ", parts)})";
    }

    /// <summary>Findings about the task configuration (part of <see cref="Findings"/>).</summary>
    List<string> TaskFindings()
    {
        var f = new List<string>();
        if (Tasks.Count == 0)
        {
            f.Add("PLC project has no task reference - no process image (tc_task action=assign)");
            return f;
        }
        foreach (var t in TaskInfos.Values)
        {
            if (rtRead && t.RtPath == null) f.Add($"Task reference {t.Name} has no real-time task TIRT^{t.Name} (tc_task action=list / create)");
            if (Tasks.TryGetValue(t.Name, out var progs) && progs.Count == 0) f.Add($"Task {t.Name} calls no program of this project (tc_task action=assign name={t.Name} program=...)");
        }
        foreach (var g in TaskInfos.Values.Where(q => !string.IsNullOrEmpty(q.Priority)).GroupBy(q => q.Priority).Where(q => q.Count() > 1))
            f.Add($"Tasks {string.Join(", ", g.Select(t => t.Name))} have the same priority {g.Key} - every task needs its own priority");
        return f;
    }
}
