// Checks of values that reach processes, the HTML report or a confirmation decision. Pure logic (no TwinCAT, no IDE),
// so the unit tests link this file.
//
//   tcpkg arguments    package ids, versions, feed names and options from a fixed set; feed sources as http(s) URL or
//                      folder; no quotes or shell characters (tcpkg is started without cmd.exe, these checks come on top)
//   confirmations      nobody could be asked = refused (running without a question needs TCAGENT_CONFIRM=off)
//   report links       only absolute http(s) links become clickable
//   report command     approval per solution and command text (the command of "report" in .tcagent\tcunit.json)
//
// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class Guards
{
    static readonly Regex Token = new(@"^[A-Za-z0-9._-]+$");
    // feed names of tcpkg may contain blanks ("Beckhoff Stable Feed"): words of [A-Za-z0-9._-] separated by one blank
    static readonly Regex FeedName = new(@"^[A-Za-z0-9._-]+( [A-Za-z0-9._-]+)*$");
    static readonly char[] ShellChars = { '"', '%', '^', '&', '|', '<', '>', '!', '`' };

    /// <summary>A package id, a version, "all": only [A-Za-z0-9._-].</summary>
    public static string TcpkgToken(string value, string what)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0 || v.Length > 200 || !Token.IsMatch(v))
            throw new ArgumentException($"{what}: only letters, digits and . _ - are allowed, not '{value}'");
        return v;
    }

    /// <summary>A package with an optional version: id or id=version.</summary>
    public static string PackageSpec(string spec)
    {
        var parts = (spec ?? "").Trim().Split('=');
        if (parts.Length > 2) throw new ArgumentException($"package: id or id=version expected, not '{spec}'");
        return parts.Length == 1 ? TcpkgToken(parts[0], "package")
                                 : TcpkgToken(parts[0], "package") + "=" + TcpkgToken(parts[1], "package version");
    }

    /// <summary>The name of a feed: words of [A-Za-z0-9._-], separated by single blanks.</summary>
    public static string FeedNameOf(string name, string what = "feed name")
    {
        var v = name?.Trim() ?? "";
        if (v.Length == 0 || v.Length > 100 || !FeedName.IsMatch(v))
            throw new ArgumentException($"{what}: only letters, digits, . _ - and single blanks are allowed, not '{name}'");
        return v;
    }

    /// <summary>The source of a feed: an absolute http(s) URL or a folder (local or UNC), without quotes and shell characters.</summary>
    public static string FeedSource(string source)
    {
        var v = source?.Trim() ?? "";
        if (v.Length == 0 || v.Length > 500 || v.IndexOfAny(ShellChars) >= 0 || v.Any(char.IsControl))
            throw new ArgumentException($"source: an http(s) URL or a folder without the characters \" % ^ & | < > ! ` expected, not '{source}'");
        if (Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return v;
        if (Regex.IsMatch(v, @"^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)")) return v;
        throw new ArgumentException($"source: an http(s) URL or an absolute folder (C:\\... or \\\\server\\share) expected, not '{source}'");
    }

    static readonly HashSet<string> TcpkgVerbs = new(StringComparer.Ordinal)
    {
        "list", "show", "install", "upgrade", "uninstall", "repair", "source", "config", "add", "edit", "remove", "verify"
    };
    static readonly HashSet<string> TcpkgFlags = new(StringComparer.Ordinal)
    {
        "-i", "-o", "-y", "--exact", "--all-versions", "--pre", "--allow-downgrade", "--include-dependencies"
    };
    // options followed by a value, and the check of that value
    static readonly Dictionary<string, Func<string, string>> TcpkgValueOptions = new(StringComparer.Ordinal)
    {
        ["-n"] = v => FeedNameOf(v), ["--new-name"] = v => FeedNameOf(v, "newName"), ["-s"] = FeedSource,
        ["-t"] = v => TcpkgToken(v, "type"),
        ["--priority"] = v => int.TryParse(v, out var n) && n >= 0 && n < 100000 ? v : throw new ArgumentException($"priority: a number expected, not '{v}'"),
        ["--enabled"] = v => v is "true" or "false" ? v : throw new ArgumentException($"enabled: true or false expected, not '{v}'")
    };

    /// <summary>Checks a whole tcpkg command line (verb first). Throws on the first argument that is not allowed.</summary>
    public static void TcpkgArgs(IReadOnlyList<string> args)
    {
        if (args == null || args.Count == 0 || !TcpkgVerbs.Contains(args[0])) throw new ArgumentException("tcpkg: unknown command " + (args?.FirstOrDefault() ?? "(none)"));
        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (TcpkgFlags.Contains(a)) continue;
            if (TcpkgValueOptions.TryGetValue(a, out var check))
            {
                if (i + 1 >= args.Count) throw new ArgumentException($"tcpkg: {a} needs a value");
                check(args[++i]);
                continue;
            }
            if (a.StartsWith("-")) throw new ArgumentException("tcpkg: option not allowed: " + a);
            // positional: sub command (source add|edit|remove|list|verify, config list), package, feed name
            if (TcpkgVerbs.Contains(a)) continue;
            if (args[0] == "source") FeedNameOf(a);
            else PackageSpec(a);
        }
    }

    /// <summary>One argument for a Windows command line (rules of CommandLineToArgvW). Arguments with quotes are refused.</summary>
    public static string QuoteArg(string s)
    {
        if (s.Contains('"') || s.Any(char.IsControl)) throw new ArgumentException("argument with quotes or control characters: " + s);
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t' }) < 0) return s;
        // backslashes at the end would escape the closing quote: double them
        var trailing = s.Length - s.TrimEnd('\\').Length;
        return "\"" + s + new string('\\', trailing) + "\"";
    }

    /// <summary>Decision after a confirmation question: null = go ahead, otherwise the reason why the action is not executed.
    /// ok == null means nobody could be asked - that is refused too.</summary>
    public static string ConfirmRefusal(bool? ok, bool notShown, string what) => ok switch
    {
        true => null,
        false when notShown => "NOT executed: the confirmation could not be shown (the AI client cannot ask in this mode " +
                               "and no TcAgentBridge dialog is reachable in the IDE). Ask the user in the chat; nothing was changed: " + what,
        false => "Rejected by the user: " + what + " - do not retry without asking.",
        null => "NOT executed: this action needs a confirmation, but nobody can be asked (the AI client has no question dialog and no " +
                "TcAgentBridge dialog is reachable in the IDE). Nothing was changed: " + what + " - Ask the user in the chat. " +
                "To run such actions without a question (pipeline, unattended agent) the user sets TCAGENT_CONFIRM=off deliberately."
    };

    /// <summary>The link if it is an absolute http(s) URL, otherwise null (not clickable).</summary>
    public static string SafeHref(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u.AbsoluteUri : null;

    /// <summary>Id of an approved report command: solution path and command text (a changed command needs a new approval).</summary>
    public static string ReportApprovalId(string sln, string command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sln).ToLowerInvariant() + "\n" + (command ?? "").Trim())))[..16].ToLowerInvariant();
}
