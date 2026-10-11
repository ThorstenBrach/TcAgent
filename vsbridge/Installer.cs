// TcAgentBridge - setup when installed via VSIX (no manual scripts):
//   1. Copy TcAgent.exe from the extension to %LOCALAPPDATA%\TcAgent\app\<version>\ (stable path for the AI clients;
//      the VS extension folder changes with every update)
//   2. Generate the interop assembly for the Automation Interface from the local TwinCAT installation (%LOCALAPPDATA%\TcAgent\lib\)
//      - it is derived from Beckhoff's type library and is therefore not redistributed
//   3. Set up the AI clients (setup-clients.ps1) - the first time only after asking, automatically on updates
// Runs in the background; result in the chat window and in %LOCALAPPDATA%\TcAgent\setup.log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TcAgentBridge
{
    static class Installer
    {
        static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
        static readonly string StateFile = Path.Combine(Local, "setup-state.json");
        static readonly string SetupLog = Path.Combine(Local, "setup.log");

        /// <summary>Source folder "tcagent" in the installed extension; not present for a developer installation via install.ps1.</summary>
        static string Source => Path.Combine(Path.GetDirectoryName(typeof(Installer).Assembly.Location) ?? "", "tcagent");

        public static string AppDir => Path.Combine(Local, "app", BridgePackage.BridgeVersion);
        public static string Exe => Path.Combine(AppDir, "TcAgent.exe");
        public static bool IsVsixInstall => File.Exists(Path.Combine(Source, "TcAgent.exe"));

        /// <summary>On extension startup (UI thread, idle). On the very first run, asks whether the AI clients should be set up.</summary>
        public static void OnStartup(Action<string> log, Action<string> notify)
        {
            if (!IsVsixInstall) return;
            var state = LoadState();
            bool consent = state.TryGetValue("consent", out var c) && c is bool b && b;
            bool asked = state.ContainsKey("consent");
            bool newVersion = !(state.TryGetValue("version", out var v) && (v as string) == BridgePackage.BridgeVersion);
            if (!asked)
            {
                var r = System.Windows.MessageBox.Show(
                    "TcAgent is installed.\n\nRegister TcAgent in the AI clients installed on this PC now?\n" +
                    "(Claude Desktop/Code, GitHub Copilot in VS and CLI, Gemini CLI, Codex CLI - only those found; " +
                    "every changed config file is backed up first)\n\nYou can do this later with \"Set up clients\" in the TcAgent Chat window.",
                    "Set up TcAgent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                consent = r == System.Windows.MessageBoxResult.Yes;
                state["consent"] = consent;
                SaveState(state);
            }
            if (newVersion || !File.Exists(Exe)) Task.Run(() => Run(consent, log, notify));
        }

        /// <summary>Copy, generate the interop and (with consent) set up the clients. Also via the button in the chat window.</summary>
        public static void Run(bool setupClients, Action<string> log, Action<string> notify)
        {
            try
            {
                if (!IsVsixInstall) { notify("Developer installation: run setup-clients.ps1 from the source folder."); return; }
                CopyApp(log);
                var interop = EnsureInterop(log);
                if (interop == null) notify("TcAgent: TwinCAT type library not found - is TwinCAT 3 XAE installed? Details: " + SetupLog);
                if (setupClients)
                {
                    var (code, output) = RunSetup();
                    File.WriteAllText(SetupLog, output);
                    var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ")).ToList();
                    notify(code == 0
                        ? $"TcAgent {BridgePackage.BridgeVersion} registered:\n  " + string.Join("\n  ", lines) + "\nRestart Claude Desktop completely (tray icon > Quit) if you use it."
                        : $"TcAgent: client setup failed (exit code {code}) - see {SetupLog}");
                }
                var state = LoadState();
                state["version"] = BridgePackage.BridgeVersion;
                SaveState(state);
                CleanupOldVersions(log);
            }
            catch (Exception ex) { log("Installer: " + ex); notify("TcAgent setup: " + ex.Message); }
        }

        static void CopyApp(Action<string> log)
        {
            if (File.Exists(Exe) && new FileInfo(Exe).Length == new FileInfo(Path.Combine(Source, "TcAgent.exe")).Length) return;
            foreach (var f in Directory.GetFiles(Source, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(AppDir, f.Substring(Source.Length).TrimStart('\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
            }
            log("TcAgent copied to " + AppDir);
        }

        static void CleanupOldVersions(Action<string> log)
        {
            var root = Path.Combine(Local, "app");
            foreach (var d in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(d), BridgePackage.BridgeVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); log("old version removed: " + d); }
                catch { /* still in use by a client - next time */ }
            }
        }

        static (int, string) RunSetup()
        {
            var script = Path.Combine(AppDir, "setup-clients.ps1");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -TcAgentExe \"{Exe}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppDir
            };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return (-1, "Timeout"); }
                return (p.ExitCode, outTask.Result + (errTask.Result.Length > 0 ? "\n--- Errors ---\n" + errTask.Result : ""));
            }
        }

        // ------------------------------------------------------------------ Interop from the TwinCAT type library
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void LoadTypeLibEx(string file, int regKind, [MarshalAs(UnmanagedType.Interface)] out object typeLib);

        public static string EnsureInterop(Action<string> log)
        {
            var dir = Path.Combine(Local, "lib");
            var file = Path.Combine(dir, "Interop.TCatSysManagerLib.dll");
            if (File.Exists(file)) return file;
            var tlb = FindTlb();
            if (tlb == null) { log("TCatSysManager.tlb not found"); return null; }
            Directory.CreateDirectory(dir);
            LoadTypeLibEx(tlb, 2 /* REGKIND_NONE */, out var typeLib);
            var sink = new Sink(dir, log);
            var ab = new TypeLibConverter().ConvertTypeLibToAssembly(typeLib, file, TypeLibImporterFlags.None, sink, null, null, "TCatSysManagerLib", null);
            ab.Save(Path.GetFileName(file));
            log("Interop generated: " + file + " from " + tlb);
            return file;
        }

        static string FindTlb()
        {
            var roots = new List<string>
            {
                @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\TypeLib",
                @"C:\TwinCAT\3.1\Components\Base\TypeLib",
            };
            var tc = Environment.GetEnvironmentVariable("TWINCAT3DIR");
            if (!string.IsNullOrEmpty(tc)) roots.Add(Path.Combine(tc, "Components", "Base", "TypeLib"));
            foreach (var r in roots.Where(Directory.Exists))
            {
                var hit = Directory.GetFiles(r, "TCatSysManager.tlb", SearchOption.AllDirectories)
                    .OrderByDescending(f => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(f)), out var v) ? v : new Version(0, 0))
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        sealed class Sink : ITypeLibImporterNotifySink
        {
            readonly string dir; readonly Action<string> log;
            public Sink(string dir, Action<string> log) { this.dir = dir; this.log = log; }
            public void ReportEvent(ImporterEventKind kind, int code, string msg) { if (kind != ImporterEventKind.NOTIF_TYPECONVERTED) log("TypeLib: " + msg); }
            public Assembly ResolveRef(object typeLib)
            {
                // convert dependent type libraries as well (stdole comes as a PIA from the GAC)
                var name = Marshal.GetTypeLibName((ITypeLib)typeLib);
                var file = Path.Combine(dir, "Interop." + name + ".dll");
                if (File.Exists(file)) return Assembly.LoadFrom(file);
                var ab = new TypeLibConverter().ConvertTypeLibToAssembly(typeLib, file, TypeLibImporterFlags.None, this, null, null, name, null);
                ab.Save(Path.GetFileName(file));
                return ab;
            }
        }

        // ------------------------------------------------------------------ State
        static Dictionary<string, object> LoadState()
        {
            try { if (File.Exists(StateFile)) return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(StateFile)) ?? new Dictionary<string, object>(); }
            catch { }
            return new Dictionary<string, object>();
        }

        static void SaveState(Dictionary<string, object> s)
        {
            Directory.CreateDirectory(Local);
            File.WriteAllText(StateFile, new JavaScriptSerializer().Serialize(s));
        }
    }
}
