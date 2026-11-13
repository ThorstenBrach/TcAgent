// TcAgentBridge - installing an update from the TcAgent Chat, in Visual Studio 2022 (the .vsix) and in the TwinCAT XAE Shell
// (the package TcAgent-XaeShell-<version>.zip with install-xaeshell.ps1). Both from a GitHub release (downloaded and checked
// by TcAgent.exe update download) and from a test build in TCAGENT_UPDATE_FOLDER (src/LocalBuilds.cs: newer, or the same
// version built again).
//
// The extension is loaded by the running IDE, so the IDE has to be closed for the installation and started again afterwards.
// After the user agreed, a small helper (PowerShell, %LOCALAPPDATA%\TcAgent\updates\install-update.ps1) waits until this
// IDE has ended, installs - VSIXInstaller /q, or the shell script (which asks for administrator rights) - and starts the IDE
// again with the same solution. The IDE is closed with File.Exit, so it asks to save open changes; if the user cancels
// that, nothing is installed until the IDE ends (at most 30 minutes). The log: updates\install-update.log.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        Border localStrip;

        /// <summary>The package this IDE installs: vsix (Visual Studio 2022), xaeshell-vs15 (32-bit shell), xaeshell-vs17 (64-bit).</summary>
        static string PackageKind
        {
            get
            {
                try
                {
                    var n = Process.GetCurrentProcess().ProcessName;
                    if (n.IndexOf("TcXaeShell64", StringComparison.OrdinalIgnoreCase) >= 0) return "xaeshell-vs17";
                    if (n.IndexOf("TcXaeShell", StringComparison.OrdinalIgnoreCase) >= 0) return "xaeshell-vs15";
                }
                catch { }
                return "vsix";
            }
        }

        static string IdeName => InXaeShell ? "the XAE Shell" : "Visual Studio";

        // ------------------------------------------------------------------ test builds
        async System.Threading.Tasks.Task CheckLocalBuild(bool asked)
        {
            var bridge = typeof(ChatControl).Assembly.Location;
            var r = await Ask($"update local --json --kind {PackageKind} --bridge \"{bridge}\" --current {BridgePackage.BridgeVersion}");
            if (r == null) return;
            if (localStrip != null) { notices.Children.Remove(localStrip); localStrip = null; }
            if (!(asked ? B(r, "available") : B(r, "notify"))) return;
            var when = DateTime.TryParse(S(r, "built"), out var t) ? t.ToString("dd.MM. HH:mm") : "";
            var what = B(r, "rebuilt") ? $"Test build {S(r, "version")} of {when}" : $"Test build {S(r, "version")} ({when})";
            localStrip = Strip(WarnBrush, new[] { new Run(what) { FontWeight = FontWeights.SemiBold },
                                                  new Run(B(r, "rebuilt") ? " - the same version built again" : $" is newer than {BridgePackage.BridgeVersion}") },
                               "Install …", true, () => InstallPackage(S(r, "file"), what, S(r, "sha")),
                               () => { notices.Children.Remove(localStrip); localStrip = null; });
            notices.Children.Insert(0, localStrip);
        }

        // ------------------------------------------------------------------ install
        /// <summary>Asks, writes the helper, starts it and closes the IDE. skipSha: a test build that "Skip this build" ends.</summary>
        internal void InstallPackage(string file, string label, string skipSha = null)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Message("TcAgent update", "The package was not found:\n" + file); return; }
            var kind = PackageKind;
            if (kind == "vsix" && !file.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase) || kind != "vsix" && !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            { Message("TcAgent update", $"{Path.GetFileName(file)} is not the package for {IdeName}."); return; }
            var sln = TcUnitControl.SolutionPath();
            var w = Dialog("Install TcAgent", 520, 300);
            w.MinHeight = 0; w.SizeToContent = SizeToContent.Height;
            var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 10) };
            sp.Children.Add(new TextBlock { Text = "Install " + label, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = file, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
            sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                $"{(InXaeShell ? "The XAE Shell" : "Visual Studio")} has to be closed for it and is started again afterwards" + (string.IsNullOrEmpty(sln) ? "." : $" with {Path.GetFileName(sln)}.") +
                " It asks to save open changes first." +
                (kind == "vsix" ? " Close other Visual Studio windows too - the installer waits for all of them." : " The installation asks for administrator rights and shows its result in a window.") +
                " Restart the AI clients you use (Claude Desktop …) afterwards, so they start the new TcAgent.exe." });
            var go = CardButton("Close, install and restart", true);
            go.Margin = new Thickness(0); go.MinWidth = 0;
            var skip = CardButton("Skip this build");
            skip.Margin = new Thickness(6, 0, 0, 0); skip.MinWidth = 0; skip.Visibility = skipSha != null ? Visibility.Visible : Visibility.Collapsed;
            skip.ToolTip = "No notice for this test build; another build is shown again";
            var cancel = CardButton("Cancel");
            cancel.IsCancel = true; cancel.Margin = new Thickness(6, 0, 0, 0); cancel.MinWidth = 0;
            var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            bar.Children.Add(go); bar.Children.Add(skip); bar.Children.Add(cancel);
            sp.Children.Add(bar);
            w.Content = sp;
            bool start = false;
            go.Click += (s, e) => { start = true; w.Close(); };
            skip.Click += async (s, e) =>
            {
                await Ask("update local skip " + skipSha);
                if (localStrip != null) { notices.Children.Remove(localStrip); localStrip = null; }
                w.Close();
            };
            cancel.Click += (s, e) => w.Close();
            w.ShowDialog();
            if (!start) return;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent", "updates");
                Directory.CreateDirectory(dir);
                var helper = Path.Combine(dir, "install-update.ps1");
                File.WriteAllText(helper, HelperScript, new UTF8Encoding(true));      // with BOM: Windows PowerShell 5 reads it as UTF-8
                var self = Process.GetCurrentProcess();
                var ide = self.MainModule.FileName;
                var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" -IdePid {self.Id} -Package \"{file}\" -Kind {kind} " +
                           $"-Ide \"{ide}\" -Log \"{Path.Combine(dir, "install-update.log")}\"" + (string.IsNullOrEmpty(sln) ? "" : $" -Solution \"{sln}\"");
                Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true });
                BridgePackage.Instance?.LogLine("Update: helper started for " + file);
                status.Text = $"Installing after {IdeName} has closed …";
                // File.Exit asks to save open changes; cancelled: the helper waits until the IDE ends
                try { (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("File.Exit"); }
                catch (Exception ex) { Message("TcAgent update", $"Close {IdeName} yourself - the installation starts when it has ended.\n({ex.Message})"); }
            }
            catch (Exception ex) { Message("TcAgent update", "Could not start the installation: " + ex.Message); }
        }

        // waits for the IDE, installs, starts the IDE again - everything it does goes to the log
        const string HelperScript = @"# TcAgent - installs an update after the IDE has closed (written by the TcAgent Chat, vsbridge\ChatInstall.cs)
