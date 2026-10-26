// TcAgent command line: skills (part of Cli, see Cli.cs; the logic is in Skills.cs). Files only, no IDE.
//
//   TcAgent.exe skills list   [--solution <sln>]    the skills of the levels (and the platform of the solution)
//   TcAgent.exe skills mirror [--solution <sln>]    copy the skills that apply everywhere into the skill folders of the
//                                                   agent CLIs (setup-clients.ps1 calls it)
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static partial class Cli
{
    static int SkillsCli(Options o)
    {
        var dir = o.Solution != null ? Path.GetDirectoryName(Path.GetFullPath(o.Solution)) : null;
        var entries = Skills.Entries(dir, KnowledgeLayers.Platforms(dir).Active);
        switch (o.Sub)
        {
            case "list":
                Console.WriteLine(Skills.Catalog(entries) ?? "No skills found.");
                foreach (var e in entries.Where(x => !x.Active)) Console.WriteLine($"  (not applied) {e.Name} ({e.Layer.Label}): {e.Status} {e.Reason}");
                return Ok;
            case "mirror":
                foreach (var line in Skills.Mirror(entries, Skills.DefaultMirrors(), write: !o.DryRun)) Console.WriteLine("  " + line);
                return Ok;
            default:
                Console.Error.WriteLine("Error: skills needs list or mirror, e.g. TcAgent.exe skills mirror");
                return Usage;
        }
    }
}
