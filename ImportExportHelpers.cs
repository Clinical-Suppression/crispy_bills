using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrispyBills
{
    /// <summary>
    /// Container for structured import results produced by the CSV parsing helpers.
    /// Holds per-year data and an optional notes section.
    /// </summary>
    public sealed class StructuredImportPackage
    {
        public Dictionary<string, StructuredYearImportData> Years { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasNotesSection { get; set; }
        public string NotesText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Holds import data for a single year: bills by month and income by month.
    /// </summary>
    public sealed class StructuredYearImportData
    {
        public Dictionary<string, List<Bill>> BillsByMonth { get; }
        public Dictionary<string, decimal> IncomeByMonth { get; }

        public StructuredYearImportData(IEnumerable<string> monthNames)
        {
            BillsByMonth = monthNames.ToDictionary(month => month, _ => new List<Bill>(), StringComparer.OrdinalIgnoreCase);
            IncomeByMonth = monthNames.ToDictionary(month => month, _ => 0m, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class ImportExportHelpers
    {
        /// <summary>
        /// Parse a single CSV line into fields, handling quoted values and doubled quotes.
        /// Calls <paramref name="onWarning"/> when non-fatal parse issues are encountered.
        /// </summary>
        public static List<string> ParseCsvLine(string line, Action<string>? onWarning = null)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
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

        /// <summary>
        /// Parse a structured export/diagnostic CSV (desktop format) into an import package.
        /// Returns a <see cref="StructuredImportPackage"/> containing per-year data and optional notes.
        /// </summary>
        public static StructuredImportPackage ParseStructuredReportCsv(
            string[] lines,
            IReadOnlyList<string> months,
            string backupsRoot,
            Action<string>? logNonFatal = null,
            bool writeDiagnostics = true)
        {
            var package = new StructuredImportPackage();
            var notesByLineNumber = new SortedDictionary<int, string>();
            string? currentYear = null;
            string? currentMonth = null;
            bool inDetailSection = false;
            bool inNotesSection = false;
            int totalLines = 0;
            int totalDetailRows = 0;
            int skippedRows = 0;
            var monthsArray = months.ToArray();

            foreach (var rawLine in lines)
            {
                totalLines++;
                var parts = ParseCsvLine(rawLine, msg => logNonFatal?.Invoke(msg));
                if (parts.Count == 0 || parts.All(string.IsNullOrWhiteSpace))
                {
                    inDetailSection = false;
                    continue;
                }

                string first = parts[0].Trim();

                if (string.Equals(first, "===== YEAR =====", StringComparison.OrdinalIgnoreCase))
                {
                    currentYear = parts.Count > 1 ? parts[1].Trim() : null;
                    currentMonth = null;
                    inDetailSection = false;
                    inNotesSection = false;
                    if (!string.IsNullOrWhiteSpace(currentYear))
                    {
                        GetOrCreateYearImportData(package, currentYear, months);
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
                        continue;

                    if (int.TryParse(parts[0].Trim(), out int lineNumber))
                    {
                        notesByLineNumber[lineNumber] = parts.Count > 1 ? parts[1] : string.Empty;
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(currentYear) && !string.IsNullOrWhiteSpace(currentMonth) &&
                    string.Equals(first, "MONTH SUMMARY", StringComparison.OrdinalIgnoreCase))
                {
                    var yearData = GetOrCreateYearImportData(package, currentYear, months);
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

                string rowMonth = string.IsNullOrWhiteSpace(parts[7]) ? currentMonth ?? string.Empty : parts[7].Trim();
                string rowYear = string.IsNullOrWhiteSpace(parts[8]) ? currentYear ?? string.Empty : parts[8].Trim();

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

                bool isPaid = string.Equals(parts[4].Trim(), "PAID", StringComparison.OrdinalIgnoreCase);
                bool isRecurring = string.Equals(parts[5].Trim(), "Yes", StringComparison.OrdinalIgnoreCase);

                var bill = new Bill
                {
                    Id = Guid.NewGuid(),
                    Name = parts[0],
                    Category = parts[1],
                    Amount = amount,
                    DueDate = dueDate,
                    IsPaid = isPaid,
                    IsRecurring = isRecurring
                };

                if (int.TryParse(rowYear, out int yearValue))
                {
                    int monthIndex = Array.IndexOf(monthsArray, rowMonth);
                    if (monthIndex >= 0)
                    {
                        bill.ContextPeriodStart = new DateTime(yearValue, monthIndex + 1, 1);
                    }
                }

                var yearDataForBill = GetOrCreateYearImportData(package, rowYear, months);
                yearDataForBill.BillsByMonth[rowMonth].Add(bill);
            }

            if (package.HasNotesSection)
            {
                package.NotesText = RebuildNotesText(notesByLineNumber);
            }

            if (writeDiagnostics)
            {
                try
                {
                    var diagSb = new StringBuilder();
                    diagSb.AppendLine($"Parsed structured CSV diagnostics");
                    diagSb.AppendLine($"Generated: {DateTime.Now:f}");
                    diagSb.AppendLine($"Total lines: {totalLines}");
                    diagSb.AppendLine($"Detail rows found: {totalDetailRows}");
                    diagSb.AppendLine($"Skipped rows: {skippedRows}");
                    diagSb.AppendLine("");
                    foreach (var y in package.Years.OrderBy(kv => kv.Key))
                    {
                        diagSb.AppendLine($"Year: {y.Key}");
                        foreach (var m in months)
                        {
                            var count = y.Value.BillsByMonth.GetValueOrDefault(m)?.Count ?? 0;
                            var income = y.Value.IncomeByMonth.GetValueOrDefault(m, 0m);
                            diagSb.AppendLine($"  {m}: {count} bill(s), Income: {income:F2}");
                        }
                        diagSb.AppendLine("");
                    }

                    var diagFolder = Path.Combine(backupsRoot, "import_diagnostics");
                    Directory.CreateDirectory(diagFolder);
                    var diagPath = Path.Combine(diagFolder, $"import_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(diagPath, diagSb.ToString());
                    logNonFatal?.Invoke($"Imported CSV parsed: {package.Years.Count} year(s), diagnostics: {Path.GetFileName(diagPath)}");
                }
                catch (Exception ex)
                {
                    logNonFatal?.Invoke($"ParseStructuredReportCsv diagnostics write failed: {ex}");
                }
            }

            return package;
        }

        /// <summary>
        /// Helper to get or create the year import container for the given year.
        /// </summary>
        public static StructuredYearImportData GetOrCreateYearImportData(StructuredImportPackage package, string year, IReadOnlyList<string> months)
        {
            if (!package.Years.TryGetValue(year, out var yearData))
            {
                yearData = new StructuredYearImportData(months);
                package.Years[year] = yearData;
            }

            return yearData;
        }

        private static decimal ParseIncomeFromMonthSummary(List<string> parts)
        {
            for (int i = 1; i + 1 < parts.Count; i += 2)
            {
                if (string.Equals(parts[i].Trim(), "Income", StringComparison.OrdinalIgnoreCase) &&
                    decimal.TryParse(parts[i + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out var income))
                {
                    return income;
                }
            }

            return 0m;
        }

        private static bool TryParseStructuredDueDate(string value, out DateTime dueDate)
        {
            return DateTime.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate) ||
                   DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dueDate);
        }

        private static string RebuildNotesText(SortedDictionary<int, string> notesByLineNumber)
        {
            if (notesByLineNumber.Count == 0)
                return string.Empty;

            int maxLineNumber = notesByLineNumber.Keys.Max();
            var lines = new List<string>(maxLineNumber);
            for (int i = 1; i <= maxLineNumber; i++)
            {
                lines.Add(notesByLineNumber.TryGetValue(i, out var noteLine) ? noteLine : string.Empty);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
