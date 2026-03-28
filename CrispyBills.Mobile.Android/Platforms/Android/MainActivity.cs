using System.Runtime.Versioning;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace CrispyBills.Mobile.Android;

[Activity(Theme = "@style/CrispySplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Bottom stop of PrimaryDarkHeaderBrush — replaces default black system status bar.</summary>
    const string StatusBarColorHex = "#204C8C";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Switch off splash theme so recents snapshots use the real app UI.
        SetTheme(Resource.Style.CrispyMainTheme);
        base.OnCreate(savedInstanceState);
        ApplySystemBarAppearance();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplySystemBarAppearance();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            ApplySystemBarAppearance();
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBarAppearance();
    }

    void ApplySystemBarAppearance()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
            return;

        var window = Window;
        if (window == null)
            return;

        // SdkInt is an int in the Java API; some bindings expose it as a numeric type — compare via int for API 35 guard.
        // On API 35+ these Window APIs are obsolete (CA1422); CrispyMainTheme already sets status/navigation colors.
        var sdk = (int)Build.VERSION.SdkInt;
        const int android15ApiLevel = 35;
        if (sdk < android15ApiLevel)
        {
#pragma warning disable CA1422 // SetStatusBarColor / SetNavigationBarColor — obsolete on API 35; not called when sdk >= 35
            var barColor = global::Android.Graphics.Color.ParseColor(StatusBarColorHex);
            window.SetStatusBarColor(barColor);

            if (sdk >= 29) // API 29+ — navigation bar color (avoid CA1416 on BuildVersionCodes.Q)
                window.SetNavigationBarColor(global::Android.Graphics.Color.ParseColor("#353A45"));
#pragma warning restore CA1422
        }

        // Light status-bar icons on our dark blue bar (clear LightStatusBars appearance).
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            ClearLightStatusBarAppearance(window);
    }

    [SupportedOSPlatform("android30.0")]
    static void ClearLightStatusBarAppearance(global::Android.Views.Window window)
    {
        window.InsetsController?.SetSystemBarsAppearance(0, (int)WindowInsetsControllerAppearance.LightStatusBars);
    }
}
