using CrispyBills.Mobile.Android.Models;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrispyBills.Mobile.Android.Services;

public sealed class BillingService
{
    private readonly SemaphoreSlim _loadYearSemaphore = new(1, 1);
    private const string ArchivedYearsMetaKey = "ArchivedYears";
    private readonly IBillingRepository _repository;

    private YearData _currentData = new();
    // Session-only guards for destructive debug tools (Android)
    private bool _debugDestructiveDeletesEnabled = false;
    private bool _debugEnableWarningShown = false;

    public BillingService(IBillingRepository repository)
    {
        _repository = repository;
    }

    public int CurrentYear { get; private set; } = DateTime.Today.Year;

    public async Task LoadYearAsync(int year)
    {
        await _loadYearSemaphore.WaitAsync();
        try
        {
            CurrentYear = year;
            _currentData = await _repository.LoadYearAsync(year);
            NormalizeForYear(CurrentYear);
            await EnsureRecurringCatchUpAsync();
        }
        finally
        {
            _loadYearSemaphore.Release();
        }
    }

    public IReadOnlyList<BillItem> GetBills(int month)
    {
        return _currentData.BillsByMonth[month]
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public decimal GetIncome(int month)
    {
        return _currentData.IncomeByMonth.TryGetValue(month, out var income) ? income : 0m;
    }

    public IReadOnlyList<int> GetAvailableYears()
    {
        return _repository.GetAvailableYears();
    }

    public IReadOnlyList<BillTemplate> GetTemplates()
    {
        return BillTemplateCatalog.GetAll();
    }

    public BillItem CreateDraftFromTemplate(BillTemplate template, int year, int month)
    {
        var dueDay = Math.Clamp(template.SuggestedDueDay, 1, DateTime.DaysInMonth(year, month));
        return new BillItem
        {
            Name = template.Name,
            Category = template.Category,
            Amount = template.SuggestedAmount,
            DueDate = new DateTime(year, month, dueDay),
            IsRecurring = template.IsRecurring,
            RecurrenceEveryMonths = Math.Max(1, template.RecurrenceEveryMonths),
            RecurrenceEndMode = template.RecurrenceEndMode,
            RecurrenceEndDate = template.RecurrenceEndDate,
            RecurrenceMaxOccurrences = template.RecurrenceMaxOccurrences,
            Year = year,
            Month = month
        };
    }

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

    public IReadOnlyList<IReadOnlyList<BillItem>> FindDuplicateRecurringRules(int month)
    {
        return _currentData.BillsByMonth[month]
            .Where(x => x.IsRecurring)
            .GroupBy(x => BuildRecurringSignature(x), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<BillItem>)g.ToList())
            .ToList();
    }

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

    public async Task RestoreYearSnapshotAsync(YearData snapshot)
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

    public async Task SetIncomeAsync(int month, decimal income)
    {
        var clamped = Math.Max(0, income);
        for (var targetMonth = month; targetMonth <= 12; targetMonth++)
        {
            _currentData.IncomeByMonth[targetMonth] = clamped;
        }

        await SaveAsync();
    }

    // Expose a programmatic toggle for destructive debug tools (session-only)
    public void SetDebugDestructiveDeletesEnabled(bool enabled)
    {
        _debugDestructiveDeletesEnabled = enabled;
    }

    public bool IsDebugDestructiveDeletesEnabled() => _debugDestructiveDeletesEnabled;

