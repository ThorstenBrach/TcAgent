// TcAgentBridge - "Format ST code": the formatter of TcAgent (TcAgent.exe format, src/StFormatter.cs) on the PLC object
// that is open in the editor - declaration and implementation, also unsaved, as one step that Ctrl+Z takes back.
//
//   Hotkey Ctrl+Alt+F ("formatHotkey" in chat-backends.json, "off" switches it off), menu Tools > Format ST code (TcAgent).
//   The rules come from formatting.json on the knowledge levels of the solution (TcAgent.exe format --show-rules).
//   The text goes into the editor like a paste of the whole part, so the undo of the editor takes it back in one step.
//   Format on save: the rule editor.format-on-save (formatting.json, Formatter settings > In the editor) formats the object of
//   the active window before File > Save / Ctrl+S / Save All runs - quietly, the status bar says what happened.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

        /// <summary>Formats the PLC object in the active editor: both parts, each only when it changes.
        /// onSave: before a save - no message boxes, nothing said when there is nothing to format.</summary>
        internal void FormatFromEditor(bool onSave = false)
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
                if (halves.Count == 0 && onSave) return;
                if (halves.Count == 0) { FormatMessage(T("Format ST code: open a POU, method, property, action, DUT or GVL in the editor first.", "ST-Code formatieren: Öffne zuerst eine POU, Methode, Eigenschaft, Aktion, einen DUT oder eine GVL im Editor."), true); return; }
                var exe = TcUnitControl.TcAgentExe();
                if (exe == null && onSave) { Log("Format on save: TcAgent.exe not found"); return; }
                if (exe == null) { FormatMessage(T("Format ST code: TcAgent.exe was not found - set it up in the TcAgent Chat first.", "ST-Code formatieren: TcAgent.exe wurde nicht gefunden - richte TcAgent zuerst im TcAgent Chat ein."), true); return; }
                var sln = TcUnitControl.SolutionPath();
                int changed = 0;
                var offNotes = new List<string>();
                string atCaret = null;
                foreach (var (syn, part) in halves)
                {
                    if (SafeGet(syn, "ReadOnly") is bool ro && ro) { said.Add(part + T(": read only (online?) - not formatted", ": schreibgeschützt (online?) - nicht formatiert")); continue; }
                    var text = SafeGet(syn, "Text") as string;
                    if (string.IsNullOrEmpty(text)) continue;
                    // the caret line before formatting (the text moves afterwards): in the part with the focus, or the only part
                    int caretLine = (halves.Count == 1 || syn.Focused || syn.ContainsFocus) && SafeGet(syn, "CaretLine") is int cl ? cl + 1 : 0;
                    var res = RunFormatter(exe, sln, part, text);
                    // the caret stands in a section switched off in the code: say by which marker, else "already formatted" misleads
                    if (caretLine > 0 && res.TryGetValue("off", out var offs) && offs is System.Collections.IEnumerable offList)
                        foreach (var o in offList.OfType<Dictionary<string, object>>())
                        {
                            int from = o.TryGetValue("from", out var fv) && fv is int fi ? fi : 0, to = o.TryGetValue("to", out var tv) && tv is int ti ? ti : 0;
                            if (from <= 0 || caretLine < from || to > 0 && caretLine > to) continue;
                            var marker = o.TryGetValue("marker", out var mv) ? mv as string : "";
                            atCaret = to == 0 ? T($"the formatter is switched off here: {marker} in line {from}, without 'on' up to the end", $"der Formatierer ist hier ausgeschaltet: {marker} in Zeile {from}, ohne 'on' bis zum Ende")
                                              : T($"the formatter is switched off here: {marker} in line {from} (up to line {to})", $"der Formatierer ist hier ausgeschaltet: {marker} in Zeile {from} (bis Zeile {to})");
                            break;
                        }
                    // a "format off" without "on" after it: from there to the end nothing is formatted - say so, else "already formatted" misleads
                    if (res.TryGetValue("offToEnd", out var ote) && ote is int offLine && offLine > 0)
                        offNotes.Add(T($"{part}: switched off from line {offLine} to the end ('off' without 'on')", $"{(part == "declaration" ? "Deklaration" : "Implementierung")}: ab Zeile {offLine} bis zum Ende ausgeschaltet ('off' ohne 'on')"));
                    if (res.TryGetValue("error", out var err) && err is string e && e.Length > 0) { said.Add(part + ": " + e); continue; }
                    if (!(res.TryGetValue("changed", out var ch) && ch is bool c && c)) continue;
                    var formatted = res["text"] as string ?? text;
                    if (!Replace(syn, formatted)) said.Add(part + T(": the editor did not take the text - check it, Ctrl+Z takes it back", ": der Editor hat den Text nicht übernommen - bitte prüfen, Strg+Z nimmt es zurück"));
                    else changed++;
                }
                if (onSave && changed == 0 && said.Count == 0 && atCaret == null && offNotes.Count == 0) { Log("Format on save: already formatted"); return; }
                var msg = changed > 0 ? T($"TcAgent: {(onSave ? "formatted on save" : "formatted")} ({changed} part{(changed == 1 ? "" : "s")}) - Ctrl+Z takes it back", $"TcAgent: {(onSave ? "beim Speichern formatiert" : "formatiert")} ({changed} Teil{(changed == 1 ? "" : "e")}) - Strg+Z nimmt es zurück")
                                      : T("TcAgent: already formatted", "TcAgent: bereits formatiert");
                if (atCaret != null) msg = (changed > 0 ? T($"TcAgent: formatted ({changed} part{(changed == 1 ? "" : "s")}), but ", $"TcAgent: formatiert ({changed} Teil{(changed == 1 ? "" : "e")}), aber ") : "TcAgent: ") + atCaret;
                else if (offNotes.Count > 0) msg += " - " + string.Join("; ", offNotes);
                if (said.Count > 0 && onSave) msg += " - " + string.Join("; ", said);
                else if (said.Count > 0) FormatMessage(T("Format ST code", "ST-Code formatieren") + "\n\n" + string.Join("\n", said), true);
                SetStatus(msg);
                // the save writes "Item(s) Saved" into the status bar afterwards: say it again once it is done
                if (onSave) ui?.BeginInvoke(new Action(() => SetStatus(msg)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Log(msg + (said.Count > 0 ? " | " + string.Join(" | ", said) : ""));
            }
            catch (Exception ex) when (onSave) { SetStatus(T("TcAgent: format on save failed - saved as it was: ", "TcAgent: Formatieren beim Speichern fehlgeschlagen - so gespeichert, wie es war: ") + Inner(ex)); Log("Format on save: " + Inner(ex)); }
            catch (Exception ex) { FormatMessage(T("Format ST code failed: ", "ST-Code formatieren fehlgeschlagen: ") + Inner(ex), true); }
        }

        // ------------------------------------------------------------------ format on save
        // File > Save (Ctrl+S, "SaveSelectedItems") and Save All of Visual Studio: before they run, the PLC object of the active
        // window is formatted when editor.format-on-save is true on the knowledge levels of the solution. The rule is read with
        // TcAgent.exe format --show-rules: once per solution before the first save, then again in the background when it is
        // older than 30 s (a formatting.json edited by hand counts from the next save) and right after Save in the settings window.
        // Only saves a person starts (a key or a click just before): File.SaveAll of the agent or of a tool (tc_save, TcUnit run,
        // docs) does not format - the agent formats only the code it wrote itself.
        EnvDTE.CommandEvents saveCmdEvents;     // the field keeps the COM event source alive (otherwise the events stop)
        readonly object fmtOnSaveLock = new object();
        string fmtOnSaveSln;
        bool fmtOnSaveValue;
        DateTime fmtOnSaveRead = DateTime.MinValue;
        bool fmtOnSaveRefreshing, fmtOnSaveBusy;
        int fmtOnSaveGen;

        void InstallFormatOnSave()
        {
            var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null) { Log("Format on save: DTE not available"); return; }
            saveCmdEvents = dte.Events.get_CommandEvents(Microsoft.VisualStudio.VSConstants.GUID_VSStandardCommandSet97.ToString("B"), -1);
            saveCmdEvents.BeforeExecute += OnBeforeSaveCommand;
            Log("Format on save: watching Save and Save All");
        }

        void OnBeforeSaveCommand(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
        {
            bool saveAll = id == (int)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.SaveSolution;
            if (!saveAll && id != (int)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.SaveProjectItem && id != (int)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.Save) return;
            if (fmtOnSaveBusy) return;
            try
            {
                if (!ByPerson()) return;
                if (!FormatOnSaveOn()) return;
                // Save (selected items) with the focus in a tool window saves what is selected there, not the open editor
                if (!saveAll && (GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveWindow?.Type != EnvDTE.vsWindowType.vsWindowTypeDocument) return;
                fmtOnSaveBusy = true;
                Log("Format on save (" + (saveAll ? "Save All" : "Save") + ")");
                FormatFromEditor(onSave: true);
            }
            catch (Exception ex) { Log("Format on save: " + Inner(ex)); }
            finally { fmtOnSaveBusy = false; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        /// <summary>A key or a click in the last 2 seconds: the save comes from a person, not from the agent or a tool.</summary>
        static bool ByPerson()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return true;
            return unchecked((uint)Environment.TickCount - li.dwTime) < 2000;
        }

        /// <summary>editor.format-on-save for the open solution - the cached value, read first when there is none for it.</summary>
        bool FormatOnSaveOn()
        {
            var sln = TcUnitControl.SolutionPath() ?? "";
            bool known, stale;
            lock (fmtOnSaveLock)
            {
                known = fmtOnSaveSln == sln && fmtOnSaveRead != DateTime.MinValue;
                stale = !known || DateTime.UtcNow - fmtOnSaveRead > TimeSpan.FromSeconds(30);
            }
            if (!known) return ReadFormatOnSave(sln);
            if (stale) RefreshFormatOnSave();
            lock (fmtOnSaveLock) return fmtOnSaveValue;
        }

        /// <summary>After Save in the settings window: the next save reads the rule again (a read still running does not count).</summary>
        internal void ForgetFormatOnSave()
        {
            lock (fmtOnSaveLock) { fmtOnSaveGen++; fmtOnSaveRead = DateTime.MinValue; }
        }

        /// <summary>Reads the rule again in the background when the cached value is old.</summary>
        void RefreshFormatOnSave()
        {
            lock (fmtOnSaveLock) { if (fmtOnSaveRefreshing) return; fmtOnSaveRefreshing = true; }
            var sln = TcUnitControl.SolutionPath() ?? "";
            System.Threading.Tasks.Task.Run(() =>
            {
                try { ReadFormatOnSave(sln); }
                finally { lock (fmtOnSaveLock) fmtOnSaveRefreshing = false; }
            });
        }

        bool ReadFormatOnSave(string sln)
        {
            int gen;
            lock (fmtOnSaveLock) gen = fmtOnSaveGen;
            bool on = false;
            try
            {
                var exe = TcUnitControl.TcAgentExe();
                if (exe != null)
                {
                    var j = FmtJson(FmtRun(exe, "format --show-rules --json" + (sln.Length == 0 ? "" : " --solution \"" + sln + "\""), null));
                    on = j.TryGetValue("rules", out var r) && r is Dictionary<string, object> rules
                         && rules.TryGetValue("editor.format-on-save", out var rule) && rule is Dictionary<string, object> rd
                         && string.Equals(FmtStr(rd, "value"), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { Log("Format on save: reading the rule: " + Inner(ex)); }
            bool changedValue;
            lock (fmtOnSaveLock)
            {
                if (gen != fmtOnSaveGen) return on;     // the rules were saved meanwhile: this value may be old
                changedValue = fmtOnSaveSln != sln || fmtOnSaveValue != on;
                fmtOnSaveSln = sln; fmtOnSaveValue = on; fmtOnSaveRead = DateTime.UtcNow;
            }
            if (changedValue) Log("Format on save: " + (on ? "on" : "off") + (sln.Length == 0 ? "" : " (" + Path.GetFileName(sln) + ")"));
            return on;
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
            // select all + paste are two entries in the editor's undo list (remove, insert): one compound action makes Ctrl+Z take it back at once
            var undo = UndoManager(syn);
            bool compound = false;
            try { if (undo != null) { undo.GetType().GetMethod("BeginCompoundAction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(undo, null); compound = true; } }
            catch (Exception ex) { Log("Format: BeginCompoundAction: " + Inner(ex)); }
            try
            {
                syn.Focus();
                // the clipboard can be held by another program for a moment: try again for a second
                System.Windows.Forms.Clipboard.SetDataObject(text.Replace("\r\n", "\n").Replace("\n", "\r\n"), true, 10, 100);
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
                if (compound)
                    try { undo.GetType().GetMethod("EndCompoundAction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(undo, null); }
                    catch (Exception ex) { Log("Format: EndCompoundAction: " + Inner(ex)); }
                try { if (hadText && clip != null) System.Windows.Forms.Clipboard.SetText(clip); else System.Windows.Forms.Clipboard.Clear(); } catch { }
            }
        }

        /// <summary>The undo list of the PLC editor (SynEdControl._buffer._undoMgr, in every TwinCAT build so far) - null when not found.</summary>
        static object UndoManager(object syn)
        {
            object Field(object o, string name)
            {
                for (var t = o?.GetType(); t != null; t = t.BaseType)
                    if (t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is FieldInfo f) return f.GetValue(o);
                return null;
            }
            try { return Field(Field(syn, "_buffer"), "_undoMgr"); } catch { return null; }
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
