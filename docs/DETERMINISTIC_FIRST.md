# Deterministic first, agent second

> **Status:** principle since 0.30, first conversions in 0.36. This page lists what TcAgent already does deterministically,
> what was converted, and the candidates for the next steps with effort and benefit.

An agent is good at understanding a task and at the parts that need judgement. It is slow, costs tokens and gives a
slightly different result every time for work that follows fixed rules. TcAgent therefore splits such work in two:

1. **Deterministic first** – TcAgent does the part that follows rules: always the same output for the same input, no
   tokens, testable with unit tests.
2. **Agent second** – the agent checks the result and sharpens only where understanding is needed (a name, an expected
   value, an explanation), and the user decides.

## Already deterministic

| Area | What TcAgent does by itself | What is left to the agent |
|---|---|---|
| Coding rules | Rules gate: the first code write is refused until the rules were read; levels, platform, locked/force | apply the rules |
| Activation | Gate: build first, refuse with errors (0.32) | fix the errors |
| Workflows | Order of the steps, checkpoints (`check-ok`, `build-ok`, `tests-ok`), counting failed checks (0.32) | the content of each step |
| Self-improvement | Run logs, KPIs, proposals by fixed rules, evaluation over 10 runs, name check before promoting (0.33) | the text of a step hint |
| Lessons | Recall of matching lessons after an error, reminder after a hard-won success | writing a lesson |
| Refactoring | `tc_refactor action=analyze`: missing comments, unclear names, external use (callers, I/O, HMI, persistent) | the new names and comments |
| Risk analysis | Built-in and own regex rules on the code (`tc_doc action=risks`, `tc_risk_rules`) | judging the findings |
| Documentation | Call hierarchy, call graph, class diagram, step chains, reference per object (`tc_doc`) | overview and short explanations |
| Plain-text ST | Export/import of the whole PLC code with a check that the text reproduces the project | – |
| Tests | TcUnit discovery from the code, selection by enable bools, run, report | writing the tests |
| Supply chain | SBOM, library lock, skill lock file with license check (0.34) | – |

## Converted in 0.36

| Conversion | Before | Now |
|---|---|---|
| **TcUnit test skeleton** (`tc_test_skeleton`) | the agent wrote the suite, the enable bools and every test method by hand – a different structure each time | TcAgent generates the suite with enable bools, a complete `InitialOutputs` test (every output against its initial value) and one test per public method with arrange / act and an assertion that fails until the TODO is replaced. The agent writes only the expected behaviour. |
| **Checkpoint check** (`tc_workflow action=step`) | a checkpoint step without a passing check was refused; the agent had to call `tc_check` and the step again | TcAgent runs `tc_check` / `tc_build` itself and completes the step when it passes – one call instead of two |
| **Changed objects** (`tc_workflow action=finish`) | the agent reconstructed from its memory what it had changed | TcAgent records every object written during the run (`tc_plc_write`, `tc_plc_create`, `tc_test_skeleton`) and lists them in the summary |

## Candidates

Effort: S (days), M (one or two weeks), L (more). Benefit: how much agent work and how many tokens it saves, and how much
more reproducible the result gets.

| # | Candidate | Deterministic part | Agent part | Effort | Benefit |
|---|---|---|---|---|---|
| 1 | **Machine-checkable coding rules** | a rule file may carry checks (regex on names and code like the risk rules); `tc_check` lists the violations with location | fix the violations; rules without a check stay text | M | high |
| 2 | **Change impact report** | for the objects of a change plan: callers, implemented interfaces, I/O links, persistent and HMI-bound variables, tasks, online-change risks (FB layout changed) | the plan and the questions to the user | M | high |
| 3 | **Review pre-pass** for `plc-code-review` | findings from the diff: variables written from several tasks, changed I/O-linked or persistent variables, new `RETAIN`, missing comments, changed interfaces | judge and explain the findings | M–L | high |
| 4 | **Tests from a specification table** | the table of the requirements interview (given / when / then) becomes one test method per row with the asserts | the table itself | M | high |
| 5 | **I/O linking by convention** | variable names ↔ terminal channels by a mapping rule (prefix, number), checked before `tc_link` | the rule, the exceptions | M | high |
| 6 | **Comment completeness in the check** | inputs, outputs and methods without `///` as warnings of `tc_check` (from the refactor analysis) | the comments | S | medium |
| 7 | **Facts before the requirements interview** | tasks and cycle times, similar existing FBs, free I/Os, naming of the project – handed to the skill before the first question | the questions | S | medium |
| 8 | **Change summary and commit message** | changed objects and the plain-text diff per object as a structured list | the one-sentence "why" | S | medium |
| 9 | **User-manual tables** | tables of inputs, outputs, enums and their comments from the declarations | the prose | S | medium |
| 10 | **HMI binding check** | every bound symbol exists in the PLC and has the expected type | – | S | medium |
| 11 | **ST formatter** | indentation, keyword case, aligned declarations on write (configurable by the coding rules) | – | L | medium |
| 12 | **Lesson similarity** | near-duplicate lessons by text similarity for `tc_improve` | merging | S | low |

**Next proposal:** 1, 2 and 6 first – they make the checkpoint "check" stronger (rules and comments are checked, not
only compiled) and give the change plan facts instead of guesses.
