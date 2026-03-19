namespace CrispyBills.Mobile.Android.Services;

public static class StartupDiagnostics
{
    private static readonly List<string> Issues = new();

    public static void AddIssue(string area, string message)
    {
        Issues.Add($"[{DateTime.Now:O}] {area}: {message}");
    }

    public static IReadOnlyList<string> GetIssues()
    {
        return Issues.ToList();
    }

    public static bool HasIssues => Issues.Count > 0;

    public static void Clear()
    {
        Issues.Clear();
    }
}
