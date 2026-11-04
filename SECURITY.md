# Security

TcAgent gives AI agents access to a development environment that can **control real machines**:
activate configurations, start and stop the PLC, write variables. Use it accordingly.

## Ground rules

* **Safe by default:** Without configuration, runtime actions are only allowed on this PC
  (`TCAGENT_ALLOW_TARGETS=local`). Enable other target systems deliberately and one at a time.
* **Do not enable machines that can endanger people or equipment** unless a human confirms every
  runtime action (keep per-tool-call confirmation enabled in the client).
* **Confirm runtime actions:** by default (`TCAGENT_CONFIRM=runtime`) you are asked before every activation, PLC
  start/stop and ADS write; `all` asks before project changes as well. If nobody can be asked (no question dialog in the
  client, no TcAgentBridge dialog in the IDE) the action is refused; without a question only with `TCAGENT_CONFIRM=off`.
* **Package changes:** `tc_packages_change` checks package ids, versions, feed names and sources against fixed character
  sets and starts `tcpkg.exe` of the program folders elevated, without a shell.
* **TcUnit report command:** the `report` command of `.tcagent\tcunit.json` runs only after you approved it once for the
  solution and the command text; TcAgent's own HTML report encodes all texts, links only http(s) URLs and has a
  Content-Security-Policy.
* **Choose a mode:** `TCAGENT_MODE=propose` returns code changes only as a diff; `ask` allows read-only access.
* **Audit log** (`%LOCALAPPDATA%\TcAgent\audit.jsonl`) records every write call with its arguments.
* **Stay local:** TcAgent runs as a local process (stdio). Do not expose it over a network or as a public URL.
* **Connections it makes by itself:** the VS extension asks `api.github.com` for the releases of TcAgent once a day, after the IDE
  has loaded: an anonymous GET, with the TcAgent version in the User-Agent and nothing about the solution or the user. It also
  fetches the Git repositories of the sources in use once a day (`git fetch`, no pull, no sign-in window). Pipelines, the
  command line and the agent do not check. Both switches are in **⋯ > About TcAgent**; `updates.json` on the company or team
  level locks them, e.g. `{ "check": false, "sources": false }`.
* **Updates are never installed silently.** A download starts on a click; the `.vsix` must match `SHA256SUMS.txt` of the release and
  **Install** opens the VSIX installer. The checksum shows a damaged download. It lies in the same release, so it does not
  protect against a taken-over repository: the packages are not signed.
* **Shared folders:** in the TcAgent Chat the user can let the agent read folders outside the solution without asking; what it reads goes
  to the AI provider. Changing a file there still asks. Drives, the user profile, `AppData`, key folders such as `.ssh` and
  system folders are refused. The list is `%LOCALAPPDATA%\TcAgent\shared-folders.json`.
* **Secrets:** No passwords, customer data or license keys in prompts or `tc_feedback` reports.
  Keep in mind that code and context are sent to your chosen AI provider – check whether that is acceptable for your
  project.

## Reporting vulnerabilities

Please do not open a public issue; use GitHub's private reporting feature
("Security → Report a vulnerability") in the repository.

Please include what an attacker can do (and under which conditions), the steps to reproduce and the output of
`TcAgent.exe diag` (versions, no names or paths). Fixes are made in the latest version only - please update first if
you can.
