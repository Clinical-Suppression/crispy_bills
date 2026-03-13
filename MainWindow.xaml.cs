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
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Diagnostics;

namespace CrispyBills
{
    public partial class MainWindow : Window
    {
        private readonly string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CrispyBills");

        // Database base filename uses per-year files: CrispyBills_{year}.db
        private string GetDbPath(string year) =>
            Path.Combine(dataRoot, $"CrispyBills_{year}.db");

        // Backups base folder
        private readonly string backupsRoot;

        public Dictionary<string, ObservableCollection<Bill>> AnnualData { get; set; } = new();
        public Dictionary<string, decimal> MonthlyIncome { get; set; } = new();

        private readonly string[] months =
            { "January", "February", "March", "April", "May", "June",
              "July", "August", "September", "October", "November", "December" };

        private bool isDarkMode = false;

        // Undo / Redo stacks
        private readonly Stack<UndoRedoAction> _undoStack = new();
        private readonly Stack<UndoRedoAction> _redoStack = new();

        // current loaded year (string)
        private string _currentYear = DateTime.Now.Year.ToString();
        private string CurrentYear => _currentYear;

        // Pie chart drill-down state
        private string? _detailedCategory = null;

        public MainWindow()
        {
            InitializeComponent();

            backupsRoot = Path.Combine(dataRoot, "db_backups");

            EnsureDataRoot();
            MigrateLegacyDatabases();

            foreach (var m in months)
            {
                AnnualData[m] = new ObservableCollection<Bill>();
                MonthlyIncome[m] = 0;
            }

            // Ensure current-year DB exists and is initialized
            InitializeDatabase(GetDbPath(CurrentYear));

            // Populate month selector and set selection
            MonthSelector.ItemsSource = months.ToList();
            MonthSelector.SelectedIndex = DateTime.Now.Month - 1;

            // Ensure available years folder exists (backwards compatibility)
            EnsureAvailableYears();
            UpdateYearSelector();

            LoadData();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateDashboard();
        }

        #region Undo/Redo helpers
        private record UndoRedoAction(Action Undo, Action Redo);

        private void PushUndo(Action undo, Action redo)
        {
            _undoStack.Push(new UndoRedoAction(undo, redo));
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
                    try { File.Delete(src); } catch { }
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
                            try { File.Delete(srcBackup); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal: app can continue with fresh files in new location.
            }
        }

        private void EnsureAvailableYears()
        {
            // Ensure at minimum current year DB exists
            if (!File.Exists(GetDbPath(CurrentYear)))
                InitializeDatabase(GetDbPath(CurrentYear));
        }

        private void InitializeDatabase(string dbFilePath)
        {
            var dbDir = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
            using var conn = new SqliteConnection($"Data Source={dbFilePath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS Bills (Id TEXT, Month TEXT, Year TEXT, Name TEXT, Amount REAL, DueDate TEXT, Paid INT, Category TEXT);
                CREATE TABLE IF NOT EXISTS Income (Month TEXT, Year TEXT, Amount REAL, PRIMARY KEY(Month, Year));";
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

            // always include current year
            if (int.TryParse(_currentYear, out int cy)) years.Add(cy);

            // return descending (newest first)
            return years.Reverse().Select(y => y.ToString()).ToList();
        }

        private void UpdateYearSelector()
        {
            var years = GetAvailableYears();
            YearSelector.ItemsSource = years;
            if (years.Contains(CurrentYear)) YearSelector.SelectedItem = CurrentYear;
        }

        private void LoadData()
        {
            // Clear structures
            foreach (var m in months)
            {
                AnnualData[m].Clear();
                MonthlyIncome[m] = 0;
            }

            var dbFile = GetDbPath(CurrentYear);
            // Always initialize to ensure correct journal mode (WAL) and schema
            InitializeDatabase(dbFile);
            using var conn = new SqliteConnection($"Data Source={dbFile}");
            conn.Open();

            // Bills
            var bCmd = conn.CreateCommand();
            bCmd.CommandText = "SELECT * FROM Bills WHERE Year = $y";
            bCmd.Parameters.AddWithValue("$y", CurrentYear);
            using (var r = bCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string mKey = r.GetString(1);
                    if (AnnualData.ContainsKey(mKey))
                        AnnualData[mKey].Add(new Bill
                        {
                            Id = Guid.Parse(r.GetString(0)),
                            Name = r.GetString(3),
                            Amount = r.GetDecimal(4),
                            DueDate = DateTime.ParseExact(r.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                            IsPaid = r.GetInt32(6) == 1,
                            Category = r.GetString(7)
                        });
                }
            }

            // Income
            var iCmd = conn.CreateCommand();
            iCmd.CommandText = "SELECT Month, Amount FROM Income WHERE Year = $y";
            iCmd.Parameters.AddWithValue("$y", CurrentYear);
            using (var r = iCmd.ExecuteReader())
            {
                while (r.Read()) MonthlyIncome[r.GetString(0)] = r.GetDecimal(1);
            }

            UpdateDashboard();
            _undoStack.Clear();
            _redoStack.Clear();
            CommandManager.InvalidateRequerySuggested();
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
                    try { backups[i].Delete(); } catch { }
                }
            }
            catch { /* non-fatal */ }
        }

