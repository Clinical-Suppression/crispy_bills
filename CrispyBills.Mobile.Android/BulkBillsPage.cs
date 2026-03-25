using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CrispyBills.Mobile.Android;

/// <summary>Buffer-style bulk entry (up to 50 rows). Programmatic UI to avoid per-row picker binding issues.</summary>
public sealed class BulkBillsPage : ContentPage
{
    private const int InitialRows = 6;
    private const int MaxRows = 50;

    private readonly BillingService _service;
    private readonly int _year;
    private readonly int _month;
    private readonly IReadOnlyList<string> _categories;
    private readonly Func<Task>? _afterSaveOrCancel;
    private readonly ObservableCollection<BulkRow> _rows = new();

    public BulkBillsPage(BillingService service, int year, int month, IReadOnlyList<string> categories, Func<Task>? afterSaveOrCancel = null)
    {
        _service = service;
        _year = year;
        _month = month;
        _categories = categories.Count > 0 ? categories : new[] { "General" };
        _afterSaveOrCancel = afterSaveOrCancel;

        Title = "Bulk add bills";

        for (var i = 0; i < InitialRows; i++)
        {
            AddRow();
        }

        var list = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(() => BuildRowTemplate())
        };

        var saveBtn = new Button { Text = "Save all" };
        saveBtn.Clicked += OnSaveClicked;
        var cancelBtn = new Button { Text = "Cancel", BackgroundColor = Color.FromArgb("#F3F4F6") };
        cancelBtn.Clicked += async (_, _) =>
        {
            await Navigation.PopAsync();
            if (_afterSaveOrCancel is not null)
            {
                await _afterSaveOrCancel();
            }
        };

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12 };
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 1);
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);

        var hint = new Label
        {
            Text = "Rows with a name and valid amount are saved. Completing the bottom row adds another (max 50).",
            FontSize = 13,
            TextColor = Color.FromArgb("#6B7280")
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Padding = new Thickness(12),
            RowSpacing = 10
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(hint);
        grid.Children.Add(list);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private IView BuildRowTemplate()
    {
        var nameEntry = new Entry { Placeholder = "Name", HeightRequest = 44 };
        nameEntry.SetBinding(Entry.TextProperty, new Binding(nameof(BulkRow.Name), BindingMode.TwoWay));

        var amtEntry = new Entry { Placeholder = "Amount", Keyboard = Keyboard.Numeric, HeightRequest = 44 };
        amtEntry.SetBinding(Entry.TextProperty, new Binding(nameof(BulkRow.AmountText), BindingMode.TwoWay));

        var catPicker = new Picker { Title = "Category", HeightRequest = 44 };
        catPicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(BulkRow.CategoryOptions)));
        catPicker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(BulkRow.Category), BindingMode.TwoWay));

        var due = new DatePicker { HeightRequest = 44 };
        due.SetBinding(DatePicker.DateProperty, new Binding(nameof(BulkRow.DueDate), BindingMode.TwoWay));

        var paidSw = new Switch();
        paidSw.SetBinding(Switch.IsToggledProperty, new Binding(nameof(BulkRow.IsPaid), BindingMode.TwoWay));
        var recurSw = new Switch();
        recurSw.SetBinding(Switch.IsToggledProperty, new Binding(nameof(BulkRow.IsRecurring), BindingMode.TwoWay));

        var paidRow = new HorizontalStackLayout { Spacing = 8, Children = { paidSw, new Label { Text = "Paid", VerticalOptions = LayoutOptions.Center } } };
        var recurRow = new HorizontalStackLayout { Spacing = 8, Children = { recurSw, new Label { Text = "Recurring", VerticalOptions = LayoutOptions.Center } } };

        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { nameEntry, amtEntry, catPicker, due, paidRow, recurRow }
        };

        return new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#E5E7EB"),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Content = stack
        };
    }

    private void AddRow()
    {
        var day = Math.Min(DateTime.Today.Day, DateTime.DaysInMonth(_year, _month));
        var row = new BulkRow
        {
            DueDate = new DateTime(_year, _month, day),
            Category = _categories[0],
            CategoryOptions = _categories.ToList()
        };
        row.PropertyChanged += OnRowPropertyChanged;
        _rows.Add(row);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BulkRow row || _rows.Count >= MaxRows)
        {
            return;
        }

        if (!ReferenceEquals(row, _rows[^1]))
        {
            return;
        }

        if (IsRowComplete(row))
        {
            AddRow();
        }
    }

    private static bool IsRowComplete(BulkRow r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
        {
            return false;
        }

        if (!TryParseAmount(r.AmountText, out var amt) || amt < 0)
        {
            return false;
        }

        return r.DueDate != default;
    }

    private static bool TryParseAmount(string? text, out decimal amt)
    {
        amt = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out amt)
               || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out amt);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var drafts = new List<BillItem>();
        var errors = new List<string>();
        var n = 0;
        foreach (var row in _rows)
        {
            n++;
            if (string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(row.AmountText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add($"Row {n}: name required.");
                continue;
            }

            if (!TryParseAmount(row.AmountText, out var amt) || amt < 0)
            {
                errors.Add($"Row {n}: amount must be a non-negative number.");
                continue;
            }

            if (row.DueDate == default)
            {
                errors.Add($"Row {n}: due date required.");
                continue;
            }

            var cat = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim();
            drafts.Add(new BillItem
            {
                Name = row.Name.Trim(),
                Amount = amt,
                Category = cat,
                DueDate = row.DueDate,
                IsPaid = row.IsPaid,
                IsRecurring = row.IsRecurring,
                RecurrenceFrequency = row.IsRecurring ? RecurrenceFrequency.MonthlyInterval : RecurrenceFrequency.None,
                Year = _year,
                Month = _month
            });
        }

        if (errors.Count > 0)
        {
            await DisplayAlert("Fix rows", string.Join(Environment.NewLine, errors.Take(8)), "OK");
            return;
        }

        if (drafts.Count == 0)
        {
            await DisplayAlert("Nothing to save", "Add at least one bill with a name and amount.", "OK");
            return;
        }

        try
        {
            await _service.AddBillsBulkAsync(_month, drafts);
            await Navigation.PopAsync();
            if (_afterSaveOrCancel is not null)
            {
                await _afterSaveOrCancel();
            }
        }
        catch (Exception ex)
        {
            await DiagnosticsLog.WriteAsync("BulkBillsPage.Save", ex);
            await DisplayAlert("Save failed", ex.Message, "OK");
        }
    }

    private sealed class BulkRow : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _amountText = string.Empty;
        private string _category = "General";
        private DateTime _dueDate;
        private bool _isPaid;
        private bool _isRecurring;
        private IList<string> _categoryOptions = Array.Empty<string>();

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                {
                    return;
                }

                _name = value;
                OnPropertyChanged();
            }
        }

        public string AmountText
        {
            get => _amountText;
            set
            {
                if (_amountText == value)
                {
                    return;
                }

                _amountText = value;
                OnPropertyChanged();
            }
        }

        public string Category
        {
            get => _category;
            set
            {
                if (_category == value)
                {
                    return;
                }

                _category = value;
                OnPropertyChanged();
            }
        }

        public IList<string> CategoryOptions
        {
            get => _categoryOptions;
            set
            {
                _categoryOptions = value;
                OnPropertyChanged();
            }
        }

        public DateTime DueDate
        {
            get => _dueDate;
            set
            {
                if (_dueDate == value)
                {
                    return;
                }

                _dueDate = value;
                OnPropertyChanged();
            }
        }

        public bool IsPaid
        {
            get => _isPaid;
            set
            {
                if (_isPaid == value)
                {
                    return;
                }

                _isPaid = value;
                OnPropertyChanged();
            }
        }

        public bool IsRecurring
        {
            get => _isRecurring;
            set
            {
                if (_isRecurring == value)
                {
                    return;
                }

                _isRecurring = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
