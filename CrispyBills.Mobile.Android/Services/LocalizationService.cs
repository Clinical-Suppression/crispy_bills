using System.Globalization;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Provides culture selection and simple formatting helpers used by the mobile UI.
/// Persists preferred language as app metadata via <see cref="IBillingRepository"/>.
/// </summary>
public sealed class LocalizationService
{
    private const string LanguageMetaKey = "PreferredLanguage";

    private readonly IBillingRepository _repository;

    public LocalizationService(IBillingRepository repository)
    {
        _repository = repository;
    }

    /// <summary>The currently selected culture used for formatting.</summary>
    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>Initialize the service by loading persisted preferred language if present.</summary>
    public async Task InitializeAsync()
    {
        var preferred = await _repository.GetAppMetaAsync(LanguageMetaKey);
        if (preferred is not null)
        {
            SetCulture(preferred);
        }
    }

    /// <summary>Raw stored culture name, or empty when following system default.</summary>
    public async Task<string?> GetPersistedLanguageCodeAsync()
    {
        return await _repository.GetAppMetaAsync(LanguageMetaKey);
    }

    /// <summary>Set the culture and persist the preference.</summary>
    public async Task SetCultureAsync(string cultureName)
    {
        SetCulture(cultureName);
        await _repository.SetAppMetaAsync(LanguageMetaKey, cultureName);
    }

    /// <summary>Format a currency amount using the current culture.</summary>
    public string FormatCurrency(decimal amount)
    {
        return amount.ToString("C2", CurrentCulture);
    }

    /// <summary>Format a date using the current culture's short date pattern.</summary>
    public string FormatDate(DateTime date)
    {
        return date.ToString("d", CurrentCulture);
    }

    private void SetCulture(string cultureName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cultureName))
            {
                CultureInfo.DefaultThreadCurrentCulture = null;
                CultureInfo.DefaultThreadCurrentUICulture = null;
                CurrentCulture = CultureInfo.CurrentCulture;
            }
            else
            {
                CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentCulture = CurrentCulture;
                CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
            }
        }
        catch
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
            CurrentCulture = CultureInfo.CurrentCulture;
        }
    }
}
