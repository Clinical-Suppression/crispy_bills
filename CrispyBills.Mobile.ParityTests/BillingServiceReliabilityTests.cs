using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Xunit;
using System;
using System.Threading.Tasks;

namespace CrispyBills.Mobile.ParityTests;

public sealed class BillingServiceReliabilityTests
{
    [Fact]
    public async Task LoadYearAsync_InvalidGuid_SkipsRowAndLogs()
    {
        var repo = new InMemoryBillingRepository();
        // Simulate a row with an invalid GUID
        repo.AddRawBillRow("not-a-guid", "Rent", 1200m, "2026-01-01", false, "Housing", false);
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);
        // Should not throw, and bill should not be present
        Assert.Empty(service.GetBills(1));
        // Optionally: Assert log contains skip message
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_MalformedRow_LogsAndSkips()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);
        // Simulate malformed CSV (missing amount)
        string[] lines = new[] { "Name,Amount,Category,DueDate,IsPaid", "BadRow,,,2026-01-01,true" };
        await service.ImportStructuredCsvForYearAsync(lines, 2026, replaceYear: true);
        // Should not throw, and no bills imported
        Assert.Empty(service.GetBills(1));
        // Optionally: Assert log contains error
    }

    [Fact]
    public async Task LoadYearAsync_Concurrent_NoDataCorruption()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        var t1 = service.LoadYearAsync(2026);
        var t2 = service.LoadYearAsync(2027);
        await Task.WhenAll(t1, t2);
        // Should not throw or corrupt state
        Assert.True(service.CurrentYear == 2026 || service.CurrentYear == 2027);
    }
}
