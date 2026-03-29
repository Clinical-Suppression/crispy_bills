using System;
using System.Globalization;
using System.Threading;
using System.Windows;

namespace CrispyBills;

public partial class SettingsWindow : Window
{
    private readonly (string Label, string Code)[] _options =
    [
        ("System default (Windows)", ""),
        ("English (United States)", "en-US"),
        ("English (United Kingdom)", "en-GB"),
        ("Spanish (Spain)", "es-ES"),
        ("French (France)", "fr-FR"),
        ("German (Germany)", "de-DE")
    ];

    public SettingsWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"}\nData: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CrispyBills")}";
        foreach (var o in _options)
        {
            CultureCombo.Items.Add(o.Label);
        }

        var saved = AppSettings.ReadSavedUICulture();
        var idx = 0;
        if (!string.IsNullOrEmpty(saved))
        {
            var found = Array.FindIndex(_options, x => string.Equals(x.Code, saved, StringComparison.OrdinalIgnoreCase));
            if (found >= 0)
            {
                idx = found;
            }
        }

        CultureCombo.SelectedIndex = idx;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var i = CultureCombo.SelectedIndex;
        if (i < 0 || i >= _options.Length)
        {
            i = 0;
        }

        var code = _options[i].Code;
        AppSettings.SaveUICulture(code);

        if (string.IsNullOrEmpty(code))
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
        else
        {
            var c = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c;
            Thread.CurrentThread.CurrentCulture = c;
            Thread.CurrentThread.CurrentUICulture = c;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
