// TcAgentBridge - "Format ST code": the formatter of TcAgent (TcAgent.exe format, src/StFormatter.cs) on the PLC object
// that is open in the editor - declaration and implementation, also unsaved, as one step that Ctrl+Z takes back.
//
//   Hotkey Ctrl+Alt+F ("formatHotkey" in chat-backends.json, "off" switches it off), menu Tools > Format ST code (TcAgent).
//   The rules come from formatting.json on the knowledge levels of the solution (TcAgent.exe format --show-rules).
//   The text goes into the editor like a paste of the whole part, so the undo of the editor takes it back in one step.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.Shell.Interop;

namespace TcAgentBridge
{
    public sealed partial class BridgePackage
    {
        const int CmdFormat = 0x0103;
        System.Windows.Forms.Keys fmtKey = System.Windows.Forms.Keys.F;
        System.Windows.Forms.Keys fmtMods = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt;
        bool fmtOn = true;

        void ReadFormatHotkey()
        {
            var spec = ChatSetting("formatHotkey") as string ?? "Ctrl+Alt+F";
            if (spec.Trim().Length == 0 || spec.Equals("off", StringComparison.OrdinalIgnoreCase)) { fmtOn = false; Log("Format hotkey disabled"); return; }
            System.Windows.Forms.Keys mods = 0, key = 0;
            foreach (var part in spec.Split('+').Select(x => x.Trim()))
            {
                var p = part.ToLowerInvariant();
                if (p == "ctrl" || p == "strg" || p == "control") mods |= System.Windows.Forms.Keys.Control;
                else if (p == "shift" || p == "umschalt") mods |= System.Windows.Forms.Keys.Shift;
                else if (p == "alt") mods |= System.Windows.Forms.Keys.Alt;
                else key = (System.Windows.Forms.Keys)Enum.Parse(typeof(System.Windows.Forms.Keys), part, true);
            }
            if (key != 0) { fmtKey = key; fmtMods = mods; }
            Log("Format hotkey: " + spec);
        }

        bool IsFormatKey(int vk) => fmtOn && vk == (int)fmtKey && System.Windows.Forms.Control.ModifierKeys == fmtMods;

