using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;   // kept for Bill
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Threading.Tasks;
using CrispyBills.Core.Storage;
using CrispyBills;

namespace CrispyBills
{
    /// <summary>
    /// Main application window that orchestrates data loading, editing workflows,
    /// visualization, theming, and persistence for the selected year/month.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CrispyBills");

        private string GetDbPath(string year) =>
            Path.Combine(dataRoot, $"CrispyBills_{year}.db");

        private readonly string backupsRoot;
        private readonly string appDbPath;
        private readonly string legacyAppDbPath;
        private readonly string legacyNotesPath;

        public Dictionary<string, ObservableCollection<Bill>> AnnualData { get; set; } = [];
        public Dictionary<string, decimal> MonthlyIncome { get; set; } = [];

        private readonly string[] months =
            ["January", "February", "March", "April", "May", "June",
              "July", "August", "September", "October", "November", "December"];

        private static int MonthNumberFromName(string monthName)
        {
            var idx = Array.FindIndex(new[]
            {
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            }, m => string.Equals(m, monthName, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx + 1 : 0;
        }

        private string MonthNameFromDbReader(SqliteDataReader reader, int column)
        {
            if (reader.IsDBNull(column)) return string.Empty;
            var value = reader.GetValue(column);
            if (value is long l && l >= 1 && l <= 12) return months[l - 1];
            if (value is int i && i >= 1 && i <= 12) return months[i - 1];
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed >= 1 && parsed <= 12)
            {
                return months[parsed - 1];
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return months.FirstOrDefault(m => string.Equals(m, text, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        private bool isDarkMode = false;

        private readonly Stack<UndoRedoAction> _undoStack = new();
        private readonly Stack<UndoRedoAction> _redoStack = new();

        private string _currentYear = DateTime.Now.Year.ToString();
        private string CurrentYear => _currentYear;

        private string? _detailedCategory = null;
        private const int MaxNoteLines = 500;
        private const int MaxUndoActions = 500;
        private const string NotesLineLimitMessage = "Notes are limited to 500 lines.";
        private bool _isUpdatingNotesText;
        private GridEditSnapshot? _pendingGridEdit;
        private int _gridEditVersion;
        private bool _gridCommitPending;
        private bool _enterMovesDown;
        private DispatcherTimer? _routineBackupTimer;
        private bool _allowImmediateClose;
        private bool _closingBackupInProgress;
        private bool _isFilteringActive = false;
        private string? _lastBillCountWarningKey;
        private bool _yearSizeWarningShown;
        // Session-only guards for destructive debug tools
        private bool _debugDestructiveDeletesEnabled = false;
        private bool _debugEnableWarningShown = false;
        private readonly List<string> _startupIssues = new();
        private string _themeMode = "system";
        private int _soonThresholdValue = 3;
        private string _soonThresholdUnit = "Days";
        private HashSet<int> _archivedYears = new();
        private bool _showArchivedYears;
        private static readonly Regex DueDatePattern = GeneratedDueDatePattern();
        private static readonly TimeSpan RoutineBackupInterval = TimeSpan.FromMinutes(15);

        // Captures original values before inline grid edits so confirmation, undo, and redo can be applied safely.
        private sealed record GridEditSnapshot(Bill Bill, string Month, string Name, decimal Amount, DateTime DueDate, string Category);
        private sealed record PersistenceSnapshot(string Year, Dictionary<string, List<Bill>> BillsByMonth, Dictionary<string, decimal> IncomeByMonth, string NotesText);
        private sealed record PieCategoryRow(string Name, decimal Total);

        /// <summary>
        /// Initializes UI state, ensures database storage exists, loads the current year,
        /// and restores global notes.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            backupsRoot = Path.Combine(dataRoot, "db_backups");
            appDbPath = Path.Combine(dataRoot, "CrispyBills_Notes.db");
            legacyAppDbPath = Path.Combine(dataRoot, "CrispyBills_App.db");
            legacyNotesPath = Path.Combine(dataRoot, "GlobalNotes.txt");

            EnsureDataRoot();
            InitializeAppDatabase();
            MigrateLegacyDatabases();
            LoadThemePreference();
            _soonThresholdValue = LoadAppMeta("soon_threshold_value", 3);
            _soonThresholdUnit = LoadAppMeta("soon_threshold_unit", "Days");
            LoadArchivedYears();

            foreach (var m in months)
            {
                AnnualData[m] = [];
                MonthlyIncome[m] = 0;
            }

            InitializeDatabase(GetDbPath(CurrentYear));

            MonthSelector.ItemsSource = months.ToList();
            MonthSelector.SelectedIndex = DateTime.Now.Month - 1;

            EnsureAvailableYears();
            UpdateYearSelector();

            LoadData();
            LoadGlobalNotes();

            PayPeriodCombo.Items.Add("/ month");
            PayPeriodCombo.Items.Add("/ week");
            PayPeriodCombo.Items.Add("/ 2 weeks");
            var savedPeriod = LoadAppMeta("income_pay_period", "/ month");
            if (PayPeriodCombo.Items.Contains(savedPeriod))
                PayPeriodCombo.SelectedItem = savedPeriod;
            else
                PayPeriodCombo.SelectedIndex = 0;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateDashboard();
            StartRoutineBackups();
            ApplyThemeFromMode();

            if (_startupIssues.Count > 0)
            {
                var result = MessageBox.Show(
                    $"The app encountered {_startupIssues.Count} issue(s) during startup:\n\n{string.Join("\n", _startupIssues.Take(5))}\n\nWould you like to view diagnostics?",
                    "Startup Issues",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    OpenDiagnostics_Click(this, new RoutedEventArgs());
                }
            }

            // Run auto-test diagnostics only when explicitly opted in to avoid accidental startup side effects.
            try
            {
                var autoTestPath = Path.Combine(dataRoot, "auto_test_roundtrip.txt");
                bool runAutoTest = string.Equals(
                    Environment.GetEnvironmentVariable("CRISPYBILLS_RUN_AUTOTEST"),
                    "1",
                    StringComparison.Ordinal);

                if (runAutoTest && File.Exists(autoTestPath))
                {
                    var tmp = Path.Combine(backupsRoot, "auto_tests");
                    Directory.CreateDirectory(tmp);
                    var preDiag = Path.Combine(tmp, $"db_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    WriteDbSnapshotDiagnostics(preDiag);

                    var exportPath = Path.Combine(tmp, $"auto_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    ExportCsvToPath(exportPath);
                    var lines = File.ReadAllLines(exportPath);
                    var pkg = ParseStructuredReportCsv(lines);
                    var testDbFolder = Path.Combine(tmp, "test_dbs");
                    Directory.CreateDirectory(testDbFolder);
                    ApplyPackageToTestDbs(pkg, testDbFolder);

                    MessageBox.Show(this, $"Auto export+parse completed. Diagnostics and test DBs saved.", "Auto Test", MessageBoxButton.OK, MessageBoxImage.Information);
                    // Remove sentinel so auto-test doesn't re-run on every launch.
                    try { File.Delete(autoTestPath); }
                    catch (Exception ex) { LogNonFatal("AutoTestPathDelete", ex); }
                }
            }
            catch (Exception ex)
            {
                LogNonFatal("Window_Loaded auto-test diagnostics", ex);
            }
        }

        private void WriteDbSnapshotDiagnostics(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Database snapshot diagnostics");
                sb.AppendLine($"Generated: {DateTime.Now:f}");
                sb.AppendLine();

                var years = GetAvailableYears().OrderBy(y => y).ToList();
                foreach (var year in years)
                {
                    var (billsByMonth, incomeByMonth) = LoadYearExportData(year);
                    sb.AppendLine($"Year: {year}");
                    foreach (var m in months)
                    {
                        var count = billsByMonth.GetValueOrDefault(m)?.Count ?? 0;
                        var income = incomeByMonth.GetValueOrDefault(m, 0m);
                        sb.AppendLine($"  {m}: {count} bill(s), Income: {income:F2}");
                    }
                    sb.AppendLine();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? backupsRoot);
                File.WriteAllText(path, sb.ToString());
                SetStatus($"DB snapshot written: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                LogNonFatal("WriteDbSnapshotDiagnostics", ex);
            }
        }

        private void ApplyPackageToTestDbs(StructuredImportPackage package, string testDbFolder)
        {
            try
            {
                foreach (var entry in package.Years)
                {
                    var year = entry.Key;
                    var data = entry.Value;
                    var dbPath = Path.Combine(testDbFolder, $"CrispyBills_{year}_test.db");
                    InitializeDatabase(dbPath);
                    PersistYearDataToDatabase(dbPath, year, data.BillsByMonth, data.IncomeByMonth);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Test DB persistence diagnostics");
                sb.AppendLine($"Generated: {DateTime.Now:f}");
                sb.AppendLine();
                foreach (var entry in package.Years.OrderBy(kv => kv.Key))
                {
                    var year = entry.Key;
                    sb.AppendLine($"Year: {year}");
                    foreach (var m in months)
                    {
                        var count = entry.Value.BillsByMonth.GetValueOrDefault(m)?.Count ?? 0;
                        var income = entry.Value.IncomeByMonth.GetValueOrDefault(m, 0m);
                        sb.AppendLine($"  {m}: {count} bill(s), Income: {income:F2}");
                    }
                    sb.AppendLine();
                }

                var diagPath = Path.Combine(testDbFolder, $"testdb_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(diagPath, sb.ToString());
                SetStatus($"Test DBs and diagnostics written: {Path.GetFileName(diagPath)}");
            }
            catch (Exception ex)
            {
                try
                {
                    var err = Path.Combine(testDbFolder, $"testdb_error_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(err, ex.ToString());
                }
                catch (Exception writeEx)
                {
                    LogNonFatal("ApplyPackageToTestDbs error file write", writeEx);
                }
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowImmediateClose)
            {
                _routineBackupTimer?.Stop();
                return;
            }

            // Prevent routine backups from queueing while shutdown backup is running.
            _routineBackupTimer?.Stop();

            e.Cancel = true;
            if (_closingBackupInProgress) return;

            _closingBackupInProgress = true;
            _ = RunClosingBackupSequenceAsync();
        }

        private async Task RunClosingBackupSequenceAsync()
        {
            Window? popup = null;
            try
            {
                popup = new Window
                {
                    Title = "Closing Crispy_Bills",
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow,
                    Width = 360,
                    Height = 160,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = TryFindResource("PanelBackgroundBrush") as Brush ?? Brushes.White,
                    Foreground = TryFindResource("WindowForegroundBrush") as Brush ?? Brushes.Black
                };

                var statusText = new TextBlock
                {
                    Text = "Saving and creating backups...",
                    Margin = new Thickness(16, 12, 16, 10),
                    TextWrapping = TextWrapping.Wrap
                };

                var progress = new ProgressBar
                {
                    IsIndeterminate = true,
                    Height = 16,
                    Margin = new Thickness(16, 0, 16, 8)
                };

                popup.Content = new StackPanel
                {
                    Children =
                    {
                        statusText,
                        progress,
                        new TextBlock
                        {
                            Text = "The app will close automatically.",
                            Margin = new Thickness(16, 0, 16, 0),
                            Opacity = 0.8
                        }
                    }
                };

                popup.Show();
                var startedAt = DateTime.UtcNow;
                var snapshot = CreatePersistenceSnapshot();
                var closeBackupTimeout = TimeSpan.FromSeconds(20);

                try
                {
                    var persistTask = Task.Run(() => PersistSnapshotToDisk(snapshot, createBackup: true));
                    var completedTask = await Task.WhenAny(persistTask, Task.Delay(closeBackupTimeout));

                    if (completedTask == persistTask)
                    {
                        await persistTask;
                        statusText.Text = "Backup complete. Closing app...";
                    }
                    else
                    {
                        LogNonFatal($"RunClosingBackupSequenceAsync timeout after {closeBackupTimeout.TotalSeconds:0} seconds");
                        statusText.Text = "Backup is taking longer than expected. Waiting for it to finish before closing...";
                        await persistTask;
                        statusText.Text = "Backup complete. Closing app...";
                    }
                }
                catch (Exception ex)
                {
                    LogNonFatal("RunClosingBackupSequenceAsync PersistData", ex);
                    statusText.Text = "Backup encountered an issue. Closing app...";
                }

                var elapsed = DateTime.UtcNow - startedAt;
                var minimumDisplay = TimeSpan.FromSeconds(3);
                if (elapsed < minimumDisplay)
                {
                    await Task.Delay(minimumDisplay - elapsed);
                }
            }
            finally
            {
                try
                {
                    if (popup?.IsVisible == true) popup.Close();
                }
                catch (Exception ex)
                {
                    LogNonFatal("RunClosingBackupSequenceAsync popup close", ex);
                }

                _allowImmediateClose = true;
                _closingBackupInProgress = false;
                Close();
            }
        }

        #region Undo/Redo helpers
        private record UndoRedoAction(Action Undo, Action Redo);

        private void PushUndo(Action undo, Action redo)
        {
            _undoStack.Push(new UndoRedoAction(undo, redo));

            if (_undoStack.Count > MaxUndoActions)
            {
                // Keep the newest actions and drop the oldest to cap memory growth.
                var kept = _undoStack.Take(MaxUndoActions).Reverse().ToList();
                _undoStack.Clear();
                foreach (var action in kept)
                {
                    _undoStack.Push(action);
                }
            }

            _redoStack.Clear(); // new action invalidates redo history
            CommandManager.InvalidateRequerySuggested();
        }
        #endregion

        #region Database helpers

        private void EnsureDataRoot()
        {
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(backupsRoot);
        }

        private void LogNonFatal(string area, Exception? ex = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:O}] {area}");
            if (ex != null)
            {
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine();

            try
            {
                var logDir = Path.Combine(backupsRoot, "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, $"nonfatal_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(logPath, sb.ToString());
                return;
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"LogNonFatal primary write failed: {logEx.Message}");
            }

            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"nonfatal_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(logPath, sb.ToString());
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"LogNonFatal fallback write failed: {logEx.Message}");
            }
        }

        private void LogNonFatalMessage(string message)
        {
            LogNonFatal(message);
        }

        private void MigrateLegacyDatabases()
        {
            try
            {
                var legacyRoot = AppDomain.CurrentDomain.BaseDirectory;
                if (string.Equals(
                    Path.GetFullPath(legacyRoot).TrimEnd('\\'),
                    Path.GetFullPath(dataRoot).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (var src in Directory.GetFiles(legacyRoot, "CrispyBills_*.db"))
                {
                    var dest = Path.Combine(dataRoot, Path.GetFileName(src));
                    if (!File.Exists(dest))
                    {
                        File.Copy(src, dest);
                    }
                    // Always remove the legacy source so it can never re-seed Documents on future launches.
                    try { File.Delete(src); }
                    catch (Exception ex)
                    {
                        LogNonFatal($"MigrateLegacyDatabases delete source: {src}", ex);
                    }
                }

                // Migrate legacy backup files once to keep recovery history.
                var legacyBackupRoot = Path.Combine(legacyRoot, "db_backups");
                if (Directory.Exists(legacyBackupRoot))
                {
                    foreach (var yearDir in Directory.GetDirectories(legacyBackupRoot))
                    {
                        var yearName = Path.GetFileName(yearDir);
                        if (string.IsNullOrWhiteSpace(yearName)) continue;

                        var destYearDir = Path.Combine(backupsRoot, yearName);
                        Directory.CreateDirectory(destYearDir);

                        foreach (var srcBackup in Directory.GetFiles(yearDir, "*.db"))
                        {
                            var destBackup = Path.Combine(destYearDir, Path.GetFileName(srcBackup));
                            if (!File.Exists(destBackup))
                            {
                                File.Copy(srcBackup, destBackup);
                            }
                            try { File.Delete(srcBackup); }
                            catch (Exception ex)
                            {
                                LogNonFatal($"MigrateLegacyDatabases delete backup: {srcBackup}", ex);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogNonFatal("MigrateLegacyDatabases", ex);
            }
        }

        private void EnsureAvailableYears()
        {
            if (!File.Exists(GetDbPath(CurrentYear)))
                InitializeDatabase(GetDbPath(CurrentYear));
        }

        private static void EnsureBillsColumn(SqliteConnection conn, string columnName, string definition)
        {
            bool exists = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Bills);";
                using var r = pragma.ExecuteReader();
                while (r.Read())
                {
                    if (string.Equals(r.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE Bills ADD COLUMN {columnName} {definition};";
                alter.ExecuteNonQuery();
            }
        }

        private static void InitializeDatabase(string dbFilePath)
        {
            var dbDir = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
            // Use SqliteConnectionStringBuilder for safe connection string construction.
            var csb = new SqliteConnectionStringBuilder { DataSource = dbFilePath };
            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS Bills (Id TEXT, Month INTEGER, Year INTEGER, Name TEXT, Amount REAL, DueDate TEXT, Paid INT, Category TEXT, Recurring INT NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS Income (Month INTEGER, Year INTEGER, Amount REAL, PRIMARY KEY(Month, Year));";
            cmd.ExecuteNonQuery();

            // Backward-compatible schema migration for existing databases.
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Bills ADD COLUMN Recurring INT NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }
            catch
            {
                // Column already exists or db engine rejected no-op migration; safe to ignore.
            }

            EnsureBillsColumn(conn, "RecurrenceEveryMonths", "INTEGER NOT NULL DEFAULT 1");
            EnsureBillsColumn(conn, "RecurrenceEndMode", "TEXT NOT NULL DEFAULT 'None'");
            EnsureBillsColumn(conn, "RecurrenceEndDate", "TEXT NULL");
            EnsureBillsColumn(conn, "RecurrenceMaxOccurrences", "INTEGER NULL");
            EnsureBillsColumn(conn, "RecurrenceFrequency", "TEXT NOT NULL DEFAULT 'MonthlyInterval'");
            EnsureBillsColumn(conn, "RecurrenceGroupId", "TEXT NULL");
            CanonicalSchemaMigrator.EnsureCanonicalSchema(conn);
        }

        private void InitializeAppDatabase()
        {
            var appDbDir = Path.GetDirectoryName(appDbPath);
            if (!string.IsNullOrEmpty(appDbDir))
            {
                Directory.CreateDirectory(appDbDir);
            }

            var csb = new SqliteConnectionStringBuilder { DataSource = appDbPath };
            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                CREATE TABLE IF NOT EXISTS AppMeta ([Key] TEXT PRIMARY KEY, [Value] TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }

        private List<string> GetAvailableYears()
        {
            var files = Directory.GetFiles(dataRoot, "CrispyBills_*.db");
            var years = new SortedSet<int>();
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var parts = name.Split('_');
                if (parts.Length >= 2 && int.TryParse(parts[^1], out int y))
                    years.Add(y);
            }

            if (int.TryParse(_currentYear, out int cy)) years.Add(cy);

            return [.. years.Reverse().Select(y => y.ToString())];
        }

        private void UpdateYearSelector()
        {
            var years = GetAvailableYears();
            if (!_showArchivedYears && _archivedYears.Count > 0)
                years = years.Where(y => !int.TryParse(y, out var yi) || !_archivedYears.Contains(yi) || y == CurrentYear).ToList();
            YearSelector.ItemsSource = years;
            if (years.Contains(CurrentYear)) YearSelector.SelectedItem = CurrentYear;
        }

        /// <summary>
        /// Loads all bill and income records for the active year from SQLite into memory,
        /// then refreshes the dashboard and clears undo/redo history.
        /// </summary>
        private void LoadData()
        {
            foreach (var m in months)
            {
                AnnualData[m].Clear();
                MonthlyIncome[m] = 0;
            }

            var dbFile = GetDbPath(CurrentYear);
            // Always initialize to ensure correct journal mode (WAL) and schema
            InitializeDatabase(dbFile);
            var loadCsb = new SqliteConnectionStringBuilder { DataSource = dbFile };
            using var conn = new SqliteConnection(loadCsb.ConnectionString);
            conn.Open();

            using var bCmd = conn.CreateCommand();
            bCmd.CommandText = @"
SELECT Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
       RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences,
       RecurrenceFrequency, RecurrenceGroupId
FROM Bills WHERE Year = $y";
            bCmd.Parameters.AddWithValue("$y", CurrentYear);
            using (var r = bCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    // Guard against NULL columns from corrupt or legacy data.
                    string mKey = MonthNameFromDbReader(r, 1);
                    if (string.IsNullOrEmpty(mKey) || !AnnualData.ContainsKey(mKey))
                        continue;

                    try
                    {
                        var guidStr = r.GetString(0);
                        if (!Guid.TryParse(guidStr, out var id))
                        {
                            LogNonFatal($"Skipping row with invalid GUID: {guidStr}");
                            _startupIssues.Add($"Skipping row with invalid GUID: {guidStr}");
                            continue;
                        }
                        var bill = new Bill
                        {
                            Id = id,
                            Name = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                            Amount = r.IsDBNull(4) ? 0m : r.GetDecimal(4),
                            DueDate = r.IsDBNull(5)
                                ? DateTime.Today
                                : DateTime.ParseExact(r.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                            IsPaid = !r.IsDBNull(6) && r.GetInt32(6) == 1,
                            Category = r.IsDBNull(7) ? "General" : r.GetString(7),
                            IsRecurring = r.FieldCount > 8 && !r.IsDBNull(8) && r.GetInt32(8) == 1,
                            RecurrenceEveryMonths = r.FieldCount > 9 && !r.IsDBNull(9) ? Math.Max(1, r.GetInt32(9)) : 1,
                            RecurrenceEndMode = r.FieldCount > 10 && !r.IsDBNull(10)
                                ? ParseRecurrenceEndModeWpf(r.GetString(10))
                                : RecurrenceEndMode.None,
                            RecurrenceEndDate = r.FieldCount > 11 && !r.IsDBNull(11)
                                && DateTime.TryParse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.None, out var endD)
                                ? endD
                                : null,
                            RecurrenceMaxOccurrences = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetInt32(12) : null,
                            RecurrenceFrequency = r.FieldCount > 13 && !r.IsDBNull(13)
                                ? ParseRecurrenceFrequencyWpf(r.GetString(13))
                                : RecurrenceFrequency.MonthlyInterval,
                            RecurrenceGroupId = r.FieldCount > 14 && !r.IsDBNull(14) && Guid.TryParse(r.GetString(14), out var gid)
                                ? gid
                                : null
                        };

                        if (!bill.IsRecurring)
                        {
                            bill.RecurrenceFrequency = RecurrenceFrequency.None;
                        }
                        else if (bill.RecurrenceFrequency == RecurrenceFrequency.None)
                        {
                            bill.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                        }

                        AssignBillContext(bill, mKey);
                        AnnualData[mKey].Add(bill);
                    }
                    catch (Exception ex)
                    {
                        LogNonFatal($"LoadData: skipping malformed bill row in {mKey}", ex);
                        _startupIssues.Add(ex.Message);
                    }
                }
            }

            using var iCmd = conn.CreateCommand();
            iCmd.CommandText = "SELECT Month, Amount FROM Income WHERE Year = $y";
            iCmd.Parameters.AddWithValue("$y", CurrentYear);
            using (var r = iCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string monthKey = MonthNameFromDbReader(r, 0);
                    if (!string.IsNullOrEmpty(monthKey) && MonthlyIncome.ContainsKey(monthKey))
                        MonthlyIncome[monthKey] = r.IsDBNull(1) ? 0m : r.GetDecimal(1);
                }
            }

            int normalizedDueDates = NormalizeDueDatesForCurrentYear();

            UpdateDashboard();
            _undoStack.Clear();
            _redoStack.Clear();
            CommandManager.InvalidateRequerySuggested();

            if (normalizedDueDates > 0)
            {
                AutoSave();
                SetStatus($"{CurrentYear} loaded. Normalized {normalizedDueDates} due date(s) to their month.");
            }

            _yearSizeWarningShown = false;
            var totalBills = AnnualData.Values.Sum(m => m.Count);
            if (totalBills > 1500 && !_yearSizeWarningShown)
            {
                _yearSizeWarningShown = true;
                MessageBox.Show(
                    $"This year has {totalBills} bill rows across all months. This is typically caused by many weekly recurring bills. Performance is fine, but saves may take a moment.",
                    "Large year",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private int NormalizeDueDatesForCurrentYear()
        {
            if (!int.TryParse(CurrentYear, out int yearValue)) return 0;

            int changedCount = 0;
            for (int i = 0; i < months.Length; i++)
            {
                string monthName = months[i];
                int monthNumber = i + 1;

                foreach (var bill in AnnualData[monthName])
                {
                    var normalized = NormalizeDueDateForMonthContext(bill, yearValue, monthNumber);
                    if (bill.DueDate.Date != normalized.Date)
                    {
                        bill.DueDate = normalized;
                        changedCount++;
                    }
                }
            }

            return changedCount;
        }

        private static DateTime NormalizeDueDateForMonthContext(Bill bill, int targetYear, int targetMonth)
        {
            var firstOfTargetMonth = new DateTime(targetYear, targetMonth, 1);

            // Preserve genuinely overdue carryovers (including cross-year) so they remain visibly past due.
            if (!bill.IsPaid && bill.DueDate.Date < firstOfTargetMonth)
                return bill.DueDate.Date;

            int day = Math.Min(bill.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
            return new DateTime(targetYear, targetMonth, day);
        }

        private static void AssignBillContext(Bill bill, int year, int month)
        {
            bill.ContextPeriodStart = new DateTime(year, month, 1);
        }

        private void AssignBillContext(Bill bill, string monthName)
        {
            if (!int.TryParse(CurrentYear, out int yearValue)) return;
            int monthIndex = Array.IndexOf(months, monthName);
            if (monthIndex < 0) return;

            AssignBillContext(bill, yearValue, monthIndex + 1);
        }

        private void BackupDatabase(string year)
        {
            try
            {
                var src = GetDbPath(year);
                if (!File.Exists(src)) return;

                var destDir = Path.Combine(backupsRoot, year);
                Directory.CreateDirectory(destDir);

                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var dest = Path.Combine(destDir, $"CrispyBills_{year}_{timestamp}.db");

                File.Copy(src, dest, true);

                // Keep only the 10 newest backups
                var backups = Directory.GetFiles(destDir, "*.db").Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.CreationTime).ToList();
                for (int i = 10; i < backups.Count; i++)
                {
                    try { backups[i].Delete(); }
                    catch (Exception ex)
                    {
                        LogNonFatal($"BackupDatabase cleanup: {backups[i].FullName}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                LogNonFatal($"BackupDatabase ({year})", ex);
            }
        }
        private int BackupAllYearDatabases()
        {
            int backedUp = 0;
            try
            {
                var files = Directory.GetFiles(dataRoot, "CrispyBills_*.db");
                foreach (var dbFile in files)
                {
                    var name = Path.GetFileNameWithoutExtension(dbFile);
                    var parts = name.Split('_');
                    if (parts.Length < 2) continue;

                    var yearPart = parts[^1];
                    if (!int.TryParse(yearPart, out _)) continue;

                    BackupDatabase(yearPart);
                    backedUp++;
                }
            }
            catch (Exception ex)
            {
                LogNonFatal("BackupAllYearDatabases scan", ex);
            }

            return backedUp;
        }

        private bool BackupAppDatabase()
        {
            try
            {
                if (!File.Exists(appDbPath)) return false;

                var destDir = Path.Combine(backupsRoot, "notes");
                Directory.CreateDirectory(destDir);

                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var dest = Path.Combine(destDir, $"CrispyBills_Notes_{timestamp}.db");

                File.Copy(appDbPath, dest, true);

                var backups = Directory.GetFiles(destDir, "*.db").Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.CreationTime).ToList();
                for (int i = 10; i < backups.Count; i++)
                {
                    try { backups[i].Delete(); }
                    catch (Exception ex)
                    {
                        LogNonFatal($"BackupAppDatabase cleanup: {backups[i].FullName}", ex);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogNonFatal("BackupAppDatabase", ex);
                return false;
            }
        }

        private bool ConfirmAndBackupBeforeImport(Dictionary<string, List<string>> selectedMonthsByYear, bool includeNotes)
        {
            int yearCount = selectedMonthsByYear.Count;
            int monthCount = selectedMonthsByYear.Sum(entry => entry.Value.Count);
            bool targetsCurrentYear = selectedMonthsByYear.Keys.Contains(CurrentYear, StringComparer.OrdinalIgnoreCase);
            int currentYearBillCount = targetsCurrentYear
                ? selectedMonthsByYear[CurrentYear].Sum(month => AnnualData.TryGetValue(month, out var bills) ? bills.Count : 0)
                : 0;

            string targetSummary = monthCount > 0
                ? $"{monthCount} month(s) across {yearCount} year(s)"
                : "notes only";

            string message = includeNotes
                ? $"Importing will overwrite {targetSummary}{(targetsCurrentYear ? $". Current loaded year impact: {currentYearBillCount} bill(s)." : string.Empty)}\n\nNotes will also be replaced. A pre-import backup folder will be created first. Continue?"
                : $"Importing will overwrite {targetSummary}{(targetsCurrentYear ? $". Current loaded year impact: {currentYearBillCount} bill(s)." : string.Empty)}\n\nA pre-import backup folder will be created first. Continue?";

            if (MessageBox.Show(this, message, "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;

            try
            {
                PersistData(createBackup: false, showSuccessMessage: false);
                CreatePreImportBackup(selectedMonthsByYear, includeNotes);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not create the pre-import backup. Import has been canceled.\n\nError: {ex.Message}",
                    "Import Canceled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool ConfirmAndBackupBeforeImport(string monthKey)
        {
            return ConfirmAndBackupBeforeImport(
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [CurrentYear] = [monthKey]
                },
                includeNotes: false);
        }

        private void CreatePreImportBackup(Dictionary<string, List<string>> selectedMonthsByYear, bool includeNotes)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetYears = selectedMonthsByYear.Keys.OrderBy(year => year, StringComparer.OrdinalIgnoreCase).ToList();
            string scopeLabel = targetYears.Count == 1
                ? targetYears[0]
                : $"{targetYears.Count}_years";
            var backupFolderName = $"pre_import_backup_{scopeLabel}_{timestamp}";
            var backupFolderPath = Path.Combine(backupsRoot, "imports", backupFolderName);
            Directory.CreateDirectory(backupFolderPath);

            foreach (var year in targetYears)
            {
                var yearDbPath = GetDbPath(year);
                if (File.Exists(yearDbPath))
                {
                    File.Copy(yearDbPath, Path.Combine(backupFolderPath, Path.GetFileName(yearDbPath)), true);
                }
            }

            if (includeNotes && File.Exists(appDbPath))
            {
                File.Copy(appDbPath, Path.Combine(backupFolderPath, Path.GetFileName(appDbPath)), true);
            }

            var info = new StringBuilder();
            info.AppendLine("Crispy_Bills Pre-Import Backup");
            info.AppendLine($"Created: {DateTime.Now:f}");
            info.AppendLine($"Target Years: {string.Join(", ", targetYears)}");
            foreach (var entry in selectedMonthsByYear.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                info.AppendLine($"{entry.Key}: {string.Join(", ", entry.Value.OrderBy(month => Array.IndexOf(months, month)))}");
            }
            info.AppendLine($"Include Notes: {(includeNotes ? "Yes" : "No")}");
            info.AppendLine($"Reason: Backup created automatically before CSV import overwrote current data.");
            File.WriteAllText(Path.Combine(backupFolderPath, "backup-info.txt"), info.ToString());
        }

        private void StartRoutineBackups()
        {
            if (_routineBackupTimer != null) return;

            _routineBackupTimer = new DispatcherTimer
            {
                Interval = RoutineBackupInterval
            };
            _routineBackupTimer.Tick += (_, _) =>
            {
                try
                {
                    PersistData(createBackup: true, showSuccessMessage: false);
                }
                catch (Exception ex)
                {
                    LogNonFatal("StartRoutineBackups tick persist", ex);
                    SetStatus("Routine backup failed. Use File > Save to retry.");
                }
            };
            _routineBackupTimer.Start();
        }

        private void LoadGlobalNotes()
        {
            if (NotesBox == null) return;

            try
            {
                InitializeAppDatabase();

                var notesCsb = new SqliteConnectionStringBuilder { DataSource = appDbPath };
                using var conn = new SqliteConnection(notesCsb.ConnectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM AppMeta WHERE [Key] = $k";
                cmd.Parameters.AddWithValue("$k", "GlobalNotes");
                if (cmd.ExecuteScalar() is string dbNotes)
                {
                    NotesBox.Text = dbNotes;
                    return;
                }

                if (TryReadNotesFromDatabase(legacyAppDbPath, out var legacyDbNotes))
                {
                    SaveGlobalNotesToDatabase(legacyDbNotes);
                    if (NotesBox != null)
                        NotesBox.Text = legacyDbNotes;
                    return;
                }

                // One-time migration from legacy text file.
                if (File.Exists(legacyNotesPath))
                {
                    var migratedText = File.ReadAllText(legacyNotesPath);
                    SaveGlobalNotesToDatabase(migratedText);
                    if (NotesBox != null)
                        NotesBox.Text = migratedText;

                    try { File.Delete(legacyNotesPath); }
                    catch (Exception ex) { LogNonFatal("LegacyNotesPathDelete", ex); }
                    return;
                }

                if (NotesBox != null)
                    NotesBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                LogNonFatal("LoadGlobalNotes", ex);
                _startupIssues.Add(ex.Message);
            }
            UpdateNotesLineCount();
        }

        private void SaveGlobalNotes()
        {
            if (NotesBox == null) return;

            try
            {
                SaveGlobalNotesToDatabase(NotesBox?.Text ?? string.Empty);
            }
            catch (Exception ex)
            {
                LogNonFatal("SaveGlobalNotes", ex);
                // Non-fatal: notes save failure should not break app workflows.
            }
        }

        private void SaveGlobalNotesToDatabase(string notesText)
        {
            InitializeAppDatabase();

            var csb = new SqliteConnectionStringBuilder { DataSource = appDbPath };
            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AppMeta ([Key], [Value]) VALUES ($k, $v)
                ON CONFLICT([Key]) DO UPDATE SET [Value] = excluded.[Value];";
            cmd.Parameters.AddWithValue("$k", "GlobalNotes");
            cmd.Parameters.AddWithValue("$v", notesText ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        private static bool TryReadNotesFromDatabase(string dbPath, out string notes)
        {
            notes = string.Empty;
            try
            {
                if (!File.Exists(dbPath)) return false;

                var legacyCsb = new SqliteConnectionStringBuilder { DataSource = dbPath };
                using var conn = new SqliteConnection(legacyCsb.ConnectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM AppMeta WHERE [Key] = $k";
                cmd.Parameters.AddWithValue("$k", "GlobalNotes");
                if (cmd.ExecuteScalar() is not string dbNotes)
                    return false;

                notes = dbNotes;
                return true;
            }
            catch (Exception)
            {
                // Logging handled by caller
                return false;
            }
        }

        #endregion

        #region UI updates
        private void SetStatus(string message)
        {
            StatusText?.SetCurrentValue(TextBlock.TextProperty, message);
        }

        /// <summary>
        /// Updates summary fields, chart visuals, and filters for the currently selected month.
        /// This method is the central UI refresh point after most data mutations.
        /// </summary>
        private void UpdateDashboard()
        {
            var selected = MonthSelector?.SelectedItem;
            if (!IsLoaded || selected == null) return;
            string cur = (string)selected;

            decimal inc = MonthlyIncome.TryGetValue(cur, out var incVal) ? incVal : 0;
            var bills = AnnualData[cur];
            foreach (var b in bills)
                b.IsSoon = IsBillSoon(b, _soonThresholdValue, _soonThresholdUnit);
            decimal exp = bills.Sum(b => b.Amount);
            decimal unpaid = bills.Where(b => !b.IsPaid).Sum(b => b.Amount);
            decimal paid = exp - unpaid;

            IncomeBox.Text = inc.ToString("F2");
            ExpenseBar.Width = inc > 0
                ? Math.Min(160 * (double)(exp / inc), 160)
                : 0;

            SetStatus($"{CurrentYear} • {cur} • Bills: {bills.Count} • Remaining: {unpaid:C} • Net: {(inc - exp):C}");

            UpdateCategoryFilter();
            DrawPieChart(bills, inc - exp);
            ApplyFilters();

            BackButton.Visibility = !string.IsNullOrEmpty(_detailedCategory) ? Visibility.Visible : Visibility.Collapsed;

            var monthIdx = MonthSelector?.SelectedIndex ?? -1;
            if (monthIdx >= 0 && monthIdx < months.Length && AnnualData.TryGetValue(months[monthIdx], out var currentMonthBills) && currentMonthBills.Count > 100)
            {
                var key = $"{_currentYear}:{months[monthIdx]}:count:{currentMonthBills.Count}";
                if (!string.Equals(_lastBillCountWarningKey, key, StringComparison.Ordinal))
                {
                    _lastBillCountWarningKey = key;
                    MessageBox.Show(
                        $"This month has {currentMonthBills.Count} bills. If this is unexpected, check for duplicate recurring rules or accidental imports.",
                        "Large month",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }

        private void UpdateCategoryFilter()
        {
            var categories = AnnualData.Values.SelectMany(bills => bills.Select(b => b.Category))
                                              .Distinct()
                                              .OrderBy(c => c)
                                              .ToList();
            categories.Insert(0, "All Categories");

            CategoryFilter.ItemsSource = categories;

            if (CategoryFilter.SelectedItem is string currentSelection && categories.Contains(currentSelection))
                CategoryFilter.SelectedItem = currentSelection;
            else
                CategoryFilter.SelectedIndex = 0;
        }

        /// <summary>
        /// Draws either category-level or bill-level pie slices depending on drill-down state.
        /// </summary>
        private void DrawPieChart(IEnumerable<Bill> bills, decimal remaining)
        {
            PieChartCanvas.Children.Clear();
            PieLegendPanel?.Children.Clear();
            var totalBillsAmount = bills.Sum(b => b.Amount);

            var colors = new List<Brush>
            {
                new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xAB)),
                new SolidColorBrush(Color.FromRgb(0xF2, 0xA5, 0x41)),
                new SolidColorBrush(Color.FromRgb(0x7A, 0x9E, 0x7E)),
                new SolidColorBrush(Color.FromRgb(0xC1, 0x66, 0x6B)),
                new SolidColorBrush(Color.FromRgb(0x7A, 0x6F, 0x9B)),
                new SolidColorBrush(Color.FromRgb(0x4C, 0xA1, 0x8F)),
                new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xE1)),
                new SolidColorBrush(Color.FromRgb(0xB5, 0x8D, 0x62)),
                new SolidColorBrush(Color.FromRgb(0x9B, 0xA3, 0x4E))
            };

            if (string.IsNullOrEmpty(_detailedCategory))
            {
                var categories = bills.GroupBy(b => b.Category)
                                      .Select(g => new PieCategoryRow(g.Key, g.Sum(x => x.Amount)))
                                      .OrderByDescending(x => x.Total)
                                      .ToList();

                decimal totalPlusRemaining = totalBillsAmount + Math.Max(0, remaining);
                if (totalPlusRemaining <= 0) return;

                double currentAngle = 0;
                for (int i = 0; i < categories.Count; i++)
                {
                    var cat = categories[i];
                    double sweep = NormalizeSweepAngle((double)(cat.Total / totalPlusRemaining) * 360);
                    double percent = totalPlusRemaining > 0 ? (double)(cat.Total / totalPlusRemaining) * 100d : 0d;
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        colors[i % colors.Count], cat.Name, $"{cat.Name}\n{cat.Total:C} ({percent:0.#}%)"));

                    if (percent >= 5)
                    {
                        AddPieLabel(currentAngle, sweep, $"{cat.Name}\n{percent:0.#}%");
                    }

                    currentAngle += sweep;
                }

                AddLegendForCategoryView(categories, colors, totalPlusRemaining, remaining);

                if (remaining > 0)
                {
                    double sweep = NormalizeSweepAngle((double)(remaining / totalPlusRemaining) * 360);
                    double percent = totalPlusRemaining > 0 ? (double)(remaining / totalPlusRemaining) * 100d : 0d;
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        Brushes.LightGray, "Remaining", $"Remaining\n{remaining:C} ({percent:0.#}%)"));
                    if (percent >= 5)
                    {
                        AddPieLabel(currentAngle, sweep, $"Remaining\n{percent:0.#}%");
                    }
                }

                AddPieCenterText("Total Expenses", totalBillsAmount, remaining);
            }
            else
            {
                var billsInCategory = bills.Where(b => b.Category == _detailedCategory)
                                           .OrderByDescending(b => b.Amount)
                                           .ToList();
                decimal categoryTotal = billsInCategory.Sum(b => b.Amount);
                if (categoryTotal <= 0) return;

                double currentAngle = 0;
                for (int i = 0; i < billsInCategory.Count; i++)
                {
                    var bill = billsInCategory[i];
                    double sweep = NormalizeSweepAngle((double)(bill.Amount / categoryTotal) * 360);
                    double percent = categoryTotal > 0 ? (double)(bill.Amount / categoryTotal) * 100d : 0d;
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        colors[i % colors.Count], bill.Id.ToString(), $"{bill.Name}\n{bill.Amount:C} ({percent:0.#}%)"));

                    if (percent >= 6)
                    {
                        AddPieLabel(currentAngle, sweep, $"{bill.Name}\n{percent:0.#}%");
                    }

                    currentAngle += sweep;
                }

                AddLegendForDetailedView(billsInCategory, colors, categoryTotal);

                AddPieCenterText(_detailedCategory ?? "Category", categoryTotal, 0m);
            }
        }

        private void AddLegendForCategoryView(List<PieCategoryRow> categories, List<Brush> colors, decimal totalPlusRemaining, decimal remaining)
        {
            if (PieLegendPanel == null) return;

            var rows = categories.Select((c, i) => new
            {
                Name = (string)c.Name,
                Total = (decimal)c.Total,
                Color = colors[i % colors.Count]
            }).ToList();

            var top = rows.Take(5).ToList();
            foreach (var row in top)
            {
                double pct = totalPlusRemaining > 0 ? (double)(row.Total / totalPlusRemaining) * 100d : 0d;
                var categoryName = row.Name;
                AddLegendRow(
                    row.Color,
                    $"{row.Name}: {row.Total:C0} ({pct:0.#}%)",
                    () =>
                    {
                        _detailedCategory = categoryName;
                        UpdateDashboard();
                    },
                    $"Open {row.Name} details");
            }

            var otherTotal = rows.Skip(5).Sum(r => r.Total);
            if (remaining > 0) otherTotal += remaining;

            if (otherTotal > 0)
            {
                double pct = totalPlusRemaining > 0 ? (double)(otherTotal / totalPlusRemaining) * 100d : 0d;
                AddLegendRow(TryFindResource("HintForegroundBrush") as Brush ?? Brushes.Gray,
                    $"Other: {otherTotal:C0} ({pct:0.#}%)");
            }
        }

        private void AddLegendForDetailedView(List<Bill> billsInCategory, List<Brush> colors, decimal categoryTotal)
        {
            if (PieLegendPanel == null) return;

            var rows = billsInCategory.Select((b, i) => new
            {
                Bill = b,
                b.Name,
                Total = b.Amount,
                Color = colors[i % colors.Count]
            }).ToList();

            var top = rows.Take(5).ToList();
            foreach (var row in top)
            {
                double pct = categoryTotal > 0 ? (double)(row.Total / categoryTotal) * 100d : 0d;
                var billRef = row.Bill;
                AddLegendRow(
                    row.Color,
                    $"{row.Name}: {row.Total:C0} ({pct:0.#}%)",
                    () => SelectBillInGrid(billRef),
                    $"Select {row.Name} in bills grid");
            }

            var otherTotal = rows.Skip(5).Sum(r => r.Total);
            if (otherTotal > 0)
            {
                double pct = categoryTotal > 0 ? (double)(otherTotal / categoryTotal) * 100d : 0d;
                AddLegendRow(TryFindResource("HintForegroundBrush") as Brush ?? Brushes.Gray,
                    $"Other: {otherTotal:C0} ({pct:0.#}%)");
            }
        }

        private void AddLegendRow(Brush swatchBrush, string text, Action? onClick = null, string? tooltip = null)
        {
            if (PieLegendPanel == null) return;

            var rowContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            rowContent.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                Background = swatchBrush,
                BorderBrush = TryFindResource("PanelBorderBrush") as Brush ?? Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            rowContent.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = TryFindResource("WindowForegroundBrush") as Brush ?? Brushes.Black,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (onClick == null)
            {
                PieLegendPanel.Children.Add(rowContent);
                return;
            }

            var interactiveRow = new Border
            {
                Child = rowContent,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                ToolTip = tooltip ?? text
            };

            interactiveRow.MouseLeftButtonUp += (_, _) => onClick();
            interactiveRow.MouseEnter += (_, _) =>
            {
                interactiveRow.Background = (TryFindResource("ControlBackgroundBrush") as Brush) ?? Brushes.LightGray;
                interactiveRow.Opacity = 0.95;
            };
            interactiveRow.MouseLeave += (_, _) =>
            {
                interactiveRow.Background = Brushes.Transparent;
                interactiveRow.Opacity = 1.0;
            };

            PieLegendPanel.Children.Add(interactiveRow);
        }

        private void SelectBillInGrid(Bill bill)
        {
            if (BillsGrid == null) return;

            BillsGrid.SelectedItem = bill;
            BillsGrid.ScrollIntoView(bill);
            BillsGrid.Focus();
        }

        private void AddPieCenterText(string title, decimal primaryValue, decimal secondaryValue)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 106,
                Height = 106,
                Fill = TryFindResource("PanelBackgroundBrush") as Brush ?? Brushes.White,
                Stroke = TryFindResource("PanelBorderBrush") as Brush ?? Brushes.Gainsboro,
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(ring, 97);
            Canvas.SetTop(ring, 97);
            Panel.SetZIndex(ring, 10);
            PieChartCanvas.Children.Add(ring);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryFindResource("HintForegroundBrush") as Brush ?? Brushes.DimGray,
                TextAlignment = TextAlignment.Center,
                Width = 88,
                TextWrapping = TextWrapping.Wrap
            };
            Canvas.SetLeft(titleBlock, 106);
            Canvas.SetTop(titleBlock, 112);
            Panel.SetZIndex(titleBlock, 11);
            PieChartCanvas.Children.Add(titleBlock);

            var valueBlock = new TextBlock
            {
                Text = primaryValue.ToString("C0"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = TryFindResource("WindowForegroundBrush") as Brush ?? Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Width = 88
            };
            Canvas.SetLeft(valueBlock, 106);
            Canvas.SetTop(valueBlock, 132);
            Panel.SetZIndex(valueBlock, 11);
            PieChartCanvas.Children.Add(valueBlock);

            if (secondaryValue > 0)
            {
                var subBlock = new TextBlock
                {
                    Text = $"Left {secondaryValue:C0}",
                    FontSize = 10,
                    Foreground = TryFindResource("HintForegroundBrush") as Brush ?? Brushes.DimGray,
                    TextAlignment = TextAlignment.Center,
                    Width = 88
                };
                Canvas.SetLeft(subBlock, 106);
                Canvas.SetTop(subBlock, 154);
                Panel.SetZIndex(subBlock, 11);
                PieChartCanvas.Children.Add(subBlock);
            }
        }

        private static double NormalizeSweepAngle(double sweepAngle)
        {
            // ArcSegment cannot render a perfect 360-degree arc; clamp slightly below full circle.
            if (sweepAngle >= 360d) return 359.999d;
            if (sweepAngle <= -360d) return -359.999d;
            return sweepAngle;
        }

        private void AddPieLabel(double startAngle, double sweepAngle, string text)
        {
            const double center = 150;
            const double labelRadius = 126;

            double labelAngle = (startAngle + (sweepAngle / 2.0)) - 90;
            double rad = Math.PI / 180;
            double x = center + labelRadius * Math.Cos(labelAngle * rad);
            double y = center + labelRadius * Math.Sin(labelAngle * rad);

            double canvasWidth = PieChartCanvas.ActualWidth > 0 ? PieChartCanvas.ActualWidth : PieChartCanvas.Width;
            double canvasHeight = PieChartCanvas.ActualHeight > 0 ? PieChartCanvas.ActualHeight : PieChartCanvas.Height;
            double maxLabelWidth = Math.Max(90, canvasWidth * 0.42);

            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryFindResource("WindowForegroundBrush") as Brush ?? Brushes.Black,
                Background = TryFindResource("PanelBackgroundBrush") as Brush ?? Brushes.White,
                Padding = new Thickness(5, 2, 5, 2),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxLabelWidth
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = label.DesiredSize.Width;
            double height = label.DesiredSize.Height;

            // Anchor left/right of midpoint, then keep the entire label within the canvas.
            x = x < center ? x - width : x;
            y -= height / 2;

            const double padding = 4;
            x = Math.Max(padding, Math.Min(x, canvasWidth - width - padding));
            y = Math.Max(padding, Math.Min(y, canvasHeight - height - padding));

            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            Panel.SetZIndex(label, 5);
            PieChartCanvas.Children.Add(label);
        }

        private System.Windows.Shapes.Path CreateSlice(double startAngle, double sweepAngle, Brush fill, object tag, string tooltip)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(150, 150), IsClosed = true };
            double rad = Math.PI / 180;
            figure.Segments.Add(new LineSegment(
                new Point(150 + 140 * Math.Cos((startAngle - 90) * rad),
                         150 + 140 * Math.Sin((startAngle - 90) * rad)), true));
            figure.Segments.Add(new ArcSegment(
                new Point(150 + 140 * Math.Cos((startAngle + sweepAngle - 90) * rad),
                          150 + 140 * Math.Sin((startAngle + sweepAngle - 90) * rad)),
                new Size(140, 140), 0,
                sweepAngle > 180,
                SweepDirection.Clockwise,
                true));
            geometry.Figures.Add(figure);
            var slice = new System.Windows.Shapes.Path
            {
                Fill = fill,
                Data = geometry,
                Tag = tag,
                Cursor = Cursors.Hand,
                Stroke = TryFindResource("PanelBackgroundBrush") as Brush ?? Brushes.White,
                StrokeThickness = 1.2,
                ToolTip = tooltip,
                Opacity = 0.95
            };
            slice.MouseEnter += (_, _) =>
            {
                slice.Opacity = 1.0;
                slice.StrokeThickness = 2.0;
            };
            slice.MouseLeave += (_, _) =>
            {
                slice.Opacity = 0.95;
                slice.StrokeThickness = 1.2;
            };
            slice.MouseLeftButtonDown += PieSlice_Click;
            return slice;
        }
        #endregion

        #region Event handlers
        private void PieSlice_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Shapes.Path slice && slice.Tag is string tag)
            {
                if (string.IsNullOrEmpty(_detailedCategory) && tag != "Remaining")
                {
                    _detailedCategory = tag;
                    UpdateDashboard();
                }
            }
        }
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _detailedCategory = null;
            UpdateDashboard();
        }

        private void YearSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearSelector.SelectedItem is string selectedYear && selectedYear != _currentYear)
            {
                _currentYear = selectedYear;
                LoadData();
            }
        }
        private void NewYear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    this,
                    "Create a new year and carry over recurring bills/income settings?",
                    "Confirm New Year",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            if (int.TryParse(_currentYear, out int cur))
            {
                // New year carryover is sourced from December only.
                var decemberBills = AnnualData["December"].ToList();

                var recurringTemplates = decemberBills
                    .Where(t => t.IsRecurring)
                    .ToList();

                // Unpaid recurring December bills get an extra January carryover copy
                // that keeps the original due date (old year) so it remains visibly past due.
                var unpaidRecurringDecember = recurringTemplates
                    .Where(t => !t.IsPaid)
                    .ToList();

                // Unpaid non-recurring December bills go to January only.
                var unpaidDecemberOneTime = decemberBills
                    .Where(t => !t.IsRecurring && !t.IsPaid)
                    .ToList();

                decimal decemberIncome = MonthlyIncome.GetValueOrDefault("December", 0m);

                int newYearVal = cur + 1;
                string newYear = newYearVal.ToString();
                bool yearAlreadyExists = File.Exists(GetDbPath(newYear));

                _currentYear = newYear;
                LoadData();

                MonthSelector.SelectedIndex = 0;

                // Carry income to every month only when this year is newly created.
                if (!yearAlreadyExists)
                {
                    foreach (var m in months)
                    {
                        MonthlyIncome[m] = decemberIncome;
                    }
                    // Immediately persist the copied income to the new year's database to prevent loss.
                    AutoSave();
                    UpdateDashboard();
                }

                if ((recurringTemplates.Count > 0 || unpaidDecemberOneTime.Count > 0 || unpaidRecurringDecember.Count > 0) && AnnualData["January"].Count == 0)
                {
                    // Generate a shared ID per recurring template to maintain recurring linkage across the new year.
                    var recurringIds = recurringTemplates.Select(t => new { Template = t, NewId = Guid.NewGuid() }).ToList();

                    foreach (var item in recurringIds)
                    {
                        var t = item.Template;
                        if (IsWeekBasedWpf(t.RecurrenceFrequency))
                        {
                            var step = t.RecurrenceFrequency == RecurrenceFrequency.Weekly ? 7 : 14;
                            var first = FirstWeeklyOccurrenceOnOrAfterWpf(t.DueDate, new DateTime(newYearVal, 1, 1), step);
                            if (first.Year != newYearVal)
                            {
                                continue;
                            }

                            var anchor = new Bill
                            {
                                Id = item.NewId,
                                Name = t.Name,
                                Amount = t.Amount,
                                Category = t.Category,
                                DueDate = first,
                                IsPaid = false,
                                IsRecurring = true,
                                RecurrenceFrequency = t.RecurrenceFrequency,
                                RecurrenceEveryMonths = t.RecurrenceEveryMonths,
                                RecurrenceEndMode = t.RecurrenceEndMode,
                                RecurrenceEndDate = t.RecurrenceEndDate,
                                RecurrenceMaxOccurrences = t.RecurrenceMaxOccurrences
                            };
                            AssignBillContext(anchor, newYearVal, first.Month);
                            AnnualData[months[first.Month - 1]].Add(anchor);
                            var weeklyScratch = new List<(string month, Bill bill)>();
                            ExpandWeeklySeriesWpf(anchor, newYearVal, weeklyScratch);
                            continue;
                        }

                        for (int i = 0; i < months.Length; i++)
                        {
                            int targetMonth = i + 1;
                            if (!ShouldCreateMonthlyOccurrenceWpf(t, 1, targetMonth, newYearVal))
                            {
                                continue;
                            }

                            int daysInMonth = DateTime.DaysInMonth(newYearVal, targetMonth);
                            int day = Math.Min(t.DueDate.Day, daysInMonth);
                            var recurringBill = new Bill
                            {
                                Id = item.NewId,
                                Name = t.Name,
                                Amount = t.Amount,
                                Category = t.Category,
                                DueDate = new DateTime(newYearVal, targetMonth, day),
                                IsPaid = false,
                                IsRecurring = true,
                                RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
                                RecurrenceEveryMonths = t.RecurrenceEveryMonths,
                                RecurrenceEndMode = t.RecurrenceEndMode,
                                RecurrenceEndDate = t.RecurrenceEndDate,
                                RecurrenceMaxOccurrences = t.RecurrenceMaxOccurrences
                            };
                            AssignBillContext(recurringBill, newYearVal, targetMonth);
                            AnnualData[months[i]].Add(recurringBill);
                        }
                    }

                    int januaryDays = DateTime.DaysInMonth(newYearVal, 1);
                    foreach (var t in unpaidDecemberOneTime)
                    {
                        int day = Math.Min(t.DueDate.Day, januaryDays);
                        var januaryBill = new Bill
                        {
                            Id = Guid.NewGuid(),
                            Name = t.Name,
                            Amount = t.Amount,
                            Category = t.Category,
                            DueDate = new DateTime(newYearVal, 1, day),
                            IsPaid = false,
                            IsRecurring = false
                        };
                        AssignBillContext(januaryBill, newYearVal, 1);
                        AnnualData["January"].Add(januaryBill);
                    }

                    // Add one extra January carryover entry for unpaid recurring December bills
                    // while keeping the normal recurring January copy above.
                    foreach (var t in unpaidRecurringDecember)
                    {
                        var carryoverBill = new Bill
                        {
                            Id = Guid.NewGuid(),
                            Name = t.Name,
                            Amount = t.Amount,
                            Category = t.Category,
                            DueDate = t.DueDate,
                            IsPaid = false,
                            IsRecurring = false
                        };
                        AssignBillContext(carryoverBill, newYearVal, 1);
                        AnnualData["January"].Add(carryoverBill);
                    }

                    MessageBox.Show($"Welcome to {newYearVal}! December {cur} carryover applied: {recurringTemplates.Count} recurring template(s) to all months, {unpaidDecemberOneTime.Count} unpaid one-time bill(s) to January, and {unpaidRecurringDecember.Count} extra unpaid recurring carryover bill(s) to January.");
                }

                AutoSave();

                UpdateYearSelector();
            }
        }

        private void IncomeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { IncomeBox_LostFocus(sender, e); Keyboard.ClearFocus(); }
        }

        private void IncomeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(IncomeBox.Text,
                                  NumberStyles.Currency | NumberStyles.AllowDecimalPoint,
                                  CultureInfo.InvariantCulture,
                                  out decimal val) || MonthSelector.SelectedIndex < 0)
                return;

            if (val < 0) val = 0;

            int selectedIdx = MonthSelector.SelectedIndex;
            var changedMonths = new List<string>();
            var oldValues = new Dictionary<string, decimal>();

            for (int i = selectedIdx; i < months.Length; i++)
            {
                string monthKey = months[i];
                changedMonths.Add(monthKey);
                oldValues[monthKey] = MonthlyIncome[monthKey];
                MonthlyIncome[monthKey] = val;
            }

            UpdateDashboard();

            PushUndo(() =>
            {
                foreach (var m in changedMonths)
                {
                    MonthlyIncome[m] = oldValues[m];
                }
                UpdateDashboard();
            },
            () =>
            {
                foreach (var m in changedMonths)
                {
                    MonthlyIncome[m] = val;
                }
                UpdateDashboard();
            });

            AutoSave();
        }

        private void ThemeFollowSystem_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "system";
            ApplyThemeFromMode();
            SaveThemePreference();
        }

        private void ThemeLight_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "light";
            ApplyThemeFromMode();
            SaveThemePreference();
        }

        private void ThemeDark_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "dark";
            ApplyThemeFromMode();
            SaveThemePreference();
        }

        private void ApplyThemeFromMode()
        {
            bool useDark;
            if (_themeMode == "system")
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    var val = key?.GetValue("AppsUseLightTheme");
                    useDark = val is int i && i == 0;
                }
                catch
                {
                    useDark = false;
                }
            }
            else
            {
                useDark = _themeMode == "dark";
            }

            var themeFile = useDark ? "Dark.xaml" : "Light.xaml";
            var themeDict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };
            var mergedDicts = Application.Current.Resources.MergedDictionaries;

