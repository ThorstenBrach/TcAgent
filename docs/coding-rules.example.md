# Coding rules – example

<!-- Template: copy to <solution folder>\.tcagent\coding-rules.md and adapt.
     TcAgent presents this file to every agent before it first writes code.
     Keep it short and unambiguous - every rule costs context in every session. -->

## Names
- POUs: `FB_`, `FC_`, `PRG_`, interfaces `I_`, methods and properties in PascalCase without prefix.
- Data types: `ST_` (struct), `E_` (enum), `T_` (alias); enum values in PascalCase.
- Variables with type prefix: `b` BOOL, `n` integer, `f` REAL/LREAL, `s` STRING, `t` TIME, `st` struct, `e` enum, `fb` FB instance.
- Scope in front: `i` VAR_INPUT, `q` VAR_OUTPUT, `io` VAR_IN_OUT, `_` internal variables (e.g. `inSpeed`, `qbDone`, `_nState`).
- Global variables only in GVLs with `{attribute 'qualified_only'}`.

## Structure
- Every POU, method and property gets a `///` header comment (purpose; for FBs also sequence and error behavior).
- State machines as `CASE` over an enum, no bare numbers as states.
- No magic numbers – constants in `VAR CONSTANT` or a parameter GVL.
- Report errors via `bError` + `nErrorId` (or `hrErrorCode`), never silently continue.
- No `ADR`/pointer code without a justification in a comment.

## Formatting
- Indent 4 spaces, align `:=` in declaration blocks.
- Keywords in upper case, one statement per line.

## Tests
- New FBs get a TcUnit test suite `FB_<Name>_Test` in the `Tests` folder.
