using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class NotesPage : ContentPage
{
    private bool _unsubscribed = false;
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
        NotesEditor.Text = await _service.LoadNotesAsync();
        UpdateLineCount();
        _unsubscribed = false;
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
        await _service.SaveNotesAsync(NotesEditor.Text ?? string.Empty);
        await DisplayAlert("Saved", "Notes updated.", "OK");
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
