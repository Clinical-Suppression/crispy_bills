## Reliability & Stability (Windows & Android)
- [ ] No unhandled exceptions in load/save/import/export/delete flows
- [ ] Malformed GUID/date/CSV rows do not crash app; errors are logged
- [ ] Data import/export round-trip preserves all records
- [ ] Undo/redo stack maintains state consistency after 100+ actions
- [ ] Backup/restore and shutdown backup complete without race or data loss
- [ ] Android: No concurrent mutation or navigation crashes (one-in-flight guard)
- [ ] Android: Notes event handlers unsubscribed on navigation
- [ ] Diagnostics logs contain operation, severity, and stack trace
- [ ] Diagnostics logs are retained for 30 days (Android)
- [ ] QA verifies all reliability tests pass in parity test suite
# QA Checklist — Android UI Improvement Pass

- Build
  - [ ] Run `dotnet build` (Debug) — passes
  - [ ] Run `dotnet build -c Release` — passes
- Visuals
  - [ ] Verify app icon shows new primary color on device/emulator
  - [ ] Verify splash tint uses primary color
  - [ ] Verify Shell background/foreground for light/dark
  - [ ] Verify key screens: Main, Summary, Bill Editor, Notes, Diagnostics
  - [ ] Verify pie chart colors and legend contrast
- Accessibility
  - [ ] Contrast check for primary text vs backgrounds
  - [ ] Touch target sizes >= 48dp
  - [ ] Font scaling preserved (system font sizes)
- Functional smoke tests
  - [ ] Add Bill flow — create/edit/delete
  - [ ] Recurring bill delete behavior (this and future months only)
  - [ ] Import/Export flows (if available)
  - [ ] Diagnostics open and show startup issues
- Deliverables
  - [ ] Screenshot: Main page
  - [ ] Screenshot: Summary + Pie chart
  - [ ] Screenshot: Bill editor
  - [ ] Screenshot: Notes
  - [ ] Commit and open PR with before/after screenshots and notes

Notes:
- Screenshots should be captured on a representative device/emulator for Android (mdpi/hdpi/xxhdpi as needed).
- Release build may require signing configuration; for CI, provide keystore and msbuild properties.
- Parity / reliability automation: `dotnet test CrispyBills.Mobile.ParityTests/CrispyBills.Mobile.ParityTests.csproj` (or run the **CrispyBills.Mobile.ParityTests** project from the solution).
