// Folders the user shares with the agent beyond one chat (⋯ > Shared folders in the TcAgent Chat, TcAgent.exe shared).
//
// %LOCALAPPDATA%\TcAgent\shared-folders.json   the list: a folder for one solution or for all solutions of this user; it
//                                              stays on this PC (paths differ from PC to PC) and is part of the knowledge backup
// %LOCALAPPDATA%\TcAgent\shared-files\<key>\   copies of single files that were kept (a file cannot be shared as a folder)
//
// The chat lets the agent read these folders without asking; tc_docs searches them as well (Knowledge.Inventory), so they
// work with every AI client. TcAgent only reads them. Sharing a folder is refused where it would hand over too much:
// what the agent reads goes to the AI provider, and a whole drive or user profile also holds keys and passwords.
//
// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcAgent;

public static class SharedFolders
{
    public const string ListName = "shared-folders.json", FilesName = "shared-files";
    static string ListFile => Path.Combine(KnowledgeLayers.UserRoot, ListName);
    public static string ProfileOverride;       // tests

    /// <summary>Solution: the solution folder, null = all solutions.</summary>
    public sealed record Entry(string Path, string Solution);

    public static List<Entry> Load()
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(ListFile)) return list;
            foreach (var n in JsonNode.Parse(File.ReadAllText(ListFile))?["folders"] as JsonArray ?? new JsonArray())
                if (n?["path"]?.ToString() is { Length: > 0 } p) list.Add(new Entry(p, n["solution"]?.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return list;
    }

    static void Save(IEnumerable<Entry> list)
    {
        Directory.CreateDirectory(KnowledgeLayers.UserRoot);
        var a = new JsonArray(list.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["solution"] = e.Solution }).ToArray());
        File.WriteAllText(ListFile, new JsonObject { ["folders"] = a }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static List<Entry> Active(string solutionDir) =>
        Load().Where(e => e.Solution == null || (solutionDir != null && Same(e.Solution, solutionDir))).ToList();

    static string Clean(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
    static bool Same(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
    static bool Inside(string path, string folder) => Same(path, folder) || Clean(path).StartsWith(Clean(folder) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a folder must not be shared, for the user; null if it may. profile: the user profile (tests pass their own).</summary>
    public static string Refused(string path, string profile = null)
    {
        string p;
        try { p = Clean(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "This is not a folder path."; }
        if (Path.GetPathRoot(p + "\\")?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true)
            return "A whole drive cannot be shared: the agent could read everything on it, and what it reads goes to your AI provider. Share the folder you need.";
        profile ??= ProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            if (Inside(profile, p))
                return "Your whole user profile cannot be shared: it holds keys, passwords and browser data, and what the agent reads goes to your AI provider. Share a folder inside it, e.g. in Documents.";
            foreach (var secret in new[] { "AppData", ".ssh", ".aws", ".azure", ".gnupg", ".kube" })
                if (Inside(p, Path.Combine(profile, secret)))
                    return $"{secret} cannot be shared: it holds keys, passwords and application data, and what the agent reads goes to your AI provider.";
        }
        foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            if (Environment.GetFolderPath(f) is { Length: > 0 } sys && (Inside(p, sys) || Inside(sys, p)))
                return "System folders cannot be shared: the agent does not need them, and what it reads goes to your AI provider.";
        return null;
    }

    /// <summary>Adds a folder; solutionDir null = all solutions. A folder is listed once: a new choice replaces the old one.</summary>
    public static void Add(string path, string solutionDir)
    {
        if (Refused(path) is { } why) throw new InvalidOperationException(why);
        if (!Directory.Exists(path)) throw new InvalidOperationException("Folder not found: " + path);
        Put(path, solutionDir);
    }

    static void Put(string path, string solutionDir)
    {
        var p = Clean(path);
        var list = Load().Where(e => !Same(e.Path, p)).ToList();
        list.Add(new Entry(p, solutionDir == null ? null : Clean(solutionDir)));
        Save(list);
    }

    public static bool Remove(string path)
    {
        var list = Load();
        var left = list.Where(e => !Same(e.Path, path)).ToList();
        if (left.Count == list.Count) return false;
        Save(left);
        return true;
    }

    /// <summary>Keeps a copy of a single file: in a folder of TcAgent that is shared like any other. Returns the copy.</summary>
    public static string KeepFile(string file, string solutionDir)
    {
        if (!File.Exists(file)) throw new InvalidOperationException("File not found: " + file);
        var key = solutionDir == null ? "all" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Clean(solutionDir).ToLowerInvariant())))[..12].ToLowerInvariant();
        var dir = Path.Combine(KnowledgeLayers.UserRoot, FilesName, key);
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, Path.GetFileName(file));
        File.Copy(file, copy, true);
        if (!Load().Any(e => Same(e.Path, dir))) Put(dir, solutionDir);      // the folder of TcAgent itself, below AppData
        return copy;
    }
}
