using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

public partial class UnlockPage : ContentPage
{
    private readonly AppLockService _appLockService;
    private readonly TaskCompletionSource<bool> _resultTcs = new();
    private bool _autoBiometricAttempted;

    public UnlockPage(AppLockService appLockService)
    {
        InitializeComponent();
        _appLockService = appLockService;
    }

    public Task<bool> WaitForResultAsync() => _resultTcs.Task;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var biometricVisible = await _appLockService.IsBiometricEnabledAsync();
        BiometricButton.IsVisible = biometricVisible;
        PinEntry.Focus();

        if (biometricVisible && !_autoBiometricAttempted)
        {
            _autoBiometricAttempted = true;
            await AttemptBiometricUnlockAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _resultTcs.TrySetResult(false);
    }

    private async void OnUnlockClicked(object? sender, EventArgs e)
    {
        await TryUnlockWithPinAsync();
    }

    private async void OnBiometricClicked(object? sender, EventArgs e)
    {
        await AttemptBiometricUnlockAsync();
    }

    private async void OnPinEntryCompleted(object? sender, EventArgs e)
    {
        await TryUnlockWithPinAsync();
    }

    private async Task TryUnlockWithPinAsync()
    {
        SetStatus(string.Empty);

        var pin = (PinEntry.Text ?? string.Empty).Trim();
        if (pin.Length != 4)
        {
            SetStatus("Enter your 4-digit PIN.");
            PinEntry.Focus();
            return;
        }

        var unlocked = await _appLockService.VerifyPinAsync(pin);
        if (!unlocked)
        {
            PinEntry.Text = string.Empty;
            SetStatus("That PIN did not match.");
            PinEntry.Focus();
            return;
        }

        await CloseAsync(true);
    }

    private async Task AttemptBiometricUnlockAsync()
    {
        SetStatus(string.Empty);

        var unlocked = await _appLockService.TryBiometricUnlockAsync();
        if (unlocked)
        {
            await CloseAsync(true);
            return;
        }

        PinEntry.Focus();
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

        _resultTcs.TrySetResult(result);
        await Navigation.PopModalAsync();
    }
}
