namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Helper for obtaining localized month names from the current culture.
/// </summary>
public static class MonthNames
{
    /// <summary>All month names for the current culture (January..December).</summary>
    public static string[] All => GetAll();

    /// <summary>Return the localized name for a month number (1-12) or "Unknown" if out of range.</summary>
    public static string Name(int month)
    {
        var all = GetAll();
        return month is >= 1 and <= 12 ? all[month - 1] : "Unknown";
    }

    private static string[] GetAll()
    {
        var names = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;
        return names.Take(12).ToArray();
    }
}
