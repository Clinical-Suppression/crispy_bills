namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Lightweight in-memory collector for startup-time diagnostic issues.
/// Used to capture non-fatal initialization problems that can be surfaced
/// to the UI or log for troubleshooting.
/// </summary>
public static class StartupDiagnostics
{
    private static readonly List<string> Issues = new();
    private static readonly object Sync = new();

    /// <summary>Add a diagnostic issue with an area label and message.</summary>
    public static void AddIssue(string area, string message)
    {
        lock (Sync)
        {
            Issues.Add($"[{DateTime.Now:O}] {area}: {message}");
        }
    }

    /// <summary>Return a snapshot list of collected issues.</summary>
    public static IReadOnlyList<string> GetIssues()
    {
        lock (Sync)
        {
            return Issues.ToList();
        }
    }

    /// <summary>Whether any issues were collected during startup.</summary>
    public static bool HasIssues
    {
        get
        {
            lock (Sync)
            {
                return Issues.Count > 0;
            }
        }
    }

    /// <summary>Clear all collected startup diagnostic issues.</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            Issues.Clear();
        }
    }
}
