using System;
using System.IO;
using System.Text;
using System.Windows;

namespace CrispyBills
{
    /// <summary>
    /// WPF application entry point; startup resources and initial window are defined in App.xaml.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                AppSettings.LoadAndApplyCulture();
                base.OnStartup(e);

                var window = new MainWindow();
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                var logPath = TryWriteStartupFailureLog(ex);
                var body = new StringBuilder()
                    .AppendLine("Crispy Bills could not start.")
                    .AppendLine()
                    .AppendLine(ex.ToString());

                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    body.AppendLine()
                        .AppendLine($"Details were written to:")
                        .AppendLine(logPath);
                }

                MessageBox.Show(
                    body.ToString(),
                    "Crispy Bills startup error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }

        private static string? TryWriteStartupFailureLog(Exception ex)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "CrispyBills",
                    "db_backups",
                    "logs");
                Directory.CreateDirectory(logDir);

                var path = Path.Combine(logDir, $"startup_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(path, ex.ToString());
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}