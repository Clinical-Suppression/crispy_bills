using CrispyBills.Mobile.Android.Models;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// High-level orchestration around bill data for the mobile app.
/// Loads/saves year data via an <see cref="IBillingRepository"/>, provides business
/// operations such as creating drafts, adding/updating bills, and generating reports.
/// </summary>
public sealed class BillingService
{
    private readonly SemaphoreSlim _stateSemaphore = new(1, 1);
    private const string ArchivedYearsMetaKey = "ArchivedYears";
    private readonly IBillingRepository _repository;

    private YearData _currentData = new();
    // Destructive debug deletes require an explicit per-session toggle (not persisted).
    private bool _debugDestructiveDeletesEnabled = false;

    public BillingService(IBillingRepository repository)
    {
        _repository = repository;
    }

    public int CurrentYear { get; private set; } = DateTime.Today.Year;

    /// <summary>Data folder root (for diagnostics and import parse logs).</summary>
    public string DataRoot => _repository.DataRoot;

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
        return _currentData.BillsByMonth[month]
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Get the configured income for the specified month.</summary>
    /// <param name="month">Month number (1-12).</param>
    public decimal GetIncome(int month)
    {
        ValidateMonth(month);
        return _currentData.IncomeByMonth.TryGetValue(month, out var income) ? income : 0m;
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
            return Array.Empty<int>();
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var y) ? y : 0)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
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
        return _currentData.BillsByMonth[month]
            .Where(x => x.IsRecurring)
            .GroupBy(x => BuildRecurringSignature(x), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<BillItem>)g.ToList())
            .ToList();
    }

    /// <summary>Create an in-memory snapshot copy of the currently loaded year data.</summary>
    public YearData CreateYearSnapshot()
    {
        var snapshot = new YearData();
        foreach (var month in Enumerable.Range(1, 12))
        {
            snapshot.BillsByMonth[month] = _currentData.BillsByMonth[month].Select(x => x.Clone()).ToList();
            snapshot.IncomeByMonth[month] = _currentData.IncomeByMonth[month];
        }

        return snapshot;
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
                _currentData.BillsByMonth[month] = snapshot.BillsByMonth[month].Select(x => x.Clone()).ToList();
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
        ValidateMonth(month);
        await _stateSemaphore.WaitAsync();
        try
        {
            var clamped = Math.Max(0, income);
            for (var targetMonth = month; targetMonth <= 12; targetMonth++)
            {
                _currentData.IncomeByMonth[targetMonth] = clamped;
            }

            await SaveAsync();
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
    private static readonly string[] DefaultCategories = new[]
    {
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
    };

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

        var current = draft.Clone();
        current.Id = draft.IsRecurring
            ? Guid.NewGuid()
            : (draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id);
        current.Year = CurrentYear;
        current.Month = month;
        current.DueDate = NormalizeDueDate(CurrentYear, month, baseDay, draft.DueDate);
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
            ExpandWeeklySeriesIntoYear(current, month, CurrentYear, _currentData);
        }
        else if (draft.IsRecurring)
        {
            current.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
            for (var target = month + 1; target <= 12; target++)
            {
                if (!ShouldCreateRecurringOccurrence(current, month, target, CurrentYear))
                {
                    continue;
                }

                var future = current.Clone();
                future.Month = target;
                future.DueDate = NormalizeDueDate(CurrentYear, target, baseDay, draft.DueDate);
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

            // One row in a weekly/bi-weekly series (non-anchor): update locally only.
            if (target.RecurrenceGroupId.HasValue && !target.IsRecurring)
            {
                var baseDayChild = Math.Min(edited.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));
                Apply(target, edited, month, baseDayChild);
                target.IsRecurring = false;
                target.RecurrenceFrequency = RecurrenceFrequency.None;
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
                    single.DueDate = NormalizeDueDate(CurrentYear, month, baseDay, edited.DueDate);
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
                anchor.DueDate = NormalizeDueDate(CurrentYear, month, baseDay, edited.DueDate);
                anchor.RecurrenceGroupId = null;
                anchor.IsRecurring = true;
                anchor.RecurrenceFrequency = edited.RecurrenceFrequency == RecurrenceFrequency.None
                    ? RecurrenceFrequency.MonthlyInterval
                    : edited.RecurrenceFrequency;
                _currentData.BillsByMonth[month].Add(anchor);

                if (IsWeekBased(anchor.RecurrenceFrequency))
                {
                    ExpandWeeklySeriesIntoYear(anchor, month, CurrentYear, _currentData);
                }
                else
                {
                    for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                    {
                        if (!ShouldCreateRecurringOccurrence(anchor, month, futureMonth, CurrentYear))
                        {
                            continue;
                        }

                        var future = anchor.Clone();
                        future.Month = futureMonth;
                        future.DueDate = NormalizeDueDate(CurrentYear, futureMonth, baseDay, edited.DueDate);
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

                ExpandWeeklySeriesIntoYear(target, month, CurrentYear, _currentData);
            }
            else if (edited.IsRecurring)
            {
                target.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
                {
                    if (!ShouldCreateRecurringOccurrence(edited, month, futureMonth, CurrentYear))
                    {
                        futureListRemoveById(futureMonth, id);
                        continue;
                    }

                    var futureList = _currentData.BillsByMonth[futureMonth];
                    var existing = futureList.FirstOrDefault(x => x.Id == id);
                    if (existing is null)
                    {
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

    /// <summary>Roll unpaid bills from the given month into the following month.</summary>
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
            var targetMonthName = MonthNames.Name(month + 1);

            foreach (var bill in _currentData.BillsByMonth[month].Where(x => !x.IsPaid).ToList())
            {
                var rollover = bill.Clone();
                rollover.Id = Guid.NewGuid();
                rollover.Month = month + 1;
                rollover.IsRecurring = false;
                rollover.RecurrenceFrequency = RecurrenceFrequency.None;
                rollover.RecurrenceGroupId = null;
                rollover.IsPaid = false;
                rollover.Name = $"{bill.Name} - Rolled over from {sourceMonthName}";
                rollover.DueDate = NormalizeDueDate(CurrentYear, month + 1, bill.DueDate.Day, bill.DueDate);

                if (!bill.Name.Contains("Rolled over", StringComparison.OrdinalIgnoreCase))
                {
                    bill.Name = $"{bill.Name} - Rolled over to {targetMonthName}";
                }

                _currentData.BillsByMonth[month + 1].Add(rollover);
            }

            await SaveAsync();
        }
        finally
        {
            _stateSemaphore.Release();
        }
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
            _stateSemaphore.Release();
        }

        return parsed.Report;
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

        if (nextData.BillsByMonth.Values.Any(x => x.Count > 0) || nextData.IncomeByMonth.Values.Any(x => x > 0))
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
                ExpandWeeklySeriesIntoYear(anchor, first.Month, newYear, nextData);

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
                    unpaidCarry.DueDate = NormalizeDueDate(newYear, 1, recurringTemplate.DueDate.Day, recurringTemplate.DueDate);
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
                copy.DueDate = NormalizeDueDate(newYear, month, recurringTemplate.DueDate.Day, recurringTemplate.DueDate);
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
            copy.DueDate = NormalizeDueDate(newYear, 1, unpaidOneTime.DueDate.Day, unpaidOneTime.DueDate);
            nextData.BillsByMonth[1].Add(copy);
        }

        var decemberIncome = GetIncome(12);
        for (var month = 1; month <= 12; month++)
        {
            nextData.IncomeByMonth[month] = decemberIncome;
        }

        await _repository.SaveYearAsync(newYear, nextData);
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

        var filePath = Path.Combine(_repository.DataRoot, $"crispybills_export_{year}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        await File.WriteAllTextAsync(filePath, sb.ToString());
        return filePath;
    }

    /// <summary>Export every stored year plus notes using the same structured CSV shape as the desktop app.</summary>
    public async Task<string> ExportFullReportCsvAsync()
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
            var yearIncome = Enumerable.Range(1, 12).Sum(m => data.IncomeByMonth.GetValueOrDefault(m, 0m));
            var yearExpenses = Enumerable.Range(1, 12).Sum(m => data.BillsByMonth[m].Sum(b => b.Amount));
            var yearRemaining = Enumerable.Range(1, 12).Sum(m => data.BillsByMonth[m].Where(b => !b.IsPaid).Sum(b => b.Amount));

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

        var filePath = Path.Combine(_repository.DataRoot, $"CrispyBills_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        await File.WriteAllTextAsync(filePath, sb.ToString());
        return filePath;
    }

    /// <summary>HTML summary for the currently loaded year (for viewing in a browser).</summary>
    public string BuildYearSummaryHtml() =>
        YearSummaryHtmlBuilder.Build(CurrentYear, _currentData, GetIncome);

    /// <summary>Bills in one category for the given month.</summary>
    public IReadOnlyList<BillItem> GetBillsInCategory(int month, string category)
    {
        ValidateMonth(month);
        var key = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        return _currentData.BillsByMonth[month]
            .Where(b => string.Equals(
                string.IsNullOrWhiteSpace(b.Category) ? "General" : b.Category.Trim(),
                key,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.DueDate)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    public Task BackupCurrentYearDatabaseFileAsync()
    {
        var year = CurrentYear;
        var src = _repository.GetYearDatabasePath(year);
        if (!File.Exists(src))
        {
            return Task.CompletedTask;
        }

        var backupDir = Path.Combine(_repository.DataRoot, "db_backups", year.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDir);
        var dest = Path.Combine(backupDir, $"CrispyBills_{year}_routine_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        File.Copy(src, dest, overwrite: false);
        return Task.CompletedTask;
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
                        c.DueDate = NormalizeDueDate(targetYearValue, monthNum, c.DueDate.Day, c.DueDate);
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
                bill.DueDate = NormalizeDueDate(year, month, bill.DueDate.Day, bill.DueDate);
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

                var normalizedDate = NormalizeDueDate(CurrentYear, month, bill.DueDate.Day, bill.DueDate);
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
                    if (ExpandWeeklySeriesIntoYear(recurring, month, CurrentYear, _currentData) > 0)
                    {
                        changed = true;
                    }

                    continue;
                }

                var dueDay = recurring.DueDate.Day;
                for (var targetMonth = month + 1; targetMonth <= 12; targetMonth++)
                {
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
                        copy.DueDate = NormalizeDueDate(CurrentYear, targetMonth, dueDay, recurring.DueDate);
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
                bill.DueDate = NormalizeDueDate(CurrentYear, month, bill.DueDate.Day, bill.DueDate);
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
                yearData = new YearData();
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
            var bill = new BillItem
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                Amount = Math.Max(0, amount),
                DueDate = dueDate,
                IsPaid = status.Equals("Paid", StringComparison.OrdinalIgnoreCase),
                IsRecurring = isRecurring,
                RecurrenceFrequency = isRecurring ? RecurrenceFrequency.MonthlyInterval : RecurrenceFrequency.None
            };

            bill.Year = year;
            bill.Month = month;
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

    private static DateTime NormalizeDueDate(int year, int month, int requestedDay, DateTime original)
    {
        var maxDay = DateTime.DaysInMonth(year, month);
        var day = Math.Clamp(requestedDay, 1, maxDay);
        return new DateTime(year, month, day);
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
        target.DueDate = NormalizeDueDate(target.Year, month, baseDay, source.DueDate);
    }

    private void NormalizeForYear(int year)
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            foreach (var bill in _currentData.BillsByMonth[month])
            {
                bill.Month = month;
                bill.Year = year;
                bill.DueDate = NormalizeDueDate(year, month, bill.DueDate.Day, bill.DueDate);
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
            var occurrenceDate = NormalizeDueDate(targetYear, targetMonth, recurring.DueDate.Day, recurring.DueDate);
            if (occurrenceDate.Date > endDate.Date)
            {
                return false;
            }
        }

        return true;
    }

    private void futureListRemoveById(int month, Guid id)
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
    private static int ExpandWeeklySeriesIntoYear(BillItem anchor, int anchorMonth, int year, YearData data)
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
