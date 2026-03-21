using System;
using System.Linq;
using Xunit;

namespace CrispyBills.Mobile.ParityTests;

public sealed class ImportExportHelpersTests
{
    [Fact]
    public void ParseCsvLine_HandlesQuotedCommasAndEscapedQuotes()
    {
        var line = "Name,Category,\"Amount,USD\",\"Note with \"\"quoted\"\" text\"";
        var fields = CrispyBills.ImportExportHelpers.ParseCsvLine(line, _ => { });

        Assert.Equal(4, fields.Count);
        Assert.Equal("Name", fields[0]);
        Assert.Equal("Category", fields[1]);
        Assert.Equal("Amount,USD", fields[2]);
        Assert.Equal("Note with \"quoted\" text", fields[3]);
    }

    [Fact]
    public void ParseStructuredReportCsv_BasicImportScansCorrectly()
    {
        var lines = new[]
        {
            "REPORT,Crispy_Bills Export",
            "",
            "===== YEAR =====,2026",
            "--- MONTH ---,January",
            "MONTH SUMMARY,Income,1000,Expenses,0,Paid,0,Remaining,0,Net,1000,Bill Count,0",
            "Name,Category,Amount,Due Date,Status,Recurring,Past Due,Month,Year",
            "Rent,Housing,1200,01/15/2026,DUE,No,No,January,2026",
            "===== YEAR =====,2027",
            "--- MONTH ---,February",
            "MONTH SUMMARY,Income,1500,Expenses,0,Paid,0,Remaining,0,Net,1500,Bill Count,0",
            "Name,Category,Amount,Due Date,Status,Recurring,Past Due,Month,Year",
            "Internet,Utilities,80,02/15/2027,DUE,No,No,February,2027"
        };

        var pkg = CrispyBills.ImportExportHelpers.ParseStructuredReportCsv(lines, new[]
        {
            "January","February","March","April","May","June","July","August","September","October","November","December"
        }, "C:\\Temp", _ => { }, writeDiagnostics: false);

        Assert.Equal(2, pkg.Years.Count);
        Assert.Single(pkg.Years["2026"].BillsByMonth["January"]);
        Assert.Single(pkg.Years["2027"].BillsByMonth["February"]);
        Assert.False(pkg.HasNotesSection);
    }
}
