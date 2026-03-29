using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CrispyBills.Mobile.ParityTests;

public class InMemoryBillingRepository : IBillingRepository
{
    private readonly ConcurrentDictionary<int, YearData> _store = new();
    private readonly ConcurrentDictionary<string, string> _meta = new();
    private string _notes = string.Empty;

    public string DataRoot => string.Empty;

    public string GetYearDatabasePath(int year) => string.Empty;

    public IReadOnlyList<int> GetAvailableYears() => _store.Keys.OrderBy(x => x).ToList();

    /// <summary>Test helper: replace stored year data without going through the service.</summary>
    public void SetYearDataForTests(int year, YearData data)
    {
        _store[year] = data;
    }

    public Task InitializeYearAsync(int year)
    {
        _store.TryAdd(year, new YearData());
        return Task.CompletedTask;
    }

    public Task<YearData> LoadYearAsync(int year)
    {
        if (!_store.TryGetValue(year, out var data))
        {
            data = new YearData();
            _store[year] = data;
        }

        return Task.FromResult(data);
    }

    public Task SaveYearAsync(int year, YearData data)
    {
        _store[year] = data;
        return Task.CompletedTask;
    }

    public Task DeletePersistedYearAsync(int year)
    {
        _store.TryRemove(year, out _);
        return Task.CompletedTask;
    }

    public Task<string> LoadNotesAsync() => Task.FromResult(_notes);

    public Task SaveNotesAsync(string notes)
    {
        _notes = notes ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetAppMetaAsync(string key)
    {
        return Task.FromResult(_meta.TryGetValue(key, out var v) ? v : null as string);
    }

    public Task SetAppMetaAsync(string key, string value)
    {
        _meta[key] = value;
        return Task.CompletedTask;
    }

    // Test helper to seed a raw bill row (matches earlier importer expectations)
    public void AddRawBillRow(string id, string name, decimal amount, string dueDate, bool isPaid, string category, bool isRecurring)
    {
        // place into year 2026 default if not present
        var year = 2026;
        var data = _store.GetOrAdd(year, _ => new YearData());

        // Simulate raw DB behavior: if the id is not a valid GUID, skip adding the row
        if (!Guid.TryParse(id, out var g))
        {
            return;
        }

        var bill = new BillItem
        {
            Id = g,
            Name = name,
            Amount = amount,
            DueDate = DateTime.Parse(dueDate),
            IsPaid = isPaid,
            Category = category,
            IsRecurring = isRecurring,
            Year = year,
            Month = 1
        };

        data.BillsByMonth[1].Add(bill);
    }
}
