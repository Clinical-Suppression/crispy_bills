using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;

namespace CrispyBills.Mobile.Android;

/// <summary>Shows bills in one category for summary drill-down.</summary>
public sealed class CategoryBillsPage : ContentPage
{
    public CategoryBillsPage(string category, int year, int month, IReadOnlyList<BillListItem> items, LocalizationService localization)
    {
        Title = category;
        var header = new Label
        {
            Text = $"{MonthNames.Name(month)} {year}",
            FontSize = 15,
            TextColor = Color.FromArgb("#64748B"),
            Margin = new Thickness(14, 0, 14, 8)
        };

        var stack = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(14, 0, 14, 14) };
        if (items.Count == 0)
        {
            stack.Add(new Label
            {
                Text = "No bills in this category.",
                FontSize = 15,
                TextColor = Color.FromArgb("#64748B")
            });
        }
        else
        {
            foreach (var it in items)
            {
                var row = new Border
                {
                    StrokeThickness = 0,
                    BackgroundColor = Color.FromArgb("#F8FAFC"),
                    Padding = new Thickness(14, 12),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Label { Text = it.Name, FontAttributes = FontAttributes.Bold, FontSize = 16 },
                            new HorizontalStackLayout
                            {
                                Spacing = 12,
                                Children =
                                {
                                    new Label { Text = localization.FormatCurrency(it.Amount), FontSize = 15 },
                                    new Label { Text = it.StatusText, FontSize = 13, TextColor = Color.FromArgb("#64748B") }
                                }
                            },
                            new Label { Text = $"Due {it.DueDate:MMM d}", FontSize = 13, TextColor = Color.FromArgb("#64748B") }
                        }
                    }
                };
                stack.Add(row);
            }
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout { Children = { header, stack } }
        };
    }
}
