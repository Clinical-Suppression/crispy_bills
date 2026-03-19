using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Xunit;

namespace CrispyBills.Mobile.ParityTests;

public sealed class BillingServiceParityTests
{
    [Fact]
    public async Task SetIncomeAsync_AppliesToSelectedAndFutureMonthsOnly()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
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
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(1, new BillItem
        {
            Name = "Rent",
            Amount = 1200m,
            Category = "Housing",
            DueDate = new DateTime(2026, 1, 31),
            IsRecurring = true
        });

        var febBill = service.GetBills(2).Single();
        var aprBill = service.GetBills(4).Single();

        Assert.Equal(new DateTime(2026, 2, 28), febBill.DueDate.Date);
        Assert.Equal(new DateTime(2026, 4, 30), aprBill.DueDate.Date);
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_Merge_DoesNotDuplicateExistingEquivalentBill()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
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
        var service = new BillingService(repo);
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
        Assert.Contains(jan, b => b.Name == "Gym" && !b.IsRecurring && !b.IsPaid);
        Assert.Contains(jan, b => b.Name == "Insurance" && b.IsRecurring && !b.IsPaid);
        Assert.Contains(jan, b => b.Name == "Laptop" && !b.IsRecurring && !b.IsPaid);

        var feb = service.GetBills(2);
        Assert.Equal(2, feb.Count);
        Assert.All(feb, b => Assert.True(b.IsRecurring));
        Assert.All(feb, b => Assert.False(b.IsPaid));
    }

    [Fact]
    public async Task CreateNewYearFromDecemberAsync_ReturnsFalse_WhenTargetYearAlreadyHasData()
    {
        var repo = new InMemoryBillingRepository();

        var seedService = new BillingService(repo);
        await seedService.LoadYearAsync(2027);
        await seedService.AddBillAsync(1, new BillItem
        {
            Name = "Existing",
            Amount = 1m,
            Category = "General",
            DueDate = new DateTime(2027, 1, 1),
            IsRecurring = false
        });

        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);

        var created = await service.CreateNewYearFromDecemberAsync();
        Assert.False(created);

        await service.LoadYearAsync(2027);
        Assert.Single(service.GetBills(1));
        Assert.Equal("Existing", service.GetBills(1).Single().Name);
    }

    private sealed class InMemoryBillingRepository : IBillingRepository
    {
        private readonly Dictionary<int, YearData> _store = new();
        private readonly Dictionary<string, string> _meta = new(StringComparer.OrdinalIgnoreCase);
        private string _notes = string.Empty;

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
            _store[year] = Clone(data);
            return Task.CompletedTask;
        }

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