            for (int i = mergedDicts.Count - 1; i >= 0; i--)
            {
                var src = mergedDicts[i].Source?.OriginalString ?? string.Empty;
                if (src.Contains("Light.xaml") || src.Contains("Dark.xaml"))
                {
                    mergedDicts.RemoveAt(i);
                }
            }

            var fontSizesIndex = -1;
            for (int i = 0; i < mergedDicts.Count; i++)
            {
                if ((mergedDicts[i].Source?.OriginalString ?? string.Empty).Contains("FontSizes"))
                {
                    fontSizesIndex = i;
                    break;
                }
            }

            if (fontSizesIndex >= 0)
                mergedDicts.Insert(fontSizesIndex, themeDict);
            else
                mergedDicts.Add(themeDict);

            isDarkMode = useDark;
            UpdateThemeMenuChecks();
            UpdateDashboard();
            SetStatus(useDark ? "Switched to dark theme." : "Switched to light theme.");
        }

        private void UpdateThemeMenuChecks()
        {
            if (ThemeFollowSystemMenuItem != null) ThemeFollowSystemMenuItem.IsChecked = _themeMode == "system";
            if (ThemeLightMenuItem != null) ThemeLightMenuItem.IsChecked = _themeMode == "light";
            if (ThemeDarkMenuItem != null) ThemeDarkMenuItem.IsChecked = _themeMode == "dark";
        }

        private void SaveThemePreference()
        {
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={appDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO AppMeta (Key, Value) VALUES ('theme_mode', $value);";
                cmd.Parameters.AddWithValue("$value", _themeMode);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private void LoadThemePreference()
        {
            try
            {
                if (!System.IO.File.Exists(appDbPath)) return;
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={appDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM AppMeta WHERE Key = 'theme_mode';";
                var val = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(val))
                    _themeMode = val;
            }
            catch { }
        }

        private string LoadAppMeta(string key, string defaultValue)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={appDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM AppMeta WHERE Key = $key;";
                cmd.Parameters.AddWithValue("$key", key);
                return cmd.ExecuteScalar() as string ?? defaultValue;
            }
            catch { return defaultValue; }
        }

        private int LoadAppMeta(string key, int defaultValue)
        {
            var str = LoadAppMeta(key, defaultValue.ToString());
            return int.TryParse(str, out var val) ? val : defaultValue;
        }

        private void SaveAppMeta(string key, string value)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={appDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO AppMeta (Key, Value) VALUES ($key, $value);";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private static bool IsBillSoon(Bill bill, int thresholdValue, string thresholdUnit)
        {
            if (bill.IsPaid || bill.IsPastDue) return false;
            var today = DateTime.Today;
            var due = bill.DueDate.Date;
            if (due < today) return false;
            var thresholdDate = thresholdUnit switch
            {
                "Weeks" => today.AddDays(thresholdValue * 7),
                "Months" => today.AddMonths(thresholdValue),
                _ => today.AddDays(thresholdValue)
            };
            return due <= thresholdDate;
        }

        private void LoadArchivedYears()
        {
            var packed = LoadAppMeta("archived_years", "");
            _archivedYears = packed.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var y) ? y : -1)
                .Where(y => y > 0)
                .ToHashSet();
        }

        private void SaveArchivedYears()
        {
            var packed = string.Join(",", _archivedYears.OrderBy(y => y));
            SaveAppMeta("archived_years", packed);
        }

        /// <summary>
        /// Rolls unpaid bills from the selected month into the next month and annotates names
        /// to show rollover direction. Supports undo/redo for all generated changes.
        /// </summary>
        private void Rollover_Click(object sender, RoutedEventArgs e)
        {
            int curIdx = MonthSelector.SelectedIndex;
            if (curIdx < 0)
            {
                MessageBox.Show("Please select a month first.", "No Month Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (curIdx >= 11) { MessageBox.Show("Cannot rollover from December to the next year yet."); return; }

            string curMonth = months[curIdx];
            string nextMonth = months[curIdx + 1];
            var unpaidBills = AnnualData[curMonth].Where(x => !x.IsPaid).ToList();
            int count = 0;
            var copies = new List<Bill>();
            var renamedOriginals = new List<(Bill bill, string oldName, string newName)>();

            foreach (var b in unpaidBills)
            {
                string fromSuffix = $" - Rolled over from {curMonth}";
                string suffix = $" - Rolled over to {nextMonth}";
                string oldName = b.Name;
                string newName = oldName.EndsWith(suffix, StringComparison.Ordinal) ? oldName : oldName + suffix;
                string copyName = oldName.EndsWith(fromSuffix, StringComparison.Ordinal) ? oldName : oldName + fromSuffix;

                var copy = new Bill
                {
                    Id = Guid.NewGuid(),
                    Name = copyName,
                    Amount = b.Amount,
                    Category = b.Category,
                    DueDate = b.DueDate,
                    IsPaid = false,
                    IsRecurring = false
                };
                AssignBillContext(copy, nextMonth);
                AnnualData[nextMonth].Add(copy);
                copies.Add(copy);

                if (!string.Equals(oldName, newName, StringComparison.Ordinal))
                {
                    b.Name = newName;
                    renamedOriginals.Add((b, oldName, newName));
                }

                count++;
            }

            MessageBox.Show($"Rolled over {count} unpaid bills to {nextMonth}.");

            PushUndo(() =>
            {
                foreach (var c in copies) AnnualData[nextMonth].Remove(c);
                foreach (var (bill, oldName, _) in renamedOriginals) bill.Name = oldName;
                UpdateDashboard();
            },
            () =>
            {
                foreach (var c in copies) AnnualData[nextMonth].Add(c);
                foreach (var (bill, _, newName) in renamedOriginals) bill.Name = newName;
                UpdateDashboard();
            });

            UpdateDashboard();
            AutoSave();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            PersistData(createBackup: true, showSuccessMessage: sender is Button);
        }

        /// <summary>
        /// Persists all in-memory data for the active year, optionally creating backups and user feedback.
        /// </summary>
        private void PersistData(bool createBackup, bool showSuccessMessage)
        {
            var snapshot = CreatePersistenceSnapshot();
            var (yearBackupsCreated, notesBackupCreated) = PersistSnapshotToDisk(snapshot, createBackup);

            if (showSuccessMessage)
            {
                MessageBox.Show(createBackup ? "Data saved and backed up!" : "Data saved!");
            }

            if (createBackup)
            {
                SetStatus($"Saved {snapshot.Year} at {DateTime.Now:hh:mm:ss tt}. Backups: {yearBackupsCreated} year DB(s){(notesBackupCreated ? " + Notes DB" : string.Empty)}.");
            }
            else
            {
                SetStatus($"Auto-saved {snapshot.Year} at {DateTime.Now:hh:mm:ss tt}");
            }
        }

        private PersistenceSnapshot CreatePersistenceSnapshot()
        {
            var billsByMonth = months.ToDictionary(
                month => month,
                month => AnnualData[month].Select(CloneBillForExport).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var incomeByMonth = months.ToDictionary(
                month => month,
                month => MonthlyIncome.GetValueOrDefault(month, 0m),
                StringComparer.OrdinalIgnoreCase);

            string notesText = NotesBox?.Text ?? string.Empty;
            return new PersistenceSnapshot(CurrentYear, billsByMonth, incomeByMonth, notesText);
        }

        private (int YearBackupsCreated, bool NotesBackupCreated) PersistSnapshotToDisk(PersistenceSnapshot snapshot, bool createBackup)
        {
            var dbFile = GetDbPath(snapshot.Year);
            var dbDir = Path.GetDirectoryName(dbFile);
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            PersistYearDataToDatabase(dbFile, snapshot.Year, snapshot.BillsByMonth, snapshot.IncomeByMonth);
            SaveGlobalNotesToDatabase(snapshot.NotesText);

            if (!createBackup)
                return (0, false);

            int yearBackupsCreated = BackupAllYearDatabases();
            bool notesBackupCreated = BackupAppDatabase();
            return (yearBackupsCreated, notesBackupCreated);
        }

        /// <summary>
        /// Performs a transactional rewrite of Bills and Income tables for the active year.
        /// </summary>
        private void PersistYearDataToDatabase(string dbFile)
        {
            var billsByMonth = months.ToDictionary(
                month => month,
                month => AnnualData[month].Select(CloneBillForExport).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var incomeByMonth = months.ToDictionary(
                month => month,
                month => MonthlyIncome.GetValueOrDefault(month, 0m),
                StringComparer.OrdinalIgnoreCase);

            PersistYearDataToDatabase(dbFile, CurrentYear, billsByMonth, incomeByMonth);
        }

        private void PersistYearDataToDatabase(string dbFile, string year, Dictionary<string, List<Bill>> billsByMonth, Dictionary<string, decimal> incomeByMonth)
        {
            // Scope the connection to ensure it closes (and checkpoints WAL) before backup
            var persistCsb = new SqliteConnectionStringBuilder { DataSource = dbFile };
            using var conn = new SqliteConnection(persistCsb.ConnectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                using var delB = conn.CreateCommand();
                delB.Transaction = tx;
                delB.CommandText = "DELETE FROM Bills WHERE Year = $y";
                delB.Parameters.AddWithValue("$y", year);
                delB.ExecuteNonQuery();

                using var delI = conn.CreateCommand();
                delI.Transaction = tx;
                delI.CommandText = "DELETE FROM Income WHERE Year = $y";
                delI.Parameters.AddWithValue("$y", year);
                delI.ExecuteNonQuery();

                foreach (var m in months)
                {
                    InsertMonthBills(conn, tx, m, year, billsByMonth);
                    InsertMonthIncome(conn, tx, m, year, incomeByMonth);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                try
                {
                    tx.Rollback();
                }
                catch
                {
                    // Rollback errors are non-fatal to handling the original exception.
                }

                throw new InvalidOperationException($"Failed to persist year {year} data to the database.", ex);
            }
        }

        /// <summary>
        /// Writes all bills for a single month to the current transaction.
        /// </summary>
        private static void InsertMonthBills(SqliteConnection conn, SqliteTransaction tx, string month, string year, Dictionary<string, List<Bill>> billsByMonth)
        {
            if (!billsByMonth.TryGetValue(month, out var billsForMonth) || billsForMonth == null)
                return;

            foreach (var b in billsForMonth)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
INSERT INTO Bills (Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
    RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences,
    RecurrenceFrequency, RecurrenceGroupId)
VALUES ($id, $m, $y, $n, $a, $d, $p, $c, $r,
    $rem, $rendMode, $rendDate, $rmax,
    $rfreq, $rgroup);";
                ins.Parameters.AddWithValue("$id", b.Id.ToString());
                ins.Parameters.AddWithValue("$m", MonthNumberFromName(month));
                ins.Parameters.AddWithValue("$y", int.TryParse(year, out var y) ? y : 0);
                ins.Parameters.AddWithValue("$n", b.Name);
                ins.Parameters.AddWithValue("$a", b.Amount);
                ins.Parameters.AddWithValue("$d", b.DueDate.ToString("yyyy-MM-dd"));
                ins.Parameters.AddWithValue("$p", b.IsPaid ? 1 : 0);
                ins.Parameters.AddWithValue("$c", b.Category);
                ins.Parameters.AddWithValue("$r", b.IsRecurring ? 1 : 0);
                ins.Parameters.AddWithValue("$rem", Math.Max(1, b.RecurrenceEveryMonths));
                ins.Parameters.AddWithValue("$rendMode", b.RecurrenceEndMode.ToString());
                ins.Parameters.AddWithValue("$rendDate", b.RecurrenceEndDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
                ins.Parameters.AddWithValue("$rmax", b.RecurrenceMaxOccurrences ?? (object)DBNull.Value);
                ins.Parameters.AddWithValue("$rfreq", (b.IsRecurring ? b.RecurrenceFrequency : RecurrenceFrequency.None).ToString());
                ins.Parameters.AddWithValue("$rgroup", b.RecurrenceGroupId?.ToString() ?? (object)DBNull.Value);
                ins.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Writes income for a single month to the current transaction.
        /// </summary>
        private void InsertMonthIncome(SqliteConnection conn, SqliteTransaction tx, string month, string year, Dictionary<string, decimal> incomeByMonth)
        {
            ArgumentNullException.ThrowIfNull(conn);
            ArgumentNullException.ThrowIfNull(tx);

            decimal incomeValue = 0m;
            if (incomeByMonth.TryGetValue(month, out var storedAmount))
            {
                incomeValue = storedAmount;
            }

            using var insI = conn.CreateCommand();
            insI.Transaction = tx;
            insI.CommandText = "INSERT INTO Income VALUES ($m, $y, $a)";
            insI.Parameters.AddWithValue("$m", MonthNumberFromName(month));
            insI.Parameters.AddWithValue("$y", int.TryParse(year, out var y) ? y : 0);
            insI.Parameters.AddWithValue("$a", incomeValue);
            insI.ExecuteNonQuery();
        }

        private void AutoSave()
        {
            try
            {
                PersistData(createBackup: false, showSuccessMessage: false);
            }
            catch (Exception ex)
            {
                LogNonFatal("AutoSave", ex);
                SetStatus("Auto-save failed. Use File > Save to retry.");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void UpdateNotesLineCount()
        {
            var text = (NotesBox.Text ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
            var lines = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
            NotesLineCount.Text = $"{Math.Min(lines, MaxNoteLines)} / {MaxNoteLines} lines";
        }

        private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingNotesText) return;
            if (NotesBox is not TextBox notesBox) return;

            var text = notesBox.Text ?? string.Empty;

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');

            if (lines.Length > MaxNoteLines)
            {
                _isUpdatingNotesText = true;
                try
                {
                    notesBox.Text = string.Join(Environment.NewLine, lines.Take(MaxNoteLines));
                    notesBox.CaretIndex = notesBox.Text?.Length ?? 0;
                }
                finally
                {
                    _isUpdatingNotesText = false;
                }

                SetStatus(NotesLineLimitMessage);
            }

            SaveGlobalNotes();
            UpdateNotesLineCount();
        }

        private void NotesBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveGlobalNotes();
        }

        /// <summary>
        /// Creates a pre-edit snapshot when inline DataGrid editing begins.
        /// </summary>
        private void BillsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (MonthSelector.SelectedIndex < 0) return;
            if (e.Row.Item is not Bill b) return;

            _gridEditVersion++;
            _gridCommitPending = false;

            _pendingGridEdit = new GridEditSnapshot(
                b,
                months[MonthSelector.SelectedIndex],
                b.Name,
                b.Amount,
                b.DueDate,
                b.Category);
        }

        /// <summary>
        /// Applies inline grid edits with scoped propagation rules:
        /// Name/Category for current month, Amount/Due Date for current+future recurring copies.
        /// </summary>
        private void ApplyGridEditWithConfirmation(GridEditSnapshot snapshot)
        {
            var bill = snapshot.Bill;

            if (int.TryParse(CurrentYear, out int yearValue))
            {
                int monthIndex = Array.IndexOf(months, snapshot.Month);
                if (monthIndex >= 0)
                {
                    bill.DueDate = NormalizeDueDateForMonthContext(bill, yearValue, monthIndex + 1);
                }
            }

            bool nameChanged = !string.Equals(bill.Name, snapshot.Name, StringComparison.Ordinal);
            bool amountChanged = bill.Amount != snapshot.Amount;
            bool dueDateChanged = bill.DueDate.Date != snapshot.DueDate.Date;
            bool categoryChanged = !string.Equals(bill.Category, snapshot.Category, StringComparison.Ordinal);

            if (!nameChanged && !amountChanged && !dueDateChanged && !categoryChanged)
                return;

            var editedState = new Bill
            {
                Id = bill.Id,
                Name = bill.Name,
                Amount = bill.Amount,
                DueDate = bill.DueDate,
                Category = bill.Category,
                IsPaid = bill.IsPaid
            };

            var futureSnapshots = new List<(Bill bill, decimal amount, DateTime dueDate)>();
            if ((amountChanged || dueDateChanged) && MonthSelector.SelectedIndex >= 0)
            {
                int selectedIdx = MonthSelector.SelectedIndex;
                for (int i = selectedIdx + 1; i < months.Length; i++)
                {
                    foreach (var futureBill in AnnualData[months[i]].Where(x => x.Id == bill.Id))
                    {
                        futureSnapshots.Add((futureBill, futureBill.Amount, futureBill.DueDate));

                        if (amountChanged)
                            futureBill.Amount = bill.Amount;

                        if (dueDateChanged)
                        {
                            int daysInMonth = DateTime.DaysInMonth(futureBill.DueDate.Year, futureBill.DueDate.Month);
                            int day = Math.Min(bill.DueDate.Day, daysInMonth);
                            futureBill.DueDate = new DateTime(futureBill.DueDate.Year, futureBill.DueDate.Month, day);
                        }
                    }
                }
            }

            PushUndo(() =>
            {
                bill.Name = snapshot.Name;
                bill.Amount = snapshot.Amount;
                bill.DueDate = snapshot.DueDate;
                bill.Category = snapshot.Category;

                foreach (var (futureBill, oldAmount, oldDueDate) in futureSnapshots)
                {
                    futureBill.Amount = oldAmount;
                    futureBill.DueDate = oldDueDate;
                }

                UpdateDashboard();
            },
            () =>
            {
                bill.Name = editedState.Name;
                bill.Amount = editedState.Amount;
                bill.DueDate = editedState.DueDate;
                bill.Category = editedState.Category;

                if (amountChanged || dueDateChanged)
                {
                    foreach (var (futureBill, _, _) in futureSnapshots)
                    {
                        if (amountChanged)
                            futureBill.Amount = editedState.Amount;

                        if (dueDateChanged)
                        {
                            int daysInMonth = DateTime.DaysInMonth(futureBill.DueDate.Year, futureBill.DueDate.Month);
                            int day = Math.Min(editedState.DueDate.Day, daysInMonth);
                            futureBill.DueDate = new DateTime(futureBill.DueDate.Year, futureBill.DueDate.Month, day);
                        }
                    }
                }

                UpdateDashboard();
            });

            UpdateDashboard();
            AutoSave();
        }

        private void StatusCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not Bill b)
                return;

            try
            {
                bool oldVal = b.IsPaid;
                b.IsPaid = !b.IsPaid;
                System.Windows.Data.CollectionViewSource.GetDefaultView(BillsGrid.ItemsSource)?.Refresh();
                UpdateDashboard();

                PushUndo(() => { b.IsPaid = oldVal; UpdateDashboard(); },
                         () => { b.IsPaid = !oldVal; UpdateDashboard(); });

                AutoSave();
                e.Handled = true;
            }
            catch
            {
                SetStatus("Unable to update bill status.");
            }
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            if (CategoryFilter.SelectedIndex != 0)
            {
                CategoryFilter.SelectedIndex = 0;
            }
            SearchBox.Focus();
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (BillsGrid == null) return;

            if (e.OriginalSource is not DependencyObject source)
                return;

            var clickedCell = FindVisualParent<DataGridCell>(source);
            bool clickedCurrentCell = clickedCell != null
                && Equals(clickedCell.DataContext, BillsGrid.CurrentItem)
                && Equals(clickedCell.Column, BillsGrid.CurrentColumn);

            // Two-click behavior for Category: first click selects cell, second click opens dropdown.
            if (_pendingGridEdit == null && clickedCurrentCell && clickedCell?.Column is DataGridComboBoxColumn categoryColumn
                && string.Equals(categoryColumn.SortMemberPath, nameof(Bill.Category), StringComparison.Ordinal))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (BillsGrid.CurrentItem == null || BillsGrid.CurrentCell.Column == null) return;

                    BillsGrid.BeginEdit();

                    var activeCell = FindVisualParent<DataGridCell>(Keyboard.FocusedElement as DependencyObject) ?? clickedCell;
                    if (activeCell == null) return;

                    var combo = FindVisualChild<ComboBox>(activeCell);
                    if (combo == null) return;

                    combo.Focus();
                    if (!combo.IsDropDownOpen)
                    {
                        combo.IsDropDownOpen = true;
                    }
                }), DispatcherPriority.Input);

                e.Handled = true;
                return;
            }

            if (_pendingGridEdit == null) return;
            if (_gridCommitPending) return;

            // Allow combo dropdown interactions to proceed without canceling the active edit.
            if (FindVisualParent<ComboBox>(source) != null || FindVisualParent<ComboBoxItem>(source) != null)
                return;

            // Clicking away should discard in-progress edits; only Enter/Tab applies changes.
            if (clickedCurrentCell) return;

            DiscardCurrentGridEdit();

        }

        private void Window_Deactivated(object? sender, EventArgs e) { }

        private void DueDateTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox editor) return;

            if (e.Key == Key.Tab)
            {
                DiscardCurrentGridEdit();
                MoveGridToAdjacentEditableCell(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;

            if (!TryApplyDueDateEditorValue(editor))
            {
                e.Handled = true;
                return;
            }

            CommitGridEditTransaction();
            if (_enterMovesDown)
            {
                MoveGridToSameColumnRowOffset(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            else
            {
                Keyboard.ClearFocus();
            }
            e.Handled = true;
        }

        private void DueDateTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox editor) return;

            if (!e.Text.All(char.IsDigit))
            {
                e.Handled = true;
                return;
            }

            int caret = editor.CaretIndex;
            string oldText = editor.Text ?? string.Empty;

            bool isAppending = caret == oldText.Length;
            bool isFullReplace = string.IsNullOrEmpty(oldText);

            if (isAppending || isFullReplace) {
                string proposed = BuildFormattedDueDateInput(editor, e.Text);
                if (string.IsNullOrEmpty(proposed)) {
                    e.Handled = true;
                    return;
                }
                if (proposed != oldText) {
                    int newCaret = caret + 1;
                    if (proposed.Length > oldText.Length && proposed.Length > newCaret && proposed[newCaret - 1] == '/')
                        newCaret++;
                    editor.Text = proposed;
                    editor.CaretIndex = Math.Min(newCaret, editor.Text.Length);
                }
                ClearEditorInvalid(editor);
                e.Handled = true;
            } else {
                e.Handled = false;
            }
        }

        private void DueDateTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is not TextBox editor) return;

            var pasted = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
                ?? e.SourceDataObject.GetData(DataFormats.Text) as string
                ?? string.Empty;

            if (!TryNormalizeDueDateInput(pasted, out var normalized, out _))
            {
                MarkEditorInvalid(editor, "Invalid pasted date. Use MM/DD/YYYY.");
                e.CancelCommand();
                return;
            }

            editor.Text = normalized;
            editor.CaretIndex = editor.Text.Length;
            ClearEditorInvalid(editor);
            e.CancelCommand();
        }

        private bool TryApplyDueDateEditorValue(TextBox editor)
        {
            if (editor.DataContext is not Bill bill) return true;

            if (!TryNormalizeDueDateInput(editor.Text, out var normalized, out var parsedDate))
            {
                editor.Text = bill.DueDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                editor.SelectAll();
                MarkEditorInvalid(editor, "Invalid date. Use MM/DD/YYYY.");
                return false;
            }

            bill.DueDate = parsedDate;
            editor.Text = normalized;
            ClearEditorInvalid(editor);
            return true;
        }

        private static string BuildFormattedDueDateInput(TextBox editor, string nextDigits)
        {
            var existingDigits = new string([.. (editor.Text ?? string.Empty).Where(char.IsDigit)]);
            if (existingDigits.Length >= 8) return string.Empty;

            var mergedDigits = existingDigits + new string([.. nextDigits.Where(char.IsDigit)]);
            if (mergedDigits.Length > 8) return string.Empty;

            if (mergedDigits.Length <= 2) return mergedDigits;
            if (mergedDigits.Length <= 4) return mergedDigits.Insert(2, "/");
            return mergedDigits.Insert(2, "/").Insert(5, "/");
        }

        private static bool TryNormalizeDueDateInput(string? raw, out string normalized, out DateTime parsedDate)
        {
            normalized = string.Empty;
            parsedDate = default;

            var text = raw?.Trim() ?? string.Empty;
            var match = DueDatePattern.Match(text);
            if (!match.Success) return false;

            if (!int.TryParse(match.Groups[1].Value, out int month)) return false;
            if (!int.TryParse(match.Groups[2].Value, out int day)) return false;
            if (!int.TryParse(match.Groups[3].Value, out int year)) return false;

            if (match.Groups[3].Value.Length == 2)
                year += 2000;

            if (!DateTime.TryParseExact($"{month}/{day}/{year}", "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                return false;

            normalized = parsedDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            return true;
        }

        private void GridTextEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox editor) return;

            if (e.Key == Key.Tab)
            {
                DiscardCurrentGridEdit();
                MoveGridToAdjacentEditableCell(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;

            if (!TryApplyTextEditorValue(editor))
            {
                e.Handled = true;
                return;
            }

            CommitGridEditTransaction();
            if (_enterMovesDown)
            {
                MoveGridToSameColumnRowOffset(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            else
            {
                Keyboard.ClearFocus();
            }
            e.Handled = true;
        }

        private void GridComboEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not ComboBox combo) return;

            if (e.Key == Key.Escape)
            {
                if (combo.IsDropDownOpen)
                {
                    combo.IsDropDownOpen = false;
                }

                DiscardCurrentGridEdit();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab)
            {
                if (!TryApplyComboEditorValue(combo))
                {
                    e.Handled = true;
                    return;
                }

                CommitGridEditTransaction();
                MoveGridToAdjacentEditableCell(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;

            if (!TryApplyComboEditorValue(combo))
            {
                e.Handled = true;
                return;
            }

            CommitGridEditTransaction();
            if (_enterMovesDown)
            {
                MoveGridToSameColumnRowOffset(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            else
            {
                Keyboard.ClearFocus();
            }
            e.Handled = true;
        }

        private void GridComboEdit_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Intentionally no auto-open on focus.
            // Forcing IsDropDownOpen during focus transitions can re-open the popup
            // after selection/commit and trap the user in a reopen loop.
        }

        private void GridComboEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            if (_pendingGridEdit == null) return;
            if (_gridCommitPending) return;

            if (e.NewFocus is DependencyObject newFocus)
            {
                // Ignore focus moves that remain inside the combo/dropdown while selecting.
                if (FindVisualParent<ComboBoxItem>(newFocus) != null || IsVisualDescendantOf(newFocus, combo))
                    return;
            }

            if (combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = false;
            }
        }

        private void GridComboEdit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            if (_pendingGridEdit == null) return;

            if (!TryApplyComboEditorValue(combo))
                return;

            if (combo.IsDropDownOpen)
            {
                // A real selection is in-flight; prevent outside-click discard until
                // DropDownClosed commits this edit transaction.
                _gridCommitPending = true;
                combo.DropDownClosed -= GridComboEdit_DropDownClosed;
                combo.DropDownClosed += GridComboEdit_DropDownClosed;
                return;
            }

            // Avoid committing from editor initialization churn; real commits happen on
            // Enter/Tab or when a user selection closes the dropdown.
        }

        private void GridComboEdit_DropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not ComboBox combo) return;

            combo.DropDownClosed -= GridComboEdit_DropDownClosed;

            if (_pendingGridEdit == null) return;
            if (!TryApplyComboEditorValue(combo)) return;

            CommitGridEditTransaction();
        }

        private void ClearBillSelection()
        {
            if (BillsGrid == null || BillsGrid.SelectedItem == null) return;

            BillsGrid.UnselectAll();
            BillsGrid.SelectedItem = null;
            System.Windows.Data.CollectionViewSource.GetDefaultView(BillsGrid.ItemsSource)?.Refresh();
            UpdateDashboard();
        }

        private bool TryApplyTextEditorValue(TextBox editor)
        {
            var binding = editor.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();

            if (!Validation.GetHasError(editor))
            {
                ClearEditorInvalid(editor);
                return true;
            }

            editor.SelectAll();
            MarkEditorInvalid(editor, "Invalid value.");
            return false;
        }

        private bool TryApplyComboEditorValue(ComboBox combo)
        {
            if (combo.DataContext is not Bill bill) return true;

            var selected = combo.SelectedItem?.ToString() ?? combo.Text;
            if (string.IsNullOrWhiteSpace(selected))
            {
                var fallback = _pendingGridEdit?.Bill == bill ? _pendingGridEdit.Category : bill.Category;
                bill.Category = fallback;
                combo.SelectedItem = fallback;
                combo.Text = fallback;
                MarkEditorInvalid(combo, "Category cannot be blank. Reverted to previous value.");
                return true;
            }

            bill.Category = selected.Trim();
            ClearEditorInvalid(combo);
            return true;
        }

        private void MarkEditorInvalid(Control editor, string message)
        {
            var brush = TryFindResource("InvalidBorderBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x44));

            editor.BorderBrush = brush;
            editor.BorderThickness = new Thickness(2);
            SetStatus(message);
        }

        private static void ClearEditorInvalid(Control editor)
        {
            editor.ClearValue(Control.BorderBrushProperty);
            editor.ClearValue(Control.BorderThicknessProperty);
        }

        private void CommitGridEditTransaction()
        {
            if (BillsGrid == null) return;
            BillsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            BillsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            if (_pendingGridEdit != null)
            {
                _gridCommitPending = true;
                int pendingVersion = _gridEditVersion;
                Dispatcher.BeginInvoke(new Action(() => FinalizePendingGridEdit(pendingVersion)), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void DiscardCurrentGridEdit()
        {
            if (BillsGrid == null) return;

            if (_pendingGridEdit != null)
            {
                var snapshot = _pendingGridEdit;
                snapshot.Bill.Name = snapshot.Name;
                snapshot.Bill.Amount = snapshot.Amount;
                snapshot.Bill.DueDate = snapshot.DueDate;
                snapshot.Bill.Category = snapshot.Category;
            }

            BillsGrid.CancelEdit(DataGridEditingUnit.Cell);
            BillsGrid.CancelEdit(DataGridEditingUnit.Row);
            _pendingGridEdit = null;
            _gridCommitPending = false;
            _gridEditVersion++;
            UpdateDashboard();
        }

        private void MoveGridToAdjacentEditableCell(bool forward)
        {
            if (BillsGrid == null || !BillsGrid.CurrentCell.IsValid) return;

            var editableColumns = BillsGrid.Columns
                .Where(c => !c.IsReadOnly)
                .OrderBy(c => c.DisplayIndex)
                .ToList();
            if (editableColumns.Count == 0) return;

            int currentRowIndex = BillsGrid.Items.IndexOf(BillsGrid.CurrentItem);
            if (currentRowIndex < 0) return;

            int currentColIndex = editableColumns.FindIndex(c => c == BillsGrid.CurrentCell.Column);
            if (currentColIndex < 0) currentColIndex = 0;

            int nextRowIndex = currentRowIndex;
            int nextColIndex = currentColIndex + (forward ? 1 : -1);

            if (forward)
            {
                if (nextColIndex >= editableColumns.Count)
                {
                    nextColIndex = 0;
                    nextRowIndex = Math.Min(currentRowIndex + 1, BillsGrid.Items.Count - 1);
                }
            }
            else
            {
                if (nextColIndex < 0)
                {
                    nextColIndex = editableColumns.Count - 1;
                    nextRowIndex = Math.Max(currentRowIndex - 1, 0);
                }
            }

            if (nextRowIndex == currentRowIndex && nextColIndex == currentColIndex)
                return;

            var nextItem = BillsGrid.Items[nextRowIndex];
            var nextColumn = editableColumns[nextColIndex];

            Dispatcher.BeginInvoke(new Action(() =>
            {
                BillsGrid.SelectedItem = nextItem;
                BillsGrid.CurrentCell = new DataGridCellInfo(nextItem, nextColumn);
                BillsGrid.ScrollIntoView(nextItem, nextColumn);
                BillsGrid.Focus();
                BillsGrid.BeginEdit();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void MoveGridToSameColumnRowOffset(int offset)
        {
            if (BillsGrid == null || !BillsGrid.CurrentCell.IsValid) return;

            int currentRowIndex = BillsGrid.Items.IndexOf(BillsGrid.CurrentItem);
            if (currentRowIndex < 0) return;

            int nextRowIndex = Math.Max(0, Math.Min(BillsGrid.Items.Count - 1, currentRowIndex + offset));
            if (nextRowIndex == currentRowIndex) return;

            var currentColumn = BillsGrid.CurrentCell.Column;
            if (currentColumn == null || currentColumn.IsReadOnly) return;

            var nextItem = BillsGrid.Items[nextRowIndex];

            Dispatcher.BeginInvoke(new Action(() =>
            {
                BillsGrid.SelectedItem = nextItem;
                BillsGrid.CurrentCell = new DataGridCellInfo(nextItem, currentColumn);
                BillsGrid.ScrollIntoView(nextItem, currentColumn);
                BillsGrid.Focus();
                BillsGrid.BeginEdit();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void FinalizePendingGridEdit(int pendingVersion)
        {
            _gridCommitPending = false;
            if (pendingVersion != _gridEditVersion) return;
            if (_pendingGridEdit == null) return;

            var snapshot = _pendingGridEdit;
            _pendingGridEdit = null;
            ApplyGridEditWithConfirmation(snapshot);
        }

        private static bool IsVisualDescendantOf(DependencyObject child, DependencyObject ancestor)
        {
            var current = child;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor)) return true;
                current = GetParentObject(current);
            }

            return false;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typed) return typed;
                child = GetParentObject(child);
            }
            return null;
        }

        private static DependencyObject? GetParentObject(DependencyObject child)
        {
            if (child is Visual || child is Visual3D)
            {
                return VisualTreeHelper.GetParent(child);
            }

            if (child is FrameworkContentElement frameworkContent)
            {
                return frameworkContent.Parent;
            }

            return LogicalTreeHelper.GetParent(child);
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;

                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }

            return null;
        }

        private void MonthSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BillsGrid != null && MonthSelector.SelectedIndex >= 0)
            {
                BillsGrid.ItemsSource = AnnualData[months[MonthSelector.SelectedIndex]];
                _detailedCategory = null;
                UpdateDashboard();
            }
        }

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        /// <summary>
        /// Applies category/search filtering to the month view, with transaction-safe deferral
        /// when WPF is in AddNew/EditItem mode.
        /// </summary>
        private void ApplyFilters()
            {
            if (_isFilteringActive) return;
            _isFilteringActive = true;
            try
            {
                if (BillsGrid == null || MonthSelector.SelectedIndex < 0) return;
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(
                    AnnualData[months[MonthSelector.SelectedIndex]]);
                if (view == null) return;

                if (view is System.Windows.Data.ListCollectionView lcv &&
                    (lcv.IsAddingNew || lcv.IsEditingItem))
                {
                    // ListCollectionView blocks Filter changes during edit/add transactions.
                    Dispatcher.BeginInvoke(new Action(ApplyFilters), System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }

                string search = SearchBox.Text ?? string.Empty;
                string? selectedCategory = CategoryFilter.SelectedItem as string;

                try
                {
                    view.Filter = item =>
                    {
                        if (item is not Bill b) return false;

                        bool categoryMatch = string.IsNullOrEmpty(selectedCategory) || selectedCategory == "All Categories" || b.Category == selectedCategory;
                        // Use OrdinalIgnoreCase to avoid culture-specific casing bugs (e.g. Turkish İ/I).
                        bool searchMatch = string.IsNullOrEmpty(search)
                            || b.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || b.Category.Contains(search, StringComparison.OrdinalIgnoreCase);

                        return categoryMatch && searchMatch;
                    };
                }
                catch (InvalidOperationException)
                {
                    Dispatcher.BeginInvoke(new Action(ApplyFilters), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            finally
            {
                _isFilteringActive = false;
            }
        }

        private void OpenAddDialog_Click(object sender, RoutedEventArgs e)
        {
            if (MonthSelector.SelectedIndex < 0)
            {
                MessageBox.Show("Please select a month before adding a bill.", "No Month Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int selectedMonthIndex = MonthSelector.SelectedIndex;
            int targetYear = int.TryParse(CurrentYear, out var parsedYear) ? parsedYear : DateTime.Today.Year;
            var targetPeriod = new DateTime(targetYear, selectedMonthIndex + 1, 1);
            string curMonth = months[selectedMonthIndex];

            var bulkChoice = MessageBox.Show(
                this,
                "Add more than one bill?\n\nYes = Bulk grid\nNo = Single bill\nCancel = go back",
                "Add Bill",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (bulkChoice == MessageBoxResult.Cancel)
            {
                return;
            }

            if (bulkChoice == MessageBoxResult.Yes)
            {
                var bulk = new BulkBillsDialog(targetPeriod) { Owner = this };
                if (bulk.ShowDialog() == true && bulk.ResultBills is { Count: > 0 } list)
                {
                    foreach (var b in list)
                    {
                        AddBillWithRecurrenceExpansion(b, b.IsRecurring, curMonth, selectedMonthIndex, targetYear);
                    }

                    AutoSave();
                }

                return;
            }

            var diag = new BillDialog(targetPeriod) { Owner = this };
            if (diag.ShowDialog() == true && diag.ResultBill != null)
            {
                var baseBill = diag.ResultBill;
                baseBill.IsRecurring = diag.IsRecurring;
                AddBillWithRecurrenceExpansion(baseBill, diag.IsRecurring, curMonth, selectedMonthIndex, targetYear);
                AutoSave();
            }
        }

        private void AddBillWithRecurrenceExpansion(Bill baseBill, bool recurring, string curMonth, int selectedMonthIndex, int targetYear)
        {
            baseBill.IsRecurring = recurring;
            if (!baseBill.IsRecurring)
            {
                baseBill.RecurrenceFrequency = RecurrenceFrequency.None;
                baseBill.RecurrenceGroupId = null;
            }
            else if (baseBill.RecurrenceFrequency == RecurrenceFrequency.None)
            {
                baseBill.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
            }

            baseBill.RecurrenceGroupId = null;
            AssignBillContext(baseBill, curMonth);
            AnnualData[curMonth].Add(baseBill);
            UpdateDashboard();

            var addedCopies = new List<(string month, Bill bill)>();
            if (recurring && IsWeekBasedWpf(baseBill.RecurrenceFrequency))
            {
                ExpandWeeklySeriesWpf(baseBill, targetYear, addedCopies);
            }
            else if (recurring)
            {
                baseBill.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                int startMonth = selectedMonthIndex + 1;
                for (int i = selectedMonthIndex + 1; i < months.Length; i++)
                {
                    var targetMonth = i + 1;
                    if (!ShouldCreateMonthlyOccurrenceWpf(baseBill, startMonth, targetMonth, targetYear))
                    {
                        continue;
                    }

                    var monthKey = months[i];
                    int day = Math.Min(baseBill.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
                    var copy = new Bill
                    {
                        Id = baseBill.Id,
                        Name = baseBill.Name,
                        Amount = baseBill.Amount,
                        Category = baseBill.Category,
                        DueDate = new DateTime(targetYear, targetMonth, day),
                        IsPaid = false,
                        IsRecurring = true,
                        RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
                        RecurrenceEveryMonths = baseBill.RecurrenceEveryMonths,
                        RecurrenceEndMode = baseBill.RecurrenceEndMode,
                        RecurrenceEndDate = baseBill.RecurrenceEndDate,
                        RecurrenceMaxOccurrences = baseBill.RecurrenceMaxOccurrences
                    };
                    AssignBillContext(copy, monthKey);
                    AnnualData[monthKey].Add(copy);
                    addedCopies.Add((monthKey, copy));
                }
            }

            PushUndo(() =>
            {
                AnnualData[curMonth].Remove(baseBill);
                if (addedCopies.Count > 0)
                {
                    foreach (var (m, c) in addedCopies)
                    {
                        AnnualData[m].Remove(c);
                    }
                }

                UpdateDashboard();
            },
            () =>
            {
                AnnualData[curMonth].Add(baseBill);
                if (addedCopies.Count > 0)
                {
                    foreach (var (m, c) in addedCopies)
                    {
                        AnnualData[m].Add(c);
                    }
                }

                UpdateDashboard();
            });
        }

        private void BillsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BillsGrid.SelectedItem is Bill b)
            {
                var source = e.OriginalSource as DependencyObject;
                while (source != null && source is not DataGridRow && source is not DataGrid)
                    source = VisualTreeHelper.GetParent(source);

                if (source is DataGridRow)
                {
                    bool oldVal = b.IsPaid;
                    b.IsPaid = !b.IsPaid;
                    System.Windows.Data.CollectionViewSource.GetDefaultView(BillsGrid.ItemsSource)?.Refresh();
                    UpdateDashboard();

                    PushUndo(() => { b.IsPaid = oldVal; UpdateDashboard(); },
                             () => { b.IsPaid = !oldVal; UpdateDashboard(); });

                    AutoSave();
                }
            }
        }

        private void DeleteBill_Click(object sender, RoutedEventArgs e)
        {
            if (BillsGrid.SelectedItem is not Bill b)
            {
                MessageBox.Show("Please select a bill to delete.", "No Bill Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selectedMonthIndex = MonthSelector.SelectedIndex;
            if (selectedMonthIndex < 0) return;

            var rootId = b.RecurrenceGroupId ?? b.Id;
            bool isSeries = b.IsRecurring
                || b.RecurrenceGroupId.HasValue
                || months.Any(m => AnnualData[m].Any(x => x.Id == b.Id && !ReferenceEquals(x, b)))
                || months.Any(m => AnnualData[m].Any(x => x.RecurrenceGroupId == b.Id));

            string message = $"Are you sure you want to delete '{b.Name}'?";
            if (isSeries)
                message += "\n\nThis bill is part of a recurring series and related rows for this year will be removed.";

            if (MessageBox.Show(this, message, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var removed = new List<(string month, Bill bill)>();
                foreach (var mKey in months)
                {
                    var toRemove = AnnualData[mKey].Where(x => x.Id == rootId || x.RecurrenceGroupId == rootId).ToList();
                    foreach (var r in toRemove)
                    {
                        AnnualData[mKey].Remove(r);
                        removed.Add((mKey, r));
                    }
                }

                UpdateDashboard();

                PushUndo(() =>
                {
                    foreach (var (m, bill) in removed)
                        AnnualData[m].Add(bill);
                    UpdateDashboard();
                },
                () =>
                {
                    foreach (var mKey in months)
                    {
                        var toRemove = AnnualData[mKey].Where(x => x.Id == rootId || x.RecurrenceGroupId == rootId).ToList();
                        foreach (var r in toRemove)
                        {
                            AnnualData[mKey].Remove(r);
                        }
                    }

                    UpdateDashboard();
                });

                AutoSave();
            }
        }

        /// <summary>
        /// Opens the add/edit dialog and applies recurring/non-recurring behavior based on dialog state.
        /// </summary>
        private void EditBill_Click(object sender, RoutedEventArgs e)
        {
            if (BillsGrid.SelectedItem is not Bill b)
            {
                MessageBox.Show("Please select a bill to edit.", "No Bill Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MonthSelector.SelectedIndex < 0)
            {
                MessageBox.Show("Please select a month before editing a bill.", "No Month Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int selectedMonthIndex = MonthSelector.SelectedIndex;
            int targetYear = int.TryParse(CurrentYear, out var editYear) ? editYear : DateTime.Today.Year;
            string curMonthKey = months[selectedMonthIndex];
            var diag = new BillDialog(b, b.IsRecurring) { Owner = this };
            if (diag.ShowDialog() == true && diag.ResultBill != null)
            {
                if (MessageBox.Show("Are you sure you want to apply these changes?", "Confirm Edit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                var res = diag.ResultBill;

                if (b.RecurrenceGroupId.HasValue && !b.IsRecurring)
                {
                    var snapshot = CloneBillForExport(b);
                    ApplyBillScalarsFromEdit(b, res);
                    UpdateDashboard();
                    PushUndo(
                        () => { ApplyBillScalarsFromEdit(b, snapshot); UpdateDashboard(); },
                        () => { ApplyBillScalarsFromEdit(b, CloneBillForExport(res)); UpdateDashboard(); });
                    AutoSave();
                    return;
                }

                bool weekInvolved = (b.IsRecurring && IsWeekBasedWpf(b.RecurrenceFrequency))
                    || (res.IsRecurring && IsWeekBasedWpf(res.RecurrenceFrequency));

                if (weekInvolved)
                {
                    var rootId = b.RecurrenceGroupId ?? b.Id;
                    var removedSnapshot = new List<(string m, Bill bill)>();
                    foreach (var m in months)
                    {
                        foreach (var x in AnnualData[m].Where(x => x.Id == rootId || x.RecurrenceGroupId == rootId).ToList())
                        {
                            removedSnapshot.Add((m, CloneBillForExport(x)));
                        }
                    }

                    RemoveBillGroupFromYear(rootId);
                    var anchorId = rootId;
                    var anchor = new Bill
                    {
                        Id = anchorId,
                        Name = res.Name,
                        Amount = res.Amount,
                        Category = res.Category,
                        DueDate = res.DueDate,
                        IsPaid = res.IsPaid,
                        IsRecurring = res.IsRecurring,
                        RecurrenceEveryMonths = Math.Max(1, res.RecurrenceEveryMonths),
                        RecurrenceEndMode = res.RecurrenceEndMode,
                        RecurrenceEndDate = res.RecurrenceEndDate,
                        RecurrenceMaxOccurrences = res.RecurrenceMaxOccurrences,
                        RecurrenceGroupId = null,
                        RecurrenceFrequency = res.IsRecurring
                            ? (res.RecurrenceFrequency == RecurrenceFrequency.None
                                ? RecurrenceFrequency.MonthlyInterval
                                : res.RecurrenceFrequency)
                            : RecurrenceFrequency.None
                    };

                    var addedAfter = new List<(string m, Bill bill)>();
                    if (!res.IsRecurring)
                    {
                        AssignBillContext(anchor, curMonthKey);
                        AnnualData[curMonthKey].Add(anchor);
                        addedAfter.Add((curMonthKey, anchor));
                    }
                    else if (IsWeekBasedWpf(anchor.RecurrenceFrequency))
                    {
                        AssignBillContext(anchor, curMonthKey);
                        AnnualData[curMonthKey].Add(anchor);
                        addedAfter.Add((curMonthKey, anchor));
                        ExpandWeeklySeriesWpf(anchor, targetYear, addedAfter);
                    }
                    else
                    {
                        anchor.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                        AssignBillContext(anchor, curMonthKey);
                        AnnualData[curMonthKey].Add(anchor);
                        addedAfter.Add((curMonthKey, anchor));
                        int startMonth = selectedMonthIndex + 1;
                        for (int i = selectedMonthIndex + 1; i < months.Length; i++)
                        {
                            var targetMonth = i + 1;
                            if (!ShouldCreateMonthlyOccurrenceWpf(anchor, startMonth, targetMonth, targetYear))
                            {
                                continue;
                            }

                            var monthKey = months[i];
                            int day = Math.Min(anchor.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
                            var copy = new Bill
                            {
                                Id = anchor.Id,
                                Name = anchor.Name,
                                Amount = anchor.Amount,
                                Category = anchor.Category,
                                DueDate = new DateTime(targetYear, targetMonth, day),
                                IsPaid = false,
                                IsRecurring = true,
                                RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
                                RecurrenceEveryMonths = anchor.RecurrenceEveryMonths,
                                RecurrenceEndMode = anchor.RecurrenceEndMode,
                                RecurrenceEndDate = anchor.RecurrenceEndDate,
                                RecurrenceMaxOccurrences = anchor.RecurrenceMaxOccurrences
                            };
                            AssignBillContext(copy, monthKey);
                            AnnualData[monthKey].Add(copy);
                            addedAfter.Add((monthKey, copy));
                        }
                    }

                    UpdateDashboard();
                    PushUndo(
                        () =>
                        {
                            foreach (var (m, x) in addedAfter)
                            {
                                AnnualData[m].Remove(x);
                            }

                            foreach (var (m, x) in removedSnapshot)
                            {
                                AnnualData[m].Add(CloneBillForExport(x));
                            }

                            UpdateDashboard();
                        },
                        () =>
                        {
                            foreach (var m in months)
                            {
                                var toRemove = AnnualData[m].Where(x => x.Id == anchorId || x.RecurrenceGroupId == anchorId).ToList();
                                foreach (var r in toRemove)
                                {
                                    AnnualData[m].Remove(r);
                                }
                            }

                            foreach (var (m, x) in addedAfter)
                            {
                                AnnualData[m].Add(x);
                            }

                            UpdateDashboard();
                        });

                    AutoSave();
                    return;
                }

                var old = CloneBillForExport(b);
                ApplyBillScalarsFromEdit(b, res);

                var addedCopies = new List<(string month, Bill bill)>();
                var modifiedCopies = new List<(string month, Bill originalState, Bill currentObj)>();
                var removedFutureCopies = new List<(string month, Bill bill)>();

                if (diag.IsRecurring)
                {
                    int startMonth = selectedMonthIndex + 1;
                    for (int i = selectedMonthIndex + 1; i < months.Length; i++)
                    {
                        var targetMonth = i + 1;
                        if (!ShouldCreateMonthlyOccurrenceWpf(b, startMonth, targetMonth, targetYear))
                        {
                            var monthKey = months[i];
                            var toDrop = AnnualData[monthKey].Where(x => x.Id == b.Id).ToList();
                            foreach (var futureBill in toDrop)
                            {
                                AnnualData[monthKey].Remove(futureBill);
                                removedFutureCopies.Add((monthKey, futureBill));
                            }

                            continue;
                        }

                        var mk = months[i];
                        var existing = AnnualData[mk].FirstOrDefault(x => x.Id == b.Id);

                        if (existing != null)
                        {
                            modifiedCopies.Add((mk, CloneBillForExport(existing), existing));
                            existing.Name = b.Name;
                            existing.Amount = b.Amount;
                            existing.Category = b.Category;
                            existing.IsRecurring = true;
                            existing.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                            existing.RecurrenceEveryMonths = b.RecurrenceEveryMonths;
                            existing.RecurrenceEndMode = b.RecurrenceEndMode;
                            existing.RecurrenceEndDate = b.RecurrenceEndDate;
                            existing.RecurrenceMaxOccurrences = b.RecurrenceMaxOccurrences;
                            int daysInMonth = DateTime.DaysInMonth(existing.DueDate.Year, existing.DueDate.Month);
                            int newDay = Math.Min(b.DueDate.Day, daysInMonth);
                            existing.DueDate = new DateTime(existing.DueDate.Year, existing.DueDate.Month, newDay);
                        }
                        else
                        {
                            int day = Math.Min(b.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
                            var copy = new Bill
                            {
                                Id = b.Id,
                                Name = b.Name,
                                Amount = b.Amount,
                                Category = b.Category,
                                DueDate = new DateTime(targetYear, targetMonth, day),
                                IsPaid = false,
                                IsRecurring = true,
                                RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval,
                                RecurrenceEveryMonths = b.RecurrenceEveryMonths,
                                RecurrenceEndMode = b.RecurrenceEndMode,
                                RecurrenceEndDate = b.RecurrenceEndDate,
                                RecurrenceMaxOccurrences = b.RecurrenceMaxOccurrences
                            };
                            AssignBillContext(copy, mk);
                            AnnualData[mk].Add(copy);
                            addedCopies.Add((mk, copy));
                        }
                    }

                    if (addedCopies.Count > 0 || modifiedCopies.Count > 0 || removedFutureCopies.Count > 0)
                    {
                        UpdateDashboard();
                    }
                }
                else
                {
                    for (int i = selectedMonthIndex + 1; i < months.Length; i++)
                    {
                        var monthKey = months[i];
                        var toRemove = AnnualData[monthKey].Where(x => x.Id == b.Id).ToList();
                        foreach (var futureBill in toRemove)
                        {
                            AnnualData[monthKey].Remove(futureBill);
                            removedFutureCopies.Add((monthKey, futureBill));
                        }
                    }

                    if (removedFutureCopies.Count > 0)
                    {
                        UpdateDashboard();
                    }
                }

                PushUndo(() =>
                {
                    ApplyBillScalarsFromEdit(b, old);
                    foreach (var (m, c) in addedCopies)
                    {
                        AnnualData[m].Remove(c);
                    }

                    foreach (var (m, c) in removedFutureCopies)
                    {
                        AnnualData[m].Add(c);
                    }

                    foreach (var (m, state, obj) in modifiedCopies)
                    {
                        ApplyBillScalarsFromEdit(obj, state);
                    }

                    UpdateDashboard();
                },
                () =>
                {
                    ApplyBillScalarsFromEdit(b, CloneBillForExport(res));
                    foreach (var (m, c) in addedCopies)
                    {
                        AnnualData[m].Add(c);
                    }

                    foreach (var (m, c) in removedFutureCopies)
                    {
                        AnnualData[m].Remove(c);
                    }

                    foreach (var (m, state, obj) in modifiedCopies)
                    {
                        obj.Name = res.Name;
                        obj.Amount = res.Amount;
                        obj.Category = res.Category;
                        int daysInMonth = DateTime.DaysInMonth(obj.DueDate.Year, obj.DueDate.Month);
                        int newDay = Math.Min(res.DueDate.Day, daysInMonth);
                        obj.DueDate = new DateTime(obj.DueDate.Year, obj.DueDate.Month, newDay);
                        obj.IsPaid = state.IsPaid;
                        obj.IsRecurring = true;
                        obj.RecurrenceEveryMonths = res.RecurrenceEveryMonths;
                        obj.RecurrenceEndMode = res.RecurrenceEndMode;
                        obj.RecurrenceEndDate = res.RecurrenceEndDate;
                        obj.RecurrenceMaxOccurrences = res.RecurrenceMaxOccurrences;
                        obj.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                    }

                    UpdateDashboard();
                });

                AutoSave();
            }
        }

        private static void ApplyBillScalarsFromEdit(Bill target, Bill source)
        {
            target.Name = source.Name;
            target.Amount = source.Amount;
            target.Category = source.Category;
            target.DueDate = source.DueDate;
            target.IsPaid = source.IsPaid;
            target.IsRecurring = source.IsRecurring;
            target.RecurrenceFrequency = source.RecurrenceFrequency;
            target.RecurrenceGroupId = source.RecurrenceGroupId;
            target.RecurrenceEveryMonths = source.RecurrenceEveryMonths;
            target.RecurrenceEndMode = source.RecurrenceEndMode;
            target.RecurrenceEndDate = source.RecurrenceEndDate;
            target.RecurrenceMaxOccurrences = source.RecurrenceMaxOccurrences;
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.TryPop(out var act))
            {
                act.Undo?.Invoke();
                _redoStack.Push(act);
                CommandManager.InvalidateRequerySuggested();
                AutoSave();
            }
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redoStack.TryPop(out var act))
            {
                act.Redo?.Invoke();
                _undoStack.Push(act);
                CommandManager.InvalidateRequerySuggested();
                AutoSave();
            }
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"CrispyBills_Report_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    ExportCsvToPath(dlg.FileName);
                    var years = GetAvailableYears().OrderBy(y => y, StringComparer.Ordinal).ToList();
                    MessageBox.Show($"Successfully exported {years.Count} year(s) and notes to {Path.GetFileName(dlg.FileName)}", "Export Complete");
                }
                catch (Exception ex)
                {
                    LogNonFatal("ExportCsv_Click", ex);
                    MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public void ImportCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
                if (dlg.ShowDialog() != true) return;

                var lines = File.ReadAllLines(dlg.FileName);
                if (lines.Length == 0)
                {
                    MessageBox.Show(this, "The selected CSV is empty.", "Import CSV", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StructuredImportPackage importPackage;
                try
                {
                    importPackage = ParseStructuredReportCsv(lines);
                }
                catch (Exception ex)
                {
                    LogNonFatal("ImportCsv_Click parse", ex);
                    MessageBox.Show($"The structured CSV could not be parsed.\nError: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (importPackage.Years.Count == 0 && !importPackage.HasNotesSection)
                {
                    MessageBox.Show("The structured CSV does not contain any importable year, month, or notes data.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var monthlySummary = importPackage.Years.ToDictionary(
                    yearEntry => yearEntry.Key,
                    yearEntry => yearEntry.Value.BillsByMonth.ToDictionary(
                        monthEntry => monthEntry.Key,
                        monthEntry => (
                            BillCount: monthEntry.Value.Count,
                            Expense: monthEntry.Value.Sum(b => b.Amount),
                            Income: yearEntry.Value.IncomeByMonth.GetValueOrDefault(monthEntry.Key, 0m)),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

                var importDialog = new ImportSelectionDialog(
                    importPackage.Years.Keys.OrderBy(year => year, StringComparer.OrdinalIgnoreCase),
                    months,
                    importPackage.HasNotesSection,
                    monthlySummary)
                {
                    Owner = this
                };

                if (importDialog.ShowDialog() != true)
                    return;

                var selectedMonthsByYear = importDialog.SelectedMonthsByYear;
                bool importNotes = importPackage.HasNotesSection && importDialog.ImportNotes;

                if (!ConfirmAndBackupBeforeImport(selectedMonthsByYear, importNotes))
                    return;

                ApplyStructuredImportPackage(importPackage, selectedMonthsByYear, importNotes);
            }
            catch (Exception ex)
            {
                LogNonFatal("ImportCsv_Click", ex);
                MessageBox.Show($"Import failed: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DebugEnableMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuItem mi) return;

                bool enabling = mi.IsChecked == true;

                if (enabling && !_debugEnableWarningShown)
                {
                    var res = MessageBox.Show(
                        "Enabling destructive delete tools will allow permanent removal of data. Backups will be created automatically, but this action can be dangerous. Do you want to enable these tools for this session?",
                        "Enable Destructive Tools",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (res != MessageBoxResult.Yes)
                    {
                        mi.IsChecked = false;
                        _debugDestructiveDeletesEnabled = false;
                        DebugDeleteMonthMenuItem.IsEnabled = false;
                        DebugDeleteYearMenuItem.IsEnabled = false;
                        return;
                    }

                    _debugEnableWarningShown = true;
                }

                _debugDestructiveDeletesEnabled = enabling;
                DebugDeleteMonthMenuItem.IsEnabled = _debugDestructiveDeletesEnabled;
                DebugDeleteYearMenuItem.IsEnabled = _debugDestructiveDeletesEnabled;
                SetStatus(_debugDestructiveDeletesEnabled ? "Destructive debug tools enabled (session)." : "Destructive debug tools disabled.");
            }
            catch (Exception ex)
            {
                LogNonFatal("DebugEnableMenuItem_Click", ex);
            }
        }

        private void DebugDeleteMonth_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_debugDestructiveDeletesEnabled)
                {
                    MessageBox.Show("Destructive tools are not enabled. Check Debug > Enable Destructive Delete Tools first.", "Not Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (MonthSelector.SelectedIndex < 0)
                {
                    MessageBox.Show("No month selected.", "Delete Month", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var monthName = months[MonthSelector.SelectedIndex];
                var confirm = MessageBox.Show($"Permanently delete all bills and income for {monthName} in {CurrentYear}? This will be backed up first. Proceed?", "Confirm Delete Month", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                BackupDatabase(CurrentYear);

                var savedBills = AnnualData[monthName].Select(CloneBillForExport).ToList();
                var savedIncome = MonthlyIncome.GetValueOrDefault(monthName, 0m);

                PushUndo(() =>
                {
                    AnnualData[monthName].Clear();
                    foreach (var sb in savedBills)
                        AnnualData[monthName].Add(sb);
                    MonthlyIncome[monthName] = savedIncome;
                    UpdateDashboard();
                },
                () =>
                {
                    AnnualData[monthName].Clear();
                    MonthlyIncome[monthName] = 0m;
                    UpdateDashboard();
                });

                AnnualData[monthName].Clear();
                MonthlyIncome[monthName] = 0m;

                try
                {
                    PersistYearDataToDatabase(GetDbPath(CurrentYear));
                    SetStatus($"Deleted month {monthName} in {CurrentYear}. Backup created.");
                }
                catch (Exception ex)
                {
                    LogNonFatal("DebugDeleteMonth persist", ex);
                    MessageBox.Show("Month deletion persisted with errors. See logs.", "Delete Month", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                UpdateDashboard();
            }
            catch (Exception ex)
            {
                LogNonFatal("DebugDeleteMonth_Click", ex);
            }
        }

        private void DebugDeleteYear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_debugDestructiveDeletesEnabled)
                {
                    MessageBox.Show("Destructive tools are not enabled. Check Debug > Enable Destructive Delete Tools first.", "Not Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var targetYear = YearSelector.SelectedItem as string ?? CurrentYear;
                var confirm = MessageBox.Show($"Permanently delete the database and sidecars for year {targetYear}? This cannot be undone except from backups. Proceed?", "Confirm Delete Year", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                try
                {
                    BackupDatabase(targetYear);
                }
                catch (Exception ex)
                {
                    LogNonFatal($"DebugDeleteYear backup ({targetYear})", ex);
                    MessageBox.Show($"Backup failed: {ex.Message}", "Delete Year", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                var dbFile = GetDbPath(targetYear);
                try
                {
                    if (File.Exists(dbFile)) File.Delete(dbFile);
                    var wal = dbFile + "-wal";
                    var shm = dbFile + "-shm";
                    if (File.Exists(wal)) File.Delete(wal);
                    if (File.Exists(shm)) File.Delete(shm);

                    SetStatus($"Deleted database for {targetYear}. Backup created.");
                }
                catch (Exception ex)
                {
                    LogNonFatal($"DebugDeleteYear file delete ({targetYear})", ex);
                    MessageBox.Show($"Failed to delete some files.\n{ex}", "Delete Year", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                UpdateYearSelector();
                var years = GetAvailableYears();
                if (!years.Contains(CurrentYear))
                {
                    _currentYear = years.FirstOrDefault() ?? DateTime.Now.Year.ToString();
                }

                LoadData();
            }
            catch (Exception ex)
            {
                LogNonFatal("DebugDeleteYear_Click", ex);
                MessageBox.Show($"Error: {ex.Message}", "Delete Year", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleYearArchive_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(CurrentYear, out var year)) return;

            if (_archivedYears.Contains(year))
            {
                _archivedYears.Remove(year);
                SaveArchivedYears();
                MessageBox.Show($"Year {year} has been unarchived.", "Year Archive", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var result = MessageBox.Show(
                    $"Archive year {year}? It will be hidden from the year list but data will be preserved.",
                    "Archive Year",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _archivedYears.Add(year);
                    SaveArchivedYears();
                }
            }

            UpdateYearSelector();
        }

        private void ShowArchivedYears_Click(object sender, RoutedEventArgs e)
        {
            _showArchivedYears = ShowArchivedMenuItem.IsChecked;
            UpdateYearSelector();
        }

        private void PayPeriodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (PayPeriodCombo.SelectedItem is string period)
                SaveAppMeta("income_pay_period", period);
        }

        private void ExportCsvToPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Export path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            string? outDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(outDir))
                throw new InvalidOperationException("Could not resolve export directory.");

            Directory.CreateDirectory(outDir);

            var sb = new StringBuilder();
            // Match ExportCsv_Click format exactly (no trailing spaces).
            sb.AppendLine($"REPORT,{EscapeCsv("Crispy_Bills Export")}");
            sb.AppendLine($"GENERATED AT,{EscapeCsv(DateTime.Now.ToString("f"))}");
            sb.AppendLine($"EXPORT SCOPE,{EscapeCsv("All available years")}");
            sb.AppendLine();

            var years = GetAvailableYears().OrderBy(y => y, StringComparer.Ordinal).ToList();
            foreach (var year in years)
            {
                var (billsByMonth, incomeByMonth) = LoadYearExportData(year);
                decimal yearIncome = months.Sum(m => incomeByMonth.GetValueOrDefault(m, 0m));
                decimal yearExpenses = months.Sum(m => billsByMonth[m].Sum((Bill b) => b.Amount));
                decimal yearRemaining = months.Sum(m => billsByMonth[m].Where((Bill b) => !b.IsPaid).Sum((Bill b) => b.Amount));

                sb.AppendLine($"===== YEAR =====,{year}");
                sb.AppendLine($"YEAR SUMMARY,Income,{yearIncome.ToString(CultureInfo.InvariantCulture)},Expenses,{yearExpenses.ToString(CultureInfo.InvariantCulture)},Remaining,{yearRemaining.ToString(CultureInfo.InvariantCulture)},Net,{(yearIncome - yearExpenses).ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine();

                foreach (var month in months)
                {
                    if (!billsByMonth.TryGetValue(month, out var monthBills))
                    {
                        LogNonFatal($"ExportCsvToPath: missing month in data: {month} for year {year}");
                        continue;
                    }

                    var bills = monthBills
                        .OrderBy(b => b.DueDate)
                        .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    decimal income = incomeByMonth.GetValueOrDefault(month, 0m);
                    decimal expenses = bills.Sum(b => b.Amount);
                    decimal remaining = bills.Where(b => !b.IsPaid).Sum(b => b.Amount);
                    decimal paid = expenses - remaining;

                    sb.AppendLine($"--- MONTH ---,{month}");
                    sb.AppendLine($"MONTH SUMMARY,Income,{income.ToString(CultureInfo.InvariantCulture)},Expenses,{expenses.ToString(CultureInfo.InvariantCulture)},Paid,{paid.ToString(CultureInfo.InvariantCulture)},Remaining,{remaining.ToString(CultureInfo.InvariantCulture)},Net,{(income - expenses).ToString(CultureInfo.InvariantCulture)},Bill Count,{bills.Count}");
                    sb.AppendLine("Name,Category,Amount,Due Date,Status,Recurring,Past Due,Month,Year");

                    int monthIndex = Array.IndexOf(months, month);
                    if (monthIndex < 0)
                    {
                        LogNonFatal($"ExportCsvToPath: unknown month '{month}' for year {year}");
                        continue;
                    }

                    if (!int.TryParse(year, out int yearVal) || yearVal <= 0)
                    {
                        LogNonFatal($"ExportCsvToPath: invalid year value '{year}'");
                        continue;
                    }

                    var contextStart = new DateTime(yearVal, monthIndex + 1, 1);

                    foreach (var b in bills)
                    {
                        bool isPastDue = !b.IsPaid && (b.DueDate.Date < DateTime.Today || b.DueDate.Date < contextStart.Date);
                        string status = b.IsPaid ? "PAID" : isPastDue ? "PAST DUE" : "DUE";
                        sb.AppendLine(string.Join(",",
                            EscapeCsv(b.Name),
                            EscapeCsv(b.Category),
                            b.Amount.ToString(CultureInfo.InvariantCulture),
                            EscapeCsv(b.DueDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)),
                            EscapeCsv(status),
                            b.IsRecurring ? "Yes" : "No",
                            isPastDue ? "Yes" : "No",
                            EscapeCsv(month),
                            EscapeCsv(year)));
                    }

                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            string notesText = NotesBox?.Text ?? string.Empty;
            string[] noteLines = SplitNotesLines(notesText);

            sb.AppendLine("===== NOTES =====,Global Notes");
            sb.AppendLine($"NOTES SUMMARY,Line Count,{noteLines.Length},Character Count,{notesText.Length}");
            sb.AppendLine("Line Number,Text");
            for (int i = 0; i < noteLines.Length; i++)
            {
                sb.AppendLine($"{i + 1},{EscapeCsv(noteLines[i])}");
            }

            File.WriteAllText(fullPath, sb.ToString());
        }

        private (Dictionary<string, List<Bill>> BillsByMonth, Dictionary<string, decimal> IncomeByMonth) LoadYearExportData(string year)
        {
            if (year == CurrentYear)
            {
                var currentBills = months.ToDictionary(
                    month => month,
                    month => AnnualData[month].Select(CloneBillForExport).ToList());
                var currentIncome = months.ToDictionary(
                    month => month,
                    month => MonthlyIncome.GetValueOrDefault(month, 0m));
                return (currentBills, currentIncome);
            }

            var billsByMonth = months.ToDictionary(month => month, _ => new List<Bill>());
            var incomeByMonth = months.ToDictionary(month => month, _ => 0m);

            var dbFile = GetDbPath(year);
            if (!File.Exists(dbFile))
            {
                LogNonFatal($"LoadYearExportData: database file missing for year {year} ({dbFile})");
                return (billsByMonth, incomeByMonth);
            }

            InitializeDatabase(dbFile);

            var exportCsb = new SqliteConnectionStringBuilder { DataSource = dbFile };
            using var conn = new SqliteConnection(exportCsb.ConnectionString);
            conn.Open();

            using var billsCmd = conn.CreateCommand();
            billsCmd.CommandText = @"
SELECT Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
       RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences,
       RecurrenceFrequency, RecurrenceGroupId
FROM Bills WHERE Year = $y";
            billsCmd.Parameters.AddWithValue("$y", year);
            using (var reader = billsCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string monthName = MonthNameFromDbReader(reader, 1);
                    if (!billsByMonth.ContainsKey(monthName))
                        continue;

                    var guidStr = reader.GetString(0);
                    if (!Guid.TryParse(guidStr, out var id))
                    {
                        LogNonFatal($"Skipping row with invalid GUID: {guidStr}");
                        continue;
                    }

                    var bill = new Bill
                    {
                        Id = id,
                        Name = reader.GetString(3),
                        Amount = reader.GetDecimal(4),
                        DueDate = DateTime.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        IsPaid = reader.GetInt32(6) == 1,
                        Category = reader.GetString(7),
                        IsRecurring = reader.GetInt32(8) == 1,
                        RecurrenceEveryMonths = reader.FieldCount > 9 && !reader.IsDBNull(9) ? Math.Max(1, reader.GetInt32(9)) : 1,
                        RecurrenceEndMode = reader.FieldCount > 10 && !reader.IsDBNull(10)
                            ? ParseRecurrenceEndModeWpf(reader.GetString(10))
                            : RecurrenceEndMode.None,
                        RecurrenceEndDate = reader.FieldCount > 11 && !reader.IsDBNull(11)
                            && DateTime.TryParse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDe)
                            ? endDe
                            : null,
                        RecurrenceMaxOccurrences = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetInt32(12) : null,
                        RecurrenceFrequency = reader.FieldCount > 13 && !reader.IsDBNull(13)
                            ? ParseRecurrenceFrequencyWpf(reader.GetString(13))
                            : RecurrenceFrequency.MonthlyInterval,
                        RecurrenceGroupId = reader.FieldCount > 14 && !reader.IsDBNull(14) && Guid.TryParse(reader.GetString(14), out var rgid)
                            ? rgid
                            : null
                    };
                    if (!bill.IsRecurring)
                    {
                        bill.RecurrenceFrequency = RecurrenceFrequency.None;
                    }
                    else if (bill.RecurrenceFrequency == RecurrenceFrequency.None)
                    {
                        bill.RecurrenceFrequency = RecurrenceFrequency.MonthlyInterval;
                    }

                    billsByMonth[monthName].Add(bill);
                }
            }

            using var incomeCmd = conn.CreateCommand();
            incomeCmd.CommandText = "SELECT Month, Amount FROM Income WHERE Year = $y";
            incomeCmd.Parameters.AddWithValue("$y", year);
            using (var reader = incomeCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var monthName = MonthNameFromDbReader(reader, 0);
                    if (incomeByMonth.ContainsKey(monthName))
                    {
                        incomeByMonth[monthName] = reader.GetDecimal(1);
                    }
                }
            }

            return (billsByMonth, incomeByMonth);
        }

        private Bill CloneBillForExport(Bill source)
        {
            return new Bill
            {
                Id = source.Id,
                Name = source.Name,
                Amount = source.Amount,
                DueDate = source.DueDate,
                IsPaid = source.IsPaid,
                Category = source.Category,
                IsRecurring = source.IsRecurring,
                RecurrenceFrequency = source.RecurrenceFrequency,
                RecurrenceGroupId = source.RecurrenceGroupId,
                RecurrenceEveryMonths = source.RecurrenceEveryMonths,
                RecurrenceEndMode = source.RecurrenceEndMode,
                RecurrenceEndDate = source.RecurrenceEndDate,
                RecurrenceMaxOccurrences = source.RecurrenceMaxOccurrences
            };
        }

        private static RecurrenceEndMode ParseRecurrenceEndModeWpf(string? raw)
        {
            if (Enum.TryParse(raw ?? string.Empty, ignoreCase: true, out RecurrenceEndMode parsed))
            {
                return parsed;
            }

            return RecurrenceEndMode.None;
        }

        private static RecurrenceFrequency ParseRecurrenceFrequencyWpf(string? raw)
        {
            if (Enum.TryParse(raw ?? string.Empty, ignoreCase: true, out RecurrenceFrequency parsed))
            {
                return parsed;
            }

            return RecurrenceFrequency.MonthlyInterval;
        }

        private static bool IsWeekBasedWpf(RecurrenceFrequency f)
            => f is RecurrenceFrequency.Weekly or RecurrenceFrequency.BiWeekly;

        private void RemoveBillGroupFromYear(Guid rootId)
        {
            foreach (var m in months)
            {
                var remove = AnnualData[m].Where(x => x.Id == rootId || x.RecurrenceGroupId == rootId).ToList();
                foreach (var r in remove)
                {
                    AnnualData[m].Remove(r);
                }
            }
        }

        private static bool RecurrenceOccurrenceAllowedWpf(Bill anchor, DateTime occurrenceDate, int occurrenceIndex)
        {
            if (anchor.RecurrenceEndMode == RecurrenceEndMode.EndAfterOccurrences
                && anchor.RecurrenceMaxOccurrences is int maxOccurrences
                && occurrenceIndex > maxOccurrences)
            {
                return false;
            }

            if (anchor.RecurrenceEndMode == RecurrenceEndMode.EndOnDate
                && anchor.RecurrenceEndDate is DateTime endDate
                && occurrenceDate.Date > endDate.Date)
            {
                return false;
            }

            return true;
        }

        private static bool ShouldCreateMonthlyOccurrenceWpf(Bill recurring, int startMonth, int targetMonth, int targetYear)
        {
            if (!recurring.IsRecurring || targetMonth < startMonth || IsWeekBasedWpf(recurring.RecurrenceFrequency))
            {
                return false;
            }

            var every = Math.Max(1, recurring.RecurrenceEveryMonths);
            var offset = targetMonth - startMonth;
            if (offset % every != 0)
            {
                return false;
            }

            var occurrenceIndex = (offset / every) + 1;
            if (recurring.RecurrenceEndMode == RecurrenceEndMode.EndAfterOccurrences
                && recurring.RecurrenceMaxOccurrences is int maxOccurrences
                && occurrenceIndex > maxOccurrences)
            {
                return false;
            }

            if (recurring.RecurrenceEndMode == RecurrenceEndMode.EndOnDate
                && recurring.RecurrenceEndDate is DateTime endDate)
            {
                int day = Math.Min(recurring.DueDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));
                var occurrenceDate = new DateTime(targetYear, targetMonth, day);
                if (occurrenceDate.Date > endDate.Date)
                {
                    return false;
                }
            }

            return true;
        }

        private void ExpandWeeklySeriesWpf(Bill anchor, int year, List<(string month, Bill bill)> addedCopies)
        {
            if (!anchor.IsRecurring || !IsWeekBasedWpf(anchor.RecurrenceFrequency))
            {
                return;
            }

            var groupId = anchor.Id;
            var step = anchor.RecurrenceFrequency == RecurrenceFrequency.Weekly ? 7 : 14;
            var d = anchor.DueDate.Date;
            var occurrenceIndex = 1;
            while (true)
            {
                d = d.AddDays(step);
                occurrenceIndex++;
                if (d.Year != year)
                {
                    break;
                }

                if (!RecurrenceOccurrenceAllowedWpf(anchor, d, occurrenceIndex))
                {
                    break;
                }

                var monthKey = months[d.Month - 1];
                if (AnnualData[monthKey].Any(x =>
                        (x.Id == groupId || x.RecurrenceGroupId == groupId) && x.DueDate.Date == d.Date))
                {
                    continue;
                }

                var child = new Bill
                {
                    Id = Guid.NewGuid(),
                    Name = anchor.Name,
                    Amount = anchor.Amount,
                    Category = anchor.Category,
                    DueDate = d,
                    IsPaid = false,
                    IsRecurring = false,
                    RecurrenceFrequency = RecurrenceFrequency.None,
                    RecurrenceGroupId = groupId,
                    RecurrenceEveryMonths = anchor.RecurrenceEveryMonths,
                    RecurrenceEndMode = RecurrenceEndMode.None,
                    RecurrenceEndDate = null,
                    RecurrenceMaxOccurrences = null
                };
                AssignBillContext(child, monthKey);
                AnnualData[monthKey].Add(child);
                addedCopies.Add((monthKey, child));
            }
        }

        private static DateTime FirstWeeklyOccurrenceOnOrAfterWpf(DateTime seriesStartDue, DateTime minInclusive, int stepDays)
        {
            var d = seriesStartDue.Date;
            var guard = 0;
            while (d < minInclusive.Date && guard++ < 400)
            {
                d = d.AddDays(stepDays);
            }

            return d;
        }

        private StructuredImportPackage ParseStructuredReportCsv(string[] lines)
        {
            return ImportExportHelpers.ParseStructuredReportCsv(lines, months, backupsRoot, LogNonFatalMessage, writeDiagnostics: true);
        }

        private static string[] SplitNotesLines(string notesText)
        {
            if (string.IsNullOrEmpty(notesText))
                return [];

            string normalized = notesText.Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Split('\n', StringSplitOptions.None);
        }

        private static string TrimNotesToLineLimit(string notesText, out bool wasTrimmed)
        {
            var lines = SplitNotesLines(notesText);
            if (lines.Length <= MaxNoteLines)
            {
                wasTrimmed = false;
                return notesText;
            }

            wasTrimmed = true;
            return string.Join(Environment.NewLine, lines.Take(MaxNoteLines));
        }

        private void ApplyStructuredImportPackage(StructuredImportPackage package, Dictionary<string, List<string>> selectedMonthsByYear, bool importNotes)
        {
            int importedBillCount = 0;
            int importedMonthCount = 0;

            foreach (var selection in selectedMonthsByYear)
            {
                if (!package.Years.TryGetValue(selection.Key, out var sourceYearData))
                    continue;

                var (targetBillsByMonth, targetIncomeByMonth) = LoadYearExportData(selection.Key);
                foreach (var month in selection.Value.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (Array.IndexOf(months, month) < 0)
                        continue;

                    if (!sourceYearData.BillsByMonth.ContainsKey(month) || !sourceYearData.IncomeByMonth.ContainsKey(month))
                        continue;

                    var importedBills = sourceYearData.BillsByMonth[month]
                        .Select(CloneBillForExport)
                        .ToList();
                    if (int.TryParse(selection.Key, out int targetYearValue))
                    {
                        int monthIndex = Array.IndexOf(months, month);
                        foreach (var bill in importedBills)
                        {
                            AssignBillContext(bill, targetYearValue, monthIndex + 1);
                        }
                    }

                    targetBillsByMonth[month] = importedBills;
                    targetIncomeByMonth[month] = sourceYearData.IncomeByMonth.GetValueOrDefault(month, 0m);
                    importedBillCount += importedBills.Count;
                    importedMonthCount++;
                }

                string targetDbPath = GetDbPath(selection.Key);
                BackupDatabase(selection.Key);
                InitializeDatabase(targetDbPath);
                PersistYearDataToDatabase(targetDbPath, selection.Key, targetBillsByMonth, targetIncomeByMonth);
            }

            if (importNotes)
            {
                string importedNotes = TrimNotesToLineLimit(package.NotesText, out bool notesWereTrimmed);
                _isUpdatingNotesText = true;
                if (NotesBox != null)
                {
                    NotesBox.Text = importedNotes;
                }
                _isUpdatingNotesText = false;
                SaveGlobalNotesToDatabase(importedNotes);

                if (notesWereTrimmed)
                {
                    SetStatus(NotesLineLimitMessage);
                }
            }

            UpdateYearSelector();

            if (selectedMonthsByYear.Keys.Contains(CurrentYear, StringComparer.OrdinalIgnoreCase))
            {
                LoadData();
            }
            else
            {
                UpdateDashboard();
            }

            string notesSuffix = importNotes ? " and notes" : string.Empty;
            MessageBox.Show($"Imported {importedBillCount} bill(s) across {importedMonthCount} month(s){notesSuffix}.", "Import Complete");
        }

        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        private static void AppendSummaryCss(StringBuilder sb)
        {
            sb.AppendLine("  <style>");
            sb.AppendLine("    :root { --bg:#f3f6fb; --panel:#fff; --ink:#122033; --muted:#5d6f87; --line:#d9e1ec; --good:#1f9d57; --warn:#cf7a00; --bad:#c73333; --accent:#1368ce; --accent-soft:#e9f2ff; }");
            sb.AppendLine("    * { box-sizing: border-box; }");
            sb.AppendLine("    body { margin:0; padding:28px; background:radial-gradient(circle at top right,#ddeaff 0%,var(--bg) 42%,#eef3fa 100%); color:var(--ink); font-family:Segoe UI,Tahoma,Arial,sans-serif; }");
            sb.AppendLine("    .wrap { max-width:1280px; margin:0 auto; }");
            sb.AppendLine("    .hero { display:flex; flex-wrap:wrap; justify-content:space-between; gap:14px; background:linear-gradient(128deg,#0f4da0 0%,#1d7ed6 65%,#43a7e6 100%); color:#fff; border-radius:18px; padding:22px 24px; box-shadow:0 12px 30px rgba(13,52,103,0.25); }");
            sb.AppendLine("    .hero h1 { margin:0; font-size:32px; letter-spacing:0.2px; }");
            sb.AppendLine("    .hero p { margin:8px 0 0 0; opacity:0.94; font-size:14px; }");
            sb.AppendLine("    .hero .meta { text-align:right; font-size:13px; opacity:0.9; }");
            sb.AppendLine("    .cards { margin-top:18px; display:grid; grid-template-columns:repeat(auto-fit,minmax(190px,1fr)); gap:12px; }");
            sb.AppendLine("    .card { background:var(--panel); border:1px solid var(--line); border-radius:14px; padding:14px 14px 12px 14px; box-shadow:0 6px 16px rgba(26,45,74,0.08); }");
            sb.AppendLine("    .kicker { font-size:12px; color:var(--muted); text-transform:uppercase; letter-spacing:0.9px; }");
            sb.AppendLine("    .value { margin-top:8px; font-size:25px; font-weight:700; }");
            sb.AppendLine("    .sub { margin-top:5px; font-size:12px; color:var(--muted); }");
            sb.AppendLine("    .tone-good { color:var(--good); } .tone-warn { color:var(--warn); } .tone-bad { color:var(--bad); }");
            sb.AppendLine("    .panel { margin-top:16px; background:var(--panel); border:1px solid var(--line); border-radius:14px; overflow:hidden; box-shadow:0 7px 18px rgba(26,45,74,0.08); }");
            sb.AppendLine("    .panel h2 { margin:0; padding:14px 16px; font-size:18px; background:linear-gradient(180deg,#f8fbff 0%,#eff5ff 100%); border-bottom:1px solid var(--line); }");
            sb.AppendLine("    table { width:100%; border-collapse:collapse; }");
            sb.AppendLine("    th,td { padding:10px 11px; border-bottom:1px solid var(--line); font-size:13px; vertical-align:middle; }");
            sb.AppendLine("    th { text-align:left; color:#36516f; background:#f9fbff; font-weight:700; position:sticky; top:0; }");
            sb.AppendLine("    tr:hover td { background:#f7fbff; }");
            sb.AppendLine("    .num { text-align:right; font-variant-numeric:tabular-nums; }");
            sb.AppendLine("    .pill { display:inline-block; border-radius:999px; padding:3px 9px; font-size:11px; font-weight:600; border:1px solid transparent; }");
            sb.AppendLine("    .pill.good { background:#e8f8ef; color:#13633a; border-color:#bce9cd; }");
            sb.AppendLine("    .pill.warn { background:#fff3e5; color:#8a4f00; border-color:#f4d4ad; }");
            sb.AppendLine("    .pill.bad { background:#fdecec; color:#8f2424; border-color:#f4c1c1; }");
            sb.AppendLine("    .util-track { width:130px; height:8px; background:#e9eef6; border-radius:999px; overflow:hidden; }");
            sb.AppendLine("    .util-fill { height:100%; }");
            sb.AppendLine("    .grid2 { display:grid; grid-template-columns:1fr 1fr; gap:16px; margin-top:16px; }");
            sb.AppendLine("    @media (max-width:960px) { .grid2 { grid-template-columns:1fr; } .hero .meta { text-align:left; } }");
            sb.AppendLine("    .bar-track { height:10px; border-radius:999px; background:#ebf0f7; overflow:hidden; }");
            sb.AppendLine("    .bar-fill { height:100%; background:linear-gradient(90deg,#1d7ed6 0%,#4ba3ee 100%); }");
            sb.AppendLine("    .footer { margin-top:14px; font-size:12px; color:var(--muted); text-align:right; }");
            sb.AppendLine("  </style>");
        }

        private void SendSummary_Click(object sender, RoutedEventArgs e)
        {
            static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

            var monthly = months.Select(month =>
            {
                var bills = AnnualData[month];
                var income = MonthlyIncome.GetValueOrDefault(month, 0m);
                var expenses = bills.Sum(b => b.Amount);
                var net = income - expenses;
                var paidCount = bills.Count(b => b.IsPaid);
                var unpaidCount = bills.Count - paidCount;
                var overdueCount = bills.Count(b => b.IsPastDue);
                var recurringCount = bills.Count(b => b.IsRecurring);
                var utilization = income > 0m ? (double)(expenses / income) * 100d : (expenses > 0m ? 100d : 0d);

                return new
                {
                    Month = month,
                    Income = income,
                    Expenses = expenses,
                    Net = net,
                    BillCount = bills.Count,
                    PaidCount = paidCount,
                    UnpaidCount = unpaidCount,
                    OverdueCount = overdueCount,
                    RecurringCount = recurringCount,
                    Utilization = Math.Max(0d, utilization)
                };
            }).ToList();

            var annualIncome = monthly.Sum(m => m.Income);
            var annualExpenses = monthly.Sum(m => m.Expenses);
            var annualNet = annualIncome - annualExpenses;
            var totalBills = monthly.Sum(m => m.BillCount);
            var totalPaid = monthly.Sum(m => m.PaidCount);
            var totalUnpaid = monthly.Sum(m => m.UnpaidCount);
            var totalOverdue = monthly.Sum(m => m.OverdueCount);
            var totalRecurring = monthly.Sum(m => m.RecurringCount);
            var paidRate = totalBills > 0 ? (double)totalPaid / totalBills * 100d : 0d;
            var averageMonthlyNet = monthly.Count > 0 ? monthly.Average(m => m.Net) : 0m;
            var strongest = monthly.OrderByDescending(m => m.Net).First();
            var weakest = monthly.OrderBy(m => m.Net).First();

            var categoryTotals = AnnualData.Values
                .SelectMany(bills => bills)
                .GroupBy(b => string.IsNullOrWhiteSpace(b.Category) ? "General" : b.Category)
                .Select(g => new { Category = g.Key, Amount = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Amount)
                .Take(8)
                .ToList();

            var topCategoryAmount = categoryTotals.FirstOrDefault()?.Amount ?? 0m;

            string netToneClass = annualNet >= 0m ? "tone-good" : "tone-bad";
            string generatedAt = DateTime.Now.ToString("f");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='en'>");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset='utf-8' />");
            sb.AppendLine("  <meta name='viewport' content='width=device-width, initial-scale=1' />");
            sb.AppendLine($"  <title>Crispy_Bills Financial Summary {Html(CurrentYear)}</title>");
            AppendSummaryCss(sb);
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <div class='wrap'>");
            sb.AppendLine("    <section class='hero'>");
            sb.AppendLine("      <div>");
            sb.AppendLine($"        <h1>Crispy_Bills - Financial Summary {Html(CurrentYear)}</h1>");
            sb.AppendLine("        <p>A complete month-by-month operational view with payment health, utilization, and category concentration.</p>");
            sb.AppendLine("      </div>");
            sb.AppendLine("      <div class='meta'>");
            sb.AppendLine($"        <div><strong>Generated:</strong> {Html(generatedAt)}</div>");
            sb.AppendLine($"        <div><strong>Best Month:</strong> {Html(strongest.Month)} ({strongest.Net:C})</div>");
            sb.AppendLine($"        <div><strong>Most Challenging Month:</strong> {Html(weakest.Month)} ({weakest.Net:C})</div>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </section>");

            sb.AppendLine("    <section class='cards'>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Annual Income</div><div class='value'>{annualIncome:C}</div><div class='sub'>Across {months.Length} months</div></article>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Annual Expenses</div><div class='value'>{annualExpenses:C}</div><div class='sub'>{totalBills} bills tracked</div></article>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Annual Net</div><div class='value {netToneClass}'>{annualNet:C}</div><div class='sub'>Average monthly net {averageMonthlyNet:C}</div></article>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Payment Health</div><div class='value'>{paidRate:0.#}%</div><div class='sub'>{totalPaid} paid, {totalUnpaid} unpaid, {totalOverdue} overdue</div></article>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Recurring Footprint</div><div class='value'>{totalRecurring}</div><div class='sub'>Recurring bill instances in year</div></article>");
            sb.AppendLine($"      <article class='card'><div class='kicker'>Top Category Weight</div><div class='value'>{(annualExpenses > 0m ? (topCategoryAmount / annualExpenses * 100m) : 0m):0.#}%</div><div class='sub'>{Html(categoryTotals.FirstOrDefault()?.Category ?? "N/A")}</div></article>");
            sb.AppendLine("    </section>");

            sb.AppendLine("    <section class='panel'>");
            sb.AppendLine("      <h2>Monthly Operating Detail</h2>");
            sb.AppendLine("      <table>");
            sb.AppendLine("        <thead><tr><th>Month</th><th class='num'>Income</th><th class='num'>Expenses</th><th class='num'>Net</th><th>Status</th><th>Payment Mix</th><th>Utilization</th></tr></thead>");
            sb.AppendLine("        <tbody>");

            foreach (var m in monthly)
            {
                string statusClass = m.Net >= 0m ? "good" : (m.Net > -250m ? "warn" : "bad");
                string statusText = m.Net >= 0m ? "Healthy" : (m.Net > -250m ? "Watch" : "Deficit");
                string utilColor = m.Utilization < 80d ? "#1f9d57" : (m.Utilization < 100d ? "#cf7a00" : "#c73333");
                double utilWidth = Math.Min(m.Utilization, 180d);

                sb.AppendLine("          <tr>");
                sb.AppendLine($"            <td><strong>{Html(m.Month)}</strong><div class='sub'>{m.BillCount} bill(s), {m.RecurringCount} recurring</div></td>");
                sb.AppendLine($"            <td class='num'>{m.Income:C}</td>");
                sb.AppendLine($"            <td class='num'>{m.Expenses:C}</td>");
                sb.AppendLine($"            <td class='num {(m.Net >= 0m ? "tone-good" : "tone-bad")}'><strong>{m.Net:C}</strong></td>");
                sb.AppendLine($"            <td><span class='pill {statusClass}'>{statusText}</span></td>");
                sb.AppendLine($"            <td>{m.PaidCount} paid / {m.UnpaidCount} unpaid / {m.OverdueCount} overdue</td>");
                sb.AppendLine("            <td>");
                sb.AppendLine($"              <div class='util-track'><div class='util-fill' style='width:{utilWidth:0.#}%; background:{utilColor};'></div></div>");
                sb.AppendLine($"              <div class='sub'>{m.Utilization:0.#}% of income consumed</div>");
                sb.AppendLine("            </td>");
                sb.AppendLine("          </tr>");
            }

            sb.AppendLine("        </tbody>");
            sb.AppendLine("      </table>");
            sb.AppendLine("    </section>");

            sb.AppendLine("    <section class='grid2'>");
            sb.AppendLine("      <section class='panel'>");
            sb.AppendLine("        <h2>Category Spend Concentration</h2>");
            sb.AppendLine("        <table>");
            sb.AppendLine("          <thead><tr><th>Category</th><th class='num'>Amount</th><th>Share</th></tr></thead>");
            sb.AppendLine("          <tbody>");

            if (categoryTotals.Count == 0)
            {
                sb.AppendLine("            <tr><td colspan='3'>No bill categories recorded for this year.</td></tr>");
            }
            else
            {
                foreach (var cat in categoryTotals)
                {
                    decimal share = annualExpenses > 0m ? cat.Amount / annualExpenses * 100m : 0m;
                    sb.AppendLine("            <tr>");
                    sb.AppendLine($"              <td>{Html(cat.Category)}</td>");
                    sb.AppendLine($"              <td class='num'>{cat.Amount:C}</td>");
                    sb.AppendLine("              <td>");
                    sb.AppendLine($"                <div class='bar-track'><div class='bar-fill' style='width:{Math.Min((double)share, 100d):0.#}%'></div></div>");
                    sb.AppendLine($"                <div class='sub'>{share:0.#}% of annual expenses</div>");
                    sb.AppendLine("              </td>");
                    sb.AppendLine("            </tr>");
                }
            }

            sb.AppendLine("          </tbody>");
            sb.AppendLine("        </table>");
            sb.AppendLine("      </section>");

            sb.AppendLine("      <section class='panel'>");
            sb.AppendLine("        <h2>Executive Readout</h2>");
            sb.AppendLine("        <div style='padding: 14px 16px;'>");
            sb.AppendLine($"          <p style='margin-top:0;'>The year closed with an <strong class='{netToneClass}'>{(annualNet >= 0m ? "operating surplus" : "operating deficit")}</strong> of <strong>{annualNet:C}</strong>.</p>");
            sb.AppendLine($"          <p><strong>Collections:</strong> {paidRate:0.#}% paid-rate across {totalBills} tracked bills. Overdue volume is currently <strong class='{(totalOverdue > 0 ? "tone-bad" : "tone-good")}'>{totalOverdue}</strong>.</p>");
            sb.AppendLine($"          <p><strong>Volatility:</strong> Monthly net ranged from <strong>{weakest.Net:C}</strong> ({Html(weakest.Month)}) to <strong>{strongest.Net:C}</strong> ({Html(strongest.Month)}).</p>");
            sb.AppendLine($"          <p style='margin-bottom:0;'><strong>Focus area:</strong> {Html(categoryTotals.FirstOrDefault()?.Category ?? "No dominant category")}{(categoryTotals.Count > 0 ? $" represents {(annualExpenses > 0m ? (categoryTotals[0].Amount / annualExpenses * 100m) : 0m):0.#}% of spend." : ".")}</p>");
            sb.AppendLine("        </div>");
            sb.AppendLine("      </section>");
            sb.AppendLine("    </section>");

            sb.AppendLine("    <div class='footer'>Generated by Crispy_Bills on " + Html(generatedAt) + "</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            var tempPath = Path.Combine(Path.GetTempPath(), $"CrispyBills_Summary_{CurrentYear}.html");
            
            try
            {
                File.WriteAllText(tempPath, sb.ToString());
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open summary. The file may be locked.\nError: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FixDueDatesCurrentYear_Click(object sender, RoutedEventArgs e)
        {
            int normalizedCount = NormalizeDueDatesForCurrentYear();
            if (normalizedCount > 0)
            {
                UpdateDashboard();
                AutoSave();
                MessageBox.Show(this,
                    $"Normalized {normalizedCount} due date(s) for {CurrentYear}.\n\nUnpaid past-due carryovers were preserved.",
                    "Due Dates Fixed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    $"No due date changes were needed for {CurrentYear}.",
                    "Due Dates Fixed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow { Owner = this };
            w.ShowDialog();
        }

        private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var w = new DiagnosticsWindow(dataRoot, backupsRoot) { Owner = this };
            w.ShowDialog();
        }

        private void OpenHelp_Click(object sender, RoutedEventArgs e)
        {
            new HelpWindow { Owner = this }.ShowDialog();
        }

        private void EnterMovesDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;

            _enterMovesDown = menuItem.IsChecked;
            SetStatus(_enterMovesDown
                ? "Enter now commits and moves down one row in the same column."
                : "Enter now commits and stays on the current row.");
        }

        private void GridEditChecklist_Click(object sender, RoutedEventArgs e)
        {
            var checklist =
                "Grid edit checklist\n" +
                "1. Name/Amount: Enter applies.\n" +
                "2. Name/Amount: Tab applies and moves to next cell.\n" +
                "3. End of row + Tab: wraps to next row first editable cell.\n" +
                "4. Shift+Tab: moves backward and wraps to previous row.\n" +
                "5. Click outside active cell: edit is discarded.\n" +
                "6. Due Date: accepts M/d/yy and M/d/yyyy, stores MM/dd/yyyy.\n" +
                "7. Invalid date/amount shows red editor border and status message.\n" +
                "8. Category blank reverts to previous value.";

            try
            {
                Clipboard.SetText(checklist);
                MessageBox.Show(this,
                    checklist + "\n\nChecklist copied to clipboard.",
                    "Grid Edit Checklist",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch
            {
                MessageBox.Show(this,
                    checklist,
                    "Grid Edit Checklist",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                RefreshData_Click(sender, e);
                e.Handled = true;
            }
        }

        /// <summary>Reloads the active year from SQLite and reapplies filters/charts (same outcome as mobile pull-to-refresh).</summary>
        private void RefreshData_Click(object sender, RoutedEventArgs e)
        {
            LoadData();
            ApplyFilters();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            SaveGlobalNotes();
            Application.Current.Shutdown();
        }
        #endregion

        #region Command bridge handlers for InputBindings
        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Save_Click(this, e);
        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Undo_Click(sender, e);
        private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Redo_Click(sender, e);

        private void UndoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _undoStack.Count > 0;
        private void RedoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _redoStack.Count > 0;
        #endregion

        private void SetFontSize_Small(object sender, RoutedEventArgs e)
        {
            Application.Current.Resources["BaseFontSize"] = 14.0;
            Application.Current.Resources["DataGridRowMinHeight"] = 24.0;
            Application.Current.Resources["DataGridHeaderHeight"] = 28.0;
        }

        private void SetFontSize_Medium(object sender, RoutedEventArgs e)
        {
            Application.Current.Resources["BaseFontSize"] = 18.0;
            Application.Current.Resources["DataGridRowMinHeight"] = 30.0;
            Application.Current.Resources["DataGridHeaderHeight"] = 34.0;
        }

        private void SetFontSize_Large(object sender, RoutedEventArgs e)
        {
            Application.Current.Resources["BaseFontSize"] = 22.0;
            Application.Current.Resources["DataGridRowMinHeight"] = 36.0;
            Application.Current.Resources["DataGridHeaderHeight"] = 40.0;
        }

        [GeneratedRegex(@"^\s*(\d{1,2})\/(\d{1,2})\/(\d{2}|\d{4})\s*$")]
        private static partial Regex GeneratedDueDatePattern();
    }

}