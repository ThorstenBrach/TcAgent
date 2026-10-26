---
name: requirements-interview
description: Clarify a machine function before writing PLC code - look things up in the project first, then ask the open decisions one question at a time with a recommended answer, end with a short specification. Use when the user asks for a new function, station, sequence, device integration or a larger change and decisions are still open.
metadata:
  tcagent-inspired-by: the open-source "grill me" interview method (one question at a time, always with a recommendation)
  tcagent-version: "1"
---
# Requirements interview

Goal: no PLC code until every decision below is either settled or explicitly left open by the user.

## 1. Look up first, ask second

Before the first question, collect what the project already answers – never ask for it:

- existing function blocks, types and similar functions: `tc_plc_search`, `tc_find_references`, `tc_doc action=outline`
- I/Os that exist or are free: `tc_io_vars`, the I/O tree (`tc_tree path=TIID`)
- tasks and cycle times: `tc_task action=list`
- coding rules, platform and skills: `tc_coding_rules`
- lessons about similar work: `tc_lessons action=search`

## 2. Ask one question at a time

Use `tc_ask_user` with **one** question per call, 2-4 options, the recommended option first with a short reason
("Recommended – matches the existing stations"). Move on only after the answer. Topics, in this order – skip what is
already clear:

1. **Purpose and boundaries** – what the function does, what it does not do, who uses it.
2. **States / step sequence** – states or steps, start and stop conditions, what happens after a power cycle.
3. **Interlocks and permissives** – what must be true before each movement or action.
4. **Inputs and outputs** – existing or new signals, types, scaling, units, inverted logic.
5. **Error cases** – timeouts, sensor faults, what the machine does on an error, how errors are acknowledged.
6. **Operating modes** – automatic, manual, setup: what is allowed in each.
7. **Safety** – which parts are safety-related. Note them only; safety functions are never designed here.
8. **HMI** – what the operator sees and may do.
9. **Timing** – task, cycle time, response times.
10. **Tests** – which TcUnit tests prove the function (names of the test cases).

If an answer raises a new decision, ask it next. If the user says "you decide", take the recommended option and note it.

## 3. Short specification

After the last answer, write a short specification in the chat:

- decisions (one line each), open points, assumptions
- planned objects (function blocks, methods, types, I/Os, task)
- planned tests

Ask the user to confirm it (`tc_ask_user`: confirm / change something). Then implement – following the coding rules –
and check with `tc_check`.
