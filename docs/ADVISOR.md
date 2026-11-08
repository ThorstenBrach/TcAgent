# Advisor

The Advisor is an agent that looks, advises and hands over a ready order – it changes nothing itself. A person reads the
advice, rates it, and sends the order to an agent when it should be acted on. That agent then works as usual, with
workflows, checkpoints and confirmations.

| | Advisor Shadow | Advisor Live |
|---|---|---|
| When | in the background: at night, after a commit, after a failed pipeline | at the running plant: commissioning, troubleshooting in operation |
| Who | the team | the person in front of the machine |
| Reads | code, commits and diff, errors, test results | in addition online values, alarms, step chains |
| Started by | `TcAgent.exe advise`, or `/advise` in the chat | the person (`/advise live`), later events at the machine |
| State | **available** | planned |

## What an Advisor run may do

A run is read only, and TcAgent enforces it – not the prompt:

- `tc_advisor action=start` makes the session read only until `action=finish`: every tool that would change the project or
  the runtime is refused, the audit log records each call.
- `TcAgent.exe advise` starts the AI client with its own MCP configuration: TcAgent with `TCAGENT_MODE=ask` and
  `TCAGENT_ADVISOR` set.
- A code change is only proposed, as a diff in the advice. It is never applied.

## The advice

Each finding is recorded with `tc_advisor action=record` and has the same fields:

| Field | Content |
|---|---|
| finding | one sentence: what was found |
| evidence | quotes from tool outputs with their place (object, line, variable, value) |
| proposal | what should be done, optionally a diff |
| order | a self-contained prompt a person sends to an agent to act on it, naming the workflow (`bug-fix`, `new-feature`, …) |
| confidence | high, medium or low, with one sentence why |

The advice is kept in the project, in Git for the whole team: `<solution folder>\.tcagent\advisor\<yyyy-MM>.jsonl`, one
line per advice. A rating is a line of its own (append only), so two people never edit the same line.

## Rating and hit rate

Everyone who works on the project can rate an advice: right, partly, wrong or n/a; the name is stored with it. The hit
rate per kind of task (review, diagnose, cause) is "right" among the last 20 rated; under 10 ratings there is no rate.
Advice that stays unrated for 14 days no longer counts as open.

## Where you see it

- **Knowledge window, tab Advisor:** the advice with its evidence, order and diff; one click to rate it; **Act on it …**
  opens the TcAgent Chat with the order in the input – you read it, change it and send it.
- `TcAgent.exe advise list|stats --solution <sln> [--open] [--json]`, `tc_advisor action=list|stats`.

## Starting a run

**In the chat:** the prompt `/advise` – choose the kind (review, diagnose, cause) and what to look at.

**Without a window** (pipeline, scheduled task):

```powershell
TcAgent.exe advise --solution MyMachine.sln --trigger nightly                      # review of the commits of the last 24 hours
TcAgent.exe advise --solution MyMachine.sln --trigger pipeline --results results   # cause of a failed pipeline
```

- The context is collected without an AI: the commits and the diff since `--since` (default "24 hours ago"), the result
  files of the pipeline (`--results`). Nothing committed: no run, no tokens.
- The AI client is the one chosen in the chat. For now only **Claude Code** runs without a window; it must be signed in
  for the user the runner runs as.
- A run stops after `--minutes` (default 15). An IDE the run opened without a window is closed afterwards – never one that
  was open before.
- `--dry-run` prints the prompt only.

In a pipeline: [PIPELINE.md](PIPELINE.md#advisor).
