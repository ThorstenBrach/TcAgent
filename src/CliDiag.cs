// Diagnostics for issue reports - one block instead of a handful of version fields:
//
//   TcAgent.exe diag
//       TcAgent, the extension version registered by setup, TwinCAT, the installed IDEs with their versions, the runtime
//       state of this PC, Windows and .NET, and which knowledge levels are configured - as a Markdown list for the
//       "Diagnostics" field of the GitHub issue forms. No names, no paths with the user name, no addresses.
//       The extension adds its own version and the IDE it runs in ("Copy diagnostics" in the chat menu).
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TcAgent;

public static partial class Cli
{
    static int Diag(Options o)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine(Diagnostics(o.Solution));
        return Ok;
    }

    public static string Diagnostics(string solution = null)
    {
        var o = new StringBuilder();
        void Line(string label, string value) => o.AppendLine($"- **{label}:** {value}");
        string Try(Func<string> f) { try { return f() ?? "?"; } catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; } }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
        var where = AppContext.BaseDirectory.StartsWith(Path.Combine(local, "app"), StringComparison.OrdinalIgnoreCase) ? "installed by the extension" : "own folder";
        Line("TcAgent", $"{Program.ServerVersion} ({(Environment.Is64BitProcess ? "64" : "32")}-bit, {where})");
        Line("Extension registered by setup", Try(() =>
        {
            var f = Path.Combine(local, "setup-state.json");
            return File.Exists(f) ? (string)JsonNode.Parse(File.ReadAllText(f))?["version"] ?? "not yet" : "never (no setup-state.json)";
        }));
        Line("TwinCAT", Try(TwinCatVersion));
        Line("IDEs installed", Try(() => string.Join(", ", InstalledIdes()) is { Length: > 0 } s ? s : "none found"));
        Line("Runtime on this PC", Try(() => { var (state, err) = ReadSystemState(LocalNetId); return state?.ToString() ?? "no answer (" + err + ")"; }));
        Line("Windows", $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})");
        Line(".NET", Environment.Version.ToString());
        Line("Knowledge levels", Try(() =>
        {
            var parts = new List<string> { "company " + (KnowledgeLayers.CompanyRoot != null ? "set" : "not set"), $"team {KnowledgeLayers.TeamRoots.Count}" };
            if (!string.IsNullOrEmpty(solution)) parts.Add("platform " + (KnowledgeLayers.Platforms(Path.GetDirectoryName(Path.GetFullPath(solution))).Active.Count > 0 ? "set" : "none"));
            return string.Join(", ", parts);
        }));
        return o.ToString().TrimEnd();
    }

    static string TwinCatVersion()
    {
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Beckhoff\TwinCAT3\System");
        return k?.GetValue("TcVersion") as string ?? "not installed";
    }

    // Visual Studio through vswhere, the XAE Shell through its executable (4026: own folder, 4024: inside TwinCAT)
    static IEnumerable<string> InstalledIdes()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(pf86, @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (File.Exists(vswhere))
        {
            var psi = new ProcessStartInfo(vswhere, "-all -prerelease -format json -utf8") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi);
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            foreach (var vs in JsonNode.Parse(json)?.AsArray() ?? new JsonArray())
                yield return $"{(string)vs?["displayName"]} {(string)vs?["catalog"]?["productDisplayVersion"]}".Trim();
        }
        foreach (var exe in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe"),
                                    Path.Combine(pf86, @"Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe"),
                                    Path.Combine(pf86, @"Beckhoff\TwinCAT\3.1\Components\TcXaeShell\Common7\IDE\TcXaeShell.exe") }.Distinct())
            if (File.Exists(exe))
                yield return $"TwinCAT XAE Shell {FileVersionInfo.GetVersionInfo(exe).ProductVersion} ({(exe.Contains("x86") ? "32" : "64")}-bit)";
    }
}
