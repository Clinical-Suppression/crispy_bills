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
	private readonly List<PieLegendItem> _chartItems;
	private readonly PieChartDrawable _pieDrawable;

	public SummaryPage(int year, int month, BillingService service, LocalizationService localization)
	{
		InitializeComponent();
		_service = service;
		_localization = localization;
		_year = year;
		_month = month;

		var resources = Application.Current?.Resources;
		var palette = new List<Color>();
		void addResource(string key, string fallback) =>
			palette.Add(GetResourceColor(key, fallback));

		addResource("Magenta", "#2563EB");
		addResource("Success", "#16A34A");
		addResource("Info", "#0EA5E9");
		addResource("Tertiary", "#0BA5A5");
		addResource("Danger", "#F87171");
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
		YearIncomeLabel.Text = _localization.FormatCurrency(yearSummary.income);
		YearExpensesLabel.Text = _localization.FormatCurrency(yearSummary.expenses);
		YearRemainingLabel.Text = _localization.FormatCurrency(yearSummary.remaining);
		YearRemainingLabel.TextColor = yearSummary.remaining >= 0 ? GetResourceColor("Success", "#16A34A") : GetResourceColor("Danger", "#F87171");
		MonthBillCountLabel.Text = monthSummary.billCount.ToString();

		_chartItems = categoryRows.Select((row, index) =>
			new PieLegendItem(
				row.category,
				row.total,
				_localization.FormatCurrency(row.total),
				paletteArr[index % paletteArr.Length]))
			.ToList();

		CategoryCollection.ItemsSource = _chartItems;
		_pieDrawable = new PieChartDrawable(_chartItems);
		PieChartView.Drawable = _pieDrawable;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Shell.SetNavBarIsVisible(this, true);
	}

	private async void OnPieChartTapped(object? sender, TappedEventArgs e)
	{
		try
		{
			var point = e.GetPosition(PieChartView);
			if (point is null)
			{
				return;
			}

			var category = _pieDrawable.GetCategoryAtPoint((float)point.Value.X, (float)point.Value.Y, (float)PieChartView.Width, (float)PieChartView.Height);
			if (string.IsNullOrWhiteSpace(category))
			{
				return;
			}

			var item = _chartItems.FirstOrDefault(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
			if (item is null)
			{
				return;
			}

			var bills = _service.GetBillsInCategory(_month, item.Category);
			var list = bills.Select(b => new BillListItem(b, _localization.FormatCurrency(b.Amount))).ToList();
			await Navigation.PushAsync(new CategoryBillsPage(item.Category, _year, _month, list, _localization));
		}
		catch (Exception ex)
		{
			await DiagnosticsLog.WriteAsync("SummaryPieTap", ex);
			await DisplayAlert("Could not open category", ex.Message, "OK");
		}
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
		var resources = Application.Current?.Resources;
		if (resources is null)
		{
			return Color.FromArgb(fallback);
		}

		if (AppThemeHelper.IsEffectiveDarkTheme())
		{
			var darkKey = key switch
			{
				"Danger" => "DangerDark",
				"ValidationDanger" => "ValidationDangerDark",
				_ => null
			};
			if (darkKey != null && resources.TryGetValue(darkKey, out var dv) && dv is Color dc)
			{
				return dc;
			}
		}

		if (resources.TryGetValue(key, out var v) && v is Color c)
		{
			return c;
		}

		return Color.FromArgb(fallback);
	}

	private sealed class PieChartDrawable : IDrawable
	{
		private readonly IReadOnlyList<PieLegendItem> _items;
		private readonly List<(string category, float startAngle, float endAngle)> _sliceAngles = new();
		private RectF _arcRect;

		public PieChartDrawable(IReadOnlyList<PieLegendItem> items)
		{
			_items = items;
		}

		public void Draw(ICanvas canvas, RectF dirtyRect)
		{
			canvas.Antialias = true;
			if (_items.Count == 0)
			{
				canvas.FontColor = EmptyChartMessageColor();
				canvas.FontSize = 14;
				canvas.DrawString("No category totals to chart.", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
				return;
			}

			var total = _items.Sum(x => (float)x.Amount);
			if (total <= 0)
			{
				canvas.FontColor = EmptyChartMessageColor();
				canvas.FontSize = 14;
				canvas.DrawString("No category totals to chart.", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
				return;
			}

			var margin = 14f;
			var size = Math.Min(dirtyRect.Width, dirtyRect.Height) - (margin * 2f);
			var left = dirtyRect.Center.X - (size / 2f);
			var top = dirtyRect.Center.Y - (size / 2f);
			var arcRect = new RectF(left, top, size, size);
			_arcRect = arcRect;
			_sliceAngles.Clear();

			var startAngle = -90f;
			foreach (var item in _items)
			{
				var sweep = ((float)item.Amount / total) * 360f;
				canvas.FillColor = item.Color;
				FillPieSlice(canvas, arcRect, startAngle, sweep);
				_sliceAngles.Add((item.Category, startAngle, startAngle + sweep));

				if (sweep >= 18f)
				{
					var labelAngle = startAngle + (sweep / 2f);
					var labelRadians = MathF.PI * labelAngle / 180f;
					var labelRadius = (size / 2f) * 0.72f;
					var labelX = arcRect.Center.X + (labelRadius * MathF.Cos(labelRadians));
					var labelY = arcRect.Center.Y + (labelRadius * MathF.Sin(labelRadians));
					canvas.FontColor = ResourcesColor("White", "#FFFFFF");
					canvas.FontSize = 10;
					canvas.DrawString(item.Category, labelX - 40f, labelY - 8f, 80f, 16f, HorizontalAlignment.Center, VerticalAlignment.Center);
				}
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
			canvas.DrawString(total.ToString("F0"), new RectF(dirtyRect.Center.X - 45f, dirtyRect.Center.Y, 90f, 20f), HorizontalAlignment.Center, VerticalAlignment.Center);
		}

		public string? GetCategoryAtPoint(float x, float y, float width, float height)
		{
			if (_sliceAngles.Count == 0 || _arcRect.Width <= 0f || _arcRect.Height <= 0f)
			{
				return null;
			}

			var dx = x - _arcRect.Center.X;
			var dy = y - _arcRect.Center.Y;
			var distance = MathF.Sqrt((dx * dx) + (dy * dy));
			var outerRadius = _arcRect.Width / 2f;
			var innerRadius = outerRadius * 0.42f;
			if (distance > outerRadius || distance < innerRadius)
			{
				return null;
			}

			var angle = MathF.Atan2(dy, dx) * (180f / MathF.PI);
			while (angle < -90f) angle += 360f;
			while (angle >= 270f) angle -= 360f;

			foreach (var slice in _sliceAngles)
			{
				if (angle >= slice.startAngle && angle < slice.endAngle)
				{
					return slice.category;
				}
			}

			return null;
		}

		private static Color ResourcesColor(string key, string fallback)
		{
			if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
			{
				return c;
			}

			return Color.FromArgb(fallback);
		}

		private static Color EmptyChartMessageColor()
		{
			var dark = AppThemeHelper.IsEffectiveDarkTheme();
			return dark
				? ResourcesColor("MutedTextOnDark", "#E2E8F0")
				: ResourcesColor("EmptyText", "#64748B");
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
