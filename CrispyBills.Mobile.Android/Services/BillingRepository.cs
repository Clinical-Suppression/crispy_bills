using CrispyBills.Mobile.Android.Models;
using Microsoft.Data.Sqlite;

namespace CrispyBills.Mobile.Android.Services;

/// <summary>
/// Concrete on-device repository implementation using SQLite files stored under
/// the application's data folder. Provides per-year DB access, notes/meta storage,
/// and simple recovery/backup behavior used by <see cref="BillingService"/>.
/// </summary>
public sealed class BillingRepository : IBillingRepository
{
    private const string NotesDatabaseName = "CrispyBills_Notes.db";
    private const string BackupSuffix = ".prewrite.bak";
    private readonly string _dataRoot;

    public BillingRepository()
    {
        _dataRoot = Path.Combine(FileSystem.Current.AppDataDirectory, "CrispyBills");
        Directory.CreateDirectory(_dataRoot);
    }

    /// <summary>Return the full path to the SQLite database file for a given year.</summary>
    /// <param name="year">Target year.</param>
    public string GetYearDatabasePath(int year)
    {
        return Path.Combine(_dataRoot, $"CrispyBills_{year}.db");
    }

    /// <summary>Enumerate years that have persisted database files available.</summary>
    public IReadOnlyList<int> GetAvailableYears()
    {
        var years = new List<int>();
        foreach (var path in Directory.GetFiles(_dataRoot, "CrispyBills_*.db"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(fileName, "CrispyBills_Notes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            const string prefix = "CrispyBills_";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = fileName[prefix.Length..];
            if (int.TryParse(suffix, out var year))
            {
                years.Add(year);
            }
        }

        return years.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>Ensure the per-year database exists and contains expected schema.</summary>
    public async Task InitializeYearAsync(int year)
    {
        var connectionString = BuildConnectionString(GetYearDatabasePath(year));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;");
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = NORMAL;");
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;");

        const string billsSql = @"
CREATE TABLE IF NOT EXISTS Bills (
    Id TEXT NOT NULL,
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Amount REAL NOT NULL,
    DueDate TEXT NOT NULL,
    Paid INTEGER NOT NULL,
    Category TEXT NOT NULL,
    Recurring INTEGER NOT NULL DEFAULT 0,
    RecurrenceEveryMonths INTEGER NOT NULL DEFAULT 1,
    RecurrenceEndMode TEXT NOT NULL DEFAULT 'None',
    RecurrenceEndDate TEXT NULL,
    RecurrenceMaxOccurrences INTEGER NULL
);";

        const string incomeSql = @"
CREATE TABLE IF NOT EXISTS Income (
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Amount REAL NOT NULL,
    PRIMARY KEY (Month, Year)
);";

        await ExecuteNonQueryAsync(connection, billsSql);
        await ExecuteNonQueryAsync(connection, incomeSql);
        await EnsureBillsColumnAsync(connection, "RecurrenceEveryMonths", "INTEGER NOT NULL DEFAULT 1");
        await EnsureBillsColumnAsync(connection, "RecurrenceEndMode", "TEXT NOT NULL DEFAULT 'None'");
        await EnsureBillsColumnAsync(connection, "RecurrenceEndDate", "TEXT NULL");
        await EnsureBillsColumnAsync(connection, "RecurrenceMaxOccurrences", "INTEGER NULL");
    }

    /// <summary>Load year data from disk into a <see cref="YearData"/> instance.</summary>
    /// <param name="year">Year to load.</param>
    public async Task<YearData> LoadYearAsync(int year)
    {
        await InitializeYearAsync(year);
        var data = new YearData();
        var dbPath = GetYearDatabasePath(year);
        var connectionString = BuildConnectionString(dbPath);

        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            await TryRecoverCorruptYearDatabaseAsync(dbPath, ex);
            await using var recovered = new SqliteConnection(BuildConnectionString(dbPath));
            await recovered.OpenAsync();
            return await LoadYearUsingOpenConnectionAsync(year, recovered);
        }

        return await LoadYearUsingOpenConnectionAsync(year, connection);
    }

    private static async Task<YearData> LoadYearUsingOpenConnectionAsync(int year, SqliteConnection connection)
    {
        var data = new YearData();

        const string billsSql = @"
SELECT Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
       RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences
FROM Bills
WHERE Year = $year
ORDER BY Month, DueDate, Name;";

        await using (var billsCommand = connection.CreateCommand())
        {
            billsCommand.CommandText = billsSql;
            billsCommand.Parameters.AddWithValue("$year", year);

            await using var reader = await billsCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var month = reader.GetInt32(1);
                var dueDateRaw = reader.GetString(5);
                DateTime.TryParse(dueDateRaw, out var dueDate);

                var bill = new BillItem
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Month = month,
                    Year = reader.GetInt32(2),
                    Name = reader.GetString(3),
                    Amount = (decimal)reader.GetDouble(4),
                    DueDate = dueDate == default ? new DateTime(year, month, 1) : dueDate,
                    IsPaid = reader.GetInt32(6) == 1,
                    Category = reader.GetString(7),
                    IsRecurring = reader.GetInt32(8) == 1,
                    RecurrenceEveryMonths = reader.IsDBNull(9) ? 1 : Math.Max(1, reader.GetInt32(9)),
                    RecurrenceEndMode = ParseEndMode(reader.IsDBNull(10) ? null : reader.GetString(10)),
                    RecurrenceEndDate = reader.IsDBNull(11)
                        ? null
                        : (DateTime.TryParse(reader.GetString(11), out var endDate) ? endDate : null),
                    RecurrenceMaxOccurrences = reader.IsDBNull(12) ? null : reader.GetInt32(12)
                };

                data.BillsByMonth[month].Add(bill);
            }
        }

        const string incomeSql = @"
SELECT Month, Amount
FROM Income
WHERE Year = $year;";

        await using (var incomeCommand = connection.CreateCommand())
        {
            incomeCommand.CommandText = incomeSql;
            incomeCommand.Parameters.AddWithValue("$year", year);

            await using var reader = await incomeCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                data.IncomeByMonth[reader.GetInt32(0)] = (decimal)reader.GetDouble(1);
            }
        }

