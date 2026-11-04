// Reset of TcAgent (TcAgent.exe reset, Knowledge window ⋯ > Reset TcAgent): the settings of the user back to a fresh installation,
// so that levels, platforms, sources, plugins and modules can be chosen again.
//
// Nothing is deleted. What takes effect by lying in the user folder moves to %LOCALAPPDATA%\TcAgent\reset-<date>\ and can be
// moved back by hand; settings.json loses its "env" (the connections), a copy goes along. The program (app\), feedback, run logs,
// chat settings, projects with their .tcagent and folders outside TcAgent stay as they are.
//
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class Reset
{
    public const string Confirmation = "delete all";

    /// <summary>The knowledge of the level and what else takes effect by being there.</summary>
    static IEnumerable<string> Names => KnowledgeBackup.Parts.Concat(new[] { "knowledge", "plugins", "sources", "modules.json", "plugins-approved.json" }).Distinct();

    static string SettingsFile => Path.Combine(KnowledgeLayers.UserRoot, "settings.json");

    /// <summary>What a reset would move, and the connections it would remove. Changes nothing.</summary>
    public static (List<string> Items, List<string> Connections) Plan()
    {
        var items = Names.Where(n => { var p = Path.Combine(KnowledgeLayers.UserRoot, n); return File.Exists(p) || Directory.Exists(p); }).ToList();
        var connections = new List<string>();
        try
        {
            if (File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile))?["env"] is JsonObject env)
                connections.AddRange(env.Select(x => x.Key));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return (items, connections);
    }

    /// <summary>Moves everything of Plan() away. A file in use (a running plugin) is named in Failed and stays: the rest goes on,
    /// and a second run takes what is left.</summary>
    public static (string Folder, List<string> Moved, List<string> Failed) Run(DateTime now)
    {
        var (items, connections) = Plan();
        var folder = Path.Combine(KnowledgeLayers.UserRoot, "reset-" + now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var moved = new List<string>();
        var failed = new List<string>();
        foreach (var n in items)
        {
            var from = Path.Combine(KnowledgeLayers.UserRoot, n);
            var to = Path.Combine(folder, n);
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                moved.Add(n);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{n}: {ex.Message}"); }
        }
        if (connections.Count > 0)
            try
            {
                File.Copy(SettingsFile, Path.Combine(folder, "settings.json"), true);
                var root = (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsFile));
                root.Remove("env");
                File.WriteAllText(SettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                moved.Add("settings.json: " + string.Join(", ", connections));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { failed.Add($"settings.json: {ex.Message}"); }
        return (folder, moved, failed);
    }
}
