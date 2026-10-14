// Library inventory of PLC projects for build pipelines - the library references with requested and resolved version,
// vendor and placeholder, read through the library manager (ITcPlcLibraryManager.References, like tc_plc_references):
//
//   tc_sbom / TcAgent.exe sbom          CycloneDX 1.6 SBOM (JSON) per PLC project, default <solution>\sbom\<PLC>.cdx.json:
//                                       the PLC project as application (version from the project information, git commit
//                                       and branch), one component per library reference (purl pkg:generic/<vendor>/<name>@<version>,
//                                       SHA-256 of the library file when it is found in a library repository), dependencies.
//   tc_library_lock / TcAgent.exe lock  <solution>\.tcagent\libraries.lock.json - the resolved library versions per PLC project,
//                                       sorted for clean Git diffs; check compares the current resolution with it (pipeline gate).
//
// Missing data is marked ("unknown", "not found", "unresolved", "not available"), never silently left out.
// '*' references (newest installed) are resolved like TwinCAT does: the newest version in the library repositories.
//
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TCatSysManagerLib;

namespace TcAgent;

public static class LibraryInventory
{
    /// <summary>One library reference of a PLC project. Placeholder = placeholder name (null for a direct reference);
    /// Requested = version asked for ('*' = newest installed, a fixed version, or "unknown"); Version = resolved version
    /// (null = could not be resolved); ResolvedFrom = how Version was found.</summary>
    public sealed record LibRef(string Plc, string Name, string Vendor, string Placeholder, string Requested, string Version, string ResolvedFrom);

    /// <summary>A PLC project with its references. Version/Title/Company from the project information (null = not found);
    /// Repositories = folders of the library repositories the IDE uses.</summary>
    public sealed record PlcInfo(string Name, string PathName, string Version, string VersionSource, string Title, string Company,
                                 List<LibRef> Refs, List<string> Repositories);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ------------------------------------------------------------------ reading (STA)
    /// <summary>Library references of the PLC projects (type 600 items). Must run on the STA (tc.Sta.Run).</summary>
    public static List<PlcInfo> Read(TwinCatSession tc, IEnumerable<ITcSmTreeItem> plcs)
    {
        string slnDir = null;
        try { slnDir = Path.GetDirectoryName((string)tc.RequireDte().Solution.FullName); } catch { }
        List<(string Name, string Version, string Vendor)> installed = null;    // ScanLibraries, read once when needed
        var list = new List<PlcInfo>();
        foreach (var plc in plcs)
        {
            var refs = new List<LibRef>();
            var repos = new List<string>();
            var lm = TwinCatSession.Children(plc).FirstOrDefault(c => c is ITcPlcLibraryManager);
            if (lm != null)
            {
                try
                {
                    foreach (var r in (System.Collections.IEnumerable)ComCall.Get(lm, "Repositories"))
                        if (ComProp(r, "Folder") is { Length: > 0 } f) repos.Add(f);
                }
                catch { }
                System.Collections.IEnumerable libRefs;
                try { libRefs = (System.Collections.IEnumerable)ComCall.Get(lm, "References"); }
                catch { libRefs = Array.Empty<object>(); }       // older TwinCAT without References: report the PLC without libraries
                foreach (object r in libRefs)
                {
                    var x = Describe(plc.Name, r);
                    if (x.Version == null && x.Name != null)
                    {
                        installed ??= Installed(lm);
                        var best = Newest(installed, x.Name, x.Vendor);
                        if (best != null)
                            x = x with { Version = best.Value.Version, Vendor = x.Vendor ?? best.Value.Vendor, ResolvedFrom = "newest installed in the library repositories" };
                    }
                    refs.Add(x);
                }
            }
            var (ver, src, title, company) = ProjectInfo(plc, slnDir);
            list.Add(new PlcInfo(plc.Name, plc.PathName, ver, src, title, company, refs, repos));
        }
        return list;
    }

    static LibRef Describe(string plc, object r)
    {
        dynamic d = r;
        var ph = T(() => d.PlaceholderName);
        object lib = r;
        if (ph != null)
        {
            object eff = null, def = null;
            try { eff = d.EffectiveResolution; } catch { }
            try { def = d.DefaultResolution; } catch { }
            lib = eff ?? def;
        }
        string name = null, version = null, vendor = null;
        if (lib != null)
        {
            dynamic l = lib;
            name = T(() => l.Name);
            version = T(() => l.Version);
            vendor = T(() => l.Distributor);
        }
        name ??= ph;
        var resolved = version != null && version != "*" ? version : null;
        return new LibRef(plc, name, vendor, ph, version ?? "unknown", resolved,
                          resolved == null ? null : ph != null ? "placeholder resolution" : "fixed version in the reference");
    }

