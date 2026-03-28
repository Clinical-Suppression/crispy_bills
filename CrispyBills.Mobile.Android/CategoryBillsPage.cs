using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CrispyBills.Mobile.Android;

/// <summary>Shows bills in one category for summary drill-down.</summary>
public sealed class CategoryBillsPage : ContentPage
{
    public CategoryBillsPage(string category, int year, int month, IReadOnlyList<BillListItem> items, LocalizationService localization)
    {
        Title = category;
        Background = ResolvePageBackgroundBrush();

        var isDark = AppThemeHelper.IsEffectiveDarkTheme();
        var muted = isDark ? Color.FromArgb("#D7E0EC") : Color.FromArgb("#64748B");
        var nameColor = isDark ? Colors.White : Color.FromArgb("#111827");
        var rowBg = isDark ? Color.FromArgb("#505968") : Color.FromArgb("#F8FAFC");

        var header = new Label
        {
            Text = $"{MonthNames.Name(month)} {year}",
            FontSize = 15,
            TextColor = muted,
            Margin = new Thickness(14, 0, 14, 8)
        };

        var stack = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(14, 0, 14, 14) };
        if (items.Count == 0)
        {
            stack.Add(new Label
            {
                Text = "No bills in this category.",
                FontSize = 15,
                TextColor = muted
            });
        }
        else
        {
            foreach (var it in items)
            {
                var row = new Border
                {
                    StrokeThickness = 0,
                    BackgroundColor = rowBg,
                    Padding = new Thickness(14, 12),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Label { Text = it.Name, FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = nameColor },
                            new HorizontalStackLayout
                            {
                                Spacing = 12,
                                Children =
                                {
                                    new Label { Text = localization.FormatCurrency(it.Amount), FontSize = 15, TextColor = nameColor },
                                    new Label { Text = it.StatusText, FontSize = 13, TextColor = muted }
                                }
                            },
                            new Label { Text = $"Due {it.DueDate:MMM d}", FontSize = 13, TextColor = muted }
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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Shell.SetNavBarIsVisible(this, true);
    }

    private static Brush ResolvePageBackgroundBrush()
    {
        var app = Application.Current;
        if (app?.Resources is null)
        {
            return new SolidColorBrush(Colors.White);
        }

        var key = AppThemeHelper.IsEffectiveDarkTheme() ? "PageBackgroundBrushDark" : "PageBackgroundBrush";
        if (app.Resources.ContainsKey(key) && app.Resources[key] is Brush b)
        {
            return b;
        }

        return new SolidColorBrush(Colors.White);
    }
}
