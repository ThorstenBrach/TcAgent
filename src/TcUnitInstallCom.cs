// TcUnit shipped with TcAgent: the calls into TwinCAT (library manager) - see TcUnitInstall.cs for what and when.
//
// SPDX-License-Identifier: MIT
using TCatSysManagerLib;

namespace TcAgent;

public static partial class TcUnitInstall
{
    /// <summary>Installs TcUnit if needed and (addReference) adds the reference to the PLC project. Call on the STA thread.</summary>
    public static string Ensure(ITcSmTreeItem plc, bool addReference, Func<object, string> describe, string libraryFile = null)
    {
        var lm = TwinCatSession.Children(plc).FirstOrDefault(c => c is ITcPlcLibraryManager)
                 ?? throw new Exception($"PLC project {plc.Name} has no library manager (References)");
        var o = new System.Text.StringBuilder();

        (Version Version, string File) bundle;
        if (libraryFile != null)
        {
            var f = Path.GetFullPath(libraryFile);
            if (!File.Exists(f)) throw new Exception("library file not found: " + f);
            bundle = (ParseVersion(Path.GetFileName(Path.GetDirectoryName(f))) ?? new Version(0, 0, 0, 0), f);
        }
        else bundle = Bundled() ?? throw new Exception($"no shipped TcUnit found under {BundleRoot} - install TcUnit with tc_library action=install path=<TcUnit.library>");

        List<(string Version, string Vendor)> Installed()
        {
            var list = new List<(string, string)>();
            foreach (object r in (System.Collections.IEnumerable)ComCall.Invoke(lm, "ScanLibraries"))
            {
                string n = Prop(r, "Name");
                if (!string.Equals(n, LibraryName, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((Prop(r, "Version"), Prop(r, "Distributor")));
            }
            return list;
        }

        var installed = Installed();
        var shown = installed.Count == 0 ? "none" : string.Join(", ", installed.Select(i => i.Version));
        // a library file of unknown version (libraryFile outside a version folder) is installed only when TcUnit is missing
        var install = bundle.Version == new Version(0, 0, 0, 0) ? installed.Count == 0 : NeedsInstall(installed.Select(i => i.Version), bundle.Version);
        if (install)
        {
            ComCall.Invoke(lm, "InstallLibrary", "System", bundle.File, false);
            installed = Installed();
            if (installed.Count == 0) throw new Exception($"InstallLibrary reported no error, but TcUnit is still not in the library repositories ({bundle.File})");
            o.AppendLine($"TcUnit installed: {bundle.Version.ToString(3)} from {bundle.File} (installed before: {shown})");
        }
        else o.AppendLine($"TcUnit already installed: {shown} (shipped: {bundle.Version.ToString(3)}) - nothing installed");

        var refs = new List<string>();
        foreach (object r in (System.Collections.IEnumerable)ComCall.Get(lm, "References")) refs.Add(describe(r));
        if (IsReferenced(refs))
            o.AppendLine($"Reference in {plc.Name}: {refs.First(r => IsReferenced(new[] { r }))}");
        else if (addReference)
        {
            var vendor = installed.Select(i => i.Vendor).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? Vendor;
            ComCall.Invoke(lm, "AddLibrary", LibraryName, "*", vendor);
            o.AppendLine($"Reference added to {plc.Name}: {LibraryName}, * ({vendor}) - save the project (tc_save) to keep it");
        }
        else o.AppendLine($"No TcUnit reference in {plc.Name} - add it in the project (tc_library action=ensure_tcunit or action=add name=TcUnit)");
        return o.ToString().TrimEnd();
    }

    static string Prop(object o, string p)
    {
        try { return Convert.ToString(ComCall.Get(o, p), System.Globalization.CultureInfo.InvariantCulture); } catch { }
        try { return Convert.ToString(o.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, o, null), System.Globalization.CultureInfo.InvariantCulture); } catch { }
        return null;
    }
}
