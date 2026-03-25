using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class AppShell : Shell
{
	public AppShell(MainPage mainPage)
	{
		try
		{
			InitializeComponent();
		}
		catch (Exception ex)
		{
			StartupDiagnostics.AddIssue("AppShell.ctor", $"InitializeComponent failed: {ex.Message}");
			try { DiagnosticsLog.WriteSync("AppShell.ctor.InitializeComponent", ex); } catch { }
		}

		try
		{
			Items.Add(new ShellContent
			{
				Title = "Home",
				Content = mainPage,
				Route = "MainPage"
			});
		}
		catch (Exception ex)
		{
			StartupDiagnostics.AddIssue("AppShell.ctor", $"ShellContent init failed: {ex.Message}");
			try { DiagnosticsLog.WriteSync("AppShell.ctor.ShellContent", ex); } catch { }
			Items.Clear();
			Items.Add(new ShellContent
			{
				Title = "Startup error",
				Content = new ContentPage
				{
					Title = "Startup error",
					Content = new ScrollView
					{
						Content = new Label
						{
							Margin = new Thickness(16),
							LineBreakMode = LineBreakMode.WordWrap,
							Text = $"Failed to initialize main shell.{Environment.NewLine}{Environment.NewLine}{ex}"
						}
					}
				}
			});
		}
	}
}
