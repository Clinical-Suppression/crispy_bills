using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CrispyBills.Mobile.Android;

public sealed class BulkBillsPage : ContentPage
{
    private const int MaxRows = 50;
    private const string RecurrenceMonthly = "Monthly (every month)";
    private const string RecurrenceEvery2Months = "Every 2 months";
    private const string RecurrenceEvery3Months = "Every 3 months";
    private const string RecurrenceWeekly = "Weekly";
    private const string RecurrenceBiWeekly = "Bi-weekly";

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

        Title = "Add bill(s)";
        AddRow();

        var hint = new Label
        {
            Text = "Start with one bill, then use Add Another Bill. Rows with a name and valid amount are saved.",
            FontSize = 13,
            TextColor = Color.FromArgb("#6B7280")
        };

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            ItemTemplate = new DataTemplate(() => BuildRowTemplate())
        };

        var addRowButton = new Button
        {
            Text = "Add Another Bill",
            FontSize = 16,
            HorizontalOptions = LayoutOptions.Fill,
            HeightRequest = 48
        };
        addRowButton.Clicked += (_, _) =>
        {
            if (_rows.Count < MaxRows)
            {
                AddRow();
            }
        };
        list.Footer = new VerticalStackLayout
        {
            Padding = new Thickness(0, 2, 0, 4),
            Children = { addRowButton }
        };

        var cancelBtn = new Button
        {
            Text = "Cancel",
            BackgroundColor = Color.FromArgb("#F3F4F6")
        };
        cancelBtn.Clicked += async (_, _) =>
        {
            await Navigation.PopAsync();
            if (_afterSaveOrCancel is not null)
            {
                await _afterSaveOrCancel();
            }
        };

        var saveBtn = new Button { Text = "Save all" };
        saveBtn.Clicked += OnSaveClicked;

        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Star)
            },
            ColumnSpacing = 12
        };
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 1);
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);

        var layout = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 10,
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            }
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(hint);
        layout.Children.Add(list);
        layout.Children.Add(buttons);
        Content = layout;
    }

    private IView BuildRowTemplate()
    {
        var nameEntry = new Entry { Placeholder = "Name", HeightRequest = 44 };
        nameEntry.Behaviors.Add(new SelectAllEntryBehavior());
        nameEntry.SetBinding(Entry.TextProperty, new Binding(nameof(BulkRow.Name), BindingMode.TwoWay));

        var amountEntry = new Entry { Placeholder = "Amount", Keyboard = Keyboard.Numeric, HeightRequest = 44 };
        amountEntry.Behaviors.Add(new SelectAllEntryBehavior());
        amountEntry.SetBinding(Entry.TextProperty, new Binding(nameof(BulkRow.AmountText), BindingMode.TwoWay));

        var categoryPicker = new Picker { Title = "Category", HeightRequest = 44 };
        categoryPicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(BulkRow.CategoryOptions)));
        categoryPicker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(BulkRow.Category), BindingMode.TwoWay));

        var dueDatePicker = new DatePicker { HeightRequest = 44 };
        dueDatePicker.SetBinding(DatePicker.DateProperty, new Binding(nameof(BulkRow.DueDate), BindingMode.TwoWay));

        var paidSwitch = new Switch();
        paidSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(BulkRow.IsPaid), BindingMode.TwoWay));
        var paidLabel = new Label { Text = "Paid", VerticalOptions = LayoutOptions.Center, FontSize = 16 };
        paidLabel.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                if (paidLabel.BindingContext is BulkRow row)
                {
                    row.IsPaid = !row.IsPaid;
                }
            })
        });
        var paidRow = new HorizontalStackLayout { Spacing = 10, Children = { paidSwitch, paidLabel } };

        var recurringSwitch = new Switch();
        recurringSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(BulkRow.IsRecurring), BindingMode.TwoWay));
        var recurringLabel = new Label { Text = "Recurring", VerticalOptions = LayoutOptions.Center, FontSize = 16 };
        recurringLabel.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                if (recurringLabel.BindingContext is BulkRow row)
                {
                    row.IsRecurring = !row.IsRecurring;
                }
            })
        });
        var recurringRow = new HorizontalStackLayout { Spacing = 10, Children = { recurringSwitch, recurringLabel } };

        var recurrencePicker = new Picker { Title = "How it repeats", HeightRequest = 44 };
        recurrencePicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(BulkRow.RecurrenceOptions)));
        recurrencePicker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(BulkRow.RecurrenceOption), BindingMode.TwoWay));
        recurrencePicker.SetBinding(IsVisibleProperty, new Binding(nameof(BulkRow.IsRecurring)));

        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                nameEntry,
                amountEntry,
                categoryPicker,
                dueDatePicker,
                paidRow,
                recurringRow,
                recurrencePicker
            }
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
        _rows.Add(new BulkRow
        {
            DueDate = new DateTime(_year, _month, day),
            Category = _categories[0],
            CategoryOptions = _categories.ToList()
        });
    }

    private static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out amount)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }

    private static (RecurrenceFrequency Frequency, int EveryMonths) ParseRecurrence(BulkRow row)
    {
        return row.RecurrenceOption switch
        {
            RecurrenceWeekly => (RecurrenceFrequency.Weekly, 1),
            RecurrenceBiWeekly => (RecurrenceFrequency.BiWeekly, 1),
            RecurrenceEvery2Months => (RecurrenceFrequency.MonthlyInterval, 2),
            RecurrenceEvery3Months => (RecurrenceFrequency.MonthlyInterval, 3),
            _ => (RecurrenceFrequency.MonthlyInterval, 1)
        };
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var drafts = new List<BillItem>();
        var errors = new List<string>();

        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            if (string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(row.AmountText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add($"Row {index + 1}: name required.");
                continue;
            }

            if (!TryParseAmount(row.AmountText, out var amount) || amount < 0)
            {
                errors.Add($"Row {index + 1}: amount must be a non-negative number.");
                continue;
            }

            var category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim();
            var recurrence = ParseRecurrence(row);
            drafts.Add(new BillItem
            {
                Name = row.Name.Trim(),
                Amount = amount,
                Category = category,
                DueDate = row.DueDate,
                IsPaid = row.IsPaid,
                IsRecurring = row.IsRecurring,
                RecurrenceFrequency = row.IsRecurring ? recurrence.Frequency : RecurrenceFrequency.None,
                RecurrenceEveryMonths = recurrence.EveryMonths,
                RecurrenceEndMode = RecurrenceEndMode.None,
                RecurrenceEndDate = null,
                RecurrenceMaxOccurrences = null,
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
        private string _recurrenceOption = RecurrenceMonthly;
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

        public IList<string> RecurrenceOptions { get; } = new List<string>
        {
            RecurrenceMonthly,
            RecurrenceEvery2Months,
            RecurrenceEvery3Months,
            RecurrenceWeekly,
            RecurrenceBiWeekly
        };

        public string RecurrenceOption
        {
            get => _recurrenceOption;
            set
            {
                if (_recurrenceOption == value)
                {
                    return;
                }

                _recurrenceOption = value;
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
