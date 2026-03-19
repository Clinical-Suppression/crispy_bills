using System.Globalization;

namespace CrispyBills.Mobile.Android.Services;

public sealed class LocalizationService
{
    private const string LanguageMetaKey = "PreferredLanguage";

    private readonly IBillingRepository _repository;

    public LocalizationService(IBillingRepository repository)
    {
        _repository = repository;
    }

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentCulture;

    public async Task InitializeAsync()
    {
        var preferred = await _repository.GetAppMetaAsync(LanguageMetaKey);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            SetCulture(preferred);
        }
    }

    public async Task SetCultureAsync(string cultureName)
    {
        SetCulture(cultureName);
        await _repository.SetAppMetaAsync(LanguageMetaKey, cultureName);
    }

    public string FormatCurrency(decimal amount)
    {
        return amount.ToString("C2", CurrentCulture);
    }

    public string FormatDate(DateTime date)
    {
        return date.ToString("d", CurrentCulture);
    }

    private void SetCulture(string cultureName)
    {
        try
        {
            CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch
        {
            CurrentCulture = CultureInfo.CurrentCulture;
        }
    }
}
