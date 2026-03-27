using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using CrispyBills.Core.Security;
using Microsoft.Maui.Graphics;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CrispyBills.Mobile.Android;

public partial class MainPage : ContentPage
{
	private readonly BillingService _service;
	private readonly LocalizationService _localization;
	private readonly AppLockService _appLockService;
	private bool _loaded;
	private bool _startupDiagnosticsShown;
	private string? _lastDuplicateWarningKey;
	private decimal _incomeAmount;
	private string _incomePayPeriod = BillingService.IncomePayPeriodMonthly;
	private bool _incomeCardEditable;
	private readonly Dictionary<Guid, CancellationTokenSource> _billHoldTokens = new();
	private CancellationTokenSource? _incomeHoldToken;
	private const int HoldToEditMs = 1000;
	private int _soonThresholdValue = 7;
	private string _soonThresholdUnit = BillingService.SoonThresholdUnitDays;
	private bool _appLockFlowActive;

	private int _currentYear = DateTime.Today.Year;
	private int _currentMonth = DateTime.Today.Month;
	private List<int> _availableYears = new();
	private HashSet<int> _archivedYears = new();

	private readonly ObservableCollection<BillListItem> _visibleBills = new();
	private List<BillItem> _monthBills = new();
	private BillSortMode _billSortMode = BillSortMode.DueDate;

	private enum BillSortMode
	{
		DueDate,
		Alphabetical,
		Amount,
		Status,
		Category
	}

	public MainPage(BillingService service, LocalizationService localization, AppLockService appLockService)
	{
		InitializeComponent();
		_service = service;
		_localization = localization;
		_appLockService = appLockService;

		CategoryPicker.ItemsSource = new List<string> { "All categories" };
		CategoryPicker.SelectedIndex = 0;
		SortPicker.ItemsSource = new List<string> { "Due Date", "Alphabetical", "Amount", "Status", "Category" };
		SortPicker.SelectedIndex = 0;

		BillsCollection.ItemsSource = _visibleBills;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		try
		{
			if (!_loaded)
			{
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

			_ = EnsureAppLockFlowSafeAsync();
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"An error occurred during startup: {ex.Message}", "OK");
		}
	}

	private async Task EnsureAppLockFlowSafeAsync()
	{
		try
		{
			await EnsureAppLockFlowAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("EnsureAppLockFlow", ex);
		}
	}

	private async Task EnsureAppLockFlowAsync()
	{
		if (_appLockFlowActive)
		{
			return;
		}

		_appLockFlowActive = true;
		try
		{
			if (await _appLockService.ShouldRequireUnlockAsync())
			{
				var unlockPage = new UnlockPage(_appLockService);
				await Navigation.PushModalAsync(unlockPage);
				var unlockTask = unlockPage.WaitForResultAsync();
				var completed = await Task.WhenAny(unlockTask, Task.Delay(TimeSpan.FromSeconds(45)));
				if (completed != unlockTask)
				{
					await DiagnosticsLog.WriteAsync("EnsureAppLockFlow", new TimeoutException("Unlock timeout reached; keeping lock screen active."));
					if (AppLockTimeoutPolicy.ShouldDismissModalOnTimeout(AppLockFlowStep.Unlock))
					{
						try { await Navigation.PopModalAsync(); } catch { }
					}
					return;
				}

				var unlocked = unlockTask.Result;
				if (!unlocked)
				{
					return;
				}
			}

			if (!await _appLockService.ShouldOfferPinSetupPromptAsync())
			{
				return;
			}

			var enablePin = await DisplayAlert(
				"Protect your bills?",
				"Would you like to use a 4-digit PIN to protect the app when your phone has been sitting idle?",
				"Set PIN",
				"Not now");

			if (!enablePin)
			{
				await _appLockService.MarkPinSetupPromptHandledAsync();
				return;
			}

			var setupPage = new PinSetupPage(_appLockService, allowSkip: true);
			await Navigation.PushModalAsync(setupPage);
			var setupTask = setupPage.WaitForResultAsync();
			var setupCompleted = await Task.WhenAny(setupTask, Task.Delay(TimeSpan.FromSeconds(45)));
			if (setupCompleted == setupTask)
			{
				_ = setupTask.Result;
			}
			else
			{
				if (AppLockTimeoutPolicy.ShouldDismissModalOnTimeout(AppLockFlowStep.PinSetup))
				{
					try { await Navigation.PopModalAsync(); } catch { }
				}
			}
			await _appLockService.MarkPinSetupPromptHandledAsync();
		}
		finally
		{
			_appLockFlowActive = false;
		}
	}

