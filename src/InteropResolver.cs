// Laedt die Interop-Assembly des TwinCAT Automation Interface zur Laufzeit.
// Sie wird nicht mitverteilt (sie entsteht aus Beckhoffs Typbibliothek): bei Entwickler-Builds liegt sie neben TcAgent.exe,
// bei der VSIX-Installation erzeugt die VS-Erweiterung sie beim ersten Start aus der lokalen TwinCAT-Installation in
// %LOCALAPPDATA%\TcAgent\lib\.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace TcAgent;

static class InteropResolver
{
    // Visual-Studio-Automationsassemblies (EnvDTE) aus der installierten VS 2022 laden - sie werden nicht mitverteilt
    static List<string> vsDirs;
    static IEnumerable<string> VsAssemblyDirs()
    {
        if (vsDirs != null) return vsDirs;
        var roots = new List<string>();
        try
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vswhere, "-version [17.0,18.0) -products * -property installationPath")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                var output = p!.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                roots.AddRange(output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
        }
        catch { }
        foreach (var ed in new[] { "Enterprise", "Professional", "Community" })
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", ed));
        vsDirs = roots.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(r => new[] { @"Common7\IDE\PublicAssemblies", @"Common7\IDE\PrivateAssemblies", @"Common7\IDE" }.Select(d => Path.Combine(r, d)))
            .Where(Directory.Exists).ToList();
        return vsDirs;
    }

    [ModuleInitializer]
    internal static void Init()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == null) return null;
            var n = name.Name;
            bool vs = n.Equals("envdte", StringComparison.OrdinalIgnoreCase) || n.Equals("envdte80", StringComparison.OrdinalIgnoreCase)
                   || n.Equals("Microsoft.VisualStudio.Interop", StringComparison.OrdinalIgnoreCase);
            if (!vs && !n.StartsWith("Interop.", StringComparison.OrdinalIgnoreCase)) return null;
            var dirs = new List<string>
            {
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "lib"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "lib"),
            };
            if (vs) dirs.AddRange(VsAssemblyDirs());
            foreach (var dir in dirs)
            {
                var f = Path.Combine(dir, n + ".dll");
                if (File.Exists(f)) return ctx.LoadFromAssemblyPath(f);
            }
            if (vs)
            {
                Console.Error.WriteLine($"[TcAgent] {n}.dll nicht in der Visual-Studio-Installation gefunden.");
                return null;
            }
            Console.Error.WriteLine($"[TcAgent] {name.Name}.dll nicht gefunden - Visual Studio mit der TcAgent-Erweiterung einmal starten " +
                                    "(erzeugt sie aus der TwinCAT-Installation) oder build.ps1 ausfuehren.");
            return null;
        };
    }
}
