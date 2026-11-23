// May this tcpkg.exe run with administrator rights? (tc_packages_change, ThorstenBrach/TcAgent-internal#414)
// Pure decision on paths, ACL entries, owners and the signature result - reading them is in Packages.cs - so the unit
// tests link this file.
//
//   program folders, C:\TwinCAT          as before: only administrators can write there
//   %ProgramData%\Beckhoff\TcPkg\...     TcPkg's own install folder. ProgramData is writable by normal users by default, so
//                                         the exe and every folder from %ProgramData%\Beckhoff down to it must be owned by
//                                         and writable only by Administrators, SYSTEM or TrustedInstaller (CREATOR OWNER
//                                         too, as the owner is one of them), and the exe must carry a valid Authenticode
//                                         signature. A folder a user can create files in would let him plant a DLL that
//                                         the elevated tcpkg loads. Opt-in TCAGENT_TCPKG_ALLOW_USER_WRITABLE (user settings
//                                         or Windows environment only, never a project): tolerates that Users/Authenticated
//                                         Users may create files in the folders (TcPkg's default) for exactly
//                                         %ProgramData%\Beckhoff\TcPkg\tcpkg.exe - with a warning; exe, owners and the
//                                         signature are still checked.
//   anything else                         refused
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public static class TcpkgTrust
{
    /// <summary>An allow entry of an ACL as it applies to the object itself (inherit-only entries left out).</summary>
    public sealed record Ace(string Sid, string Name, int Rights);

    /// <summary>Owner and allow entries of one folder or file.</summary>
    public sealed record PathAcl(string Path, string OwnerSid, string OwnerName, IReadOnlyList<Ace> Allow);

    const string SidSystem = "S-1-5-18", Administrators = "S-1-5-32-544", CreatorOwner = "S-1-3-0",
                 TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    static bool TrustedOwner(string sid) => sid is SidSystem or Administrators or TrustedInstaller;
    static bool TrustedWriter(string sid) => TrustedOwner(sid) || sid == CreatorOwner;

    // FILE_WRITE_DATA/ADD_FILE, FILE_APPEND_DATA/ADD_SUBDIRECTORY, FILE_WRITE_EA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES,
    // DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE
    const int WriteMask = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    /// <summary>The folder below ProgramData that TcPkg installs to.</summary>
    public static string ProgramDataTcpkgDir(string programData) => Path.Combine(programData, "Beckhoff", "TcPkg");

    static bool Under(string path, string root) =>
        !string.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which ACLs must be checked for this exe under ProgramData: %ProgramData%\Beckhoff, each folder below it and the
    /// exe itself. Empty when the exe is not under %ProgramData%\Beckhoff\TcPkg.</summary>
    public static List<string> ChainToCheck(string exe, string programData)
    {
        var list = new List<string>();
        var beckhoff = Path.Combine(programData, "Beckhoff");
        if (!Under(exe, ProgramDataTcpkgDir(programData))) return list;
        for (var d = Path.GetDirectoryName(exe); d != null && (Under(d, beckhoff) || d.Equals(beckhoff, StringComparison.OrdinalIgnoreCase)); d = Path.GetDirectoryName(d))
            list.Insert(0, d);
        list.Add(exe);
        return list;
    }

    public const string AllowSwitch = "TCAGENT_TCPKG_ALLOW_USER_WRITABLE";

    // what the opt-in tolerates on the folders %ProgramData%\Beckhoff and ...\TcPkg (never on the exe): BUILTIN\Users or
    // Authenticated Users may create files and folders (write data/add file, append/add subdirectory, write EA, write
    // attributes) - the default of these folders. Delete, delete child, permissions, owner and generic rights stay refused.
    const string Users = "S-1-5-32-545", AuthenticatedUsers = "S-1-5-11";
    const int CreateOnly = 0x2 | 0x4 | 0x10 | 0x100;

    /// <summary>Decision: Refusal = null means tcpkg.exe may be started elevated; Warning (only then) = it is started although
    /// normal users may create files in its folders (allowed by the opt-in) - to be shown in the question and the result.</summary>
    public sealed record Decision(string Refusal, string Warning);

    /// <summary>Whether this tcpkg.exe may be started with administrator rights.
    /// acls: for an exe under %ProgramData%\Beckhoff\TcPkg the entries of <see cref="ChainToCheck"/> (missing = refused).
    /// signatureValid: result of WinVerifyTrust for the exe (signatureInfo describes it).
    /// allowUserWritable: the opt-in <see cref="AllowSwitch"/> (read with <see cref="AllowUserWritable"/>, never from a project).</summary>
    public static Decision Decide(string exe, IEnumerable<string> programRoots, string programData,
                                  IReadOnlyList<PathAcl> acls, bool signatureValid, string signatureInfo, bool allowUserWritable = false)
    {
        Decision No(string why) => new($"tcpkg.exe ({exe}) is not started with administrator rights: {why}", null);
        if (programRoots.Any(r => Under(exe, r))) return new(null, null);
        if (string.IsNullOrEmpty(programData) || !Under(exe, ProgramDataTcpkgDir(programData)))
            return new($"tcpkg.exe ({exe}) is not in the program folders, C:\\TwinCAT or {(string.IsNullOrEmpty(programData) ? "%ProgramData%\\Beckhoff\\TcPkg" : ProgramDataTcpkgDir(programData))} - it is not started with administrator rights.", null);
        // the opt-in only for exactly %ProgramData%\Beckhoff\TcPkg\tcpkg.exe
        bool exact = exe.Equals(Path.Combine(ProgramDataTcpkgDir(programData), "tcpkg.exe"), StringComparison.OrdinalIgnoreCase);
        var tolerated = new List<string>();
        foreach (var path in ChainToCheck(exe, programData))
        {
            var acl = acls?.FirstOrDefault(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (acl == null) return No($"the permissions of {path} could not be read.");
            if (!TrustedOwner(acl.OwnerSid))
                return No($"{path} is owned by {acl.OwnerName ?? acl.OwnerSid}, not by Administrators, SYSTEM or TrustedInstaller.");
            bool isExe = path.Equals(exe, StringComparison.OrdinalIgnoreCase);
            foreach (var e in acl.Allow.Where(e => (e.Rights & WriteMask) != 0 && !TrustedWriter(e.Sid)))
            {
                bool createOnly = e.Sid is Users or AuthenticatedUsers && (e.Rights & WriteMask & ~CreateOnly) == 0;
                if (!isExe && createOnly && allowUserWritable && exact) { tolerated.Add($"{e.Name ?? e.Sid} on {path}"); continue; }
                var how = !isExe && createOnly && exact
                    ? $" To allow it anyway, switch on \"Start tcpkg although users may write to its folder\" ({AllowSwitch}) in Tools > TcAgent settings > " +
                      $"Gates and confirmations (your own settings, not the project's), or as administrator remove the write right: icacls \"{path}\" /remove:g \"*{e.Sid}\" " +
                      $"and icacls \"{path}\" /grant \"*{e.Sid}:(OI)(CI)RX\". Or run tcpkg yourself in an administrator console."
                    : " Only Administrators, SYSTEM and TrustedInstaller may change it; otherwise run tcpkg yourself in an administrator console.";
                return No($"{e.Name ?? e.Sid} may write to {path} (rights 0x{e.Rights:X}) - a normal user could place a file there that the elevated tcpkg loads." + how);
            }
        }
        if (!signatureValid) return No($"it has no valid Authenticode signature ({signatureInfo}).");
        return new(null, tolerated.Count == 0 ? null
            : $"tcpkg.exe ({exe}) is started with administrator rights although normal users may create files in its folders ({string.Join("; ", tolerated)}) - allowed by {AllowSwitch}.");
    }

    /// <summary>The refusal alone (null = allowed) - without the opt-in.</summary>
    public static string Refusal(string exe, IEnumerable<string> programRoots, string programData,
                                 IReadOnlyList<PathAcl> acls, bool signatureValid, string signatureInfo) =>
        Decide(exe, programRoots, programData, acls, signatureValid, signatureInfo).Refusal;

    /// <summary>The opt-in, read only from places a project cannot set: the user's own settings.json ("env") and the Windows
    /// environment variables of the user or the machine (registry). Not from the environment of this process - an MCP
    /// configuration that comes with a project (e.g. a .mcp.json in the repository) could set that - and never from
    /// &lt;solution&gt;\.tcagent. "1" = on.</summary>
    public static bool AllowUserWritable(string userSettingsFile, string windowsUserValue, string windowsMachineValue)
    {
        static bool On(string v) => (v ?? "").Trim() == "1";
        // as everywhere in TcAgent the environment wins over settings.json (the user's variable over the machine's)
        if (!string.IsNullOrWhiteSpace(windowsUserValue)) return On(windowsUserValue);
        if (!string.IsNullOrWhiteSpace(windowsMachineValue)) return On(windowsMachineValue);
        try
        {
            if (string.IsNullOrEmpty(userSettingsFile) || !File.Exists(userSettingsFile)) return false;
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(userSettingsFile))?["env"]?[AllowSwitch] is System.Text.Json.Nodes.JsonValue v && On(v.ToString());
        }
        catch { return false; }
    }
}