        /// <summary>Formats the PLC object in the active editor: both parts, each only when it changes.</summary>
        internal void FormatFromEditor()
        {
            var said = new List<string>();
            fmtGerman = UiGerman();
            try
            {
                var dv = ActiveDocView(out var doc);
                var cds = SafeGet(dv, "EditorView") ?? SafeGet(SafeGet(EnvProp("Engine"), "Frame"), "ActiveView");
                var halves = new List<(System.Windows.Forms.Control Syn, string Part)>();
                foreach (var pn in new[] { new[] { "InterfaceEditorView", "declaration" }, new[] { "ImplementationEditorView", "implementation" } })
                {
                    if (!(SafeGet(cds, pn[0]) is System.Windows.Forms.Control ctl)) continue;
                    var syn = ctl.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0 ? ctl
                            : Descendants(ctl).FirstOrDefault(c => c.GetType().Name.IndexOf("SynEd", StringComparison.Ordinal) >= 0);
                    if (syn != null) halves.Add((syn, pn[1]));
                }
                // actions and transitions: one text editor without a declaration
                if (halves.Count == 0 && SafeCall(dv, "GetActiveSynEdControl", SafeGet(dv, "EditorView")) is System.Windows.Forms.Control only)
                    halves.Add((only, "implementation"));
                if (halves.Count == 0) { FormatMessage(T("Format ST code: open a POU, method, property, action, DUT or GVL in the editor first.", "ST-Code formatieren: Öffne zuerst eine POU, Methode, Eigenschaft, Aktion, einen DUT oder eine GVL im Editor."), true); return; }
                var exe = TcUnitControl.TcAgentExe();
                if (exe == null) { FormatMessage(T("Format ST code: TcAgent.exe was not found - set it up in the TcAgent Chat first.", "ST-Code formatieren: TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
                var sln = TcUnitControl.SolutionPath();
                int changed = 0;
                foreach (var (syn, part) in halves)
                {
                    if (SafeGet(syn, "ReadOnly") is bool ro && ro) { said.Add(part + T(": read only (online?) - not formatted", ": schreibgeschützt (online?) - nicht formatiert")); continue; }
                    var text = SafeGet(syn, "Text") as string;
                    if (string.IsNullOrEmpty(text)) continue;
                    var res = RunFormatter(exe, sln, part, text);
                    if (res.TryGetValue("error", out var err) && err is string e && e.Length > 0) { said.Add(part + ": " + e); continue; }
                    if (!(res.TryGetValue("changed", out var ch) && ch is bool c && c)) continue;
                    var formatted = res["text"] as string ?? text;
                    if (!Replace(syn, formatted)) said.Add(part + T(": the editor did not take the text - check it, Ctrl+Z takes it back", ": der Editor hat den Text nicht übernommen - bitte prüfen, Strg+Z nimmt es zurück"));
                    else changed++;
                }
                var msg = changed > 0 ? T($"TcAgent: formatted ({changed} part{(changed == 1 ? "" : "s")}) - Ctrl+Z takes it back", $"TcAgent: formatiert ({changed} Teil{(changed == 1 ? "" : "e")}) - Strg+Z nimmt es zurück")
                                      : T("TcAgent: already formatted", "TcAgent: bereits formatiert");
                if (said.Count > 0) FormatMessage(T("Format ST code", "ST-Code formatieren") + "\n\n" + string.Join("\n", said), true);
                SetStatus(msg);
                Log(msg + (said.Count > 0 ? " | " + string.Join(" | ", said) : ""));
            }
            catch (Exception ex) { FormatMessage(T("Format ST code failed: ", "ST-Code formatieren fehlgeschlagen: ") + Inner(ex), true); }
        }

        Dictionary<string, object> RunFormatter(string exe, string sln, string part, string text)
        {
            var args = "format --stdin --json --part " + part + (string.IsNullOrEmpty(sln) ? "" : " --solution \"" + sln + "\"");
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(psi))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new Exception("TcAgent.exe format did not answer within 30 s"); }
                var output = outTask.Result;
                if (string.IsNullOrWhiteSpace(output)) throw new Exception("TcAgent.exe format: " + errTask.Result.Trim() + " (TcAgent 0.54 or newer is needed)");
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(output);
            }
        }

        static string Norm(string s) => (s ?? "").Replace("\r\n", "\n");

        /// <summary>The whole part replaced like a paste (select all, paste), so the editor's undo takes it back in one step.</summary>
        bool Replace(System.Windows.Forms.Control syn, string text)
        {
            string clip = null;
            bool hadText = false;
            try { hadText = System.Windows.Forms.Clipboard.ContainsText(); if (hadText) clip = System.Windows.Forms.Clipboard.GetText(); } catch { }
            try
            {
                syn.Focus();
                System.Windows.Forms.Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                var selectAll = AllMethods(syn.GetType()).FirstOrDefault(m => m.Name == "SelectAll" && m.GetParameters().Length == 0);
                var paste = AllMethods(syn.GetType()).FirstOrDefault(m => m.Name == "Paste" && m.GetParameters().Length == 0);
                if (selectAll != null && paste != null)
                {
                    try { selectAll.Invoke(syn, null); paste.Invoke(syn, null); } catch (Exception ex) { Log("Format: SelectAll/Paste: " + Inner(ex)); }
                    if (Norm(SafeGet(syn, "Text") as string) == Norm(text)) return true;
                }
                // the keys a person would press
                syn.Focus();
                System.Windows.Forms.SendKeys.SendWait("^a");
                System.Windows.Forms.SendKeys.SendWait("^v");
                System.Windows.Forms.Application.DoEvents();
                return Norm(SafeGet(syn, "Text") as string) == Norm(text);
            }
            finally
            {
                try { if (hadText && clip != null) System.Windows.Forms.Clipboard.SetText(clip); else System.Windows.Forms.Clipboard.Clear(); } catch { }
            }
        }

        void SetStatus(string text)
        {
            try { if (GetService(typeof(SVsStatusbar)) is IVsStatusbar sb) sb.SetText(text); } catch { }
        }

        void FormatMessage(string text, bool warn)
        {
            Log(text);
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner != null) System.Windows.MessageBox.Show(owner, text, "TcAgent", System.Windows.MessageBoxButton.OK, warn ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
            else System.Windows.MessageBox.Show(text, "TcAgent");
        }
    }
}
