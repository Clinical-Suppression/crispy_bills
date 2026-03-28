using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

/// <summary>Result of the structured-import scope picker (desktop CSV parity).</summary>
public sealed class ImportSelectionResult
{
    public Dictionary<string, List<string>> SelectedMonthsByYear { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ImportNotes { get; init; }
}

/// <summary>Lets the user pick years/months (and notes) before applying a desktop-format CSV import.</summary>
public sealed class ImportSelectionPage : ContentPage
{
    private readonly MobileStructuredImportPackage _package;
    private readonly TaskCompletionSource<ImportSelectionResult?> _tcs = new();
    private readonly Dictionary<string, List<CheckBox>> _yearToChecks = new(StringComparer.OrdinalIgnoreCase);
    private bool _importNotes = true;

    public Task<ImportSelectionResult?> WaitForResultAsync() => _tcs.Task;

    public ImportSelectionPage(MobileStructuredImportPackage package)
    {
        _package = package;
        Title = "Import scope";

        var root = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto }
            },
            Padding = new Thickness(14),
            RowSpacing = 12
        };

        var instructions = new Label
        {
            Text = "Choose years and months to import. Each selected month replaces bills and income for that month. Months with no rows in the file are skipped unless you confirm importing them as empty on the next step.",
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap
        };
        root.Add(instructions);
        Grid.SetRow(instructions, 0);

        var scroll = new ScrollView();
        var stack = new VerticalStackLayout { Spacing = 16 };

        var orderedYears = package.Years.Keys
            .OrderBy(y => int.TryParse(y, out var n) ? n : int.MaxValue)
            .ToList();

        foreach (var year in orderedYears)
        {
            stack.Add(new Label
            {
                Text = $"Year {year}",
                FontAttributes = FontAttributes.Bold,
                FontSize = 17
            });

            var toggleRow = new HorizontalStackLayout { Spacing = 10 };
            var allBtn = new Button { Text = "All months", FontSize = 13 };
            var noneBtn = new Button { Text = "Clear", FontSize = 13 };
            toggleRow.Add(allBtn);
            toggleRow.Add(noneBtn);
            stack.Add(toggleRow);

            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (var c = 0; c < 3; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            }

            for (var r = 0; r < 4; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var checks = new List<CheckBox>();
            for (var i = 0; i < 12; i++)
            {
                var row = i / 3;
                var col = i % 3;
                var monthName = StructuredReportMonthNames.All[i];
                var cell = new HorizontalStackLayout { Spacing = 6 };
                var cb = new CheckBox();
                checks.Add(cb);
                cell.Add(cb);
                cell.Add(new Label
                {
                    Text = monthName.Length >= 3 ? monthName[..3] : monthName,
                    VerticalOptions = LayoutOptions.Center,
                    FontSize = 13
                });
                grid.Add(cell, col, row);
            }

            _yearToChecks[year] = checks;

            allBtn.Clicked += (_, _) =>
            {
                foreach (var c in checks)
                {
                    c.IsChecked = true;
                }
            };
            noneBtn.Clicked += (_, _) =>
            {
                foreach (var c in checks)
                {
                    c.IsChecked = false;
                }
            };

            stack.Add(grid);
        }

        if (package.HasNotesSection)
        {
            var notesRow = new HorizontalStackLayout { Spacing = 10 };
            var notesCheck = new CheckBox { IsChecked = true };
            notesCheck.CheckedChanged += (_, e) => _importNotes = e.Value;
            notesRow.Add(notesCheck);
            notesRow.Add(new Label
            {
                Text = "Import global notes",
                VerticalOptions = LayoutOptions.Center,
                FontSize = 15
            });
            stack.Add(notesRow);
            _importNotes = true;
        }
        else
        {
            _importNotes = false;
        }

        scroll.Content = stack;
        Grid.SetRow(scroll, 1);
        root.Add(scroll);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(), new ColumnDefinition()), ColumnSpacing = 12 };
        var cancel = new Button
        {
            Text = "Cancel",
            BackgroundColor = Color.FromArgb("#E2E8F0"),
            TextColor = Color.FromArgb("#0F172A")
        };
        var ok = new Button { Text = "Import" };
        cancel.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult(null);
            await Navigation.PopModalAsync();
        };
        ok.Clicked += async (_, _) =>
        {
            var selected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _yearToChecks)
            {
                var months = new List<string>();
                for (var i = 0; i < kv.Value.Count && i < StructuredReportMonthNames.All.Length; i++)
                {
                    if (kv.Value[i].IsChecked)
                    {
                        months.Add(StructuredReportMonthNames.All[i]);
                    }
                }

                if (months.Count > 0)
                {
                    selected[kv.Key] = months;
                }
            }

            if (selected.Count == 0 && !(_importNotes && _package.HasNotesSection))
            {
                await DisplayAlert("Nothing selected", "Select at least one month, or import notes.", "OK");
                return;
            }

            _tcs.TrySetResult(new ImportSelectionResult
            {
                SelectedMonthsByYear = selected,
                ImportNotes = _importNotes && _package.HasNotesSection
            });
            await Navigation.PopModalAsync();
        };
        buttons.Add(cancel, 0, 0);
        buttons.Add(ok, 1, 0);
        Grid.SetRow(buttons, 2);
        root.Add(buttons);

        Content = root;
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_tcs.Task.IsCompleted)
        {
            _tcs.TrySetResult(null);
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Navigation.PopModalAsync();
            }
            catch
            {
                // ignore double-pop
            }
        });
        return true;
    }
}
