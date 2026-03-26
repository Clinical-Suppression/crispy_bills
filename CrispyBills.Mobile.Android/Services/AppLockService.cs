using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;

namespace CrispyBills.Mobile.Android.Services;

public sealed class AppLockService(BiometricAuthService biometricAuthService)
{
    private const string PinHashKey = "app_lock.pin_hash";
    private const string PinSaltKey = "app_lock.pin_salt";
    private const string PromptHandledKey = "app_lock.prompt_handled";
    private const string BiometricEnabledKey = "app_lock.biometric_enabled";
    private static readonly TimeSpan RelockDelay = TimeSpan.FromMinutes(2);

    private bool _sessionUnlocked;
    private DateTimeOffset? _lastBackgroundUtc;

    public async Task<bool> HasPinAsync()
    {
        var hash = await SafeGetAsync(PinHashKey);
        var salt = await SafeGetAsync(PinSaltKey);
        return !string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(salt);
    }

    public async Task<bool> IsBiometricAvailableAsync() => await biometricAuthService.IsAvailableAsync();

    public async Task<bool> IsBiometricEnabledAsync()
    {
        if (!await HasPinAsync())
        {
            return false;
        }

        return await ReadBoolAsync(BiometricEnabledKey);
    }

    public async Task SetBiometricEnabledAsync(bool enabled)
    {
        if (!await HasPinAsync())
        {
            enabled = false;
        }

        if (enabled && !await IsBiometricAvailableAsync())
        {
            enabled = false;
        }

        await SafeSetAsync(BiometricEnabledKey, enabled.ToString());
    }

    public async Task<bool> ShouldOfferPinSetupPromptAsync()
    {
        if (await HasPinAsync())
        {
            return false;
        }

        return !await ReadBoolAsync(PromptHandledKey);
    }

    public Task MarkPinSetupPromptHandledAsync() => SafeSetAsync(PromptHandledKey, bool.TrueString);

    public async Task SetPinAsync(string pin, bool enableBiometrics)
    {
        ValidatePin(pin);

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPin(pin, salt);

        await SafeSetAsync(PinSaltKey, Convert.ToBase64String(salt));
        await SafeSetAsync(PinHashKey, hash);
        await SafeSetAsync(PromptHandledKey, bool.TrueString);
        await SetBiometricEnabledAsync(enableBiometrics);

        NoteSuccessfulUnlock();
    }

    public async Task DisablePinAsync()
    {
        SafeRemove(PinSaltKey);
        SafeRemove(PinHashKey);
        SafeRemove(BiometricEnabledKey);
        await SafeSetAsync(PromptHandledKey, bool.TrueString);

        _sessionUnlocked = true;
        _lastBackgroundUtc = null;
    }

    public async Task<bool> VerifyPinAsync(string pin)
    {
        ValidatePin(pin);

        var saltText = await SafeGetAsync(PinSaltKey);
        var storedHash = await SafeGetAsync(PinHashKey);
        if (string.IsNullOrWhiteSpace(saltText) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(saltText);
        }
        catch
        {
            return false;
        }
        var candidateHash = HashPin(pin, salt);
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));

        if (matches)
        {
            NoteSuccessfulUnlock();
        }

        return matches;
    }

    public async Task<bool> TryBiometricUnlockAsync()
    {
        if (!await IsBiometricEnabledAsync())
        {
            return false;
        }

        var unlocked = await biometricAuthService.AuthenticateAsync("Unlock Crispy Bills", "Use your fingerprint or face to continue.");
        if (unlocked)
        {
            NoteSuccessfulUnlock();
        }

        return unlocked;
    }

    public async Task<bool> ShouldRequireUnlockAsync()
    {
        if (!await HasPinAsync())
        {
            return false;
        }

        if (!_sessionUnlocked)
        {
            return true;
        }

        if (_lastBackgroundUtc is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - _lastBackgroundUtc.Value >= RelockDelay)
        {
            _sessionUnlocked = false;
            return true;
        }

        return false;
    }

    public void NoteAppBackgrounded()
    {
        if (_sessionUnlocked)
        {
            _lastBackgroundUtc = DateTimeOffset.UtcNow;
        }
    }

    public void NoteSuccessfulUnlock()
    {
        _sessionUnlocked = true;
        _lastBackgroundUtc = null;
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9'))
        {
            throw new ArgumentException("PIN must be exactly 4 digits.", nameof(pin));
        }
    }

    private static string HashPin(string pin, byte[] salt)
    {
        var pinBytes = Encoding.UTF8.GetBytes(pin);
        var buffer = new byte[salt.Length + pinBytes.Length];
        Buffer.BlockCopy(salt, 0, buffer, 0, salt.Length);
        Buffer.BlockCopy(pinBytes, 0, buffer, salt.Length, pinBytes.Length);
        return Convert.ToBase64String(SHA256.HashData(buffer));
    }

    private static async Task<bool> ReadBoolAsync(string key)
    {
        var value = await SafeGetAsync(key);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static async Task<string?> SafeGetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch
        {
            return null;
        }
    }

    private static async Task SafeSetAsync(string key, string value)
    {
        try
        {
            await SecureStorage.Default.SetAsync(key, value);
        }
        catch
        {
            // Ignore secure storage write failures to avoid app startup deadlocks.
        }
    }

    private static void SafeRemove(string key)
    {
        try
        {
            SecureStorage.Default.Remove(key);
        }
        catch
        {
            // Ignore remove failures.
        }
    }
}
