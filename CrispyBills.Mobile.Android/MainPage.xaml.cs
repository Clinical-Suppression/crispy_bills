using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using System.IO;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CrispyBills.Mobile.Android;

public partial class MainPage : ContentPage
{
	private readonly BillingRepository _repository = new();
	private readonly BillingService _service;
	private readonly LocalizationService _localization;
	private bool _loaded;
	private bool _startupDiagnosticsShown;
	private string? _lastDuplicateWarningKey;

	private int _currentYear = DateTime.Today.Year;
	private int _currentMonth = DateTime.Today.Month;
	private List<int> _availableYears = new();
	private HashSet<int> _archivedYears = new();

	private readonly ObservableCollection<BillListItem> _visibleBills = new();
	private List<BillItem> _monthBills = new();

	public MainPage()
	{
		InitializeComponent();
		_service = new BillingService(_repository);
		_localization = new LocalizationService(_repository);

		CategoryPicker.ItemsSource = new List<string> { "All categories" };
		CategoryPicker.SelectedIndex = 0;

		BillsCollection.ItemsSource = _visibleBills;
	}

	protected override async Task OnAppearing()
	{
		base.OnAppearing();
		try
		{
			if (_loaded)
			{
				return;
			}

			_loaded = true;
			await _localization.InitializeAsync();
			await LoadYearAsync(_currentYear);

			if (StartupDiagnostics.HasIssues && !_startupDiagnosticsShown)
			{
				_startupDiagnosticsShown = true;
				var open = await DisplayAlert("Startup diagnostics", "Issues were detected during startup recovery. Open diagnostics now?", "Open", "Later");
				if (open)
				{
					await Navigation.PushAsync(new DiagnosticsPage(_service));
				}
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"An error occurred during startup: {ex.Message}", "OK");
			// Optionally log to diagnostics
		}
	}

	private async Task LoadYearAsync(int year)
	{
		await RefreshAvailableYearsAsync();
		if (_availableYears.Count > 0 && !_availableYears.Contains(year))
		{
			year = _availableYears[^1];
		}

		_currentYear = year;
		await _service.LoadYearAsync(year);
		await ReloadMonthAsync();
	}

	private Task ReloadMonthAsync()
	{
		YearButton.Text = _archivedYears.Contains(_currentYear)
			? $"{_currentYear} (Archived)"
			: _currentYear.ToString();
		MonthButton.Text = MonthNames.Name(_currentMonth);
		var yearIdx = _availableYears.IndexOf(_currentYear);
		PrevYearButton.IsEnabled = yearIdx > 0;
		NextYearButton.IsEnabled = yearIdx >= 0 && yearIdx < _availableYears.Count - 1;

		_monthBills = _service.GetBills(_currentMonth).Select(x => x.Clone()).ToList();

		UpdateCategoryFilter();
		ApplyFilters();
		UpdateSummaryCards();
		WarnDuplicateRecurringRules();
		return Task.CompletedTask;
	}

	private async Task RefreshAvailableYearsAsync()
	{
		_archivedYears = (await _service.GetArchivedYearsAsync()).ToHashSet();
		_availableYears = _service.GetAvailableYears().OrderBy(x => x).ToList();
		if (_availableYears.Count == 0)
		{
			_availableYears.Add(_currentYear);
		}

		if (!_availableYears.Contains(_currentYear))
		{
			_availableYears.Add(_currentYear);
			_availableYears = _availableYears.OrderBy(x => x).ToList();
		}

		if (!_archivedYears.Contains(_currentYear))
		{
			_availableYears = _availableYears.Where(y => !_archivedYears.Contains(y)).ToList();
			if (_availableYears.Count == 0)
			{
				_availableYears.Add(_currentYear);
			}
		}
	}

	private void UpdateCategoryFilter()
	{
		var selected = CategoryPicker.SelectedItem?.ToString() ?? "All categories";
		var categories = _service.Categories().ToList();
		categories.Insert(0, "All categories");

		CategoryPicker.ItemsSource = categories;
		CategoryPicker.SelectedItem = categories.Contains(selected) ? selected : "All categories";
	}

