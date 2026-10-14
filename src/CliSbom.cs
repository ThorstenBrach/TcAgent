// Library inventory commands for CI pipelines (LibraryInventory.cs):
//
//   TcAgent.exe sbom --solution <sln> [--out <file.cdx.json>] [--plc <names>]
//       CycloneDX 1.6 SBOM per PLC project (default <solution folder>\sbom\<PLC project>.cdx.json) - build artifact.
//   TcAgent.exe lock --solution <sln> [--plc <names>] [--check]
//       writes <solution folder>\.tcagent\libraries.lock.json; --check compares the current library resolution with it
//       (exit 0 = equal, 1 = differences, 2 = no lock file) - pipeline gate.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static partial class Cli
{
    static string SolutionDirOf(TwinCatSession tc)
    {
        var d = Path.GetDirectoryName(SolutionName(tc));
        return string.IsNullOrEmpty(d) ? Directory.GetCurrentDirectory() : d;
    }

    /// <summary>SBOM (CycloneDX 1.6 JSON) per selected PLC project.</summary>
    static int Sbom(Options o, TwinCatSession tc)
    {
        var dir = SolutionDirOf(tc);
        var infos = tc.Sta.Run(() => LibraryInventory.Read(tc, SelectedPlcs(o, tc)), o.TimeoutSec * 1000);
        foreach (var p in infos)
        {
            var file = LibraryInventory.SbomFile(dir, o.Out, p.Name, infos.Count);
            var text = LibraryInventory.WriteSbom(p, dir, file);
            var lines = text.Split('\n');
            Say(lines[0].Replace("SBOM written:", "SBOM OK:"));
            foreach (var l in lines.Skip(1)) Console.WriteLine(l);
        }
        return Ok;
    }

    /// <summary>Writes the library lock file, or with --check compares the current resolution with it.</summary>
    static int Lock(Options o, TwinCatSession tc)
    {
        var dir = SolutionDirOf(tc);
        var infos = tc.Sta.Run(() => LibraryInventory.Read(tc, SelectedPlcs(o, tc)), o.TimeoutSec * 1000);
        var partial = o.Plc.Count > 0;
        if (!o.CheckLock)
        {
            Say(LibraryInventory.WriteLock(dir, infos, partial));
            return Ok;
        }
        var (code, text) = LibraryInventory.CheckLock(dir, infos, partial);
        var lines = text.Split('\n');
        Say(lines[0]);
        foreach (var l in lines.Skip(1)) Console.WriteLine(l);
        if (code == 1)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine($"::error title=TcAgent lock::{text.Replace("\n", "%0A")}");
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
                Console.WriteLine($"##vso[task.logissue type=error]TcAgent lock: {lines[0]}");
        }
        return code switch { 0 => Ok, 1 => Failed, _ => Usage };
    }
}