param([int]$IdePid, [string]$Package, [string]$Kind, [string]$Ide, [string]$Solution, [string]$Log)
Start-Transcript -Path $Log -Force | Out-Null
try {
    $p = Get-Process -Id $IdePid -ErrorAction SilentlyContinue
    if ($p -and -not $p.WaitForExit(1800000)) { Write-Host 'The IDE is still running after 30 minutes - nothing installed.'; exit 1 }
    Start-Sleep -Seconds 2
    if ($Kind -eq 'vsix') {
        $installer = Join-Path (Split-Path $Ide) 'VSIXInstaller.exe'
        Write-Host ""$installer /q $Package""
        $i = Start-Process -FilePath $installer -ArgumentList @('/q', ('""' + $Package + '""')) -Wait -PassThru
        Write-Host ""VSIXInstaller ended with $($i.ExitCode)""
    } else {
        $dst = Join-Path $env:LOCALAPPDATA ('TcAgent\updates\xaeshell-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Expand-Archive -Path $Package -DestinationPath $dst -Force
        Get-ChildItem $dst -Recurse -File | Unblock-File
        $script = Join-Path $dst 'install-xaeshell.ps1'
        Write-Host ""$script -ShellIde $(Split-Path $Ide)""
        $i = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('""' + $script + '""'), '-ShellIde', ('""' + (Split-Path $Ide) + '""')) -Wait -PassThru
        Write-Host ""install-xaeshell.ps1 ended with $($i.ExitCode)""
    }
} catch { Write-Host ""Failed: $_"" }
finally {
    if ($Solution) { Start-Process -FilePath $Ide -ArgumentList ('""' + $Solution + '""') } else { Start-Process -FilePath $Ide }
    Stop-Transcript | Out-Null
}
";
    }
}
