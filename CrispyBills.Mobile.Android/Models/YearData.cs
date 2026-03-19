namespace CrispyBills.Mobile.Android.Models;

public sealed class YearData
{
    public Dictionary<int, List<BillItem>> BillsByMonth { get; } = new();
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
