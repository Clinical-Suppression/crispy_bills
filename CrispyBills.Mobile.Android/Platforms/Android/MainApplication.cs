using Android.App;
using Android.Runtime;
using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

[Application]
public class MainApplication : MauiApplication
{
	private static bool _globalHandlersRegistered;

	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();
		RegisterGlobalExceptionHandlersOnce();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	private static void RegisterGlobalExceptionHandlersOnce()
	{
		if (_globalHandlersRegistered)
		{
			return;
		}

		_globalHandlersRegistered = true;

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex)
			{
				DiagnosticsLog.WriteSync("AppDomain.UnhandledException", ex);
				global::Android.Util.Log.Error("CrispyBills", ex.ToString());
			}
			else
			{
				DiagnosticsLog.WriteSync("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "(null)", "Fatal");
				global::Android.Util.Log.Error("CrispyBills", e.ExceptionObject?.ToString() ?? "null");
			}
		};

		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			DiagnosticsLog.WriteSync("TaskScheduler.UnobservedTaskException", e.Exception);
			global::Android.Util.Log.Error("CrispyBills", e.Exception.ToString());
			e.SetObserved();
		};
	}
}
