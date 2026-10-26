---
name: plc-code-review
description: Review PLC code or a change systematically - static risks, coding and risk rules, and a TwinCAT checklist (tasks and cycle time, access from several tasks, persistence, I/O links, online change, safety interfaces) - and report findings with their location. Use when the user asks for a review, before activating larger changes, or when a change touches shared data.
metadata:
  tcagent-inspired-by: open-source code review methods (structured checklist, findings with location and severity)
  tcagent-version: "1"
---
# PLC code review

## 1. Scope

Agree what is reviewed: a list of objects, a folder, or the changes since the last commit. Read every object
completely (`tc_plc_read`) before judging it.

## 2. Automatic checks

- Static risks: `tc_doc action=risks filter=<object>` – verify every finding in the code before reporting it.
- Compiler: `tc_check` (warnings count too).
- Coding rules: `tc_coding_rules` – compare names, comments and structure.

## 3. TwinCAT checklist

For each object:

- **Tasks and cycle time** – which task calls it (`tc_task`), heavy loops or string handling in a fast task.
- **Access from several tasks** – variables written by one task and read by another without a defined handover.
- **State machines** – CASE with ELSE, enums instead of numbers, every state can be left, timeouts on waits.
- **Edges and timers** – timers called every cycle (not inside conditions), edge detection where a single action is
  meant.
- **Arrays and pointers** – index checks, pointer and reference checks before use.
- **Persistence** – PERSISTENT/RETAIN only where needed; what happens after a restart.
- **I/O** – linked variables (`tc_io_vars`), inverted logic and units documented, outputs written in one place.
- **Online change** – changes that break an online change (moved or renamed variables of running instances).
- **Safety** – interfaces to safety logic only read and documented; safety logic itself is never changed here.
- **Tests and comments** – public methods and inputs/outputs commented (`///`), important behaviour covered by TcUnit.

## 4. Report

List the findings, most severe first: severity (high / medium / low), object and line, what is wrong, why it matters, a
concrete proposal. Say which findings were verified by reading the code. Change nothing without the user's consent;
offer to fix them one by one.
