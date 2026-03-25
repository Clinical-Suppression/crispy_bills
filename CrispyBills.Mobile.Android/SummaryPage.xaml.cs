using CrispyBills.Mobile.Android.Models;
using CrispyBills.Mobile.Android.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;

namespace CrispyBills.Mobile.Android;

public partial class SummaryPage : ContentPage
{
	private readonly BillingService _service;
	private readonly LocalizationService _localization;
	private readonly int _year;
	private readonly int _month;

	public SummaryPage(int year, int month, BillingService service, LocalizationService localization)
	{
		InitializeComponent();
		_service = service;
		_localization = localization;
		_year = year;
		_month = month;

		var resources = Application.Current?.Resources;
		var palette = new List<Color>();
		void addResource(string key, string fallback)
		{
			if (resources != null && resources.TryGetValue(key, out var val) && val is Color c)
			{
				palette.Add(c);
			}
			else
			{
				palette.Add(Color.FromArgb(fallback));
			}
		}

		addResource("Magenta", "#2563EB");
		addResource("Success", "#16A34A");
		addResource("Info", "#0EA5E9");
		addResource("Tertiary", "#0BA5A5");
		addResource("Danger", "#DC2626");
		addResource("Primary", "#1F4B99");
		addResource("SecondaryDarkText", "#9DB4E9");
		addResource("PrimaryDark", "#163762");
		var paletteArr = palette.ToArray();

		var yearSummary = _service.GetYearSummary();
		var monthSummary = _service.GetMonthSummary(month);
		var categoryRows = _service.GetCategoryTotals(month)
			.OrderByDescending(x => x.total)
			.ToList();

		HeaderLabel.Text = $"{MonthNames.Name(month)} {year}";
		YearIncomeLabel.Text = $"${yearSummary.income:F2}";
		YearExpensesLabel.Text = $"${yearSummary.expenses:F2}";
		YearRemainingLabel.Text = $"${yearSummary.remaining:F2}";
		YearRemainingLabel.TextColor = yearSummary.remaining >= 0 ? GetResourceColor("Success", "#16A34A") : GetResourceColor("Danger", "#DC2626");
		MonthBillCountLabel.Text = monthSummary.billCount.ToString();

		var chartItems = categoryRows.Select((row, index) =>
			new PieLegendItem(
				row.category,
				row.total,
				$"${row.total:F2}",
				paletteArr[index % paletteArr.Length]))
			.ToList();

		CategoryCollection.ItemsSource = chartItems;
		PieChartView.Drawable = new PieChartDrawable(chartItems);
	}

	private async void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		try
		{
			if (e.CurrentSelection.FirstOrDefault() is not PieLegendItem item)
			{
				return;
			}

			CategoryCollection.SelectedItem = null;
			var bills = _service.GetBillsInCategory(_month, item.Category);
			var list = bills.Select(b => new BillListItem(b, _localization.FormatCurrency(b.Amount))).ToList();
			await Navigation.PushAsync(new CategoryBillsPage(item.Category, _year, _month, list, _localization));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SummaryCategorySelect", ex);
			await DisplayAlert("Could not open category", ex.Message, "OK");
		}
	}

	private async void OnOpenHtmlSummaryClicked(object? sender, EventArgs e)
	{
		try
		{
			var html = _service.BuildYearSummaryHtml();
			var path = Path.Combine(FileSystem.CacheDirectory, $"CrispyBills_Summary_{_year}.html");
			await File.WriteAllTextAsync(path, html);
			await Launcher.Default.OpenAsync(new OpenFileRequest("Year summary", new ReadOnlyFile(path)));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SummaryHtmlOpen", ex);
			await DisplayAlert("Could not open report", ex.Message, "OK");
		}
	}

	private static Color GetResourceColor(string key, string fallback)
	{
		if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
		{
			return c;
		}

		return Color.FromArgb(fallback);
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
				canvas.FontColor = ResourcesColor("EmptyText", "#64748B");
				canvas.FontSize = 14;
				canvas.DrawString("No category totals to chart.", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
				return;
			}

			var total = _items.Sum(x => (float)x.Amount);
			if (total <= 0)
			{
				canvas.FontColor = ResourcesColor("EmptyText", "#64748B");
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

			canvas.FillColor = ResourcesColor("White", "#FFFFFF");
			var donutSize = size * 0.42f;
			var donutLeft = dirtyRect.Center.X - (donutSize / 2f);
			var donutTop = dirtyRect.Center.Y - (donutSize / 2f);
			canvas.FillCircle(donutLeft + (donutSize / 2f), donutTop + (donutSize / 2f), donutSize / 2f);

			canvas.FontColor = ResourcesColor("MidnightBlue", "#0F172A");
			canvas.FontSize = 12;
			canvas.DrawString("Total", new RectF(dirtyRect.Center.X - 40f, dirtyRect.Center.Y - 16f, 80f, 16f), HorizontalAlignment.Center, VerticalAlignment.Center);
			canvas.FontSize = 14;
			canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
			canvas.DrawString($"${total:F0}", new RectF(dirtyRect.Center.X - 45f, dirtyRect.Center.Y, 90f, 20f), HorizontalAlignment.Center, VerticalAlignment.Center);
		}

		private static Color ResourcesColor(string key, string fallback)
		{
			if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
			{
				return c;
			}

			return Color.FromArgb(fallback);
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
