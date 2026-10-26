// tc_skills (Skills.cs): list, show, add and mirror the skills of the levels and the platform of the project.
//
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace TcAgent;

public sealed partial class ToolRegistry
{
    void RegisterSkills()
    {
        Add("tc_skills", "Skills = how this team does a kind of task (e.g. requirements-interview before a new function, plc-debugging, " +
                         "plc-code-review), in the open Agent Skills format, from the levels built-in / company / user / platform / project. " +
                         "action=list (name + description - also shown by tc_coding_rules), show name=<name> (load it when the task fits and follow it), " +
                         "add name description body scope=project|personal|platform:<name> (a new skill, after the user agreed; dryRun first), " +
                         "mirror (copy the skills that apply everywhere into the skill folders of the agent CLIs).",
            Schema(Req(P("action", "string", "list, show, add or mirror")),
                   P("name", "string", "show/add: skill name (lowercase, digits, hyphens, e.g. plc-code-review)"),
                   P("description", "string", "add: what the skill does and when to use it (one or two sentences)"),
                   P("body", "string", "add: the instructions (Markdown: steps, checks, examples)"),
                   P("inspiredBy", "string", "add: source of the method the skill adopts (link), if any"),
                   P("scope", "string", "add: project (default), personal or platform:<name>"),
                   P("dryRun", "boolean", "add/mirror: only show what would be written")),
            a =>
            {
                var dir = SolutionDir();
                var pi = KnowledgeLayers.Platforms(dir);
                var entries = Skills.Entries(dir, pi.Active);
                switch ((Str(a, "action") ?? "list").ToLowerInvariant())
                {
                    case "list":
                        return Skills.Catalog(entries) ?? "No skills found (built-in: knowledge\\skills next to TcAgent.exe; " +
                                                         "own: <level>\\skills\\<name>\\SKILL.md, see tc_knowledge).";
                    case "show":
                        var name = Str(a, "name") ?? throw new Exception("name is required");
                        var e = entries.FirstOrDefault(x => x.Active && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                ?? throw new Exception($"Skill '{name}' not found or switched off. Available: {string.Join(", ", entries.Where(x => x.Active).Select(x => x.Name))}");
                        return Skills.Show(e);
                    case "add":
                    {
                        string file;
                        try { file = Skills.Add(dir, Str(a, "scope"), Str(a, "name"), Str(a, "description"), Str(a, "body"), Str(a, "inspiredBy"), !DryRun(a)); }
                        catch (ArgumentException ex) { throw new Exception("NOT added: " + ex.Message); }
                        return (DryRun(a) ? "PROPOSAL (not written): " : "Skill saved: ") + file;
                    }
                    case "mirror":
                        var report = Skills.Mirror(entries, Skills.DefaultMirrors(), write: !DryRun(a));
                        return (DryRun(a) ? "PROPOSAL (nothing copied):\n" : "Mirrored:\n") + string.Join("\n", report);
                    default: throw new Exception("action: list, show, add or mirror");
                }
            });
    }
}
