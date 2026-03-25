using System;
using System.Diagnostics;
using System.IO;
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
}