	private void ApplyFilters()
	{
		var search = (SearchEntry.Text ?? string.Empty).Trim();
		var category = CategoryPicker.SelectedItem?.ToString() ?? "All categories";

		var filtered = _monthBills.Where(b =>
			(string.IsNullOrWhiteSpace(search)
			 || b.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
			 || b.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
			&& (category == "All categories" || b.Category.Equals(category, StringComparison.OrdinalIgnoreCase)));

		_visibleBills.Clear();
		foreach (var bill in filtered.OrderBy(x => x.DueDate).ThenBy(x => x.Name))
		{
			_visibleBills.Add(new BillListItem(bill));
		}
	}

	private void UpdateSummaryCards()
	{
		var summary = _service.GetMonthSummary(_currentMonth);
		IncomeLabel.Text = _localization.FormatCurrency(_service.GetIncome(_currentMonth));
		UnpaidLabel.Text = _localization.FormatCurrency(summary.unpaid);
		RemainingLabel.Text = _localization.FormatCurrency(summary.remaining);
		BillCountLabel.Text = summary.billCount.ToString();
		RemainingLabel.TextColor = summary.remaining >= 0 ? GetResourceColor("Success", "#16A34A") : GetResourceColor("Danger", "#991B1B");
	}

	private static Color GetResourceColor(string key, string fallback)
	{
		if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
			return c;
		return Color.FromArgb(fallback);
	}

	private async Task OnPrevYearClicked(object? sender, EventArgs e)
	{
		try
		{
			var idx = _availableYears.IndexOf(_currentYear);
			if (idx > 0)
			{
				await LoadYearAsync(_availableYears[idx - 1]);
			}
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnPrevYearClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task OnNextYearClicked(object? sender, EventArgs e)
	{
		try
		{
			var idx = _availableYears.IndexOf(_currentYear);
			if (idx >= 0 && idx < _availableYears.Count - 1)
			{
				await LoadYearAsync(_availableYears[idx + 1]);
			}
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnNextYearClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task OnPrevMonthClicked(object? sender, EventArgs e)
	{
		try
		{
			_currentMonth = _currentMonth == 1 ? 12 : _currentMonth - 1;
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnPrevMonthClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnDebugToggleToggled(object? sender, ToggledEventArgs e)
	{
		try
		{
			if (sender is not Switch sw) return;
			if (e.Value)
			{
				var ok = await DisplayAlert("Enable Destructive Tools",
					"Enabling destructive delete tools will allow permanent removal of data. A backup will be created automatically where possible. Do you want to enable these tools for this session?",
					"Enable",
					"Cancel");
				if (!ok)
				{
					sw.IsToggled = false;
					_service.SetDebugDestructiveDeletesEnabled(false);
					DebugDeleteMonthButton.IsEnabled = false;
					DebugDeleteYearButton.IsEnabled = false;
					return;
				}

				_service.SetDebugDestructiveDeletesEnabled(true);
				DebugDeleteMonthButton.IsEnabled = true;
				DebugDeleteYearButton.IsEnabled = true;
			}
			else
			{
				_service.SetDebugDestructiveDeletesEnabled(false);
				DebugDeleteMonthButton.IsEnabled = false;
				DebugDeleteYearButton.IsEnabled = false;
			}
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnDebugToggleToggled", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnDebugDeleteMonthClicked(object? sender, EventArgs e)
	{
		try
		{
			var confirm = await DisplayAlert("Confirm Delete Month",
				$"Permanently delete all bills and income for {MonthNames.Name(_currentMonth)} in {_currentYear}? A backup will be created where possible.",
				"Delete",
				"Cancel");
			if (!confirm) return;

			var ok = await _service.DeleteMonthAsync(_currentMonth);
			if (!ok)
			{
				await DisplayAlert("Failed", "Month deletion did not complete. See diagnostics.", "OK");
			}
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnDebugDeleteMonthClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnDebugDeleteYearClicked(object? sender, EventArgs e)
	{
		try
		{
			var confirm = await DisplayAlert("Confirm Delete Year",
				$"Permanently delete the database and sidecars for year {_currentYear}? This cannot be undone except from backups. Proceed?",
				"Delete",
				"Cancel");
			if (!confirm) return;

			var ok = await _service.DeleteYearAsync(_currentYear);
			if (!ok)
			{
				await DisplayAlert("Failed", "Year deletion did not complete. See diagnostics.", "OK");
			}

			// Refresh UI after possible fallback
			await LoadYearAsync(_currentYear);
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnDebugDeleteYearClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task OnNextMonthClicked(object? sender, EventArgs e)
	{
		try
		{
			_currentMonth = _currentMonth == 12 ? 1 : _currentMonth + 1;
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnNextMonthClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task OnMonthSelectClicked(object? sender, EventArgs e)
	{
		try
		{
			var selected = await DisplayActionSheet(
				"Select Month",
				"Cancel",
				null,
				MonthNames.All.ToArray());

			if (string.IsNullOrWhiteSpace(selected) || selected == "Cancel")
			{
				return;
			}

			var idx = Array.IndexOf(MonthNames.All, selected);
			if (idx < 0)
			{
				return;
			}

			_currentMonth = idx + 1;
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnMonthSelectClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task OnYearSelectClicked(object? sender, EventArgs e)
	{
		try
		{
			await RefreshAvailableYearsAsync();
			var options = _availableYears.Select(y => y.ToString()).ToArray();
			var selected = await DisplayActionSheet("Select Year", "Cancel", null, options);
			if (string.IsNullOrWhiteSpace(selected) || selected == "Cancel")
			{
				return;
			}

			if (!int.TryParse(selected, out var year))
			{
				return;
			}

			await LoadYearAsync(year);
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnYearSelectClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private void OnSearchChanged(object? sender, TextChangedEventArgs e)
	{
		ApplyFilters();
	}

	private void OnCategoryChanged(object? sender, EventArgs e)
	{
		ApplyFilters();
	}

	private async void OnSetIncomeClicked(object? sender, EventArgs e)
	{
		var currentIncome = _service.GetIncome(_currentMonth).ToString("0.00", CultureInfo.InvariantCulture);
		var input = await DisplayPromptAsync(
			"Set Monthly Income",
			$"Enter income for {MonthNames.Name(_currentMonth)} and future months of {_currentYear}.",
			"Save",
			"Cancel",
			initialValue: currentIncome,
			keyboard: Keyboard.Numeric);

		if (string.IsNullOrWhiteSpace(input))
		{
			return;
		}

		if (!decimal.TryParse(input,
			NumberStyles.Currency | NumberStyles.AllowDecimalPoint,
			CultureInfo.InvariantCulture,
			out var income)
			&& !decimal.TryParse(input,
				NumberStyles.Currency,
				CultureInfo.CurrentCulture,
				out income))
		{
			await DisplayAlert("Invalid Income", "Enter a valid number.", "OK");
			return;
		}

		await _service.SetIncomeAsync(_currentMonth, income);
		await ReloadMonthAsync();
	}

	private async Task OnAddClicked(object? sender, EventArgs e)
	{
		try
		{
			var seed = new BillItem
			{
				Name = string.Empty,
				Amount = 0,
				Category = "General",
				DueDate = new DateTime(_currentYear, _currentMonth, Math.Min(DateTime.Today.Day, DateTime.DaysInMonth(_currentYear, _currentMonth))),
				IsPaid = false,
				IsRecurring = false,
				Year = _currentYear,
				Month = _currentMonth
			};

			var editor = new BillEditorPage(seed, _currentYear, _currentMonth, _service.Categories());
			await Navigation.PushAsync(editor);
			var result = await editor.WaitForResultAsync();
			if (result is null)
			{
				return;
			}

			await _service.AddBillAsync(_currentMonth, result);
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnAddClicked", ex);
			await DisplayAlert("Could not open add bill", ex.Message, "OK");
		}
	}

	private async void OnAddTemplateClicked(object? sender, EventArgs e)
	{
		try
		{
			var templates = _service.GetTemplates();
			if (templates.Count == 0)
			{
				await DisplayAlert("No templates", "No templates are configured.", "OK");
				return;
			}

			var options = templates.Select(t => $"{t.Name} ({t.Category})").ToArray();
			var selected = await DisplayActionSheet("Choose template", "Cancel", null, options);
			if (string.IsNullOrWhiteSpace(selected) || selected == "Cancel")
			{
				return;
			}

			var idx = Array.IndexOf(options, selected);
			if (idx < 0)
			{
				return;
			}

			var seed = _service.CreateDraftFromTemplate(templates[idx], _currentYear, _currentMonth);
			var editor = new BillEditorPage(seed, _currentYear, _currentMonth, _service.Categories());
			await Navigation.PushAsync(editor);
			var result = await editor.WaitForResultAsync();
			if (result is null)
			{
				return;
			}

			await _service.AddBillAsync(_currentMonth, result);
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnAddTemplateClicked", ex);
			await DisplayAlert("Template add failed", ex.Message, "OK");
		}
	}

	private async void OnEditClicked(object? sender, EventArgs e)
	{
		if ((sender as Button)?.CommandParameter is not Guid id)
		{
			return;
		}

		var existing = _monthBills.FirstOrDefault(x => x.Id == id);
		if (existing is null)
		{
			return;
		}

		try
		{
			var editor = new BillEditorPage(existing.Clone(), _currentYear, _currentMonth, _service.Categories());
			await Navigation.PushAsync(editor);
			var result = await editor.WaitForResultAsync();
			if (result is null)
			{
				return;
			}

			await _service.UpdateBillAsync(_currentMonth, id, result);
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnEditClicked", ex);
			await DisplayAlert("Could not open bill editor", ex.Message, "OK");
		}
	}

	private async void OnDeleteClicked(object? sender, EventArgs e)
	{
		if ((sender as Button)?.CommandParameter is not Guid id)
		{
			return;
		}

		var existing = _monthBills.FirstOrDefault(x => x.Id == id);
		if (existing is null)
		{
			return;
		}

		var warning = existing.IsRecurring
			? "Delete this recurring bill from this and future months?"
			: "Delete this bill?";

		var confirm = await DisplayAlert("Delete Bill", warning, "Delete", "Cancel");
		if (!confirm)
		{
			return;
		}

		var snapshot = _service.CreateYearSnapshot();
		await _service.DeleteBillAsync(_currentMonth, id);
		await ReloadMonthAsync();

		var undo = await DisplayActionSheet("Bill deleted.", "Done", null, "Undo");
		if (undo == "Undo")
		{
			await _service.RestoreYearSnapshotAsync(snapshot);
			await ReloadMonthAsync();
		}
	}

	private async void OnTogglePaidClicked(object? sender, EventArgs e)
	{
		if ((sender as Button)?.CommandParameter is not Guid id)
		{
			return;
		}

		await _service.TogglePaidAsync(_currentMonth, id);
		await ReloadMonthAsync();
	}

	private async void OnRolloverClicked(object? sender, EventArgs e)
	{
		if (_currentMonth == 12)
		{
			await DisplayAlert("Not Available", "Rollover only works from January to November.", "OK");
			return;
		}

		var confirm = await DisplayAlert("Rollover Unpaid",
			$"Copy unpaid bills from {MonthNames.Name(_currentMonth)} to {MonthNames.Name(_currentMonth + 1)}?",
			"Rollover",
			"Cancel");

		if (!confirm)
		{
			return;
		}

		var snapshot = _service.CreateYearSnapshot();
		await _service.RolloverUnpaidAsync(_currentMonth);
		await ReloadMonthAsync();

		var undo = await DisplayActionSheet("Rollover completed.", "Done", null, "Undo");
		if (undo == "Undo")
		{
			await _service.RestoreYearSnapshotAsync(snapshot);
			await ReloadMonthAsync();
		}
	}

	private async void OnNewYearClicked(object? sender, EventArgs e)
	{
		var target = _currentYear + 1;
		var confirm = await DisplayAlert("Create New Year",
			$"Create {target} using December recurring templates and unpaid carryovers?",
			"Create",
			"Cancel");

		if (!confirm)
		{
			return;
		}

		var created = await _service.CreateNewYearFromDecemberAsync();
		await RefreshAvailableYearsAsync();

		if (!created)
		{
			await DisplayAlert("Year Already Exists", $"{target} already has data. No new year snapshot was created.", "OK");
			return;
		}

		var switchYear = await DisplayAlert("Year Created", $"{target} is ready. Switch now?", "Yes", "Stay");
		if (switchYear)
		{
			_currentMonth = 1;
			await LoadYearAsync(target);
		}
	}

	private async void OnExportClicked(object? sender, EventArgs e)
	{
		try
		{
			var path = await _service.ExportStructuredCsvAsync(_currentYear);
			await DisplayAlert("Exported", $"Saved CSV to:\n{path}", "OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnExportClicked", ex);
			await DisplayAlert("Export failed", ex.Message, "OK");
		}
	}

	private async void OnNotesClicked(object? sender, EventArgs e)
	{
		await Navigation.PushAsync(new NotesPage(_service));
	}

	private async void OnDiagnosticsClicked(object? sender, EventArgs e)
	{
		await Navigation.PushAsync(new DiagnosticsPage(_service));
	}

	private async void OnSummaryClicked(object? sender, EventArgs e)
	{
		try
		{
			await Navigation.PushAsync(new SummaryPage(_currentYear, _currentMonth, _service));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnSummaryClicked", ex);
			await DisplayAlert("Could not open summary", ex.Message, "OK");
		}
    }

	private async void OnImportClicked(object? sender, EventArgs e)
	{
		try
		{
			var pick = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Select structured export CSV"
			});

			if (pick is null)
			{
				return;
			}

			// Read lines from the picked file
			List<string> lines = new();
			await using (var stream = await pick.OpenReadAsync())
			using (var reader = new System.IO.StreamReader(stream))
			{
				while (!reader.EndOfStream)
				{
					lines.Add(await reader.ReadLineAsync() ?? string.Empty);
				}
			}

			var mode = await DisplayActionSheet("Import mode", "Cancel", null, "Replace Year", "Merge");
			if (string.IsNullOrWhiteSpace(mode) || mode == "Cancel")
			{
				return;
			}

			var replaceYear = mode == "Replace Year";
			await _service.ImportStructuredCsvForYearAsync(lines, _currentYear, replaceYear);
			await LoadYearAsync(_currentYear);
			await DisplayAlert("Import complete", "CSV import finished.", "OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnImportClicked", ex);
			await DisplayAlert("Import failed", ex.Message, "OK");
		}
	}

	private async void OnArchiveYearClicked(object? sender, EventArgs e)
	{
		try
		{
			var currentlyArchived = _archivedYears.Contains(_currentYear);
			var targetState = !currentlyArchived;
			var confirm = await DisplayAlert(
				targetState ? "Archive Year" : "Unarchive Year",
				targetState
					? $"Archive {_currentYear}? It will be hidden from normal year navigation."
					: $"Unarchive {_currentYear}? It will be visible in year navigation.",
				"Yes",
				"Cancel");

			if (!confirm)
			{
				return;
			}

			await _service.SetYearArchivedAsync(_currentYear, targetState);
			await RefreshAvailableYearsAsync();
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnArchiveYearClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void WarnDuplicateRecurringRules()
	{
		var duplicates = _service.FindDuplicateRecurringRules(_currentMonth);
		if (duplicates.Count == 0)
		{
			_lastDuplicateWarningKey = null;
			return;
		}

		var key = $"{_currentYear}:{_currentMonth}:{duplicates.Count}";
		if (string.Equals(_lastDuplicateWarningKey, key, StringComparison.Ordinal))
		{
			return;
		}
		_lastDuplicateWarningKey = key;

		var sample = duplicates.First().First();
		await DisplayAlert(
			"Duplicate recurring rules detected",
			$"Found {duplicates.Count} duplicate recurring rule group(s) for this month. Example: {sample.Name}. Consider deleting duplicates or merging manually.",
			"OK");
	}
}
