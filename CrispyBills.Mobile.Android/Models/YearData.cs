namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// In-memory container for a single year's bill and income data.
/// Keys are month numbers (1-12).
/// </summary>
public sealed class YearData
{
    /// <summary>Mapping of month number to the bills for that month.</summary>
    public Dictionary<int, List<BillItem>> BillsByMonth { get; } = new();

    /// <summary>Mapping of month number to income values.</summary>
    public Dictionary<int, decimal> IncomeByMonth { get; } = new();

    public YearData()
    {
        for (var month = 1; month <= 12; month++)
        {
            BillsByMonth[month] = new List<BillItem>();
            IncomeByMonth[month] = 0m;
        }
    }
}
