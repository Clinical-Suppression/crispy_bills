using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CrispyBills;

/// <summary>Persists lightweight desktop preferences (e.g. UI culture) outside the bill databases.</summary>
internal static class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Crispy_Bills");

    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    public static void LoadAndApplyCulture()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.UICulture))
            {
                return;
            }

            var c = CultureInfo.GetCultureInfo(dto.UICulture);
            CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c;
            Thread.CurrentThread.CurrentCulture = c;
            Thread.CurrentThread.CurrentUICulture = c;
        }
        catch
        {
            // ignore invalid settings
        }
    }

    public static void SaveUICulture(string? cultureName)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var dto = new SettingsDto { UICulture = cultureName ?? string.Empty };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // best-effort
        }
    }

    public static string? ReadSavedUICulture()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(SettingsPath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            return dto?.UICulture;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsDto
    {
        public string UICulture { get; set; } = string.Empty;
    }
}
