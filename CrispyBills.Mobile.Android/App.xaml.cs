using CrispyBills.Mobile.Android.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CrispyBills.Mobile.Android;

public partial class App : Application
{
	private readonly IServiceProvider _services;
	private readonly AppLockService _appLockService;

	/// <summary>
	/// Resolves the root service provider from DI so <see cref="CreateWindow"/> does not depend on
	/// <c>IPlatformApplication.Current?.Services</c>, which can still be null on Android when the first window is created.
	/// </summary>
	public App(IServiceProvider services)
	{
		_services = services ?? throw new ArgumentNullException(nameof(services));
		_appLockService = _services.GetRequiredService<AppLockService>();
		try
		{
			InitializeComponent();
		}
		catch (Exception ex)
		{
			StartupDiagnostics.AddIssue("App.ctor", $"InitializeComponent failed: {ex.Message}");
			try { DiagnosticsLog.WriteSync("App.ctor.InitializeComponent", ex); } catch { }
			Resources = new ResourceDictionary();
		}
	}

	protected override void OnStart()
	{
		base.OnStart();
		Dispatcher?.StartTimer(TimeSpan.FromMinutes(15), () =>
		{
			_ = RoutineBackupTickAsync();
			return true;
		});
	}

	protected override void OnSleep()
	{
		base.OnSleep();
		_appLockService.NoteAppBackgrounded();
		_ = RoutineBackupTickAsync();
	}

	private async Task RoutineBackupTickAsync()
	{
		try
		{
			var billing = _services.GetRequiredService<BillingService>();
			await billing.BackupCurrentYearDatabaseFileAsync();
		}
		catch
		{
			// best-effort only
		}
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		StartupDiagnostics.Clear();
		try
		{
			// Build the root shell up-front to avoid page swapping races
			// during window handler initialization on Android.
			return new Window(_services.GetRequiredService<AppShell>());
		}
		catch (Exception ex)
		{
			return BuildErrorWindow(ex);
		}
	}

	private static Window BuildErrorWindow(Exception ex)
	{
		var body = $"The app could not start normally.{Environment.NewLine}{Environment.NewLine}{ex}";
		try
		{
			DiagnosticsLog.WriteSync("App.CreateWindow", ex);
			body += $"{Environment.NewLine}{Environment.NewLine}Details written to:{Environment.NewLine}{DiagnosticsLog.CurrentLogPath}";
		}
		catch
		{
			// DiagnosticsLog itself may fail if MAUI services aren't ready; ignore to avoid cascading crash.
		}

#if ANDROID
		try { global::Android.Util.Log.Error("CrispyBills", ex.ToString()); } catch { }
#endif

		var label = new Label
		{
			Text = body,
			Margin = new Thickness(16),
			LineBreakMode = LineBreakMode.WordWrap
		};
		return new Window(new ContentPage { Title = "Startup error", Content = new ScrollView { Content = label } });
	}
}
