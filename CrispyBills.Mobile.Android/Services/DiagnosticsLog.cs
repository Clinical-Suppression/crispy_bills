using System.Text;

namespace CrispyBills.Mobile.Android.Services;

public static class DiagnosticsLog
{
    private const int LogRetentionDays = 30;
    private static readonly string LogDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills", "logs");

    public static string CurrentLogPath => Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");

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

            // Log retention: delete logs older than LogRetentionDays
            var files = Directory.GetFiles(LogDirectory, "mobile_*.log");
            var threshold = DateTime.Now.AddDays(-LogRetentionDays);
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name.Replace("mobile_", string.Empty), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                {
                    if (dt < threshold)
                    {
                        try { File.Delete(file); }
                        catch (Exception innerEx) { await WriteAsync("DiagnosticsLogFileDelete", innerEx); }
                    }
                }
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
