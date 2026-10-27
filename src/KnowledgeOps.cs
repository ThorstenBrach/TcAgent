// Changes to the knowledge files from the knowledge manager window (TcAgent.exe knowledge new|move|delete|git ...):
//
//   new     a rule, risk rule, skill, workflow or lesson from a template - or an existing file - at a level (the window
//           opens it in the editor); company and team levels kept in Git get it in their working copy (Git bar: commit, push
//           to a new branch for a pull request)
//   move    to another level (moving up checks the text for names of this computer, user, solution and projects and the
//           privateTerms); into a shared level kept in Git as a copy in its working copy - the original stays until the pull
//           request is merged
//   delete  only on own levels (user, project, the user's platform folders, a team folder without Git); built-in and
//           company files are switched off instead (disabled.json)
//   git     per level kept in a Git repository: branch, local changes, commits behind/ahead (as of the last fetch);
//           fetch, pull (fast-forward only), commit, push - push of company/team goes to a new branch for a pull request
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TcAgent;

public static class KnowledgeOps
{
    public static readonly string[] NewKinds = { "coding-rules", "risk-rules", "skills", "workflows", "lessons" };

    /// <summary>The root of a level name: built-in, company, user, team, team:&lt;folder&gt;, platform:&lt;name&gt; (the user's), project.</summary>
    public static string RootOf(string solutionDir, string level)
    {
        var l = (level ?? "").Trim();
        if (l.Equals("project", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.ProjectRoot(solutionDir) ?? throw new ArgumentException("no solution open");
        if (l.Equals("user", StringComparison.OrdinalIgnoreCase) || l.Equals("personal", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.UserRoot;
        if (l.Equals("company", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.CompanyRoot ?? throw new ArgumentException("no company level (TCAGENT_COMPANY)");
        if (l.Equals("built-in", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.BuiltInRoot;
        if (l.Equals("team", StringComparison.OrdinalIgnoreCase)) return KnowledgeLayers.TeamRoot ?? throw new ArgumentException("no team level (TCAGENT_TEAM)");
        if (l.StartsWith("team:", StringComparison.OrdinalIgnoreCase))
            return KnowledgeLayers.TeamRoots.FirstOrDefault(t => Path.GetFileName(t.TrimEnd('\\', '/')).Equals(l[5..].Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException("unknown team " + l[5..]);
        if (l.StartsWith("platform:", StringComparison.OrdinalIgnoreCase)) return Path.Combine(KnowledgeLayers.UserRoot, "platforms", l[9..].Trim());
        // the company definition of a platform: <company>\platforms\<name> (shared like the company level)
        if (l.StartsWith("company-platform:", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(KnowledgeLayers.CompanyRoot ?? throw new ArgumentException("no company level (TCAGENT_COMPANY) - set it with Levels …"), "platforms", l[17..].Trim());
        throw new ArgumentException("level: user, project, team, platform:<name> or company");
    }

    /// <summary>Shared levels change by pull request only: company, and a team kept in Git.</summary>
    public static bool ViaPullRequest(string level, string root) =>
        level.Equals("company", StringComparison.OrdinalIgnoreCase) || level.StartsWith("company-platform:", StringComparison.OrdinalIgnoreCase) || level.StartsWith("team", StringComparison.OrdinalIgnoreCase) && KnowledgeLayers.IsGitRepo(RepoOf(root) ?? root);

    /// <summary>The level of a file: the layer whose root contains it (the most specific one).</summary>
    public static KnowledgeLayers.Layer LayerOf(string solutionDir, string path, IEnumerable<string> platforms) =>
        KnowledgeLayers.Layers(solutionDir, platforms).Where(l => IsUnder(path, l.Root)).OrderByDescending(l => l.Root.Length).FirstOrDefault();

    static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(root) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The item of a file: the folder of a skill/workflow (SKILL.md, WORKFLOW.md), otherwise the file itself.</summary>
    public static string ItemOf(string path) =>
        Path.GetFileName(path) is Skills.MainFile or Workflows.MainFile ? Path.GetDirectoryName(path)! : path;

    static string KindDirOf(string item, string root) => Path.GetRelativePath(root, item).Split('\\', '/')[0];

    public static bool Deletable(KnowledgeLayers.Layer l) =>
        l.Level is "user" or "project" || l.Level == "team" && !KnowledgeLayers.IsGitRepo(RepoOf(l.Root) ?? l.Root);

    // ------------------------------------------------------------------ new
    public static string New(string solutionDir, string kind, string name, string level)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (!NewKinds.Contains(kind)) throw new ArgumentException("kind: " + string.Join(", ", NewKinds));
        var root = RootOf(solutionDir, level);
        if (level.Equals("built-in", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the built-in level comes with TcAgent - create the file at another level");
        // company / team in Git: the file goes into the working copy - commit and push it in the Git bar (a new branch for a pull request)
        // "Valve Commissioning" -> valve-commissioning (the name rules of the Agent Skills standard)
        string slug = kind is "skills" or "workflows" ? Skills.ValidateName(KnowledgeLayers.Slug(name)) : KnowledgeLayers.Slug(name);
        string file, text;
        var title = (name ?? "").Trim();
        switch (kind)
        {
            case "skills":
                file = Path.Combine(root, "skills", slug, Skills.MainFile);
                text = $"---\nname: {slug}\ndescription: What this skill does and when to use it (one or two sentences).\n---\n# {slug}\n\n1. First step - which TcAgent tools, what to check.\n2. ...\n";
                break;
            case "workflows":
                file = Path.Combine(root, "workflows", slug, Workflows.MainFile);
                text = $"---\nname: {slug}\ndescription: Which task this workflow is for (one or two sentences).\n---\nFollow the steps in order and mark each one done with `tc_workflow action=step`.\n\n" +
                       "1. **plan** – What to clarify and show the user before any change.\n2. **code** – What to write, following the coding rules.\n" +
                       "3. **check** – `tc_check` until it is clean.\n4. **build** (requires: build-ok) – `tc_build` after the clean check.\n5. **save** – `tc_save` and a short summary.\n";
                break;
            case "lessons":
                file = Path.Combine(root, "lessons", $"{DateTime.Now:yyyy-MM-dd}-{slug}.md");
                text = $"## {title}\n- id: L{DateTime.Now:yyyyMMdd-HHmmss} · date: {DateTime.Now:yyyy-MM-dd} · tags:  · tool: \n- **Symptom:** what exactly happened (error text, behaviour)\n- **Cause:** why it happens\n- **Solution:** exactly what fixed it\n";
                break;
            case "risk-rules":
                file = Path.Combine(root, "risk-rules", slug + ".md");
                text = $"## Rule {slug}\n- severity: medium\n- find: `regular expression`\n- message: What is wrong and what to do instead.\n- why: What can happen on the machine.\n";
                break;
            default:
                file = Path.Combine(root, "coding-rules", slug + ".md");
                text = $"---\ndescription: {title}\n---\n# {title}\n\n- One rule per line, concise and with an example if needed.\n";
                break;
        }
        if (File.Exists(file)) throw new ArgumentException(file + " exists already");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.Replace("\n", "\r\n"));
        return file;
    }

    /// <summary>An existing file taken over to a level: a Markdown file (rule, risk rule, lesson) is copied into the folder of
    /// its kind; a skill or workflow is its folder (pick its SKILL.md / WORKFLOW.md or the folder). Returns the new path.</summary>
    public static string AddExisting(string solutionDir, string kind, string source, string level)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (!NewKinds.Contains(kind)) throw new ArgumentException("kind: " + string.Join(", ", NewKinds));
        var root = RootOf(solutionDir, level);
        if (level.Equals("built-in", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the built-in level comes with TcAgent - add the file at another level");
        source = Path.GetFullPath(source ?? throw new ArgumentException("--path is required"));
        if (kind is "skills" or "workflows")
        {
            var main = kind == "skills" ? Skills.MainFile : Workflows.MainFile;
            var folder = Directory.Exists(source) ? source : Path.GetFileName(source).Equals(main, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(source)! : null;
            if (folder == null || !File.Exists(Path.Combine(folder, main)))
                throw new ArgumentException($"a {kind[..^1]} is a folder with {main} - pick its {main}");
            var name = Skills.ValidateName(KnowledgeLayers.Slug(KnowledgeLayers.ParseFrontMatter(File.ReadAllText(Path.Combine(folder, main))).Top.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileName(folder)));
            var target = Path.Combine(root, kind, name);
            if (Directory.Exists(target)) throw new ArgumentException(target + " exists already");
            CopyDir(folder, target);
            return Path.Combine(target, main);
        }
        if (!File.Exists(source) || !source.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("pick a Markdown file (.md)");
        var file = Path.Combine(root, kind, KnowledgeLayers.Slug(Path.GetFileNameWithoutExtension(source)) + ".md");
        if (File.Exists(file)) throw new ArgumentException(file + " exists already");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(source, file);
        return file;
    }

    // ------------------------------------------------------------------ platforms
    /// <summary>A platform of the user: &lt;user&gt;\platforms\&lt;name&gt;\platform.md with the libraries and terms that detect it.</summary>
    public static string NewPlatform(string name, string description, string libraries, string terms, string level = "user")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a name is required");
        var n = Skills.ValidateName(KnowledgeLayers.Slug(name));          // "My Framework" -> my-framework
        // user: only for you; company: the definition for everybody (in the company folder / working copy - Git bar for the pull request)
        var baseRoot = (level ?? "user").Equals("company", StringComparison.OrdinalIgnoreCase)
            ? KnowledgeLayers.CompanyRoot ?? throw new ArgumentException("no company level (TCAGENT_COMPANY) - set it with Levels …")
            : KnowledgeLayers.UserRoot;
        var file = Path.Combine(baseRoot, "platforms", n, "platform.md");
        if (File.Exists(file)) throw new ArgumentException(file + " exists already - open it to change it");
        static string List(string v) => string.Join(", ", (v ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ($"---\nname: {n}\ndescription: {(string.IsNullOrWhiteSpace(description) ? "Automation framework " + n : description.Trim())}\n" +
                                 $"metadata:\n  tcagent-detect-libraries: \"{List(libraries)}\"\n  tcagent-detect-terms: \"{List(terms)}\"\n---\n" +
                                 $"# {n}\n\nWhat the agent should know about this framework (base blocks, conventions). Rules, skills and lessons of the\n" +
                                 $"platform go into the folders coding-rules, skills, lessons next to this file.\n").Replace("\n", "\r\n"));
        return file;
    }

    /// <summary>Back to detection: removes "platforms" from .tcagent\config.json.</summary>
    public static string AutoPlatform(string solutionDir)
    {
        var f = Path.Combine(KnowledgeLayers.ProjectRoot(solutionDir)!, "config.json");
        if (!File.Exists(f)) return "Platform detection is already automatic.";
        var cfg = JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? new JsonObject();
        cfg.Remove("platforms");
        File.WriteAllText(f, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return "Platform detection is automatic again (" + f + ")";
    }

    // ------------------------------------------------------------------ move
    public static string Move(string solutionDir, string path, string level, IEnumerable<string> platforms)
    {
        var item = ItemOf(Path.GetFullPath(path));
        var from = LayerOf(solutionDir, item, platforms) ?? throw new ArgumentException("not a file of a knowledge level: " + path);
        var root = RootOf(solutionDir, level);
        var target = Path.Combine(root, KindDirOf(item, from.Root), Path.GetFileName(item));
        if (Path.GetFullPath(target).Equals(Path.GetFullPath(item), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("it is already on that level");
        if (Directory.Exists(target) || File.Exists(target)) throw new ArgumentException(target + " exists already");
        // moving towards more people: nothing specific travels upwards
        if (!level.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            var texts = Directory.Exists(item) ? Directory.GetFiles(item, "*", SearchOption.AllDirectories).Where(f => Path.GetExtension(f) is ".md" or ".txt" or ".json").Select(File.ReadAllText) : new[] { File.ReadAllText(item) };
            var found = Improver.Specifics(string.Join("\n", texts), Improver.PrivateTerms(solutionDir));
            if (found.Count > 0 && from.Level == "project") throw new ArgumentException($"it names {string.Join(", ", found)} - make it general first");
        }
        if (Directory.Exists(item)) CopyDir(item, target);
        else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(item, target); }
        // built-in and shared files stay (the copy on the new level replaces them by name); own files are moved
        // into a shared level kept in Git: a copy in its working copy - the user commits and pushes it (Git bar: a new branch for a
        // pull request); the original stays until the pull request is merged
        if (ViaPullRequest(level, root))
            return $"Copied into the working copy of the {level} repository: {target} - commit and push it in the Git bar (the push goes to a new branch for a pull request). The original stays until the pull request is merged.";
        if (Deletable(from)) { if (Directory.Exists(item)) Directory.Delete(item, true); else File.Delete(item); return $"Moved to {target}"; }
        return $"Copied to {target} (the {from.Label} file stays; the copy replaces it by name)";
    }

    static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
    }

    // ------------------------------------------------------------------ delete
    /// <summary>Renames a file (rule, lesson, template) or a skill/workflow folder on its level; the name in its header follows.</summary>
    public static string Rename(string solutionDir, string path, string name, IEnumerable<string> platforms)
    {
        var item = ItemOf(Path.GetFullPath(path));
        var from = LayerOf(solutionDir, item, platforms) ?? throw new ArgumentException("not a file of a knowledge level: " + path);
        if (from.Level == "built-in") throw new ArgumentException("the built-in level comes with TcAgent - switch the file off and create your own instead");
        var kind = KindDirOf(item, from.Root);
        var oldName = Path.GetFileNameWithoutExtension(item);
        if (Sources.LoadLock(from.Root).ContainsKey(kind + "/" + oldName))
            throw new ArgumentException($"{kind}/{oldName} is installed from a source - \"Keep as own version …\" first, then rename it");
        var isDir = Directory.Exists(item);
        var slug = isDir ? Skills.ValidateName(KnowledgeLayers.Slug(name)) : KnowledgeLayers.Slug(name);
        if (slug.Length == 0) throw new ArgumentException("a name is needed");
        var target = Path.Combine(Path.GetDirectoryName(item)!, isDir ? slug : slug + Path.GetExtension(item));
        if (Path.GetFullPath(target).Equals(Path.GetFullPath(item), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("it has that name already");
        if (Directory.Exists(target) || File.Exists(target)) throw new ArgumentException(target + " exists already");
        if (isDir) Directory.Move(item, target); else File.Move(item, target);
        // the name in the header (skills and workflows need it to match the folder)
        var main = isDir ? new[] { Skills.MainFile, Workflows.MainFile }.Select(m => Path.Combine(target, m)).FirstOrDefault(File.Exists) : target;
        if (main != null && Path.GetExtension(main).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var t = File.ReadAllText(main);
            var m = System.Text.RegularExpressions.Regex.Match(t, @"\A(\uFEFF)?---\r?\n(.*?)\r?\n---", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success && System.Text.RegularExpressions.Regex.IsMatch(m.Groups[2].Value, @"(?m)^name:"))
                File.WriteAllText(main, t.Substring(0, m.Groups[2].Index) +
                                        System.Text.RegularExpressions.Regex.Replace(m.Groups[2].Value, @"(?m)^name:.*$", "name: " + slug) + t.Substring(m.Groups[2].Index + m.Groups[2].Length));
        }
        return $"Renamed: {item} -> {target}";
    }

    public static string Delete(string solutionDir, string path, IEnumerable<string> platforms)
    {
        var item = ItemOf(Path.GetFullPath(path));
        var from = LayerOf(solutionDir, item, platforms) ?? throw new ArgumentException("not a file of a knowledge level: " + path);
        if (!Deletable(from)) throw new ArgumentException($"files of the {from.Label} level are not deleted here - switch it off instead (it stays in its source)");
        if (Directory.Exists(item)) Directory.Delete(item, true); else File.Delete(item);
        // an item installed from a source: its lock entry goes too
        var key = KindDirOf(item, from.Root) + "/" + Path.GetFileNameWithoutExtension(item);
        Sources.Unlock(from.Root, key);
        return "Deleted: " + item;
    }

    // ------------------------------------------------------------------ git
    /// <summary>The working tree a folder belongs to, null if none.</summary>
    public static string RepoOf(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    /// <summary>Git state of a level root: branch, upstream, behind/ahead (as of the last fetch), changed files under the root.</summary>
    public static JsonObject GitState(string root)
    {
        var repo = RepoOf(root);
        if (repo == null) return null;
        var o = new JsonObject { ["repo"] = repo };
        try
        {
            o["branch"] = Sources.Git(repo, "rev-parse --abbrev-ref HEAD", 15_000);
            try
            {
                var counts = Sources.Git(repo, "rev-list --left-right --count HEAD...@{u}", 15_000).Split('\t', ' ');
                o["ahead"] = int.Parse(counts[0]); o["behind"] = int.Parse(counts[^1]); o["upstream"] = true;
            }
            catch { o["upstream"] = false; }
            var rel = Path.GetRelativePath(repo, root);
            var changes = new JsonObject();
            foreach (var line in Sources.Git(repo, $"status --porcelain -uall -- \"{(rel == "." ? "." : rel)}\"", 15_000).Split('\n').Where(l => l.Length > 3))
            {
                var st = line[..2].Trim();
                var p = line[3..].Trim().Trim('"');
                if (p.Contains(" -> ")) p = p[(p.IndexOf(" -> ") + 4)..];
                changes[Path.GetFullPath(Path.Combine(repo, p.Replace('/', '\\')))] = st == "??" ? "new" : st.Contains('D') ? "deleted" : "changed";
            }
            o["changes"] = changes;
        }
        catch (Exception ex) { o["error"] = ex.Message; }
        return o;
    }

    public static string GitAction(string solutionDir, string action, string level, string message)
    {
        var root = RootOf(solutionDir, level);
        var repo = RepoOf(root) ?? throw new ArgumentException($"the {level} level is not in a Git repository");
        var rel = Path.GetRelativePath(repo, root);
        var scope = rel == "." ? "." : rel;
        switch (action)
        {
            case "fetch": Sources.Git(repo, "fetch -q"); return "Fetched " + repo;
            case "pull": return Sources.Git(repo, "pull --ff-only -q") is var o && o.Length > 0 ? o : "Pulled " + repo;
            case "commit":
                if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required");
                Sources.Git(repo, $"add -A -- \"{scope}\"");
                Sources.Git(repo, $"commit -q -m \"{message.Replace("\"", "'")}\" -- \"{scope}\"");
                return "Committed in " + repo;
            case "push":
                var branch = Sources.Git(repo, "rev-parse --abbrev-ref HEAD");
                // shared levels change by pull request: their commits go to a new branch, never directly to the main branch
                if (ViaPullRequest(level, root))
                {
                    var b = $"tcagent/{Environment.UserName.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}";
                    Sources.Git(repo, $"push -q -u origin HEAD:refs/heads/{b}");
                    return $"Pushed to the new branch {b} - open a pull request in the repository of the {level} level.";
                }
                Sources.Git(repo, "push -q");
                return $"Pushed {branch} of {repo}";
            default: throw new ArgumentException("git action: fetch, pull, commit or push");
        }
    }
}
