// TcAgentBridge - "Reload settings" in the ⋯ menu of the TcAgent Chat: every running TcAgent server on this PC reads its
// settings again (src/Reload.cs) - settings.json, targets and mode, modules.json, plugins, the skills of the agent CLIs - and the
// chat reads chat-backends.json again. The chat shows what each server changed (reload.<pid>.json).
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TcAgentBridge
{
    public sealed partial class ChatControl
    {
        async void ReloadSettings()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
            var asked = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "reload.signal"), asked.ToString("o"));
            }
            catch (Exception ex) { Notice("Reload settings failed: " + ex.Message); return; }
            try { LoadSettings(); } catch { }                 // chat-backends.json of the chat itself
            status.Text = "Reading the settings again …";
            await Task.Delay(3500);                            // the servers look every two seconds
            var lines = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(dir, "reload.*.json"))
                {
                    if (File.GetLastWriteTimeUtc(f) < asked.AddSeconds(-1)) continue;
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                    var changes = (d.TryGetValue("changes", out var c) ? c as ArrayList : null)?.Cast<object>().Select(x => x.ToString()).ToList() ?? new List<string>();
                    var who = d.TryGetValue("client", out var cl) && cl is string s && s.Length > 0 ? s : "TcAgent";
                    lines.Add($"{who} (pid {d["pid"]}): " + (changes.Count == 0 ? "nothing changed" : string.Join("; ", changes)));
                }
            }
            catch (Exception ex) { lines.Add("could not read the answers: " + ex.Message); }
            status.Text = "";
            Notice(lines.Count == 0
                ? "Settings read again by the chat. No TcAgent server is running right now - the next question starts one with the new settings."
                : "Settings read again - " + string.Join(" · ", lines));
        }
    }
}
