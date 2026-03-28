using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class PinSetupPage : ContentPage
{
    private readonly AppLockService _appLockService;
    private readonly bool _allowSkip;
    private readonly TaskCompletionSource<bool> _resultTcs = new();
    private bool _allowClose;

    public PinSetupPage(AppLockService appLockService, bool allowSkip)
    {
        InitializeComponent();
        _appLockService = appLockService;
        _allowSkip = allowSkip;
        CancelButton.Text = allowSkip ? "Not now" : "Cancel";
    }

    public Task<bool> WaitForResultAsync() => _resultTcs.Task;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BiometricRow.IsVisible = await _appLockService.IsBiometricAvailableAsync();
        PinEntry.Focus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_allowClose)
        {
            _resultTcs.TrySetResult(false);
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        await SaveAsync();
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(false);
    }

    private async void OnPinCompleted(object? sender, EventArgs e)
    {
        ConfirmPinEntry.Focus();
    }

    private async void OnConfirmCompleted(object? sender, EventArgs e)
    {
        await SaveAsync();
    }

    private void OnBiometricRowTapped(object? sender, TappedEventArgs e)
    {
        BiometricSwitch.IsToggled = !BiometricSwitch.IsToggled;
    }

    private async Task SaveAsync()
    {
        SetStatus(string.Empty);

        var pin = (PinEntry.Text ?? string.Empty).Trim();
        var confirm = (ConfirmPinEntry.Text ?? string.Empty).Trim();

        if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9'))
        {
            SetStatus("PIN must be exactly 4 digits.");
            PinEntry.Focus();
            return;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            SetStatus("PIN and confirmation must match.");
            ConfirmPinEntry.Focus();
            return;
        }

        var saved = await _appLockService.SetPinAsync(pin, BiometricRow.IsVisible && BiometricSwitch.IsToggled);
        if (!saved)
        {
            SetStatus("Could not save your PIN to secure storage. Check device security settings and try again.");
            return;
        }

        await CloseAsync(true);
    }

    private void SetStatus(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private async Task CloseAsync(bool result)
    {
        if (_resultTcs.Task.IsCompleted)
        {
            return;
        }

        _allowClose = true;
        _resultTcs.TrySetResult(result);
        await Navigation.PopModalAsync();
    }
}
