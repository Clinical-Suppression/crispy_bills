using System.Globalization;
using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class SettingsPage : ContentPage
{
	private readonly BillingService _service;
	private readonly LocalizationService _localization;
	private readonly int _year;
	private readonly int _month;
	private readonly Func<Task> _reloadMainPage;
	private bool _languageInit;

	private static readonly (string Label, string Code)[] LanguageOptions =
	[
		("System default", ""),
		("English (United States)", "en-US"),
		("English (United Kingdom)", "en-GB"),
		("Spanish (Spain)", "es-ES"),
		("French (France)", "fr-FR"),
		("German (Germany)", "de-DE")
	];

	public SettingsPage(BillingService service, int year, int month, Func<Task> reloadMainPage, LocalizationService localization)
	{
		InitializeComponent();
		_service = service;
		_localization = localization;
		_year = year;
		_month = month;
		_reloadMainPage = reloadMainPage;

		LanguagePicker.ItemsSource = LanguageOptions.Select(x => x.Label).ToList();
		_languageInit = true;
		LanguagePicker.SelectedIndex = 0;
		_languageInit = false;
		_ = InitLanguagePickerAsync();

		VersionLabel.Text = $"Version {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
		DataRootLabel.Text = $"Data: {Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills")}";

		DebugToggle.IsToggled = _service.IsDebugDestructiveDeletesEnabled();
		DeleteMonthButton.IsEnabled = DebugToggle.IsToggled;
		DeleteYearButton.IsEnabled = DebugToggle.IsToggled;
	}

	private async void OnDebugToggleToggled(object? sender, ToggledEventArgs e)
	{
		if (!e.Value)
		{
			_service.SetDebugDestructiveDeletesEnabled(false);
			DeleteMonthButton.IsEnabled = false;
			DeleteYearButton.IsEnabled = false;
			return;
		}

		var ok = await DisplayAlert("Enable destructive tools",
			"This allows permanent deletion of month or year data. A backup is created when possible. Enable for this session?",
			"Enable",
			"Cancel");
		if (!ok)
		{
			DebugToggle.IsToggled = false;
			return;
		}

		_service.SetDebugDestructiveDeletesEnabled(true);
		DeleteMonthButton.IsEnabled = true;
		DeleteYearButton.IsEnabled = true;
	}

	private async void OnDeleteMonthClicked(object? sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Confirm delete month",
			$"Permanently delete all bills and income for month {_month} in {_year}?",
			"Delete",
			"Cancel");
		if (!confirm)
		{
			return;
		}

		try
		{
			var ok = await _service.DeleteMonthAsync(_month);
			if (!ok)
			{
				await DisplayAlert("Failed", "Month deletion did not complete. See diagnostics.", "OK");
			}

			await _reloadMainPage();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsDeleteMonth", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnDeleteYearClicked(object? sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Confirm delete year",
			$"Delete the database for year {_year}? This cannot be undone except from backups.",
			"Delete",
			"Cancel");
		if (!confirm)
		{
			return;
		}

		try
		{
			var ok = await _service.DeleteYearAsync(_year);
			if (!ok)
			{
				await DisplayAlert("Failed", "Year deletion did not complete. See diagnostics.", "OK");
			}

			await _reloadMainPage();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsDeleteYear", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnDiagnosticsClicked(object? sender, EventArgs e)
	{
		await Navigation.PushAsync(new DiagnosticsPage(_service));
	}

	private async void OnFixDueDatesClicked(object? sender, EventArgs e)
	{
		try
		{
			var n = await _service.NormalizeDueDatesForCurrentYearAsync();
			await _reloadMainPage();
			await DisplayAlert(
				"Due dates",
				n > 0
					? $"Normalized {n} due date(s) for {_year}. Unpaid carryovers before their month were kept."
					: $"No due date changes were needed for {_year}.",
				"OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsFixDueDates", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async Task InitLanguagePickerAsync()
	{
		try
		{
			var stored = await _localization.GetPersistedLanguageCodeAsync();
			var idx = 0;
			if (!string.IsNullOrEmpty(stored))
			{
				var found = Array.FindIndex(LanguageOptions, o => string.Equals(o.Code, stored, StringComparison.OrdinalIgnoreCase));
				if (found >= 0)
				{
					idx = found;
				}
			}

			_languageInit = true;
			LanguagePicker.SelectedIndex = idx;
			_languageInit = false;
		}
		catch
		{
			// ignore picker init failures
		}
	}

	private async void OnLanguageChanged(object? sender, EventArgs e)
	{
		if (_languageInit || LanguagePicker.SelectedIndex < 0)
		{
			return;
		}

		try
		{
			var opt = LanguageOptions[LanguagePicker.SelectedIndex];
			await _localization.SetCultureAsync(opt.Code);
			await _reloadMainPage();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsLanguage", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}
}
