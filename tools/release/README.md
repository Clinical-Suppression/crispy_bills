Crispy_Bills — Release Wizard

Purpose

This folder contains existing release scripts (build, publish, release). The
`wizard.ps1` script provides an interactive, local-first orchestrator that
prompts the user to select tasks, supports dry-run, and optionally runs the
conventional commit helper.

Usage

From PowerShell:

powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\wizard.ps1

Notes

- The wizard delegates to the existing scripts in this directory; it does not
  re-implement build/publish logic.
- Use the dry-run option to inspect commands before executing.
- The wizard will run `conventional-commit.ps1` if present; otherwise it will
  prompt for a manual commit message.