	private async void OnBillsRefreshing(object? sender, EventArgs e)
	{
		try
		{
			await LoadYearAsync(_currentYear);
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnBillsRefreshing", ex);
			await DisplayAlert("Refresh failed", ex.Message, "OK");
		}
		finally
		{
			BillsRefreshView.IsRefreshing = false;
		}
	}

	private static BillListItem? BillFromElement(object? sender)
	{
		if (sender is not BindableObject b)
		{
			return null;
		}

		var p = b as Element;
		while (p != null)
		{
			if (p is VisualElement ve && ve.BindingContext is BillListItem item)
			{
				return item;
			}

			p = p.Parent;
		}

		return null;
	}

	private async void OnSwipeTogglePaid(object? sender, EventArgs e)
	{
		var item = BillFromElement(sender);
		if (item is null)
		{
			return;
		}

		try
		{
			await _service.TogglePaidAsync(_currentMonth, item.Id);
			await ReloadMonthAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnSwipeTogglePaid", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnSwipeDelete(object? sender, EventArgs e)
	{
		var item = BillFromElement(sender);
		if (item is null)
		{
			return;
		}

		await DeleteBillByIdAsync(item.Id);
	}

	private void OnBillPointerPressed(object? sender, PointerEventArgs e)
	{
		if (sender is not BindableObject b || b.BindingContext is not BillListItem item)
		{
			return;
		}
		StartBillHold(item.Id);
	}

	private void OnBillPointerReleased(object? sender, PointerEventArgs e)
	{
		if (sender is not BindableObject b || b.BindingContext is not BillListItem item)
		{
			return;
		}
		StopBillHold(item.Id);
	}

	private void StartBillHold(Guid billId)
	{
		if (_billHoldTokens.TryGetValue(billId, out var existing))
		{
			existing.Cancel();
			existing.Dispose();
		}

		var cts = new CancellationTokenSource();
		_billHoldTokens[billId] = cts;
		_ = BeginBillHoldEditAsync(billId, cts.Token);
	}

	private void StopBillHold(Guid billId)
	{
		if (_billHoldTokens.TryGetValue(billId, out var cts))
		{
			cts.Cancel();
			cts.Dispose();
			_billHoldTokens.Remove(billId);
		}
	}

	private async Task BeginBillHoldEditAsync(Guid billId, CancellationToken token)
	{
		try
		{
			await Task.Delay(HoldToEditMs, token);
			if (token.IsCancellationRequested)
			{
				return;
			}

			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				await EditBillByIdAsync(billId);
			});
		}
		catch (OperationCanceledException)
		{
			// Ignore canceled hold.
		}
	}

	private void OnBillPanUpdated(object? sender, PanUpdatedEventArgs e)
	{
		if (sender is not BindableObject b || b.BindingContext is not BillListItem item)
		{
			return;
		}

		switch (e.StatusType)
		{
			case GestureStatus.Started:
				StartBillHold(item.Id);
				break;
			case GestureStatus.Canceled:
			case GestureStatus.Completed:
				StopBillHold(item.Id);
				break;
		}
	}

	private void OnBillSwipeStarted(object? sender, SwipeStartedEventArgs e)
	{
		foreach (var kv in _billHoldTokens.ToList())
		{
			kv.Value.Cancel();
			kv.Value.Dispose();
			_billHoldTokens.Remove(kv.Key);
		}
	}

	private async Task EditBillByIdAsync(Guid id)
	{
		var draft = _service.GetEditableBillDraft(_currentMonth, id);
		if (draft is null)
		{
			return;
		}

		try
		{
			var editor = new BillEditorPage(draft, _currentYear, _currentMonth, _service.Categories());
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
			await DiagnosticsLog.WriteAsync("EditBillByIdAsync", ex);
			await DisplayAlert("Could not open bill editor", ex.Message, "OK");
		}
	}

