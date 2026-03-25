using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class DiagnosticsPage : ContentPage
{
	private readonly BillingService? _service;

	public DiagnosticsPage()
	{
		InitializeComponent();
	}

	public DiagnosticsPage(BillingService service)
	{
		InitializeComponent();
		_service = service;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	private async void OnRefreshClicked(object? sender, EventArgs e)
	{
		await LoadAsync();
	}

	private async Task LoadAsync()
	{
		var dataRoot = Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills");
		InfoLabel.Text = $"Data root: {dataRoot}\nLog: {DiagnosticsLog.CurrentLogPath}";
		var startupIssues = StartupDiagnostics.GetIssues();
		StartupIssuesLabel.Text = startupIssues.Count == 0
			? "Startup issues: none"
			: "Startup issues:\n" + string.Join("\n", startupIssues.Take(8));
		var log = await DiagnosticsLog.ReadCurrentAsync();
		LogEditor.Text = string.IsNullOrWhiteSpace(log) ? "No diagnostics logged yet." : log;
	}

	private async void OnIntegrityRepairClicked(object? sender, EventArgs e)
	{
		if (_service is null)
		{
			await DisplayAlert("Not available", "Open Diagnostics from the main app for full repair.", "OK");
			return;
		}

		try
		{
			var report = await _service.RunIntegrityCheckAndRepairAsync();
			await DisplayAlert("Integrity check complete", report.ToString(), "OK");
			await LoadAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("DiagnosticsIntegrityRepair", ex);
			await DisplayAlert("Integrity repair failed", ex.Message, "OK");
		}
	}
}
