namespace CrispyBills.Mobile.Android.Services;

public static class MonthNames
{
    public static string[] All => GetAll();

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
