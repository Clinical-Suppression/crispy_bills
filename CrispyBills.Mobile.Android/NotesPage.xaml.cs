using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class NotesPage : ContentPage
{
	private bool _unsubscribed;
	private readonly BillingService _service;

	public NotesPage(BillingService service)
	{
		InitializeComponent();
		_service = service;
		NotesEditor.TextChanged += OnNotesChanged;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Shell.SetNavBarIsVisible(this, true);
		if (_unsubscribed)
		{
			NotesEditor.TextChanged += OnNotesChanged;
			_unsubscribed = false;
		}

		try
		{
			NotesEditor.Text = await _service.LoadNotesAsync();
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("NotesPage.OnAppearing", ex);
			await DisplayAlert("Load failed", "Could not load notes. Please try again.", "OK");
		}

		UpdateLineCount();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_unsubscribed)
		{
			NotesEditor.TextChanged -= OnNotesChanged;
			_unsubscribed = true;
		}
	}

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		try
		{
			await _service.SaveNotesAsync(NotesEditor.Text ?? string.Empty);
			await DisplayAlert("Saved", "Notes updated.", "OK");
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("NotesPage.OnSaveClicked", ex);
			await DisplayAlert("Save failed", $"Could not save notes: {ex.Message}", "OK");
		}
	}

	private void OnNotesChanged(object? sender, TextChangedEventArgs e)
	{
		UpdateLineCount();
	}

	private void UpdateLineCount()
	{
		var text = (NotesEditor.Text ?? string.Empty).Replace("\r\n", "\n");
		var trimmed = text.TrimEnd('\n');
		var lines = string.IsNullOrEmpty(trimmed) ? 0 : trimmed.Split('\n').Length;

		LinesLabel.Text = $"{Math.Min(lines, 500)} / 500 lines";
	}
}
