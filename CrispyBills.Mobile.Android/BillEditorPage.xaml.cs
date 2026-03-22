using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android;

/// <summary>
/// Page used to edit or create a <see cref="BillItem"/> in the mobile UI.
/// Exposes an async <see cref="WaitForResultAsync"/> pattern so callers can await the user's result.
/// </summary>
public partial class BillEditorPage : ContentPage
{
    private readonly TaskCompletionSource<BillItem?> _tcs = new();
    private readonly int _year;
    private readonly int _month;

    public BillEditorPage(BillItem seed, int year, int month, IReadOnlyList<string> categories)
    {
        InitializeComponent();
        _year = year;
        _month = month;

        var categoryList = categories
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        if (!string.IsNullOrWhiteSpace(seed.Category)
            && !categoryList.Contains(seed.Category, StringComparer.OrdinalIgnoreCase))
        {
            categoryList.Add(seed.Category);
            categoryList = categoryList.OrderBy(x => x).ToList();
        }

        if (categoryList.Count == 0)
        {
            categoryList.Add("General");
        }

        CategoryPicker.ItemsSource = categoryList;

        NameEntry.Text = seed.Name;
        AmountEntry.Text = seed.Amount.ToString("0.00");
        CategoryPicker.SelectedItem = categoryList.FirstOrDefault(x => x.Equals(seed.Category, StringComparison.OrdinalIgnoreCase))
            ?? "General";
        DueDatePicker.Date = seed.DueDate == default ? DateTime.Today : seed.DueDate.Date;
        PaidCheckBox.IsChecked = seed.IsPaid;
        RecurringCheckBox.IsChecked = seed.IsRecurring;

        RecurrenceEveryPicker.SelectedIndex = seed.RecurrenceEveryMonths switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            6 => 3,
            12 => 4,
            _ => 0
        };
        RecurrenceEndModePicker.SelectedIndex = seed.RecurrenceEndMode switch
        {
            RecurrenceEndMode.EndOnDate => 1,
            RecurrenceEndMode.EndAfterOccurrences => 2,
            _ => 0
        };
        RecurrenceEndDatePicker.Date = seed.RecurrenceEndDate ?? DueDatePicker.Date;
        RecurrenceCountEntry.Text = seed.RecurrenceMaxOccurrences?.ToString() ?? string.Empty;

        RecurringOptionsGrid.IsVisible = seed.IsRecurring;
        UpdateEndModeVisibility();

        DueDatePicker.MinimumDate = new DateTime(year, month, 1);
        DueDatePicker.MaximumDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));

        ValidateInputs(showValidation: false);
    }

    public Task<BillItem?> WaitForResultAsync()
    {
        return _tcs.Task;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (!_tcs.Task.IsCompleted)
        {
            _tcs.TrySetResult(null);
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(null);
        await Navigation.PopAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!ValidateInputs(showValidation: true))
        {
            return;
        }

        _ = decimal.TryParse(AmountEntry.Text, out var amount);

        if (CategoryPicker.SelectedItem is not string category || string.IsNullOrWhiteSpace(category))
        {
            category = "General";
        }

        var recurrenceEveryMonths = RecurrenceEveryPicker.SelectedIndex switch
        {
            1 => 2,
            2 => 3,
            3 => 6,
            4 => 12,
            _ => 1
        };

        var endMode = RecurrenceEndModePicker.SelectedIndex switch
        {
            1 => RecurrenceEndMode.EndOnDate,
            2 => RecurrenceEndMode.EndAfterOccurrences,
            _ => RecurrenceEndMode.None
        };

        int? maxOccurrences = null;
        if (endMode == RecurrenceEndMode.EndAfterOccurrences)
        {
            maxOccurrences = int.TryParse(RecurrenceCountEntry.Text, out var parsed) ? Math.Max(1, parsed) : 1;
        }

        DateTime? endDate = endMode == RecurrenceEndMode.EndOnDate ? RecurrenceEndDatePicker.Date : null;

        var result = new BillItem
        {
            Name = NameEntry.Text.Trim(),
            Amount = amount,
            Category = category,
            DueDate = DueDatePicker.Date,
            IsPaid = PaidCheckBox.IsChecked,
            IsRecurring = RecurringCheckBox.IsChecked,
            RecurrenceEveryMonths = recurrenceEveryMonths,
            RecurrenceEndMode = RecurringCheckBox.IsChecked ? endMode : RecurrenceEndMode.None,
            RecurrenceEndDate = RecurringCheckBox.IsChecked ? endDate : null,
            RecurrenceMaxOccurrences = RecurringCheckBox.IsChecked ? maxOccurrences : null,
            Year = _year,
            Month = _month
        };

        _tcs.TrySetResult(result);
        await Navigation.PopAsync();
    }

    private void OnRecurringChanged(object? sender, CheckedChangedEventArgs e)
    {
        RecurringOptionsGrid.IsVisible = e.Value;
        if (!e.Value)
        {
            RecurrenceEndModePicker.SelectedIndex = 0;
            UpdateEndModeVisibility();
        }

        ValidateInputs(showValidation: false);
    }

    private void OnEndModeChanged(object? sender, EventArgs e)
    {
        UpdateEndModeVisibility();
        ValidateInputs(showValidation: false);
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        ValidateInputs(showValidation: false);
    }

    private void UpdateEndModeVisibility()
    {
        RecurrenceEndDatePicker.IsVisible = RecurrenceEndModePicker.SelectedIndex == 1;
        RecurrenceCountEntry.IsVisible = RecurrenceEndModePicker.SelectedIndex == 2;
    }

    private bool ValidateInputs(bool showValidation)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(NameEntry.Text))
        {
            errors.Add("Name is required.");
        }

        if (!decimal.TryParse(AmountEntry.Text, out var amount) || amount < 0)
        {
            errors.Add("Amount must be a non-negative number.");
        }

        if (RecurringCheckBox.IsChecked && RecurrenceEndModePicker.SelectedIndex == 2)
        {
            if (!int.TryParse(RecurrenceCountEntry.Text, out var count) || count < 1)
            {
                errors.Add("Occurrences must be a positive integer.");
            }
        }

        if (showValidation)
        {
            ValidationLabel.IsVisible = errors.Count > 0;
            ValidationLabel.Text = string.Join(" ", errors);
        }
        else if (errors.Count == 0)
        {
            ValidationLabel.IsVisible = false;
            ValidationLabel.Text = string.Empty;
        }

        return errors.Count == 0;
    }
}
