using System.Text;

namespace CrispyBills.Mobile.Android.Services;

public static class DiagnosticsLog
{
    private static readonly string LogDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills", "logs");

    public static string CurrentLogPath => Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");

    public static async Task WriteAsync(string area, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"mobile_{DateTime.Now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:O}] {area}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            await File.AppendAllTextAsync(path, sb.ToString());
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
