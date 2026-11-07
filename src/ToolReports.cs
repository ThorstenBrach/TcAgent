// What the tools tell about the state of a project or a PLC - without IDE or ADS, so it can be tested.
// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace TcAgent;

public static class ToolReports
{
    /// <summary>Whether a login loaded something, from OnlineChangeCnt and AppTimestamp before and after it.</summary>
    public static string LoadReport((uint? onlineChanges, uint? timestamp)? before, (uint? onlineChanges, uint? timestamp)? after)
    {
        var (c0, t0) = before ?? (null, null);
        var (c1, t1) = after ?? (null, null);
        if (c1 == null || t1 == null) return "Loaded: unknown, OnlineChangeCnt and AppTimestamp are not readable.";
        if (c0 == null || t0 == null) return $"Loaded: unknown, there was nothing to compare with (now OnlineChangeCnt {c1}, AppTimestamp {t1}).";
        if (c1 > c0) return $"Loaded: online change (OnlineChangeCnt {c0} -> {c1}).";
        if (t1 != t0 || c1 < c0) return $"Loaded: full download (AppTimestamp {t0} -> {t1}, OnlineChangeCnt {c0} -> {c1}).";
        return $"Loaded: nothing (OnlineChangeCnt {c1} and AppTimestamp unchanged).";
    }

    /// <summary>The .hmiproj entries of a solution file, as full paths.</summary>
    public static IEnumerable<string> HmiProjectsOf(string solutionText, string solutionDir) =>
        Regex.Matches(solutionText, @"""([^""]+\.hmiproj)""", RegexOptions.IgnoreCase)
             .Select(m => Path.GetFullPath(Path.Combine(solutionDir, m.Groups[1].Value)));

    public static bool MentionsTwinCatProject(string solutionText) =>
        solutionText.Contains(".tsproj", StringComparison.OrdinalIgnoreCase) || solutionText.Contains(".tspproj", StringComparison.OrdinalIgnoreCase);
}
