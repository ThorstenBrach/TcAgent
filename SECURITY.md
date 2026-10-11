# Security

TcAgent gives AI agents access to a development environment that can **control real machines**:
activate configurations, start and stop the PLC, write variables. Use it accordingly.

## Ground rules

* **Safe by default:** Without configuration, runtime actions are only allowed on this PC
  (`TCAGENT_ALLOW_TARGETS=local`). Enable other target systems deliberately and one at a time.
* **Do not enable machines that can endanger people or equipment** unless a human confirms every
  runtime action (keep per-tool-call confirmation enabled in the client).
* **Choose a mode:** `TCAGENT_MODE=propose` returns code changes only as a diff; `ask` allows read-only access.
* **Audit log** (`%LOCALAPPDATA%\TcAgent\audit.jsonl`) records every write call with its arguments.
* **Stay local:** TcAgent runs as a local process (stdio). Do not expose it over a network or as a public URL.
* **Secrets:** No passwords, customer data or license keys in prompts or `tc_feedback` reports.
  Keep in mind that code and context are sent to your chosen AI provider – check whether that is acceptable for your
  project.

## Reporting vulnerabilities

Please do not open a public issue; use GitHub's private reporting feature
("Security → Report a vulnerability") in the repository.
