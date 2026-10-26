# Knowledge layers, platforms, skills and self-improving workflows

> **Status: concept.** Nothing in this document is built yet. It describes how TcAgent shall organise the knowledge it
> gives to an agent – coding rules, risk rules, skills, lessons learned, knowledge sources, documentation templates and
> engineering workflows – and how this knowledge improves over time. Today's behaviour is described in
> [USER_GUIDE.md](USER_GUIDE.md).

## Why

Agents work best when they know how *this* team works: naming and structure rules, how a driver block is built, which
mistakes were made before, which manuals apply. That knowledge lives at different places:

- **company** – rules and know-how that apply to every TwinCAT project of the company
- **user** – personal preferences and tricks of one engineer
- **project** – what is special about one machine, versioned with its code
- **platform** – many projects are built on an automation framework of a customer or of the company (base classes,
  naming rules, libraries). Its rules apply to every project on that framework, but not to others.

TcAgent already reads coding rules, risk rules, lessons, knowledge sources and documentation templates from the levels
company / user / project. This concept adds the platform axis, skills and workflows, uses the same scheme for all of
them and adds a loop that improves them.

## Levels × platforms

```
                 general                    platform framework-a                platform framework-b
built-in         TcAgent guide, basic rules
company          \\server\twincat\          \\server\twincat\platforms\         \\server\twincat\platforms\
                                            framework-a\                        framework-b\
user             %LOCALAPPDATA%\TcAgent\    %LOCALAPPDATA%\TcAgent\platforms\   …
                                            framework-a\
project          <solution>\.tcagent\   – belongs to 0…n platforms, has no platform folders of its own
```

- The company root is `TCAGENT_COMPANY` (a network share or a Git repository). The existing variables
  (`TCAGENT_RULES`, `TCAGENT_RISK_RULES`, `TCAGENT_LESSONS`, `TCAGENT_KNOWLEDGE`, `TCAGENT_DOC_TEMPLATES`) keep working.
- A platform can also be a repository of its own, kept by the team that owns the framework: `TCAGENT_PLATFORMS` lists
  such roots.
- Every level and platform has the same folders:

```
coding-rules\   risk-rules\   skills\<name>\SKILL.md   workflows\<name>\WORKFLOW.md
lessons\        knowledge\    doc-templates\           platform.md (platform folders only)
```

### Which platform a project belongs to

1. Explicitly in `<solution>\.tcagent\config.json`: `{ "platforms": ["framework-a"] }`.
2. Otherwise TcAgent detects it: each `platform.md` lists terms and libraries that identify the framework.

```markdown
---
name: framework-a
description: Automation framework A
detect:
  libraries: [FwA_Base*, FwA_Motion]
  terms: [FB_FwA_DeviceBase]
---
Overview of the framework, terms, base classes, do's and don'ts …
```

3. If the detection is ambiguous (or finds nothing although platforms exist), the agent **asks the user** which platform
   the project is built on and writes the answer into `config.json`, so it is known next time.

TcAgent always shows which platform is active and why (configured / detected from which library / chosen by the user).

## Many small files

One topic, one skill, one workflow, one lesson = one file (or one folder for skills and workflows). Benefits:

- two people (or two agents) change different files – no merge conflicts in Git
- `git log` of a single rule shows who changed it, when and why
- each folder has one owner: the company TwinCAT team, the framework team, the engineer, the project team
- a platform can be handed on or versioned as a whole

Existing single files (`coding-rules.md`, `risk-rules.md`, `lessons.md`, `knowledge.md`) are still read. A command
splits them into single files – after the user agrees.

### Common file header