    static List<(string Name, string Version, string Vendor)> Installed(object lm)
    {
        var list = new List<(string Name, string Version, string Vendor)>();
        try
        {
            foreach (object r in (System.Collections.IEnumerable)((dynamic)lm).ScanLibraries())
            {
                dynamic d = r;
                list.Add((T(() => d.Name), T(() => d.Version), T(() => d.Distributor)));
            }
        }
        catch { }
        return list;
    }

    static (string Name, string Version, string Vendor)? Newest(List<(string Name, string Version, string Vendor)> installed, string name, string vendor)
    {
        var c = installed.Where(i => i.Version != null && i.Version != "*" && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                     (vendor == null || string.Equals(i.Vendor, vendor, StringComparison.OrdinalIgnoreCase))).ToList();
        if (c.Count == 0) return null;
        return c.OrderByDescending(i => System.Version.TryParse(i.Version, out var v) ? v : new System.Version(0, 0))
                .ThenByDescending(i => i.Version, StringComparer.Ordinal).First();
    }

    /// <summary>Version, title and company from the project information: the .plcproj file (ProjectVersion, Title, Company),
    /// otherwise ProjectVersion in the XML description of the PLC project. Best effort - nulls when not found.</summary>
    static (string Version, string Source, string Title, string Company) ProjectInfo(ITcSmTreeItem plc, string slnDir)
    {
        string version = null, source = null, title = null, company = null;
        var outer = plc.PathName.Split('^').Skip(1).FirstOrDefault();
        var plcName = plc.Name;
        var file = FindPlcProj(slnDir, outer, plcName,
                               plcName.EndsWith(" Project", StringComparison.OrdinalIgnoreCase) ? plcName[..^" Project".Length] : null);
        if (file != null)
        {
            try
            {
                var x = XDocument.Load(file);
                string El(string n) => x.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim() is { Length: > 0 } v ? v : null;
                version = El("ProjectVersion");
                title = El("Title");
                company = El("Company");
                if (version != null) source = "project information (" + Path.GetFileName(file) + ")";
            }
            catch { }
        }
        if (version == null)
            try
            {
                var x = XDocument.Parse((string)((dynamic)plc).ProduceXml(false));
                var v = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProjectVersion")?.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) { version = v; source = "XML description of the PLC project"; }
            }
            catch { }
        return (version, source, title, company);
    }

