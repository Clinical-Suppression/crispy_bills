using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace CrispyBills.Mobile.Android.Models;

/// <summary>
/// Resolves light vs dark for UI colors when <see cref="Application.RequestedTheme"/> is
/// <see cref="AppTheme.Unspecified"/> (follow system). Matches effective app appearance with
/// <see cref="AppInfo.RequestedTheme"/> as fallback.
/// </summary>
public static class AppThemeHelper
{
	public static bool IsEffectiveDarkTheme()
	{
		var app = Application.Current;
		if (app is null)
		{
			return AppInfo.RequestedTheme == AppTheme.Dark;
		}

		var t = app.RequestedTheme;
		if (t == AppTheme.Dark)
		{
			return true;
		}

		if (t == AppTheme.Light)
		{
			return false;
		}

		var user = app.UserAppTheme;
		if (user == AppTheme.Dark)
		{
			return true;
		}

		if (user == AppTheme.Light)
		{
			return false;
		}

		return AppInfo.RequestedTheme == AppTheme.Dark;
	}
}
