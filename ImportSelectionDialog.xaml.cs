using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CrispyBills
{
    /// <summary>
    /// Dialog allowing the user to select which months/years to import from a structured
    /// CSV package. Presents tri-state year checkboxes and per-month selections.
    /// </summary>
    public partial class ImportSelectionDialog : Window
    {
        private sealed record MonthTag(string Year, string Month);

        private readonly Dictionary<string, CheckBox> _yearCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CheckBox>> _monthCheckBoxesByYear = new(StringComparer.OrdinalIgnoreCase);
        private bool _isUpdatingSelections;
        private readonly bool _notesAvailable;
        private readonly Dictionary<string, Dictionary<string, (int BillCount, decimal Expense, decimal Income)>>? _monthlySummary;

        /// <summary>Months selected by year to import. Keys are year strings.</summary>
        public Dictionary<string, List<string>> SelectedMonthsByYear { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the notes section (if available) should be imported.</summary>
        public bool ImportNotes => _notesAvailable && ImportNotesCheckBox.IsChecked == true;

        public ImportSelectionDialog(
            IEnumerable<string> years,
            IEnumerable<string> monthNames,
            bool notesAvailable,
            Dictionary<string, Dictionary<string, (int BillCount, decimal Expense, decimal Income)>>? monthlySummary = null)
        {
            InitializeComponent();

            _notesAvailable = notesAvailable;
            _monthlySummary = monthlySummary;
            ImportNotesCheckBox.IsEnabled = notesAvailable;
            if (!notesAvailable)
            {
                ImportNotesCheckBox.Content = "Import notes (no notes section found in file)";
            }

            BuildYearSections(years, monthNames);

            // Default: everything selected so the user can Import without extra steps.
            SelectAll_Click(this, new RoutedEventArgs());
        }

        private void BuildYearSections(IEnumerable<string> years, IEnumerable<string> monthNames)
        {
            var monthList = monthNames.ToList();

            foreach (var year in years.OrderByDescending(y => y, StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, (int BillCount, decimal Expense, decimal Income)>? yearMonthSummary = null;
                _monthlySummary?.TryGetValue(year, out yearMonthSummary);

                var headerCheckBox = new CheckBox
                {
                    Content = year,
                    FontWeight = FontWeights.SemiBold,
                    IsThreeState = true,
                    Margin = new Thickness(0, 0, 0, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerCheckBox.Checked += YearCheckBox_Changed;
                headerCheckBox.Unchecked += YearCheckBox_Changed;
                headerCheckBox.Indeterminate += YearCheckBox_Changed;

                var yearHeader = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                yearHeader.Children.Add(headerCheckBox);

                if (yearMonthSummary != null)
                {
                    int totalBills = yearMonthSummary.Values.Sum(m => m.BillCount);
                    decimal totalExpense = yearMonthSummary.Values.Sum(m => m.Expense);
                    decimal totalIncome = yearMonthSummary.Values.Sum(m => m.Income);

                    if (totalBills > 0 || totalIncome > 0)
                    {
                        yearHeader.Children.Add(new TextBlock
                        {
                            Text = $"  {totalBills} bill{(totalBills == 1 ? "" : "s")}  •  ${totalExpense:N2} expenses  •  ${totalIncome:N2} income",
                            VerticalAlignment = VerticalAlignment.Center,
                            Opacity = 0.65,
                            Margin = new Thickness(4, 0, 0, 0)
                        });
                    }
                }

                var monthGrid = new UniformGrid
                {
                    Columns = 3,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                var monthCheckBoxes = new List<CheckBox>();
                foreach (var month in monthList)
                {
                    string monthDisplay = month;
                    string? monthTooltip = null;

                    if (yearMonthSummary != null && yearMonthSummary.TryGetValue(month, out var ms))
                    {
                        if (ms.BillCount > 0)
                        {
                            monthDisplay = $"{month} ({ms.BillCount}  bill{(ms.BillCount == 1 ? "" : "s")}  )";
                        }
                        if (ms.BillCount > 0 || ms.Income > 0)
                        {
                            monthTooltip = $"Expenses: ${ms.Expense:N2}  |  Income: ${ms.Income:N2}";
                        }
                    }

                    var monthCheckBox = new CheckBox
                    {
                        Content = monthDisplay,
                        Tag = new MonthTag(year, month),
                        Margin = new Thickness(0, 0, 12, 6),
                        ToolTip = monthTooltip
                    };
                    monthCheckBox.Checked += MonthCheckBox_Changed;
                    monthCheckBox.Unchecked += MonthCheckBox_Changed;
                    monthGrid.Children.Add(monthCheckBox);
                    monthCheckBoxes.Add(monthCheckBox);
                }

                _yearCheckBoxes[year] = headerCheckBox;
                _monthCheckBoxesByYear[year] = monthCheckBoxes;

                YearPanel.Children.Add(new Expander
                {
                    Header = yearHeader,
                    IsExpanded = true,
                    Margin = new Thickness(0, 0, 0, 10),
                    Content = monthGrid
                });

                UpdateYearCheckState(year);
            }
        }

        private void YearCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSelections || sender is not CheckBox yearCheckBox)
                return;

            string year = yearCheckBox.Content?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(year) || !_monthCheckBoxesByYear.TryGetValue(year, out var monthCheckBoxes))
                return;

            bool shouldCheck = yearCheckBox.IsChecked == true;
            _isUpdatingSelections = true;
            foreach (var monthCheckBox in monthCheckBoxes)
            {
                monthCheckBox.IsChecked = shouldCheck;
            }
            _isUpdatingSelections = false;

            UpdateYearCheckState(year);
        }

        private void MonthCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSelections || sender is not CheckBox monthCheckBox)
                return;

            string year = (monthCheckBox.Tag as MonthTag)?.Year ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(year))
            {
                UpdateYearCheckState(year);
            }
        }

        /// <summary>
        /// Updates the tri-state year checkbox to reflect its month checkboxes.
        /// Preserves the outer _isUpdatingSelections flag so callers like
        /// SelectAll_Click / ClearAll_Click keep their guard intact.
        /// </summary>
        private void UpdateYearCheckState(string year)
        {
            if (!_yearCheckBoxes.TryGetValue(year, out var yearCheckBox) ||
                !_monthCheckBoxesByYear.TryGetValue(year, out var monthCheckBoxes))
            {
                return;
            }

            bool allChecked = monthCheckBoxes.All(checkBox => checkBox.IsChecked == true);
            bool anyChecked = monthCheckBoxes.Any(checkBox => checkBox.IsChecked == true);

            // Save and restore the flag so an outer bulk-update loop stays guarded.
            bool previousFlag = _isUpdatingSelections;
            _isUpdatingSelections = true;
            yearCheckBox.IsChecked = allChecked ? true : anyChecked ? null : false;
            _isUpdatingSelections = previousFlag;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingSelections = true;
            foreach (var monthCheckBoxes in _monthCheckBoxesByYear.Values)
            {
                foreach (var monthCheckBox in monthCheckBoxes)
                {
                    monthCheckBox.IsChecked = true;
                }
            }

            foreach (var year in _yearCheckBoxes.Keys.ToList())
            {
                UpdateYearCheckState(year);
            }

            if (_notesAvailable)
            {
                ImportNotesCheckBox.IsChecked = true;
            }
            _isUpdatingSelections = false;
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingSelections = true;
            foreach (var monthCheckBoxes in _monthCheckBoxesByYear.Values)
            {
                foreach (var monthCheckBox in monthCheckBoxes)
                {
                    monthCheckBox.IsChecked = false;
                }
            }

            foreach (var year in _yearCheckBoxes.Keys.ToList())
            {
                UpdateYearCheckState(year);
            }

            ImportNotesCheckBox.IsChecked = false;
            _isUpdatingSelections = false;
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            SelectedMonthsByYear = CaptureSelection();
            if (SelectedMonthsByYear.Count == 0 && !ImportNotes)
            {
                MessageBox.Show(this,
                    "Select at least one month or enable notes import before continuing.",
                    "Nothing Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private Dictionary<string, List<string>> CaptureSelection()
        {
            var selection = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _monthCheckBoxesByYear)
            {
                var selectedMonths = entry.Value
                    .Where(checkBox => checkBox.IsChecked == true)
                    .Select(checkBox => (checkBox.Tag as MonthTag)?.Month ?? string.Empty)
                    .Where(month => !string.IsNullOrWhiteSpace(month))
                    .ToList();

                if (selectedMonths.Count > 0)
                {
                    selection[entry.Key] = selectedMonths;
                }
            }

            return selection;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}