namespace CrispyBills.Mobile.Android;

public partial class HelpPage : ContentPage
{
	public HelpPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Shell.SetNavBarIsVisible(this, true);
	}
}
