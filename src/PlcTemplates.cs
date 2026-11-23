// Templates for new PLC projects (tc_plc_project_add): where TwinCAT keeps the .plcproj templates. Pure file logic (no IDE),
// so the unit tests link this file.
//
//   <Components>\Plc\PlcTemplate\Plc Templates\<name>.plcproj                          classic place (4024)
//   <Components>\Plc\Build_<build>\PlcTemplates\<version>\Plc Templates\<name>.plcproj  newer builds (remote manager), newest first
//
// <Components> = %TWINCAT3DIR%\Components, C:\TwinCAT\3.1\Components, C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static class PlcTemplates
{
    /// <summary>The Components folders of the TwinCAT installation that exist, in the order they are searched.</summary>
    public static List<string> ComponentRoots()
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        if (!string.IsNullOrEmpty(env)) roots.Add(Path.Combine(env, "Components"));
        roots.Add(@"C:\TwinCAT\3.1\Components");
        roots.Add(@"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components");
        return roots.Select(r => { try { return Path.GetFullPath(r).TrimEnd('\\'); } catch { return r; } })
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>The template folders below these Components folders that exist: the classic one first, then those of the
    /// builds (highest build, highest version first).</summary>
    public static List<string> Folders(IEnumerable<string> componentRoots)
    {
        var list = new List<string>();
        foreach (var root in componentRoots)
        {
            var plc = Path.Combine(root, "Plc");
            list.Add(Path.Combine(plc, "PlcTemplate", "Plc Templates"));
            string[] builds;
            try { builds = Directory.Exists(plc) ? Directory.GetDirectories(plc, "Build_*") : Array.Empty<string>(); } catch { builds = Array.Empty<string>(); }
            foreach (var b in builds.OrderByDescending(d => VersionOf(Path.GetFileName(d)["Build_".Length..])))
            {
                var pt = Path.Combine(b, "PlcTemplates");
                string[] versions;
                try { versions = Directory.Exists(pt) ? Directory.GetDirectories(pt) : Array.Empty<string>(); } catch { versions = Array.Empty<string>(); }
                foreach (var v in versions.OrderByDescending(d => VersionOf(Path.GetFileName(d))))
                    list.Add(Path.Combine(v, "Plc Templates"));
            }
        }
        return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static Version VersionOf(string s) => Version.TryParse(s, out var v) ? v : int.TryParse(s, out var n) ? new Version(n, 0) : new Version(0, 0);

    /// <summary>The template file of this name (with or without .plcproj) in the first folder that has it; null = none.</summary>
    public static string Find(string name, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var file = name.Trim().EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase) ? name.Trim() : name.Trim() + ".plcproj";
        return folders.Select(f => Path.Combine(f, file)).FirstOrDefault(File.Exists);
    }

    /// <summary>The names of the templates in these folders (for the error message).</summary>
    public static List<string> Available(IEnumerable<string> folders) =>
        folders.SelectMany(f => { try { return Directory.GetFiles(f, "*.plcproj"); } catch { return Array.Empty<string>(); } })
               .Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
}
