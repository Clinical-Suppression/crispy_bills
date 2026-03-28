using CrispyBills.Mobile.Android.Services;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace CrispyBills.Mobile.Android;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<IBillingRepository, BillingRepository>();
		builder.Services.AddSingleton<BillingService>();
		builder.Services.AddSingleton<LocalizationService>();
		builder.Services.AddSingleton<BiometricAuthService>();
		builder.Services.AddSingleton<AppLockService>();
		// Shell/Page instances must be fresh per window lifecycle on Android.
		// Reusing singleton visual trees can cause handler/context invalidation on recreation.
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<AppShell>();
		builder.Services.AddTransient<PinSetupPage>();
		builder.Services.AddTransient<UnlockPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
