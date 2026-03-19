using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android.Services;

public interface IBillingRepository
{
    string DataRoot { get; }
    string GetYearDatabasePath(int year);
    IReadOnlyList<int> GetAvailableYears();
    Task InitializeYearAsync(int year);
    Task<YearData> LoadYearAsync(int year);
    Task SaveYearAsync(int year, YearData data);
    Task<string> LoadNotesAsync();
    Task SaveNotesAsync(string notes);
    Task<string?> GetAppMetaAsync(string key);
    Task SetAppMetaAsync(string key, string value);
}