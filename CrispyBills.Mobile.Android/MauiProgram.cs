using CrispyBills.Mobile.Android.Services;
using Microsoft.Extensions.Logging;

namespace CrispyBills.Mobile.Android;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<IBillingRepository, BillingRepository>();
		builder.Services.AddSingleton<BillingService>();
		builder.Services.AddSingleton<LocalizationService>();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddSingleton<AppShell>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