	private async Task DeleteBillByIdAsync(Guid id)
	{
		var existing = _monthBills.FirstOrDefault(x => x.Id == id);
		if (existing is null)
		{
			return;
		}

		var warning = (existing.IsRecurring || existing.RecurrenceGroupId.HasValue)
			? "Delete this bill from the current occurrence onward?"
			: "Delete this bill?";

		var confirm = await DisplayAlert("Delete Bill", warning, "Delete", "Cancel");
		if (!confirm)
		{
			return;
		}

		try
		{
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
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("DeleteBillByIdAsync", ex);
			await DisplayAlert("Error", ex.Message, "OK");
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

	private async Task ReloadMonthAsync()
	{
		YearButton.Text = _archivedYears.Contains(_currentYear)
			? $"{_currentYear} (Archived)"
			: _currentYear.ToString();
		MonthButton.Text = MonthNames.Name(_currentMonth);
		var yearIdx = _availableYears.IndexOf(_currentYear);
		PrevYearButton.IsEnabled = yearIdx > 0;
		NextYearButton.IsEnabled = yearIdx >= 0 && yearIdx < _availableYears.Count - 1;

		var periodStart = new DateTime(_currentYear, _currentMonth, 1);
		_monthBills = _service.GetBills(_currentMonth).Select(x =>
		{
			var c = x.Clone();
			c.ContextPeriodStart = periodStart;
			return c;
		}).ToList();
		(_soonThresholdValue, _soonThresholdUnit) = await _service.GetSoonThresholdAsync();
		await RefreshIncomeControlsAsync();

		UpdateCategoryFilter();
		ApplyFilters();
		UpdateSummaryCards();
		UpdateAddButtonText();
		WarnDuplicateRecurringRules();
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
			 || (b.Name ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
			 || (b.Category ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase))
			&& (category == "All categories" || (b.Category ?? string.Empty).Equals(category, StringComparison.OrdinalIgnoreCase)));

		IEnumerable<BillItem> sorted = _billSortMode switch
		{
			BillSortMode.Alphabetical => filtered.OrderBy(x => x.Name),
			BillSortMode.Amount => filtered.OrderByDescending(x => x.Amount).ThenBy(x => x.Name),
			BillSortMode.Status => filtered.OrderBy(x => GetStatusSortKey(x)).ThenBy(x => x.DueDate).ThenBy(x => x.Name),
			BillSortMode.Category => filtered.OrderBy(x => x.Category).ThenBy(x => x.DueDate).ThenBy(x => x.Name),
			_ => filtered.OrderBy(x => x.DueDate).ThenBy(x => x.Name)
		};

		_visibleBills.Clear();
		foreach (var bill in sorted)
		{
			_visibleBills.Add(new BillListItem(
				bill,
				_localization.FormatCurrency(bill.Amount),
				BillingService.IsBillSoon(bill, _soonThresholdValue, _soonThresholdUnit)));
		}
	}

	private int GetStatusSortKey(BillItem bill)
	{
		if (bill.IsPaid)
		{
			return 0;
		}

		var isSoon = BillingService.IsBillSoon(bill, _soonThresholdValue, _soonThresholdUnit);
		if (bill.IsPastDue)
		{
			return 3;
		}
		return isSoon ? 2 : 1;
	}

	private void UpdateSummaryCards()
	{
		var summary = _service.GetMonthSummary(_currentMonth);
		UnpaidLabel.Text = _localization.FormatCurrency(summary.unpaid);
		RemainingLabel.Text = _localization.FormatCurrency(summary.remaining);
		BillCountLabel.Text = summary.billCount.ToString();
		RemainingLabel.TextColor = summary.remaining >= 0 ? GetResourceColor("Success", "#16A34A") : GetResourceColor("Danger", "#991B1B");

		var income = _service.GetIncome(_currentMonth);
		var categoryTotals = _service.GetCategoryTotals(_currentMonth).Where(x => x.total > 0m).OrderByDescending(x => x.total).ToList();
		var palette = GetSummaryPalette();
		var segments = new List<(decimal amount, Color color)>();
		for (var i = 0; i < categoryTotals.Count; i++)
		{
			segments.Add((categoryTotals[i].total, palette[i % palette.Count]));
		}
		IncomeExpenseBarView.Drawable = new IncomeExpenseBarDrawable(
			income,
			segments,
			GetResourceColor("Gray200", "#E2E8F0"));
	}

	private List<Color> GetSummaryPalette()
	{
		var keys = new[] { "Magenta", "Success", "Info", "Tertiary", "Danger", "Primary", "SecondaryDarkText", "PrimaryDark" };
		return keys.Select(k => GetResourceColor(k, "#2563EB")).ToList();
	}

	private async Task RefreshIncomeControlsAsync()
	{
		var (amount, payPeriod) = await _service.GetIncomeEntryAsync(_currentMonth);
		_incomeAmount = amount;
		_incomePayPeriod = payPeriod;
		_incomeCardEditable = amount > 0m;
		IncomeValueLabel.Text = amount > 0m
			? _localization.FormatCurrency(amount)
			: "Enter Income";
	}

	private void UpdateAddButtonText()
	{
		var hasBills = _monthBills.Count > 0;
		BillsListAddButton.IsVisible = hasBills;
		if (hasBills)
		{
			BillsListAddButton.Text = "Add bill(s)";
		}

		EmptyAddBillsButton.Text = "Add your first bill(s)";
	}

	private async Task PromptAndSaveIncomeAsync()
	{
		var amountInput = await DisplayPromptAsync(
			"Income",
			"Enter income amount",
			accept: "Save",
			cancel: "Cancel",
			initialValue: _incomeAmount > 0m ? _incomeAmount.ToString("0.00", _localization.CurrentCulture) : string.Empty,
			keyboard: Keyboard.Numeric);
		if (amountInput is null)
		{
			return;
		}

		if (!decimal.TryParse(amountInput, NumberStyles.Currency, _localization.CurrentCulture, out var income)
			&& !decimal.TryParse(amountInput, NumberStyles.Currency, CultureInfo.InvariantCulture, out income))
		{
			await DisplayAlert("Invalid Income", "Enter a valid number.", "OK");
			return;
		}

		var periodSelection = await DisplayActionSheet(
			"Income timeframe",
			"Cancel",
			null,
			"Monthly",
			"Weekly",
			"Bi-weekly");
		if (periodSelection == "Cancel")
		{
			return;
		}

		var payPeriod = periodSelection switch
		{
			"Weekly" => BillingService.IncomePayPeriodWeekly,
			"Bi-weekly" => BillingService.IncomePayPeriodBiWeekly,
			_ => BillingService.IncomePayPeriodMonthly
		};

		await _service.SetIncomeAsync(_currentMonth, income, payPeriod);
		await ReloadMonthAsync();
	}

	private static Color GetResourceColor(string key, string fallback)
	{
		if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
		{
			return c;
		}

		return Color.FromArgb(fallback);
	}

	private sealed class IncomeExpenseBarDrawable : IDrawable
	{
		private readonly decimal _income;
		private readonly IReadOnlyList<(decimal amount, Color color)> _segments;
		private readonly Color _trackColor;

		public IncomeExpenseBarDrawable(decimal income, IReadOnlyList<(decimal amount, Color color)> segments, Color trackColor)
		{
			_income = income;
			_segments = segments;
			_trackColor = trackColor;
		}

		public void Draw(ICanvas canvas, RectF dirtyRect)
		{
			canvas.Antialias = true;
			var pad = 2f;
			var track = new RectF(dirtyRect.X + pad, dirtyRect.Y + 6f, dirtyRect.Width - (pad * 2f), dirtyRect.Height - 12f);
			canvas.FillColor = _trackColor;
			canvas.FillRoundedRectangle(track, 6f);

			var totalExpenses = _segments.Sum(x => x.amount);
			if (_income <= 0m && totalExpenses <= 0m)
			{
				return;
			}

			var maxRange = _income > 0m ? _income : totalExpenses;
			if (maxRange <= 0m)
			{
				return;
			}

			var cursorX = track.X;
			foreach (var segment in _segments.Where(x => x.amount > 0m))
			{
				var width = (float)(track.Width * (double)(segment.amount / maxRange));
				if (width <= 0f)
				{
					continue;
				}

				canvas.FillColor = segment.color;
				canvas.FillRectangle(new RectF(cursorX, track.Y, width, track.Height));
				cursorX += width;
			}
		}
	}

	private async void OnPrevYearClicked(object? sender, EventArgs e)
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

	private async void OnNextYearClicked(object? sender, EventArgs e)
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

	private async void OnPrevMonthClicked(object? sender, EventArgs e)
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

	private async void OnNextMonthClicked(object? sender, EventArgs e)
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

	private async void OnMonthSelectClicked(object? sender, EventArgs e)
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

	private async void OnYearSelectClicked(object? sender, EventArgs e)
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
		try { ApplyFilters(); } catch { }
	}

