// TcAgentBridge - "Reload" in the TcAgent settings window (SettingsWindow.cs): every running TcAgent server on this PC reads its
// settings again (src/Reload.cs) - settings.json, targets and mode, modules.json, plugins, the skills of the agent CLIs - and the
// chat reads chat-backends.json again. What each server changed comes back from reload.<pid>.json.
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
        /// <summary>The chat reads chat-backends.json again (UI thread).</summary>
        internal void ReloadChatSettings() { try { LoadSettings(); } catch { } }

        /// <summary>Asks every running TcAgent server to read its settings again and waits for the answers: one line per server
        /// ("TcAgent (pid 123): nothing changed"); empty when no server runs.</summary>
        internal static async Task<List<string>> ReloadServers()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
            var asked = DateTime.UtcNow;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "reload.signal"), asked.ToString("o"));
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
            return lines;
        }
    }
}
