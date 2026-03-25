using System.Globalization;
using System.Net;
using System.Text;
using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android.Services;

public static class YearSummaryHtmlBuilder
{
    public static string Build(int year, YearData data, Func<int, decimal> getIncome)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        var months = StructuredReportMonthNames.All;
        var monthly = months.Select((monthName, i) =>
        {
            var monthNum = i + 1;
            var bills = data.BillsByMonth[monthNum];
            var income = getIncome(monthNum);
            var expenses = bills.Sum(b => b.Amount);
            var net = income - expenses;
            var paidCount = bills.Count(b => b.IsPaid);
            var unpaidCount = bills.Count - paidCount;
            var ctx = new DateTime(year, monthNum, 1);
            var overdueCount = bills.Count(b =>
                !b.IsPaid && (b.DueDate.Date < DateTime.Today || b.DueDate.Date < ctx.Date));
            var recurringCount = bills.Count(b => b.IsRecurring);
            var utilization = income > 0m ? (double)(expenses / income) * 100d : (expenses > 0 ? 100d : 0d);
            return new MonthRow(
                monthName,
                income,
                expenses,
                net,
                bills.Count,
                paidCount,
                unpaidCount,
                overdueCount,
                recurringCount,
                Math.Max(0d, utilization));
        }).ToList();

        var annualIncome = monthly.Sum(m => m.Income);
        var annualExpenses = monthly.Sum(m => m.Expenses);
        var annualNet = annualIncome - annualExpenses;
        var totalBills = monthly.Sum(m => m.BillCount);
        var totalPaid = monthly.Sum(m => m.PaidCount);
        var totalUnpaid = monthly.Sum(m => m.UnpaidCount);
        var totalOverdue = monthly.Sum(m => m.OverdueCount);
        var totalRecurring = monthly.Sum(m => m.RecurringCount);
        var paidRate = totalBills > 0 ? (double)totalPaid / totalBills * 100d : 0d;
        var averageMonthlyNet = monthly.Count > 0 ? monthly.Average(m => m.Net) : 0m;
        var strongest = monthly.OrderByDescending(m => m.Net).First();
        var weakest = monthly.OrderBy(m => m.Net).First();

        var categoryTotals = data.BillsByMonth.Values
            .SelectMany(bills => bills)
            .GroupBy(b => string.IsNullOrWhiteSpace(b.Category) ? "General" : b.Category)
            .Select(g => (Category: g.Key, Amount: g.Sum(x => x.Amount)))
            .OrderByDescending(x => x.Amount)
            .Take(8)
            .ToList();

        var netToneClass = annualNet >= 0m ? "tone-good" : "tone-bad";
        var generatedAt = DateTime.Now.ToString("f", CultureInfo.CurrentCulture);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'>");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset='utf-8' />");
        sb.AppendLine("  <meta name='viewport' content='width=device-width, initial-scale=1' />");
        sb.AppendLine($"  <title>Crispy Bills Summary {H(year.ToString(CultureInfo.InvariantCulture))}</title>");
        AppendCss(sb);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class='wrap'>");
        sb.AppendLine("    <section class='hero'>");
        sb.AppendLine("      <div>");
        sb.AppendLine($"        <h1>Crispy Bills — {H(year.ToString(CultureInfo.InvariantCulture))}</h1>");
        sb.AppendLine("        <p>Month-by-month income, expenses, and payment health.</p>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class='meta'>");
        sb.AppendLine($"        <div><strong>Generated:</strong> {H(generatedAt)}</div>");
        sb.AppendLine($"        <div><strong>Best month:</strong> {H(strongest.Month)} ({strongest.Net:C})</div>");
        sb.AppendLine($"        <div><strong>Most challenging:</strong> {H(weakest.Month)} ({weakest.Net:C})</div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </section>");

        sb.AppendLine("    <section class='cards'>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Annual income</div><div class='value'>{annualIncome:C}</div></article>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Annual expenses</div><div class='value'>{annualExpenses:C}</div></article>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Annual net</div><div class='value {netToneClass}'>{annualNet:C}</div></article>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Paid rate</div><div class='value'>{paidRate:0.#}%</div><div class='sub'>{totalPaid} paid / {totalUnpaid} unpaid / {totalOverdue} overdue</div></article>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Recurring rows</div><div class='value'>{totalRecurring}</div></article>");
        sb.AppendLine($"      <article class='card'><div class='kicker'>Avg monthly net</div><div class='value'>{averageMonthlyNet:C}</div></article>");
        sb.AppendLine("    </section>");

        sb.AppendLine("    <section class='panel'>");
        sb.AppendLine("      <h2>Monthly detail</h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Month</th><th class='num'>Income</th><th class='num'>Expenses</th><th class='num'>Net</th><th>Status</th><th>Utilization</th></tr></thead>");
        sb.AppendLine("        <tbody>");

        foreach (var m in monthly)
        {
            var statusClass = m.Net >= 0m ? "good" : (m.Net > -250m ? "warn" : "bad");
            var statusText = m.Net >= 0m ? "Healthy" : (m.Net > -250m ? "Watch" : "Deficit");
            var utilColor = m.Utilization < 80d ? "#1f9d57" : (m.Utilization < 100d ? "#cf7a00" : "#c73333");
            var utilWidth = Math.Min(m.Utilization, 100d);

            sb.AppendLine("          <tr>");
            sb.AppendLine($"            <td><strong>{H(m.Month)}</strong><div class='sub'>{m.BillCount} bills, {m.RecurringCount} recurring</div></td>");
            sb.AppendLine($"            <td class='num'>{m.Income:C}</td>");
            sb.AppendLine($"            <td class='num'>{m.Expenses:C}</td>");
            sb.AppendLine($"            <td class='num {(m.Net >= 0m ? "tone-good" : "tone-bad")}'><strong>{m.Net:C}</strong></td>");
            sb.AppendLine($"            <td><span class='pill {statusClass}'>{H(statusText)}</span></td>");
            sb.AppendLine("            <td>");
            sb.AppendLine($"              <div class='util-track'><div class='util-fill' style='width:{utilWidth:0.#}%; background:{utilColor};'></div></div>");
            sb.AppendLine($"              <div class='sub'>{m.Utilization:0.#}% of income</div>");
            sb.AppendLine("            </td>");
            sb.AppendLine("          </tr>");
        }

        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </section>");

