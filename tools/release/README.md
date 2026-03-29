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
- **Timeouts**: `-TaskTimeoutSeconds <n>` (override per-task idle timeout; `0` disables timeout override). `build-mobile.ps1` defaults to a 300-second idle timeout so wizard runs fail fast only when work appears stalled.
- **Commit metadata** (for `conventional-commit.ps1` or publish pre-commit): `-CommitType`, `-CommitScope`, `-CommitMessage`, `-CommitBody`, `-BreakingChange`, `-AutoCommit`, `-NoCommit`.
- **Publish / major version**: `-ApproveMajorVersion` (required when a real publish would bump major and automation cannot prompt).
- **Responses automation**: `-ResponsesFile` (JSON consumed by `prompt-helpers.ps1` when present), `-NonInteractive`, `-RequireNonInteractiveReady` (validates task + dry-run inputs for CI).

Local artifact folders (repo root; gitignored)

- **`artifacts/logs/`** — wizard progress JSON, publish summaries, release notes, manifests, `build.lock`, harness runs under `wizard-tests/`.
- **`artifacts/releases/<version>/windows`** and **`.../mobile`** — outputs from `release.ps1` / publish flow (versioned exe + apk).
- **`artifacts/ci/`** — GitHub Actions `release-build.yml` publishes desktop/Android builds here (separate from versioned releases).

Older checkouts may still have a legacy **`publish/`** tree; current scripts write to **`artifacts/`** only. `recover-missing-release.ps1` still looks under **`publish/releases`** and **`publish/logs`** when recovering from an old layout.

Progress and diagnostics

- The wizard now tracks each selected task through explicit states:
  - `queued`: selected and waiting to run.
  - `running`: currently executing.
  - `succeeded`: completed with exit code `0`.
  - `failed`: terminated with non-zero exit or thrown error.
  - `skipped`: not executed because dry-run mode was enabled.
- At the end of each run, a step summary is printed with:
  - step number, script name, state, duration, and exit code.
- While a task runs, the wizard renders an in-place single-line status with:
  - spinner glyph (`| / - \\`)
  - activity bar (`[###.....]`) that advances when real work pulses are detected
    (CPU deltas, child-process changes, and output-file growth).
  - The activity bar is intentionally liveness-oriented and caps before completion
    until the task exits.
- By default, the wizard also writes a machine-readable run artifact:
  - `artifacts/logs/wizard-progress-<timestamp>.json`
  - This includes run metadata (`RunId`, mode, status, total duration) and full per-step timeline.
- Progress redraws are in-place console updates only and are not appended into
  wizard JSON artifacts or release logs.
- To disable JSON artifact output, pass:
  - `-NoProgressJson`

Testing

- **Pester** (`tools/release/wizard.tests.ps1`): unit-style checks for wizard helpers (`Prompt-MultiSelect`, `Get-WizardTaskArguments`, major-version approval, git status parsing, etc.). Requires the Pester module. Example:

  ```powershell
  Invoke-Pester -Path "tools\release\wizard.tests.ps1"
  ```

- **Harness** (`tools/release/tests/harness.ps1`): copies `wizard.ps1` and `common.ps1` into an isolated work directory under `artifacts/logs/wizard-tests/`, stubs release scripts, and runs the wizard with scripted stdin to exercise end-to-end selection and timeout behavior. Parameters:

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
  `artifacts/logs` artifacts plus script output for root cause details.
- When publish tasks are selected, the wizard runs `recover-missing-release.ps1` once in advisory `-DryRun` mode before the real steps.
