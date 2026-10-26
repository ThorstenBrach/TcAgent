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

```markdown
---
title: Naming rules of framework A
description: one line – shown in lists
locked: true            # company/platform only: later levels may not replace this file
force: false            # user only: keep this file even against a platform file
applies-to: [FB, METHOD, VAR]
tags: [naming]
scope: platform:framework-a
owner: framework-a team
version: 3
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
  anyway; only then the rule is saved with `force: true`. `force` does not override `locked`.

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
description: Create a new device driver FB in framework A
---
1. Derive from FB_FwA_DeviceBase …
```

- The format is the one of Claude Code skills (`SKILL.md` with `name` and `description`, optional further files).
- **`tc_skills`** (actions `list`, `show`, `add`) delivers the skills in every AI client. TcAgent resolves levels,
  platform, `locked` and `force`. The agent gets the list (name and description) together with the coding rules and
  loads a skill only when the task fits – the context stays small.
- TcAgent additionally mirrors the valid skills into `.claude\skills`, so Claude Code finds them without a tool call.
- The TcAgent guide stays the built-in base skill.

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

## Deterministic first, agent second

Wherever a result can be produced deterministically (templates, generators, checks, formatting, report and
documentation skeletons), TcAgent shall do that first and let the agent only review and refine it. The output becomes
reproducible and fewer tokens are spent. A list of candidates (what the agent does today that a program could do) is
part of the implementation plan.

## Implementation steps (proposal)

1. Levels × platforms for the existing kinds (coding rules, risk rules, lessons, knowledge, doc templates):
   `TCAGENT_COMPANY`, `platforms\`, `config.json`, detection and the question on ambiguity, file header, `locked`/`force`,
   one file per topic/lesson, split command.
2. `tc_skills` with the mirror into `.claude\skills`.
3. Workflows and hard gates (`tc_activate` only after a build without errors).
4. Run logs and the improvement loop (`tc_improve`), moving knowledge up with the name check, pull requests for shared
   levels.
5. Deterministic-first candidates.
