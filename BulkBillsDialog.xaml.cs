using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace CrispyBills
{
    /// <summary>Simple grid for entering up to 50 bills (buffer-style initial rows).</summary>
    public partial class BulkBillsDialog : Window
    {
        private readonly DateTime _period;
        private const int InitialRows = 10;
        private const int MaxRows = 50;

        public ObservableCollection<BulkBillRowVm> Rows { get; } = new();

        public List<Bill>? ResultBills { get; private set; }

        public BulkBillsDialog(DateTime contextPeriod)
        {
            InitializeComponent();
            _period = new DateTime(contextPeriod.Year, contextPeriod.Month, 1);
            DataContext = this;
            var defaultDay = Math.Min(DateTime.Today.Day, DateTime.DaysInMonth(_period.Year, _period.Month));
            var defaultDue = new DateTime(_period.Year, _period.Month, defaultDay);
            for (var i = 0; i < InitialRows; i++)
            {
                Rows.Add(new BulkBillRowVm { DueDateText = defaultDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            }
        }

        private void BillsGrid_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            TryAppendRowIfNeeded();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var list = new List<Bill>();
            foreach (var r in Rows)
            {
                if (string.IsNullOrWhiteSpace(r.Name))
                {
                    continue;
                }

                if (!decimal.TryParse(r.Amount, NumberStyles.Any, CultureInfo.CurrentCulture, out var amt)
                    && !decimal.TryParse(r.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out amt))
                {
                    MessageBox.Show(this, $"Invalid amount for '{r.Name}'.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (amt < 0)
                {
                    MessageBox.Show(this, $"Amount cannot be negative for '{r.Name}'.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!DateTime.TryParse(r.DueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                {
                    MessageBox.Show(this, $"Invalid due date for '{r.Name}'. Use yyyy-MM-dd.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var cat = string.IsNullOrWhiteSpace(r.Category) ? "General" : r.Category.Trim();
                var bill = new Bill
                {
                    Id = Guid.NewGuid(),
                    Name = r.Name.Trim(),
                    Amount = amt,
                    Category = cat,
                    DueDate = due,
                    IsPaid = r.IsPaid,
                    IsRecurring = r.IsRecurring,
                    RecurrenceFrequency = r.IsRecurring ? RecurrenceFrequency.MonthlyInterval : RecurrenceFrequency.None,
                    RecurrenceEveryMonths = 1,
                    RecurrenceEndMode = RecurrenceEndMode.None,
                    RecurrenceEndDate = null,
                    RecurrenceMaxOccurrences = null
                };
                list.Add(bill);
            }

            if (list.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one bill with a name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ResultBills = list;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>Called by host when the last row is considered complete; grows toward 50.</summary>
        public void TryAppendRowIfNeeded()
        {
            if (Rows.Count >= MaxRows)
            {
                return;
            }

            var last = Rows[^1];
            if (string.IsNullOrWhiteSpace(last.Name))
            {
                return;
            }

            if (!decimal.TryParse(last.Amount, NumberStyles.Any, CultureInfo.CurrentCulture, out var amt)
                && !decimal.TryParse(last.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out amt))
            {
                return;
            }

            if (amt < 0 || !DateTime.TryParse(last.DueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return;
            }

            var defaultDay = Math.Min(DateTime.Today.Day, DateTime.DaysInMonth(_period.Year, _period.Month));
            var defaultDue = new DateTime(_period.Year, _period.Month, defaultDay);
            Rows.Add(new BulkBillRowVm { DueDateText = defaultDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        }
    }

    public sealed class BulkBillRowVm
    {
        public string Name { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public string DueDateText { get; set; } = string.Empty;
        public bool IsPaid { get; set; }
        public bool IsRecurring { get; set; }
    }
}