All kinds use the front matter of the Agent Skills standard (see [Skills](#skills)): `name` and `description` at the
top, TcAgent's own fields under `metadata` with the prefix `tcagent-`. One parser, one format for rules, skills,
workflows and lessons.

```markdown
---
name: naming
description: Naming rules of framework A – function blocks, instances, variables
metadata:
  tcagent-locked: "true"           # company/platform only: later levels may not replace this file
  tcagent-force: "false"           # user only: keep this file even against a platform file
  tcagent-applies-to: FB METHOD VAR
  tcagent-tags: naming
  tcagent-scope: platform:framework-a
  tcagent-owner: framework-a team
  tcagent-version: "3"
---
```

## Precedence

The later source wins on conflicts:

1. built-in
2. company · general
3. user · general
4. company · platform
5. user · platform
6. project

Framework rules therefore beat general preferences, and the project beats everything.

- **locked** (company and platform files): a later level cannot replace the file. TcAgent reports the conflict.
- **force** (user files): an engineer can keep a personal rule although a platform file says otherwise. When the user
  defines a rule in the chat that contradicts the platform, the agent points this out and asks whether the user wants it
  anyway; only then the rule is saved with `tcagent-force: "true"`. `force` does not override `locked`.

### How each kind is combined

| Kind | Unit | Same name on several levels |
|---|---|---|
| Coding rules, risk rules | one file per topic | the later level replaces the file with the same name; files with different names are all read |
| Skills | one folder per skill | the later level replaces the whole skill |
| Workflows | one folder per workflow | the later level replaces the whole workflow |
| Lessons | one file per lesson | nothing is replaced – all matching lessons are collected |
| Knowledge sources, documentation templates | one file per source / template | collected; platform sources only if the project belongs to that platform |

`locked` stops a replacement in every row.

## Skills

A skill describes **how a kind of task is done** (as opposed to coding rules, which describe what the code must look
like): "create a device driver block", "commission an axis", "write TcUnit tests", "review a change".

```markdown
---
name: driver-fb
description: Create a new device driver FB in framework A. Use when the user asks for a driver block for a new device.
metadata:
  tcagent-scope: platform:framework-a
  tcagent-locked: "false"
  tcagent-owner: framework-a team
---
1. Derive from FB_FwA_DeviceBase …
```

- **Format: the open [Agent Skills](https://agentskills.io/specification) standard** – a folder with `SKILL.md`
  (YAML front matter + Markdown), optional `scripts\`, `references\`, `assets\`. Claude Code, OpenAI Codex, GitHub
  Copilot, Gemini CLI, Cursor and others read this format, so the same skill works with every CLI the chat offers.
- Only the standard fields are used at the top level: `name` (lowercase letters, digits, hyphens, max. 64, equal to the
  folder name), `description` (what the skill does and when to use it, max. 1024), optional `license`, `compatibility`,
  `allowed-tools`. TcAgent's own fields (`locked`, `force`, `scope`, `owner`, `version`) go into **`metadata`** with the
  prefix `tcagent-` – other tools ignore them, and `skills-ref validate` accepts the files.
- **`tc_skills`** (actions `list`, `show`, `add`) delivers the skills through MCP in every AI client, independent of
  where a client looks for skills. TcAgent resolves levels, platform, `locked` and `force`. The agent gets the list
  (name and description) together with the coding rules and loads a skill only when the task fits – the context stays
  small (the "progressive disclosure" of the standard).
- Each tool has its own discovery folders (e.g. `.claude\skills` for Claude Code, `.agents\skills` for Codex). TcAgent
  **mirrors** the resolved skills into the folders of the CLIs that are set up, so a client also finds them without a
  tool call. The list of mirror folders is configurable.
- The TcAgent guide stays the built-in base skill and is itself an Agent Skill.

### Built-in TwinCAT skills: method adopted, content written for TwinCAT

Good general skills (e.g. the open-source collections obra/superpowers and mattpocock/skills, both MIT) consist of a
method and a text with examples from web development. TcAgent adopts the **method** and writes its own TwinCAT skills
that work with the TcAgent tools; `metadata.tcagent-inspired-by` names the original. First skills:

- `requirements-interview` – clarify a machine function before code: look things up in the project first, then one
  question at a time with a recommended answer (`tc_ask_user`): states/sequence, interlocks, I/Os, error cases and
  acknowledgement, operating modes, safety (noted, never designed), HMI, timing, tests; ends with a short specification
- `plc-debugging` – pin down the symptom, reproduce and measure (`ads_read`, scope, runtime messages), cause before
  fix, record a lesson
- `plc-code-review` – risk analysis, coding and risk rules, TwinCAT checklist (task assignment and cycle time, access from
  several tasks, persistence, I/O links, online change, safety interfaces read-only)

Later: `plc-verify` (partly hard gates), `tcunit-tdd`, `plc-change-plan`. Built-in skills are not locked – company,
platform or project can extend or replace them.

### Skill sources (Git repositories)

Every level can be a Git repository, and external collections can be added as sources:

- external: e.g. `github.com/obra/superpowers`, `github.com/mattpocock/skills`
- user: an own repository (local, optionally with a private remote)
- team, platform, company: shared repositories
- project: `.tcagent\skills` in the project repository

`tc_skills` / `TcAgent.exe skills`: `source add|remove`, `search`, `list`, `pull` (shows diff and license, asks),
`install <source>/<skill> --to user|team|platform:<name>|company|project`, `promote <skill> --to …` (a pull request
for shared repositories, after the check for customer/project names), `status` (outdated, changed locally, source,
license), `diff`.

- **Lock file** `.tcagent\skills.lock.json`: source, commit and checksum per skill – the team and the pipeline get
  exactly the same skills; updates only by `pull`.
- **Adapt without changing the original:** an overlay skill with `tcagent-extends: <source>/<skill>` adds only the
  TwinCAT parts; or a fork with origin and license in its header – `pull` reports changes of the original and offers a
  comparison.
- **Licenses:** detected on `pull` and recorded (`tcagent-source`, `tcagent-license`). MIT and Apache 2.0 are accepted,
  their notices travel with the skill. "Source-available" or unknown licenses are reported and not taken over by
  default.
- **Security:** skills may contain scripts – TcAgent shows the content before taking a skill over, never runs anything
  automatically, and pins sources to a commit.
- The repository layout is the common one of the Agent Skills ecosystem, so skills stay interchangeable with other skill
  installers.
- The same mechanism distributes **coding rules, risk rules, workflows and lessons** – every kind can come from a source
  repository.

The same applies to project instructions: `AGENTS.md` is the cross-tool standard for instruction files (Linux
Foundation, read by Codex, Copilot, Gemini CLI and others); TcAgent keeps writing it next to `CLAUDE.md`.

## Engineering workflows

A workflow describes how a kind of task is **always** done, with checkpoints:

```markdown
---
name: new-driver-fb
description: Add a driver FB for a new device
---
1. Read the coding rules; load skill driver-fb
2. Write the FB and its methods
3. tc_check – until 0 compiler errors
4. TcUnit tests, if the project has tests for drivers
5. Comments for every input/output and method
6. Save; record a lesson if something was hard
```

- **Soft steps** are guidance for the agent.
- **Hard gates** are enforced by TcAgent itself (like today's gate that refuses the first write before the coding rules
  were read):
  - `tc_activate` only if the **build** has 0 errors. "Check all objects" is not the measure – it also checks code that
    is never called and does not break the build.
  - Saving (`tc_save`) stays allowed with compiler errors, so intermediate states can be kept.
  - TcUnit tests are **not** a hard gate – not every task has tests. A workflow can recommend them.

## Self-improvement

### Run logs

After every workflow run TcAgent writes a short run log **locally** (not in Git):
`%LOCALAPPDATA%\TcAgent\runs\<project>\`. The last **100 runs** per project are kept; a compact summary per workflow
(e.g. "average failed checks per run") survives the deletion, so before/after comparisons stay possible.

A run log contains only counts and names – no code, no customer data:

- workflow, start, duration
- failed attempts per step (e.g. tc_check runs until 0 errors)
- gates and checkpoints met or missed
- coding rules violated, skills and lessons used
- where the user corrected, denied a permission or answered a question differently than proposed

### The improvement loop

1. TcAgent looks for improvements in the run logs, the feedback and the lessons, for example:
   - a step costs several failed attempts every time → a new rule or a hint in the skill
   - a lesson occurs in several projects → move it up (project → platform → company)
   - a rule is never needed or contradicts another one; a skill refers to a renamed tool
   - lessons that duplicate each other → merge; lessons never confirmed → retire
   - knowledge sources that no longer exist → report
2. It **asks the user** whether it may make the change and shows the diff. Nothing changes silently.
3. Where the change goes:
   - user and project files: after the user agreed in the chat
   - a level kept in a **shared repository** (company, platform, team): as a **pull request** in that repository, so
     its owners decide
   - `locked` files: only through their owner (pull request)
4. Moving knowledge up (project → platform → company) always asks the user first, and TcAgent checks the text for
   customer, project and machine names and removes them – nothing specific travels upwards.
5. The next run logs show whether the change helped (fewer failed attempts, fewer corrections). If not, TcAgent
   proposes to revert it.

## Knowledge manager (window)

A window in Visual Studio / XAE Shell ("TcAgent Knowledge", next to the TcUnit Tests window) shows everything the agent
gets and where it comes from:

- **Tree:** kind (coding rules, risk rules, skills, workflows, lessons, knowledge, templates) → level / platform →
  file. A second view shows the **effective** set for the open project: what applies, from which file, and why
  (replaced by a later level, locked, forced, disabled, platform not active).
- **Status per file:** active · replaced · locked · forced · disabled · changed locally · newer version in the source ·
  source and license.
- **Actions:** new (from a template, at the chosen level), edit (opens the file in the IDE editor), disable / enable,
  remove, move to another level, promote (pull request for shared repositories), pull updates, diff, validate (front
  matter, name rules).
- **Disabling** does not change the file: a small per-level list (`disabled.json`) records what is switched off, so
  files from a source or a shared repository stay untouched and can still be updated.
- Removing or changing files of a shared repository goes the same way as every change there: as a pull request.
  `locked` files can only be changed by their owner.
- **Overview (start page):** KPI tiles with a sparkline and the change against the previous period – runs,
  first-time-right (a run without a failed check and without a user correction), failed checks per run, corrections per
  run, minutes per run, gate stops; a table of all workflows (runs, trend, current value, change, proposals); open
  improvement proposals; the most frequent causes of failed checks; counts of rules, skills, workflows and lessons.
  A click on a workflow opens its history.
- **History view:** the metrics of the run logs over the runs (failed checks, user corrections, minutes per run,
  moving average) per workflow, every adopted change as a marker, a reverted change as a shaded range, and a list of the
  changes with before / after values over the following 10 runs. Open proposals can be reviewed from there; export as CSV.
- The same actions exist for agents and the command line (`tc_knowledge`, `TcAgent.exe knowledge`), so the agent can
  answer "which rules apply here and why?".

## Client neutrality (high priority)

TcAgent must work the same with every agent CLI the chat offers (Claude Code, GitHub Copilot CLI, Gemini CLI, Codex
CLI, …). The MCP server is already neutral; the chat window, the question tool, the guide files and the setup still
favour one client. These points come **first**, before the other implementation steps:

1. **Server texts:** hints that name one product ("quit Claude Desktop …") become neutral ("quit the AI client …").
   Confirmations stay MCP elicitation or the dialog in the IDE – both client-neutral.
2. **Chat window on the Agent Client Protocol (ACP):** today only one backend gets a persistent session, live tool
   calls, permission and question cards and attachments; the others run one process per question with plain text.
   [ACP](https://agentclientprotocol.com) is the open protocol for "editor talks to agent": persistent sessions,
   streaming, tool calls, permission requests (`allow_once`, `allow_always`, `reject_once`, `reject_always`). Gemini CLI
   and GitHub Copilot (preview) are listed as ACP agents; Claude Code and Codex CLI through adapters
   (`claude-agent-acp`, `codex-acp`). The chat window becomes an ACP client; the current stream-json path stays as a
   fallback until ACP works with every set-up CLI.
3. **Questions through TcAgent:** a new tool `tc_ask_user` (options, multiple choice, free text) shows the question card
   in the chat window and uses MCP elicitation outside it. Every client can call TcAgent tools, so questions work with
   every CLI – no dependency on a client's own question tool.
4. **One guide file:** `AGENTS.md` (cross-tool standard) is the single source of the TcAgent guide and the project
   instructions. `CLAUDE.md` only imports it (`@AGENTS.md`), Gemini CLI is configured to read `AGENTS.md`, Copilot and
   Codex read it anyway. Skills: Agent Skills format, mirrored into the folders of all set-up CLIs (see [Skills](#skills)).
5. **Neutral settings:** `%LOCALAPPDATA%\TcAgent\settings.json` becomes the single source of TcAgent's settings
   (allowed targets, confirmation, company root …). The setup writes every client's MCP configuration from it – no
   client's own configuration is the source any more, and the registered TcAgent version is read from there.
6. Smaller things: neutral defaults and texts in the chat (default backend, skill name in the prompt), documentation
   examples for several CLIs.

## Deterministic first, agent second

Wherever a result can be produced deterministically (templates, generators, checks, formatting, report and
documentation skeletons), TcAgent shall do that first and let the agent only review and refine it. The output becomes
reproducible and fewer tokens are spent. A list of candidates (what the agent does today that a program could do) is
part of the implementation plan.

## Implementation steps (proposal)

0. **Client neutrality, points 1–5 (high priority, as soon as possible)**: `tc_ask_user` → one guide file and neutral
   settings → ACP in the chat window (tried with Gemini CLI and Copilot first) → neutral texts.
1. Levels × platforms for the existing kinds (coding rules, risk rules, lessons, knowledge, doc templates):
   `TCAGENT_COMPANY`, `platforms\`, `config.json`, detection and the question on ambiguity, file header, `locked`/`force`,
   one file per topic/lesson, split command.
2. `tc_skills` (Agent Skills format) with the mirror into the skill folders of the set-up CLIs.
3. Workflows and hard gates (`tc_activate` only after a build without errors).
4. Run logs and the improvement loop (`tc_improve`), moving knowledge up with the name check, pull requests for shared
   levels.
5. Deterministic-first candidates.
