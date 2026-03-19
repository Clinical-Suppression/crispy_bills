namespace CrispyBills.Mobile.Android.Models;

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
