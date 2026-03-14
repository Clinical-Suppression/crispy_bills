using System;
using System.Globalization;
using System.Windows;

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
            DueCalendar.SelectedDate = DateTime.Now;
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
            IsPaidCheck.IsChecked = existing.IsPaid;
            RecurringCheck.IsChecked = isRecurring;
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

            var bill = new Bill
            {
                Id = id,
                Name = name,
                Amount = amt,
                Category = selectedCategory,
                DueDate = selectedDate,
                IsPaid = paid,
                IsRecurring = IsRecurring
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
    }
}