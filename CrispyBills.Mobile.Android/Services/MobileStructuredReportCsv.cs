using System.Globalization;
using System.Text;
using CrispyBills.Mobile.Android.Models;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>English month names used in desktop structured CSV exports (must match WPF <c>months</c> array).</summary>
public static class StructuredReportMonthNames
{
    public static readonly string[] All =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    public static int IndexOf(string monthName) =>
        Array.FindIndex(All, m => m.Equals(monthName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Per-year data parsed from a desktop-format structured report CSV.</summary>
public sealed class MobileStructuredYearImportData
{
    public Dictionary<string, List<BillItem>> BillsByMonth { get; }
    public Dictionary<string, decimal> IncomeByMonth { get; }

    public MobileStructuredYearImportData(IEnumerable<string> monthNames)
    {
        BillsByMonth = monthNames.ToDictionary(m => m, _ => new List<BillItem>(), StringComparer.OrdinalIgnoreCase);
        IncomeByMonth = monthNames.ToDictionary(m => m, _ => 0m, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Full structured import payload including optional global notes.</summary>
public sealed class MobileStructuredImportPackage
{
    public Dictionary<string, MobileStructuredYearImportData> Years { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasNotesSection { get; set; }
    public string NotesText { get; set; } = string.Empty;
}

/// <summary>Parses desktop <c>Export CSV</c> structured reports into <see cref="MobileStructuredImportPackage"/>.</summary>
public static class MobileStructuredReportCsv
{
    public static bool LooksLikeDesktopStructuredReport(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var head = lines[0].TrimStart();
        if (head.StartsWith("REPORT,", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return lines.Any(l => l.TrimStart().StartsWith("===== YEAR =====", StringComparison.OrdinalIgnoreCase));
    }

    public static MobileStructuredImportPackage Parse(
        string[] lines,
        Action<string>? logNonFatal = null,
        bool writeDiagnostics = false,
        string? dataRootForDiagnostics = null)
    {
        var package = new MobileStructuredImportPackage();
        var notesByLineNumber = new SortedDictionary<int, string>();
        string? currentYear = null;
        string? currentMonth = null;
        var inDetailSection = false;
        var inNotesSection = false;
        var totalLines = 0;
        var totalDetailRows = 0;
        var skippedRows = 0;
        var monthsArray = StructuredReportMonthNames.All;

        foreach (var rawLine in lines)
        {
            totalLines++;
            var parts = ParseCsvLine(rawLine, msg => logNonFatal?.Invoke(msg));
            if (parts.Count == 0 || parts.All(string.IsNullOrWhiteSpace))
            {
                inDetailSection = false;
                continue;
            }

            var first = parts[0].Trim();

            if (string.Equals(first, "===== YEAR =====", StringComparison.OrdinalIgnoreCase))
            {
                currentYear = parts.Count > 1 ? parts[1].Trim() : null;
                currentMonth = null;
                inDetailSection = false;
                inNotesSection = false;
                if (!string.IsNullOrWhiteSpace(currentYear))
                {
                    GetOrCreateYearImportData(package, currentYear);
                }

                continue;
            }

            if (string.Equals(first, "--- MONTH ---", StringComparison.OrdinalIgnoreCase))
            {
                currentMonth = parts.Count > 1 ? parts[1].Trim() : null;
                inDetailSection = false;
                inNotesSection = false;
                continue;
            }

            if (string.Equals(first, "===== NOTES =====", StringComparison.OrdinalIgnoreCase))
            {
                package.HasNotesSection = true;
                currentYear = null;
                currentMonth = null;
                inDetailSection = false;
                inNotesSection = true;
                continue;
            }

            if (inNotesSection)
            {
                if (string.Equals(first, "NOTES SUMMARY", StringComparison.OrdinalIgnoreCase) ||
                    (parts.Count >= 2 && string.Equals(parts[0].Trim(), "Line Number", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(parts[1].Trim(), "Text", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (int.TryParse(parts[0].Trim(), out var lineNumber))
                {
                    notesByLineNumber[lineNumber] = parts.Count > 1 ? parts[1] : string.Empty;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(currentYear) && !string.IsNullOrWhiteSpace(currentMonth) &&
                string.Equals(first, "MONTH SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                var yearData = GetOrCreateYearImportData(package, currentYear);
                yearData.IncomeByMonth[currentMonth] = ParseIncomeFromMonthSummary(parts);
                continue;
            }

            if (parts.Count >= 9 &&
                string.Equals(parts[0].Trim(), "Name", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1].Trim(), "Category", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[2].Trim(), "Amount", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[3].Trim(), "Due Date", StringComparison.OrdinalIgnoreCase))
            {
                inDetailSection = true;
                continue;
            }

            if (!inDetailSection || parts.Count < 9)
            {
                skippedRows++;
                continue;
            }

            totalDetailRows++;

            var rowMonth = string.IsNullOrWhiteSpace(parts[7]) ? currentMonth ?? string.Empty : parts[7].Trim();
            var rowYear = string.IsNullOrWhiteSpace(parts[8]) ? currentYear ?? string.Empty : parts[8].Trim();

            if (string.IsNullOrWhiteSpace(rowMonth) || string.IsNullOrWhiteSpace(rowYear))
            {
                skippedRows++;
                logNonFatal?.Invoke($"ParseStructuredReportCsv: skipping row with missing month/year at line {totalLines}.");
                continue;
            }

            if (Array.IndexOf(monthsArray, rowMonth) < 0)
            {
                skippedRows++;
                logNonFatal?.Invoke($"ParseStructuredReportCsv: skipping row with invalid month '{rowMonth}' at line {totalLines}.");
                continue;
            }

            if (!decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                skippedRows++;
                logNonFatal?.Invoke($"ParseStructuredReportCsv: skipping row with invalid amount '{parts[2]}' at line {totalLines}.");
                continue;
            }

            if (!TryParseStructuredDueDate(parts[3], out var dueDate))
            {
                skippedRows++;
                logNonFatal?.Invoke($"ParseStructuredReportCsv: skipping row with invalid due date '{parts[3]}' at line {totalLines}.");
                continue;
            }

            var isPaid = string.Equals(parts[4].Trim(), "PAID", StringComparison.OrdinalIgnoreCase);
            var isRecurring = string.Equals(parts[5].Trim(), "Yes", StringComparison.OrdinalIgnoreCase);

            var bill = new BillItem
            {
                Id = Guid.NewGuid(),
                Name = parts[0],
                Category = parts[1],
                Amount = amount,
                DueDate = dueDate,
                IsPaid = isPaid,
                IsRecurring = isRecurring,
                RecurrenceFrequency = isRecurring ? RecurrenceFrequency.MonthlyInterval : RecurrenceFrequency.None
            };

            if (int.TryParse(rowYear, out var yearValue))
            {
                var monthIndex = Array.IndexOf(monthsArray, rowMonth);
                if (monthIndex >= 0)
                {
                    bill.ContextPeriodStart = new DateTime(yearValue, monthIndex + 1, 1);
                }
            }

            var yearDataForBill = GetOrCreateYearImportData(package, rowYear);
            yearDataForBill.BillsByMonth[rowMonth].Add(bill);
        }

        if (package.HasNotesSection)
        {
            package.NotesText = RebuildNotesText(notesByLineNumber);
        }

        if (writeDiagnostics && !string.IsNullOrWhiteSpace(dataRootForDiagnostics))
        {
            try
            {
                var diagSb = new StringBuilder();
                diagSb.AppendLine("Parsed structured CSV diagnostics (mobile)");
                diagSb.AppendLine($"Generated: {DateTime.Now:f}");
                diagSb.AppendLine($"Total lines: {totalLines}");
                diagSb.AppendLine($"Detail rows found: {totalDetailRows}");
                diagSb.AppendLine($"Skipped rows: {skippedRows}");
                diagSb.AppendLine("");
                foreach (var y in package.Years.OrderBy(kv => kv.Key))
                {
                    diagSb.AppendLine($"Year: {y.Key}");
                    foreach (var m in monthsArray)
                    {
                        var count = y.Value.BillsByMonth.GetValueOrDefault(m)?.Count ?? 0;
                        var income = y.Value.IncomeByMonth.GetValueOrDefault(m, 0m);
                        diagSb.AppendLine($"  {m}: {count} bill(s), Income: {income:F2}");
                    }

                    diagSb.AppendLine("");
                }

                var diagFolder = Path.Combine(dataRootForDiagnostics, "import_diagnostics");
                Directory.CreateDirectory(diagFolder);
                var diagPath = Path.Combine(diagFolder, $"import_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(diagPath, diagSb.ToString());
                logNonFatal?.Invoke($"Imported CSV parsed: {package.Years.Count} year(s), diagnostics: {Path.GetFileName(diagPath)}");
            }
            catch (Exception ex)
            {
                logNonFatal?.Invoke($"ParseStructuredReportCsv diagnostics write failed: {ex.Message}");
            }
        }

        return package;
    }

    public static MobileStructuredYearImportData GetOrCreateYearImportData(MobileStructuredImportPackage package, string year)
    {
        if (!package.Years.TryGetValue(year, out var yearData))
        {
            yearData = new MobileStructuredYearImportData(StructuredReportMonthNames.All);
            package.Years[year] = yearData;
        }

        return yearData;
    }

    public static List<string> ParseCsvLine(string line, Action<string>? onWarning = null)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i < line.Length - 1 && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        fields.Add(sb.ToString());

        if (line.Count(ch => ch == '"') % 2 == 1)
        {
            onWarning?.Invoke($"ParseCsvLine: unclosed quote in line: {line}");
        }

        return fields;
    }

    private static decimal ParseIncomeFromMonthSummary(List<string> parts)
    {
        for (var i = 1; i + 1 < parts.Count; i += 2)
        {
            if (string.Equals(parts[i].Trim(), "Income", StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(parts[i + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out var income))
            {
                return income;
            }
        }

        return 0m;
    }

    private static bool TryParseStructuredDueDate(string value, out DateTime dueDate) =>
        DateTime.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate) ||
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate);

    private static string RebuildNotesText(SortedDictionary<int, string> notesByLineNumber)
    {
        if (notesByLineNumber.Count == 0)
        {
            return string.Empty;
        }

        var maxLineNumber = notesByLineNumber.Keys.Max();
        var lines = new List<string>(maxLineNumber);
        for (var i = 1; i <= maxLineNumber; i++)
        {
            lines.Add(notesByLineNumber.TryGetValue(i, out var noteLine) ? noteLine : string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
