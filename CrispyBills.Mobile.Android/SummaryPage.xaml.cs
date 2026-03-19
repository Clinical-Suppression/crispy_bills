using CrispyBills.Mobile.Android.Services;
using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android;

public partial class SummaryPage : ContentPage
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb("#2563EB"),
        Color.FromArgb("#16A34A"),
        Color.FromArgb("#F97316"),
        Color.FromArgb("#9333EA"),
        Color.FromArgb("#DC2626"),
        Color.FromArgb("#0EA5E9"),
        Color.FromArgb("#14B8A6"),
        Color.FromArgb("#A855F7")
    };

    public SummaryPage(int year, int month, BillingService service)
    {
        InitializeComponent();

        var yearSummary = service.GetYearSummary();
        var monthSummary = service.GetMonthSummary(month);
        var categoryRows = service.GetCategoryTotals(month)
            .OrderByDescending(x => x.total)
            .ToList();

        HeaderLabel.Text = $"{MonthNames.Name(month)} {year}";
        YearIncomeLabel.Text = $"${yearSummary.income:F2}";
        YearExpensesLabel.Text = $"${yearSummary.expenses:F2}";
        YearRemainingLabel.Text = $"${yearSummary.remaining:F2}";
        YearRemainingLabel.TextColor = yearSummary.remaining >= 0 ? Color.FromArgb("#166534") : Color.FromArgb("#991B1B");
        MonthBillCountLabel.Text = monthSummary.billCount.ToString();

        var chartItems = categoryRows.Select((row, index) =>
            new PieLegendItem(
                row.category,
                row.total,
                $"${row.total:F2}",
                Palette[index % Palette.Length]))
            .ToList();

        CategoryCollection.ItemsSource = chartItems;
        PieChartView.Drawable = new PieChartDrawable(chartItems);
    }

    private sealed class PieChartDrawable : IDrawable
    {
        private readonly IReadOnlyList<PieLegendItem> _items;

        public PieChartDrawable(IReadOnlyList<PieLegendItem> items)
        {
            _items = items;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.Antialias = true;
            if (_items.Count == 0)
            {
                canvas.FontColor = Color.FromArgb("#64748B");
                canvas.FontSize = 14;
                canvas.DrawString("No category totals to chart.", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            var total = _items.Sum(x => (float)x.Amount);
            if (total <= 0)
            {
                canvas.FontColor = Color.FromArgb("#64748B");
                canvas.FontSize = 14;
                canvas.DrawString("No category totals to chart.", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            var margin = 14f;
            var size = Math.Min(dirtyRect.Width, dirtyRect.Height) - (margin * 2f);
            var left = dirtyRect.Center.X - (size / 2f);
            var top = dirtyRect.Center.Y - (size / 2f);
            var arcRect = new RectF(left, top, size, size);

            var startAngle = -90f;
            foreach (var item in _items)
            {
                var sweep = ((float)item.Amount / total) * 360f;
                canvas.FillColor = item.Color;
                FillPieSlice(canvas, arcRect, startAngle, sweep);
                startAngle += sweep;
            }

            canvas.FillColor = Colors.White;
            var donutSize = size * 0.42f;
            var donutLeft = dirtyRect.Center.X - (donutSize / 2f);
            var donutTop = dirtyRect.Center.Y - (donutSize / 2f);
            canvas.FillCircle(donutLeft + (donutSize / 2f), donutTop + (donutSize / 2f), donutSize / 2f);

            canvas.FontColor = Color.FromArgb("#0F172A");
            canvas.FontSize = 12;
            canvas.DrawString("Total", new RectF(dirtyRect.Center.X - 40f, dirtyRect.Center.Y - 16f, 80f, 16f), HorizontalAlignment.Center, VerticalAlignment.Center);
            canvas.FontSize = 14;
            canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
            canvas.DrawString($"${total:F0}", new RectF(dirtyRect.Center.X - 45f, dirtyRect.Center.Y, 90f, 20f), HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        private static void FillPieSlice(ICanvas canvas, RectF rect, float startAngle, float sweepAngle)
        {
            var path = new PathF();
            var centerX = rect.Center.X;
            var centerY = rect.Center.Y;
            var radiusX = rect.Width / 2f;
            var radiusY = rect.Height / 2f;
            var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweepAngle) / 6f));
            var angleStep = sweepAngle / steps;

            path.MoveTo(centerX, centerY);

            var firstRadians = MathF.PI * startAngle / 180f;
            path.LineTo(
                centerX + (radiusX * MathF.Cos(firstRadians)),
                centerY + (radiusY * MathF.Sin(firstRadians)));

            for (var i = 1; i <= steps; i++)
            {
                var angle = startAngle + (angleStep * i);
                var radians = MathF.PI * angle / 180f;
                path.LineTo(
                    centerX + (radiusX * MathF.Cos(radians)),
                    centerY + (radiusY * MathF.Sin(radians)));
            }

            path.LineTo(centerX, centerY);
            path.Close();
            canvas.FillPath(path);
        }
    }
}
