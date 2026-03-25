using System.Text;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Lightweight file-backed diagnostics logger for the mobile app. Keeps daily
/// log files and prunes old logs automatically. Designed to be best-effort and
/// never throw to calling code.
/// </summary>
public static class DiagnosticsLog
{
    private const int LogRetentionDays = 30;
    private static string? _logDirectory;
    private static DateTime _lastRetentionCleanupUtc = DateTime.MinValue;

    /// <summary>
    /// Lazily resolved log directory. Using a static readonly field initializer here would call
    /// <c>FileSystem.Current.AppDataDirectory</c> at class-load time, which crashes with
    /// <see cref="TypeInitializationException"/> if the class is touched before MAUI initializes
    /// the platform services -- a cascading failure that hides the original crash.
    /// </summary>
    private static string LogDirectory
    {
        get
        {
            if (_logDirectory is null)
            {
                try
                {
                    _logDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills", "logs");
                }
                catch
                {
                    _logDirectory = Path.Combine(Path.GetTempPath(), "CrispyBills", "logs");
                }
            }
            return _logDirectory;
        }
    }

    /// <summary>Path to the current day's log file.</summary>
    public static string CurrentLogPath => Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Synchronous best-effort write for fatal startup paths where async is unsafe
    /// or the process may terminate immediately.
    /// </summary>
    public static void WriteSync(string area, Exception ex, string severity = "Fatal")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:O}] [{severity}] {area}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // Logging must never crash app flows.
        }
    }

    public static void WriteSync(string area, string message, string severity = "Error")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] [{severity}] {area}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static async Task WriteAsync(string area, Exception ex)
    {
        await WriteAsync(area, ex, "Error");
    }

    public static async Task WriteAsync(string area, Exception ex, string severity)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:O}] [{severity}] {area}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            await File.AppendAllTextAsync(path, sb.ToString());

            // Log retention: delete logs older than LogRetentionDays (at most once per hour)
            if ((DateTime.UtcNow - _lastRetentionCleanupUtc).TotalHours >= 1)
            {
                var files = Directory.GetFiles(LogDirectory, "mobile_*.log");
                var threshold = DateTime.Now.AddDays(-LogRetentionDays);
                var retentionDeleteFailures = 0;
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (DateTime.TryParseExact(name.Replace("mobile_", string.Empty), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        if (dt < threshold)
                        {
                            try { File.Delete(file); }
                            catch { retentionDeleteFailures++; }
                        }
                    }
                }

                if (retentionDeleteFailures > 0)
                {
                    await File.AppendAllTextAsync(path, $"[{DateTime.Now:O}] [Warning] DiagnosticsLog retention delete failures: {retentionDeleteFailures}{Environment.NewLine}{Environment.NewLine}");
                }

                _lastRetentionCleanupUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // Logging must never crash app flows.
        }
    }

    public static async Task<string> ReadCurrentAsync()
    {
        try
        {
            var path = CurrentLogPath;
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return await File.ReadAllTextAsync(path);
        }
        catch
        {
            return string.Empty;
        }
    }
}
