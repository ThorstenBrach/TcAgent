// Build machine checks and installation for CI runners - no AI involved:
//
//   TcAgent.exe doctor [--solution <sln>] [--target <AmsNetId>] [--progid <ProgID>]
//       checks whether this machine and this user can run TcAgent pipelines: desktop session (not a service), Visual
//       Studio / XAE Shell registered, TwinCAT XAE, Automation Interface interop, TwinCAT runtime via ADS, TcUnit library,
//       allowed targets, git - each with a concrete hint. With --solution it also opens the solution hidden, waits for
//       TwinCAT, lists the PLC projects and closes it again (the real end-to-end check of a runner).
//       Exit 0 = ready (warnings possible), 3 = something a pipeline needs is missing.
//
//   TcAgent.exe install --path <folder>
//       copies this TcAgent (all files next to TcAgent.exe plus the interop generated for this TwinCAT installation) to a
//       fixed folder, e.g. C:\Tools\TcAgent - a stable path for pipelines; the VSIX installs per version and per user.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Win32;
using TwinCAT.Ads;

namespace TcAgent;

public static partial class Cli
{
    enum Level { Ok, Info, Warn, Fail }

    sealed class Finding
    {
        public Level Level; public string Area, Text, Hint;
    }

    static int Doctor(Options o)
    {
        var f = new List<Finding>();
        void Add(Level l, string area, string text, string hint = null) => f.Add(new Finding { Level = l, Area = area, Text = text, Hint = hint });

        Console.WriteLine($"TcAgent {Program.ServerVersion} - build machine check ({Environment.MachineName}, user {Environment.UserDomainName}\\{Environment.UserName})");
        Console.WriteLine($"  {Environment.ProcessPath}");
        Console.WriteLine();

        // ---- session: Visual Studio automation needs a user desktop, a service in session 0 has none
        int session = -1;
        try { session = Process.GetCurrentProcess().SessionId; } catch { }
        var system = Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || Environment.UserName.EndsWith("$");
        if (session == 0 || system)
            Add(Level.Fail, "Session", $"runs in session {session} as {Environment.UserName} - no desktop",
                "Run the CI agent as a normal user with a logged-on desktop (GitHub: run.cmd at logon, not as a service) - see docs/PIPELINE.md.");
        else if (!Environment.UserInteractive)
            Add(Level.Warn, "Session", $"session {session}, not interactive", "Visual Studio may fail to start; prefer an agent started at logon.");
        else Add(Level.Ok, "Session", $"desktop session {session}");

        // ---- IDE registered for COM automation
        var ides = new[] { "VisualStudio.DTE.17.0", "TcXaeShell.DTE.17.0", "TcXaeShell.DTE.15.0" }
            .Where(id => { try { return Type.GetTypeFromProgID(id) != null; } catch { return false; } }).ToList();
        if (ides.Count == 0)
            Add(Level.Fail, "IDE", "neither Visual Studio 2022 nor the TwinCAT XAE Shell is registered",
                "Install Visual Studio 2022 or the XAE Shell with TwinCAT 3.1 XAE, then start it once interactively as this user.");
        else Add(Level.Ok, "IDE", string.Join(", ", ides) + (string.IsNullOrEmpty(o.ProgId) ? " (the first one is used; --progid selects another)" : ""));

        // ---- TwinCAT engineering (type library of the Automation Interface)
        var tcDir = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        var tlbRoots = new[] { tcDir == null ? null : Path.Combine(tcDir, "Components", "Base", "TypeLib"),
                               @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\TypeLib", @"C:\TwinCAT\3.1\Components\Base\TypeLib" }
            .Where(d => d != null && Directory.Exists(d)).ToList();
        var tlb = tlbRoots.SelectMany(d => SafeFiles(d, "TCatSysManager.tlb")).FirstOrDefault();
        if (tlb == null) Add(Level.Fail, "TwinCAT XAE", "TCatSysManager.tlb not found", "Install TwinCAT 3.1 XAE (engineering).");
        else Add(Level.Ok, "TwinCAT XAE", tcDir ?? Path.GetDirectoryName(tlb));

        // ---- interop for the Automation Interface (generated per machine, never shipped)
        var interopDirs = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "lib"),
                                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "lib") };
        var interop = interopDirs.Select(d => Path.Combine(d, "Interop.TCatSysManagerLib.dll")).FirstOrDefault(File.Exists);
        if (interop == null)
            Add(Level.Fail, "Interop", "Interop.TCatSysManagerLib.dll not found",
                "Start Visual Studio 2022 with the TcAgent extension once as this user (it generates the DLL), or build with build.ps1; " +
                "TcAgent.exe install --path <folder> copies it next to TcAgent.exe.");
        else Add(Level.Ok, "Interop", interop);

        // ---- runtime (only needed for test / sil)
        var target = string.IsNullOrEmpty(o.Target) || o.Target.Equals("local", StringComparison.OrdinalIgnoreCase) ? LocalNetId : o.Target.Trim();
        var (state, err) = ReadSystemState(target);
        if (state == null)
            Add(Level.Warn, "Runtime", $"no TwinCAT runtime answers on {target} ({err})",
                "Needed for test/sil/hil: install TwinCAT XAR (or start the usermode runtime and pass --target), system service running.");
        else Add(state is AdsState.Run or AdsState.Config ? Level.Ok : Level.Warn, "Runtime", $"{target}: {state}");

        // ---- TcUnit (library repository of this TwinCAT installation)
        var libRoots = new[] { tcDir == null ? null : Path.Combine(tcDir, "Components", "Plc", "Managed Libraries"),
                               @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Plc\Managed Libraries",
                               @"C:\TwinCAT\3.1\Components\Plc\Managed Libraries" }.Where(d => d != null && Directory.Exists(d)).Distinct().ToList();
        var tcunit = libRoots.SelectMany(d => SafeDirs(d, "TcUnit")).FirstOrDefault();
        if (tcunit == null) Add(Level.Warn, "TcUnit", "TcUnit library not found in the library repository",
                                "Needed for test/sil: install TcUnit (TwinCAT Package Manager or tcunit.org) into the library repository.");
        else Add(Level.Ok, "TcUnit", tcunit);

        // ---- licenses: only a hint - the license state needs the runtime and is read by TwinCAT itself
        var licenseDirs = new[] { tcDir == null ? null : Path.Combine(tcDir, "Target", "License"),
                                  @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Target\License", @"C:\TwinCAT\3.1\Target\License" }
            .Where(d => d != null && Directory.Exists(d)).Distinct().ToList();
        var licFiles = licenseDirs.SelectMany(d => SafeFiles(d, "*.tclrs")).ToList();
        Add(Level.Info, "Licenses", licFiles.Count > 0 ? $"{licFiles.Count} license file(s) in {licenseDirs[0]}" : "no license files found",
            "TcAgent needs TE1030 (engineering automation); test/sil need the runtime licenses of the project. Trial licenses are " +
            "activated by a person - for a build server use real licenses or a dongle.");

        // ---- policy and tools
        var allow = Environment.GetEnvironmentVariable("TCAGENT_ALLOW_TARGETS");
        Add(Level.Info, "Targets", "TCAGENT_ALLOW_TARGETS = " + (string.IsNullOrEmpty(allow) ? "(not set: local only)" : allow));
        var git = WhereExe("git.exe");
        Add(git == null ? Level.Warn : Level.Ok, "git", git ?? "not found in PATH", git == null ? "The CI checkout needs git (GitHub runner: git for Windows)." : null);
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), "tcagent-doctor-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x"); File.Delete(probe);
            Add(Level.Ok, "Temp", Path.GetTempPath());
        }
        catch (Exception ex) { Add(Level.Fail, "Temp", "not writable: " + Msg(ex)); }

        // ---- end to end: open the solution like a pipeline does
        if (!string.IsNullOrEmpty(o.Solution) && !f.Any(x => x.Level == Level.Fail && x.Area is "IDE" or "Interop" or "Session"))
        {
            var sw = Stopwatch.StartNew();
            var sta = new StaWorker();
            var tc = new TwinCatSession(sta);
            try
            {
                var sln = Path.GetFullPath(o.Solution);
                Console.WriteLine($"Opening {sln} hidden (as a pipeline does) ...");
                sta.Run(() => tc.Open(sln, false, o.ProgId), o.LoadTimeoutSec * 1000);
                sta.Run(() => { tc.WaitForTwinCat(o.LoadTimeoutSec); return 0; }, (o.LoadTimeoutSec + 30) * 1000);
                var plcs = sta.Run(() => tc.PlcProjects().Select(p => p.Name).ToList());
                Add(Level.Ok, "Solution", System.FormattableString.Invariant($"opened in {sw.Elapsed.TotalSeconds:0} s, PLC projects: {(plcs.Count == 0 ? "none" : string.Join(", ", plcs))}"));
                if (plcs.Count == 0) Add(Level.Warn, "Solution", "no PLC project found");
            }
            catch (Exception ex)
            {
                Add(Level.Fail, "Solution", "could not open it: " + Msg(ex),
                    "Start the IDE once interactively as this user (first-start settings), check licenses and dialogs; try --visible with check.");
            }
            finally
            {
                try { sta.Run(() => { tc.CloseIde(); return 0; }, 60_000); } catch { }
                sta.Dispose();
            }
        }
        else if (string.IsNullOrEmpty(o.Solution))
            Add(Level.Info, "Solution", "not opened - add --solution <sln> for the end-to-end check");

        // ---- report
        foreach (var x in f)
        {
            var tag = x.Level switch { Level.Ok => "OK  ", Level.Info => "INFO", Level.Warn => "WARN", _ => "FAIL" };
            Console.WriteLine($"{tag}  {x.Area,-12} {x.Text}");
            if (x.Hint != null && x.Level != Level.Ok) Console.WriteLine($"      {"",-12} -> {x.Hint}");
        }
        int fails = f.Count(x => x.Level == Level.Fail), warns = f.Count(x => x.Level == Level.Warn);
        Console.WriteLine();
        Console.WriteLine(fails > 0 ? $"NOT READY: {fails} problem(s), {warns} warning(s)." :
                          warns > 0 ? $"READY with {warns} warning(s) - check/build work; the warnings matter for test/sil." : "READY.");
        return fails > 0 ? Infra : Ok;
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); } catch { return Array.Empty<string>(); }
    }

    static IEnumerable<string> SafeDirs(string dir, string name)
    {
        try { return Directory.GetDirectories(dir, name, SearchOption.AllDirectories); } catch { return Array.Empty<string>(); }
    }

    static string WhereExe(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.Combine(d.Trim().Trim('"'), exe); } catch { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));

    // ------------------------------------------------------------------ install
    static int Install(Options o)
    {
        if (string.IsNullOrEmpty(o.Out)) { Console.Error.WriteLine("install needs --path <folder>, e.g. --path C:\\Tools\\TcAgent"); return Usage; }
        var dst = Path.GetFullPath(o.Out);
        var src = AppContext.BaseDirectory.TrimEnd('\\');
        if (string.Equals(dst.TrimEnd('\\'), src, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Already installed here: " + dst); return Ok; }
        try
        {
            Directory.CreateDirectory(dst);
            int n = 0;
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file);
                if (rel.StartsWith("feedback", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".source-stamp", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                n++;
            }
            // the interop generated for this TwinCAT installation, so other users / the runner find it next to TcAgent.exe
            var interop = new[] { Path.Combine(src, "lib", "Interop.TCatSysManagerLib.dll"),
                                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "lib", "Interop.TCatSysManagerLib.dll") }
                .FirstOrDefault(File.Exists);
            if (interop != null)
            {
                Directory.CreateDirectory(Path.Combine(dst, "lib"));
                File.Copy(interop, Path.Combine(dst, "lib", "Interop.TCatSysManagerLib.dll"), true);
            }
            Console.WriteLine($"TcAgent {Program.ServerVersion} installed: {Path.Combine(dst, "TcAgent.exe")} ({n} files" +
                              (interop != null ? ", interop included" : ", WITHOUT interop - start VS with the TcAgent extension once, then install again") + ")");
            Console.WriteLine($"Check the machine: \"{Path.Combine(dst, "TcAgent.exe")}\" doctor --solution <your.sln>");
            return interop != null ? Ok : Infra;
        }
        catch (IOException ex) when (ex.Message.Contains("TcAgent.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("TcAgent.exe in the target folder is in use (a running pipeline or AI client) - stop it and try again. " + Msg(ex));
            return Infra;
        }
        catch (Exception ex) { Console.Error.WriteLine("install failed: " + Msg(ex)); return Infra; }
    }
}
