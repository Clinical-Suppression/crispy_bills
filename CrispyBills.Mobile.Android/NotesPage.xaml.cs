using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class NotesPage : ContentPage
{
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
        var lines = (NotesEditor.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.None)
            .Length;

        LinesLabel.Text = $"{Math.Min(lines, 500)} / 500 lines";
    }
}
