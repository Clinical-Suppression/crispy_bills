using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Xunit;

namespace CrispyBills.Mobile.ParityTests;

public sealed class BillingServiceParityTests
{
    /// <summary>January 2026 so automatic month-boundary rollover (same calendar year) does not run (today.Month is 1).</summary>
    private static readonly Func<DateTime> TestTodayJan2026 = () => new DateTime(2026, 1, 10);

    [Fact]
    public async Task SetIncomeAsync_AppliesToSelectedAndFutureMonthsOnly()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.SetIncomeAsync(5, 1234.56m);

        Assert.Equal(0m, service.GetIncome(4));
        Assert.Equal(1234.56m, service.GetIncome(5));
        Assert.Equal(1234.56m, service.GetIncome(12));
    }

    [Fact]
    public async Task AddBillAsync_Recurring_ClampsFutureDueDatesByMonth()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(1, new BillItem
        {
            Name = "Rent",
            Amount = 1200m,
            Category = "Housing",
            DueDate = new DateTime(2026, 1, 31),
            IsRecurring = true
        });

        var janBill = service.GetBills(1).Single();
        Assert.Equal(new DateTime(2026, 1, 31), janBill.DueDate.Date);

        for (var m = 2; m <= 12; m++)
        {
            var b = service.GetBills(m).Single();
            Assert.Equal(janBill.Id, b.Id);
            Assert.False(b.IsPaid);
            var expectedDay = Math.Min(31, DateTime.DaysInMonth(2026, m));
            Assert.Equal(new DateTime(2026, m, expectedDay), b.DueDate.Date);
        }
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_Merge_DoesNotDuplicateExistingEquivalentBill()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(1, new BillItem
        {
            Name = "Internet",
            Amount = 80m,
            Category = "Utilities",
            DueDate = new DateTime(2026, 1, 15),
            IsRecurring = false
        });

        var csvPath = Path.Combine(Path.GetTempPath(), $"crispy_import_{Guid.NewGuid():N}.csv");
        var csv = string.Join("\n", new[]
        {
            "TYPE,Year,Month,Name,Category,Amount,DueDate,Status,Recurring",
            "MONTH,2026,January,,,,,,",
            "BILL,2026,January,Internet,Utilities,80.00,2026-01-15,Unpaid,No",
            "INCOME,2026,January,,,0.00,,,"
        });

        await File.WriteAllTextAsync(csvPath, csv);

        try
        {
            await service.ImportStructuredCsvForYearAsync(csvPath, 2026, replaceYear: false);
        }
        finally
        {
            if (File.Exists(csvPath))
            {
                File.Delete(csvPath);
            }
        }

        Assert.Single(service.GetBills(1));
    }

    [Fact]
    public async Task CreateNewYearFromDecemberAsync_CarriesExpectedTemplatesAndUnpaidItems()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.SetIncomeAsync(12, 3000m);

        await service.AddBillAsync(12, new BillItem
        {
            Name = "Gym",
            Amount = 55m,
            Category = "Health",
            DueDate = new DateTime(2026, 12, 31),
            IsRecurring = true,
            IsPaid = false
        });

        await service.AddBillAsync(12, new BillItem
        {
            Name = "Insurance",
            Amount = 120m,
            Category = "Bills",
            DueDate = new DateTime(2026, 12, 20),
            IsRecurring = true,
            IsPaid = true
        });

        await service.AddBillAsync(12, new BillItem
        {
            Name = "Laptop",
            Amount = 900m,
            Category = "Shopping",
            DueDate = new DateTime(2026, 12, 18),
            IsRecurring = false,
            IsPaid = false
        });

        var created = await service.CreateNewYearFromDecemberAsync();
        Assert.True(created);

        await service.LoadYearAsync(2027);

        Assert.Equal(3000m, service.GetIncome(1));
        Assert.Equal(3000m, service.GetIncome(12));

        var jan = service.GetBills(1);
        Assert.Contains(jan, b => b.Name == "Gym" && b.IsRecurring && !b.IsPaid);
        Assert.Contains(jan, b => b.Name == "Gym - December" && !b.IsRecurring && !b.IsPaid);
        Assert.Contains(jan, b => b.Name == "Insurance" && b.IsRecurring && !b.IsPaid);
        Assert.Contains(jan, b => b.Name == "Laptop - December" && !b.IsRecurring && !b.IsPaid);

        var feb = service.GetBills(2);
        Assert.Equal(2, feb.Count);
        Assert.All(feb, b => Assert.True(b.IsRecurring));
        Assert.All(feb, b => Assert.False(b.IsPaid));
    }

    [Fact]
    public async Task CreateNewYearFromDecemberAsync_ReturnsFalse_WhenTargetYearAlreadyHasData()
    {
        var repo = new InMemoryBillingRepository();

        var seedService = new BillingService(repo, TestTodayJan2026);
        await seedService.LoadYearAsync(2027);
        await seedService.AddBillAsync(1, new BillItem
        {
            Name = "Existing",
            Amount = 1m,
            Category = "General",
            DueDate = new DateTime(2027, 1, 1),
            IsRecurring = false
        });

        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        var created = await service.CreateNewYearFromDecemberAsync();
        Assert.False(created);

        await service.LoadYearAsync(2027);
        Assert.Single(service.GetBills(1));
        Assert.Equal("Existing", service.GetBills(1).Single().Name);
    }

    [Fact]
    public async Task CreateNewYearFromDecemberAsync_ReturnsFalse_WhenTargetYearHasJanuaryIncomeOnly()
    {
        var repo = new InMemoryBillingRepository();

        var seedService = new BillingService(repo, TestTodayJan2026);
        await seedService.LoadYearAsync(2027);
        await seedService.SetIncomeAsync(1, 5000m);

        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        var created = await service.CreateNewYearFromDecemberAsync();
        Assert.False(created);
    }

    /// <summary>
    /// New year copies monthly templates with <c>ShouldCreateRecurringOccurrence(template, 1, month, newYear)</c>—January-anchored phase, not strict continuation from December (Feb would be next if continuing from a Dec occurrence).
    /// </summary>
    [Fact]
    public async Task CreateNewYearFromDecemberAsync_BiMonthlyRecurring_UsesJanuaryAnchoredPhase()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(12, new BillItem
        {
            Name = "BiMonth",
            Amount = 40m,
            Category = "General",
            DueDate = new DateTime(2026, 12, 15),
            IsRecurring = true,
            RecurrenceEveryMonths = 2,
            IsPaid = true
        });

        Assert.True(await service.CreateNewYearFromDecemberAsync());

        await service.LoadYearAsync(2027);
        Assert.Contains(service.GetBills(1), b => b.Name == "BiMonth" && b.IsRecurring);
        Assert.DoesNotContain(service.GetBills(2), b => b.Name == "BiMonth");
        Assert.Contains(service.GetBills(3), b => b.Name == "BiMonth" && b.IsRecurring);
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_TargetYearDifferentFromCurrentYear_SavesToTargetAndKeepsCurrentContext()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(1, new BillItem
        {
            Name = "Current Year Bill",
            Amount = 10m,
            Category = "General",
            DueDate = new DateTime(2026, 1, 5),
            IsRecurring = false
        });

        var lines = new[]
        {
            "TYPE,Year,Month,Name,Category,Amount,DueDate,Status,Recurring",
            "MONTH,2027,January,,,,,,",
            "BILL,2027,January,Imported 2027,Utilities,45.00,2027-01-20,Unpaid,No",
            "INCOME,2027,January,,,1200.00,,,"
        };

        await service.ImportStructuredCsvForYearAsync(lines, 2027, replaceYear: true);

        Assert.Equal(2026, service.CurrentYear);
        Assert.Contains(2027, repo.SaveYears);
        Assert.DoesNotContain(2026, repo.SaveYears.Where(x => x == 2026).Skip(1));

        var verify2026 = new BillingService(repo, TestTodayJan2026);
        await verify2026.LoadYearAsync(2026);
        Assert.Contains(verify2026.GetBills(1), b => b.Name == "Current Year Bill");

        var verify2027 = new BillingService(repo, TestTodayJan2026);
        await verify2027.LoadYearAsync(2027);
        Assert.Contains(verify2027.GetBills(1), b => b.Name == "Imported 2027");
        Assert.Equal(1200m, verify2027.GetIncome(1));
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_MergeIntoDifferentYear_DoesNotMutateLoadedYear()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(2, new BillItem
        {
            Name = "Current-2026",
            Amount = 25m,
            Category = "General",
            DueDate = new DateTime(2026, 2, 10),
            IsRecurring = false
        });

        var lines = new[]
        {
            "TYPE,Year,Month,Name,Category,Amount,DueDate,Status,Recurring",
            "MONTH,2027,February,,,,,,",
            "BILL,2027,February,Merged-2027,Utilities,55.00,2027-02-11,Unpaid,No",
            "INCOME,2027,February,,,1800.00,,,"
        };

        await service.ImportStructuredCsvForYearAsync(lines, 2027, replaceYear: false);

        Assert.Equal(2026, service.CurrentYear);
        Assert.Contains(service.GetBills(2), b => b.Name == "Current-2026");
        Assert.DoesNotContain(service.GetBills(2), b => b.Name == "Merged-2027");

        var verify2027 = new BillingService(repo, TestTodayJan2026);
        await verify2027.LoadYearAsync(2027);
        Assert.Contains(verify2027.GetBills(2), b => b.Name == "Merged-2027");
        Assert.Equal(1800m, verify2027.GetIncome(2));
    }

    [Fact]
    public async Task DeleteYearAsync_WhenOnlyYearOnDisk_LeavesNoPersistedYearAndEmptyInMemoryState()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);
        await service.AddBillAsync(1, new BillItem
        {
            Name = "Only",
            Amount = 1m,
            Category = "General",
            DueDate = new DateTime(2026, 1, 1),
            IsRecurring = false
        });

        service.SetDebugDestructiveDeletesEnabled(true);
        var ok = await service.DeleteYearAsync(2026);
        Assert.True(ok);
        Assert.Empty(repo.GetAvailableYears());
        Assert.Empty(service.GetBills(1));
    }

    [Fact]
    public async Task DeleteMonthAsync_RemovesForwardMonthlyRecurringWithSameId()
    {
        var repo = new InMemoryBillingRepository();
        var sharedId = Guid.NewGuid();
        var yearData = new YearData();
        yearData.BillsByMonth[3].Add(new BillItem
        {
            Id = sharedId,
            Name = "Sub",
            Amount = 10m,
            Category = "General",
            DueDate = new DateTime(2026, 3, 15),
            IsRecurring = true,
            RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
            Year = 2026,
            Month = 3
        });
        yearData.BillsByMonth[4].Add(new BillItem
        {
            Id = sharedId,
            Name = "Sub",
            Amount = 10m,
            Category = "General",
            DueDate = new DateTime(2026, 4, 15),
            IsRecurring = true,
            RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
            Year = 2026,
            Month = 4
        });
        repo.SetYearDataForTests(2026, yearData);

        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        service.SetDebugDestructiveDeletesEnabled(true);
        var ok = await service.DeleteMonthAsync(3);
        Assert.True(ok);

        Assert.Empty(service.GetBills(3));
        Assert.Empty(service.GetBills(4));
    }

    [Fact]
    public async Task LoadYearAsync_CurrentYear_ChainsAutomaticMonthBoundaryCarryover_ForUnpaidOneTime()
    {
        var repo = new InMemoryBillingRepository();
        var yearData = new YearData();
        yearData.BillsByMonth[1].Add(new BillItem
        {
            Id = Guid.NewGuid(),
            Name = "Water",
            Amount = 40m,
            Category = "Utilities",
            DueDate = new DateTime(2026, 1, 20),
            IsRecurring = false,
            Year = 2026,
            Month = 1,
            IsPaid = false
        });
        repo.SetYearDataForTests(2026, yearData);

        var service = new BillingService(repo, () => new DateTime(2026, 3, 15));
        await service.LoadYearAsync(2026);

        Assert.Contains(service.GetBills(2), b => b.Name == "Water - January" && !b.IsRecurring);
        Assert.Contains(service.GetBills(3), b => b.Name == "Water - February" && !b.IsRecurring);

        await service.LoadYearAsync(2026);
        Assert.Equal(1, service.GetBills(2).Count(b => b.Name == "Water - January"));
        Assert.Equal(1, service.GetBills(3).Count(b => b.Name == "Water - February"));
    }

    private sealed class InMemoryBillingRepository : IBillingRepository
    {
        private readonly Dictionary<int, YearData> _store = new();
        private readonly Dictionary<string, string> _meta = new(StringComparer.OrdinalIgnoreCase);
        private string _notes = string.Empty;
        public List<int> SaveYears { get; } = [];

        public string DataRoot => Path.GetTempPath();

        public string GetYearDatabasePath(int year)
        {
            return Path.Combine(DataRoot, $"inmemory_{year}.db");
        }

        public IReadOnlyList<int> GetAvailableYears()
        {
            return _store.Keys.OrderBy(x => x).ToList();
        }

        public Task InitializeYearAsync(int year)
        {
            if (!_store.ContainsKey(year))
            {
                _store[year] = new YearData();
            }

            return Task.CompletedTask;
        }

        public Task<YearData> LoadYearAsync(int year)
        {
            if (!_store.TryGetValue(year, out var data))
            {
                data = new YearData();
                _store[year] = data;
            }

            return Task.FromResult(Clone(data));
        }

        public Task SaveYearAsync(int year, YearData data)
        {
            SaveYears.Add(year);
            _store[year] = Clone(data);
            return Task.CompletedTask;
        }

        public Task DeletePersistedYearAsync(int year)
        {
            _store.Remove(year);
            return Task.CompletedTask;
        }

        public void SetYearDataForTests(int year, YearData data) => _store[year] = data;

        public Task<string> LoadNotesAsync()
        {
            return Task.FromResult(_notes);
        }

        public Task SaveNotesAsync(string notes)
        {
            _notes = notes;
            return Task.CompletedTask;
        }

        public Task<string?> GetAppMetaAsync(string key)
        {
            return Task.FromResult(_meta.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAppMetaAsync(string key, string value)
        {
            _meta[key] = value;
            return Task.CompletedTask;
        }

        private static YearData Clone(YearData source)
        {
            var clone = new YearData();
            foreach (var month in Enumerable.Range(1, 12))
            {
                clone.BillsByMonth[month] = source.BillsByMonth[month].Select(x => x.Clone()).ToList();
                clone.IncomeByMonth[month] = source.IncomeByMonth[month];
            }

            return clone;
        }
    }
}
