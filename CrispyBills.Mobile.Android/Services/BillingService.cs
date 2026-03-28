using CrispyBills.Mobile.Android.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if ANDROID
using AndroidEnvironment = Android.OS.Environment;
#endif

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// High-level orchestration around bill data for the mobile app.
/// Loads/saves year data via an <see cref="IBillingRepository"/>, provides business
/// operations such as creating drafts, adding/updating bills, and generating reports.
/// </summary>
public sealed partial class BillingService(IBillingRepository repository)
{
    private readonly SemaphoreSlim _stateSemaphore = new(1, 1);
    private const string ArchivedYearsMetaKey = "ArchivedYears";
    private const string IncomeProfilesMetaKey = "IncomeProfiles";
    private const string SoonThresholdMetaKey = "SoonThreshold";
    public const string IncomePayPeriodMonthly = "Monthly";
    public const string IncomePayPeriodBiWeekly = "Bi-weekly";
    public const string IncomePayPeriodWeekly = "Weekly";
    public const string SoonThresholdUnitDays = "Days";
    public const string SoonThresholdUnitWeeks = "Weeks";
    public const string SoonThresholdUnitMonths = "Months";
    [GeneratedRegex(@"\s-\s(?:Week|Bi-weekly)\s\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex WeekBasedSuffixRegex();
    private readonly IBillingRepository _repository = repository;

    private YearData _currentData = new();
    // Destructive debug deletes require an explicit per-session toggle (not persisted).
    private bool _debugDestructiveDeletesEnabled = false;

    public int CurrentYear { get; private set; } = DateTime.Today.Year;

    /// <summary>Data folder root (for diagnostics and import parse logs).</summary>
    public string DataRoot => _repository.DataRoot;

    public static IReadOnlyList<string> IncomePayPeriodOptions() =>
        [IncomePayPeriodMonthly, IncomePayPeriodBiWeekly, IncomePayPeriodWeekly];

    public static IReadOnlyList<string> SoonThresholdUnits() =>
        [SoonThresholdUnitDays, SoonThresholdUnitWeeks, SoonThresholdUnitMonths];

    /// <summary>Load data for the specified year into the service and normalize state.</summary>
    /// <param name="year">Year to load.</param>
    public async Task LoadYearAsync(int year)
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            await LoadYearCoreAsync(year);
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Return the list of bills for the specified month, ordered for display.</summary>
    /// <param name="month">Month number (1-12).</param>
    public IReadOnlyList<BillItem> GetBills(int month)
    {
        ValidateMonth(month);
        return
        [
            .. _currentData.BillsByMonth[month]
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Get the configured income for the specified month.</summary>
    /// <param name="month">Month number (1-12).</param>
    public decimal GetIncome(int month)
    {
        ValidateMonth(month);
        return _currentData.IncomeByMonth.TryGetValue(month, out var income) ? income : 0m;
    }

    public async Task<(decimal Amount, string PayPeriod)> GetIncomeEntryAsync(int month)
    {
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var profiles = await LoadIncomeProfilesAsync();
            if (profiles.TryGetValue(BuildIncomeProfileKey(CurrentYear, month), out var stored))
            {
                return (stored.Amount, NormalizeIncomePayPeriod(stored.PayPeriod));
            }

            return (GetIncome(month), IncomePayPeriodMonthly);
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    public async Task<(int Value, string Unit)> GetSoonThresholdAsync()
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            var raw = await _repository.GetAppMetaAsync(SoonThresholdMetaKey);
            var stored = DeserializeJson<SoonThresholdMeta>(raw);
            var value = Math.Clamp(stored?.Value ?? 7, 1, 30);
            var unit = NormalizeSoonThresholdUnit(stored?.Unit);
            return (value, unit);
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    public async Task SetSoonThresholdAsync(int value, string unit)
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            var stored = new SoonThresholdMeta
            {
                Value = Math.Clamp(value, 1, 30),
                Unit = NormalizeSoonThresholdUnit(unit)
            };
            await _repository.SetAppMetaAsync(SoonThresholdMetaKey, JsonSerializer.Serialize(stored));
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    public static bool IsBillSoon(BillItem bill, int value, string unit)
    {
        if (bill.IsPaid || bill.IsPastDue)
        {
            return false;
        }

        var normalizedValue = Math.Clamp(value, 1, 30);
        var normalizedUnit = NormalizeSoonThresholdUnit(unit);
        var today = DateTime.Today;
        var due = bill.DueDate.Date;
        if (due < today)
        {
            return false;
        }

        var thresholdDate = normalizedUnit switch
        {
            SoonThresholdUnitWeeks => today.AddDays(normalizedValue * 7),
            SoonThresholdUnitMonths => today.AddMonths(normalizedValue),
            _ => today.AddDays(normalizedValue)
        };

        return due <= thresholdDate.Date;
    }

    /// <summary>Return the list of years available from the repository.</summary>
    public IReadOnlyList<int> GetAvailableYears()
    {
        return _repository.GetAvailableYears();
    }

    /// <summary>Return archived years stored in app metadata.</summary>
    public async Task<IReadOnlyList<int>> GetArchivedYearsAsync()
    {
        var raw = await _repository.GetAppMetaAsync(ArchivedYearsMetaKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return
        [
            .. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var y) ? y : 0)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
        ];
    }

    /// <summary>Mark or unmark a year as archived in app metadata.</summary>
    public async Task SetYearArchivedAsync(int year, bool archived)
    {
        var current = (await GetArchivedYearsAsync()).ToHashSet();
        if (archived)
        {
            current.Add(year);
        }
        else
        {
            current.Remove(year);
        }

        var packed = string.Join(',', current.OrderBy(x => x));
        await _repository.SetAppMetaAsync(ArchivedYearsMetaKey, packed);
    }

    /// <summary>Find groups of recurring bills that appear duplicated for a month.</summary>
    /// <param name="month">Month number (1-12).</param>
    public IReadOnlyList<IReadOnlyList<BillItem>> FindDuplicateRecurringRules(int month)
    {
        ValidateMonth(month);
        return
        [
            .. _currentData.BillsByMonth[month]
            .Where(x => x.IsRecurring)
            .GroupBy(x => BuildRecurringSignature(x), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<BillItem>)[.. g])
        ];
    }

    /// <summary>Create an in-memory snapshot copy of the currently loaded year data.</summary>
    public YearData CreateYearSnapshot()
    {
        var snapshot = new YearData();
        foreach (var month in Enumerable.Range(1, 12))
        {
            snapshot.BillsByMonth[month] = [.. _currentData.BillsByMonth[month].Select(x => x.Clone())];
            snapshot.IncomeByMonth[month] = _currentData.IncomeByMonth[month];
        }

        return snapshot;
    }

    public BillItem? GetEditableBillDraft(int month, Guid id)
    {
        ValidateMonth(month);
        var existing = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
        if (existing is null)
        {
            return null;
        }

        var draft = existing.Clone();
        if (existing.RecurrenceGroupId is Guid rootId)
        {
            var anchor = FindBillAcrossYear(rootId);
            if (anchor is not null)
            {
                draft.IsRecurring = true;
                draft.RecurrenceFrequency = anchor.RecurrenceFrequency;
                draft.RecurrenceEveryMonths = anchor.RecurrenceEveryMonths;
                draft.RecurrenceEndMode = anchor.RecurrenceEndMode;
                draft.RecurrenceEndDate = anchor.RecurrenceEndDate;
                draft.RecurrenceMaxOccurrences = anchor.RecurrenceMaxOccurrences;
            }
        }

        return draft;
    }

    /// <summary>Restore in-memory state from a previously created snapshot and persist it.</summary>
    public async Task RestoreYearSnapshotAsync(YearData snapshot)
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            _currentData = new YearData();
            foreach (var month in Enumerable.Range(1, 12))
            {
                _currentData.BillsByMonth[month] = [.. snapshot.BillsByMonth[month].Select(x => x.Clone())];
                _currentData.IncomeByMonth[month] = snapshot.IncomeByMonth[month];
            }

            NormalizeForYear(CurrentYear);
            ValidateInvariants();
            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Set income value for the specified month and persist the change.</summary>
    public async Task SetIncomeAsync(int month, decimal income)
    {
        await SetIncomeAsync(month, income, IncomePayPeriodMonthly);
    }

    /// <summary>Set income value using the selected pay period and persist it for the current and future months.</summary>
    public async Task SetIncomeAsync(int month, decimal income, string payPeriod)
    {
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var normalizedPayPeriod = NormalizeIncomePayPeriod(payPeriod);
            var enteredAmount = RoundCurrency(Math.Max(0, income));
            var monthlyAmount = ConvertIncomeToMonthlyAmount(enteredAmount, normalizedPayPeriod);
            var profiles = await LoadIncomeProfilesAsync();
            for (var targetMonth = month; targetMonth <= 12; targetMonth++)
            {
                _currentData.IncomeByMonth[targetMonth] = monthlyAmount;
                profiles[BuildIncomeProfileKey(CurrentYear, targetMonth)] = new IncomeProfileMeta
                {
                    Amount = enteredAmount,
                    PayPeriod = normalizedPayPeriod
                };
            }

            await SaveAsync();
            await SaveIncomeProfilesAsync(profiles);
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Enable or disable session-only destructive debug operations.</summary>
    public void SetDebugDestructiveDeletesEnabled(bool enabled)
    {
        _debugDestructiveDeletesEnabled = enabled;
    }

    /// <summary>Return whether destructive debug operations are currently enabled.</summary>
    public bool IsDebugDestructiveDeletesEnabled() => _debugDestructiveDeletesEnabled;

    /// <summary>
    /// Debug helper: permanently deletes all bills and income for the specified month in the currently loaded year.
    /// Creates a file backup before performing the operation. Requires the debug destructive toggle for the session.
    /// </summary>
    /// <returns>True if deletion completed successfully.</returns>
    public async Task<bool> DeleteMonthAsync(int month)
    {
        ValidateMonth(month);
        if (!_debugDestructiveDeletesEnabled) return false;

        await _stateSemaphore.WaitAsync();
        try
        {
            var dbPath = _repository.GetYearDatabasePath(CurrentYear);
            try
            {
                if (File.Exists(dbPath))
                {
                    var backupDir = Path.Combine(_repository.DataRoot, "db_backups", CurrentYear.ToString());
                    Directory.CreateDirectory(backupDir);
                    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var dest = Path.Combine(backupDir, $"CrispyBills_{CurrentYear}_{timestamp}.db");
                    File.Copy(dbPath, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic("DebugDeleteMonth backup", ex.ToString());
            }

            _currentData.BillsByMonth[month].Clear();
            _currentData.IncomeByMonth[month] = 0m;

            await SaveAsync();
            return true;
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>
    /// Debug helper: permanently deletes the on-disk database and sidecars for the specified year.
    /// Creates a backup before deleting and, if the deleted year was the current year,
    /// attempts to fall back to another available year and load it.
    /// Requires the debug destructive toggle for the session.
    /// </summary>
    /// <param name="year">Year to delete.</param>
    /// <returns>True if the operation completed successfully.</returns>
    public async Task<bool> DeleteYearAsync(int year)
    {
        if (!_debugDestructiveDeletesEnabled) return false;

        await _stateSemaphore.WaitAsync();
        try
        {
            var dbPath = _repository.GetYearDatabasePath(year);
            ReportDiagnostic("DebugDeleteYear", $"Attempting to delete year {year}. DB path: {dbPath}");

            try
            {
                if (File.Exists(dbPath))
                {
                    var backupDir = Path.Combine(_repository.DataRoot, "db_backups", year.ToString());
                    Directory.CreateDirectory(backupDir);
                    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var dest = Path.Combine(backupDir, $"CrispyBills_{year}_{timestamp}.db");
                    File.Copy(dbPath, dest, overwrite: true);
                    ReportDiagnostic("DebugDeleteYear backup", $"Backup created at: {dest}");
                }
                else
                {
                    ReportDiagnostic("DebugDeleteYear backup", $"No DB file to backup at: {dbPath}");
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic("DebugDeleteYear backup", ex.ToString());
            }

            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                    ReportDiagnostic("DebugDeleteYear delete", $"Deleted DB file: {dbPath}");
                }
                else
                {
                    ReportDiagnostic("DebugDeleteYear delete", $"DB file not found for deletion: {dbPath}");
                }
                var wal = dbPath + "-wal";
                var shm = dbPath + "-shm";
                if (File.Exists(wal))
                {
                    File.Delete(wal);
                    ReportDiagnostic("DebugDeleteYear delete", $"Deleted WAL file: {wal}");
                }
                if (File.Exists(shm))
                {
                    File.Delete(shm);
                    ReportDiagnostic("DebugDeleteYear delete", $"Deleted SHM file: {shm}");
                }
                var bak = dbPath + ".prewrite.bak";
                if (File.Exists(bak))
                {
                    File.Delete(bak);
                    ReportDiagnostic("DebugDeleteYear delete", $"Deleted BAK file: {bak}");
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic($"DebugDeleteYear delete files ({year})", ex.ToString());
            }

            if (year == CurrentYear)
            {
                var years = _repository.GetAvailableYears();
                var fallback = years.FirstOrDefault(y => y != year);
                if (fallback == 0)
                {
                    fallback = DateTime.Now.Year;
                }

                await LoadYearCoreAsync(fallback);
            }

            return true;
        }
        catch (Exception ex)
        {
            ReportDiagnostic("DebugDeleteYear", ex.ToString());
            return false;
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Compute a simple financial summary for the loaded year.</summary>
    public (decimal income, decimal expenses, decimal remaining, int billCount) GetYearSummary()
    {
        var income = Enumerable.Range(1, 12).Sum(m => GetIncome(m));
        var expenses = Enumerable.Range(1, 12).SelectMany(m => _currentData.BillsByMonth[m]).Sum(x => x.Amount);
        var billCount = Enumerable.Range(1, 12).Sum(m => _currentData.BillsByMonth[m].Count);
        return (income, expenses, income - expenses, billCount);
    }

    /// <summary>Return aggregated totals per category for a month.</summary>
    public IReadOnlyList<(string category, decimal total)> GetCategoryTotals(int month)
    {
        ValidateMonth(month);
        return _currentData.BillsByMonth[month]
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "General" : x.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (string.IsNullOrWhiteSpace(g.Key) ? "General" : g.Key, g.Sum(x => x.Amount)))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    // Common household categories copied from the desktop app's `BillDialog`.
    private static readonly string[] DefaultCategories =
    [
        "General",
        "Housing",
        "Utilities",
        "Groceries",
        "Transportation",
        "Insurance",
        "Healthcare",
        "Entertainment",
        "Subscriptions",
        "Savings",
        "Debt",
        "Education",
        "Personal Care",
        "Miscellaneous"
    ];

    /// <summary>Add a bill to the specified month, optionally expanding recurring instances.</summary>
    public async Task AddBillAsync(int month, BillItem draft)
    {
        ValidateMonth(month);
        ValidateBill(draft);
        await _stateSemaphore.WaitAsync();
        try
        {
            AddBillCoreWithoutSave(month, draft);
            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Add several bills in one locked save. Skips rows that are completely blank.</summary>
    public async Task AddBillsBulkAsync(int month, IReadOnlyList<BillItem> drafts)
    {
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var any = false;
            foreach (var draft in drafts)
            {
                if (IsBulkRowCompletelyBlank(draft))
                {
                    continue;
                }

                ValidateBill(draft);
                AddBillCoreWithoutSave(month, draft);
                any = true;
            }

            if (any)
            {
                await SaveAsync();
            }
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    private void AddBillCoreWithoutSave(int month, BillItem draft)
    {
        var baseDay = Math.Min(draft.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));
        var baseName = StripWeekBasedSuffix(draft.Name);

        var current = draft.Clone();
        current.Id = draft.IsRecurring
            ? Guid.NewGuid()
            : (draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id);
        current.Year = CurrentYear;
        current.Month = month;
        current.DueDate = NormalizeDueDate(CurrentYear, month, baseDay);
        current.RecurrenceGroupId = null;
        if (draft.IsRecurring)
        {
            current.RecurrenceFrequency = draft.RecurrenceFrequency == RecurrenceFrequency.None
                ? RecurrenceFrequency.MonthlyInterval
                : draft.RecurrenceFrequency;
        }
        else
        {
            current.RecurrenceFrequency = RecurrenceFrequency.None;
        }

        _currentData.BillsByMonth[month].Add(current);

        if (draft.IsRecurring && IsWeekBased(current.RecurrenceFrequency))
        {
            ExpandWeeklySeriesIntoYear(current, CurrentYear, _currentData);
            ApplyWeekBasedSeriesLabels(current.Id, baseName, current.RecurrenceFrequency);
        }
        else if (draft.IsRecurring)
        {
            current.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
            for (var target = month + 1; target <= 12; target++)
            {
                if (!CalendarMonthHasBegunLocally(CurrentYear, target))
                {
                    continue;
                }

                if (!ShouldCreateRecurringOccurrence(current, month, target, CurrentYear))
                {
                    continue;
                }

                var future = current.Clone();
                future.Month = target;
                future.DueDate = NormalizeDueDate(CurrentYear, target, baseDay);
                future.IsPaid = false;
                future.RecurrenceGroupId = null;
                _currentData.BillsByMonth[target].Add(future);
            }
        }
    }

    private static bool IsBulkRowCompletelyBlank(BillItem d)
        => string.IsNullOrWhiteSpace(d.Name)
           && d.Amount == 0
           && !d.IsPaid
           && !d.IsRecurring;

    /// <summary>Update an existing bill by id in the specified month.</summary>
    public async Task UpdateBillAsync(int month, Guid id, BillItem edited)
    {
        ValidateMonth(month);
        ValidateBill(edited);
        await _stateSemaphore.WaitAsync();
        try
        {
            var all = _currentData.BillsByMonth[month];
            var target = all.FirstOrDefault(x => x.Id == id);
            if (target is null)
            {
                return;
            }

            // Editing one row in a weekly/bi-weekly series should affect that occurrence and future ones,
            // while preserving already-completed history.
            if (target.RecurrenceGroupId.HasValue && !target.IsRecurring)
            {
                var originalGroupId = target.RecurrenceGroupId.Value;
                var baseDayChild = Math.Min(edited.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));
                var nextFrequency = edited.IsRecurring
                    ? (edited.RecurrenceFrequency == RecurrenceFrequency.None ? RecurrenceFrequency.MonthlyInterval : edited.RecurrenceFrequency)
                    : RecurrenceFrequency.None;
                var newBaseName = StripWeekBasedSuffix(edited.Name);

                DeactivateWeekBasedAnchor(originalGroupId);
                RemoveWeekBasedOccurrencesFromDate(originalGroupId, target.DueDate.Date);

                var single = edited.Clone();
                single.Id = id;
                single.Year = CurrentYear;
                single.Month = month;
                single.DueDate = NormalizeDueDate(CurrentYear, month, baseDayChild);
                single.RecurrenceGroupId = null;

                if (!edited.IsRecurring)
                {
                    single.IsRecurring = false;
                    single.RecurrenceFrequency = RecurrenceFrequency.None;
                    _currentData.BillsByMonth[month].Add(single);
                    await SaveAsync();
                    return;
                }

                single.IsRecurring = true;
                single.RecurrenceFrequency = nextFrequency;
                _currentData.BillsByMonth[month].Add(single);

                if (IsWeekBased(nextFrequency))
                {
                    ExpandWeeklySeriesIntoYear(single, CurrentYear, _currentData);
                    ApplyWeekBasedSeriesLabels(single.Id, newBaseName, nextFrequency);
                }
                else
                {
                    for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                    {
                        if (!ShouldCreateRecurringOccurrence(single, month, futureMonth, CurrentYear))
                        {
                            continue;
                        }

                        var future = single.Clone();
                        future.Month = futureMonth;
                        future.Year = CurrentYear;
                        future.DueDate = NormalizeDueDate(CurrentYear, futureMonth, baseDayChild);
                        future.IsPaid = false;
                        future.RecurrenceGroupId = null;
                        _currentData.BillsByMonth[futureMonth].Add(future);
                    }
                }

                await SaveAsync();
                return;
            }

            var baseDay = Math.Min(edited.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));
            var wasRecurring = target.IsRecurring;
            var wasWeekBased = wasRecurring && IsWeekBased(target.RecurrenceFrequency);
            var anchorGroupId = target.RecurrenceGroupId ?? target.Id;

            if (wasWeekBased)
            {
                RemoveBillsInGroup(anchorGroupId);
                if (!edited.IsRecurring)
                {
                    var single = edited.Clone();
                    single.Id = id;
                    single.Year = CurrentYear;
                    single.Month = month;
                    single.DueDate = NormalizeDueDate(CurrentYear, month, baseDay);
                    single.IsRecurring = false;
                    single.RecurrenceFrequency = RecurrenceFrequency.None;
                    single.RecurrenceGroupId = null;
                    _currentData.BillsByMonth[month].Add(single);
                    await SaveAsync();
                    return;
                }

                var anchor = edited.Clone();
                anchor.Id = id;
                anchor.Year = CurrentYear;
                anchor.Month = month;
                anchor.DueDate = NormalizeDueDate(CurrentYear, month, baseDay);
                anchor.RecurrenceGroupId = null;
                anchor.IsRecurring = true;
                anchor.RecurrenceFrequency = edited.RecurrenceFrequency == RecurrenceFrequency.None
                    ? RecurrenceFrequency.MonthlyInterval
                    : edited.RecurrenceFrequency;
                _currentData.BillsByMonth[month].Add(anchor);

                if (IsWeekBased(anchor.RecurrenceFrequency))
                {
                    ExpandWeeklySeriesIntoYear(anchor, CurrentYear, _currentData);
                    ApplyWeekBasedSeriesLabels(anchor.Id, StripWeekBasedSuffix(anchor.Name), anchor.RecurrenceFrequency);
                }
                else
                {
                    for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                    {
                        if (!CalendarMonthHasBegunLocally(CurrentYear, futureMonth))
                        {
                            continue;
                        }

                        if (!ShouldCreateRecurringOccurrence(anchor, month, futureMonth, CurrentYear))
                        {
                            continue;
                        }

                        var future = anchor.Clone();
                        future.Month = futureMonth;
                        future.DueDate = NormalizeDueDate(CurrentYear, futureMonth, baseDay);
                        future.IsPaid = false;
                        future.RecurrenceGroupId = null;
                        _currentData.BillsByMonth[futureMonth].Add(future);
                    }
                }

                await SaveAsync();
                return;
            }

            Apply(target, edited, month, baseDay);

            if (edited.IsRecurring && IsWeekBased(edited.RecurrenceFrequency))
            {
                for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                {
                    _currentData.BillsByMonth[futureMonth].RemoveAll(x => x.Id == id);
                }

                ExpandWeeklySeriesIntoYear(target, CurrentYear, _currentData);
                ApplyWeekBasedSeriesLabels(target.Id, StripWeekBasedSuffix(edited.Name), edited.RecurrenceFrequency);
            }
            else if (edited.IsRecurring)
            {
                target.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                {
                    if (!ShouldCreateRecurringOccurrence(edited, month, futureMonth, CurrentYear))
                    {
                        FutureListRemoveById(futureMonth, id);
                        continue;
                    }

                    var futureList = _currentData.BillsByMonth[futureMonth];
                    var existing = futureList.FirstOrDefault(x => x.Id == id);
                    if (existing is null)
                    {
                        if (!CalendarMonthHasBegunLocally(CurrentYear, futureMonth))
                        {
                            continue;
                        }

                        existing = target.Clone();
                        existing.Month = futureMonth;
                        existing.Year = CurrentYear;
                        existing.IsPaid = false;
                        existing.RecurrenceGroupId = null;
                        futureList.Add(existing);
                    }

                    Apply(existing, edited, futureMonth, baseDay);
                    existing.IsPaid = false;
                }
            }
            else if (wasRecurring)
            {
                for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                {
                    _currentData.BillsByMonth[futureMonth].RemoveAll(x => x.Id == id);
                }
            }

            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Delete a bill (and its future recurring instances) by id for a month.</summary>
    public async Task DeleteBillAsync(int month, Guid id)
    {
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var current = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
            if (current is null)
            {
                return;
            }

            if (current.RecurrenceGroupId.HasValue && !current.IsRecurring)
            {
                var recurrenceRootId = current.RecurrenceGroupId.Value;
                DeactivateWeekBasedAnchor(recurrenceRootId);
                RemoveWeekBasedOccurrencesFromDate(recurrenceRootId, current.DueDate.Date);
                await SaveAsync();
                return;
            }

            var rootId = current.RecurrenceGroupId ?? current.Id;
            RemoveBillsInGroup(rootId);

            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Toggle the paid state of a bill and persist the change.</summary>
    public async Task TogglePaidAsync(int month, Guid id)
    {
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var bill = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
            if (bill is null)
            {
                return;
            }

            bill.IsPaid = !bill.IsPaid;
            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Roll unpaid bills from the given month into the following month (debug/maintenance).</summary>
    public async Task RolloverUnpaidAsync(int month)
    {
        ValidateMonth(month);
        if (month >= 12)
        {
            return;
        }

        await _stateSemaphore.WaitAsync();
        try
        {
            var sourceMonthName = MonthNames.Name(month);
            var targetMonth = month + 1;
            var targetList = _currentData.BillsByMonth[targetMonth];

            foreach (var bill in _currentData.BillsByMonth[month].Where(x => !x.IsPaid).ToList())
            {
                if (bill.RecurrenceGroupId.HasValue && !bill.IsRecurring)
                {
                    continue;
                }

                var baseName = StripCarryoverNameSuffix(bill.Name);
                var carryName = $"{baseName} - {sourceMonthName}";
                var duePreserved = bill.DueDate;

                if (bill.IsRecurring && IsWeekBased(bill.RecurrenceFrequency))
                {
                    if (IsCarryoverDuplicate(targetList, carryName, duePreserved, bill.Amount))
                    {
                        continue;
                    }

                    var copy = bill.Clone();
                    copy.Id = Guid.NewGuid();
                    copy.Month = targetMonth;
                    copy.Year = CurrentYear;
                    copy.IsRecurring = false;
                    copy.RecurrenceFrequency = RecurrenceFrequency.None;
                    copy.RecurrenceGroupId = null;
                    copy.IsPaid = false;
                    copy.Name = carryName;
                    copy.DueDate = duePreserved;
                    targetList.Add(copy);
                    continue;
                }

                if (!bill.IsRecurring)
                {
                    if (IsCarryoverDuplicate(targetList, carryName, duePreserved, bill.Amount))
                    {
                        continue;
                    }

                    var rollover = bill.Clone();
                    rollover.Id = Guid.NewGuid();
                    rollover.Month = targetMonth;
                    rollover.Year = CurrentYear;
                    rollover.IsRecurring = false;
                    rollover.RecurrenceFrequency = RecurrenceFrequency.None;
                    rollover.RecurrenceGroupId = null;
                    rollover.IsPaid = false;
                    rollover.Name = carryName;
                    rollover.DueDate = duePreserved;
                    targetList.Add(rollover);
                    continue;
                }

                AddOrReplaceMonthlyRecurringNextMonth(bill, targetMonth, targetList);

                if (IsCarryoverDuplicate(targetList, carryName, duePreserved, bill.Amount))
                {
                    continue;
                }

                var overdueCarry = bill.Clone();
                overdueCarry.Id = Guid.NewGuid();
                overdueCarry.Month = targetMonth;
                overdueCarry.Year = CurrentYear;
                overdueCarry.IsRecurring = false;
                overdueCarry.RecurrenceFrequency = RecurrenceFrequency.None;
                overdueCarry.RecurrenceGroupId = null;
                overdueCarry.IsPaid = false;
                overdueCarry.Name = carryName;
                overdueCarry.DueDate = duePreserved;
                targetList.Add(overdueCarry);
            }

            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    private void AddOrReplaceMonthlyRecurringNextMonth(BillItem bill, int targetMonth, List<BillItem> targetList)
    {
        if (targetList.Any(x => x.Id == bill.Id && x.IsRecurring))
        {
            return;
        }

        var baseDay = Math.Min(bill.DueDate.Day, DateTime.DaysInMonth(CurrentYear, targetMonth));
        var next = bill.Clone();
        next.Month = targetMonth;
        next.Year = CurrentYear;
        next.DueDate = NormalizeDueDate(CurrentYear, targetMonth, baseDay);
        next.IsPaid = false;
        next.RecurrenceGroupId = null;
        next.IsRecurring = true;
        next.RecurrenceFrequency = bill.RecurrenceFrequency == RecurrenceFrequency.None
            ? RecurrenceFrequency.MonthlyInterval
            : bill.RecurrenceFrequency;
        targetList.Add(next);
    }

    private static string StripCarryoverNameSuffix(string name)
    {
        var trimmed = name.Trim();
        foreach (var monthName in MonthNames.All)
        {
            var suffix = $" - {monthName}";
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length].TrimEnd();
            }
        }

        return trimmed;
    }

    private static bool IsCarryoverDuplicate(List<BillItem> targetMonth, string carryName, DateTime dueDate, decimal amount)
    {
        return targetMonth.Any(b =>
            !b.IsRecurring
            && string.Equals(b.Name, carryName, StringComparison.Ordinal)
            && b.DueDate.Date == dueDate.Date
            && b.Amount == amount);
    }

    /// <summary>Import structured CSV data into the given year from a file path.</summary>
    public async Task<ImportStructuredCsvReport> ImportStructuredCsvForYearAsync(string csvPath, int year, bool replaceYear)
    {
        var parsed = ParseStructuredCsvByYear(await File.ReadAllLinesAsync(csvPath));
        if (!parsed.Years.TryGetValue(year, out var importedYear))
        {
            // No data for requested year - treat as no-op for robustness in tests and imports
            return parsed.Report;
        }

        await _stateSemaphore.WaitAsync();
        try
        {
            await ImportStructuredCsvForYearCoreAsync(importedYear, year, replaceYear);
        }
        finally
        {
            _stateSemaphore.Release();
        }

        return parsed.Report;
    }

    // Overload used by tests to import CSV content from lines instead of a file path.
    /// <summary>Import structured CSV data into the given year from an enumerable of lines.</summary>
    public async Task<ImportStructuredCsvReport> ImportStructuredCsvForYearAsync(IEnumerable<string> lines, int year, bool replaceYear)
    {
        var parsed = ParseStructuredCsvByYear(lines);
        if (!parsed.Years.TryGetValue(year, out var importedYear))
        {
            // No data for requested year - treat as a no-op to be robust for different CSV shapes
            return parsed.Report;
        }

        await _stateSemaphore.WaitAsync();
        try
        {
            await ImportStructuredCsvForYearCoreAsync(importedYear, year, replaceYear);
        }
        finally
        {
            _stateSemaphore.Release();
        }

        return parsed.Report;
    }

    private async Task ImportStructuredCsvForYearCoreAsync(YearData importedYear, int year, bool replaceYear)
    {
        var previousYear = CurrentYear;
        var previousData = _currentData;
        var switchedYearContext = false;

        if (year != CurrentYear)
        {
            _currentData = await _repository.LoadYearAsync(year);
            CurrentYear = year;
            switchedYearContext = true;
        }

        try
        {
            if (replaceYear)
            {
                _currentData = importedYear;
            }
            else
            {
                foreach (var month in Enumerable.Range(1, 12))
                {
                    var existing = _currentData.BillsByMonth[month];
                    foreach (var incoming in importedYear.BillsByMonth[month])
                    {
                        var idx = existing.FindIndex(x => x.Id == incoming.Id);
                        if (idx < 0)
                        {
                            idx = existing.FindIndex(x => IsSameBillForMerge(x, incoming));
                        }

                        if (idx >= 0)
                        {
                            existing[idx] = incoming.Clone();
                        }
                        else
                        {
                            existing.Add(incoming.Clone());
                        }
                    }

                    if (importedYear.IncomeByMonth[month] > 0)
                    {
                        _currentData.IncomeByMonth[month] = importedYear.IncomeByMonth[month];
                    }
                }
            }

            NormalizeForYear(year);
            ValidateInvariants();
            await SaveAsync();
        }
        finally
        {
            if (switchedYearContext)
            {
                CurrentYear = previousYear;
                _currentData = previousData;
            }
        }
    }

    /// <summary>Create a new year database from recurring templates found in December.</summary>
    public async Task<bool> CreateNewYearFromDecemberAsync()
    {
        await _stateSemaphore.WaitAsync();
        try
        {
        var newYear = CurrentYear + 1;

        await _repository.InitializeYearAsync(newYear);
        var nextData = await _repository.LoadYearAsync(newYear);

        // Parity with desktop: only block when January already has data (not the whole year).
        if (nextData.BillsByMonth[1].Count > 0 || nextData.IncomeByMonth[1] > 0)
        {
            return false;
        }

        var decemberBills = _currentData.BillsByMonth[12];

        foreach (var recurringTemplate in decemberBills.Where(x => x.IsRecurring))
        {
            if (IsWeekBased(recurringTemplate.RecurrenceFrequency))
            {
                var step = recurringTemplate.RecurrenceFrequency == RecurrenceFrequency.Weekly ? 7 : 14;
                var minDate = new DateTime(newYear, 1, 1);
                var first = FirstWeeklyOccurrenceOnOrAfter(recurringTemplate.DueDate, minDate, step);
                if (first.Year != newYear)
                {
                    continue;
                }

                var anchor = recurringTemplate.Clone();
                anchor.Id = Guid.NewGuid();
                anchor.Year = newYear;
                anchor.Month = first.Month;
                anchor.DueDate = first;
                anchor.IsPaid = false;
                anchor.IsRecurring = true;
                anchor.RecurrenceGroupId = null;
                nextData.BillsByMonth[first.Month].Add(anchor);
                ExpandWeeklySeriesIntoYear(anchor, newYear, nextData);
                ApplyWeekBasedSeriesLabels(nextData, anchor.Id, StripWeekBasedSuffix(recurringTemplate.Name), recurringTemplate.RecurrenceFrequency);

                if (!recurringTemplate.IsPaid)
                {
                    var unpaidCarry = recurringTemplate.Clone();
                    unpaidCarry.Id = Guid.NewGuid();
                    unpaidCarry.Year = newYear;
                    unpaidCarry.Month = 1;
                    unpaidCarry.IsRecurring = false;
                    unpaidCarry.IsPaid = false;
                    unpaidCarry.RecurrenceGroupId = null;
                    unpaidCarry.RecurrenceFrequency = RecurrenceFrequency.None;
                    unpaidCarry.Name = $"{StripCarryoverNameSuffix(recurringTemplate.Name)} - December";
                    unpaidCarry.DueDate = recurringTemplate.DueDate;
                    nextData.BillsByMonth[1].Add(unpaidCarry);
                }

                continue;
            }

            var familyId = Guid.NewGuid();
            for (var month = 1; month <= 12; month++)
            {
                if (!ShouldCreateRecurringOccurrence(recurringTemplate, 1, month, newYear))
                {
                    continue;
                }

                var copy = recurringTemplate.Clone();
                copy.Id = familyId;
                copy.Year = newYear;
                copy.Month = month;
                copy.IsPaid = false;
                copy.RecurrenceGroupId = null;
                copy.DueDate = NormalizeDueDate(newYear, month, recurringTemplate.DueDate.Day);
                nextData.BillsByMonth[month].Add(copy);
            }

            if (!recurringTemplate.IsPaid)
            {
                var unpaidCarry = recurringTemplate.Clone();
                unpaidCarry.Id = Guid.NewGuid();
                unpaidCarry.Year = newYear;
                unpaidCarry.Month = 1;
                unpaidCarry.IsRecurring = false;
                unpaidCarry.IsPaid = false;
                unpaidCarry.RecurrenceGroupId = null;
                unpaidCarry.RecurrenceFrequency = RecurrenceFrequency.None;
                unpaidCarry.Name = $"{StripCarryoverNameSuffix(recurringTemplate.Name)} - December";
                unpaidCarry.DueDate = recurringTemplate.DueDate;
                nextData.BillsByMonth[1].Add(unpaidCarry);
            }
        }

        foreach (var unpaidOneTime in decemberBills.Where(x => !x.IsRecurring && !x.IsPaid))
        {
            var copy = unpaidOneTime.Clone();
            copy.Id = Guid.NewGuid();
            copy.Year = newYear;
            copy.Month = 1;
            copy.IsRecurring = false;
            copy.IsPaid = false;
            copy.Name = $"{StripCarryoverNameSuffix(unpaidOneTime.Name)} - December";
            copy.DueDate = NormalizeDueDate(newYear, 1, unpaidOneTime.DueDate.Day);
            nextData.BillsByMonth[1].Add(copy);
        }

        var decemberIncome = GetIncome(12);
        for (var month = 1; month <= 12; month++)
        {
            nextData.IncomeByMonth[month] = decemberIncome;
        }

        await _repository.SaveYearAsync(newYear, nextData);
        await CarryIncomeProfileToNewYearAsync(newYear, decemberIncome);
        return true;
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Return a summary tuple for a specific month.</summary>
    public (decimal total, decimal paid, decimal unpaid, decimal remaining, int billCount) GetMonthSummary(int month)
    {
        ValidateMonth(month);
        var bills = _currentData.BillsByMonth[month];
        var total = bills.Sum(x => x.Amount);
        var paid = bills.Where(x => x.IsPaid).Sum(x => x.Amount);
        var unpaid = total - paid;
        var remaining = GetIncome(month) - unpaid;
        return (total, paid, unpaid, remaining, bills.Count);
    }

    /// <summary>Return a list of categories merged with built-in defaults.</summary>
    public IReadOnlyList<string> Categories()
    {
        var fromData = _currentData.BillsByMonth.Values
            .SelectMany(x => x)
            .Select(x => x.Category)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim());

        var merged = DefaultCategories
            .Concat(fromData)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        return merged;
    }

    /// <summary>Export the given year's data to a structured CSV file and return its path.</summary>
    public async Task<string> ExportStructuredCsvAsync(int year)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TYPE,Year,Month,Name,Category,Amount,DueDate,Status,Recurring");

        foreach (var month in Enumerable.Range(1, 12))
        {
            sb.AppendLine($"MONTH,{year},{MonthNames.Name(month)},,,,,,");
            foreach (var bill in _currentData.BillsByMonth[month].OrderBy(x => x.DueDate))
            {
                var status = bill.IsPaid ? "Paid" : "Unpaid";
                var recurring = bill.IsRecurring ? "Yes" : "No";
                sb.AppendLine(string.Join(",",
                    Escape("BILL"),
                    Escape(year.ToString(CultureInfo.InvariantCulture)),
                    Escape(MonthNames.Name(month)),
                    Escape(bill.Name),
                    Escape(bill.Category),
                    Escape(bill.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                    Escape(bill.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Escape(status),
                    Escape(recurring)));
            }

            sb.AppendLine($"INCOME,{year},{MonthNames.Name(month)},,,{GetIncome(month).ToString("0.00", CultureInfo.InvariantCulture)},,,");
        }

        var fileName = $"crispybills_export_{year}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var filePath = Path.Combine(_repository.DataRoot, fileName);
        await WriteExportWithPublicCopyAsync(fileName, sb.ToString());
        return filePath;
    }

    /// <summary>Export every stored year plus notes using the same structured CSV shape as the desktop app.</summary>
    public async Task<string> ExportFullReportCsvAsync()
    {
        var result = await ExportFullReportCsvWithLocationsAsync();
        return result.PrivatePath;
    }

    /// <summary>Export full report to app-private storage and copy to public Downloads/CrispyBills when available.</summary>
    public async Task<ExportPathsResult> ExportFullReportCsvWithLocationsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"REPORT,{Escape("Crispy_Bills Export")}");
        sb.AppendLine($"GENERATED AT,{Escape(DateTime.Now.ToString("f", CultureInfo.InvariantCulture))}");
        sb.AppendLine($"EXPORT SCOPE,{Escape("All available years")}");
        sb.AppendLine();

        var years = _repository.GetAvailableYears().OrderBy(y => y).ToList();
        foreach (var year in years)
        {
            var data = await _repository.LoadYearAsync(year);
            var (yearIncome, yearExpenses, yearRemaining) = ComputeYearFinancialTotals(data);

            sb.AppendLine($"===== YEAR =====,{year}");
            sb.AppendLine($"YEAR SUMMARY,Income,{yearIncome.ToString(CultureInfo.InvariantCulture)},Expenses,{yearExpenses.ToString(CultureInfo.InvariantCulture)},Remaining,{yearRemaining.ToString(CultureInfo.InvariantCulture)},Net,{(yearIncome - yearExpenses).ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();

            foreach (var month in Enumerable.Range(1, 12))
            {
                var monthName = StructuredReportMonthNames.All[month - 1];
                var bills = data.BillsByMonth[month]
                    .OrderBy(b => b.DueDate)
                    .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var income = data.IncomeByMonth.GetValueOrDefault(month, 0m);
                var expenses = bills.Sum(b => b.Amount);
                var remaining = bills.Where(b => !b.IsPaid).Sum(b => b.Amount);
                var paid = expenses - remaining;

                sb.AppendLine($"--- MONTH ---,{monthName}");
                sb.AppendLine($"MONTH SUMMARY,Income,{income.ToString(CultureInfo.InvariantCulture)},Expenses,{expenses.ToString(CultureInfo.InvariantCulture)},Paid,{paid.ToString(CultureInfo.InvariantCulture)},Remaining,{remaining.ToString(CultureInfo.InvariantCulture)},Net,{(income - expenses).ToString(CultureInfo.InvariantCulture)},Bill Count,{bills.Count}");
                sb.AppendLine("Name,Category,Amount,Due Date,Status,Recurring,Past Due,Month,Year");

                var contextStart = new DateTime(year, month, 1);
                foreach (var b in bills)
                {
                    var isPastDue = !b.IsPaid && (b.DueDate.Date < DateTime.Today || b.DueDate.Date < contextStart.Date);
                    var status = b.IsPaid ? "PAID" : isPastDue ? "PAST DUE" : "DUE";
                    sb.AppendLine(string.Join(",",
                        Escape(b.Name),
                        Escape(b.Category),
                        b.Amount.ToString(CultureInfo.InvariantCulture),
                        Escape(b.DueDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)),
                        Escape(status),
                        b.IsRecurring ? "Yes" : "No",
                        isPastDue ? "Yes" : "No",
                        Escape(monthName),
                        Escape(year.ToString(CultureInfo.InvariantCulture))));
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        var notesText = await _repository.LoadNotesAsync();
        var noteLines = SplitNotesLines(notesText);
        sb.AppendLine("===== NOTES =====,Global Notes");
        sb.AppendLine($"NOTES SUMMARY,Line Count,{noteLines.Length},Character Count,{notesText.Length}");
        sb.AppendLine("Line Number,Text");
        for (var i = 0; i < noteLines.Length; i++)
        {
            sb.AppendLine($"{i + 1},{Escape(noteLines[i])}");
        }

        var fileName = $"CrispyBills_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        return await WriteExportWithPublicCopyAsync(fileName, sb.ToString());
    }

    private async Task<ExportPathsResult> WriteExportWithPublicCopyAsync(string fileName, string contents)
    {
        Directory.CreateDirectory(_repository.DataRoot);
        var privatePath = Path.Combine(_repository.DataRoot, fileName);
        await File.WriteAllTextAsync(privatePath, contents);

        string? publicPath = null;
        try
        {
            var publicDir = GetPublicExportDirectoryPath();
            if (!string.IsNullOrWhiteSpace(publicDir))
            {
                Directory.CreateDirectory(publicDir);
                publicPath = Path.Combine(publicDir, fileName);
                await File.WriteAllTextAsync(publicPath, contents);
            }
        }
        catch
        {
            // Best effort only. Private export remains the source of truth.
            publicPath = null;
        }

        return new ExportPathsResult(privatePath, publicPath);
    }

    private static (decimal Income, decimal Expenses, decimal Remaining) ComputeYearFinancialTotals(YearData data)
    {
        decimal income = 0m;
        decimal expenses = 0m;
        decimal remaining = 0m;

        for (var month = 1; month <= 12; month++)
        {
            income += data.IncomeByMonth.GetValueOrDefault(month, 0m);

            foreach (var bill in data.BillsByMonth[month])
            {
                expenses += bill.Amount;
                if (!bill.IsPaid)
                {
                    remaining += bill.Amount;
                }
            }
        }

        return (income, expenses, remaining);
    }

    private static string? GetPublicExportDirectoryPath()
    {
#if ANDROID
        var downloads = AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(downloads))
        {
            var externalRoot = AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(externalRoot))
            {
                downloads = Path.Combine(externalRoot, "Download");
            }
        }

        if (string.IsNullOrWhiteSpace(downloads))
        {
            return null;
        }

        return Path.Combine(downloads, "CrispyBills");
#else
        return null;
#endif
    }

    /// <summary>HTML summary for the currently loaded year (for viewing in a browser).</summary>
    public string BuildYearSummaryHtml() =>
        YearSummaryHtmlBuilder.Build(CurrentYear, _currentData, GetIncome);

    /// <summary>Bills in one category for the given month.</summary>
    public IReadOnlyList<BillItem> GetBillsInCategory(int month, string category)
    {
        ValidateMonth(month);
        var key = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        return
        [
            .. _currentData.BillsByMonth[month]
            .Where(b => string.Equals(
                string.IsNullOrWhiteSpace(b.Category) ? "General" : b.Category.Trim(),
                key,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.DueDate)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Normalize due dates to the bill's calendar month (preserves unpaid carryovers before month start).</summary>
    public async Task<int> NormalizeDueDatesForCurrentYearAsync()
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            var year = CurrentYear;
            var changed = 0;
            foreach (var month in Enumerable.Range(1, 12))
            {
                foreach (var bill in _currentData.BillsByMonth[month])
                {
                    var normalized = NormalizeDueDateForMonthContext(bill, year, month);
                    if (bill.DueDate.Date != normalized.Date)
                    {
                        bill.DueDate = normalized;
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                ValidateInvariants();
                await SaveAsync();
            }

            return changed;
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Copies the current year SQLite file into <c>db_backups</c> (routine / background).</summary>
    public async Task BackupCurrentYearDatabaseFileAsync()
    {
        await _stateSemaphore.WaitAsync();
        try
        {
            var year = CurrentYear;
            var src = _repository.GetYearDatabasePath(year);
            if (!File.Exists(src))
            {
                return;
            }

            var backupDir = Path.Combine(_repository.DataRoot, "db_backups", year.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backupDir);
            var dest = Path.Combine(backupDir, $"CrispyBills_{year}_routine_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            await Task.Run(() => File.Copy(src, dest, overwrite: false));
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>Apply a desktop structured CSV package for the selected months per year (replaces those months).</summary>
    public async Task<StructuredReportApplyResult> ApplyStructuredReportImportAsync(
        MobileStructuredImportPackage package,
        IReadOnlyDictionary<string, List<string>> selectedMonthsByYear,
        bool importNotes)
    {
        if (selectedMonthsByYear.Count == 0 && !(importNotes && package.HasNotesSection))
        {
            return new StructuredReportApplyResult();
        }

        var importedBillCount = 0;
        var importedMonthCount = 0;

        await _stateSemaphore.WaitAsync();
        try
        {
            var reloadCurrent = false;
            var currentYear = CurrentYear;

            foreach (var selection in selectedMonthsByYear)
            {
                if (!package.Years.TryGetValue(selection.Key, out var sourceYearData))
                {
                    continue;
                }

                if (!int.TryParse(selection.Key, out var targetYearValue))
                {
                    continue;
                }

                await _repository.InitializeYearAsync(targetYearValue);
                await BackupYearDatabaseFileForImportAsync(targetYearValue);
                var targetData = await _repository.LoadYearAsync(targetYearValue);

                foreach (var monthName in selection.Value.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var monthIndex = StructuredReportMonthNames.IndexOf(monthName);
                    if (monthIndex < 0)
                    {
                        continue;
                    }

                    var monthNum = monthIndex + 1;
                    if (!sourceYearData.BillsByMonth.TryGetValue(monthName, out var sourceBills))
                    {
                        continue;
                    }

                    sourceYearData.IncomeByMonth.TryGetValue(monthName, out var monthIncome);

                    var importedBills = sourceBills.Select(b =>
                    {
                        var c = b.Clone();
                        c.Year = targetYearValue;
                        c.Month = monthNum;
                        c.ContextPeriodStart = new DateTime(targetYearValue, monthNum, 1);
                        c.DueDate = NormalizeDueDate(targetYearValue, monthNum, c.DueDate.Day);
                        c.RecurrenceGroupId = null;
                        return c;
                    }).ToList();

                    targetData.BillsByMonth[monthNum] = importedBills;
                    targetData.IncomeByMonth[monthNum] = Math.Max(0m, monthIncome);
                    importedBillCount += importedBills.Count;
                    importedMonthCount++;
                }

                NormalizeImportedYearData(targetData, targetYearValue);
                await _repository.SaveYearAsync(targetYearValue, targetData);

                if (targetYearValue == currentYear)
                {
                    reloadCurrent = true;
                }
            }

            if (importNotes && package.HasNotesSection)
            {
                var lines = SplitNotesLines(package.NotesText);
                var trimmed = string.Join("\n", lines.Take(500));
                await _repository.SaveNotesAsync(trimmed);
            }

            if (reloadCurrent)
            {
                await LoadYearCoreAsync(currentYear);
            }

            return new StructuredReportApplyResult(importedBillCount, importedMonthCount, importNotes && package.HasNotesSection);
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    private static void NormalizeImportedYearData(YearData data, int year)
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            foreach (var bill in data.BillsByMonth[month])
            {
                bill.Year = year;
                bill.Month = month;
                bill.Category = string.IsNullOrWhiteSpace(bill.Category) ? "General" : bill.Category.Trim();
                bill.Name = string.IsNullOrWhiteSpace(bill.Name) ? "Untitled bill" : bill.Name.Trim();
                bill.Amount = Math.Max(0, bill.Amount);
                bill.DueDate = NormalizeDueDate(year, month, bill.DueDate.Day);
            }

            data.IncomeByMonth[month] = Math.Max(0, data.IncomeByMonth.GetValueOrDefault(month, 0m));
        }
    }

    private async Task BackupYearDatabaseFileForImportAsync(int year)
    {
        var src = _repository.GetYearDatabasePath(year);
        if (!File.Exists(src))
        {
            return;
        }

        var backupDir = Path.Combine(_repository.DataRoot, "db_backups", year.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDir);
        var dest = Path.Combine(backupDir, $"CrispyBills_{year}_preimport_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        await Task.Run(() => File.Copy(src, dest, overwrite: false));
    }

    private static string[] SplitNotesLines(string notesText)
    {
        if (string.IsNullOrEmpty(notesText))
        {
            return [];
        }

        var normalized = notesText.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n', StringSplitOptions.None);
    }

    /// <summary>Load stored notes from the repository.</summary>
    public Task<string> LoadNotesAsync()
    {
        return _repository.LoadNotesAsync();
    }

    /// <summary>Save notes to persistent storage (normalizes line count).</summary>
    public Task SaveNotesAsync(string notes)
    {
        var normalized = string.Join("\n", notes.Split('\n').Take(500));
        return _repository.SaveNotesAsync(normalized);
    }

    /// <summary>Run an integrity scan of the loaded year, repair simple issues, and persist changes.</summary>
    public async Task<IntegrityCheckReport> RunIntegrityCheckAndRepairAsync()
    {
        await _stateSemaphore.WaitAsync();
        try
        {
        var report = new IntegrityCheckReport();
        foreach (var month in Enumerable.Range(1, 12))
        {
            var seen = new HashSet<Guid>();
            foreach (var bill in _currentData.BillsByMonth[month])
            {
                report.BillsScanned++;

                if (string.IsNullOrWhiteSpace(bill.Name))
                {
                    bill.Name = "Untitled bill";
                    report.EmptyNameRepaired++;
                }

                if (bill.Amount < 0)
                {
                    bill.Amount = Math.Abs(bill.Amount);
                    report.NegativeAmountRepaired++;
                }

                if (string.IsNullOrWhiteSpace(bill.Category))
                {
                    bill.Category = "General";
                    report.CategoryRepaired++;
                }

                if (bill.Id == Guid.Empty || !seen.Add(bill.Id))
                {
                    bill.Id = Guid.NewGuid();
                    seen.Add(bill.Id);
                    report.DuplicateIdRepaired++;
                }

                var normalizedDate = NormalizeDueDateForMonthContext(bill, CurrentYear, month);
                if (bill.DueDate.Date != normalizedDate.Date)
                {
                    bill.DueDate = normalizedDate;
                    report.InvalidDateRepaired++;
                }
            }
        }

        await SaveAsync();
        return report;
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    private async Task SaveAsync()
    {
        ValidateInvariants();
        await _repository.SaveYearAsync(CurrentYear, _currentData);
    }

    private async Task<Dictionary<string, IncomeProfileMeta>> LoadIncomeProfilesAsync()
    {
        var raw = await _repository.GetAppMetaAsync(IncomeProfilesMetaKey);
        return DeserializeJson<Dictionary<string, IncomeProfileMeta>>(raw)
            ?? new Dictionary<string, IncomeProfileMeta>(StringComparer.OrdinalIgnoreCase);
    }

    private Task SaveIncomeProfilesAsync(Dictionary<string, IncomeProfileMeta> profiles)
    {
        return _repository.SetAppMetaAsync(IncomeProfilesMetaKey, JsonSerializer.Serialize(profiles));
    }

    private async Task CarryIncomeProfileToNewYearAsync(int newYear, decimal decemberIncome)
    {
        var profiles = await LoadIncomeProfilesAsync();
        if (!profiles.TryGetValue(BuildIncomeProfileKey(CurrentYear, 12), out var decemberProfile))
        {
            decemberProfile = new IncomeProfileMeta
            {
                Amount = decemberIncome,
                PayPeriod = IncomePayPeriodMonthly
            };
        }

        for (var month = 1; month <= 12; month++)
        {
            profiles[BuildIncomeProfileKey(newYear, month)] = new IncomeProfileMeta
            {
                Amount = decemberProfile.Amount,
                PayPeriod = NormalizeIncomePayPeriod(decemberProfile.PayPeriod)
            };
        }

        await SaveIncomeProfilesAsync(profiles);
    }

    private static string BuildIncomeProfileKey(int year, int month) => $"{year:D4}-{month:D2}";

    private static decimal ConvertIncomeToMonthlyAmount(decimal amount, string payPeriod)
    {
        var normalized = NormalizeIncomePayPeriod(payPeriod);
        var monthlyAmount = normalized switch
        {
            IncomePayPeriodWeekly => amount * 52m / 12m,
            IncomePayPeriodBiWeekly => amount * 26m / 12m,
            _ => amount
        };

        return RoundCurrency(monthlyAmount);
    }

    private static string NormalizeIncomePayPeriod(string? payPeriod)
    {
        if (string.Equals(payPeriod, IncomePayPeriodWeekly, StringComparison.OrdinalIgnoreCase))
        {
            return IncomePayPeriodWeekly;
        }

        if (string.Equals(payPeriod, IncomePayPeriodBiWeekly, StringComparison.OrdinalIgnoreCase))
        {
            return IncomePayPeriodBiWeekly;
        }

        return IncomePayPeriodMonthly;
    }

    private static string NormalizeSoonThresholdUnit(string? unit)
    {
        if (string.Equals(unit, SoonThresholdUnitWeeks, StringComparison.OrdinalIgnoreCase))
        {
            return SoonThresholdUnitWeeks;
        }

        if (string.Equals(unit, SoonThresholdUnitMonths, StringComparison.OrdinalIgnoreCase))
        {
            return SoonThresholdUnitMonths;
        }

        return SoonThresholdUnitDays;
    }

    private static decimal RoundCurrency(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private BillItem? FindBillAcrossYear(Guid id)
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            var bill = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
            if (bill is not null)
            {
                return bill;
            }
        }

        return null;
    }

    private void DeactivateWeekBasedAnchor(Guid rootId)
    {
        var anchor = FindBillAcrossYear(rootId);
        if (anchor is null)
        {
            return;
        }

        anchor.IsRecurring = false;
        anchor.RecurrenceFrequency = RecurrenceFrequency.None;
        anchor.RecurrenceEndMode = RecurrenceEndMode.None;
        anchor.RecurrenceEndDate = null;
        anchor.RecurrenceMaxOccurrences = null;
        anchor.RecurrenceGroupId = null;
    }

    private void RemoveWeekBasedOccurrencesFromDate(Guid rootId, DateTime fromDate)
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            _currentData.BillsByMonth[month].RemoveAll(x =>
                (x.Id == rootId || x.RecurrenceGroupId == rootId)
                && x.DueDate.Date >= fromDate.Date);
        }
    }

    private void ApplyWeekBasedSeriesLabels(Guid rootId, string baseName, RecurrenceFrequency frequency)
        => ApplyWeekBasedSeriesLabels(_currentData, rootId, baseName, frequency);

    private static void ApplyWeekBasedSeriesLabels(YearData data, Guid rootId, string baseName, RecurrenceFrequency frequency)
    {
        var normalizedBaseName = StripWeekBasedSuffix(baseName);
        foreach (var month in Enumerable.Range(1, 12))
        {
            var occurrences = data.BillsByMonth[month]
                .Where(x => x.Id == rootId || x.RecurrenceGroupId == rootId)
                .OrderBy(x => x.DueDate)
                .ThenBy(x => x.Id)
                .ToList();

            for (var index = 0; index < occurrences.Count; index++)
            {
                occurrences[index].Name = BuildWeekBasedOccurrenceName(normalizedBaseName, frequency, index + 1);
            }
        }
    }

    private static string StripWeekBasedSuffix(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? "Untitled bill" : name.Trim();
        return WeekBasedSuffixRegex().Replace(normalized, string.Empty);
    }

    private static string BuildWeekBasedOccurrenceName(string baseName, RecurrenceFrequency frequency, int occurrenceIndex)
    {
        var normalizedBaseName = StripWeekBasedSuffix(baseName);
        var suffix = frequency == RecurrenceFrequency.BiWeekly ? "Bi-weekly" : "Week";
        return $"{normalizedBaseName} - {suffix} {occurrenceIndex}";
    }

    private static T? DeserializeJson<T>(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch
        {
            return default;
        }
    }

    private void ReportDiagnostic(string area, string message)
    {
        try
        {
            var root = _repository?.DataRoot ?? Path.GetTempPath();
            var file = Path.Combine(root, "debug_issues.log");
            File.AppendAllText(file, $"[{DateTime.Now:O}] {area}: {message}{Environment.NewLine}");
        }
        catch
        {
            // best-effort diagnostics only
        }
    }

    private async Task EnsureRecurringCatchUpAsync()
    {
        var changed = false;
        foreach (var month in Enumerable.Range(1, 12))
        {
            foreach (var recurring in _currentData.BillsByMonth[month].Where(x => x.IsRecurring).ToList())
            {
                if (IsWeekBased(recurring.RecurrenceFrequency))
                {
                    if (ExpandWeeklySeriesIntoYear(recurring, CurrentYear, _currentData) > 0)
                    {
                        changed = true;
                    }

                    ApplyWeekBasedSeriesLabels(recurring.Id, StripWeekBasedSuffix(recurring.Name), recurring.RecurrenceFrequency);

                    continue;
                }

                var dueDay = recurring.DueDate.Day;
                for (var targetMonth = month + 1; targetMonth <= 12; targetMonth++)
                {
                    if (!CalendarMonthHasBegunLocally(CurrentYear, targetMonth))
                    {
                        continue;
                    }

                    if (!ShouldCreateRecurringOccurrence(recurring, month, targetMonth, CurrentYear))
                    {
                        continue;
                    }

                    var existing = _currentData.BillsByMonth[targetMonth].FirstOrDefault(x => x.Id == recurring.Id);
                    if (existing is null)
                    {
                        var copy = recurring.Clone();
                        copy.Month = targetMonth;
                        copy.Year = CurrentYear;
                        copy.IsPaid = false;
                        copy.DueDate = NormalizeDueDate(CurrentYear, targetMonth, dueDay);
                        copy.RecurrenceGroupId = null;
                        _currentData.BillsByMonth[targetMonth].Add(copy);
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            await SaveAsync();
        }
    }

    private void ValidateInvariants()
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            var seenIds = new HashSet<Guid>();
            for (var i = _currentData.BillsByMonth[month].Count - 1; i >= 0; i--)
            {
                var bill = _currentData.BillsByMonth[month][i];
                bill.Year = CurrentYear;
                bill.Month = month;
                bill.Category = string.IsNullOrWhiteSpace(bill.Category) ? "General" : bill.Category.Trim();
                bill.Name = string.IsNullOrWhiteSpace(bill.Name) ? "Untitled bill" : bill.Name.Trim();
                bill.Amount = Math.Max(0, bill.Amount);
                bill.DueDate = NormalizeDueDateForMonthContext(bill, CurrentYear, month);
                if (bill.Id == Guid.Empty)
                {
                    bill.Id = Guid.NewGuid();
                }

                if (!seenIds.Add(bill.Id))
                {
                    bill.Id = Guid.NewGuid();
                    seenIds.Add(bill.Id);
                }
            }

            _currentData.IncomeByMonth[month] = Math.Max(0, _currentData.IncomeByMonth[month]);
        }
    }

    private static StructuredCsvParseResult ParseStructuredCsvByYear(IEnumerable<string> lines)
    {
        var result = new Dictionary<int, YearData>();
        var report = new ImportStructuredCsvReport();
        foreach (var line in lines)
        {
            report.RowsSeen++;
            if (string.IsNullOrWhiteSpace(line))
            {
                report.SkippedMalformed++;
                continue;
            }

            var cells = SplitCsvLine(line);
            if (cells.Count < 3)
            {
                report.SkippedMalformed++;
                continue;
            }

            var type = cells[0].Trim();
            if (type.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(cells[1], out var year))
            {
                report.SkippedInvalidYear++;
                continue;
            }

            if (!result.TryGetValue(year, out var yearData))
            {
                yearData = new();
                result[year] = yearData;
            }

            var monthName = cells[2].Trim();
            var month = TryParseMonthName(monthName);
            if (month < 1 || month > 12)
            {
                report.SkippedInvalidMonth++;
                continue;
            }

            if (type.Equals("INCOME", StringComparison.OrdinalIgnoreCase))
            {
                if (cells.Count > 5 && decimal.TryParse(cells[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var income))
                {
                    yearData.IncomeByMonth[month] = Math.Max(0, income);
                    report.ImportedIncomeRows++;
                }
                else
                {
                    report.SkippedMalformed++;
                }

                continue;
            }

            if (!type.Equals("BILL", StringComparison.OrdinalIgnoreCase))
            {
                report.SkippedMalformed++;
                continue;
            }

            var name = cells.ElementAtOrDefault(3) ?? string.Empty;
            var category = cells.ElementAtOrDefault(4) ?? "General";
            var amountText = cells.ElementAtOrDefault(5) ?? "0";
            var dueText = cells.ElementAtOrDefault(6) ?? string.Empty;
            var status = cells.ElementAtOrDefault(7) ?? "Unpaid";
            var recurringText = cells.ElementAtOrDefault(8) ?? "No";

            if (!decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                amount = 0;
            }

            if (!DateTime.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
            {
                dueDate = new DateTime(year, month, 1);
            }

            var isRecurring = recurringText.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            BillItem bill = new()
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                Amount = Math.Max(0, amount),
                DueDate = dueDate,
                IsPaid = status.Equals("Paid", StringComparison.OrdinalIgnoreCase),
                IsRecurring = isRecurring,
                RecurrenceFrequency = isRecurring ? RecurrenceFrequency.MonthlyInterval : RecurrenceFrequency.None,
                Year = year,
                Month = month
            };
            yearData.BillsByMonth[month].Add(bill);
            report.ImportedBillRows++;
        }

        return new StructuredCsvParseResult(result, report);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }

    private static void ValidateBill(BillItem bill)
    {
        if (string.IsNullOrWhiteSpace(bill.Name))
        {
            throw new InvalidOperationException("Bill name is required.");
        }

        if (bill.Amount < 0)
        {
            throw new InvalidOperationException("Amount cannot be negative.");
        }

        if (bill.DueDate == default)
        {
            throw new InvalidOperationException("Please choose a due date.");
        }

        if (string.IsNullOrWhiteSpace(bill.Category))
        {
            bill.Category = "General";
        }

        bill.RecurrenceEveryMonths = Math.Max(1, bill.RecurrenceEveryMonths);
        if (!bill.IsRecurring)
        {
            bill.RecurrenceFrequency = RecurrenceFrequency.None;
            bill.RecurrenceEndMode = RecurrenceEndMode.None;
            bill.RecurrenceEndDate = null;
            bill.RecurrenceMaxOccurrences = null;
        }
        else if (bill.RecurrenceFrequency == RecurrenceFrequency.None)
        {
            bill.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
        }
        else if (bill.RecurrenceEndMode == RecurrenceEndMode.EndAfterOccurrences)
        {
            bill.RecurrenceMaxOccurrences = Math.Max(1, bill.RecurrenceMaxOccurrences ?? 1);
        }
    }

    private static DateTime NormalizeDueDate(int year, int month, int requestedDay)
    {
        var maxDay = DateTime.DaysInMonth(year, month);
        var day = Math.Clamp(requestedDay, 1, maxDay);
        return new DateTime(year, month, day);
    }

    /// <summary>
    /// True when the local calendar date is on or after the first day of <paramref name="month"/> in <paramref name="year"/>.
    /// Used to avoid materializing recurring bill rows for months that have not started yet (import/load catch-up).
    /// </summary>
    private static bool CalendarMonthHasBegunLocally(int year, int month)
    {
        return DateTime.Today >= new DateTime(year, month, 1);
    }

    private static void Apply(BillItem target, BillItem source, int month, int baseDay)
    {
        target.Name = source.Name.Trim();
        target.Amount = source.Amount;
        target.Category = source.Category.Trim();
        target.IsRecurring = source.IsRecurring;
        target.RecurrenceFrequency = source.IsRecurring
            ? (source.RecurrenceFrequency == RecurrenceFrequency.None
                ? RecurrenceFrequency.MonthlyInterval
                : source.RecurrenceFrequency)
            : RecurrenceFrequency.None;
        target.RecurrenceEveryMonths = Math.Max(1, source.RecurrenceEveryMonths);
        target.RecurrenceEndMode = source.RecurrenceEndMode;
        target.RecurrenceEndDate = source.RecurrenceEndDate;
        target.RecurrenceMaxOccurrences = source.RecurrenceMaxOccurrences;
        target.IsPaid = source.IsPaid;
        target.Month = month;
        target.DueDate = NormalizeDueDate(target.Year, month, baseDay);
    }

    private void NormalizeForYear(int year)
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            foreach (var bill in _currentData.BillsByMonth[month])
            {
                bill.Month = month;
                bill.Year = year;
                bill.DueDate = NormalizeDueDateForMonthContext(bill, year, month);
            }
        }
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private static bool ShouldCreateRecurringOccurrence(BillItem recurring, int startMonth, int targetMonth, int targetYear)
    {
        if (!recurring.IsRecurring || targetMonth < startMonth || IsWeekBased(recurring.RecurrenceFrequency))
        {
            return false;
        }

        var every = Math.Max(1, recurring.RecurrenceEveryMonths);
        var offset = targetMonth - startMonth;
        if (offset % every != 0)
        {
            return false;
        }

        var occurrenceIndex = (offset / every) + 1;
        if (recurring.RecurrenceEndMode == RecurrenceEndMode.EndAfterOccurrences
            && recurring.RecurrenceMaxOccurrences is int maxOccurrences
            && occurrenceIndex > maxOccurrences)
        {
            return false;
        }

        if (recurring.RecurrenceEndMode == RecurrenceEndMode.EndOnDate
            && recurring.RecurrenceEndDate is DateTime endDate)
        {
            var occurrenceDate = NormalizeDueDate(targetYear, targetMonth, recurring.DueDate.Day);
            if (occurrenceDate.Date > endDate.Date)
            {
                return false;
            }
        }

        return true;
    }

    private void FutureListRemoveById(int month, Guid id)
    {
        _currentData.BillsByMonth[month].RemoveAll(x => x.Id == id);
    }

    private static bool IsWeekBased(RecurrenceFrequency frequency)
        => frequency is RecurrenceFrequency.Weekly or RecurrenceFrequency.BiWeekly;

    private void RemoveBillsInGroup(Guid rootId)
    {
        foreach (var m in Enumerable.Range(1, 12))
        {
            _currentData.BillsByMonth[m].RemoveAll(x => x.Id == rootId || x.RecurrenceGroupId == rootId);
        }
    }

    /// <summary>Adds weekly/bi-weekly child occurrences for the rest of <paramref name="year"/> after <paramref name="anchor"/> (anchor is already stored).</summary>
    /// <returns>Number of new rows added.</returns>
    private static int ExpandWeeklySeriesIntoYear(BillItem anchor, int year, YearData data)
    {
        if (!anchor.IsRecurring || !IsWeekBased(anchor.RecurrenceFrequency))
        {
            return 0;
        }

        var groupId = anchor.Id;
        var step = anchor.RecurrenceFrequency == RecurrenceFrequency.Weekly ? 7 : 14;
        var d = anchor.DueDate.Date;
        var occurrenceIndex = 1;
        var added = 0;
        while (true)
        {
            d = d.AddDays(step);
            occurrenceIndex++;
            if (d.Year != year)
            {
                break;
            }

            if (!RecurrenceOccurrenceAllowed(anchor, d, occurrenceIndex))
            {
                break;
            }

            var m = d.Month;
            if (!CalendarMonthHasBegunLocally(year, m))
            {
                continue;
            }

            if (HasSeriesOccurrenceOnDate(m, groupId, d, data))
            {
                continue;
            }

            var child = anchor.Clone();
            child.Id = Guid.NewGuid();
            child.Month = m;
            child.Year = year;
            child.DueDate = d;
            child.IsPaid = false;
            child.IsRecurring = false;
            child.RecurrenceGroupId = groupId;
            child.RecurrenceFrequency = RecurrenceFrequency.None;
            data.BillsByMonth[m].Add(child);
            added++;
        }

        return added;
    }

    private static bool HasSeriesOccurrenceOnDate(int month, Guid groupId, DateTime dueDate, YearData data)
    {
        return data.BillsByMonth[month].Any(x =>
            (x.Id == groupId || x.RecurrenceGroupId == groupId) && x.DueDate.Date == dueDate.Date);
    }

    private static bool RecurrenceOccurrenceAllowed(BillItem anchor, DateTime occurrenceDate, int occurrenceIndex)
    {
        if (anchor.RecurrenceEndMode == RecurrenceEndMode.EndAfterOccurrences
            && anchor.RecurrenceMaxOccurrences is int maxOccurrences
            && occurrenceIndex > maxOccurrences)
        {
            return false;
        }

        if (anchor.RecurrenceEndMode == RecurrenceEndMode.EndOnDate
            && anchor.RecurrenceEndDate is DateTime endDate
            && occurrenceDate.Date > endDate.Date)
        {
            return false;
        }

        return true;
    }

    private static DateTime FirstWeeklyOccurrenceOnOrAfter(DateTime seriesStartDue, DateTime minInclusive, int stepDays)
    {
        var d = seriesStartDue.Date;
        var guard = 0;
        while (d < minInclusive.Date && guard++ < 400)
        {
            d = d.AddDays(stepDays);
        }

        return d;
    }

    private static string BuildRecurringSignature(BillItem bill)
    {
        return string.Join("|",
            bill.Name.Trim().ToLowerInvariant(),
            bill.Category.Trim().ToLowerInvariant(),
            bill.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            bill.DueDate.Day.ToString(CultureInfo.InvariantCulture),
            bill.RecurrenceFrequency.ToString(),
            bill.RecurrenceEveryMonths.ToString(CultureInfo.InvariantCulture),
            bill.RecurrenceEndMode.ToString(),
            bill.RecurrenceEndDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            bill.RecurrenceMaxOccurrences?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool IsSameBillForMerge(BillItem a, BillItem b)
    {
        return a.Month == b.Month
            && string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Category.Trim(), b.Category.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.Amount == b.Amount
            && a.IsRecurring == b.IsRecurring
            && a.DueDate.Date == b.DueDate.Date;
    }

    private static int TryParseMonthName(string monthName)
    {
        var current = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Take(12).ToArray();
        var invariant = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

        var idx = Array.FindIndex(current, m => m.Equals(monthName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            return idx + 1;
        }

        idx = Array.FindIndex(invariant, m => m.Equals(monthName, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx + 1 : -1;
    }

    private static DateTime NormalizeDueDateForMonthContext(BillItem bill, int targetYear, int targetMonth)
    {
        var firstOfTargetMonth = new DateTime(targetYear, targetMonth, 1);
        if (!bill.IsPaid && bill.DueDate.Date < firstOfTargetMonth)
        {
            return bill.DueDate.Date;
        }

        var day = Math.Min(bill.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
        return new DateTime(targetYear, targetMonth, day);
    }

    private static void ValidateMonth(int month)
    {
        if (month < 1 || month > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");
        }
    }

    private async Task LoadYearCoreAsync(int year)
    {
        CurrentYear = year;
        _currentData = await _repository.LoadYearAsync(year);
        NormalizeForYear(CurrentYear);
        await EnsureRecurringCatchUpAsync();
    }

    private sealed class IncomeProfileMeta
    {
        public decimal Amount { get; set; }
        public string PayPeriod { get; set; } = IncomePayPeriodMonthly;
    }

    private sealed class SoonThresholdMeta
    {
        public int Value { get; set; } = 7;
        public string Unit { get; set; } = SoonThresholdUnitDays;
    }

    private readonly record struct StructuredCsvParseResult(
        Dictionary<int, YearData> Years,
        ImportStructuredCsvReport Report);
}

public sealed class ImportStructuredCsvReport
{
    public int RowsSeen { get; set; }
    public int ImportedBillRows { get; set; }
    public int ImportedIncomeRows { get; set; }
    public int SkippedMalformed { get; set; }
    public int SkippedInvalidMonth { get; set; }
    public int SkippedInvalidYear { get; set; }
}

public sealed record StructuredReportApplyResult(int ImportedBillCount = 0, int ImportedMonthCount = 0, bool NotesImported = false);
public sealed record ExportPathsResult(string PrivatePath, string? PublicPath);
