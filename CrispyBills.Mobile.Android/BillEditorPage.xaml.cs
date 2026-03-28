using CrispyBills.Mobile.Android.Models;
using Microsoft.Maui.ApplicationModel;

namespace CrispyBills.Mobile.Android;

public partial class BillEditorPage : ContentPage
{
	private readonly TaskCompletionSource<BillItem?> _tcs = new();
	private readonly int _year;
	private readonly int _month;
	private readonly Guid _preservedId;
	private bool _dirty;
	private bool _suppressDirty = true;

	public BillEditorPage(BillItem seed, int year, int month, IReadOnlyList<string> categories)
	{
		InitializeComponent();
		_year = year;
		_month = month;
		_preservedId = seed.Id;

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
		PaidSwitch.IsToggled = seed.IsPaid;
		RecurringSwitch.IsToggled = seed.IsRecurring;

		RecurrenceFrequencyPicker.SelectedIndex = !seed.IsRecurring
			? 0
			: seed.RecurrenceFrequency switch
			{
				RecurrenceFrequency.Weekly => 1,
				RecurrenceFrequency.BiWeekly => 2,
				_ => 0
			};
		RecurrenceEveryMonthsEntry.Text = Math.Max(1, seed.RecurrenceEveryMonths).ToString();
		RecurrenceEndModePicker.SelectedIndex = seed.RecurrenceEndMode switch
		{
			RecurrenceEndMode.EndOnDate => 1,
			RecurrenceEndMode.EndAfterOccurrences => 2,
			_ => 0
		};
		RecurrenceEndDatePicker.Date = seed.RecurrenceEndDate ?? DueDatePicker.Date;
		RecurrenceCountEntry.Text = seed.RecurrenceMaxOccurrences?.ToString() ?? string.Empty;

		RecurringOptionsGrid.IsVisible = seed.IsRecurring;
		RecurrenceEveryMonthsGrid.IsVisible = seed.IsRecurring && RecurrenceFrequencyPicker.SelectedIndex == 0;
		UpdateEndModeVisibility();

		DueDatePicker.MinimumDate = new DateTime(year, month, 1);
		DueDatePicker.MaximumDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));

		ValidateInputs(showValidation: false);
		_suppressDirty = false;
	}

	public Task<BillItem?> WaitForResultAsync() => _tcs.Task;

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Shell.SetNavBarIsVisible(this, true);
	}

	protected override bool OnBackButtonPressed()
	{
		if (_tcs.Task.IsCompleted)
		{
			return base.OnBackButtonPressed();
		}

		MainThread.BeginInvokeOnMainThread(async () => await HandleSystemBackAsync());
		return true;
	}

	private async Task HandleSystemBackAsync()
	{
		if (_tcs.Task.IsCompleted)
		{
			return;
		}

		if (!_dirty)
		{
			_tcs.TrySetResult(null);
			await Navigation.PopAsync();
			return;
		}

		var discard = await DisplayAlert(
			"Discard changes?",
			"Your edits on this bill will be lost.",
			"Discard",
			"Keep editing");
		if (!discard)
		{
			return;
		}

		_tcs.TrySetResult(null);
		await Navigation.PopAsync();
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
		if (_dirty)
		{
			var discard = await DisplayAlert(
				"Discard changes?",
				"Your edits on this bill will be lost.",
				"Discard",
				"Keep editing");
			if (!discard)
			{
				return;
			}
		}

		_tcs.TrySetResult(null);
		await Navigation.PopAsync();
	}

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		if (!ValidateInputs(showValidation: true))
		{
			return;
		}

		if (!decimal.TryParse(AmountEntry.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var amount)
			&& !decimal.TryParse(AmountEntry.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out amount))
		{
			ValidationLabel.IsVisible = true;
			ValidationLabel.Text = "Amount must be a valid number.";
			return;
		}

		if (CategoryPicker.SelectedItem is not string category || string.IsNullOrWhiteSpace(category))
		{
			category = "General";
		}

		var recurrenceEveryMonths = 1;
		if (RecurrenceFrequencyPicker.SelectedIndex == 0)
		{
			recurrenceEveryMonths = int.TryParse(RecurrenceEveryMonthsEntry.Text, out var parsedEveryMonths)
				? Math.Max(1, parsedEveryMonths)
				: 1;
		}

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

		var recurrenceFrequency = RecurrenceFrequencyPicker.SelectedIndex switch
		{
			1 => RecurrenceFrequency.Weekly,
			2 => RecurrenceFrequency.BiWeekly,
			_ => RecurrenceFrequency.MonthlyInterval
		};

		var result = new BillItem
		{
			Id = _preservedId,
			Name = NameEntry.Text.Trim(),
			Amount = amount,
			Category = category,
			DueDate = DueDatePicker.Date,
			IsPaid = PaidSwitch.IsToggled,
			IsRecurring = RecurringSwitch.IsToggled,
			RecurrenceFrequency = RecurringSwitch.IsToggled ? recurrenceFrequency : RecurrenceFrequency.None,
			RecurrenceEveryMonths = recurrenceEveryMonths,
			RecurrenceEndMode = RecurringSwitch.IsToggled ? endMode : RecurrenceEndMode.None,
			RecurrenceEndDate = RecurringSwitch.IsToggled ? endDate : null,
			RecurrenceMaxOccurrences = RecurringSwitch.IsToggled ? maxOccurrences : null,
			Year = _year,
			Month = _month
		};

		_tcs.TrySetResult(result);
		await Navigation.PopAsync();
	}

	private void OnRecurringToggled(object? sender, ToggledEventArgs e)
	{
		RecurringOptionsGrid.IsVisible = e.Value;
		RecurrenceEveryMonthsGrid.IsVisible = e.Value && RecurrenceFrequencyPicker.SelectedIndex == 0;
		if (!e.Value)
		{
			RecurrenceEndModePicker.SelectedIndex = 0;
			UpdateEndModeVisibility();
		}
		else
		{
			// Recurrence details remain visible while recurrence is enabled.
		}

		ValidateInputs(showValidation: false);
	}

	private void OnRecurrenceFrequencyChanged(object? sender, EventArgs e)
	{
		MarkDirty();
		RecurrenceEveryMonthsGrid.IsVisible = RecurrenceFrequencyPicker.SelectedIndex == 0;
		ValidateInputs(showValidation: false);
	}

	private void OnEndModeChanged(object? sender, EventArgs e)
	{
		MarkDirty();
		UpdateEndModeVisibility();
		ValidateInputs(showValidation: false);
	}

	private void OnInputChanged(object? sender, EventArgs e)
	{
		MarkDirty();
		ValidateInputs(showValidation: false);
	}

	private void OnDateSelected(object? sender, DateChangedEventArgs e)
	{
		MarkDirty();
	}

	private void OnPaidSwitchToggled(object? sender, ToggledEventArgs e)
	{
		MarkDirty();
	}

	private void MarkDirty()
	{
		if (_suppressDirty)
		{
			return;
		}

		_dirty = true;
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

		if (!decimal.TryParse(AmountEntry.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var amt)
			&& !decimal.TryParse(AmountEntry.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out amt))
		{
			errors.Add("Amount must be a valid number.");
		}
		else if (amt < 0)
		{
			errors.Add("Amount must be non-negative.");
		}

		if (RecurringSwitch.IsToggled && RecurrenceEndModePicker.SelectedIndex == 2)
		{
			if (!int.TryParse(RecurrenceCountEntry.Text, out var count) || count < 1)
			{
				errors.Add("Occurrences must be a positive integer.");
			}
		}

		if (RecurringSwitch.IsToggled && RecurrenceFrequencyPicker.SelectedIndex == 0)
		{
			if (!int.TryParse(RecurrenceEveryMonthsEntry.Text, out var everyMonths) || everyMonths < 1)
			{
				errors.Add("Monthly interval must be a positive integer.");
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

	private void OnPaidRowTapped(object? sender, TappedEventArgs e)
	{
		MarkDirty();
		PaidSwitch.IsToggled = !PaidSwitch.IsToggled;
	}

	private void OnRecurringRowTapped(object? sender, TappedEventArgs e)
	{
		MarkDirty();
		RecurringSwitch.IsToggled = !RecurringSwitch.IsToggled;
	}
}
