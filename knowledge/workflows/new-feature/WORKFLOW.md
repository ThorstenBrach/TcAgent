---
name: new-feature
description: Add a new machine function or extend an existing one in the PLC project - from the clarified requirement over a change plan to code that compiles and is reviewed. Use it when the user asks for a new function, a new FB or a larger extension.
metadata:
  tcagent-gates: check-ok before review; tc_activate only after a build with 0 errors (always)
---
Follow the steps in order. Mark each one done with `tc_workflow action=step`; skip a step that does not apply with
`skip=true` (checkpoints cannot be skipped).

1. **requirements** – Clarify what the function must do before any code: load the skill `requirements-interview`
   (`tc_skills action=show name=requirements-interview`) and follow it. Skip this step only if the user already gave a
   complete specification (inputs, outputs, states, error handling, limits).
2. **change-plan** – Read the coding rules (`tc_coding_rules`) and the affected code (`tc_plc_read`, `tc_plc_search`,
   `tc_find_references`). Show the user a short plan: which POUs, methods, DUTs and GVLs are new or changed, which
   interfaces change, which I/Os or tasks are touched. Wait for the user's agreement before writing code.
3. **code** – Write the code with `tc_plc_write` / `tc_plc_create`, following the coding rules (names, comments, structure).
   Keep the change as small as the plan; no unrelated reformatting.
4. **check** (requires: check-ok) – `tc_check` until it reports 0 compiler errors. Fix the cause, not the symptom. If the
   same error comes back several times, look for a lesson (`tc_lessons`) before guessing.
5. **tests** – If the project has TcUnit tests for this area, add or extend a test and run it (`tc_test_run`). Recommended,
   not a checkpoint - skip it when the project has no tests.
6. **review** – Load the skill `plc-code-review` and review the change against it. Fix the findings that are clearly
   wrong; list the others for the user.
7. **save** – `tc_save`. Summarize for the user what was changed, what was not tested, and what they should check on the
   machine. If something was hard to find, record a lesson (`tc_lessons action=add`, after the user agreed).
