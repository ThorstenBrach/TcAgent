// TcAgent command line: TcUnit tests of a solution (part of Cli, see Cli.cs; the logic is in TcUnitDiscovery.cs / TcUnitRun.cs).
//
//   TcAgent.exe tcunit list --solution <sln> [--plc <name>]          tests from the code as JSON, no IDE
//   TcAgent.exe tcunit run  --solution <sln>|--attach [pid] [--tests <a,b>] [--target ...] [--junit <file>]
//                                                                    one JSON event per line (TcUnit Tests window)
//   TcAgent.exe tcunit report --solution <sln> [--junit <file>] [--out <file.html>]   HTML report of the last run (TcUnitReport.cs)
// tcunit run stops when a line "cancel" arrives on stdin (or Ctrl+C): the PLC is reset, open tests stay "notrun".
//
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static partial class Cli
{
    static void ValidateTcUnit(Options o)
    {
        if (o.Sub is not ("list" or "run" or "report")) throw new Exception("tcunit needs list, run or report, e.g. TcAgent.exe tcunit list --solution <sln>");
        if (o.Sub is "list" or "report" && o.Solution == null) throw new Exception($"tcunit {o.Sub} needs --solution <path to .sln>");
        if (o.Sub == "report" && (o.Tests.Count > 0 || o.Download)) throw new Exception("--tests and --download are options of tcunit run");
        if ((o.Out != null || o.BuiltIn) && o.Sub != "report") throw new Exception("--out and --builtin are options of tcunit report");
        if (o.Sub == "list" && (o.Tests.Count > 0 || o.Download || o.Junit != null)) throw new Exception("--tests, --download and --junit are options of tcunit run");
        if (o.Solution == null && o.Attach == null) throw new Exception("--solution <path to .sln> (or --attach) is required");
        if (o.Solution != null && o.Attach != null) throw new Exception("use either --solution or --attach");
        if (o.Solution != null && !File.Exists(o.Solution)) throw new Exception(SolutionNotFound(o.Solution));
        if (o.Plc.Count > 1) throw new Exception("tcunit: one PLC project (--plc <name>)");
        if (o.TimeoutGiven && o.TimeoutSec < 10) throw new Exception("--timeout is in seconds (min. 10)");
        if (o.Target != null && !o.Target.Equals("local", StringComparison.OrdinalIgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(o.Target, @"^\d{1,3}(\.\d{1,3}){5}$"))
            throw new Exception($"--target: AmsNetId like 5.12.34.56.1.1 or 'local' expected, not '{o.Target}'");
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static int TcUnitList(Options o)
    {
        var d = TcUnitDiscovery.Discover(o.Solution);
        var json = TcUnitDiscovery.ToJson(d);
        if (o.Plc.Count > 0)
        {
            var keep = d.Projects.Where(p => p.Name.Equals(o.Plc[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (keep.Count == 0) { Console.Error.WriteLine($"Error: PLC project '{o.Plc[0]}' not found - the solution has: {string.Join(", ", d.Projects.Select(p => p.Name))}"); return Usage; }
            json["projects"] = new JsonArray(keep.Select(p => (JsonNode)TcUnitDiscovery.ToJson(p)).ToArray());
        }
        var cfg = TcUnitConfig.Load(Path.GetDirectoryName(Path.GetFullPath(o.Solution)));
        json["config"] = new JsonObject
        {
            ["file"] = cfg.File, ["exists"] = File.Exists(cfg.File), ["plcProject"] = cfg.PlcProject, ["target"] = cfg.Target,
            ["timeoutSec"] = (int)cfg.Timeout.TotalSeconds, ["progress"] = cfg.Progress,
            ["errors"] = new JsonArray(cfg.Errors.Select(e => (JsonNode)e).ToArray())
        };
        Console.WriteLine(json.ToJsonString(Indented));
        return Ok;
    }

    /// <summary>HTML report of the last run: the command of "report" in .tcagent\tcunit.json or TcAgent's own report.
    /// Writes the file and a JSON line {"event":"report","html":...} for the window.</summary>
    static int TcUnitReportCli(Options o)
    {
        var sln = Path.GetFullPath(o.Solution);
        var cfg = TcUnitConfig.Load(Path.GetDirectoryName(sln));
        try
        {
            if (o.BuiltIn) cfg.Report = null;
            var html = TcUnitReport.Make(sln, o.Junit, o.Out, cfg, Say);
            Say("REPORT OK: " + html);
            Console.WriteLine(new JsonObject { ["event"] = "report", ["ok"] = true, ["html"] = html }.ToJsonString(Compact));
            return Ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR (report): " + Msg(ex));
            Console.WriteLine(new JsonObject { ["event"] = "report", ["ok"] = false, ["message"] = Msg(ex) }.ToJsonString(Compact));
            return ex is FileNotFoundException ? Failed : Infra;
        }
    }

    static int TcUnitCli(Options o, TwinCatSession tc)
    {
        var cancel = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel = true; };
        // the window sends "cancel" on stdin; a pipeline without stdin just ends the reader
        new Thread(() =>
        {
            // a writer may start the stream with a byte order mark (PowerShell, .NET Framework): not part of the word
            try { for (string l; (l = Console.In.ReadLine()) != null;) if (l.Trim().Trim('﻿').Equals("cancel", StringComparison.OrdinalIgnoreCase)) { cancel = true; break; } }
            catch { }
        }) { IsBackground = true, Name = "tcunit-cancel" }.Start();

        var gate = new object();
        void Emit(JsonObject e)
        {
            lock (gate) Console.WriteLine(e.ToJsonString(Compact));
        }
        var ro = new TcUnitRun.Options
        {
            Solution = o.Solution != null ? Path.GetFullPath(o.Solution) : null, Plc = o.Plc.FirstOrDefault(), Target = o.Target,
            Junit = o.Junit, NoBuild = o.NoBuild, Download = o.Download,
            Timeout = o.TimeoutGiven ? TimeSpan.FromSeconds(o.TimeoutSec) : null
        };
        ro.Tests.AddRange(o.Tests);
        var run = new TcUnitRun(tc, Emit, () => cancel);
        var rc = run.Run(ro);
        Say(run.Summary ?? "");
        return rc;
    }
}
