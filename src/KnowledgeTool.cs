// tc_knowledge: what the agent's knowledge consists of and where it comes from (KnowledgeLayers.cs, docs/KNOWLEDGE_LAYERS.md).
//
//   platforms      the platform (automation framework) of the project: configured, detected or ambiguous
//   set_platform   store it in .tcagent\config.json (after asking the user when it was ambiguous)
//   list           files of a kind on every level with their status (active, replaced, refused, disabled, forced)
//   effective      only what applies for this project, and why
//   why            one name: every file with that name and what happened to it
//   disable/enable switch a file off for the user or the project (disabled.json - the file itself stays unchanged)
//   split          split a former single file (coding-rules.md, risk-rules.md, lessons.md) into one file per topic
//
// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    static readonly string[] KnowledgeKinds = { "coding-rules", "risk-rules", "skills", "lessons", "doc-templates" };

    void RegisterKnowledge()
    {
        Add("tc_knowledge", "The knowledge the agent gets and where it comes from - levels company / user / project and the platform " +
                            "(automation framework) of the project: action=platforms (which platform, why; ambiguous = ask the user with tc_ask_user), " +
                            "set_platform platforms=<names> (store it in .tcagent\\config.json), list kind=coding-rules|risk-rules|lessons|doc-templates " +
                            "(every file with its level and status), effective (what applies here and why), why name=<kind/name or name>, " +
                            "disable/enable key=<kind/name> scope=user|project (switch a file off without changing it), " +
                            "split kind=... scope=project|personal (one file per topic instead of a former single file; dryRun first).",
            Schema(Req(P("action", "string", "platforms, set_platform, list, effective, why, disable, enable or split")),
                   P("kind", "string", "list/split: coding-rules, risk-rules, skills, lessons or doc-templates (list: default all)"),
                   P("platforms", "string", "set_platform: comma-separated platform names, or 'none'"),
                   P("name", "string", "why: name of a file (e.g. naming or coding-rules/naming)"),
                   P("key", "string", "disable/enable: <kind>/<name>, e.g. coding-rules/naming"),
                   P("scope", "string", "disable/enable/split: project (default) or user/personal"),
                   P("dryRun", "boolean", "split: only show the files")),
            a => KnowledgeCall(a));
    }

    string KnowledgeCall(JsonObject a)
    {
        var dir = SolutionDir();
        var action = (Str(a, "action") ?? "effective").ToLowerInvariant();
        var pi = KnowledgeLayers.Platforms(dir);
        switch (action)
        {
            case "platforms":
            {
                var sb = new StringBuilder();
                sb.AppendLine(pi.Active.Count > 0 ? $"Platform of this project: {string.Join(", ", pi.Active)} ({pi.Reason})" : $"Platform: none ({pi.Reason})");
                if (KnowledgeLayers.PlatformNote(pi) is { } note && pi.Ambiguous.Count > 0) sb.AppendLine(note);
                sb.AppendLine(pi.Known.Count == 0 ? "No platforms defined (TCAGENT_COMPANY\\platforms\\<name>\\platform.md, TCAGENT_PLATFORMS, %LOCALAPPDATA%\\TcAgent\\platforms\\<name>)."
                                                  : "Known platforms:");
                foreach (var d in pi.Known)
                    sb.AppendLine($"  {d.Name}{(d.Description != null ? " – " + d.Description : "")}" +
                                  (d.Libraries.Count > 0 ? $"  [libraries: {string.Join(", ", d.Libraries)}]" : "") +
                                  (d.Terms.Count > 0 ? $"  [terms: {string.Join(", ", d.Terms)}]" : "") +
                                  (d.Roots.Count > 0 ? $"\n    {string.Join("\n    ", d.Roots)}" : ""));
                var libs = KnowledgeLayers.ReferencedLibraries(dir);
                if (libs.Count > 0) sb.AppendLine($"Libraries of the project ({libs.Count}): {string.Join(", ", libs.Take(40))}{(libs.Count > 40 ? " …" : "")}");
                return sb.ToString();
            }
            case "set_platform":
            {
                var names = (Str(a, "platforms") ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                                                       .Where(s => !s.Equals("none", StringComparison.OrdinalIgnoreCase)).ToList();
                var unknown = names.Where(n => !pi.Known.Any(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (DryRun(a)) return $"PROPOSAL: platforms = {(names.Count == 0 ? "none" : string.Join(", ", names))}";
                var f = KnowledgeLayers.SetPlatforms(dir, names);
                return $"Stored: platforms = {(names.Count == 0 ? "none (no platform)" : string.Join(", ", names))} in {f}" +
                       (unknown.Count > 0 ? $"\nNote: no definition found for {string.Join(", ", unknown)} - only the user's own platform folder applies." : "");
            }
            case "list":
            case "effective":
            {
                var kinds = Str(a, "kind") is { Length: > 0 } k ? new[] { k.Trim().ToLowerInvariant() } : KnowledgeKinds;
                var sb = new StringBuilder();
                sb.AppendLine(pi.Active.Count > 0 ? $"Platform: {string.Join(", ", pi.Active)} ({pi.Reason})" : $"Platform: none ({pi.Reason})");
                if (pi.Ambiguous.Count > 0) sb.AppendLine(KnowledgeLayers.PlatformNote(pi));
                foreach (var kind in kinds)
                {
                    var entries = EntriesOf(kind, dir, pi);
                    if (action == "effective") entries = entries.Where(e => e.Active).ToList();
                    sb.AppendLine($"\n{kind} ({entries.Count(e => e.Active)} applied{(action == "list" ? $" of {entries.Count}" : "")}):");
                    foreach (var e in entries)
                        sb.AppendLine($"  {(e.Active ? "✔" : "✖")} {e.Name,-28} {e.Layer.Label,-26} {e.Status}{(e.Locked ? ", locked" : "")}" +
                                      $"{(e.Reason != null ? " – " + e.Reason : "")}\n      {e.Path}");
                }
                return sb.ToString();
            }
            case "why":
            {
                var name = Str(a, "name") ?? throw new Exception("name is required, e.g. naming or coding-rules/naming");
                var kindPart = name.Contains('/') ? name[..name.IndexOf('/')] : null;
                var namePart = name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;
                var hits = (kindPart != null ? new[] { kindPart } : KnowledgeKinds)
                           .SelectMany(k => EntriesOf(k, dir, pi)).Where(e => e.Name.Equals(namePart, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) return $"No file named '{name}' on any level.";
                return string.Join("\n", hits.Select(e => $"{(e.Active ? "✔ applies" : "✖ " + e.Status)}: {e.Key} from {e.Layer.Label}{(e.Locked ? " (locked)" : "")}" +
                                                          $"{(e.Reason != null ? " – " + e.Reason : "")}\n   {e.Path}"));
            }
            case "disable":
            case "enable":
            {
                var key = Str(a, "key") ?? throw new Exception("key is required, e.g. coding-rules/naming");
                if (!key.Contains('/')) throw new Exception("key: <kind>/<name>, e.g. coding-rules/naming");
                var scope = (Str(a, "scope") ?? "project").ToLowerInvariant() is "personal" ? "user" : (Str(a, "scope") ?? "project").ToLowerInvariant();
                var kind = key[..key.IndexOf('/')];
                var target = EntriesOf(kind, dir, pi).FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (action == "disable" && target?.Locked == true)
                    return $"NOT switched off: {key} is locked by {target.Layer.Label} - only its owner can change it.";
                var f = KnowledgeLayers.SetDisabled(dir, scope, key, action == "disable");
                return $"{(action == "disable" ? "Switched off" : "Switched on")}: {key} for {(scope == "user" ? "this user" : "this project")} ({f})" +
                       (target == null ? "\nNote: no such file found right now - the entry applies as soon as one exists." : "");
            }
            case "split":
            {
                var kind = (Str(a, "kind") ?? throw new Exception("kind is required: coding-rules, risk-rules or lessons")).ToLowerInvariant();
                if (kind is not ("coding-rules" or "risk-rules" or "lessons")) throw new Exception("kind: coding-rules, risk-rules or lessons");
                var scope = (Str(a, "scope") ?? "project").ToLowerInvariant();
                var root = scope is "personal" or "user" ? KnowledgeLayers.UserRoot : KnowledgeLayers.ProjectRoot(dir) ?? throw new Exception("No solution open");
                var single = Path.Combine(root, kind + ".md");
                if (!File.Exists(single)) return $"Nothing to split: {single} does not exist.";
                if (kind == "risk-rules") return "risk-rules.md keeps its sections (## Built-in rules, ## Rule <id>, ## Checks) - split it by hand into risk-rules\\<topic>.md if wanted.";
                var files = KnowledgeLayers.Split(single, write: !DryRun(a));
                return (DryRun(a) ? "PROPOSAL (nothing written) - " : "Done - ") + $"{single} → {files.Count} files:\n" +
                       string.Join("\n", files.Select(f => "  " + f.File)) +
                       (DryRun(a) ? "\nAsk the user before splitting (the single file is kept as *.split-<time>, no longer read)." : "\nThe single file was renamed to *.split-<time> (backup, no longer read).");
            }
            default: throw new Exception("action: platforms, set_platform, list, effective, why, disable, enable or split");
        }
    }

    static List<KnowledgeLayers.Entry> EntriesOf(string kind, string dir, KnowledgeLayers.PlatformInfo pi) => kind switch
    {
        "coding-rules" => CodingRules.Entries(dir, pi),
        "risk-rules" => KnowledgeLayers.Resolve("risk-rules", dir, pi.Active, true, (Environment.GetEnvironmentVariable("TCAGENT_RISK_RULES") ?? "").Split(';')),
        "skills" => Skills.Entries(dir, pi.Active),
        "lessons" => KnowledgeLayers.Resolve("lessons", dir, pi.Active, false),
        "doc-templates" => KnowledgeLayers.Resolve("doc-templates", dir, pi.Active, false, pattern: "*.*"),
        _ => throw new Exception("kind: " + string.Join(", ", KnowledgeKinds))
    };
}
