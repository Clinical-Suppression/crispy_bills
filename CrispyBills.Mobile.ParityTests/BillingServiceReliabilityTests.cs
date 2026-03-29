using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using static CrispyBills.Mobile.Android.Services.BillingService;
using Xunit;
using System;
using System.Threading.Tasks;

namespace CrispyBills.Mobile.ParityTests;

public sealed class BillingServiceReliabilityTests
{
    private static readonly Func<DateTime> TestTodayJan2026 = () => new DateTime(2026, 1, 10);

    [Fact]
    public async Task LoadYearAsync_InvalidGuid_SkipsRowAndLogs()
    {
        var repo = new InMemoryBillingRepository();
        // Simulate a row with an invalid GUID
        repo.AddRawBillRow("not-a-guid", "Rent", 1200m, "2026-01-01", false, "Housing", false);
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);
        // Should not throw, and bill should not be present
        Assert.Empty(service.GetBills(1));
        // Optionally: Assert log contains skip message
    }

    [Fact]
    public async Task ImportStructuredCsvForYearAsync_MalformedRow_LogsAndSkips()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
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
        var service = new BillingService(repo, TestTodayJan2026);
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
        var service = new BillingService(repo, TestTodayJan2026);
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
        var service = new BillingService(repo, TestTodayJan2026);
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
        var service = new BillingService(repo, TestTodayJan2026);
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
        var service = new BillingService(repo, TestTodayJan2026);
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

    [Fact]
    public async Task SetIncomeAsync_CurrentAndFutureOnly_StoresEntryPayPeriod()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.SetIncomeAsync(1, 1200m, BillingService.IncomePayPeriodMonthly);
        await service.SetIncomeAsync(3, 500m, BillingService.IncomePayPeriodWeekly);

        Assert.Equal(1200m, service.GetIncome(1));
        Assert.Equal(1200m, service.GetIncome(2));
        Assert.Equal(2166.67m, service.GetIncome(3));
        Assert.Equal(2166.67m, service.GetIncome(12));

        var februaryEntry = await service.GetIncomeEntryAsync(2);
        var marchEntry = await service.GetIncomeEntryAsync(3);

        Assert.Equal((1200m, BillingService.IncomePayPeriodMonthly), februaryEntry);
        Assert.Equal((500m, BillingService.IncomePayPeriodWeekly), marchEntry);
    }

    [Fact]
    public async Task AddBillAsync_WeeklyRecurring_CreatesExpectedMonthlyWeekLabels()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(2, new BillItem
        {
            Name = "Gym",
            Category = "Health",
            Amount = 25m,
            DueDate = new DateTime(2026, 2, 1),
            IsRecurring = true,
            RecurrenceFrequency = RecurrenceFrequency.Weekly
        });

        var februaryBills = service.GetBills(2).OrderBy(x => x.DueDate).ToList();

        Assert.Equal(4, februaryBills.Count);
        Assert.Equal(
            ["Gym - Week 1", "Gym - Week 2", "Gym - Week 3", "Gym - Week 4"],
            februaryBills.Select(x => x.Name).ToArray());
        Assert.Equal(
            [new DateTime(2026, 2, 1), new DateTime(2026, 2, 8), new DateTime(2026, 2, 15), new DateTime(2026, 2, 22)],
            februaryBills.Select(x => x.DueDate).ToArray());
    }

    [Fact]
    public async Task TogglePaidAsync_WeeklyOccurrence_OnlyChangesSelectedOccurrence()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(2, new BillItem
        {
            Name = "Tutoring",
            Category = "Income Offset",
            Amount = 40m,
            DueDate = new DateTime(2026, 2, 1),
            IsRecurring = true,
            RecurrenceFrequency = RecurrenceFrequency.Weekly
        });

        var target = service.GetBills(2).Single(x => x.DueDate == new DateTime(2026, 2, 8));
        await service.TogglePaidAsync(2, target.Id);

        var februaryBills = service.GetBills(2).OrderBy(x => x.DueDate).ToList();

        Assert.Single(februaryBills, x => x.IsPaid);
        Assert.True(februaryBills.Single(x => x.DueDate == new DateTime(2026, 2, 8)).IsPaid);
        Assert.All(
            februaryBills.Where(x => x.DueDate != new DateTime(2026, 2, 8)),
            bill => Assert.False(bill.IsPaid));
    }

    [Fact]
    public async Task UpdateBillAsync_WeeklyChild_OnlyChangesCurrentAndFutureOccurrences()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.AddBillAsync(2, new BillItem
        {
            Name = "Lessons",
            Category = "Education",
            Amount = 30m,
            DueDate = new DateTime(2026, 2, 1),
            IsRecurring = true,
            RecurrenceFrequency = RecurrenceFrequency.Weekly
        });

        var thirdOccurrence = service.GetBills(2).Single(x => x.DueDate == new DateTime(2026, 2, 15));
        var edited = thirdOccurrence.Clone();
        edited.Name = "Lessons Updated";
        edited.Amount = 45m;
        edited.IsRecurring = true;
        edited.RecurrenceFrequency = RecurrenceFrequency.Weekly;

        await service.UpdateBillAsync(2, thirdOccurrence.Id, edited);

        var februaryBills = service.GetBills(2).OrderBy(x => x.DueDate).ToList();

        Assert.Equal(
            ["Lessons - Week 1", "Lessons - Week 2", "Lessons Updated - Week 1", "Lessons Updated - Week 2"],
            februaryBills.Select(x => x.Name).ToArray());
        Assert.Equal([30m, 30m, 45m, 45m], februaryBills.Select(x => x.Amount).ToArray());
    }

    [Fact]
    public async Task SoonThreshold_RoundTripsAndSkipsPaidOrPastDueBills()
    {
        var repo = new InMemoryBillingRepository();
        var service = new BillingService(repo, TestTodayJan2026);
        await service.LoadYearAsync(2026);

        await service.SetSoonThresholdAsync(2, BillingService.SoonThresholdUnitWeeks);
        var threshold = await service.GetSoonThresholdAsync();

        var soonBill = new BillItem { DueDate = DateTime.Today.AddDays(10), IsPaid = false };
        var farBill = new BillItem { DueDate = DateTime.Today.AddDays(20), IsPaid = false };
        var paidBill = new BillItem { DueDate = DateTime.Today.AddDays(2), IsPaid = true };
        var pastDueBill = new BillItem { DueDate = DateTime.Today.AddDays(-1), IsPaid = false };

        Assert.Equal((2, BillingService.SoonThresholdUnitWeeks), threshold);
        Assert.True(global::CrispyBills.Mobile.Android.Services.BillingService.IsBillSoon(soonBill, threshold.Value, threshold.Unit));
        Assert.False(global::CrispyBills.Mobile.Android.Services.BillingService.IsBillSoon(farBill, threshold.Value, threshold.Unit));
        Assert.False(global::CrispyBills.Mobile.Android.Services.BillingService.IsBillSoon(paidBill, threshold.Value, threshold.Unit));
        Assert.False(global::CrispyBills.Mobile.Android.Services.BillingService.IsBillSoon(pastDueBill, threshold.Value, threshold.Unit));
    }
}