    static string FindPlcProj(string slnDir, params string[] names)
    {
        if (string.IsNullOrEmpty(slnDir) || !Directory.Exists(slnDir)) return null;
        string[] files;
        try { files = Directory.GetFiles(slnDir, "*.plcproj", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
        catch { return null; }
        foreach (var n in names.Where(x => !string.IsNullOrEmpty(x)))
        {
            var hit = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return files.Length == 1 ? files[0] : null;
    }

    static string T(Func<object> f)
    {
        try { var s = f()?.ToString(); return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); } catch { return null; }
    }

    static string ComProp(object o, string p)
    {
        try { return Convert.ToString(ComCall.Get(o, p), CultureInfo.InvariantCulture); } catch { }
        try { return Convert.ToString(o.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, o, null), CultureInfo.InvariantCulture); } catch { }
        return null;
    }

    static IEnumerable<LibRef> Sorted(IEnumerable<LibRef> refs) =>
        refs.OrderBy(r => r.Placeholder ?? r.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Vendor ?? "", StringComparer.Ordinal);

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    static string Show(LibRef r) =>
        (r.Placeholder != null && !string.Equals(r.Placeholder, r.Name, StringComparison.OrdinalIgnoreCase) ? $"placeholder {r.Placeholder} -> " :
         r.Placeholder != null ? "placeholder " : "") +
        $"{r.Name ?? "?"}, {r.Version ?? "unresolved"} ({r.Vendor ?? "unknown vendor"}), requested {r.Requested ?? "unknown"}";

    // ------------------------------------------------------------------ SBOM (CycloneDX 1.6)
    /// <summary>Output file of the SBOM of one PLC project: default &lt;solution&gt;\sbom\&lt;PLC&gt;.cdx.json; with several PLC
    /// projects and an explicit file the PLC name is inserted (x.cdx.json -> x.&lt;PLC&gt;.cdx.json).</summary>
    public static string SbomFile(string slnDir, string outFile, string plc, int count)
    {
        var safe = string.Concat(plc.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string f;
        if (string.IsNullOrEmpty(outFile)) f = Path.Combine(slnDir ?? ".", "sbom", safe + ".cdx.json");
        else if (count <= 1) f = outFile;
        else
        {
            var name = Path.GetFileName(outFile);
            var ext = name.EndsWith(".cdx.json", StringComparison.OrdinalIgnoreCase) ? name[^".cdx.json".Length..] : Path.GetExtension(name);
            f = Path.Combine(Path.GetDirectoryName(outFile) ?? "", name[..^ext.Length] + "." + safe + ext);
        }
        return Path.GetFullPath(f);
    }

    /// <summary>Writes the SBOM of one PLC project and returns a short summary (file, counts, what is missing).
    /// Runs off the STA: hashing and git only touch files and processes.</summary>
    public static string WriteSbom(PlcInfo p, string slnDir, string file)
    {
        var roots = RepositoryRoots(p.Repositories);
        var commit = Git(slnDir, "rev-parse", "HEAD");
        var branch = Git(slnDir, "rev-parse", "--abbrev-ref", "HEAD");
        static JsonObject Pr(string key, string value) => new JsonObject { ["name"] = key, ["value"] = value };

        // the PLC project itself
        var appRef = "plc:" + p.Name;
        var app = new JsonObject { ["type"] = "application", ["bom-ref"] = appRef, ["name"] = p.Name };
        if (p.Version != null) app["version"] = p.Version;
        if (p.Company != null) app["supplier"] = new JsonObject { ["name"] = p.Company };
        var appProps = new JsonArray();
        appProps.Add(Pr("tcagent:plcPath", p.PathName));
        appProps.Add(p.Version != null ? Pr("tcagent:versionSource", p.VersionSource ?? "unknown") : Pr("tcagent:version", "not found"));
        if (p.Title != null) appProps.Add(Pr("tcagent:title", p.Title));
        appProps.Add(Pr("tcagent:git.commit", commit ?? "not available"));
        appProps.Add(Pr("tcagent:git.branch", branch ?? "not available"));
        app["properties"] = appProps;

        // one component per library reference
        var comps = new JsonArray();
        var deps = new JsonArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int hashed = 0;
        var unresolved = new List<string>();
        foreach (var r in Sorted(p.Refs))
        {
            var name = r.Name ?? r.Placeholder ?? "unknown";
            var purl = Purl(r.Vendor, name, r.Version);
            var bomRef = purl;
            for (int i = 2; !used.Add(bomRef); i++) bomRef = purl + "#" + i;
            var c = new JsonObject { ["type"] = "library", ["bom-ref"] = bomRef };
            if (r.Vendor != null)
            {
                c["supplier"] = new JsonObject { ["name"] = r.Vendor };
                c["publisher"] = r.Vendor;
            }
            c["name"] = name;
            if (r.Version != null) c["version"] = r.Version;
            var props = new JsonArray();
            string libFile = null;
            try { libFile = FindLibraryFile(roots, r.Vendor, name, r.Version); } catch { }
            string hash = null;
            if (libFile != null) try { hash = Sha256(libFile); } catch { }
            if (hash != null)
            {
                c["hashes"] = new JsonArray(new JsonObject { ["alg"] = "SHA-256", ["content"] = hash });
                props.Add(Pr("tcagent:libraryFile", Path.GetFileName(libFile)));
                hashed++;
            }
            else props.Add(Pr("tcagent:hash", "not found"));
            c["purl"] = purl;
            if (r.Placeholder != null) props.Add(Pr("tcagent:placeholder", r.Placeholder));
            props.Add(Pr("tcagent:requestedVersion", r.Requested ?? "unknown"));
            if (r.Version != null) props.Add(Pr("tcagent:resolvedFrom", r.ResolvedFrom ?? "unknown"));
            else { props.Add(Pr("tcagent:resolvedVersion", "unresolved")); unresolved.Add(Show(r)); }
            if (r.Vendor == null) props.Add(Pr("tcagent:vendor", "unknown"));
            props.Add(Pr("tcagent:license", "unknown"));
            c["properties"] = props;
            comps.Add(c);
            deps.Add(JsonValue.Create(bomRef));
        }

        var bom = new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.6",
            ["serialNumber"] = "urn:uuid:" + Guid.NewGuid().ToString("D"),
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = Now(),
                ["tools"] = new JsonObject
                {
                    ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = "TcAgent", ["version"] = Program.ServerVersion })
                },
                ["component"] = app
            },
            ["components"] = comps,
            ["dependencies"] = new JsonArray(new JsonObject { ["ref"] = appRef, ["dependsOn"] = deps })
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, bom.ToJsonString(Json) + Environment.NewLine, new UTF8Encoding(false));

        var n = p.Refs.Count;
        var s = new StringBuilder();
        s.Append($"SBOM written: {file} - {p.Name} {p.Version ?? "(version not found)"}, {n} librar{(n == 1 ? "y" : "ies")}, " +
                 $"{hashed} with SHA-256, {n - hashed} without hash (library file not found), {unresolved.Count} unresolved, git " +
                 (commit != null ? commit[..Math.Min(12, commit.Length)] + (branch != null ? $" ({branch})" : "") : "not available"));
        foreach (var u in unresolved) s.Append("\n  unresolved: " + u);
        return s.ToString();
    }

    static string Purl(string vendor, string name, string version) =>
        "pkg:generic/" + (vendor != null ? Uri.EscapeDataString(vendor) + "/" : "") + Uri.EscapeDataString(name) +
        (version != null ? "@" + Uri.EscapeDataString(version) : "");

    /// <summary>Folders of the library repositories: those the IDE reports, then the default places of TwinCAT 4024/4026.</summary>
    static List<string> RepositoryRoots(IEnumerable<string> fromIde)
    {
        var tcDir = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        return (fromIde ?? Enumerable.Empty<string>()).Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Beckhoff", "TwinCAT", "3.1", "Components", "Plc", "Managed Libraries"),
                @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Plc\Managed Libraries",
                string.IsNullOrEmpty(tcDir) ? null : Path.Combine(tcDir, "Components", "Plc", "Managed Libraries"),
                @"C:\TwinCAT\3.1\Components\Plc\Managed Libraries"
            })
            .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Library file in a repository: &lt;root&gt;\&lt;vendor&gt;\&lt;name&gt;\&lt;version&gt;\*.library or *.compiled-library*.
    /// Best effort - null when not found.</summary>
    static string FindLibraryFile(List<string> roots, string vendor, string name, string version)
    {
        if (name == null || version == null) return null;
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var own = vendor != null ? Path.Combine(root, vendor) : null;
                var vendorDirs = own != null && Directory.Exists(own) ? new[] { own } : Directory.GetDirectories(root);
                foreach (var vd in vendorDirs)
                {
                    var d = Path.Combine(vd, name, version);
                    if (!Directory.Exists(d)) continue;
                    var f = Directory.GetFiles(d)
                        .Where(x => x.EndsWith(".library", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetExtension(x).StartsWith(".compiled-library", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (f != null) return f;
                }
            }
            catch { }
        }
        return null;
    }

    static string Sha256(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Output of a git command in the folder (5 s timeout), null on any failure.</summary>
    static string Git(string dir, params string[] args)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
            if (p.ExitCode != 0 || !stdout.Wait(1000)) return null;
            var s = stdout.Result.Trim();
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ lock file
    public static string LockFile(string slnDir) => Path.Combine(slnDir ?? ".", ".tcagent", "libraries.lock.json");

    static JsonObject LockEntry(PlcInfo p)
    {
        var refs = new JsonArray();
        foreach (var r in Sorted(p.Refs))
            refs.Add(new JsonObject
            {
                ["name"] = r.Name, ["vendor"] = r.Vendor, ["placeholder"] = r.Placeholder, ["requested"] = r.Requested, ["resolved"] = r.Version
            });
        return new JsonObject { ["name"] = p.Name, ["references"] = refs };
    }

    static string S(JsonObject o, string k) => o[k] is JsonValue v ? v.ToString() : null;

    /// <summary>Writes the lock file. partial (--plc / plcProjectPath given): only the selected PLC projects are replaced, the
    /// others stay. When the references did not change the file is left untouched (no timestamp-only diff in Git).</summary>
    public static string WriteLock(string slnDir, List<PlcInfo> plcs, bool partial)
    {
        var file = LockFile(slnDir);
        JsonObject old = null;
        try { if (File.Exists(file)) old = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch { }
        var entries = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (partial && old?["plcProjects"] is JsonArray oa)
            foreach (var e in oa.OfType<JsonObject>())
                if (S(e, "name") is { } n) entries[n] = (JsonObject)e.DeepClone();
        foreach (var p in plcs) entries[p.Name] = LockEntry(p);
        var arr = new JsonArray();
        foreach (var kv in entries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) arr.Add(kv.Value);
        int refCount = plcs.Sum(p => p.Refs.Count), unresolved = plcs.Sum(p => p.Refs.Count(r => r.Version == null));
        var counts = $"{plcs.Count} PLC project(s), {refCount} reference(s)" + (unresolved > 0 ? $", {unresolved} unresolved" : "");
        if (old?["plcProjects"] is JsonNode op && op.ToJsonString() == arr.ToJsonString())
            return $"Lock file unchanged: {file} ({counts} - same resolution as locked)";
        var doc = new JsonObject { ["tcagent"] = Program.ServerVersion, ["generated"] = Now(), ["plcProjects"] = arr };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, doc.ToJsonString(Json) + Environment.NewLine, new UTF8Encoding(false));
        var s = new StringBuilder($"Lock file written: {file} ({counts}) - commit it to Git.");
        foreach (var p in plcs)
            foreach (var r in Sorted(p.Refs).Where(x => x.Version == null))
                s.Append($"\n  unresolved: {p.Name}: {Show(r)}");
        return s.ToString();
    }

    /// <summary>Compares the current resolution with the lock file. Code 0 = equal, 1 = differences, 2 = no (readable) lock file.</summary>
    public static (int Code, string Text) CheckLock(string slnDir, List<PlcInfo> plcs, bool partial)
    {
        var file = LockFile(slnDir);
        if (!File.Exists(file))
            return (2, $"No lock file: {file} - create it with TcAgent.exe lock --solution <sln> (or tc_library_lock action=write) and commit it.");
        JsonObject old;
        try { old = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception("not a JSON object"); }
        catch (Exception ex) { return (2, $"Lock file cannot be read: {file} - {ex.Message}"); }

        var locked = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in (old["plcProjects"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (S(e, "name") is { } n && !locked.ContainsKey(n)) locked[n] = e;

        var diffs = new List<string>();
        foreach (var p in plcs.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!locked.TryGetValue(p.Name, out var le))
            {
                diffs.Add($"+ PLC project {p.Name}: not in the lock file ({p.Refs.Count} reference(s))");
                continue;
            }
            var lockedRefs = (le["references"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(e => new LibRef(p.Name, S(e, "name"), S(e, "vendor"), S(e, "placeholder"), S(e, "requested"), S(e, "resolved"), null)).ToList();
            CompareRefs(p.Name, lockedRefs, p.Refs, diffs);
        }
        if (!partial)
            foreach (var k in locked.Keys.Where(lk => !plcs.Any(p => p.Name.Equals(lk, StringComparison.OrdinalIgnoreCase))).OrderBy(lk => lk, StringComparer.OrdinalIgnoreCase))
                diffs.Add($"- PLC project {k}: in the lock file, not in the solution");

        var what = string.Join(", ", plcs.Select(p => p.Name));
        if (diffs.Count == 0)
            return (0, $"Library lock OK: the resolved libraries of {what} match {file} ({plcs.Sum(p => p.Refs.Count)} reference(s)).");
        return (1, $"Library lock FAILED: {diffs.Count} difference(s) between the current resolution and {file}:\n  " + string.Join("\n  ", diffs) +
                   "\n(+ added, - removed, ~ changed) If the change is intended: TcAgent.exe lock --solution <sln> (or tc_library_lock action=write) and commit the lock file.");
    }

    static void CompareRefs(string plc, List<LibRef> locked, List<LibRef> now, List<string> diffs)
    {
        static string K(LibRef r) => (r.Placeholder ?? r.Name ?? "").ToUpperInvariant();
        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var lk = locked.GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        var nk = now.GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        foreach (var k in nk.Keys.Union(lk.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            lk.TryGetValue(k, out var l);
            nk.TryGetValue(k, out var n);
            if (l == null) diffs.Add($"+ {plc}: {Show(n)} - added");
            else if (n == null) diffs.Add($"- {plc}: {Show(l)} - removed");
            else
            {
                var ch = new List<string>();
                if (!Eq(l.Name, n.Name)) ch.Add($"library {l.Name ?? "?"} -> {n.Name ?? "?"}");
                if (!Eq(l.Vendor, n.Vendor)) ch.Add($"vendor {l.Vendor ?? "unknown"} -> {n.Vendor ?? "unknown"}");
                if (!Eq(l.Requested, n.Requested)) ch.Add($"requested {l.Requested ?? "unknown"} -> {n.Requested ?? "unknown"}");
                if (!Eq(l.Version, n.Version)) ch.Add($"version {l.Version ?? "unresolved"} -> {n.Version ?? "unresolved"}");
                if (ch.Count > 0) diffs.Add($"~ {plc}: {(n.Placeholder != null ? "placeholder " + n.Placeholder : n.Name)}: " + string.Join(", ", ch));
            }
        }
    }
}

public sealed partial class ToolRegistry
{
    void RegisterLibraryInventory()
    {
        Add("tc_sbom", "Software bill of materials (SBOM) of the PLC project(s) as CycloneDX 1.6 JSON - for security/compliance " +
                       "(e.g. Cyber Resilience Act) and as pipeline artifact. The PLC project is the application (version from the project " +
                       "information, git commit/branch of the solution folder), every library reference a component with resolved version, " +
                       "vendor, placeholder, requested version, purl pkg:generic/<vendor>/<name>@<version> and the SHA-256 of the library " +
                       "file when it is found in a library repository. Missing data is marked (e.g. tcagent:hash = not found), never left " +
                       "out. Writes one file per PLC project and returns a short summary. Pipeline: TcAgent.exe sbom --solution X.sln.",
            Schema(P("plcProjectPath", "string", "PLC project, e.g. TIPC^MyPlc^MyPlc Project (empty = all PLC projects)"),
                   P("file", "string", "Target file (default <solution>\\sbom\\<PLC project>.cdx.json; several PLC projects: <name>.<PLC>.cdx.json)")),
            a => SbomTool(a));

        Add("tc_library_lock", "Library lock file <solution>\\.tcagent\\libraries.lock.json: the resolved library versions per PLC project " +
                               "(name, vendor, placeholder, requested '*'/fixed version, resolved version), sorted for clean Git diffs. " +
                               "action=check (default, read only) compares the current resolution with the lock file and lists the " +
                               "differences (added, removed, version changed) - e.g. after installing libraries or before a release; " +
                               "action=write creates/updates it (only when the user wants to accept the current versions). " +
                               "Pipeline gate: TcAgent.exe lock --solution X.sln --check (exit 1 on differences).",
            Schema(P("action", "string", "check (default) or write"),
                   P("plcProjectPath", "string", "PLC project (empty = all; with a project only its entry is checked/replaced)")),
            a => LibraryLockTool(a));
    }

    List<LibraryInventory.PlcInfo> ReadInventory(string plcProjectPath) => tc.Sta.Run(() =>
    {
        var plcs = string.IsNullOrEmpty(plcProjectPath) ? tc.PlcProjects() : new List<ITcSmTreeItem> { PlcProject(plcProjectPath) };
        if (plcs.Count == 0) throw new Exception("No PLC project in the solution.");
        return LibraryInventory.Read(tc, plcs);
    }, 600_000);

    string SbomTool(JsonObject a)
    {
        var dir = SolutionDir() ?? throw new Exception("No solution open - tc_attach or tc_open first.");
        var infos = ReadInventory(Str(a, "plcProjectPath"));
        var file = Str(a, "file");
        if (!string.IsNullOrEmpty(file)) file = Path.GetFullPath(file, dir);     // relative to the solution, not to the server's working directory
        return string.Join("\n", infos.Select(p => LibraryInventory.WriteSbom(p, dir, LibraryInventory.SbomFile(dir, file, p.Name, infos.Count))));
    }

    string LibraryLockTool(JsonObject a)
    {
        var action = (Str(a, "action") ?? "check").Trim().ToLowerInvariant();
        if (action is not ("check" or "write")) throw new Exception("action: check (default) or write");
        var dir = SolutionDir() ?? throw new Exception("No solution open - tc_attach or tc_open first.");
        var path = Str(a, "plcProjectPath");
        var infos = ReadInventory(path);
        var partial = !string.IsNullOrEmpty(path);
        return action == "write" ? LibraryInventory.WriteLock(dir, infos, partial) : LibraryInventory.CheckLock(dir, infos, partial).Text;
    }
}
