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
//                                         the elevated tcpkg loads.
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

    /// <summary>null = this tcpkg.exe may be started with administrator rights, otherwise the reason why not.
    /// acls: for an exe under %ProgramData%\Beckhoff\TcPkg the entries of <see cref="ChainToCheck"/> (missing = refused).
    /// signatureValid: result of WinVerifyTrust for the exe (signatureInfo describes it).</summary>
    public static string Refusal(string exe, IEnumerable<string> programRoots, string programData,
                                 IReadOnlyList<PathAcl> acls, bool signatureValid, string signatureInfo)
    {
        if (programRoots.Any(r => Under(exe, r))) return null;
        if (string.IsNullOrEmpty(programData) || !Under(exe, ProgramDataTcpkgDir(programData)))
            return $"tcpkg.exe ({exe}) is not in the program folders, C:\\TwinCAT or {(string.IsNullOrEmpty(programData) ? "%ProgramData%\\Beckhoff\\TcPkg" : ProgramDataTcpkgDir(programData))} - it is not started with administrator rights.";
        var needed = ChainToCheck(exe, programData);
        foreach (var path in needed)
        {
            var acl = acls?.FirstOrDefault(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (acl == null) return $"tcpkg.exe ({exe}) is not started with administrator rights: the permissions of {path} could not be read.";
            if (!TrustedOwner(acl.OwnerSid))
                return $"tcpkg.exe ({exe}) is not started with administrator rights: {path} is owned by {acl.OwnerName ?? acl.OwnerSid}, " +
                       "not by Administrators, SYSTEM or TrustedInstaller.";
            var writer = acl.Allow.FirstOrDefault(e => (e.Rights & WriteMask) != 0 && !TrustedWriter(e.Sid));
            if (writer != null)
                return $"tcpkg.exe ({exe}) is not started with administrator rights: {writer.Name ?? writer.Sid} may write to {path} " +
                       $"(rights 0x{writer.Rights:X}) - a normal user could place a file there that the elevated tcpkg loads. " +
                       "Only Administrators, SYSTEM and TrustedInstaller may change that folder and the exe; otherwise run tcpkg yourself in an administrator console.";
        }
        if (!signatureValid)
            return $"tcpkg.exe ({exe}) is not started with administrator rights: it has no valid Authenticode signature ({signatureInfo}).";
        return null;
    }
}
