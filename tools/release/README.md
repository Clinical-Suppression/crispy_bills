Crispy_Bills — Release Wizard

Purpose

This folder contains existing release scripts (build, publish, release). The
`wizard.ps1` script provides an interactive, local-first orchestrator that
prompts the user to select tasks, supports dry-run, and optionally runs the
conventional commit helper.

Usage

From PowerShell:

powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\wizard.ps1

Wizard parameters (non-exhaustive; see `wizard.ps1` for the full `param` block)

- **Task selection**: `-Tasks` (script names, indices, ranges, comma-separated). With `-NonInteractive` and `-RequireNonInteractiveReady`, you must also pass `-Tasks`, `-All`, or `wizard.Tasks` in the responses file; `-All` satisfies that check so the wizard can take the non-interactive default (all discovered scripts when no other selection is supplied).
- **Execution mode**: `-DryRun` (print commands only), `-NoCommit` (skip publish auto-commit / commit step), `-AutoConfirm` (non-interactive defaults; selects all tasks when none specified), `-Verbose`.
- **Git / tree**: `-AllowDirty` vs `-RequireCleanTree` (mutually exclusive; default allows dirty tree unless you require clean).
- **UX**: `-NoSpinner` (disable spinner for child scripts; interactive scripts that use `Read-Host` already run attached without spinner).
- **Commit metadata** (for `conventional-commit.ps1` or publish pre-commit): `-CommitType`, `-CommitScope`, `-CommitMessage`, `-CommitBody`, `-BreakingChange`, `-AutoCommit`, `-NoCommit`.
- **Publish / major version**: `-ApproveMajorVersion` (required when a real publish would bump major and automation cannot prompt).
- **Responses automation**: `-ResponsesFile` (JSON consumed by `prompt-helpers.ps1` when present), `-NonInteractive`, `-RequireNonInteractiveReady` (validates task + dry-run inputs for CI).

Progress and diagnostics

- The wizard now tracks each selected task through explicit states:
  - `queued`: selected and waiting to run.
  - `running`: currently executing.
  - `succeeded`: completed with exit code `0`.
  - `failed`: terminated with non-zero exit or thrown error.
  - `skipped`: not executed because dry-run mode was enabled.
- At the end of each run, a step summary is printed with:
  - step number, script name, state, duration, and exit code.
- By default, the wizard also writes a machine-readable run artifact:
  - `publish/logs/wizard-progress-<timestamp>.json`
  - This includes run metadata (`RunId`, mode, status, total duration) and full per-step timeline.
- To disable JSON artifact output, pass:
  - `-NoProgressJson`

Testing

- **Pester** (`tools/release/wizard.tests.ps1`): unit-style checks for wizard helpers (`Prompt-MultiSelect`, `Get-WizardTaskArguments`, major-version approval, git status parsing, etc.). Requires the Pester module. Example:

  ```powershell
  Invoke-Pester -Path "tools\release\wizard.tests.ps1"
  ```

- **Harness** (`tools/release/tests/harness.ps1`): copies `wizard.ps1` and `common.ps1` into an isolated work directory under `publish/logs/wizard-tests/`, stubs release scripts, and runs the wizard with scripted stdin to exercise end-to-end selection and timeout behavior. Parameters:

  - `-ScenarioId` (default `heartbeat-01`) — folder name suffix for the run.
  - `-StubMode` — `heartbeat` | `quiet` | `prompt` | `fail` (controls the stub used for `preflight.ps1`).
  - `-TimeoutSeconds` (default `60`) — watchdog; exit code `124` on timeout.
  - `-NoCommit` — adjusts stdin answers for the no-commit path.

  Example:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\release\tests\harness.ps1" -StubMode heartbeat -TimeoutSeconds 60
  ```

Notes

- The wizard delegates to the existing scripts in this directory; it does not
  re-implement build/publish logic.
- Use the dry-run option to inspect commands before executing.
- The wizard will run `conventional-commit.ps1` if present; otherwise it will
  prompt for a manual commit message.
- On failure, review the summary entry for the failed step and then inspect
  `publish/logs` artifacts plus script output for root cause details.
- When publish tasks are selected, the wizard runs `recover-missing-release.ps1` once in advisory `-DryRun` mode before the real steps.
