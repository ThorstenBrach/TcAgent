// Verbindung zu Visual Studio 2022 / TcXaeShell und Zugriff auf das TwinCAT Automation Interface.
// Alle Methoden hier werden ueber StaWorker.Run auf dem STA-Thread ausgefuehrt.
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
    public string InstanceInfo { get; private set; } = "nicht verbunden";
    /// <summary>Prozess-ID der verbundenen IDE (0 = unbekannt, z. B. nach tc_open) - fuer die VS-Erweiterung TcAgentBridge.</summary>
    public int AttachedPid { get; private set; }

    // ProgIDs in der Reihenfolge, in der neue Instanzen gestartet werden
    static readonly string[] ProgIds = { "VisualStudio.DTE.17.0", "TcXaeShell.DTE.17.0", "TcXaeShell.DTE.15.0" };

    public TwinCatSession(StaWorker sta) { Sta = sta; }

    // ------------------------------------------------------------------ Instanzen
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
        if (inst.Count == 0) throw new Exception("Keine laufende Visual-Studio-/XAE-Instanz gefunden. tc_open startet eine neue.");
        // Auswahlregel: pid > Solution-Hinweis > eigene IDE (TcAgent-Chat / Terminal in VS) > TCAGENT_SOLUTION
        //               > einzige Instanz mit Solution > Auswahl durch den Benutzer. Es wird NICHT geraten.
        DteInstance pick = null;
        if (pid > 0)
            pick = inst.FirstOrDefault(i => i.Pid == pid) ?? throw new Exception($"Keine IDE mit pid {pid}.\n" + Describe(inst));
        else if (!string.IsNullOrEmpty(solutionHint))
            pick = BySolution(inst, solutionHint);
        else
        {
            pick = HostIde(inst);
            var envSln = Environment.GetEnvironmentVariable("TCAGENT_SOLUTION");
            if (pick == null && !string.IsNullOrEmpty(envSln))
            {
                var hits = inst.Where(i => i.Solution.IndexOf(envSln, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) pick = hits[0];   // passt TCAGENT_SOLUTION nicht (mehr), normal weiter auswaehlen
            }
            if (pick == null)
            {
                var withSln = inst.Where(i => !string.IsNullOrEmpty(i.Solution)).ToList();
                if (withSln.Count == 1) pick = withSln[0];
                else pick = AskUser(withSln.Count > 1 ? withSln : inst)
                     ?? throw new Exception((withSln.Count > 1 ? $"{withSln.Count} TwinCAT-Projekte sind geoeffnet." : "Keine Instanz hat eine Solution geoeffnet.") +
                                            " Bitte den Benutzer fragen, welches gemeint ist, und tc_attach mit pid aufrufen (Auswahl wird fuer diese Sitzung gemerkt).\n" + Describe(inst));
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
        if (hits.Count != 1) throw new Exception($"'{hint}' passt auf {hits.Count} Instanzen - bitte genauer angeben.\n" + Describe(inst));
        return hits[0];
    }

    /// <summary>Die IDE, aus der TcAgent (indirekt) gestartet wurde: TcAgent-Chat (TCAGENT_VS_PID) oder Terminal/Prozess innerhalb von VS
    /// (devenv ist ein Vorfahre dieses Prozesses). Damit braucht der Chat nie einen Projektnamen.</summary>
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

    /// <summary>true, wenn dieser Prozess (indirekt) von Prozess pid gestartet wurde - z. B. aus dem TcAgent-Chat dieser IDE.</summary>
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

    /// <summary>Auswahlliste im Client (MCP-Elicitation), falls der Client das unterstuetzt.</summary>
    static DteInstance AskUser(List<DteInstance> cands)
    {
        if (cands.Count == 0) return null;
        var enumVals = new System.Text.Json.Nodes.JsonArray();
        var enumNames = new System.Text.Json.Nodes.JsonArray();
        foreach (var i in cands)
        {
            enumVals.Add(i.Pid.ToString());
            enumNames.Add(string.IsNullOrEmpty(i.Solution) ? $"(keine Solution) - pid {i.Pid}" : $"{Path.GetFileNameWithoutExtension(i.Solution)}  -  {i.Solution}");
        }
        var schema = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "object",
            ["properties"] = new System.Text.Json.Nodes.JsonObject
            {
                ["instance"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "string", ["title"] = "TwinCAT-Projekt",
                    ["description"] = "In welcher Visual-Studio-Instanz soll TcAgent arbeiten?",
                    ["enum"] = enumVals, ["enumNames"] = enumNames
                }
            },
            ["required"] = new System.Text.Json.Nodes.JsonArray("instance")
        };
        var answer = Mcp.Elicit("Mehrere TwinCAT-Projekte sind geoeffnet - mit welchem soll TcAgent arbeiten?", schema);
        var sel = answer?["instance"]?.ToString();
        return int.TryParse(sel, out var pid) ? cands.FirstOrDefault(i => i.Pid == pid) : null;
    }

    string lastSolution;

    // Verbindung pruefen und bei Bedarf automatisch (wieder) herstellen:
    // nach Server-Neustart (noch nie verbunden) oder wenn die IDE neu gestartet wurde (COM-Objekt tot).
    void EnsureConnected()
    {
        if (Dte != null)
        {
            try { _ = Dte.Solution.FullName; return; }
            catch { Log.Info("Verbindung zur IDE verloren - verbinde neu"); Dte = null; SysMan = null; InstanceInfo = "nicht verbunden"; }
        }
        try
        {
            Attach(string.IsNullOrEmpty(lastSolution) ? null : lastSolution, 0);
            Log.Info("Automatisch verbunden: " + InstanceInfo);
        }
        catch (Exception ex)
        {
            throw new Exception("Nicht verbunden, automatisches Verbinden nicht moeglich - tc_attach mit solution oder pid aufrufen.\n" + ex.Message);
        }
    }

    static string Describe(List<DteInstance> inst) =>
        "Laufende Instanzen:\n" + string.Join("\n", inst.Select(i => $"  pid={i.Pid}  {i.Solution}"));

    public string Open(string solutionPath, bool visible, string progId)
    {
        if (!File.Exists(solutionPath)) throw new FileNotFoundException("Solution nicht gefunden", solutionPath);
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
                Bind(dte, $"{id} (neu gestartet) | {solutionPath}");
                AttachedPid = 0;
                try { AttachedPid = ListInstances().FirstOrDefault(i => string.Equals(i.Solution, solutionPath, StringComparison.OrdinalIgnoreCase))?.Pid ?? 0; } catch { }
                return InstanceInfo;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new Exception("Konnte keine IDE starten: " + (last?.Message ?? "keine passende ProgID registriert"));
    }

    /// <summary>Nach tc_open/CLI: warten, bis die Solution geladen ist und das TwinCAT-Projekt erreichbar ist.</summary>
    public void WaitForTwinCat(int timeoutSec)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = InstanceInfo.Split(" | TwinCAT-Projekt")[0].Split(" | WARNUNG")[0];
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
            catch { /* IDE noch beschaeftigt */ }
            if (sw.Elapsed.TotalSeconds > timeoutSec) throw new Exception($"TwinCAT-Projekt nach {timeoutSec} s nicht geladen: {InstanceInfo}");
            System.Threading.Thread.Sleep(1000);
        }
    }

    /// <summary>Selbst gestartete IDE ohne Speichern schliessen (CLI).</summary>
    public void CloseIde()
    {
        if (Dte == null) return;
        try { Dte.Solution.Close(false); } catch { }
        try { Dte.Quit(); } catch { }
        Dte = null; SysMan = null; InstanceInfo = "nicht verbunden";
    }

    void Bind(DTE2 dte, string info)
    {
        Dte = dte;
        SysMan = null;
        foreach (Project p in dte.Solution.Projects)
        {
            try { if (p.Object is ITcSysManager sm) { SysMan = sm; info += $" | TwinCAT-Projekt: {p.Name}"; break; } } catch { }
        }
        InstanceInfo = info + (SysMan == null ? " | WARNUNG: kein TwinCAT-Projekt in der Solution" : "");
    }

    public ITcSysManager RequireSysMan()
    {
        EnsureConnected();
        return SysMan ?? throw new Exception("Die verbundene Solution enthaelt kein TwinCAT-Projekt - tc_attach auf eine andere Solution.");
    }

    public DTE2 RequireDte() { EnsureConnected(); return Dte; }

    // ------------------------------------------------------------------ Baum
    public ITcSmTreeItem Item(string path)
    {
        try { return RequireSysMan().LookupTreeItem(path); }
        catch (COMException) { throw new Exception($"Baumelement nicht gefunden: '{path}'"); }
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

    // ------------------------------------------------------------------ Build / Fehlerliste
    public string Build(string projectHint, bool rebuild, bool checkAll = true)
    {
        var dte = RequireDte();
        var sb = dte.Solution.SolutionBuild;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (rebuild) sb.Clean(true);
        sb.Build(true);
        var tBuild = sw.Elapsed.TotalSeconds;
        var failed = sb.LastBuildInfo;
        // TwinCAT uebersetzt nur verwendete POUs - "Alle Objekte pruefen" findet Fehler auch in unbenutztem Code
        var checkedInfo = "";
        if (checkAll)
            foreach (var plc in PlcProjects())
                checkedInfo += CheckAllObjects(plc) ? $"Alle Objekte geprueft: {plc.Name}\n" : $"Alle Objekte pruefen nicht verfuegbar: {plc.Name}\n";
        var tCheck = sw.Elapsed.TotalSeconds - tBuild;
        var (compile, runtime) = ReadErrorList(1000);
        int errs = compile.Count(e => e.Level == "ERROR"), warns = compile.Count(e => e.Level == "WARN");
        var o = new StringBuilder($"Build {(failed == 0 && errs == 0 ? "ERFOLGREICH" : "FEHLGESCHLAGEN")}: " +
                                  $"{failed} fehlgeschlagene Projekte, {errs} Compilerfehler, {warns} Warnungen\n");
        o.Append(checkedInfo);
        o.AppendLine(System.FormattableString.Invariant($"Dauer: {sw.Elapsed.TotalSeconds:0.0} s (Build {tBuild:0.0} s") +
                     (checkAll ? System.FormattableString.Invariant($", Alle Objekte pruefen {tCheck:0.0} s)") : ")"));
        foreach (var e in compile) o.AppendLine(e.ToString());
        if (runtime.Count > 0)
            o.AppendLine($"({runtime.Count} Laufzeit-/TcUnit-Meldungen ausgeblendet - siehe tc_errors kind=runtime oder tc_test_results)");
        return o.ToString();
    }

    // Nur "Alle Objekte pruefen": Compilerfehler ohne Build (kein ausfuehrbarer Code, keine Dateien)
    public string Check()
    {
        RequireDte();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = new StringBuilder();
        var plcs = PlcProjects();
        if (plcs.Count == 0) return "Kein PLC-Projekt gefunden.";
        foreach (var plc in plcs)
            if (!CheckAllObjects(plc)) info.AppendLine($"Alle Objekte pruefen nicht verfuegbar: {plc.Name} - stattdessen tc_build nehmen");
        var (compile, runtime) = ReadErrorList(1000);
        int errs = compile.Count(e => e.Level == "ERROR"), warns = compile.Count(e => e.Level == "WARN");
        var o = new StringBuilder($"Pruefung {(errs == 0 ? "OK" : "FEHLER")}: {errs} Compilerfehler, {warns} Warnungen " +
                                  $"({string.Join(", ", plcs.Select(p => p.Name))}; " +
                                  System.FormattableString.Invariant($"{sw.Elapsed.TotalSeconds:0.0} s)") + "\n");
        o.Append(info);
        foreach (var e in compile) o.AppendLine(e.ToString());
        if (runtime.Count > 0)
            o.AppendLine($"({runtime.Count} Laufzeit-/TcUnit-Meldungen ausgeblendet - siehe tc_errors kind=runtime oder tc_test_results)");
        return o.ToString();
    }

    // PLC-Projekte (Typ 600) unter TIPC
    public List<ITcSmTreeItem> PlcProjects()
    {
        var list = new List<ITcSmTreeItem>();
        foreach (var plc in Children(Item("TIPC")))
            foreach (var c in Children(plc))
                if (c.ItemType == 600) list.Add(c);
        return list;
    }

    // Alle PLC-Objekte unterhalb der PLC-Projekte (oder eines Startknotens), ohne Bibliotheksreferenzen
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

    // ------------------------------------------------------------------ Editor-Kontext
    // Was der Nutzer gerade in der IDE offen/markiert hat. Cursor/Selektion nur, soweit der Editor sie ueber DTE preisgibt.
    public string EditorContext()
    {
        var dte = RequireDte();
        var o = new StringBuilder();
        static string S(Func<string> f) { try { return f() ?? ""; } catch (Exception ex) { return "(" + ex.Message + ")"; } }

        var caption = S(() => dte.ActiveWindow.Caption);
        o.AppendLine("Aktives Fenster: " + caption);
        Document doc = null;
        try { doc = dte.ActiveDocument; } catch { }
        var names = new List<string> { caption };
        if (doc != null)
        {
            o.AppendLine("Aktives Dokument: " + S(() => doc.FullName));
            names.Add(S(() => doc.Name));
            // Methoden/Properties oeffnet TwinCAT als "<Pfad>\\FB.TcPOU@Methode" -> qualifizierter Name "FB.Methode".
            // Wichtig, weil die Fensterbeschriftung bei Fokus im Chat- oder einem anderen Werkzeugfenster fehlt
            // und ein reiner Name wie "Start" in mehreren FBs vorkommen kann.
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
                o.AppendLine($"Cursor: Zeile {S(() => ts.ActivePoint.Line.ToString())}, Spalte {S(() => ts.ActivePoint.LineCharOffset.ToString())}");
                var sel = S(() => ts.Text);
                if (sel.Length > 0) o.AppendLine("Selektion:\n" + (sel.Length > 4000 ? sel[..4000] + "..." : sel));
                var line = S(() => { var ep = ts.ActivePoint.CreateEditPoint(); ep.StartOfLine(); return ep.GetLines(ep.Line, ep.Line + 1); });
                if (line.Length > 0) o.AppendLine("Aktuelle Zeile: " + line.TrimEnd());
            }
            else o.AppendLine("Cursor/Selektion: vom TwinCAT-Editor nicht ueber DTE verfuegbar (kommt mit der VSIX-Erweiterung)");
        }
        else o.AppendLine("Kein Dokument aktiv");

        var selected = new List<string>();
        try { foreach (SelectedItem si in dte.SelectedItems) { var n = S(() => si.Name); if (n.Length > 0) selected.Add(n); } } catch { }
        if (selected.Count > 0) { o.AppendLine("Markiert im Projektmappen-Explorer: " + string.Join(", ", selected)); names.AddRange(selected); }

        // Fenster-/Dokumentnamen auf TwinCAT-Baumpfade abbilden, damit tc_plc_read direkt nutzbar ist
        var paths = ResolvePlcPaths(names);
        o.AppendLine(paths.Count > 0 ? "Baumpfad(e):\n  " + string.Join("\n  ", paths) : "Baumpfad: nicht eindeutig ermittelbar");
        return o.ToString();
    }

    // Namen + Pfade aller PLC-Objekte, kurz zwischengespeichert. Jeder COM-Zugriff laeuft im UI-Thread von VS -
    // ein voller Baumdurchlauf bei jeder Kontextabfrage laesst die IDE (auch das Chat-Fenster) haken.
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
        // qualifizierte Treffer (FB.Methode) sind eindeutig und schlagen reine Namenstreffer
        if (qualified.Count > 0) found = qualified;
        // spezifischste Treffer (Methode vor FB) zuerst
        return found.OrderByDescending(p => p.Count(c => c == '^')).ToList();
    }

    // Ruft CheckAllObjects ueber das passende Automation-Interface (ITcPlcIECProject*) auf
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

    /// <param name="Stamp">Zeitstempel einer Laufzeitmeldung (lokale Zeit), sonst null</param>
    public record ErrorEntry(string Level, string Project, string File, int Line, int Column, string Text, bool Runtime, DateTime? Stamp = null)
    {
        public override string ToString() => $"{Level}\t{Project}\t{File}:{Line}:{Column}\t{Text}";
    }

    // Laufzeitmeldungen der PLC (z. B. TcUnit, ADSLOGSTR) erscheinen ebenfalls in der Fehlerliste:
    // ohne Datei, Text beginnt mit Zeitstempel und "| 'Task' (Port):".
    // Datumsformat je nach Sprache der IDE: 11.10.2026 / 10/11/2026 / 2026-10-11, Uhrzeit ggf. mit AM/PM.
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

    /// <summary>Laufzeitmeldungen in zeitlicher Reihenfolge (die Fehlerliste ist nicht zuverlaessig sortiert;
    /// gleiche Zeitstempel behalten ihre Reihenfolge in der Liste, in deren Richtung).</summary>
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
            o.AppendLine($"Compiler/Projekt: {compile.Count} Eintraege ({compile.Count(e => e.Level == "ERROR")} Fehler, {compile.Count(e => e.Level == "WARN")} Warnungen)");
            foreach (var e in compile) o.AppendLine(e.ToString());
        }
        if (kind != "compile")
        {
            o.AppendLine($"Laufzeit/TcUnit: {runtime.Count} Meldungen (neueste zuerst)");
            foreach (var e in runtime) o.AppendLine(e.Text);
        }
        return o.ToString();
    }

    // TcUnit-Ergebnis aus den Laufzeitmeldungen: Zusammenfassung + fehlgeschlagene Tests (gleicher Parser wie TcAgent.exe test)
    public string TestResults()
    {
        var (_, runtime) = ReadErrorList(20000);
        var r = TcUnitResults.Parse(Chronological(runtime).Select(e => e.Text));
        var o = new StringBuilder();
        if (r.Finished)
            o.AppendLine(System.FormattableString.Invariant(
                $"TcUnit: {r.TotalCount} Tests in {r.SuiteCount ?? r.Suites.Count} Suites, {r.FailedCount} fehlgeschlagen, Dauer {r.Duration ?? 0:0.000} s"));
        else o.AppendLine("Kein abgeschlossener TcUnit-Lauf in den Laufzeitmeldungen gefunden (Tests laufen noch oder PLC nicht gestartet).");
        foreach (var s in r.Suites)
        {
            var failed = s.Cases.Where(c => c.Failed).ToList();
            o.AppendLine($"  {s.Name}: {Math.Max(s.Tests ?? 0, s.Cases.Count)} Tests, {failed.Count} fehlgeschlagen");
            foreach (var c in failed) o.AppendLine($"    FEHLER {c.Name}: {c.Failure ?? c.Status}");
        }
        return o.ToString();
    }

    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
