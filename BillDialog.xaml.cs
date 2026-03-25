using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CrispyBills
{
    /// <summary>
    /// Modal add/edit dialog for bill fields, including paid/recurring flags used by month propagation logic.
    /// </summary>
    public partial class BillDialog : Window
    {
        // Common household categories - used to populate the category dropdown
        private static readonly string[] _categories = new[]
        {
            "General",
            "Housing",
            "Utilities",
            "Groceries",
            "Transportation",
            "Insurance",
            "Healthcare",
            "Entertainment",
            "Subscriptions",
            "Savings",
            "Debt",
            "Education",
            "Personal Care",
            "Miscellaneous"
        };

        public Bill? ResultBill { get; private set; }
        public bool IsRecurring => RecurringCheck.IsChecked == true;

        /// <summary>
        /// Initializes the dialog with default category and current date.
        /// </summary>
        public BillDialog()
        {
            InitializeComponent();

            // Populate category dropdown
            CategoryBox.ItemsSource = _categories;
            CategoryBox.SelectedItem = "General";

            // Default calendar selection
            InitializeCalendarDefaults(DateTime.Today);

            RecurrenceFrequencyBox.SelectedIndex = 2;
            RecurrenceEveryBox.SelectedIndex = 0;
            RecurrenceEndBox.SelectedIndex = 0;
            UpdateRecurringDetailsVisibility();
            UpdateMonthlyIntervalVisibility();
            UpdateRecurrenceEndVisibility();
        }

        /// <summary>
        /// Initializes the dialog with a target period so the calendar opens on that year/month.
        /// </summary>
        public BillDialog(DateTime contextPeriodStart) : this()
        {
            InitializeCalendarDefaults(contextPeriodStart);
        }

        public BillDialog(Bill existing) : this()
        {
            InitializeFromExisting(existing, existing.IsRecurring);
        }

        /// <summary>
        /// Initializes the dialog with an existing bill and explicit recurring state.
        /// </summary>
        public BillDialog(Bill existing, bool isRecurring) : this()
        {
            InitializeFromExisting(existing, isRecurring);
        }

        /// <summary>
        /// Copies existing bill values into dialog controls while preserving custom categories.
        /// </summary>
        private void InitializeFromExisting(Bill existing, bool isRecurring)
        {
            ResultBill = existing;
            NameBox.Text = existing.Name;
            AmountBox.Text = existing.Amount.ToString(CultureInfo.InvariantCulture);

            // select category if present in list, otherwise add it and select
            if (!string.IsNullOrEmpty(existing.Category))
            {
                if (Array.IndexOf(_categories, existing.Category) >= 0)
                    CategoryBox.SelectedItem = existing.Category;
                else
                {
                    // keep the known categories intact, allow custom value by adding
                    CategoryBox.ItemsSource = null;
                    var list = new System.Collections.ObjectModel.ObservableCollection<string>(_categories) { existing.Category };
                    CategoryBox.ItemsSource = list;
                    CategoryBox.SelectedItem = existing.Category;
                }
            }

            DueCalendar.SelectedDate = existing.DueDate;
            // Keep the selected due date and open the calendar on the bill's context month from the main app.
            DueCalendar.DisplayDate = existing.ContextPeriodStart;
            IsPaidCheck.IsChecked = existing.IsPaid;
            RecurringCheck.IsChecked = isRecurring;

            if (isRecurring)
            {
                RecurrenceFrequencyBox.SelectedIndex = existing.RecurrenceFrequency switch
                {
                    RecurrenceFrequency.Weekly => 0,
                    RecurrenceFrequency.BiWeekly => 1,
                    _ => 2
                };
                RecurrenceEveryBox.SelectedIndex = existing.RecurrenceEveryMonths switch
                {
                    2 => 1,
                    3 => 2,
                    6 => 3,
                    12 => 4,
                    _ => 0
                };
                RecurrenceEndBox.SelectedIndex = existing.RecurrenceEndMode switch
                {
                    RecurrenceEndMode.EndOnDate => 1,
                    RecurrenceEndMode.EndAfterOccurrences => 2,
                    _ => 0
                };
                RecurrenceEndDatePicker.SelectedDate = existing.RecurrenceEndDate ?? existing.DueDate;
                RecurrenceCountBox.Text = existing.RecurrenceMaxOccurrences?.ToString() ?? string.Empty;
            }
            else
            {
                RecurrenceFrequencyBox.SelectedIndex = 2;
                RecurrenceEveryBox.SelectedIndex = 0;
                RecurrenceEndBox.SelectedIndex = 0;
                RecurrenceEndDatePicker.SelectedDate = existing.DueDate;
                RecurrenceCountBox.Text = string.Empty;
            }

            UpdateRecurringDetailsVisibility();
            UpdateMonthlyIntervalVisibility();
            UpdateRecurrenceEndVisibility();
        }

        private void InitializeCalendarDefaults(DateTime contextPeriodStart)
        {
            var monthStart = new DateTime(contextPeriodStart.Year, contextPeriodStart.Month, 1);
            int day = Math.Min(DateTime.Today.Day, DateTime.DaysInMonth(monthStart.Year, monthStart.Month));

            DueCalendar.DisplayDate = monthStart;
            DueCalendar.SelectedDate = new DateTime(monthStart.Year, monthStart.Month, day);
        }

        /// <summary>
        /// Validates user input and emits a normalized bill payload through <see cref="ResultBill"/>.
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var name = (NameBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Bill name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            if (!decimal.TryParse(AmountBox.Text, NumberStyles.Currency, CultureInfo.CurrentCulture, out decimal amt) &&
                !decimal.TryParse(AmountBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out amt))
            {
                MessageBox.Show("Enter a valid amount.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                AmountBox.Focus();
                AmountBox.SelectAll();
                return;
            }

            if (amt < 0)
            {
                MessageBox.Show("Amount cannot be negative.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                AmountBox.Focus();
                AmountBox.SelectAll();
                return;
            }

            var selectedCategory = CategoryBox.Text;
            if (string.IsNullOrWhiteSpace(selectedCategory))
                selectedCategory = CategoryBox.SelectedItem?.ToString() ?? "General";
            var selectedDate = DueCalendar.SelectedDate ?? DateTime.Now;

            // If we are editing an existing bill keep its Id and Paid state where appropriate
            Guid id = ResultBill?.Id ?? Guid.NewGuid();
            bool paid = IsPaidCheck.IsChecked == true;
            var prior = ResultBill;
            var isRecurring = RecurringCheck.IsChecked == true;

            var recurrenceEveryMonths = RecurrenceEveryBox.SelectedIndex switch
            {
                1 => 2,
                2 => 3,
                3 => 6,
                4 => 12,
                _ => 1
            };

            var endMode = RecurrenceEndBox.SelectedIndex switch
            {
                1 => RecurrenceEndMode.EndOnDate,
                2 => RecurrenceEndMode.EndAfterOccurrences,
                _ => RecurrenceEndMode.None
            };

            int? maxOccurrences = null;
            if (isRecurring && endMode == RecurrenceEndMode.EndAfterOccurrences)
            {
                maxOccurrences = int.TryParse(RecurrenceCountBox.Text, out var oc) ? Math.Max(1, oc) : 1;
            }

            if (isRecurring && endMode == RecurrenceEndMode.EndOnDate && RecurrenceEndDatePicker.SelectedDate is null)
            {
                MessageBox.Show("Select an end date for the recurring series.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime? endDate = isRecurring && endMode == RecurrenceEndMode.EndOnDate
                ? RecurrenceEndDatePicker.SelectedDate?.Date
                : null;

            var recurrenceFrequency = RecurrenceFrequencyBox.SelectedIndex switch
            {
                0 => RecurrenceFrequency.Weekly,
                1 => RecurrenceFrequency.BiWeekly,
                _ => RecurrenceFrequency.MonthlyInterval
            };

            var bill = new Bill
            {
                Id = id,
                Name = name,
                Amount = amt,
                Category = selectedCategory,
                DueDate = selectedDate,
                IsPaid = paid,
                IsRecurring = isRecurring,
                RecurrenceFrequency = isRecurring ? recurrenceFrequency : RecurrenceFrequency.None,
                RecurrenceEveryMonths = isRecurring ? Math.Max(1, recurrenceEveryMonths) : 1,
                RecurrenceEndMode = isRecurring ? endMode : RecurrenceEndMode.None,
                RecurrenceEndDate = isRecurring ? endDate : null,
                RecurrenceMaxOccurrences = isRecurring ? maxOccurrences : null,
                RecurrenceGroupId = prior?.RecurrenceGroupId
            };

            ResultBill = bill;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RecurringCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateRecurringDetailsVisibility();
        }

        private void RecurrenceFrequencyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMonthlyIntervalVisibility();
        }

        private void RecurrenceEndBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRecurrenceEndVisibility();
        }

        private void UpdateRecurringDetailsVisibility()
        {
            RecurringDetailsGrid.Visibility = RecurringCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateMonthlyIntervalVisibility()
        {
            var monthly = RecurrenceFrequencyBox.SelectedIndex == 2;
            var v = monthly ? Visibility.Visible : Visibility.Collapsed;
            EveryMonthsLabel.Visibility = v;
            RecurrenceEveryBox.Visibility = v;
        }

        private void UpdateRecurrenceEndVisibility()
        {
            var idx = RecurrenceEndBox.SelectedIndex;
            RecurrenceEndDatePicker.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            RecurrenceCountBox.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}