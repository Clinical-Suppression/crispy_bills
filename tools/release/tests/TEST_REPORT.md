# Wizard Test Report

Date: 2026-03-20
Scope: `tools/release/wizard.ps1` with harnessed delegated-script stubs under `tools/release/tests/harness.ps1`

## What was run

Primary scenarios executed (isolated `preflight.ps1` selection):

- `heartbeat-11` (`StubMode=heartbeat`, timeout 20s)
- `quiet-06` (`StubMode=quiet`, timeout 8s)
- `prompt-05` (`StubMode=prompt`, timeout 8s)
- `fail-05` (`StubMode=fail`, timeout 20s)

Artifacts are under `artifacts/logs/wizard-tests/<scenario>-<timestamp>/`.

Additional thorough suite executed:

- `thorough-heartbeat` (`StubMode=heartbeat`, timeout 25s)
- `thorough-quiet` (`StubMode=quiet`, timeout 8s)
- `thorough-prompt` (`StubMode=prompt`, timeout 8s)
- `thorough-fail` (`StubMode=fail`, timeout 20s)

Real wizard script runs (not fixture copy):

- Real dry-run, all tasks selected, commit disabled, dry-run enabled (`exit 0`)
- Real preflight path, single task selected, commit disabled, dry-run disabled (`exit 1` expected on dirty tree)

## Classifications observed

- `heartbeat-11`: `completed`
- `quiet-06`: `running-but-quiet` (watchdog timeout)
- `prompt-05`: `completed` (prompt script did not block in this host mode)
- `fail-05`: `delegated-failure` (exit code 1 propagated)

Additional suite:

- `thorough-heartbeat`: `completed`
- `thorough-quiet`: `running-but-quiet`
- `thorough-prompt`: `completed`
- `thorough-fail`: `delegated-failure`

Real wizard run outcomes:

- Real dry-run all-task flow completed and printed all delegated command previews.
- Real non-dry preflight flow failed fast with expected message: `Working tree is not clean. Commit or stash changes before publish.`

## Important defects discovered and fixed

1. Strict-mode parse bug in `Prompt-MultiSelect`
- Symptom: selecting numeric choices failed with `[ref] cannot be applied to a variable that does not exist`.
- Fix applied in `tools/release/wizard.ps1`: initialize `$n = 0` before `[int]::TryParse(...,[ref]$n)`.

2. Single-selection treated as empty
- Symptom: entering `1` could produce `No tasks selected; exiting.`
- Fix applied in `tools/release/wizard.ps1`: normalize selection to array with `$sel = @((Prompt-MultiSelect -Options $available))` and check only `$sel.Count -eq 0`.

3. Output formatting polish (`\`n` printed literally)
- Symptom: wizard printed literal `` `n `` text in commit-step and completion lines.
- Fix applied in `tools/release/wizard.ps1`: use double-quoted strings for newline escapes in `Write-Host`.

## Observability finding

`running-but-quiet` is reproducible: child scripts can run long enough to trip watchdog with minimal incremental output visible at parent level.

Evidence:
- `quiet-06` stdout shows delegated `preflight.ps1` start banner and command invocation, then no further lines before watchdog timeout.
- This indicates operator perception of "hang" can be caused by low/late child output visibility.

## Notes on prompt behavior

`prompt-05` completed rather than blocking in this harness mode. The stub prompt did not remain waiting. This suggests prompt blocking may be host-dependent and should still be validated in manual interactive VS Code terminal runs.

The thorough prompt scenario (`thorough-prompt`) behaved the same way in this redirected-input harness host.

## Real wizard validation summary

1. Dry-run orchestration path is functioning end-to-end:
- prompt flow accepted scripted input
- task selection with `all` worked
- each delegated script command was printed in DRY-RUN mode
- wizard completed with diagnostics summary

2. Real preflight gate path is functioning and correctly blocks unsafe publish:
- auth and remote checks executed
- branch sync check executed
- dirty working tree gate triggered and aborted the run
- wizard propagated delegated failure and printed partial diagnostics

3. Manual-host dry-run prompt flow works with single-task selection:
- Input sequence `1,n,y,y` executed preflight dry-run and exited successfully.
- Note: when piping stdin from file, an extra trailing token can be interpreted by the parent shell after child process exits.

## Suggested next runs (for next agent)

1. Real wizard manual host validation
- Run in VS Code integrated terminal interactively (no input redirection).
- Compare with standalone PowerShell host.

2. Real script dry-run matrix
- Select one real task at a time: preflight -> build-windows -> build-mobile -> build-both.
- Then run full `all` dry-run path.

3. Terminal/session reliability checks
- Reproduce prior terminal closure behavior while running wizard from agent-managed terminal.
- Capture host details and whether process remains alive when terminal detaches.

## Harness location

- `tools/release/tests/harness.ps1`

Current harness supports:
- stub modes: `heartbeat`, `quiet`, `prompt`, `fail`
- timeout watchdog
- classification output + ledger writing
- per-scenario stdout/stderr capture
