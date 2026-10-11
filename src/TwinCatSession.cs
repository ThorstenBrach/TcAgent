// Connection to Visual Studio 2022 / TcXaeShell and access to the TwinCAT Automation Interface.
// All methods here are executed on the STA thread via StaWorker.Run.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using EnvDTE;
using EnvDTE80;
using TCatSysManagerLib;

namespace TcAgent;

public sealed class TwinCatSession
{
    public readonly StaWorker Sta;
    public DTE2 Dte { get; private set; }
    public ITcSysManager SysMan { get; private set; }
    public string InstanceInfo { get; private set; } = "not connected";
    /// <summary>Process ID of the connected IDE (0 = unknown, e.g. after tc_open) - for the VS extension TcAgentBridge.</summary>
    public int AttachedPid { get; private set; }

    // ProgIDs in the order in which new instances are started
    static readonly string[] ProgIds = { "VisualStudio.DTE.17.0", "TcXaeShell.DTE.17.0", "TcXaeShell.DTE.15.0" };

    public TwinCatSession(StaWorker sta) { Sta = sta; }

    // ------------------------------------------------------------------ Instances
    public record DteInstance(string Moniker, int Pid, string Solution, object Dte);

    public List<DteInstance> ListInstances()
    {
        var list = new List<DteInstance>();
        GetRunningObjectTable(0, out var rot);
        rot.EnumRunning(out var en);
        var monikers = new IMoniker[1];
        while (en.Next(1, monikers, IntPtr.Zero) == 0)
        {
            CreateBindCtx(0, out var ctx);
            monikers[0].GetDisplayName(ctx, null, out var name);
            if (!(name.StartsWith("!VisualStudio.DTE.") || name.StartsWith("!TcXaeShell.DTE."))) continue;
            rot.GetObject(monikers[0], out var obj);
            int pid = int.TryParse(name.Split(':').LastOrDefault(), out var p) ? p : 0;
            string sln = "";
            try { sln = ((DTE2)obj).Solution?.FullName ?? ""; } catch { }
            list.Add(new DteInstance(name, pid, sln, obj));
        }
        return list;
    }

