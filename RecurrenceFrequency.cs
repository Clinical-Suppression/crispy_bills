namespace CrispyBills
{
    /// <summary>How a recurring bill repeats. Monthly uses <see cref="Bill.RecurrenceEveryMonths"/>.</summary>
    public enum RecurrenceFrequency
    {
        None = 0,
        Weekly = 1,
        BiWeekly = 2,
        MonthlyInterval = 3
    }
}
