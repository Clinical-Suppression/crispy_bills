# Crispy_Bills — Code Review Report

This document records the in-depth review performed against the plan in `.cursor/plans` (Crispy Bills code review). It includes domain invariants, build/test status, prioritized findings, and test-gap recommendations.

---

## 1. Executive summary

The codebase splits into a **WPF desktop** app ([`CrispyBills.csproj`](CrispyBills.csproj)) with monolithic [`MainWindow.xaml.cs`](MainWindow.xaml.cs), a **.NET MAUI** mobile app ([`CrispyBills.Mobile.Android`](CrispyBills.Mobile.Android/CrispyBills.Mobile.Android.csproj)) with [`BillingService`](CrispyBills.Mobile.Android/Services/BillingService.cs) + [`BillingRepository`](CrispyBills.Mobile.Android/Services/BillingRepository.cs), and **parity tests** ([`CrispyBills.Mobile.ParityTests`](CrispyBills.Mobile.ParityTests/CrispyBills.Mobile.ParityTests.csproj)) that compile selected mobile sources with an `extern alias` reference to WPF for [`ImportExportHelpers`](ImportExportHelpers.cs).

**Structured CSV (desktop “full report” format):** WPF [`ImportExportHelpers.ParseStructuredReportCsv`](ImportExportHelpers.cs) and mobile [`MobileStructuredReportCsv.Parse`](CrispyBills.Mobile.Android/Services/MobileStructuredReportCsv.cs) are **largely duplicated** line-for-line (same state machine, same detail-row layout). **No automated test asserts cross-parser equivalence** on shared fixtures; tests cover WPF and mobile paths separately.

**Critical engineering issue (resolved in this review):** ParityTests linked [`BillingService.cs`](CrispyBills.Mobile.Android/Services/BillingService.cs) but not [`MobileStructuredReportCsv.cs`](CrispyBills.Mobile.Android/Services/MobileStructuredReportCsv.cs) or [`YearSummaryHtmlBuilder.cs`](CrispyBills.Mobile.Android/Services/YearSummaryHtmlBuilder.cs), so the test project **did not compile**. [`CrispyBills.Mobile.ParityTests.csproj`](CrispyBills.Mobile.ParityTests/CrispyBills.Mobile.ParityTests.csproj) was updated to include those linked files so the `BillingService` dependency closure matches.

---

## 2. Domain invariants (Bill vs BillItem / YearData)

| Topic | Desktop [`Bill`](Bill.cs) | Mobile [`BillItem`](CrispyBills.Mobile.Android/Models/BillItem.cs) |
|--------|---------------------------|-------------------------------------|
| Identity | `Guid` with change notification | `Guid`; clone for drafts |
| Category default | `"General"` | `"General"` |
| Recurrence | `RecurrenceFrequency`, `RecurrenceEveryMonths`, end mode/date/max | Same fields |
| Past due | `!Paid && (DueDate < Today \|\| DueDate < ContextPeriodStart)` | Same formula |
| Year/month in model | Implicit (stored in `AnnualData` month key) | Explicit `Year`, `Month` on each item |

**[`YearData`](CrispyBills.Mobile.Android/Models/YearData.cs):** months `1..12` always initialized with empty bill lists and zero income.

**Enums:** [`RecurrenceFrequency`](RecurrenceFrequency.cs) / [`RecurrenceEndMode`](RecurrenceEndMode.cs) exist in **both** `CrispyBills` and `CrispyBills.Mobile.Android.Models` with matching numeric values — they must stay in sync manually (duplicate definitions).

**Validation:** [`BillingService.ValidateBill`](CrispyBills.Mobile.Android/Services/BillingService.cs) clamps `RecurrenceEveryMonths` and clears recurrence metadata when not recurring. Desktop [`Bill`](Bill.cs) clamps `RecurrenceEveryMonths` in the property setter; [`BillItem`](CrispyBills.Mobile.Android/Models/BillItem.cs) does not clamp in the setter — inconsistency is mitigated by `ValidateBill` on service entry.

---

## 3. Build and tests (recorded)

| Project | Command | Result |
|---------|---------|--------|
| WPF | `dotnet build CrispyBills.csproj` | Succeeded |
| MAUI (Windows TFM) | `dotnet build CrispyBills.Mobile.Android.csproj -f net9.0-windows10.0.19041.0` | Succeeded |
| ParityTests | `dotnet build CrispyBills.Mobile.ParityTests.csproj` | Succeeded after linking missing sources |
| Tests | `dotnet test CrispyBills.Mobile.ParityTests.csproj` | **14 passed**, 0 failed |

---

## 4. Findings (prioritized)

### Blocker / high