        sb.AppendLine("    <section class='panel'>");
        sb.AppendLine("      <h2>Category concentration</h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Category</th><th class='num'>Amount</th><th>Share</th></tr></thead>");
        sb.AppendLine("        <tbody>");

        if (categoryTotals.Count == 0)
        {
            sb.AppendLine("            <tr><td colspan='3'>No categories.</td></tr>");
        }
        else
        {
            foreach (var cat in categoryTotals)
            {
                var share = annualExpenses > 0m ? cat.Amount / annualExpenses * 100m : 0m;
                sb.AppendLine("            <tr>");
                sb.AppendLine($"              <td>{H(cat.Category)}</td>");
                sb.AppendLine($"              <td class='num'>{cat.Amount:C}</td>");
                sb.AppendLine("              <td>");
                sb.AppendLine($"                <div class='bar-track'><div class='bar-fill' style='width:{Math.Min((double)share, 100d):0.#}%'></div></div>");
                sb.AppendLine($"                <div class='sub'>{share:0.#}% of expenses</div>");
                sb.AppendLine("              </td>");
                sb.AppendLine("            </tr>");
            }
        }

        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </section>");

        sb.AppendLine($"    <div class='footer'>Crispy Bills — {H(generatedAt)}</div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private readonly record struct MonthRow(
        string Month,
        decimal Income,
        decimal Expenses,
        decimal Net,
        int BillCount,
        int PaidCount,
        int UnpaidCount,
        int OverdueCount,
        int RecurringCount,
        double Utilization);

    private static void AppendCss(StringBuilder sb)
    {
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg:#f3f6fb; --panel:#fff; --ink:#122033; --muted:#5d6f87; --line:#d9e1ec; --good:#1f9d57; --warn:#cf7a00; --bad:#c73333; }");
        sb.AppendLine("    * { box-sizing: border-box; }");
        sb.AppendLine("    body { margin:0; padding:16px; background:var(--bg); color:var(--ink); font-family:system-ui,-apple-system,sans-serif; font-size:14px; }");
        sb.AppendLine("    .wrap { max-width:900px; margin:0 auto; }");
        sb.AppendLine("    .hero { background:linear-gradient(128deg,#0f4da0,#1d7ed6); color:#fff; border-radius:14px; padding:16px; margin-bottom:14px; }");
        sb.AppendLine("    .hero h1 { margin:0; font-size:22px; }");
        sb.AppendLine("    .hero p { margin:6px 0 0; opacity:.92; font-size:13px; }");
        sb.AppendLine("    .meta { margin-top:10px; font-size:12px; opacity:.95; }");
        sb.AppendLine("    .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(140px,1fr)); gap:10px; margin-bottom:14px; }");
        sb.AppendLine("    .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; }");
        sb.AppendLine("    .kicker { font-size:11px; color:var(--muted); text-transform:uppercase; }");
        sb.AppendLine("    .value { margin-top:6px; font-size:20px; font-weight:700; }");
        sb.AppendLine("    .sub { margin-top:4px; font-size:11px; color:var(--muted); }");
        sb.AppendLine("    .tone-good { color:var(--good); } .tone-bad { color:var(--bad); }");
        sb.AppendLine("    .panel { background:var(--panel); border:1px solid var(--line); border-radius:12px; overflow:hidden; margin-bottom:14px; }");
        sb.AppendLine("    .panel h2 { margin:0; padding:12px 14px; font-size:16px; background:#f8fbff; border-bottom:1px solid var(--line); }");
        sb.AppendLine("    table { width:100%; border-collapse:collapse; font-size:13px; }");
        sb.AppendLine("    th,td { padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:top; }");
        sb.AppendLine("    th { text-align:left; color:#36516f; background:#f9fbff; }");
        sb.AppendLine("    .num { text-align:right; font-variant-numeric:tabular-nums; }");
        sb.AppendLine("    .pill { display:inline-block; border-radius:999px; padding:2px 8px; font-size:11px; font-weight:600; }");
        sb.AppendLine("    .pill.good { background:#e8f8ef; color:#13633a; }");
        sb.AppendLine("    .pill.warn { background:#fff3e5; color:#8a4f00; }");
        sb.AppendLine("    .pill.bad { background:#fdecec; color:#8f2424; }");
        sb.AppendLine("    .util-track { width:120px; height:8px; background:#e9eef6; border-radius:999px; overflow:hidden; }");
        sb.AppendLine("    .util-fill { height:100%; }");
        sb.AppendLine("    .bar-track { height:8px; border-radius:999px; background:#ebf0f7; overflow:hidden; }");
        sb.AppendLine("    .bar-fill { height:100%; background:linear-gradient(90deg,#1d7ed6,#4ba3ee); }");
        sb.AppendLine("    .footer { font-size:11px; color:var(--muted); text-align:right; }");
        sb.AppendLine("  </style>");
    }
}