    /// <summary>
    /// Permanently deletes all bills and income for the specified month in the currently loaded year.
    /// Creates a file backup before performing the operation. Returns true if the deletion completed.
    /// This operation requires the debug destructive toggle to be enabled for the session.
    /// </summary>
    public async Task<bool> DeleteMonthAsync(int month)
    {
        if (!_debugDestructiveDeletesEnabled) return false;
        if (month < 1 || month > 12) return false;

        try
        {
            // Create a simple backup of the current year DB
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

            // Snapshot is available to callers through RestoreYearSnapshotAsync if needed
            var snapshot = CreateYearSnapshot();

            // Perform deletion in-memory
            _currentData.BillsByMonth[month].Clear();
            _currentData.IncomeByMonth[month] = 0m;

            // Persist
            await SaveAsync();
            return true;
        }
        catch (Exception ex)
        {
            ReportDiagnostic("DebugDeleteMonth", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// Permanently deletes the on-disk database and sidecars for the specified year.
    /// Creates a backup before deleting and, if the deleted year was the current year,
    /// attempts to fall back to another available year and load it.
    /// Requires the debug destructive toggle to be enabled for the session.
    /// </summary>
    public async Task<bool> DeleteYearAsync(int year)
    {
        if (!_debugDestructiveDeletesEnabled) return false;

        try
        {
            var dbPath = _repository.GetYearDatabasePath(year);

            // Backup first
            try
            {
                if (File.Exists(dbPath))
                {
                    var backupDir = Path.Combine(_repository.DataRoot, "db_backups", year.ToString());
                    Directory.CreateDirectory(backupDir);
                    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var dest = Path.Combine(backupDir, $"CrispyBills_{year}_{timestamp}.db");
                    File.Copy(dbPath, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic("DebugDeleteYear backup", ex.ToString());
            }

            try
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
                var wal = dbPath + "-wal";
                var shm = dbPath + "-shm";
                if (File.Exists(wal)) File.Delete(wal);
                if (File.Exists(shm)) File.Delete(shm);
                var bak = dbPath + ".prewrite.bak";
                if (File.Exists(bak)) File.Delete(bak);
            }
            catch (Exception ex)
            {
                ReportDiagnostic($"DebugDeleteYear delete files ({year})", ex.ToString());
            }

            // If this was the currently loaded year, pick a fallback and load it
            if (year == CurrentYear)
            {
                var years = _repository.GetAvailableYears();
                var fallback = years.FirstOrDefault(y => y != year);
                if (fallback == 0)
                {
                    fallback = DateTime.Now.Year;
                }

                if (fallback != CurrentYear)
                {
                    await LoadYearAsync(fallback);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            ReportDiagnostic("DebugDeleteYear", ex.ToString());
            return false;
        }
    }

    public (decimal income, decimal expenses, decimal remaining, int billCount) GetYearSummary()
    {
        var income = Enumerable.Range(1, 12).Sum(m => GetIncome(m));
        var expenses = Enumerable.Range(1, 12).SelectMany(m => _currentData.BillsByMonth[m]).Sum(x => x.Amount);
        var billCount = Enumerable.Range(1, 12).Sum(m => _currentData.BillsByMonth[m].Count);
        return (income, expenses, income - expenses, billCount);
    }

    public IReadOnlyList<(string category, decimal total)> GetCategoryTotals(int month)
    {
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

    public async Task AddBillAsync(int month, BillItem draft)
    {
        ValidateBill(draft);
        var baseDay = Math.Min(draft.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));

        var current = draft.Clone();
        current.Id = draft.IsRecurring
            ? Guid.NewGuid()
            : (draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id);
        current.Year = CurrentYear;
        current.Month = month;
        current.DueDate = NormalizeDueDate(CurrentYear, month, baseDay, draft.DueDate);
        _currentData.BillsByMonth[month].Add(current);

        if (draft.IsRecurring)
        {
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
                _currentData.BillsByMonth[target].Add(future);
            }
        }

        await SaveAsync();
    }

    public async Task UpdateBillAsync(int month, Guid id, BillItem edited)
    {
        ValidateBill(edited);

        var all = _currentData.BillsByMonth[month];
        var target = all.FirstOrDefault(x => x.Id == id);
        if (target is null)
        {
            return;
        }

        var baseDay = Math.Min(edited.DueDate.Day, DateTime.DaysInMonth(CurrentYear, month));
        var wasRecurring = target.IsRecurring;

        Apply(target, edited, month, baseDay);

        if (edited.IsRecurring)
        {
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

    public async Task DeleteBillAsync(int month, Guid id)
    {
        var current = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
        if (current is null)
        {
            return;
        }

        _currentData.BillsByMonth[month].RemoveAll(x => x.Id == id);

        if (current.IsRecurring)
        {
            for (var futureMonth = month + 1; futureMonth <= 12; futureMonth++)
            {
                _currentData.BillsByMonth[futureMonth].RemoveAll(x => x.Id == id);
            }
        }

        await SaveAsync();
    }

    public async Task TogglePaidAsync(int month, Guid id)
    {
        var bill = _currentData.BillsByMonth[month].FirstOrDefault(x => x.Id == id);
        if (bill is null)
        {
            return;
        }

        bill.IsPaid = !bill.IsPaid;
        await SaveAsync();
    }

    public async Task RolloverUnpaidAsync(int month)
    {
        if (month >= 12)
        {
            return;
        }

        var sourceMonthName = MonthNames.Name(month);
        var targetMonthName = MonthNames.Name(month + 1);

        foreach (var bill in _currentData.BillsByMonth[month].Where(x => !x.IsPaid).ToList())
        {
            var rollover = bill.Clone();
            rollover.Id = Guid.NewGuid();
            rollover.Month = month + 1;
            rollover.IsRecurring = false;
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

    public async Task ImportStructuredCsvForYearAsync(string csvPath, int year, bool replaceYear)
    {
        var parsed = ParseStructuredCsvByYear(await File.ReadAllLinesAsync(csvPath));
        if (!parsed.TryGetValue(year, out var importedYear))
        {
            // No data for requested year - treat as no-op for robustness in tests and imports
            return;
        }

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

        NormalizeForYear(CurrentYear);
        ValidateInvariants();
        await SaveAsync();
    }

    // Overload used by tests to import CSV content from lines instead of a file path.
    public async Task ImportStructuredCsvForYearAsync(IEnumerable<string> lines, int year, bool replaceYear)
    {
        var parsed = ParseStructuredCsvByYear(lines);
        if (!parsed.TryGetValue(year, out var importedYear))
        {
            // No data for requested year - treat as a no-op to be robust for different CSV shapes
            return;
        }

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

        NormalizeForYear(CurrentYear);
        ValidateInvariants();
        await SaveAsync();
    }

    public async Task<bool> CreateNewYearFromDecemberAsync()
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

    public (decimal total, decimal paid, decimal unpaid, decimal remaining, int billCount) GetMonthSummary(int month)
    {
        var bills = _currentData.BillsByMonth[month];
        var total = bills.Sum(x => x.Amount);
        var paid = bills.Where(x => x.IsPaid).Sum(x => x.Amount);
        var unpaid = total - paid;
        var remaining = GetIncome(month) - unpaid;
        return (total, paid, unpaid, remaining, bills.Count);
    }

    public IReadOnlyList<string> Categories()
    {
        // Merge stored categories with the common household defaults from the desktop app
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

    public Task<string> LoadNotesAsync()
    {
        return _repository.LoadNotesAsync();
    }

    public Task SaveNotesAsync(string notes)
    {
        var normalized = string.Join("\n", notes.Split('\n').Take(500));
        return _repository.SaveNotesAsync(normalized);
    }

    public async Task<IntegrityCheckReport> RunIntegrityCheckAndRepairAsync()
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
                bill.Name = bill.Name.Trim();
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

    private static Dictionary<int, YearData> ParseStructuredCsvByYear(IEnumerable<string> lines)
    {
        var result = new Dictionary<int, YearData>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = SplitCsvLine(line);
            if (cells.Count < 3)
            {
                continue;
            }

            var type = cells[0].Trim();
            if (type.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(cells[1], out var year))
            {
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
                continue;
            }

            if (type.Equals("INCOME", StringComparison.OrdinalIgnoreCase))
            {
                if (cells.Count > 5 && decimal.TryParse(cells[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var income))
                {
                    yearData.IncomeByMonth[month] = Math.Max(0, income);
                }

                continue;
            }

            if (!type.Equals("BILL", StringComparison.OrdinalIgnoreCase))
            {
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

            var bill = new BillItem
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                Amount = Math.Max(0, amount),
                DueDate = dueDate,
                IsPaid = status.Equals("Paid", StringComparison.OrdinalIgnoreCase),
                IsRecurring = recurringText.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            };

            bill.Year = year;
            bill.Month = month;
            yearData.BillsByMonth[month].Add(bill);
        }

        return result;
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
            bill.RecurrenceEndMode = RecurrenceEndMode.None;
            bill.RecurrenceEndDate = null;
            bill.RecurrenceMaxOccurrences = null;
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
        if (!recurring.IsRecurring || targetMonth < startMonth)
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

    private static string BuildRecurringSignature(BillItem bill)
    {
        return string.Join("|",
            bill.Name.Trim().ToLowerInvariant(),
            bill.Category.Trim().ToLowerInvariant(),
            bill.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            bill.DueDate.Day.ToString(CultureInfo.InvariantCulture),
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
}
