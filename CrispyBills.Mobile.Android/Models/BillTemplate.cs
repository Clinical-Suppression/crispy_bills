namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Template used to seed new bills. Contains suggested amount, due day,
/// and recurrence configuration to create draft bill instances.
/// </summary>
public sealed record BillTemplate(
    string Name,
    string Category,
    decimal SuggestedAmount,
    int SuggestedDueDay,
    bool IsRecurring,
    int RecurrenceEveryMonths,
    RecurrenceEndMode RecurrenceEndMode,
    DateTime? RecurrenceEndDate,
    int? RecurrenceMaxOccurrences);
