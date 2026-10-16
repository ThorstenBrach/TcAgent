// TcAgent.exe st export|check|import - PLC code as plain-text ST files and back (PlainText.cs). Works on the project files,
// no IDE is started:
//
//   TcAgent.exe st export --solution <sln> [--plc <names>] [--out <folder>] [--clean]   project -> .st files (only changed files written)
//   TcAgent.exe st check  --solution <sln> [--plc <names>] [--out <folder>]             exit 1 if the .st files differ from the project
//   TcAgent.exe st import --solution <sln> [--plc <names>] [--from <folder>] [--dry-run] [--force]   .st files -> existing project
//
// Default folder: <solution folder>\st. export/check also write/compare <folder>\<PLC project>\references.json (library
// references of the .plcproj); import ignores it. Format: docs/PLAINTEXT.md.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static partial class Cli
{
    static void ValidateSt(Options o)
    {
        if (o.Sub is not ("export" or "check" or "import"))
            throw new Exception("st needs export, check or import, e.g. TcAgent.exe st export --solution <sln>");
        if (o.Attach != null) throw new Exception("st works on the project files - use --solution, not --attach");
        if (o.Solution == null) throw new Exception("--solution <path to .sln> is required");
        if (!File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
        if (o.Sub != "import" && (o.From != null || o.DryRun || o.Force)) throw new Exception("--from, --dry-run and --force are options of st import");
        if (o.Sub == "import" && o.Out != null) throw new Exception("st import reads the folder given with --from (--out is for export/check)");
        if (o.Clean && o.Sub != "export") throw new Exception("--clean is an option of st export");
        if (o.CheckLock) throw new Exception("--check is an option of lock - use TcAgent.exe st check");
    }

    static int PlainTextCli(Options o)
    {
        var sln = Path.GetFullPath(o.Solution);
        var slnDir = Path.GetDirectoryName(sln) ?? ".";
        var folder = Path.GetFullPath(o.Sub == "import" ? o.From ?? Path.Combine(slnDir, "st") : o.Out ?? Path.Combine(slnDir, "st"));
        var tag = "ST " + o.Sub.ToUpperInvariant();
        PlainText.Report rep;
        try
        {
            rep = o.Sub == "import" ? PlainText.Import(slnDir, o.Plc, folder)
                                    : PlainText.Export(slnDir, o.Plc, folder, check: o.Sub == "check", clean: o.Clean);
        }
        catch (Exception ex) { return InfraError(o, "st", Msg(ex)); }

        foreach (var l in rep.Lines) Console.WriteLine("  " + l);
        foreach (var w in rep.Warnings) Console.WriteLine("WARNING: " + w);
        foreach (var d in rep.Differences) Console.WriteLine("  " + d);
        foreach (var e in rep.Errors) Console.Error.WriteLine("ERROR: " + e);
        if (rep.Errors.Count > 0)
        {
            Say($"{tag} FAILED: {rep.Errors.Count} error(s)" + (o.Sub == "import" ? " - nothing was written" : ""));
            if (rep.Plcs.Count == 0 && rep.Errors.All(e => e.StartsWith("no (matching) PLC project", StringComparison.Ordinal))) return Usage;
            return o.Sub == "import" ? Failed : Infra;
        }

        if (o.Sub == "export")
        {
            Say($"{tag} OK: {rep.Objects} object(s), {rep.Written} file(s) written, {rep.Unchanged} unchanged" +
                (rep.Removed > 0 ? $", {rep.Removed} removed" : "") +
                (rep.NonSt > 0 ? $", {rep.NonSt} graphical implementation(s) only as marker" : "") + $" - {folder}");
            return Ok;
        }
        if (o.Sub == "check")
        {
            if (rep.Differences.Count == 0) { Say($"{tag} OK: the text folder matches the project ({rep.Objects} object(s)) - {folder}"); return Ok; }
            var msg = $"{rep.Differences.Count} difference(s) between {folder} and the project - run TcAgent.exe st export and commit the .st files and references.json";
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Console.WriteLine($"::error title=TcAgent st check::{msg}");
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))) Console.WriteLine($"##vso[task.logissue type=error]TcAgent st check: {msg}");
            Say($"{tag} FAILED: {msg}");
            return Failed;
        }

        // import
        if (rep.Files.Count == 0) { Say($"{tag} OK: nothing to change ({rep.Objects} object(s) compared) - {folder}"); return Ok; }
        if (o.DryRun)
        {
            foreach (var (path, _) in rep.Files) Console.WriteLine("  would write " + path);
            Say($"{tag} (dry run): {rep.Files.Count} file(s) would be written, nothing was written");
            return Ok;
        }
        // TwinCAT keeps the project in memory: files changed behind an open IDE are overwritten on the next save
        var (open, info) = SolutionOpenInIde(sln);
        if (open == true)
            return Refused(o, "st", $"the solution is open in the IDE ({info}) - close it first, or let the agent import with tc_plaintext action=import (it writes through the IDE)");
        if (open == null)
        {
            if (!o.Force) return Refused(o, "st", $"could not check whether the solution is open in an IDE ({info}) - close the IDE and pass --force");
            Say($"WARNING: could not check whether the solution is open in an IDE ({info}) - writing anyway (--force)");
        }
        try { PlainText.WriteFiles(rep); }
        catch (Exception ex) { return InfraError(o, "st", "Writing failed: " + Msg(ex)); }
        Say($"{tag} OK: {rep.Files.Count} file(s) written ({rep.Changes.Count} object(s) changed or new) - reload the solution in the IDE");
        return Ok;
    }

    /// <summary>true = open in a running Visual Studio/XAE Shell (Running Object Table), false = not open,
    /// null = could not be checked (info says why).</summary>
    static (bool? Open, string Info) SolutionOpenInIde(string sln)
    {
        try
        {
            using var sta = new StaWorker();
            var tc = new TwinCatSession(sta);
            var list = sta.Run(() => tc.ListInstances().Select(i => (i.Pid, i.Solution)).ToList(), 60_000);
            var hits = list.Where(i =>
            {
                if (string.IsNullOrEmpty(i.Solution)) return false;
                try { return string.Equals(Path.GetFullPath(i.Solution), sln, StringComparison.OrdinalIgnoreCase); } catch { return false; }
            }).ToList();
            if (hits.Count > 0) return (true, "pid " + string.Join(", ", hits.Select(h => h.Pid)));
            return (false, $"{list.Count} IDE instance(s) running");
        }
        catch (Exception ex) { return (null, Msg(ex)); }
    }
}