| ID | Severity | Area | Location | Issue | Suggested action |
|----|----------|------|----------|--------|------------------|
| H1 | High | Persistence parity | [`MainWindow.xaml.cs`](MainWindow.xaml.cs) `InitializeDatabase` / inserts vs [`BillingRepository`](CrispyBills.Mobile.Android/Services/BillingRepository.cs) | **Desktop SQLite stores `Bills.Month` and `Bills.Year` as TEXT** (English month name + year string). **Mobile stores `Month` and `Year` as INTEGER.** Same per-year filename pattern (`CrispyBills_{year}.db`) but **schemas are not interchangeable**; copying a DB between desktop and mobile will not work correctly. | Document clearly in user-facing docs; if cross-device migration is a goal, add an explicit import/export path only (not raw DB copy). |
| H2 | High | Engineering | ParityTests (fixed) | Linked `BillingService` without `MobileStructuredReportCsv` / `YearSummaryHtmlBuilder` broke compilation whenever those types are referenced. | **Done:** added linked compile items in [`CrispyBills.Mobile.ParityTests.csproj`](CrispyBills.Mobile.ParityTests/CrispyBills.Mobile.ParityTests.csproj). When adding new types used only by `BillingService`, update the link set or switch to a shared class library. |

### Medium

| ID | Severity | Area | Location | Issue | Suggested action |
|----|----------|------|----------|--------|------------------|
| M1 | Medium | Concurrency | [`BillingService`](CrispyBills.Mobile.Android/Services/BillingService.cs) | `_stateSemaphore` protects mutating async paths, but **read/query APIs** (`GetBills`, `GetIncome`, `GetYearSummary`, `CreateYearSnapshot`, `ExportStructuredCsvAsync`, `BuildYearSummaryHtml`, `Categories`, etc.) run **without** the lock. If anything other than the main thread invokes these while mutations run, torn reads are possible. Parity test [`LoadYearAsync_Concurrent`](CrispyBills.Mobile.ParityTests/BillingServiceReliabilityTests.cs) only checks load. | Prefer serializing all service access on one scheduler, or take a read lock pattern / `ReaderWriterLockSlim` if background work is introduced. |
| M2 | Medium | Concurrency | `SetYearArchivedAsync` | Writes metadata via repository **without** `_stateSemaphore`. | Low practical risk; for consistency, either document as intentionally unsynchronized or await under the same coordination as other meta + state updates. |
| M3 | Medium | Resource / DoS | `ImportStructuredCsvForYearAsync(string csvPath, ...)` | [`File.ReadAllLinesAsync`](CrispyBills.Mobile.Android/Services/BillingService.cs) loads the **entire** file into memory. | For untrusted large files, cap size or stream-parse. |
| M4 | Medium | Parser drift | `ImportExportHelpers` vs `MobileStructuredReportCsv` | Logic is duplicated; **no golden-file test** compares outputs for the same input lines. | Add tests that feed identical `string[] lines` to both parsers and compare bill counts, amounts, and month buckets (see §6). |
| M5 | Medium | Floating point | [`BillingRepository`](CrispyBills.Mobile.Android/Services/BillingRepository.cs) | Amounts stored as `REAL` / `GetDouble` then cast to `decimal`. | Acceptable for typical money ranges; document as known tradeoff or use integer cents if strict precision is required everywhere. |

### Low

| ID | Severity | Area | Location | Issue | Suggested action |
|----|----------|------|----------|--------|------------------|
| L1 | Low | Numeric display | [`YearSummaryHtmlBuilder`](CrispyBills.Mobile.Android/Services/YearSummaryHtmlBuilder.cs) | Utilization uses `double` from `decimal` ratio. | Acceptable for display; avoid using `double` for monetary totals. |
| L2 | Low | Observability | Multiple `catch { }` / best-effort logs | Intentional for startup and logging; failures can be silent to users. | Optional: surface a single “diagnostics available” path on mobile ([`DiagnosticsPage`](CrispyBills.Mobile.Android/DiagnosticsPage.xaml.cs)). |
| L3 | Low | Solution composition | [`CrispyBills.sln`](CrispyBills.sln) | Lists WPF + ParityTests; MAUI project may be opened separately. | Add MAUI project to solution if CI should build all targets in one step. |

### Security (local app, proportionate)

| ID | Severity | Notes |
|----|----------|--------|
| S1 | Low | Structured CSV and HTML report use encoding/escaping where reviewed (`WebUtility.HtmlEncode` in [`YearSummaryHtmlBuilder`](CrispyBills.Mobile.Android/Services/YearSummaryHtmlBuilder.cs)); imports use parameterized SQL on mobile. |
| S2 | Low | [`Android MainApplication`](CrispyBills.Mobile.Android/Platforms/Android/MainApplication.cs) logs unhandled exceptions; does not replace in-app error UI for handled paths. |

