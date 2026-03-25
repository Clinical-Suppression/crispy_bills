namespace CrispyBills.Mobile.Android.Models;

/// <summary>How a recurring bill repeats. Monthly interval uses <see cref="BillItem.RecurrenceEveryMonths"/>.</summary>
public enum RecurrenceFrequency
{
    /// <summary>Not used when <see cref="BillItem.IsRecurring"/> is false.</summary>
    None = 0,
    Weekly = 1,
    BiWeekly = 2,
    MonthlyInterval = 3
}