	private void OnCategoryChanged(object? sender, EventArgs e)
	{
		try { ApplyFilters(); } catch { }
	}

	private void OnSortChanged(object? sender, EventArgs e)
	{
		_billSortMode = SortPicker.SelectedItem?.ToString() switch
		{
			"Alphabetical" => BillSortMode.Alphabetical,
			"Amount" => BillSortMode.Amount,
			"Status" => BillSortMode.Status,
			"Category" => BillSortMode.Category,
			_ => BillSortMode.DueDate
		};
		try { ApplyFilters(); } catch { }
	}

	private async void OnIncomeExpenseBarTapped(object? sender, TappedEventArgs e)
	{
		await Navigation.PushAsync(new SummaryPage(_currentYear, _currentMonth, _service, _localization));
	}

	private async void OnIncomeCardTapped(object? sender, TappedEventArgs e)
	{
		try
		{
			if (_incomeCardEditable)
			{
				// Existing income requires hold-to-edit.
				return;
			}
			await PromptAndSaveIncomeAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnIncomeCardTapped", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private void OnIncomeCardPointerPressed(object? sender, PointerEventArgs e)
	{
		if (!_incomeCardEditable)
		{
			return;
		}
		StartIncomeHold();
	}

	private void OnIncomeCardPointerReleased(object? sender, PointerEventArgs e)
	{
		StopIncomeHold();
	}

	private void StartIncomeHold()
	{
		_incomeHoldToken?.Cancel();
		_incomeHoldToken?.Dispose();
		_incomeHoldToken = new CancellationTokenSource();
		_ = BeginIncomeHoldEditAsync(_incomeHoldToken.Token);
	}

	private void StopIncomeHold()
	{
		_incomeHoldToken?.Cancel();
		_incomeHoldToken?.Dispose();
		_incomeHoldToken = null;
	}

	private void OnIncomeCardPanUpdated(object? sender, PanUpdatedEventArgs e)
	{
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				StartIncomeHold();
				break;
			case GestureStatus.Canceled:
			case GestureStatus.Completed:
				StopIncomeHold();
				break;
		}
	}

	private async Task BeginIncomeHoldEditAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(HoldToEditMs, token);
			if (token.IsCancellationRequested)
			{
				return;
			}
			await MainThread.InvokeOnMainThreadAsync(async () => await PromptAndSaveIncomeAsync());
		}
		catch (OperationCanceledException)
		{
			// Ignore canceled hold.
		}
	}

	private async void OnAddClicked(object? sender, EventArgs e)
	{
		try
		{
			await Navigation.PushAsync(new BulkBillsPage(_service, _currentYear, _currentMonth, _service.Categories().ToList(), ReloadMonthAsync));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnAddClicked", ex);
			await DisplayAlert("Could not open add bill", ex.Message, "OK");
		}
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

		try
		{
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
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnRolloverClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnNewYearClicked(object? sender, EventArgs e)
	{
		try
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
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnNewYearClicked", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnImportClicked(object? sender, EventArgs e)
	{
		try
		{
			var preferred = await TryPickExportFromDefaultFolderAsync();
			FileResult? pick = preferred;
			if (pick is null)
			{
				pick = await FilePicker.Default.PickAsync(new PickOptions
				{
					PickerTitle = "Select CSV export"
				});
			}

			if (pick is null)
			{
				return;
			}

			var lines = new List<string>();
			await using (var stream = await pick.OpenReadAsync())
			using (var reader = new StreamReader(stream))
			{
				while (!reader.EndOfStream)
				{
					lines.Add(await reader.ReadLineAsync() ?? string.Empty);
				}
			}

			if (lines.Count == 0)
			{
				await DisplayAlert("Import", "The file is empty.", "OK");
				return;
			}

			if (MobileStructuredReportCsv.LooksLikeDesktopStructuredReport(lines))
			{
				var package = MobileStructuredReportCsv.Parse(
					lines.ToArray(),
					msg => DiagnosticsLog.WriteSync("ImportStructuredCsv", msg),
					writeDiagnostics: true,
					dataRootForDiagnostics: _service.DataRoot);

				if (package.Years.Count == 0 && !package.HasNotesSection)
				{
					await DisplayAlert("Import", "No importable year, month, or notes were found in this file.", "OK");
					return;
				}

				var picker = new ImportSelectionPage(package);
				await Navigation.PushModalAsync(picker);
				var choice = await picker.WaitForResultAsync();
				if (choice is null)
				{
					return;
				}

				var ok = await DisplayAlert(
					"Confirm import",
					"Selected months will replace existing data for those months. A database backup is created per affected year when possible. Continue?",
					"Import",
					"Cancel");
				if (!ok)
				{
					return;
				}

				var result = await _service.ApplyStructuredReportImportAsync(
					package,
					choice.SelectedMonthsByYear,
					choice.ImportNotes);
				await RefreshAvailableYearsAsync();
				await LoadYearAsync(_currentYear);
				var notePart = result.NotesImported ? " Notes were updated." : string.Empty;
				await DisplayAlert(
					"Import complete",
					$"Imported {result.ImportedBillCount} bill(s) across {result.ImportedMonthCount} month(s).{notePart}",
					"OK");
				return;
			}

			var mode = await DisplayActionSheet("Portable import (single-year CSV)", "Cancel", null, "Replace Year", "Merge");
			if (string.IsNullOrWhiteSpace(mode) || mode == "Cancel")
			{
				return;
			}

			var replaceYear = mode == "Replace Year";
			var report = await _service.ImportStructuredCsvForYearAsync(lines, _currentYear, replaceYear);
			await LoadYearAsync(_currentYear);
			await DisplayAlert(
				"Import complete",
				$"CSV import finished.\n\nImported bills: {report.ImportedBillRows}\nImported income rows: {report.ImportedIncomeRows}\nSkipped malformed: {report.SkippedMalformed}\nSkipped invalid month: {report.SkippedInvalidMonth}\nSkipped invalid year: {report.SkippedInvalidYear}",
				"OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnImportClicked", ex);
			await DisplayAlert("Import failed", ex.Message, "OK");
		}
	}

	private async Task<FileResult?> TryPickExportFromDefaultFolderAsync()
	{
		try
		{
			var defaultFolder = _service.DataRoot;
			if (!Directory.Exists(defaultFolder))
			{
				return null;
			}

			var files = Directory
				.GetFiles(defaultFolder, "*.csv", SearchOption.TopDirectoryOnly)
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.Take(10)
				.ToList();
			if (files.Count == 0)
			{
				return null;
			}

			var display = files
				.Select(x => Path.GetFileName(x))
				.ToArray();
			var selected = await DisplayActionSheet("Import from exports folder", "Browse...", null, display);
			if (string.IsNullOrWhiteSpace(selected) || selected == "Browse...")
			{
				return null;
			}

			var selectedPath = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), selected, StringComparison.OrdinalIgnoreCase));
			if (string.IsNullOrWhiteSpace(selectedPath))
			{
				return null;
			}

			return new FileResult(selectedPath);
		}
		catch
		{
			return null;
		}
	}

	private async void OnExportClicked(object? sender, EventArgs e)
	{
		try
		{
			var export = await _service.ExportFullReportCsvWithLocationsAsync();
			var message = $"Full report saved to app storage:\n{export.PrivatePath}";
			if (!string.IsNullOrWhiteSpace(export.PublicPath))
			{
				message += $"\n\nPublic copy saved to:\n{export.PublicPath}";
			}
			await DisplayAlert("Exported", message, "OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnExportClicked", ex);
			await DisplayAlert("Export failed", ex.Message, "OK");
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

	private async void OnSummaryClicked(object? sender, EventArgs e)
	{
		try
		{
			await Navigation.PushAsync(new SummaryPage(_currentYear, _currentMonth, _service, _localization));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnSummaryClicked", ex);
			await DisplayAlert("Could not open summary", ex.Message, "OK");
		}
	}

	private async void OnNotesClicked(object? sender, EventArgs e)
	{
		try
		{
			await Navigation.PushAsync(new NotesPage(_service));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnNotesClicked", ex);
			await DisplayAlert("Could not open notes", ex.Message, "OK");
		}
	}

	private async void OnSettingsClicked(object? sender, EventArgs e)
	{
		try
		{
			await Navigation.PushAsync(new SettingsPage(_service, _currentYear, _currentMonth, ReloadMonthAsync, _localization, _appLockService));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("OnSettingsClicked", ex);
			await DisplayAlert("Could not open settings", ex.Message, "OK");
		}
	}

	private async void WarnDuplicateRecurringRules()
	{
		try
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
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("WarnDuplicateRecurringRules", ex);
		}
	}
}