---

## 5. Parser parity matrix (desktop vs mobile)

| Artifact | WPF | Mobile | Tests |
|----------|-----|--------|--------|
| `ParseCsvLine` | [`ImportExportHelpers.ParseCsvLine`](ImportExportHelpers.cs) | [`MobileStructuredReportCsv.ParseCsvLine`](CrispyBills.Mobile.Android/Services/MobileStructuredReportCsv.cs) | [`ImportExportHelpersTests`](CrispyBills.Mobile.ParityTests/ImportExportHelpersTests.cs) (WPF only) |
| Desktop structured export (`REPORT`, `===== YEAR =====`, detail rows) | [`ParseStructuredReportCsv`](ImportExportHelpers.cs) | [`MobileStructuredReportCsv.Parse`](CrispyBills.Mobile.Android/Services/MobileStructuredReportCsv.cs) | WPF: `ParseStructuredReportCsv_BasicImportScansCorrectly`; **no mobile mirror** |
| Simplified mobile export (`TYPE,Year,Month,...`) | N/A | [`BillingService.ParseStructuredCsvByYear`](CrispyBills.Mobile.Android/Services/BillingService.cs) | [`BillingServiceReliabilityTests`](CrispyBills.Mobile.ParityTests/BillingServiceReliabilityTests.cs), [`BillingServiceParityTests`](CrispyBills.Mobile.ParityTests/BillingServiceParityTests.cs) |

Month names for the simplified parser use **current culture + invariant** in `TryParseMonthName` — English `"January"` in export must match parsing expectations for non-English cultures (potential **medium** i18n bug if UI culture differs from export language).

---

## 6. `BillingService` concurrency map (summary)

- **Serialized (waits `_stateSemaphore`):** `LoadYearAsync`, `RestoreYearSnapshotAsync`, `SetIncomeAsync`, destructive deletes, `AddBillAsync`, `AddBillsBulkAsync`, `UpdateBillAsync`, `DeleteBillAsync`, `TogglePaidAsync`, `RolloverUnpaidAsync`, structured CSV imports, `CreateNewYearFromDecemberAsync`, `NormalizeDueDatesForCurrentYearAsync`, `ApplyStructuredReportImportAsync`, `RunIntegrityCheckAndRepairAsync`.
- **Unsynchronized reads / exports:** `GetBills`, `GetIncome`, `GetYearSummary`, `GetMonthSummary`, `GetCategoryTotals`, `CreateYearSnapshot`, `ExportStructuredCsvAsync`, `BuildYearSummaryHtml`, `ExportFullReportCsvAsync` (loads via repository), `Categories`, `FindDuplicateRecurringRules`, `GetBillsInCategory`.
- **Metadata without service lock:** `SetYearArchivedAsync` (async meta only).

---

## 7. Repository hygiene

- [`.gitignore`](.gitignore) correctly ignores `bin/`, `obj/`, `*.db-wal`, `*.db-shm`. If `bin`/`obj` appeared in `git status` previously, ensure commits exclude them.
- Duplicate enum files at repo root vs mobile `Models/` — **single source of truth** is not enforced by the compiler; consider one shared project or source generator in a future refactor.

---

## 8. Recommended tests (gaps)

1. **Cross-parser golden file:** Same `string[] lines` → `ImportExportHelpers.ParseStructuredReportCsv` vs `MobileStructuredReportCsv.Parse` → assert equivalent year keys, per-month bill counts, and totals (map `Bill` → `BillItem` for comparison).
2. **Culture / month name:** `TryParseMonthName` with non-English `CurrentCulture` vs exported English month lines.
3. **SQLite schema:** Documented automated test is hard across processes; **manual** checklist: do not copy WPF DB files to mobile paths without migration.
4. **Concurrent read/write:** Optional stress test: interleave `GetBills` with `AddBillAsync` from thread pool (documents expected behavior after any lock change).

---

## 9. Appendix: orchestration notes (`BillingService`)

- **Load path:** `LoadYearAsync` → `LoadYearCoreAsync` → `NormalizeForYear` → `EnsureRecurringCatchUpAsync` (may save).
- **Save path:** `SaveAsync` → `ValidateInvariants` → `IBillingRepository.SaveYearAsync`.
- **Recurring:** Monthly expansion uses shared `Id` across months; weekly uses `RecurrenceGroupId` on child rows. `DeleteBillAsync` removes by group id via `RemoveBillsInGroup`.

---

*Generated as part of the planned code review; not a substitute for security penetration testing or formal verification.*
