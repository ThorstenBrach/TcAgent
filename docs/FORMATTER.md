# ST formatter

TcAgent formats Structured Text the same way every time: indentation, case of keywords, spacing, aligned declarations,
blank lines, long calls and brackets in conditions. It never changes what the code means - a safety net checks that before
anything is written. Every rule can be set, per company, team, platform or project.

## In the IDE

Open a POU, method, property, action, DUT or GVL and press **Ctrl+Alt+F** (or **Tools > Format ST code (TcAgent)**).
TcAgent formats the declaration and the implementation - also unsaved changes - and puts the result into the editor like
a paste, so **Ctrl+Z** takes it back. The status bar says what happened; a part that cannot be formatted stays as it is and
a message says why.

The key can be changed or switched off: `"formatHotkey": "Ctrl+Alt+F"` (or `"off"`) in `chat-backends.json`.

## Settings with a preview

**Tools > Formatter settings (TcAgent) ...** lists every rule with its value and the level it comes from. Change one and the
preview shows at once what it does - before and after, for the declaration and the implementation, on an example or on the
object open in the editor. **Save** writes the changed rules into `formatting.json` of the chosen level (project, user, team,
company; a backup of the levels is made first). Rules a level above locked are shown with a lock and cannot be changed.

**Wish a rule or feature ...** opens a new issue on GitHub with the example of the preview: what the code looks like, what the
formatter makes of it today, and a place for what you would like instead. Check it in the browser, remove names of customers
or plants, and send it yourself.

## The safety net

- The lexer keeps every character: comments, pragmas, strings, typed and time literals, direct addresses.
- The tokens that carry meaning must be the same before and after (keywords without case). Brackets the formatter adds are
  checked on the expression trees instead: the order of the operators must stay the same.
- A second run must give the same text.
- A part that cannot be read (a comment that is not closed, an `END_IF` without `IF`) stays unchanged and is reported.

Tested on more than 66,000 declaration and implementation parts of real projects: no change of meaning, every part formatted
twice gave the same result.

## Switching it off in the code

```
// tcagent-format: off
aTable : ARRAY[0..2] OF INT := [  1,  20, 300,
                                400,   5,  60];
// tcagent-format: on
```

`// tcagent-format: next-line` keeps the next line, `// tcagent-format: file` the whole part. The pragma form works in the
declaration and the implementation without a compiler warning: `{attribute 'tcagent.format' := 'off'}` ... `'on'`.
The markers of other formatters are understood as well (`STweep.Disable`/`Enable`, `fmt: off`/`on`, `stformat:off`/`on`,
`tctidier-ignore`).

## Rules

Nothing is a fixed rule. The defaults (profile `twincat`):

| Rule | Default | Values |
|---|---|---|
| `indent.style` | `spaces` | spaces, tabs |
| `indent.size` | `2` | spaces per level |
| `indent.tab-width` | `4` | width of a tab |
| `indent.case-labels` | `true` | CASE labels one level in, their statements one more |
| `indent.continuation` | `preserve` | continuation lines keep their offset to the first line, or `indent` one level |
| `case.keywords`, `case.types`, `case.literals` | `upper` | upper, lower, preserve |
| `space.operators` | `one` | one space around `:=`, `=>`, `S=`, `R=`, `REF=`, arithmetic and comparisons; or preserve |
| `space.comma` | `one` | no space before a comma, one after; or preserve |
| `space.decl-colon` | `one` | `name : TYPE`; or preserve |
| `space.parens` | `preserve` | none, one, preserve - spaces inside `( )` and `[ ]` |
| `space.keyword-paren` | `one` | `IF (`, `AND (`; or preserve |
| `space.call-paren` | `none` | `F(x)`; or preserve |
| `comment.eol-spaces` | `preserve` | spaces before a comment at the end of a code line |
| `align.decl`, `align.decl-init`, `align.eol-comments` | `true` | `:`, `:=` and end-of-line comments of declarations in columns |
| `align.max-pad` | `40` | no alignment when a line would need more filling |
| `wrap.max-length` | `120` | longest line, `0` = never wrap |
| `wrap.call-args` | `when-long` | a call that is too long: one parameter per line, `)` on its own line; or never |
| `blank.max` | `1` | most blank lines in a row, or preserve |
| `blank.body-edges` | `remove` | blank lines at the start and end of a part |
| `eol.trim-trailing` | `true` | spaces at the end of lines |
| `eol.style` | `preserve` | preserve, crlf, lf |
| `paren.precedence` | `true` | brackets around AND groups inside OR, so the order is visible |

A group of aligned declarations ends at a blank line and at a comment line; attribute lines do not break it.

Profiles: `twincat` (the defaults), `compact` (no alignment), `preserve` (only what is switched on explicitly).

### Brackets in conditions

```
// before
bRelease := NOT bRunning AND bDoorOk OR bSetup AND bKeySwitch;
// after: the order that already applies becomes visible
bRelease := (NOT bRunning AND bDoorOk) OR (bSetup AND bKeySwitch);
```

The brackets always follow the order that applies (`NOT`, then `AND`, `XOR`, `OR`); they never change the meaning. Where
they show something else than what was meant, a person corrects the code.

## formatting.json on the knowledge levels

The rules live in `formatting.json` on the knowledge levels - built-in, company, user, team, platform, project; a later
level wins, a level can lock rules:

```json
{
  "profile": "twincat",
  "rules": { "indent.size": 4, "align.decl-init": false },
  "locked": ["indent.style"]
}
```

`"locked": true` locks every rule the file sets; a rule set to `"unset"` goes back to the default. A project file is
`.tcagent\formatting.json` next to the solution; the user level is `%LOCALAPPDATA%\TcAgent\formatting.json`.

`TcAgent.exe format --show-rules --solution <sln>` lists the effective rules and the level each one comes from.

## Command line

```
TcAgent.exe format --stdin --part implementation --solution <sln> [--json]   text from stdin, formatted to stdout
TcAgent.exe format --file <file.st> [--write | --check]                      a plain-text file
TcAgent.exe format --show-rules --solution <sln> [--json]                    the effective rules
TcAgent.exe format --save <changes.json> --level project --solution <sln>   rules into formatting.json of a level
```

Exit codes: 0 formatted or nothing to do, 1 `--check`: it would change, 2 wrong call, 3 not formatted (safety net or a part
that cannot be read). The step for the pipeline (`format --check` over a whole project, only changed lines, `format --init`
for the one big reformatting commit) follows; see the concept in the plan.
