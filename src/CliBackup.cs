// TcAgent command line: runtime data backup and restore (part of Cli, see Cli.cs; the logic is in RuntimeBackup.cs).
// ADS only, no IDE - for the machine park: one backup file per PLC.
//
//   TcAgent.exe backup  [--target a,b,c] [--port 851] [--include <patterns>] [--exclude <patterns>] [--out <folder>] [--solution <sln>]
//   TcAgent.exe restore --from <file> [--target <AmsNetId>] [--allow <AmsNetId>] [--include <patterns>] [--dry-run] [--while-running]
//
// Exit codes: 0 = OK, 1 = values not restored (missing, type changed, failed), 2 = wrong call / target not allowed, 3 = ADS problem.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static partial class Cli
{
    static int BackupCli(Options o)
    {
        var dir = o.Solution != null ? Path.GetDirectoryName(Path.GetFullPath(o.Solution)) : null;
        List<string> Pats(string s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (o.Command == "backup")
        {
            int failed = 0;
            foreach (var target in (o.Target ?? "local").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var net = target.Equals("local", StringComparison.OrdinalIgnoreCase) ? null : target;
                try
                {
                    var b = RuntimeBackupAds.Backup(net, o.Port, dir, Pats(o.Include), Pats(o.Exclude), Program.ServerVersion);
                    var folder = o.Out ?? RuntimeBackup.DefaultFolder(dir);
                    var file = folder.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? folder : Path.Combine(folder, RuntimeBackup.FileName(net, o.Port));
                    RuntimeBackup.Save(file, b);
                    Console.WriteLine($"{target}:{o.Port}: {((System.Text.Json.Nodes.JsonObject)b["values"]!).Count} value(s), PLC {b["plcState"]}, project {b["project"] ?? "?"} -> {file}");
                }
                catch (Exception ex) { Console.Error.WriteLine($"{target}:{o.Port}: NO backup - {ex.Message}"); failed++; }
            }
            return failed == 0 ? Ok : Infra;
        }
        // restore: a runtime write - the allowed targets of TCAGENT_ALLOW_TARGETS plus --allow, like hil
        var tgt = string.IsNullOrWhiteSpace(o.Target) || o.Target.Trim().Equals("local", StringComparison.OrdinalIgnoreCase) ? null : o.Target.Trim();
        if (!o.DryRun)
            try { new Policy(o.Allow).Check("tc_backup", () => tgt, new System.Text.Json.Nodes.JsonObject { ["action"] = "restore" }); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message + " (pass --allow <AmsNetId> or set TCAGENT_ALLOW_TARGETS)"); return Usage; }
        try
        {
            var r = RuntimeBackupAds.Restore(RuntimeBackup.Load(o.From), tgt, o.Port, Pats(o.Include), o.DryRun, o.WhileRunning);
            Console.WriteLine($"{(o.DryRun ? "Difference" : "Restore")} {Path.GetFileName(o.From)} -> {tgt ?? "local"}:{o.Port}");
            Console.WriteLine(r.Report(o.DryRun, 200));
            return r.Missing.Count + r.TypeChanged.Count + r.Failed.Count == 0 ? Ok : Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { Console.Error.WriteLine(ex.Message); return Usage; }
        catch (Exception ex) { Console.Error.WriteLine("ADS: " + ex.Message); return Infra; }
    }
}
