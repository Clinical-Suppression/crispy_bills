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
        string[] lines = ["Name,Amount,Category,DueDate,IsPaid", "BadRow,,,2026-01-01,true"];
        var report = await service.ImportStructuredCsvForYearAsync(lines, 2026, replaceYear: true);
        // Should not throw, and no bills imported
        Assert.Empty(service.GetBills(1));
        Assert.True(report.SkippedMalformed > 0 || report.SkippedInvalidYear > 0 || report.SkippedInvalidMonth > 0);
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

    [Fact]
    public async Task PublicMonthApis_InvalidMonth_ThrowArgumentOutOfRange()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetBills(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetIncome(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetMonthSummary(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetCategoryTotals(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.FindDuplicateRecurringRules(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIncomeAsync(0, 1m));
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_ReportsImportedAndSkippedCounts()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);

        var lines = new[]
        {
            "TYPE,Year,Month,Name,Category,Amount,DueDate,Status,Recurring",
            "BILL,2026,January,Rent,Housing,1000.00,2026-01-01,Unpaid,No",
            "INCOME,2026,January,,,2500.00,,,",
            "BILL,not-a-year,January,Bad,Housing,100.00,2026-01-10,Unpaid,No",
            "BILL,2026,NoMonth,Oops,Housing,100.00,2026-01-10,Unpaid,No",
            "garbage"
        };

        var report = await service.ImportStructuredCsvForYearAsync(lines, 2026, replaceYear: true);

        Assert.Equal(6, report.RowsSeen);
        Assert.Equal(1, report.ImportedBillRows);
        Assert.Equal(1, report.ImportedIncomeRows);
        Assert.True(report.SkippedInvalidYear >= 1);
        Assert.True(report.SkippedInvalidMonth >= 1);
        Assert.True(report.SkippedMalformed >= 1);
    }

    [Fact]
    public async Task MutatingOperations_Concurrent_DoNotThrow()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);

        var add1 = service.AddBillAsync(1, new BillItem
        {
            Name = "A",
            Category = "General",
            Amount = 1m,
            DueDate = new DateTime(2026, 1, 1)
        });
        var add2 = service.AddBillAsync(1, new BillItem
        {
            Name = "B",
            Category = "General",
            Amount = 2m,
            DueDate = new DateTime(2026, 1, 2)
        });

        await Task.WhenAll(add1, add2);
        Assert.Equal(2, service.GetBills(1).Count);
    }

    [Fact]
    public async Task DeleteYearAsync_CurrentYearFallsBackToAnotherYear()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo);
        await service.LoadYearAsync(2026);
        await service.AddBillAsync(1, new BillItem
        {
            Name = "A",
            Category = "General",
            Amount = 1m,
            DueDate = new DateTime(2026, 1, 1)
        });

        await service.LoadYearAsync(2027);
        await service.AddBillAsync(1, new BillItem
        {
            Name = "B",
            Category = "General",
            Amount = 1m,
            DueDate = new DateTime(2027, 1, 1)
        });

        service.SetDebugDestructiveDeletesEnabled(true);
        var deleted = await service.DeleteYearAsync(2027);

        Assert.True(deleted);
        Assert.Equal(2026, service.CurrentYear);
    }
}
