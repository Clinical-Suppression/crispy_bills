using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Abstraction over persistent storage used by the mobile billing service.
/// Implementations provide file paths, load/save semantics and simple key-value metadata.
/// </summary>
public interface IBillingRepository
{
    /// <summary>Root directory used for repository files.</summary>
    string DataRoot { get; }

    /// <summary>Path to the database file for the given year.</summary>
    /// <param name="year">Year to target.</param>
    string GetYearDatabasePath(int year);

    /// <summary>List of years that have persisted data available.</summary>
    IReadOnlyList<int> GetAvailableYears();

    /// <summary>Prepare storage for a new year.</summary>
    Task InitializeYearAsync(int year);

    /// <summary>Load year data from persistent storage.</summary>
    Task<YearData> LoadYearAsync(int year);

    /// <summary>Persist year data to storage.</summary>
    Task SaveYearAsync(int year, YearData data);

    /// <summary>Load free-form notes stored by the app.</summary>
    Task<string> LoadNotesAsync();

    /// <summary>Save free-form notes stored by the app.</summary>
    Task SaveNotesAsync(string notes);

    /// <summary>Read an application metadata value by key.</summary>
    Task<string?> GetAppMetaAsync(string key);

    /// <summary>Set an application metadata key.</summary>
    Task SetAppMetaAsync(string key, string value);
}