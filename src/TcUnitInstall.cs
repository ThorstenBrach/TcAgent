// TcUnit shipped with TcAgent (thirdparty\TcUnit\<version>\TcUnit.library next to TcAgent.exe, MIT license, see
// thirdparty\TcUnit\LICENSE) and installed into the TwinCAT library repository only when it is needed:
//
//   tc_library action=ensure_tcunit         installs if TcUnit is missing or older than the shipped version,
//                                           then adds the TcUnit reference to the PLC project if it has none
//   TcAgent.exe tcunit ensure --solution    the same for pipelines; the reference is only reported (nothing is saved)
//
// Installed versions are read through the library manager (ScanLibraries = all repositories TwinCAT uses), the install
// goes through ITcPlcLibraryManager.InstallLibrary into the repository "System". A newer TcUnit that is already installed
// is never touched.
//
// The decisions (versions, reference) are here and unit-tested; the calls into TwinCAT are in TcUnitInstallCom.cs.
//
// SPDX-License-Identifier: MIT

namespace TcAgent;

public static partial class TcUnitInstall
{
    public const string LibraryName = "TcUnit";
    public const string Vendor = "www.tcunit.org";

    /// <summary>Folder with the shipped TcUnit versions; tests point it elsewhere.</summary>
    public static string BundleRootOverride;
    public static string BundleRoot => BundleRootOverride ?? Path.Combine(AppContext.BaseDirectory, "thirdparty", "TcUnit");

    /// <summary>The highest shipped version: &lt;root&gt;\&lt;version&gt;\*.library, or null.</summary>
    public static (Version Version, string File)? Bundled(string root = null)
    {
        root ??= BundleRoot;
        if (!Directory.Exists(root)) return null;
        (Version Version, string File)? best = null;
        foreach (var d in Directory.GetDirectories(root))
        {
            var v = ParseVersion(Path.GetFileName(d));
            if (v == null) continue;
            var f = Directory.GetFiles(d, "*.library").FirstOrDefault();
            if (f == null) continue;
            if (best == null || v > best.Value.Version) best = (v, f);
        }
        return best;
    }

    /// <summary>"1.3.2", "1.3.2.0", "v1.3" -> version with four parts (missing parts 0), so 1.3.2 equals 1.3.2.0; else null.</summary>
    public static Version ParseVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var n = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        return new Version(n[0], n[1], n[2], n[3]);
    }

    /// <summary>Install only when no version is installed or the newest installed one is older than the shipped one.</summary>
    public static bool NeedsInstall(IEnumerable<string> installedVersions, Version bundled)
    {
        var newest = installedVersions.Select(ParseVersion).Where(v => v != null).DefaultIfEmpty().Max();
        return newest == null || newest < bundled;
    }

    /// <summary>True when one of the reference descriptions ("TcUnit, 1.3.2 (www.tcunit.org)" or
    /// "Placeholder TcUnit -> ...") already names TcUnit.</summary>
    public static bool IsReferenced(IEnumerable<string> references) =>
        references.Any(r => r.StartsWith(LibraryName + ",", StringComparison.OrdinalIgnoreCase) ||
                            r.StartsWith($"Placeholder {LibraryName} ", StringComparison.OrdinalIgnoreCase) ||
                            r.Contains($"-> {LibraryName},", StringComparison.OrdinalIgnoreCase));
}
