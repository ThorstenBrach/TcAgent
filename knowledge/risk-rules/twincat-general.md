# TwinCAT general risk rules

<!-- Shipped with TcAgent (level built-in). They add to the 13 built-in code rules and apply to every project.
     Change them on your own level: a file with the same name replaces this one, or switch single rules off with
     tc_risk_rules / the Knowledge window. Format: docs/risk-rules.example.md.
     "high" is used sparingly: TcAgent.exe doc --risks --fail-on high fails a pipeline on these. -->

## Rule hardcoded-credential
- severity: high
- find: `(?i)\b\w*(password|passwd|pwd|secret|token|apikey)\w*\s*:=\s*'[^']+'`
- in: both
- comments: keep
- message: Password, token or key as a literal in the code - it ends up in the repository, in backups and on the target. Load it from protected configuration at run time.
- why: Source code and PLC archives are copied widely; a literal cannot be changed per machine and cannot be revoked.

## Rule new-without-delete
- severity: high
- find: `\b__NEW\s*\(`
- unlessInObject: `\b__DELETE\s*\(`
- message: __NEW without __DELETE in this object - check who frees the memory, otherwise the router memory of the runtime runs out.
- why: A leak in a cyclic PLC runs until an allocation fails, often weeks after commissioning.

## Rule trigger-in-method
- severity: high
- find: `:\s*(R_TRIG|F_TRIG)\b`
- in: decl
- kinds: METHOD
- unlessInObject: `\bVAR_INST\b`
- message: R_TRIG/F_TRIG in the VAR block of a method - it is initialised on every call and cannot keep the previous state. Use VAR_INST or an instance in the function block.
- why: Method variables live for one call only, so the edge detection does not work as intended.

## Rule memcpy-without-sizeof
- severity: medium
- find: `\b(MEMCPY|MEMSET|MEMMOVE)\s*\(`
- unless: `SIZEOF`
- message: Memory function without SIZEOF - check that the length cannot exceed the target (also with an offset).
- why: A buffer overrun stops the PLC with a page fault or silently corrupts neighbouring variables.

## Rule fixed-address
- severity: medium
- find: `\bAT\s+%[IQM][XBWDL]\d`
- in: decl
- message: Fixed address instead of %I* / %Q* - overlaps are not detected and the address breaks when the memory layout changes.

## Rule hardcoded-address
- severity: medium
- find: `'\d{1,3}(\.\d{1,3}){3}(\.\d{1,3}\.\d{1,3})?'`
- in: both
- comments: keep
- message: IP address or AmsNetId as a literal - put it into a parameter so it can be changed per machine.

## Rule float-equality-literal
- severity: medium
- find: `(?<![:<>])(=|<>)\s*-?\d+\.\d+\b`
- message: Exact comparison with a decimal number - REAL/LREAL values are rarely exactly equal. Compare with a tolerance.

## Rule time-equality-literal
- severity: medium
- find: `(?<![:<>])(=|<>)\s*(L?T|TIME|LTIME)#`
- message: Exact comparison with a time - a cyclic value can step over it. Use >= / <= or a range.

## Rule jmp-used
- severity: medium
- find: `\bJMP\b`
- message: JMP makes the program flow hard to follow and to test - use IF, CASE or a state machine.

## Rule analysis-suppressed
- severity: low
- find: `\{\s*(warning\s+disable|analysis\s+-|attribute\s+'analysis')`
- in: both
- comments: keep
- message: Compiler warning or static analysis switched off by a pragma - check that the reason still holds and comment it.

## Rule todo-left
- severity: low
- find: `\b(TODO|FIXME|HACK)\b`
- in: both
- comments: keep
- message: Open TODO/FIXME in the code - finish it or move it to the issue list before release.

## Rule narrowing-conversion
- severity: low
- find: `\b(L?REAL|LINT|ULINT|DINT|UDINT)_TO_(U?S?INT|BYTE|WORD)\b`
- message: Conversion to a smaller type - check the value range, it is cut off without a warning.

## Checks

<!-- The AI agent verifies these by reading the code - a regular expression cannot decide them. -->

- Asynchronous function blocks (ADS, file, TCP, CoE ...): Busy and Error are evaluated, Execute is used as an edge and a new request starts only after the last one ended.
- EtherCAT: WcState and State of important devices are monitored, and the program has a defined reaction when the bus or a device fails.
- Outputs that move hardware go to a defined safe state on error and stop - no output stays TRUE after an error.
- Every wait on an external signal (sensor, drive, communication) has a timeout.
- Every state machine has an error state that can be reached from every state.
- No WHILE/REPEAT loop waits for a signal inside one call - waiting belongs in a state machine over several cycles.
- Data handed over between tasks stays consistent: one writer, and a structure is not read while the other task writes it.
- No heavy loops or string handling in fast tasks - the cycle time has a margin.
- Online change: pointers and references are valid again after it (assigned cyclically or checked before use).
- References that are not fixed at startup are checked with __ISVALIDREF before use; pointers and interfaces are checked against 0 (a pointer can also be valid but point to the wrong or a freed object).
- Persistent and retain data are checked for plausibility after loading, and a change of their structure is handled.
- Standard PLC logic does not override or bypass safety signals - it only reads them for diagnosis and sequence control.
- Symbols writable from outside (ADS, OPC UA, HMI) do not start motion without a plausibility check in the PLC.
