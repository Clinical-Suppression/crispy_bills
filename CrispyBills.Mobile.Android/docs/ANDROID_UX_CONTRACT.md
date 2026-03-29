# Android UX contract (repair baseline)

This document captures the expected Android behavior for Crispy Bills mobile. Treat contradictions as bugs.

## Main (home)

- Single vertical scroll: header, filters, bill list; bottom Summary / Notes / Settings / Help stays pinned.
- **Swipe gestures (MAUI `SwipeView`):** finger moves **left** → `RightItems` (Delete); finger moves **right** → `LeftItems` (Paid / Unpaid). Copy and layout must stay aligned with this. Bill rows use **`SwipeView.Threshold` = 300** (MAUI device-independent units, not raw screen pixels): minimum horizontal distance before paid/delete **`Execute`** actions run. A higher threshold plus correct tap placement reduces accidental swipe vs vertical scroll and double-tap.
- **Double-tap** a bill row on the **swipe content** (inner padded `Border` inside the `SwipeView`, not the outer card chrome) to open the bill editor. Placing the `TapGestureRecognizer` on a parent `Border` around the `SwipeView` caused Android to arbitrate parent tap vs child pan poorly, so swipe often won over double-tap; the inner placement matches the conventional MAUI pattern. **Double-tap** the income card to edit income when set; **single-tap** when income is empty opens the income entry flow.
- Light and dark themes: readable text, no black system bars, sufficient contrast on cards and semantic colors (including danger/success). Bill row status tints use `AppThemeHelper.IsEffectiveDarkTheme()` so **Unspecified** (follow system) matches the visible UI after cold start and theme changes.
- **Empty states:** distinguish “no bills this month” from “bills exist but filters/search hide them.”

## Navigation

- `MainPage`: custom chrome; **Shell navigation bar hidden** for this page only.
- Pushed pages (Summary, Notes, Settings, editors, drill-downs): **visible title and system/toolbar back** where applicable.
- Modal flows (unlock, PIN setup, import picker): predictable completion; no hung awaits from biometrics or secure storage.

## Security

- Biometric: failed attempts that keep the system prompt open must not strand the UI; terminal outcomes complete the auth task (`OnAuthenticationError`, negative button). Recoverable failures are logged, not forced-complete.
- PIN / secure storage: failed writes are **logged**; user-visible feedback when a feature would otherwise appear to succeed.

## Data

- First navigation to dependent screens should not run against a half-loaded year; initial load is **serialized** before app-lock modals and before pushing data-heavy pages from cold start.
- DB file backup/copy should not race active writes; coordinate with the same lock used for year load/save.
- **Billing invariants (`BillingService`):** monthly recurring copies share the same `Id` across months; weekly/bi-weekly uses an anchor `Id` and child rows with new `Guid` + `RecurrenceGroupId`. `EnsureRecurringCatchUpAsync` fills missing future months per `ShouldCreateRecurringOccurrence`. Current calendar year loads may run chained unpaid carryover (`EnsureAutomaticMonthBoundaryCarryoversUnlocked`) using the same rules as debug rollover; carry rows are non-recurring and deduped by `IsCarryoverDuplicate`. Debug **delete year** with no remaining DBs must not recreate the deleted year via `LoadYearCoreAsync` (use empty in-memory state). Debug **delete month** strips forward monthly copies by `Id` and forward weekly groups via `RemoveForwardRecurringInstancesForDeletedMonth`. Do not change these without parity tests.

## Secondary pages

- Settings: destructive actions look destructive (month delete parity with year delete when enabled).
- Summary: currency in UI matches `LocalizationService` (no hardcoded `$`).
- Bill editor: explicit confirm before discarding **dirty** changes on Android back where possible.

## Verification

- After changes: build `net9.0-android` and smoke-test both themes (startup, lock, bills, filters, summary, notes, settings).