        return data;
    }

    /// <summary>Persist the provided <see cref="YearData"/> to the on-disk database.
    /// Creates a backup before writing and performs the save in a transaction.</summary>
    public async Task SaveYearAsync(int year, YearData data)
    {
        await InitializeYearAsync(year);
        var dbPath = GetYearDatabasePath(year);
        var connectionString = BuildConnectionString(dbPath);

        var backupPath = dbPath + BackupSuffix;
        if (File.Exists(dbPath))
        {
            File.Copy(dbPath, backupPath, overwrite: true);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = connection.BeginTransaction();

        try
        {
            await using (var deleteBills = connection.CreateCommand())
            {
                deleteBills.CommandText = "DELETE FROM Bills WHERE Year = $year;";
                deleteBills.Parameters.AddWithValue("$year", year);
                deleteBills.Transaction = tx;
                await deleteBills.ExecuteNonQueryAsync();
            }

            await using (var deleteIncome = connection.CreateCommand())
            {
                deleteIncome.CommandText = "DELETE FROM Income WHERE Year = $year;";
                deleteIncome.Parameters.AddWithValue("$year", year);
                deleteIncome.Transaction = tx;
                await deleteIncome.ExecuteNonQueryAsync();
            }

        const string insertBillSql = @"
INSERT INTO Bills (Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
    RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences)
VALUES ($id, $month, $year, $name, $amount, $dueDate, $paid, $category, $recurring,
    $recurrenceEveryMonths, $recurrenceEndMode, $recurrenceEndDate, $recurrenceMaxOccurrences);";

            foreach (var month in data.BillsByMonth.Keys.OrderBy(k => k))
            {
                foreach (var bill in data.BillsByMonth[month])
                {
                    await using var insertBill = connection.CreateCommand();
                    insertBill.Transaction = tx;
                    insertBill.CommandText = insertBillSql;
                    insertBill.Parameters.AddWithValue("$id", bill.Id.ToString());
                    insertBill.Parameters.AddWithValue("$month", month);
                    insertBill.Parameters.AddWithValue("$year", year);
                    insertBill.Parameters.AddWithValue("$name", bill.Name);
                    insertBill.Parameters.AddWithValue("$amount", (double)bill.Amount);
                    insertBill.Parameters.AddWithValue("$dueDate", bill.DueDate.ToString("yyyy-MM-dd"));
                    insertBill.Parameters.AddWithValue("$paid", bill.IsPaid ? 1 : 0);
                    insertBill.Parameters.AddWithValue("$category", bill.Category);
                    insertBill.Parameters.AddWithValue("$recurring", bill.IsRecurring ? 1 : 0);
                    insertBill.Parameters.AddWithValue("$recurrenceEveryMonths", Math.Max(1, bill.RecurrenceEveryMonths));
                    insertBill.Parameters.AddWithValue("$recurrenceEndMode", bill.RecurrenceEndMode.ToString());
                    insertBill.Parameters.AddWithValue("$recurrenceEndDate", bill.RecurrenceEndDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
                    insertBill.Parameters.AddWithValue("$recurrenceMaxOccurrences", bill.RecurrenceMaxOccurrences ?? (object)DBNull.Value);
                    await insertBill.ExecuteNonQueryAsync();
                }
            }

        const string insertIncomeSql = @"
INSERT INTO Income (Month, Year, Amount)
VALUES ($month, $year, $amount);";

            foreach (var month in data.IncomeByMonth.Keys.OrderBy(k => k))
            {
                await using var insertIncome = connection.CreateCommand();
                insertIncome.Transaction = tx;
                insertIncome.CommandText = insertIncomeSql;
                insertIncome.Parameters.AddWithValue("$month", month);
                insertIncome.Parameters.AddWithValue("$year", year);
                insertIncome.Parameters.AddWithValue("$amount", (double)data.IncomeByMonth[month]);
                await insertIncome.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            try
            {
                await tx.RollbackAsync();
            }
            catch
            {
            }

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, dbPath, overwrite: true);
                File.Delete(backupPath);
            }

            throw;
        }
    }

    /// <summary>Load the free-form notes string from the notes database.</summary>
    public async Task<string> LoadNotesAsync()
    {
        await InitializeNotesAsync();
        var notesPath = Path.Combine(_dataRoot, NotesDatabaseName);

        await using var connection = new SqliteConnection(BuildConnectionString(notesPath));
        await connection.OpenAsync();

        const string sql = "SELECT Value FROM AppMeta WHERE Key = 'Notes' LIMIT 1;";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>Persist free-form notes to the notes metadata store.</summary>
    public async Task SaveNotesAsync(string notes)
    {
        await InitializeNotesAsync();
        var notesPath = Path.Combine(_dataRoot, NotesDatabaseName);

        await using var connection = new SqliteConnection(BuildConnectionString(notesPath));
        await connection.OpenAsync();

        const string sql = @"
INSERT INTO AppMeta (Key, Value)
VALUES ('Notes', $value)
ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$value", notes);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Read an application-level metadata value by key.</summary>
    public async Task<string?> GetAppMetaAsync(string key)
    {
        await InitializeNotesAsync();
        var notesPath = Path.Combine(_dataRoot, NotesDatabaseName);
        await using var connection = new SqliteConnection(BuildConnectionString(notesPath));
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppMeta WHERE Key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    /// <summary>Set an application-level metadata key/value pair.</summary>
    public async Task SetAppMetaAsync(string key, string value)
    {
        await InitializeNotesAsync();
        var notesPath = Path.Combine(_dataRoot, NotesDatabaseName);
        await using var connection = new SqliteConnection(BuildConnectionString(notesPath));
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AppMeta (Key, Value)
VALUES ($key, $value)
ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Root directory used for storing DB files and notes.</summary>
    public string DataRoot => _dataRoot;

    private async Task InitializeNotesAsync()
    {
        var notesPath = Path.Combine(_dataRoot, NotesDatabaseName);

        await using var connection = new SqliteConnection(BuildConnectionString(notesPath));
        await connection.OpenAsync();

        const string createMetaSql = @"
CREATE TABLE IF NOT EXISTS AppMeta (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);";

        await ExecuteNonQueryAsync(connection, createMetaSql);
    }

    private static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        return builder.ToString();
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureBillsColumnAsync(SqliteConnection connection, string columnName, string definition)
    {
        var exists = false;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Bills);";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await ExecuteNonQueryAsync(connection, $"ALTER TABLE Bills ADD COLUMN {columnName} {definition};");
        }
    }

    private static RecurrenceEndMode ParseEndMode(string? raw)
    {
        if (Enum.TryParse<RecurrenceEndMode>(raw ?? string.Empty, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return RecurrenceEndMode.None;
    }

    private static async Task TryRecoverCorruptYearDatabaseAsync(string dbPath, Exception source)
    {
        var backupPath = dbPath + BackupSuffix;
        var markerPath = dbPath + ".corrupt." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";

        var note = $"[{DateTime.Now:O}] failed to open DB '{dbPath}'. Exception: {source}";
        await File.WriteAllTextAsync(markerPath, note);
        StartupDiagnostics.AddIssue("DatabaseRecovery", note);

        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, dbPath, overwrite: true);
            File.Delete(backupPath);
            StartupDiagnostics.AddIssue("DatabaseRecovery", $"Recovered year DB from backup: {backupPath}");
        }
    }
}
