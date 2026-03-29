using System.Globalization;
using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class SettingsPage : ContentPage
{
	private readonly BillingService _service;
	private readonly LocalizationService _localization;
	private readonly AppLockService _appLockService;
	private readonly int _year;
	private readonly int _month;
	private readonly Func<Task> _reloadMainPage;
	private bool _languageInit;
	private bool _themeInit;
	private bool _soonInit;
	private bool _securityInit;

	private static readonly (string Label, string Code)[] LanguageOptions =
	[
		("System default", ""),
		("English (United States)", "en-US"),
		("English (United Kingdom)", "en-GB"),
		("Spanish (Spain)", "es-ES"),
		("French (France)", "fr-FR"),
		("German (Germany)", "de-DE")
	];
	private static readonly (string Label, AppTheme Theme)[] ThemeOptions =
	[
		("Follow system", AppTheme.Unspecified),
		("Light", AppTheme.Light),
		("Dark", AppTheme.Dark)
	];

	public SettingsPage(BillingService service, int year, int month, Func<Task> reloadMainPage, LocalizationService localization, AppLockService appLockService)
	{
		InitializeComponent();
		_service = service;
		_localization = localization;
		_appLockService = appLockService;
		_year = year;
		_month = month;
		_reloadMainPage = reloadMainPage;

		LanguagePicker.ItemsSource = LanguageOptions.Select(x => x.Label).ToList();
		_languageInit = true;
		LanguagePicker.SelectedIndex = 0;
		_languageInit = false;
		_ = InitLanguagePickerAsync();
		ThemeModePicker.ItemsSource = ThemeOptions.Select(x => x.Label).ToList();
		_themeInit = true;
		ThemeModePicker.SelectedIndex = ThemeOptions.ToList().FindIndex(x => x.Theme == Application.Current?.UserAppTheme);
		if (ThemeModePicker.SelectedIndex < 0) ThemeModePicker.SelectedIndex = 0;
		_themeInit = false;

		SoonValuePicker.ItemsSource = Enumerable.Range(1, 30).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
		SoonUnitPicker.ItemsSource = BillingService.SoonThresholdUnits().ToList();

		VersionLabel.Text = $"Version {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
		DataRootLabel.Text = $"Private app data: {Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills")}";

		DebugToggle.IsToggled = _service.IsDebugDestructiveDeletesEnabled();
		DeleteMonthButton.IsEnabled = DebugToggle.IsToggled;
		DeleteYearButton.IsEnabled = DebugToggle.IsToggled;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Shell.SetNavBarIsVisible(this, true);
		await InitSoonThresholdAsync();
		await RefreshSecurityAsync();
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

	private void OnDebugLabelTapped(object? sender, TappedEventArgs e)
	{
		DebugToggle.IsToggled = !DebugToggle.IsToggled;
	}

	private async void OnDeleteMonthClicked(object? sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Confirm delete month",
			$"Permanently delete all bills and income for month {_month} in {_year}? Forward copies of monthly recurring bills and weekly or bi-weekly series tied to this month (from this month through year-end) are removed too.",
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

	private async Task InitSoonThresholdAsync()
	{
		try
		{
			var threshold = await _service.GetSoonThresholdAsync();
			_soonInit = true;
			SoonValuePicker.SelectedItem = threshold.Value.ToString(CultureInfo.InvariantCulture);
			SoonUnitPicker.SelectedItem = threshold.Unit;
			_soonInit = false;
		}
		catch
		{
			_soonInit = false;
		}
	}

	private async Task RefreshSecurityAsync()
	{
		try
		{
			var hasPin = await _appLockService.HasPinAsync();
			var biometricAvailable = await _appLockService.IsBiometricAvailableAsync();
			var biometricEnabled = hasPin && await _appLockService.IsBiometricEnabledAsync();

			_securityInit = true;
			ConfigurePinButton.Text = hasPin ? "Change PIN" : "Set PIN";
			RemovePinButton.IsVisible = hasPin;
			BiometricRow.IsVisible = hasPin && biometricAvailable;
			BiometricSwitch.IsToggled = biometricEnabled;
			_securityInit = false;
		}
		catch
		{
			_securityInit = false;
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

	private void OnThemeModeChanged(object? sender, EventArgs e)
	{
		if (_themeInit || ThemeModePicker.SelectedIndex < 0)
		{
			return;
		}

		var selected = ThemeOptions[ThemeModePicker.SelectedIndex].Theme;
		if (Application.Current != null)
		{
			Application.Current.UserAppTheme = selected;
		}
		Preferences.Default.Set("app_theme_mode", selected.ToString());
	}

	private async void OnSoonThresholdChanged(object? sender, EventArgs e)
	{
		if (_soonInit || SoonValuePicker.SelectedItem is null || SoonUnitPicker.SelectedItem is null)
		{
			return;
		}

		try
		{
			var value = int.Parse(SoonValuePicker.SelectedItem.ToString()!, CultureInfo.InvariantCulture);
			var unit = SoonUnitPicker.SelectedItem.ToString() ?? BillingService.SoonThresholdUnitDays;
			await _service.SetSoonThresholdAsync(value, unit);
			await _reloadMainPage();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsSoonThreshold", ex);
			await DisplayAlert("Error", ex.Message, "OK");
		}
	}

	private async void OnConfigurePinClicked(object? sender, EventArgs e)
	{
		if (await _appLockService.HasPinAsync())
		{
			var verifyPage = new UnlockPage(_appLockService);
			await Navigation.PushModalAsync(verifyPage);
			var verified = await verifyPage.WaitForResultAsync();
			if (!verified)
			{
				return;
			}
		}

		var page = new PinSetupPage(_appLockService, allowSkip: false);
		await Navigation.PushModalAsync(page);
		await page.WaitForResultAsync();
		await RefreshSecurityAsync();
	}

	private async void OnRemovePinClicked(object? sender, EventArgs e)
	{
		var verifyPage = new UnlockPage(_appLockService);
		await Navigation.PushModalAsync(verifyPage);
		var verified = await verifyPage.WaitForResultAsync();
		if (!verified)
		{
			return;
		}

		var confirm = await DisplayAlert("Remove PIN", "Turn off the app PIN and biometric unlock?", "Remove", "Cancel");
		if (!confirm)
		{
			return;
		}

		if (!await _appLockService.DisablePinAsync())
		{
			await DisplayAlert("Could not update security", "Secure storage did not accept the change. Check device security settings and try again.", "OK");
		}

		await RefreshSecurityAsync();
	}

	private void OnBiometricRowTapped(object? sender, TappedEventArgs e)
	{
		BiometricSwitch.IsToggled = !BiometricSwitch.IsToggled;
	}

	private async void OnBiometricSwitchToggled(object? sender, ToggledEventArgs e)
	{
		if (_securityInit)
		{
			return;
		}

		try
		{
			_securityInit = true;
			if (!await _appLockService.SetBiometricEnabledAsync(e.Value))
			{
				BiometricSwitch.IsToggled = !e.Value;
				await DisplayAlert("Could not save", "Secure storage did not accept the biometric preference. Check device security settings and try again.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SettingsBiometricToggle", ex);
			await DisplayAlert("Error", ex.Message, "OK");
			await RefreshSecurityAsync();
		}
		finally
		{
			_securityInit = false;
		}
	}
}
