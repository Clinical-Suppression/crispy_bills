using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace CrispyBills;

public partial class DiagnosticsWindow : Window
{
    private readonly string _dataRoot;
    private readonly string _backupsRoot;

    public DiagnosticsWindow(string dataRoot, string backupsRoot)
    {
        InitializeComponent();
        _dataRoot = dataRoot;
        _backupsRoot = backupsRoot;
        var logsDir = Path.Combine(backupsRoot, "logs");
        PathsBox.Text =
            $"Data root:{Environment.NewLine}{dataRoot}{Environment.NewLine}{Environment.NewLine}" +
            $"Backups / logs root:{Environment.NewLine}{backupsRoot}{Environment.NewLine}{Environment.NewLine}" +
            $"Log directory:{Environment.NewLine}{logsDir}";

        try
        {
            var logFiles = Directory.GetFiles(logsDir, "*.log")
                .OrderByDescending(f => f)
                .Take(1)
                .ToArray();
            if (logFiles.Length > 0)
            {
                var lines = File.ReadAllLines(logFiles[0]);
                var tail = lines.Length > 200 ? lines.Skip(lines.Length - 200) : lines;
                LogBox.Text = string.Join(Environment.NewLine, tail);
                LogBox.ScrollToEnd();
            }
            else
            {
                LogBox.Text = "No log entries found.";
            }
        }
        catch
        {
            LogBox.Text = "Could not read log files.";
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder:{Environment.NewLine}{ex.Message}", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenInExplorer(_dataRoot);

    private void OpenBackups_Click(object sender, RoutedEventArgs e) => OpenInExplorer(_backupsRoot);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RunIntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = Directory.GetFiles(_dataRoot, "CrispyBills_*.db");
            var years = files
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(n => n!.Replace("CrispyBills_", ""))
                .Where(s => int.TryParse(s, out _))
                .Select(int.Parse)
                .ToList();

            var report = new System.Text.StringBuilder();
            int totalScanned = 0, totalIssues = 0;

            foreach (var year in years.OrderBy(y => y))
            {
                var dbPath = Path.Combine(_dataRoot, $"CrispyBills_{year}.db");
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                using var readCmd = conn.CreateCommand();
                readCmd.CommandText = "SELECT Id, Month, Name, Amount, Category FROM Bills WHERE Year = $year;";
                readCmd.Parameters.AddWithValue("$year", year);

                int yearScanned = 0, yearIssues = 0;
                var seenByMonth = new Dictionary<int, HashSet<string>>();

                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                {
                    yearScanned++;
                    var id = reader.GetValue(0)?.ToString() ?? "";
                    var month = Convert.ToInt32(reader.GetValue(1));
                    var name = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var amount = reader.GetDouble(3);
                    var category = reader.IsDBNull(4) ? "" : reader.GetString(4);

                    if (!seenByMonth.ContainsKey(month)) seenByMonth[month] = new HashSet<string>();
                    if (string.IsNullOrWhiteSpace(name)) yearIssues++;
                    if (amount < 0) yearIssues++;
                    if (string.IsNullOrWhiteSpace(category)) yearIssues++;
                    if (!seenByMonth[month].Add(id)) yearIssues++;
                }

                totalScanned += yearScanned;
                totalIssues += yearIssues;
                report.AppendLine($"Year {year}: {yearScanned} bills scanned, {yearIssues} issues found");
            }

            report.AppendLine();
            report.AppendLine($"Total: {totalScanned} bills scanned, {totalIssues} issues found");

            if (totalIssues == 0)
                report.AppendLine("No integrity issues detected.");
            else
                report.AppendLine("Issues detected. Review the data or re-import from a backup if needed.");

            MessageBox.Show(report.ToString(), "Integrity Check Results", MessageBoxButton.OK,
                totalIssues > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not complete integrity check: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
