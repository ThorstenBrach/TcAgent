// Read the settings again without a restart: the button "Reload" of the TcAgent settings window (or TcAgent.exe reload; saving there does it too) writes
// %LOCALAPPDATA%\TcAgent\reload.signal; every running TcAgent MCP server on this PC sees it within two seconds and reads
// settings.json ("env"), the policy (targets, mode), modules.json and the plugins again, mirrors the skills and tells its client
// when its tool list changed (notifications/tools/list_changed). Each server writes what it did to reload.<pid>.json, which
// the chat shows. Variables set by the environment itself (a client configuration, a pipeline) still win over settings.json.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Reload
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcAgent");
    public static string SignalFile => Path.Combine(Dir, "reload.signal");
    static DateTime seen = DateTime.MaxValue;

    /// <summary>At the start of the server: a reload asked for before it ran is not one for it.</summary>
    public static void Start() => seen = DateTime.UtcNow;

    /// <summary>Asks every running TcAgent server to read its settings again.</summary>
    public static string Request()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SignalFile, DateTime.UtcNow.ToString("o"));
        return SignalFile;
    }

    /// <summary>Was a reload asked for since the last look? (Cheap: the time of one file.)</summary>
    public static bool Pending()
    {
        try
        {
            if (!File.Exists(SignalFile)) return false;
            var t = File.GetLastWriteTimeUtc(SignalFile);
            if (t <= seen) return false;
            seen = t;
            return true;
        }
        catch { return false; }
    }

    /// <summary>What a server did, for the chat: reload.&lt;pid&gt;.json.</summary>
    public static void Report(List<string> changes)
    {
        try
        {
            File.WriteAllText(Path.Combine(Dir, $"reload.{Environment.ProcessId}.json"), new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["time"] = DateTime.UtcNow.ToString("o"), ["client"] = Program.ClientName,
                ["changes"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray())
            }.ToJsonString());
        }
        catch { }
    }
}