    public string Attach(string solutionHint, int pid)
    {
        var inst = ListInstances();
        if (inst.Count == 0) throw new Exception("No running Visual Studio/XAE instance found. tc_open starts a new one.");
        // Selection rule: pid > solution hint > own IDE (TcAgent chat / terminal in VS) > TCAGENT_SOLUTION
        //                 > only instance with a solution > selection by the user. There is NO guessing.
        DteInstance pick = null;
        if (pid > 0)
            pick = inst.FirstOrDefault(i => i.Pid == pid) ?? throw new Exception($"No IDE with pid {pid}.\n" + Describe(inst));
        else if (!string.IsNullOrEmpty(solutionHint))
            pick = BySolution(inst, solutionHint);
        else
        {
            pick = HostIde(inst);
            var envSln = Environment.GetEnvironmentVariable("TCAGENT_SOLUTION");
            if (pick == null && !string.IsNullOrEmpty(envSln))
            {
                var hits = inst.Where(i => i.Solution.IndexOf(envSln, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) pick = hits[0];   // if TCAGENT_SOLUTION does not match (any more), continue with normal selection
            }
            if (pick == null)
            {
                var withSln = inst.Where(i => !string.IsNullOrEmpty(i.Solution)).ToList();
                if (withSln.Count == 1) pick = withSln[0];
                else pick = AskUser(withSln.Count > 1 ? withSln : inst)
                     ?? throw new Exception((withSln.Count > 1 ? $"{withSln.Count} TwinCAT projects are open." : "No instance has a solution open.") +
                                            " Ask the user which one is meant and call tc_attach with pid (the choice is remembered for this session).\n" + Describe(inst));
            }
        }
        Bind((DTE2)pick.Dte, $"{pick.Moniker} | {pick.Solution}");
        lastSolution = pick.Solution;
        AttachedPid = pick.Pid;
        return InstanceInfo;
    }

    static DteInstance BySolution(List<DteInstance> inst, string hint)
    {
        var hits = inst.Where(i => i.Solution.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (hits.Count != 1) throw new Exception($"'{hint}' matches {hits.Count} instances - please be more specific.\n" + Describe(inst));
        return hits[0];
    }

    /// <summary>The IDE from which TcAgent was (indirectly) started: TcAgent chat (TCAGENT_VS_PID) or terminal/process inside VS
    /// (devenv is an ancestor of this process). This way the chat never needs a project name.</summary>
    static DteInstance HostIde(List<DteInstance> inst)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("TCAGENT_VS_PID"), out var vsPid))
        {
            var hit = inst.FirstOrDefault(i => i.Pid == vsPid);
            if (hit != null) return hit;
        }
        try
        {
            int p = Environment.ProcessId;
            for (int depth = 0; depth < 12 && p > 4; depth++)
            {
                p = ParentPid(p);
                var hit = inst.FirstOrDefault(i => i.Pid == p);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>true if this process was (indirectly) started by process pid - e.g. from the TcAgent chat of this IDE.</summary>
    public static bool StartedFrom(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            int p = Environment.ProcessId;
            for (int depth = 0; depth < 12 && p > 4; depth++)
            {
                p = ParentPid(p);
                if (p == pid) return true;
            }
        }
        catch { }
        return false;
    }

    static int ParentPid(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : 0;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Selection list in the client (MCP elicitation), if the client supports it.</summary>
    static DteInstance AskUser(List<DteInstance> cands)
    {
        if (cands.Count == 0) return null;
        var enumVals = new System.Text.Json.Nodes.JsonArray();
        var enumNames = new System.Text.Json.Nodes.JsonArray();
        foreach (var i in cands)
        {
            enumVals.Add(i.Pid.ToString());
            enumNames.Add(string.IsNullOrEmpty(i.Solution) ? $"(no solution) - pid {i.Pid}" : $"{Path.GetFileNameWithoutExtension(i.Solution)}  -  {i.Solution}");
        }
        var schema = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "object",
            ["properties"] = new System.Text.Json.Nodes.JsonObject
            {
                ["instance"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "string", ["title"] = "TwinCAT project",
                    ["description"] = "Which Visual Studio instance should TcAgent work in?",
                    ["enum"] = enumVals, ["enumNames"] = enumNames
                }
            },
            ["required"] = new System.Text.Json.Nodes.JsonArray("instance")
        };
        var answer = Mcp.Elicit("Several TwinCAT projects are open - which one should TcAgent work with?", schema);
        var sel = answer?["instance"]?.ToString();
        return int.TryParse(sel, out var pid) ? cands.FirstOrDefault(i => i.Pid == pid) : null;
    }

    string lastSolution;

    // Check the connection and (re)establish it automatically if needed:
    // after a server restart (never connected) or when the IDE was restarted (COM object dead).
    void EnsureConnected()
    {
        if (Dte != null)
        {
            try { _ = Dte.Solution.FullName; return; }
            catch { Log.Info("Connection to the IDE lost - reconnecting"); Dte = null; SysMan = null; InstanceInfo = "not connected"; }
        }
        try
        {
            Attach(string.IsNullOrEmpty(lastSolution) ? null : lastSolution, 0);
            Log.Info("Connected automatically: " + InstanceInfo);
        }
        catch (Exception ex)
        {
            throw new Exception("Not connected, automatic connection not possible - call tc_attach with solution or pid.\n" + ex.Message);
        }
    }

    static string Describe(List<DteInstance> inst) =>
        "Running instances:\n" + string.Join("\n", inst.Select(i => $"  pid={i.Pid}  {i.Solution}"));

    public string Open(string solutionPath, bool visible, string progId)
    {
        if (!File.Exists(solutionPath)) throw new FileNotFoundException("Solution not found", solutionPath);
        var ids = string.IsNullOrEmpty(progId) ? ProgIds : new[] { progId };
        Exception last = null;
        foreach (var id in ids)
        {
            var t = Type.GetTypeFromProgID(id);
            if (t == null) continue;
            try
            {
                var dte = (DTE2)Activator.CreateInstance(t);
                dte.SuppressUI = !visible;
                dte.MainWindow.Visible = visible;
                dte.UserControl = visible;
                dte.Solution.Open(solutionPath);
                Bind(dte, $"{id} (started) | {solutionPath}");
                AttachedPid = 0;
                try { AttachedPid = ListInstances().FirstOrDefault(i => string.Equals(i.Solution, solutionPath, StringComparison.OrdinalIgnoreCase))?.Pid ?? 0; } catch { }
                return InstanceInfo;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new Exception("Could not start an IDE: " + (last?.Message ?? "no matching ProgID registered"));
    }

    /// <summary>Starts an IDE, creates a new solution with a TwinCAT XAE project from the Beckhoff project template and connects to it.</summary>
    public string NewSolution(string folder, string name, bool visible, string progId, string template)
    {
        template = string.IsNullOrEmpty(template) ? FindTemplate(@"Base\PrjTemplate\TwinCAT Project.tsproj") : template;
        if (template == null || !File.Exists(template))
            throw new Exception("TwinCAT project template not found (TwinCAT Project.tsproj) - pass template=<path>.");
        var dir = Path.Combine(folder, name);
        var sln = Path.Combine(dir, name + ".sln");
        if (File.Exists(sln)) throw new Exception("Solution already exists: " + sln + " - open it with tc_open.");
        var ids = string.IsNullOrEmpty(progId) ? ProgIds : new[] { progId };
        Exception last = null;
        foreach (var id in ids)
        {
            var t = Type.GetTypeFromProgID(id);
            if (t == null) continue;
            try
            {
                var dte = (DTE2)Activator.CreateInstance(t);
                dte.SuppressUI = !visible;
                dte.MainWindow.Visible = visible;
                dte.UserControl = visible;
                Directory.CreateDirectory(dir);
                dte.Solution.Create(folder, name);
                dte.Solution.SaveAs(sln);
                dte.Solution.AddFromTemplate(template, dir, name, false);
                dte.Solution.SaveAs(sln);
                Bind(dte, $"{id} (new solution) | {sln}");
                AttachedPid = 0;
                try { AttachedPid = ListInstances().FirstOrDefault(i => string.Equals(i.Solution, sln, StringComparison.OrdinalIgnoreCase))?.Pid ?? 0; } catch { }
                return InstanceInfo;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new Exception("Could not create the solution: " + (last?.Message ?? "no matching ProgID registered"));
    }

    /// <summary>Beckhoff template below ...\TwinCAT\3.1\Components\ (TWINCAT3DIR, C:\TwinCAT, Program Files (x86)).</summary>
    public static string FindTemplate(string relative)
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        if (!string.IsNullOrEmpty(env)) roots.Add(Path.Combine(env, "Components"));
        roots.Add(@"C:\TwinCAT\3.1\Components");
        roots.Add(@"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components");
        return roots.Select(r => Path.Combine(r, relative)).FirstOrDefault(File.Exists);
    }

    public void Save()
    {
        var dte = RequireDte();
        foreach (Project p in dte.Solution.Projects) { try { p.Save(); } catch { } }
        try { dte.Solution.SaveAs(dte.Solution.FullName); } catch { }
    }

    /// <summary>After tc_open/CLI: wait until the solution is loaded and the TwinCAT project is reachable.</summary>
    public void WaitForTwinCat(int timeoutSec)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = InstanceInfo.Split(" | TwinCAT project")[0].Split(" | WARNING")[0];
        while (true)
        {
            try
            {
                if (Dte.Solution.IsOpen)
                {
                    Bind(Dte, info);
                    if (SysMan != null) return;
                }
            }
            catch { /* IDE still busy */ }
            if (sw.Elapsed.TotalSeconds > timeoutSec) throw new Exception($"TwinCAT project not loaded after {timeoutSec} s: {InstanceInfo}");
            System.Threading.Thread.Sleep(1000);
        }
    }

    /// <summary>Close a self-started IDE without saving (CLI).</summary>
    public void CloseIde()
    {
        if (Dte == null) return;
        try { Dte.Solution.Close(false); } catch { }
        try { Dte.Quit(); } catch { }
        Dte = null; SysMan = null; InstanceInfo = "not connected";
    }

    void Bind(DTE2 dte, string info)
    {
        Dte = dte;
        SysMan = null;
        foreach (Project p in dte.Solution.Projects)
        {
            try { if (p.Object is ITcSysManager sm) { SysMan = sm; info += $" | TwinCAT project: {p.Name}"; break; } } catch { }
        }
        InstanceInfo = info + (SysMan == null ? " | WARNING: no TwinCAT project in the solution" : "");
    }

    public ITcSysManager RequireSysMan()
    {
        EnsureConnected();
        return SysMan ?? throw new Exception("The connected solution contains no TwinCAT project - tc_attach to a different solution.");
    }

    public DTE2 RequireDte() { EnsureConnected(); return Dte; }

    // ------------------------------------------------------------------ Tree
    public ITcSmTreeItem Item(string path)
    {
        try { return RequireSysMan().LookupTreeItem(path); }
        catch (COMException) { throw new Exception($"Tree item not found: '{path}'"); }
    }

    public static IEnumerable<ITcSmTreeItem> Children(ITcSmTreeItem item)
    {
        foreach (ITcSmTreeItem c in item) yield return c;
    }

    public string Tree(string path, int depth)
    {
        var sb = new StringBuilder();
        void Walk(ITcSmTreeItem it, int level)
        {
            sb.Append(' ', level * 2).Append(it.Name)
              .Append($"  [type={it.ItemType} sub={it.ItemSubType}]  {it.PathName}").AppendLine();
            if (level >= depth) return;
            foreach (var c in Children(it)) Walk(c, level + 1);
        }
        if (string.IsNullOrEmpty(path))
            foreach (var root in new[] { "TIRC", "TIRS", "TIRT", "TIPC", "TIID", "TINC", "TICC", "TIAC" })
            {
                ITcSmTreeItem r = null;
                try { r = RequireSysMan().LookupTreeItem(root); } catch { }
                if (r != null) Walk(r, 0);
            }
        else Walk(Item(path), 0);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Build / error list
    public string Build(string projectHint, bool rebuild, bool checkAll = true)
    {
        var dte = RequireDte();
        var sb = dte.Solution.SolutionBuild;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (rebuild) sb.Clean(true);
        sb.Build(true);
        var tBuild = sw.Elapsed.TotalSeconds;
        var failed = sb.LastBuildInfo;
        // TwinCAT only compiles POUs that are used - "Check all objects" also finds errors in unused code
        var checkedInfo = "";
        if (checkAll)
            foreach (var plc in PlcProjects())
                checkedInfo += CheckAllObjects(plc) ? $"All objects checked: {plc.Name}\n" : $"Check all objects not available: {plc.Name}\n";
        var tCheck = sw.Elapsed.TotalSeconds - tBuild;
        var (compile, runtime) = ReadErrorList(1000);
        int errs = compile.Count(e => e.Level == "ERROR"), warns = compile.Count(e => e.Level == "WARN");
        var o = new StringBuilder($"Build {(failed == 0 && errs == 0 ? "SUCCEEDED" : "FAILED")}: " +
                                  $"{failed} failed projects, {errs} compiler errors, {warns} warnings\n");
        o.Append(checkedInfo);
        o.AppendLine(System.FormattableString.Invariant($"Duration: {sw.Elapsed.TotalSeconds:0.0} s (build {tBuild:0.0} s") +
                     (checkAll ? System.FormattableString.Invariant($", check all objects {tCheck:0.0} s)") : ")"));
        foreach (var e in compile) o.AppendLine(e.ToString());
        if (runtime.Count > 0)
            o.AppendLine($"({runtime.Count} runtime/TcUnit messages hidden - see tc_errors kind=runtime or tc_test_results)");
        return o.ToString();
    }

    // Only "Check all objects": compiler errors without a build (no executable code, no files)
    public string Check()
    {
        RequireDte();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = new StringBuilder();
        var plcs = PlcProjects();
        if (plcs.Count == 0) return "No PLC project found.";
        foreach (var plc in plcs)
            if (!CheckAllObjects(plc)) info.AppendLine($"Check all objects not available: {plc.Name} - use tc_build instead");
        var (compile, runtime) = ReadErrorList(1000);
        int errs = compile.Count(e => e.Level == "ERROR"), warns = compile.Count(e => e.Level == "WARN");
        var o = new StringBuilder($"Check {(errs == 0 ? "OK" : "ERRORS")}: {errs} compiler errors, {warns} warnings " +
                                  $"({string.Join(", ", plcs.Select(p => p.Name))}; " +
                                  System.FormattableString.Invariant($"{sw.Elapsed.TotalSeconds:0.0} s)") + "\n");
        o.Append(info);
        foreach (var e in compile) o.AppendLine(e.ToString());
        if (runtime.Count > 0)
            o.AppendLine($"({runtime.Count} runtime/TcUnit messages hidden - see tc_errors kind=runtime or tc_test_results)");
        return o.ToString();
    }

    // PLC projects (type 600) under TIPC
    public List<ITcSmTreeItem> PlcProjects()
    {
        var list = new List<ITcSmTreeItem>();
        foreach (var plc in Children(Item("TIPC")))
            foreach (var c in Children(plc))
                if (c.ItemType == 600) list.Add(c);
        return list;
    }

    // All PLC objects below the PLC projects (or a start node), without library references
    public IEnumerable<ITcSmTreeItem> PlcObjects(ITcSmTreeItem root = null)
    {
        var roots = root != null ? new List<ITcSmTreeItem> { root } : PlcProjects();
        foreach (var r in roots)
            foreach (var x in WalkPlc(r)) yield return x;
    }

    static IEnumerable<ITcSmTreeItem> WalkPlc(ITcSmTreeItem it)
    {
        var kids = new List<ITcSmTreeItem>();
        try { kids.AddRange(Children(it)); } catch { }
        foreach (var c in kids)
        {
            if (c is ITcPlcLibraryManager) continue;
            yield return c;
            foreach (var g in WalkPlc(c)) yield return g;
        }
    }

    // ------------------------------------------------------------------ Editor context
    // What the user currently has open/selected in the IDE. Cursor/selection only as far as the editor exposes them via DTE.
    public string EditorContext()
    {
        var dte = RequireDte();
        var o = new StringBuilder();
        static string S(Func<string> f) { try { return f() ?? ""; } catch (Exception ex) { return "(" + ex.Message + ")"; } }

        var caption = S(() => dte.ActiveWindow.Caption);
        o.AppendLine("Active window: " + caption);
        Document doc = null;
        try { doc = dte.ActiveDocument; } catch { }
        var names = new List<string> { caption };
        if (doc != null)
        {
            o.AppendLine("Active document: " + S(() => doc.FullName));
            names.Add(S(() => doc.Name));
            // TwinCAT opens methods/properties as "<path>\\FB.TcPOU@Method" -> qualified name "FB.Method".
            // Important because the window caption is missing when the chat or another tool window has focus
            // and a plain name like "Start" can occur in several FBs.
            var full = S(() => doc.FullName);
            var at = full.LastIndexOf('@');
            if (at > 0)
            {
                var pou = System.IO.Path.GetFileNameWithoutExtension(full[..at]);
                var member = full[(at + 1)..];
                if (pou.Length > 0 && member.Length > 0) names.Insert(0, pou + "." + member);
            }
            else if (full.Length > 0) names.Insert(0, System.IO.Path.GetFileNameWithoutExtension(full));
            TextSelection ts = null;
            try { ts = doc.Selection as TextSelection; } catch { }
            if (ts == null) try { ts = (doc.Object("TextDocument") as TextDocument)?.Selection; } catch { }
            if (ts != null)
            {
                o.AppendLine($"Cursor: line {S(() => ts.ActivePoint.Line.ToString())}, column {S(() => ts.ActivePoint.LineCharOffset.ToString())}");
                var sel = S(() => ts.Text);
                if (sel.Length > 0) o.AppendLine("Selection:\n" + (sel.Length > 4000 ? sel[..4000] + "..." : sel));
                var line = S(() => { var ep = ts.ActivePoint.CreateEditPoint(); ep.StartOfLine(); return ep.GetLines(ep.Line, ep.Line + 1); });
                if (line.Length > 0) o.AppendLine("Current line: " + line.TrimEnd());
            }
            else o.AppendLine("Cursor/selection: not available from the TwinCAT editor via DTE (provided by the TcAgentBridge extension)");
        }
        else o.AppendLine("No active document");

        var selected = new List<string>();
        try { foreach (SelectedItem si in dte.SelectedItems) { var n = S(() => si.Name); if (n.Length > 0) selected.Add(n); } } catch { }
        if (selected.Count > 0) { o.AppendLine("Selected in Solution Explorer: " + string.Join(", ", selected)); names.AddRange(selected); }

        // Map window/document names to TwinCAT tree paths so that tc_plc_read can be used directly
        var paths = ResolvePlcPaths(names);
        o.AppendLine(paths.Count > 0 ? "Tree path(s):\n  " + string.Join("\n  ", paths) : "Tree path: cannot be determined unambiguously");
        return o.ToString();
    }

    // Names + paths of all PLC objects, cached briefly. Every COM access runs on the VS UI thread -
    // a full tree walk on every context query makes the IDE (including the chat window) stutter.
    List<(string Name, string Path)> plcIndex;
    DateTime plcIndexAt;
    public void InvalidatePlcIndex() => plcIndex = null;
    public List<(string Name, string Path)> PlcIndex()
    {
        if (plcIndex != null && DateTime.UtcNow - plcIndexAt < TimeSpan.FromMinutes(2)) return plcIndex;
        var list = new List<(string, string)>();
        foreach (var it in PlcObjects())
            try { list.Add((it.Name, it.PathName)); } catch { }
        plcIndex = list; plcIndexAt = DateTime.UtcNow;
        return list;
    }

    public List<string> ResolvePlcPathsPublic(string dottedName) => ResolvePlcPaths(new[] { dottedName });

    List<string> ResolvePlcPaths(IEnumerable<string> rawNames)
    {
        var wanted = new List<string[]>();
        foreach (var raw in rawNames)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("(")) continue;
            var n = raw.Trim().TrimEnd('*').Trim();
            foreach (var cut in new[] { " [", " (", " - " }) { var i = n.IndexOf(cut, StringComparison.Ordinal); if (i > 0) n = n[..i]; }
            foreach (var ext in new[] { ".TcPOU", ".TcDUT", ".TcGVL", ".TcIO", ".TcTTO" })
                if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) n = n[..^ext.Length];
            if (n.Length > 0) wanted.Add(n.Split('.'));
        }
        var found = new List<string>(); var qualified = new List<string>();
        if (wanted.Count == 0) return found;
        foreach (var (name, path) in PlcIndex())
        {
            foreach (var w in wanted)
            {
                bool hit = w.Length == 1 ? name == w[0]
                         : name == w[^1] && path.Contains("^" + w[^2] + "^", StringComparison.Ordinal);
                if (hit && !found.Contains(path)) found.Add(path);
                if (hit && w.Length > 1 && !qualified.Contains(path)) qualified.Add(path);
            }
        }
        // qualified hits (FB.Method) are unambiguous and beat plain name hits
        if (qualified.Count > 0) found = qualified;
        // most specific hits (method before FB) first
        return found.OrderByDescending(p => p.Count(c => c == '^')).ToList();
    }

    // Calls CheckAllObjects via the matching Automation Interface (ITcPlcIECProject*)
    public bool CheckAllObjects(ITcSmTreeItem plcProject)
    {
        foreach (var t in typeof(ITcSysManager).Assembly.GetTypes().Where(t => t.IsInterface).OrderByDescending(t => t.Name))
        {
            var m = t.GetMethod("CheckAllObjects", Type.EmptyTypes);
            if (m == null || !t.IsInstanceOfType(plcProject)) continue;
            m.Invoke(plcProject, null);
            return true;
        }
        return false;
    }

    /// <param name="Stamp">Timestamp of a runtime message (local time), otherwise null</param>
    public record ErrorEntry(string Level, string Project, string File, int Line, int Column, string Text, bool Runtime, DateTime? Stamp = null)
    {
        public override string ToString() => $"{Level}\t{Project}\t{File}:{Line}:{Column}\t{Text}";
    }

    // PLC runtime messages (e.g. TcUnit, ADSLOGSTR) also appear in the error list:
    // without a file, the text starts with a timestamp and "| 'Task' (Port):".
    // Date format depends on the IDE language: 11.10.2026 / 10/11/2026 / 2026-10-11, time possibly with AM/PM.
    static readonly System.Text.RegularExpressions.Regex RuntimeMsg =
        new(@"^(?<ts>\d{1,4}[./-]\d{1,2}[./-]\d{1,4} \d{1,2}:\d{2}:\d{2}(?:\s?[AaPp][Mm])?)(?:\s+(?<ms>\d{1,3})\s*ms)?.*?\|\s*'[^']*'\s*\(\d+\):\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly string[] StampFormats =
        { "d.M.yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "d/M/yyyy H:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy/MM/dd H:mm:ss" };

    static DateTime? ParseStamp(string ts) =>
        DateTime.TryParseExact(ts.Trim(), StampFormats, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.AllowInnerWhite, out var d) ? d : null;

    public (List<ErrorEntry> compile, List<ErrorEntry> runtime) ReadErrorList(int max)
    {
        var dte = RequireDte();
        var items = dte.ToolWindows.ErrorList.ErrorItems;
        var compile = new List<ErrorEntry>(); var runtime = new List<ErrorEntry>();
        int n = items.Count;
        for (int i = 1; i <= n && i <= max; i++)
        {
            var e = items.Item(i);
            string lvl = e.ErrorLevel switch
            {
                vsBuildErrorLevel.vsBuildErrorLevelHigh => "ERROR",
                vsBuildErrorLevel.vsBuildErrorLevelMedium => "WARN",
                _ => "INFO"
            };
            string text = e.Description ?? "";
            var m = RuntimeMsg.Match(text);
            bool rt = string.IsNullOrEmpty(e.FileName) && m.Success;
            var entry = new ErrorEntry(lvl, e.Project, e.FileName, e.Line, e.Column, rt ? text.Substring(m.Length) : text, rt,
                                       rt ? ParseStamp(m.Groups["ts"].Value)?.AddMilliseconds(m.Groups["ms"].Success ? int.Parse(m.Groups["ms"].Value) : 0) : null);
            (rt ? runtime : compile).Add(entry);
        }
        return (compile, runtime);
    }

    /// <summary>Runtime messages in chronological order (the error list is not reliably sorted;
    /// equal timestamps keep their order in the list, following the list's direction).</summary>
    public static List<ErrorEntry> Chronological(List<ErrorEntry> entries)
    {
        var withStamp = entries.Where(e => e.Stamp != null).ToList();
        bool newestFirst = withStamp.Count > 1 && withStamp[0].Stamp > withStamp[^1].Stamp;
        return entries.Select((e, i) => (e, i))
                      .OrderBy(x => x.e.Stamp ?? DateTime.MinValue)
                      .ThenBy(x => newestFirst ? -x.i : x.i)
                      .Select(x => x.e).ToList();
    }

    public string ErrorList(int max, string kind)
    {
        var (compile, runtime) = ReadErrorList(max);
        var o = new StringBuilder();
        if (kind != "runtime")
        {
            o.AppendLine($"Compiler/project: {compile.Count} entries ({compile.Count(e => e.Level == "ERROR")} errors, {compile.Count(e => e.Level == "WARN")} warnings)");
            foreach (var e in compile) o.AppendLine(e.ToString());
        }
        if (kind != "compile")
        {
            o.AppendLine($"Runtime/TcUnit: {runtime.Count} messages (newest first)");
            foreach (var e in runtime) o.AppendLine(e.Text);
        }
        return o.ToString();
    }

    // TcUnit result from the runtime messages: summary + failed tests (same parser as TcAgent.exe test)
    public string TestResults()
    {
        var (_, runtime) = ReadErrorList(20000);
        var r = TcUnitResults.Parse(Chronological(runtime).Select(e => e.Text));
        var o = new StringBuilder();
        if (r.Finished)
            o.AppendLine(System.FormattableString.Invariant(
                $"TcUnit: {r.TotalCount} tests in {r.SuiteCount ?? r.Suites.Count} suites, {r.FailedCount} failed, duration {r.Duration ?? 0:0.000} s"));
        else o.AppendLine("No completed TcUnit run found in the runtime messages (tests still running or PLC not started).");
        foreach (var s in r.Suites)
        {
            var failed = s.Cases.Where(c => c.Failed).ToList();
            o.AppendLine($"  {s.Name}: {Math.Max(s.Tests ?? 0, s.Cases.Count)} tests, {failed.Count} failed");
            foreach (var c in failed) o.AppendLine($"    FAILED {c.Name}: {c.Failure ?? c.Status}");
        }
        return o.ToString();
    }

    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
