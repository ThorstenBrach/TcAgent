// Test builds from a folder (TCAGENT_UPDATE_FOLDER) - for development and in-house testing: build-vsix.ps1 writes
// TcAgent-<version>.vsix and TcAgent-XaeShell-<version>.zip into bin\, often several times with the same version number.
//
// A package in the folder is offered when its version is newer than the installed one, or when it has the same version but
// another build: its TcAgentBridge.dll (in the VSIX at the root, in the ZIP under bridge-vs15 / bridge-vs17) differs from the
// one that runs in this IDE (SHA-256). Installing stays a click of the user (TcAgent Chat); "skip" remembers the build.
//
// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class LocalBuilds
{
    public const string EnvKey = "TCAGENT_UPDATE_FOLDER", SkippedKey = "localSkipped";

    public sealed record Build(string Kind, string Version, string File, DateTime Built, string BridgeSha);

    /// <summary>The kinds: "vsix" (Visual Studio 2022), "xaeshell-vs15" (32-bit XAE Shell), "xaeshell-vs17" (64-bit).</summary>
    static (Regex Name, string Entry) Pattern(string kind) => kind switch
    {
        "vsix" => (new Regex(@"^TcAgent-(\d+(?:\.\d+){1,3})\.vsix$", RegexOptions.IgnoreCase), "TcAgentBridge.dll"),
        "xaeshell-vs15" => (new Regex(@"^TcAgent-XaeShell-(\d+(?:\.\d+){1,3})\.zip$", RegexOptions.IgnoreCase), "bridge-vs15/TcAgentBridge.dll"),
        "xaeshell-vs17" => (new Regex(@"^TcAgent-XaeShell-(\d+(?:\.\d+){1,3})\.zip$", RegexOptions.IgnoreCase), "bridge-vs17/TcAgentBridge.dll"),
        _ => throw new ArgumentException("--kind vsix, xaeshell-vs15 or xaeshell-vs17")
    };

    public static string Sha(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    public static string FileSha(string file)
    {
        try { using var s = File.OpenRead(file); return Sha(s); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The newest package of the kind in the folder (by version, then by time), with the hash of its bridge.</summary>
    public static Build Find(string folder, string kind)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var (name, entry) = Pattern(kind);
        var candidates = Directory.GetFiles(folder)
            .Select(f => (File: f, M: name.Match(Path.GetFileName(f))))
            .Where(x => x.M.Success && Updates.ParseVersion(x.M.Groups[1].Value) != null)
            .OrderByDescending(x => Updates.ParseVersion(x.M.Groups[1].Value)).ThenByDescending(x => File.GetLastWriteTimeUtc(x.File));
        foreach (var (file, m) in candidates)
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var e = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').Equals(entry, StringComparison.OrdinalIgnoreCase));
                if (e == null) continue;            // e.g. a ZIP without the bridge for this shell
                using var s = e.Open();
                return new Build(kind, Updates.ParseVersion(m.Groups[1].Value)!.ToString(), file, File.GetLastWriteTime(file), Sha(s));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Is there a build to offer? runningDll: the TcAgentBridge.dll this IDE runs; current: its version.</summary>
    public static JsonObject Check(string folder, string kind, string runningDll, string current)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? Environment.GetEnvironmentVariable(EnvKey) : folder;
        folder = string.IsNullOrWhiteSpace(folder) ? null : Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
        var o = new JsonObject { ["folder"] = folder, ["kind"] = kind, ["current"] = current, ["available"] = false, ["notify"] = false };
        if (folder == null) { o["error"] = $"no folder for test builds ({EnvKey})"; return o; }
        if (!Directory.Exists(folder)) { o["error"] = "folder not found: " + folder; return o; }
        var b = Find(folder, kind);
        if (b == null) return o;
        var cur = Updates.ParseVersion(current) ?? new Version(0, 0);
        var ver = Updates.ParseVersion(b.Version)!;
        var running = runningDll != null && File.Exists(runningDll) ? FileSha(runningDll) : null;
        var newer = ver > cur;
        var rebuilt = ver == cur && running != null && running != b.BridgeSha;
        var skipped = Updates.LoadState()[SkippedKey]?.ToString();
        o["version"] = b.Version; o["file"] = b.File; o["built"] = b.Built.ToString("yyyy-MM-ddTHH:mm:ss"); o["sha"] = b.BridgeSha;
        o["newer"] = newer; o["rebuilt"] = rebuilt;
        o["available"] = newer || rebuilt;
        o["notify"] = (newer || rebuilt) && skipped != b.BridgeSha;
        return o;
    }

    public static void Skip(string sha)
    {
        var st = Updates.LoadState();
        st[SkippedKey] = sha;
        Updates.SaveState(st);
    }
}
