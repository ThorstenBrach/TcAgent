# Risk rules – example

<!-- Template: copy to <solution folder>\.tcagent\risk-rules.md (project, in Git) or %LOCALAPPDATA%\TcAgent\risk-rules.md
     (personal) and adapt. TcAgent applies it in tc_doc action=risks, in the "Risks" section of the HTML documentation and in
     TcAgent.exe doc --risks [--fail-on high] (pipeline). Company rules: TCAGENT_RISK_RULES=<file or folder>.
     Later files win: project > personal > company. tc_risk_rules shows what is active and reports errors in this file.
     Text in ``` blocks and in sections other than the three below is ignored - use it for explanations. -->

## Built-in rules

<!-- Switch off, change severity or tune the thresholds of the 13 built-in rules (tc_risk_rules shows them all):
     - <rule id>: off | on | severity=high|medium|low | <parameter>=<number>   (several separated by commas) -->

- case-else: severity=medium
- long-code: limit=200, memberLimit=100
- deep-nesting: limit=4, mediumLimit=6
- magic-number: off

## Rule memcpy-without-sizeof
- severity: high
- find: `\bMEMCPY\s*\(`
- unless: `SIZEOF`
- message: MEMCPY without SIZEOF in the same line - the length can exceed the target buffer and overwrite memory.
- why: Buffer overruns stop the PLC (page fault) or corrupt other variables silently.

## Rule no-hardcoded-ams-netid
- severity: medium
- find: `'\d+\.\d+\.\d+\.\d+\.\d+\.\d+'`
- comments: keep
- message: AmsNetId as a literal - put it into a parameter GVL or a persistent variable so it can be changed per machine.

## Rule fb-error-output
- severity: low
- kinds: FB
- in: decl
- require: `\b(bError|Error)\s*:\s*BOOL\b`
- unlessInObject: `\bEXTENDS\b`
- message: Function block without an error output (bError : BOOL) - callers cannot react to failures.

## Rule fb-header-comment
- severity: low
- kinds: FB, PRG, FUNCTION
- in: decl
- comments: keep
- require: `\A(?:.*\n){0,3}\s*(///|//|\(\*)`
- message: No header comment in the first lines of the declaration - describe purpose, inputs/outputs and error behaviour.

## Rule wait-loop
- severity: high
- find: `\bWHILE\b[^;]*\bDO\b`
- kinds: METHOD, FUNCTION
- message: WHILE in a method/function - waiting for a signal inside one call blocks the task; use a state machine over several cycles.

## Checks

<!-- Checks the AI agent verifies by reading the code (things a regular expression cannot decide). They appear with the
     results of tc_doc action=risks and in the HTML page; the agent reports violations like findings. One sentence each. -->

- Every MC_ function block instance (MC_Power, MC_MoveAbsolute ...): the Error output is evaluated and the ErrorID is logged or passed on.
- Every state machine has a defined error state, and every state can reach it.
- Outputs that move hardware are reset when the PLC goes to error or stop (no output stays TRUE after an error).
- Timeouts exist for every wait on an external signal (sensor, drive, communication).
