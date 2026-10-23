// TwinCAT Package Manager (TwinCAT 3.1 build 4026 and later) through its command line tcpkg.exe.
//
//   tc_packages          read only: installed packages, available packages/workloads, all versions of one package,
//                        outdated packages, details of a package, configured feeds, feed check, tcpkg settings
//   tc_packages_change   install, upgrade, uninstall, repair packages; add, edit, remove feeds.
//                        Always confirmed by the user (runtime class) and run elevated (UAC prompt on this PC):
//                        TcAgent.exe tcpkg-elevated starts tcpkg.exe of the program folders directly, without cmd.exe.
// Package ids, versions, feed names and options are checked against fixed character sets first (Guards.TcpkgArgs).
//
// Command syntax from the TwinCAT installation documentation (InfoSys "Working with the command line": list, install,
// upgrade, uninstall, repair, show, source, config). Installing XAE packages usually needs Visual Studio / XAE Shell closed.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterPackages()
    {
        Add("tc_packages", "TwinCAT Package Manager (tcpkg, TwinCAT 4026+), read only: action=installed (installed packages and workloads), " +
                           "available (all packages, filter=text; type=workload for workloads only), versions (all versions of ONE package), " +
                           "outdated (packages with a newer version), show (details and dependencies of a package), feeds (configured feeds), " +
                           "verify_feeds (are the feeds reachable), config (tcpkg settings, e.g. useVS2022 / useTcXaeShell64). " +
                           "Changes with tc_packages_change.",
            Schema(Req(P("action", "string", "installed, available, versions, outdated, show, feeds, verify_feeds or config")),
                   P("package", "string", "versions/show: package id, e.g. TE1300.ScopeViewProfessional.XAE; available/installed/outdated: optional filter"),
                   P("type", "string", "available/installed: Workload to list workloads only"),
                   P("feed", "string", "only this feed (name as in action=feeds)"),
                   P("prerelease", "boolean", "include pre-release versions")),
            a => Packages(a));

        Add("tc_packages_change", "TwinCAT Package Manager, changes (always asks the user, runs elevated - Windows shows a UAC prompt): " +
                                  "action=install|upgrade|uninstall|repair packages (package ids separated by space or ',', optionally with " +
                                  "=version, e.g. TwinCAT.XAE.PLC=3.6.16; upgrade also 'all'), feed_add (name, source URL/path, priority), " +
                                  "feed_edit (name, enabled, priority, newName, source), feed_remove (name). " +
                                  "Close Visual Studio / XAE Shell before installing or upgrading XAE packages. Check first with tc_packages.",
            Schema(Req(P("action", "string", "install, upgrade, uninstall, repair, feed_add, feed_edit or feed_remove")),
                   P("packages", "string", "install/upgrade/uninstall/repair: package ids, e.g. 'TE1300.ScopeViewProfessional.XAE' or 'TwinCAT.Standard.XAE=4026.22.0'"),
                   P("allowDowngrade", "boolean", "upgrade: allow a lower version than the installed one"),
                   P("includeDependencies", "boolean", "uninstall: also remove dependencies no longer needed"),
                   P("prerelease", "boolean", "install/upgrade: allow pre-release versions"),
                   P("feed", "string", "install/upgrade: only this feed"),
                   P("name", "string", "feed_*: feed name"),
                   P("source", "string", "feed_add/feed_edit: feed URL or folder"),
                   P("priority", "integer", "feed_add/feed_edit: search order (lower = first)"),
                   P("enabled", "boolean", "feed_edit: enable/disable the feed"),
                   P("newName", "string", "feed_edit: rename"),
                   P("dryRun", "boolean", "only show the tcpkg command")),
            a => PackagesChange(a));
    }

    // ------------------------------------------------------------------ read
    string Packages(JsonObject a)
    {
        var action = (Str(a, "action") ?? "installed").ToLowerInvariant();
        var pkg = Str(a, "package");
        var args = new List<string>();
        void Common() { if (Str(a, "feed") is { } f) { args.Add("-n"); args.Add(f); } if (Bool(a, "prerelease", false)) args.Add("--pre"); }
        switch (action)
        {
            case "installed": args.Add("list"); if (pkg != null) args.Add(pkg); args.Add("-i"); if (Str(a, "type") is { } t1) { args.Add("-t"); args.Add(t1); } break;
            case "available": args.Add("list"); if (pkg != null) args.Add(pkg); if (Str(a, "type") is { } t2) { args.Add("-t"); args.Add(t2); } Common(); break;
            case "versions":
                args.Add("list"); args.Add(pkg ?? throw new Exception("package is required, e.g. TE1300.ScopeViewProfessional.XAE"));
                args.Add("--exact"); args.Add("--all-versions"); Common(); break;
            case "outdated": args.Add("list"); if (pkg != null) args.Add(pkg); args.Add("-o"); Common(); break;
            case "show": args.Add("show"); args.Add(pkg ?? throw new Exception("package is required")); Common(); break;
            case "feeds": args.Add("source"); args.Add("list"); break;
            case "verify_feeds": args.Add("source"); args.Add("verify"); if (Str(a, "feed") is { } vf) args.Add(vf); break;
            case "config": args.Add("config"); args.Add("list"); break;
            default: throw new Exception("action: installed, available, versions, outdated, show, feeds, verify_feeds or config");
        }
        var (code, output) = RunTcpkg(args, elevated: false, timeoutMs: 180_000);
        output = CleanTcpkg(output);
        if (action == "versions") output = CompactVersions(output);
        return $"tcpkg {string.Join(" ", args.Select(QuoteArg))}  (exit code {code})\n{Tail(output, 30_000)}";
    }

    // "[Beckhoff Stable Feed]  X 1.2.3" lines -> one line per version with the feeds that offer it
    static string CompactVersions(string output)
    {
        var rx = new Regex(@"^\[(?<feed>[^\]]+)\]\s+(?<id>\S+)\s+(?<ver>\S+)\s*$", RegexOptions.Multiline);
        var ms = rx.Matches(output);
        if (ms.Count == 0) return output;
        var byVer = ms.Cast<Match>().GroupBy(m => m.Groups["ver"].Value)
                      .Select(g => (ver: g.Key, feeds: string.Join(", ", g.Select(m => m.Groups["feed"].Value.Replace("Beckhoff ", "").Replace(" Feed", "")).Distinct())))
                      .OrderByDescending(x => VersionKey(x.ver)).ToList();
        var id = ms[0].Groups["id"].Value;
        return $"{id}: {byVer.Count} versions (newest first)\n" + string.Join("\n", byVer.Select(x => $"  {x.ver,-14} {x.feeds}"));
    }

    static string VersionKey(string v) => string.Join(".", v.Split('.', '-').Select(p => int.TryParse(p, out var n) ? n.ToString("D8") : p));

    // ------------------------------------------------------------------ changes
    string PackagesChange(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        var args = PackagesChangeArgs(a);
        var cmd = "tcpkg " + string.Join(" ", args.Select(QuoteArg));
        if (DryRun(a)) return "PROPOSAL (nothing executed): " + cmd;
        var ideOpen = action is "install" or "upgrade" or "uninstall" or "repair" && System.Diagnostics.Process.GetProcessesByName("devenv").Length +
                      System.Diagnostics.Process.GetProcessesByName("TcXaeShell").Length + System.Diagnostics.Process.GetProcessesByName("TcXaeShell64").Length > 0;
        var (code, output) = RunTcpkg(args, elevated: true, timeoutMs: 3_600_000);
        output = CleanTcpkg(output);
        var o = new StringBuilder($"{cmd}  (exit code {code})\n{Tail(output, 30_000)}");
        if (code != 0 && ideOpen)
            o.Append("\nVisual Studio / XAE Shell is open - XAE packages can usually only be changed with the IDE closed. Ask the user to close it and retry.");
        if (code == 0 && action is "install" or "upgrade" or "repair")
            o.Append("\nCheck the result with tc_packages action=installed. Visual Studio integrations take effect after the IDE is restarted.");
        return o.ToString();
    }

    /// <summary>The tcpkg arguments of a tc_packages_change call, checked (Guards.TcpkgArgs). Also called before the
    /// confirmation, so a value that is not allowed is refused before anybody is asked.</summary>
    static List<string> PackagesChangeArgs(JsonObject a)
    {
        var action = (Str(a, "action") ?? "").ToLowerInvariant();
        var args = new List<string>();
        List<string> Pkgs() => (Str(a, "packages") ?? throw new Exception("packages is required"))
            .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        string Name() => Str(a, "name") ?? throw new Exception("name (feed) is required");
        switch (action)
        {
            case "install":
            case "upgrade":
                args.Add(action); args.AddRange(Pkgs());
                if (action == "upgrade" && Bool(a, "allowDowngrade", false)) args.Add("--allow-downgrade");
                if (Bool(a, "prerelease", false)) args.Add("--pre");
                if (Str(a, "feed") is { } f) { args.Add("-n"); args.Add(f); }
                args.Add("-y");
                break;
            case "uninstall":
                args.Add("uninstall"); args.AddRange(Pkgs());
                if (Bool(a, "includeDependencies", false)) args.Add("--include-dependencies");
                args.Add("-y");
                break;
            case "repair":
                args.Add("repair"); args.AddRange(Pkgs()); args.Add("-y");
                break;
            case "feed_add":
                args.AddRange(new[] { "source", "add", "-n", Name(), "-s", Str(a, "source") ?? throw new Exception("source (URL or folder) is required") });
                if (a["priority"] != null) { args.Add("--priority"); args.Add(Int(a, "priority", 1).ToString()); }
                args.Add("-y");
                break;
            case "feed_edit":
                args.AddRange(new[] { "source", "edit", Name() });
                if (Str(a, "newName") is { } nn) { args.Add("--new-name"); args.Add(nn); }
                if (Str(a, "source") is { } s) { args.Add("-s"); args.Add(s); }
                if (a["enabled"] != null) { args.Add("--enabled"); args.Add(Bool(a, "enabled", true) ? "true" : "false"); }
                if (a["priority"] != null) { args.Add("--priority"); args.Add(Int(a, "priority", 1).ToString()); }
                args.Add("-y");
                break;
            case "feed_remove":
                args.AddRange(new[] { "source", "remove", Name(), "-y" });
                break;
            default: throw new Exception("action: install, upgrade, uninstall, repair, feed_add, feed_edit or feed_remove");
        }
        try { Guards.TcpkgArgs(args); }
        catch (ArgumentException ex) { throw new Exception("NOT executed: " + ex.Message); }
        return args;
    }

    // ------------------------------------------------------------------ tcpkg process
    static readonly string[] TcpkgLocations = { @"Beckhoff\TcPkg\tcpkg.exe", @"Beckhoff\TwinCAT\TcPkg\tcpkg.exe", @"Beckhoff\TcPkg\bin\tcpkg.exe" };

    static IEnumerable<string> ProgramRoots() =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"C:\TwinCAT" }
            .Where(r => !string.IsNullOrEmpty(r));

    static string TcpkgExe()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var f = Path.Combine(d.Trim('"'), "tcpkg.exe"); if (File.Exists(f)) return f; } catch { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            foreach (var rel in new[] { @"Beckhoff\TcPkg\tcpkg.exe", @"Beckhoff\TwinCAT\TcPkg\tcpkg.exe", @"Beckhoff\TcPkg\bin\tcpkg.exe" })
            {
                var f = Path.Combine(root, rel);
                if (File.Exists(f)) return f;
            }
        throw new Exception("tcpkg.exe not found - the TwinCAT Package Manager is part of TwinCAT 3.1 build 4026 and later (Beckhoff TcPkg). " +
                            "If it is installed elsewhere, add its folder to PATH.");
    }

    static string QuoteArg(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>tcpkg.exe for the elevated run: only from the program folders or C:\TwinCAT - a tcpkg.exe in a folder of
    /// PATH the user can write to must not run with administrator rights.</summary>
    static string TrustedTcpkgExe()
    {
        foreach (var root in ProgramRoots())
            foreach (var rel in TcpkgLocations)
                if (File.Exists(Path.Combine(root, rel))) return Path.Combine(root, rel);
        var exe = Path.GetFullPath(TcpkgExe());
        if (!ProgramRoots().Select(r => r.TrimEnd('\\') + "\\").Any(r => exe.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            throw new Exception($"tcpkg.exe ({exe}) is not in the program folders or C:\\TwinCAT - it is not started with administrator rights.");
        return exe;
    }

    static (int code, string output) RunTcpkgDirect(string exe, IReadOnlyList<string> args, int timeoutMs)
    {
        // no shell: every argument goes to tcpkg as it is
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        psi.EnvironmentVariables["NO_COLOR"] = "1";
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "Timeout after " + timeoutMs / 1000 + " s\n" + so.Result); }
        return (p.ExitCode, so.Result + (se.Result.Trim().Length > 0 ? "\n" + se.Result : ""));
    }

    static readonly Regex ElevatedLog = new(@"^tcagent-tcpkg-[0-9a-f]{32}\.log$");

    /// <summary>TcAgent.exe tcpkg-elevated &lt;log&gt; &lt;tcpkg arguments&gt;: the part that runs with administrator rights.
    /// Checks the arguments again, starts tcpkg without a shell and writes its output to the log file.</summary>
    public static int RunElevatedTcpkg(string[] argv)
    {
        try
        {
            if (argv.Length < 3) return 2;
            var log = Path.GetFullPath(argv[1]);
            if (!ElevatedLog.IsMatch(Path.GetFileName(log)) || !Directory.Exists(Path.GetDirectoryName(log))) return 2;
            var args = argv.Skip(2).ToList();
            Guards.TcpkgArgs(args);
            var (code, output) = RunTcpkgDirect(TrustedTcpkgExe(), args, 3_600_000);
            File.WriteAllText(log, output, new UTF8Encoding(false));
            return code;
        }
        catch (Exception ex)
        {
            try { if (argv.Length > 1 && ElevatedLog.IsMatch(Path.GetFileName(argv[1]))) File.WriteAllText(argv[1], "Error: " + ex.Message); } catch { }
            return 2;
        }
    }

    static (int code, string output) RunTcpkg(List<string> args, bool elevated, int timeoutMs)
    {
        // package ids, versions, feed names and options only from fixed character sets (Guards.cs)
        try { Guards.TcpkgArgs(args); }
        catch (ArgumentException ex) { throw new Exception("NOT executed: " + ex.Message); }
        if (!elevated) return RunTcpkgDirect(TcpkgExe(), args, timeoutMs);
        // elevated: Windows cannot redirect the output of a "runas" process - TcAgent.exe itself runs elevated, starts
        // tcpkg without cmd.exe and writes the output to a file
        TrustedTcpkgExe();
        var self = Environment.ProcessPath ?? throw new Exception("Path of TcAgent.exe unknown");
        var log = Path.Combine(Path.GetTempPath(), $"tcagent-tcpkg-{Guid.NewGuid():N}.log");
        var run = new ProcessStartInfo(self, string.Join(" ", new[] { "tcpkg-elevated", log }.Concat(args).Select(Guards.QuoteArg)))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var p = Process.Start(run)!;
            if (!p.WaitForExit(timeoutMs)) return (-1, "Timeout after " + timeoutMs / 1000 + " s - tcpkg may still be running.");
            var text = File.Exists(log) ? File.ReadAllText(log) : "(no output)";
            return (p.ExitCode, text);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (-2, "The user declined the administrator prompt (UAC) - nothing was changed.");
        }
        finally { try { File.Delete(log); } catch { } }
    }

    // progress bars, ANSI escape sequences and empty lines out
    static string CleanTcpkg(string s)
    {
        s = Regex.Replace(s ?? "", @"\x1B\[[0-9;?]*[A-Za-z]", "");
        var lines = s.Replace("\r\n", "\n").Split('\n')
                     .Select(l => l.Contains('\r') ? l.Substring(l.LastIndexOf('\r') + 1) : l)
                     .Where(l => l.Trim().Length > 0 && !Regex.IsMatch(l, @"^\s*[\[\(]?[#=\-\s]*[\]\)]?\s*\d{1,3}\s?%\s*$"));
        return string.Join("\n", lines);
    }

    static string Tail(string s, int max) => s.Length <= max ? s : "... (shortened)\n" + s[^max..];
}
