using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Provides a small catalog of common bill templates used to seed new drafts.
/// Templates are static and intended for quick creation of common recurring bills.
/// </summary>
public static class BillTemplateCatalog
{
    private static readonly IReadOnlyList<BillTemplate> Templates = new List<BillTemplate>
    {
        new("Rent", "Housing", 1200m, 1, true, 1, RecurrenceEndMode.None, null, null),
        new("Electricity", "Utilities", 95m, 10, true, 1, RecurrenceEndMode.None, null, null),
        new("Water", "Utilities", 45m, 12, true, 1, RecurrenceEndMode.None, null, null),
        new("Internet", "Utilities", 80m, 15, true, 1, RecurrenceEndMode.None, null, null),
        new("Mobile Phone", "Utilities", 55m, 18, true, 1, RecurrenceEndMode.None, null, null),
        new("Insurance", "Insurance", 140m, 20, true, 1, RecurrenceEndMode.None, null, null),
        new("Car Payment", "Transportation", 280m, 5, true, 1, RecurrenceEndMode.None, null, null),
        new("Streaming Subscription", "Subscriptions", 15m, 22, true, 1, RecurrenceEndMode.None, null, null),
        new("Quarterly Tax", "Taxes", 500m, 15, true, 3, RecurrenceEndMode.None, null, null),
        new("Annual Membership", "Subscriptions", 120m, 5, true, 12, RecurrenceEndMode.None, null, null)
    };

    /// <summary>Return all built-in bill templates.</summary>
    public static IReadOnlyList<BillTemplate> GetAll()
    {
        return Templates;
    }
}
