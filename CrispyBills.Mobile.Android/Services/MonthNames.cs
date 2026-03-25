namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Helper for obtaining localized month names from the current culture.
/// </summary>
public static class MonthNames
{
    private static readonly string[] _all = GetAll();

    /// <summary>All month names for the current culture (January..December).</summary>
    public static string[] All => _all;

    /// <summary>Return the localized name for a month number (1-12) or "Unknown" if out of range.</summary>
    public static string Name(int month)
    {
        return month is >= 1 and <= 12 ? _all[month - 1] : "Unknown";
    }

    private static string[] GetAll()
    {
        var names = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;
        return names.Take(12).ToArray();
    }
}