        #endregion

        #region UI updates
        private void UpdateDashboard()
        {
            var selected = MonthSelector?.SelectedItem;
            if (!IsLoaded || selected == null) return;
            string cur = (string)selected;

            decimal inc = MonthlyIncome.ContainsKey(cur) ? MonthlyIncome[cur] : 0;
            var bills = AnnualData[cur];
            decimal exp = bills.Sum(b => b.Amount);
            decimal unpaid = bills.Where(b => !b.IsPaid).Sum(b => b.Amount);
            decimal paid = exp - unpaid;

            IncomeBox.Text = inc.ToString("F2");
            ExpenseBar.Width = inc > 0
                ? Math.Min(160 * (double)(exp / inc), 160)
                : 0;

            // Footer summary
            try { StatusText.Text = $"{CurrentYear} • {cur} • Bills: {bills.Count} • Remaining: {unpaid:C} • Net: {(inc - exp):C}"; } catch { }

            UpdateCategoryFilter();
            DrawPieChart(bills, inc - exp);
            ApplyFilters();

            // Show/hide back button based on drill-down state
            BackButton.Visibility = !string.IsNullOrEmpty(_detailedCategory) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCategoryFilter()
        {
            var currentSelection = CategoryFilter.SelectedItem as string;

            var categories = AnnualData.Values.SelectMany(bills => bills.Select(b => b.Category))
                                              .Distinct()
                                              .OrderBy(c => c)
                                              .ToList();
            categories.Insert(0, "All Categories");

            CategoryFilter.ItemsSource = categories;

            if (currentSelection != null && categories.Contains(currentSelection))
                CategoryFilter.SelectedItem = currentSelection;
            else
                CategoryFilter.SelectedIndex = 0;
        }

        private void DrawPieChart(IEnumerable<Bill> bills, decimal remaining)
        {
            PieChartCanvas.Children.Clear();
            var totalBillsAmount = bills.Sum(b => b.Amount);

            var colors = new List<Brush>
            {
                Brushes.MediumSeaGreen, Brushes.LightCoral, Brushes.RoyalBlue,
                Brushes.Goldenrod, Brushes.MediumOrchid, Brushes.DarkCyan,
                Brushes.Tomato, Brushes.LimeGreen, Brushes.SteelBlue
            };

            if (string.IsNullOrEmpty(_detailedCategory))
            {
                // --------------- CATEGORY VIEW (default) ---------------
                var categories = bills.GroupBy(b => b.Category)
                                      .Select(g => new {
                                          Name = g.Key,
                                          Total = g.Sum(x => x.Amount)
                                      })
                                      .OrderByDescending(x => x.Total)
                                      .ToList();

                decimal totalPlusRemaining = totalBillsAmount + Math.Max(0, remaining);
                if (totalPlusRemaining <= 0) return;

                double currentAngle = 0;
                for (int i = 0; i < categories.Count; i++)
                {
                    var cat = categories[i];
                    double sweep = NormalizeSweepAngle((double)(cat.Total / totalPlusRemaining) * 360);
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        colors[i % colors.Count], cat.Name));
                    AddPieLabel(currentAngle, sweep, $"{cat.Name}: {cat.Total:C}");
                    currentAngle += sweep;
                }

                if (remaining > 0)
                {
                    double sweep = NormalizeSweepAngle((double)(remaining / totalPlusRemaining) * 360);
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        Brushes.LightGray, "Remaining"));
                    AddPieLabel(currentAngle, sweep, $"Remaining: {remaining:C}");
                }
            }
            else
            {
                // --------------- DETAILED/BILL VIEW ---------------
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
                    PieChartCanvas.Children.Add(CreateSlice(currentAngle, sweep,
                        colors[i % colors.Count], bill.Id.ToString()));
                    AddPieLabel(currentAngle, sweep, $"{bill.Name}: {bill.Amount:C}");
                    currentAngle += sweep;
                }
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
            const double labelRadius = 118;

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
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryFindResource("WindowForegroundBrush") as Brush ?? Brushes.Black,
                Background = TryFindResource("PanelBackgroundBrush") as Brush ?? Brushes.White,
                Padding = new Thickness(4, 1, 4, 1),
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

        private System.Windows.Shapes.Path CreateSlice(double startAngle, double sweepAngle, Brush fill, object tag)
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
            var slice = new System.Windows.Shapes.Path { Fill = fill, Data = geometry, Tag = tag, Cursor = Cursors.Hand };
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
                // In detailed view, clicking does nothing further, but this could be extended
            }
        }
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _detailedCategory = null; // Go back to category view
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
            if (int.TryParse(_currentYear, out int cur))
            {
                // Carry forward December bills using year-start rules:
                // - recurring bills always carry
                // - non-recurring bills carry only if unpaid
                var templates = AnnualData["December"]
                    .Where(t =>
                    {
                        bool isRecurring = months.Take(11).Any(m => AnnualData[m].Any(b => b.Id == t.Id));
                        return isRecurring || !t.IsPaid;
                    })
                    .ToList();

                _currentYear = (cur + 1).ToString();
                LoadData();

                // If the new year is empty (checked via January), populate it with templates
                if (templates.Count > 0 && AnnualData["January"].Count == 0)
                {
                    // Generate a shared ID for each template to maintain the recurring link across the new year
                    var templateIds = templates.Select(t => new { Template = t, NewId = Guid.NewGuid() }).ToList();

                    int newYearVal = cur + 1;
                    for (int i = 0; i < months.Length; i++)
                    {
                        int daysInMonth = DateTime.DaysInMonth(newYearVal, i + 1);
                        foreach (var item in templateIds)
                        {
                            var t = item.Template;
                            // Create new bill copy, handling month-end days (e.g. Feb 28)
                            int day = Math.Min(t.DueDate.Day, daysInMonth);
                            AnnualData[months[i]].Add(new Bill
                            {
                                Id = item.NewId,
                                Name = t.Name,
                                Amount = t.Amount,
                                Category = t.Category,
                                DueDate = new DateTime(newYearVal, i + 1, day),
                                IsPaid = false
                            });
                        }
                    }
                    MessageBox.Show($"Welcome to {newYearVal}! Eligible bills from December {cur} have been copied to the new year.");
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

            // Apply income to selected month and all future months.
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

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkMode = !isDarkMode;
            var dictUri = new Uri(isDarkMode ? "Dark.xaml" : "Light.xaml", UriKind.Relative);

            // Replace only existing theme dictionaries (Light/Dark) so we don't clear unrelated merged dictionaries
            var toRemove = Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source != null &&
                            (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                             d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var d in toRemove) Application.Current.Resources.MergedDictionaries.Remove(d);

            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = dictUri });

            UpdateDashboard();
            try { StatusText.Text = isDarkMode ? "Dark theme activated" : "Light theme activated"; } catch { }
        }

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

            foreach (var b in unpaidBills)
            {
                var copy = new Bill
                {
                    Id = Guid.NewGuid(),
                    Name = b.Name,
                    Amount = b.Amount,
                    Category = b.Category,
                    DueDate = b.DueDate.AddMonths(1),
                    IsPaid = false
                };
                AnnualData[nextMonth].Add(copy);
                copies.Add(copy);
                count++;
            }

            MessageBox.Show($"Rolled over {count} unpaid bills to {nextMonth}.");

            PushUndo(() =>
            {
                foreach (var c in copies) AnnualData[nextMonth].Remove(c);
                UpdateDashboard();
            },
            () =>
            {
                foreach (var c in copies) AnnualData[nextMonth].Add(c);
                UpdateDashboard();
            });

            UpdateDashboard();
            AutoSave();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            PersistData(createBackup: true, showSuccessMessage: sender is Button);
        }

        private void PersistData(bool createBackup, bool showSuccessMessage)
        {
            var dbFile = GetDbPath(CurrentYear);
            var dbDir = Path.GetDirectoryName(dbFile);
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            // Scope the connection to ensure it closes (and checkpoints WAL) before backup
            using (var conn = new SqliteConnection($"Data Source={dbFile}"))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();

                // Delete old data
                var delB = conn.CreateCommand();
                delB.Transaction = tx;
                delB.CommandText = "DELETE FROM Bills WHERE Year = $y";
                delB.Parameters.AddWithValue("$y", CurrentYear);
                delB.ExecuteNonQuery();

                var delI = conn.CreateCommand();
                delI.Transaction = tx;
                delI.CommandText = "DELETE FROM Income WHERE Year = $y";
                delI.Parameters.AddWithValue("$y", CurrentYear);
                delI.ExecuteNonQuery();

                // Insert new data
                foreach (var m in months)
                {
                    foreach (var b in AnnualData[m])
                    {
                        var ins = conn.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText =
                            "INSERT INTO Bills VALUES ($id, $m, $y, $n, $a, $d, $p, $c)";
                        ins.Parameters.AddWithValue("$id", b.Id.ToString());
                        ins.Parameters.AddWithValue("$m", m);
                        ins.Parameters.AddWithValue("$y", CurrentYear);
                        ins.Parameters.AddWithValue("$n", b.Name);
                        ins.Parameters.AddWithValue("$a", b.Amount);
                        ins.Parameters.AddWithValue("$d",
                            b.DueDate.ToString("yyyy-MM-dd"));
                        ins.Parameters.AddWithValue("$p", b.IsPaid ? 1 : 0);
                        ins.Parameters.AddWithValue("$c", b.Category);
                        ins.ExecuteNonQuery();
                    }

                    var insI = conn.CreateCommand();
                    insI.Transaction = tx;
                    insI.CommandText = "INSERT INTO Income VALUES ($m, $y, $a)";
                    insI.Parameters.AddWithValue("$m", m);
                    insI.Parameters.AddWithValue("$y", CurrentYear);
                    insI.Parameters.AddWithValue("$a", MonthlyIncome.GetValueOrDefault(m, 0));
                    insI.ExecuteNonQuery();
                }

                tx.Commit();
            }

            if (createBackup)
            {
                BackupDatabase(CurrentYear);
            }

            if (showSuccessMessage)
            {
                MessageBox.Show(createBackup ? "Data saved and backed up!" : "Data saved!");
            }

            try { StatusText.Text = $"Auto-saved {CurrentYear} at {DateTime.Now:hh:mm:ss tt}"; } catch { }
        }

        private void AutoSave()
        {
            try
            {
                PersistData(createBackup: false, showSuccessMessage: false);
            }
            catch
            {
                try { StatusText.Text = "Auto-save failed. Use File > Save to retry."; } catch { }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
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
            if (BillsGrid?.SelectedItem == null) return;

            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (ReferenceEquals(source, BillsGrid)) return;
                source = VisualTreeHelper.GetParent(source);
            }

            ClearBillSelection();
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            ClearBillSelection();
        }

        private void ClearBillSelection()
        {
            if (BillsGrid == null || BillsGrid.SelectedItem == null) return;

            BillsGrid.UnselectAll();
            BillsGrid.SelectedItem = null;
            System.Windows.Data.CollectionViewSource.GetDefaultView(BillsGrid.ItemsSource)?.Refresh();
            UpdateDashboard();
        }

        private void MonthSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BillsGrid != null && MonthSelector.SelectedIndex >= 0)
            {
                BillsGrid.ItemsSource = AnnualData[months[MonthSelector.SelectedIndex]];
                _detailedCategory = null; // Reset drill-down when month changes
                UpdateDashboard();
            }
        }

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (BillsGrid == null || MonthSelector.SelectedIndex < 0) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(
                AnnualData[months[MonthSelector.SelectedIndex]]);
            if (view == null) return;

            string search = SearchBox.Text.ToLower();
            string? selectedCategory = CategoryFilter.SelectedItem as string;

            view.Filter = item =>
            {
                if (item is not Bill b) return false;

                bool categoryMatch = string.IsNullOrEmpty(selectedCategory) || selectedCategory == "All Categories" || b.Category == selectedCategory;
                bool searchMatch = string.IsNullOrEmpty(search) || b.Name.ToLower().Contains(search) || b.Category.ToLower().Contains(search);

                return categoryMatch && searchMatch;
            };
        }

        private void OpenAddDialog_Click(object sender, RoutedEventArgs e)
        {
            if (MonthSelector.SelectedIndex < 0)
            {
                MessageBox.Show("Please select a month before adding a bill.", "No Month Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var diag = new BillDialog { Owner = this };
            if (diag.ShowDialog() == true && diag.ResultBill != null)
            {
                // Add the bill to the selected month
                string curMonth = months[MonthSelector.SelectedIndex];
                var baseBill = diag.ResultBill;
                AnnualData[curMonth].Add(baseBill);
                UpdateDashboard();

                bool recurring = diag.IsRecurring && MonthSelector.SelectedIndex < 11;

                var addedCopies = new List<(string month, Bill bill)>();
                if (recurring)
                {
                    int startIdx = MonthSelector.SelectedIndex + 1;
                    for (int i = startIdx; i < months.Length; i++)
                    {
                        var monthKey = months[i];
                        var copy = new Bill
                        {
                            Id = baseBill.Id,
                            Name = baseBill.Name,
                            Amount = baseBill.Amount,
                            Category = baseBill.Category,
                            DueDate = baseBill.DueDate.AddMonths(i - MonthSelector.SelectedIndex),
                            IsPaid = false
                        };
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
                            AnnualData[m].Remove(c);
                    }
                    UpdateDashboard();
                },
                () =>
                {
                    AnnualData[curMonth].Add(baseBill);
                    if (addedCopies.Count > 0)
                    {
                        foreach (var (m, c) in addedCopies)
                            AnnualData[m].Add(c);
                    }
                    UpdateDashboard();
                });

                AutoSave();
            }
        }

        private void BillsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BillsGrid.SelectedItem is Bill b)
            {
                // Verify the click was on a row (not the header)
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

            // Check if this is a recurring bill to provide a more informative prompt
            bool isRecurring = false;
            for (int i = selectedMonthIndex + 1; i < months.Length; i++)
            {
                if (AnnualData[months[i]].Any(futureBill => futureBill.Id == b.Id))
                {
                    isRecurring = true;
                    break;
                }
            }

            string message = $"Are you sure you want to delete '{b.Name}'?";
            if (isRecurring)
                message += "\n\nThis is a recurring bill and will be deleted from all subsequent months this year.";

            if (MessageBox.Show(this, message, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var removed = new List<(string month, Bill bill)>();
                for (int i = selectedMonthIndex; i < months.Length; i++)
                {
                    string mKey = months[i];
                    var toRemove = AnnualData[mKey].Where(x => x.Id == b.Id).ToList();
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
                    for (int i = selectedMonthIndex; i < months.Length; i++)
                    {
                        string mKey = months[i];
                        var toRemove = AnnualData[mKey].Where(x => x.Id == b.Id).ToList();
                        foreach (var r in toRemove) AnnualData[mKey].Remove(r);
                    }
                    UpdateDashboard();
                });

                AutoSave();
            }
        }

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

            var diag = new BillDialog(b) { Owner = this };
            if (diag.ShowDialog() == true && diag.ResultBill != null)
            {
                if (MessageBox.Show("Are you sure you want to apply these changes?", "Confirm Edit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                    // Capture old values for undo
                    var old = new Bill
                    {
                        Id = b.Id,
                        Name = b.Name,
                        Amount = b.Amount,
                        Category = b.Category,
                        DueDate = b.DueDate,
                        IsPaid = b.IsPaid
                    };

                    // Apply changes
                    b.Name = diag.ResultBill.Name;
                    b.Amount = diag.ResultBill.Amount;
                    b.Category = diag.ResultBill.Category;
                    b.DueDate = diag.ResultBill.DueDate;
                    b.IsPaid = diag.ResultBill.IsPaid;
                    UpdateDashboard();

                    // If user set recurring during edit, propagate to future months
                    var addedCopies = new List<(string month, Bill bill)>();
                    var modifiedCopies = new List<(string month, Bill originalState, Bill currentObj)>();

                    if (diag.IsRecurring)
                    {
                        int selectedIdx = MonthSelector.SelectedIndex;
                        for (int i = selectedIdx + 1; i < months.Length; i++)
                        {
                            var monthKey = months[i];
                            var existing = AnnualData[monthKey].FirstOrDefault(x => x.Id == b.Id);

                            if (existing != null)
                            {
                                // Capture state of existing future bill before modification
                                modifiedCopies.Add((monthKey, new Bill
                                {
                                    Id = existing.Id, Name = existing.Name, Amount = existing.Amount,
                                    Category = existing.Category, DueDate = existing.DueDate, IsPaid = existing.IsPaid
                                }, existing));

                                // Update existing bill
                                existing.Name = b.Name;
                                existing.Amount = b.Amount;
                                existing.Category = b.Category;
                                // Update day of month, handling shorter months
                                int daysInMonth = DateTime.DaysInMonth(existing.DueDate.Year, existing.DueDate.Month);
                                int newDay = Math.Min(b.DueDate.Day, daysInMonth);
                                existing.DueDate = new DateTime(existing.DueDate.Year, existing.DueDate.Month, newDay);
                            }
                            else
                            {
                                // Add new copy if missing
                                var copy = new Bill
                                {
                                    Id = b.Id,
                                    Name = b.Name,
                                    Amount = b.Amount,
                                    Category = b.Category,
                                    DueDate = b.DueDate.AddMonths(i - selectedIdx),
                                    IsPaid = false
                                };
                                AnnualData[monthKey].Add(copy);
                                addedCopies.Add((monthKey, copy));
                            }
                        }
                        if (addedCopies.Count > 0 || modifiedCopies.Count > 0) UpdateDashboard();
                    }

                PushUndo(() =>
                {
                    // Undo: restore old values, remove added copies, revert modified copies
                    b.Name = old.Name;
                    b.Amount = old.Amount;
                    b.Category = old.Category;
                    b.DueDate = old.DueDate;
                    b.IsPaid = old.IsPaid;
                    foreach (var (m, c) in addedCopies) AnnualData[m].Remove(c);
                    foreach (var (m, state, obj) in modifiedCopies)
                    {
                        obj.Name = state.Name;
                        obj.Amount = state.Amount;
                        obj.Category = state.Category;
                        obj.DueDate = state.DueDate;
                        obj.IsPaid = state.IsPaid;
                    }
                    UpdateDashboard();
                },
                () =>
                {
                    // Redo: reapply edited values, re-add copies, re-apply modifications
                    b.Name = diag.ResultBill.Name;
                    b.Amount = diag.ResultBill.Amount;
                    b.Category = diag.ResultBill.Category;
                    b.DueDate = diag.ResultBill.DueDate;
                    b.IsPaid = diag.ResultBill.IsPaid;
                    foreach (var (m, c) in addedCopies) AnnualData[m].Add(c);
                    foreach (var (m, state, obj) in modifiedCopies)
                    {
                        obj.Name = diag.ResultBill.Name;
                        obj.Amount = diag.ResultBill.Amount;
                        obj.Category = diag.ResultBill.Category;
                        int daysInMonth = DateTime.DaysInMonth(obj.DueDate.Year, obj.DueDate.Month);
                        int newDay = Math.Min(diag.ResultBill.DueDate.Day, daysInMonth);
                        obj.DueDate = new DateTime(obj.DueDate.Year, obj.DueDate.Month, newDay);
                        obj.IsPaid = state.IsPaid;
                    }
                    UpdateDashboard();
                });

                AutoSave();
            }
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
                FileName = $"CrispyBills_Export_{CurrentYear}.csv"
            };

            if (dlg.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Month,Name,Amount,Category,DueDate,IsPaid");
                foreach (var month in months)
                {
                    foreach (var b in AnnualData[month])
                    {
                        var name = EscapeCsv(b.Name);
                        var category = EscapeCsv(b.Category);
                        sb.AppendLine($"{month},{name},{b.Amount.ToString(CultureInfo.InvariantCulture)},{category}," +
                                      $"{b.DueDate:yyyy-MM-dd},{b.IsPaid}");
                    }
                }
                File.WriteAllText(dlg.FileName, sb.ToString());
                MessageBox.Show($"Successfully exported year {CurrentYear} to {Path.GetFileName(dlg.FileName)}", "Export Complete");
            }
        }

        private void ImportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (MonthSelector.SelectedIndex < 0) return;
            var dlg = new OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(dlg.FileName);
                }
                catch (IOException ex)
                {
                    MessageBox.Show($"Could not read file. It may be open in another program.\nError: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (lines.Length <= 1) return; // no data

                string monthKey = months[MonthSelector.SelectedIndex];

                // Support both formats:
                // 1) Name,Amount,Category,DueDate,IsPaid
                // 2) Month,Name,Amount,Category,DueDate,IsPaid (year export)
                var header = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();
                bool hasMonthColumn = header.Count >= 6 &&
                                      string.Equals(header[0], "Month", StringComparison.OrdinalIgnoreCase);

                bool validLegacyHeader = header.Count >= 5 &&
                    string.Equals(header[0], "Name", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[1], "Amount", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[2], "Category", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[3], "DueDate", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[4], "IsPaid", StringComparison.OrdinalIgnoreCase);

                bool validYearHeader = header.Count >= 6 &&
                    string.Equals(header[0], "Month", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[1], "Name", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[2], "Amount", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[3], "Category", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[4], "DueDate", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header[5], "IsPaid", StringComparison.OrdinalIgnoreCase);

                if (!validLegacyHeader && !validYearHeader)
                {
                    MessageBox.Show("Unsupported CSV format. Expected either Name,Amount,Category,DueDate,IsPaid or Month,Name,Amount,Category,DueDate,IsPaid.",
                                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int nameIndex = hasMonthColumn ? 1 : 0;
                int amountIndex = hasMonthColumn ? 2 : 1;
                int categoryIndex = hasMonthColumn ? 3 : 2;
                int dueDateIndex = hasMonthColumn ? 4 : 3;
                int isPaidIndex = hasMonthColumn ? 5 : 4;
                
                if (AnnualData[monthKey].Count > 0)
                {
                    if (MessageBox.Show($"Importing will overwrite {AnnualData[monthKey].Count} existing bills in {monthKey}. Continue?", "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                }
                AnnualData[monthKey].Clear();

                int importedCount = 0;

                foreach (var line in lines.Skip(1))
                {
                    var parts = ParseCsvLine(line);
                    if (parts.Count <= isPaidIndex) continue;

                    if (hasMonthColumn && !string.Equals(parts[0], monthKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (!decimal.TryParse(parts[amountIndex], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount))
                            continue;
                        if (!DateTime.TryParseExact(parts[dueDateIndex], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                            continue;
                        bool isPaid;
                        if (!bool.TryParse(parts[isPaidIndex], out isPaid))
                        {
                            var paidText = parts[isPaidIndex].Trim();
                            if (paidText == "1") isPaid = true;
                            else if (paidText == "0") isPaid = false;
                            else continue;
                        }

                        var bill = new Bill
                        {
                            Name = parts[nameIndex],
                            Amount = amount,
                            Category = parts[categoryIndex],
                            DueDate = dueDate,
                            IsPaid = isPaid
                        };
                        AnnualData[monthKey].Add(bill);
                        importedCount++;
                    }
                    catch { /* ignore malformed line */ }
                }

                UpdateDashboard();
                AutoSave();
                MessageBox.Show($"Imported {importedCount} bill(s) into {monthKey}.", "Import Complete");
            }
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

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Check for escaped quote
                        if (i < line.Length - 1 && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++; // Skip next quote
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }

        private void SendSummary_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<html><body>");
            sb.AppendLine($"<h1>Monthly Summary - {CurrentYear}</h1>");
            sb.AppendLine("<table border='1' cellpadding='5'><tr><th>Month</th><th>Income</th>" +
                          "<th>Bills Total</th><th>Net</th></tr>");

            foreach (var month in months)
            {
                var inc = MonthlyIncome[month];
                var exp = AnnualData[month].Sum(b => b.Amount);
                sb.AppendLine($"<tr><td>{month}</td><td>{inc:C}</td>" +
                              $"<td>{exp:C}</td><td>{(inc - exp):C}</td></tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<p>Generated by Crispy Bills on " + DateTime.Now.ToString() + "</p>");
            sb.AppendLine("</body></html>");

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

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) Save_Click(this, new RoutedEventArgs());
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) Undo_Click(sender, new RoutedEventArgs());
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) Redo_Click(sender, new RoutedEventArgs());
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
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
        }

        private void SetFontSize_Medium(object sender, RoutedEventArgs e)
        {
            Application.Current.Resources["BaseFontSize"] = 18.0;
        }

        private void SetFontSize_Large(object sender, RoutedEventArgs e)
        {
            Application.Current.Resources["BaseFontSize"] = 22.0;
        }
    }

    public class Bill : INotifyPropertyChanged
    {
        private Guid _id = Guid.NewGuid();
        public Guid Id { get => _id; set { if (_id != value) { _id = value; OnPropertyChanged(); } } }

        private string _name = "";
        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(); } } }

        private decimal _amount;
        public decimal Amount { get => _amount; set { if (_amount != value) { _amount = value; OnPropertyChanged(); } } }

        private string _category = "General";
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(); } } }

        private DateTime _dueDate = DateTime.Now;
        public DateTime DueDate
        {
            get => _dueDate;
            set
            {
                if (_dueDate != value)
                {
                    _dueDate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPastDue));
                }
            }
        }

        private bool _isPaid;
        public bool IsPaid
        {
            get => _isPaid;
            set
            {
                if (_isPaid != value)
                {
                    _isPaid = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPastDue));
                }
            }
        }

        public bool IsPastDue => !IsPaid && DueDate.Date < DateTime.Today;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}