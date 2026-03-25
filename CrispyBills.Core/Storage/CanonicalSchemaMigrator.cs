using Microsoft.Data.Sqlite;

namespace CrispyBills.Core.Storage;

public static class CanonicalSchemaMigrator
{
    public const int CurrentSchemaVersion = 1;

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    public static async Task EnsureCanonicalSchemaAsync(SqliteConnection connection)
    {
        await using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync();

        try
        {
            await EnsureBillsCanonicalAsync(connection);
            await EnsureIncomeCanonicalAsync(connection);
            await SetUserVersionAsync(connection, CurrentSchemaVersion);

            await using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }
        catch
        {
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
            throw;
        }
    }

    public static void EnsureCanonicalSchema(SqliteConnection connection)
    {
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        try
        {
            EnsureBillsCanonical(connection);
            EnsureIncomeCanonical(connection);
            SetUserVersion(connection, CurrentSchemaVersion);

            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        }
        catch
        {
            using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            throw;
        }
    }

    private static async Task EnsureBillsCanonicalAsync(SqliteConnection connection)
    {
        var shape = await ReadTableShapeAsync(connection, "Bills");
        if (shape.Count == 0)
        {
            await CreateCanonicalBillsAsync(connection);
            return;
        }

        if (shape.TryGetValue("Month", out var monthType)
            && monthType.Contains("INT", StringComparison.OrdinalIgnoreCase)
            && shape.TryGetValue("Year", out var yearType)
            && yearType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureBillsColumnsAsync(connection);
            return;
        }

        await MigrateBillsToCanonicalAsync(connection);
        await EnsureBillsColumnsAsync(connection);
    }

    private static async Task EnsureIncomeCanonicalAsync(SqliteConnection connection)
    {
        var shape = await ReadTableShapeAsync(connection, "Income");
        if (shape.Count == 0)
        {
            await CreateCanonicalIncomeAsync(connection);
            return;
        }

        if (shape.TryGetValue("Month", out var monthType)
            && monthType.Contains("INT", StringComparison.OrdinalIgnoreCase)
            && shape.TryGetValue("Year", out var yearType)
            && yearType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await MigrateIncomeToCanonicalAsync(connection);
    }

    private static void EnsureBillsCanonical(SqliteConnection connection)
    {
        var shape = ReadTableShape(connection, "Bills");
        if (shape.Count == 0)
        {
            CreateCanonicalBills(connection);
            return;
        }

        if (shape.TryGetValue("Month", out var monthType)
            && monthType.Contains("INT", StringComparison.OrdinalIgnoreCase)
            && shape.TryGetValue("Year", out var yearType)
            && yearType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            EnsureBillsColumns(connection);
            return;
        }

        MigrateBillsToCanonical(connection);
        EnsureBillsColumns(connection);
    }

    private static void EnsureIncomeCanonical(SqliteConnection connection)
    {
        var shape = ReadTableShape(connection, "Income");
        if (shape.Count == 0)
        {
            CreateCanonicalIncome(connection);
            return;
        }

        if (shape.TryGetValue("Month", out var monthType)
            && monthType.Contains("INT", StringComparison.OrdinalIgnoreCase)
            && shape.TryGetValue("Year", out var yearType)
            && yearType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MigrateIncomeToCanonical(connection);
    }

    private static async Task<Dictionary<string, string>> ReadTableShapeAsync(SqliteConnection connection, string tableName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            result[name] = type;
        }

        return result;
    }

    private static Dictionary<string, string> ReadTableShape(SqliteConnection connection, string tableName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            result[name] = type;
        }

        return result;
    }

    private static async Task CreateCanonicalBillsAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = CanonicalBillsSql("Bills");
        await cmd.ExecuteNonQueryAsync();
    }

    private static void CreateCanonicalBills(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CanonicalBillsSql("Bills");
        cmd.ExecuteNonQuery();
    }

    private static async Task CreateCanonicalIncomeAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Income (
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Amount REAL NOT NULL,
    PRIMARY KEY (Month, Year)
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static void CreateCanonicalIncome(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Income (
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Amount REAL NOT NULL,
    PRIMARY KEY (Month, Year)
);";
        cmd.ExecuteNonQuery();
    }

    private static async Task MigrateBillsToCanonicalAsync(SqliteConnection connection)
    {
        var sourceCount = await GetRowCountAsync(connection, "Bills");
        var shape = await ReadTableShapeAsync(connection, "Bills");

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = CanonicalBillsSql("Bills_canonical");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
INSERT INTO Bills_canonical (
    Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
    RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences,
    RecurrenceFrequency, RecurrenceGroupId
)
SELECT
    Id,
    {BuildMonthToIntSql("Month")},
    CAST(Year AS INTEGER),
    Name,
    Amount,
    DueDate,
    {ColumnOrDefault(shape, "Paid", "Paid", "0")},
    {ColumnOrDefault(shape, "Category", "Category", "'General'")},
    {ColumnOrDefault(shape, "Recurring", "Recurring", "0")},
    {ColumnOrDefault(shape, "RecurrenceEveryMonths", "COALESCE(RecurrenceEveryMonths, 1)", "1")},
    {ColumnOrDefault(shape, "RecurrenceEndMode", "COALESCE(RecurrenceEndMode, 'None')", "'None'")},
    {ColumnOrDefault(shape, "RecurrenceEndDate", "RecurrenceEndDate", "NULL")},
    {ColumnOrDefault(shape, "RecurrenceMaxOccurrences", "RecurrenceMaxOccurrences", "NULL")},
    {ColumnOrDefault(shape, "RecurrenceFrequency", "COALESCE(RecurrenceFrequency, 'MonthlyInterval')", "'MonthlyInterval'")},
    {ColumnOrDefault(shape, "RecurrenceGroupId", "RecurrenceGroupId", "NULL")}
FROM Bills
WHERE {BuildMonthToIntSql("Month")} BETWEEN 1 AND 12
  AND CAST(Year AS INTEGER) > 0;";
            await cmd.ExecuteNonQueryAsync();
        }

        var migratedCount = await GetRowCountAsync(connection, "Bills_canonical");
        ValidateMigrationCounts("Bills", sourceCount, migratedCount);

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE Bills;";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var rename = connection.CreateCommand())
        {
            rename.CommandText = "ALTER TABLE Bills_canonical RENAME TO Bills;";
            await rename.ExecuteNonQueryAsync();
        }
    }

    private static void MigrateBillsToCanonical(SqliteConnection connection)
    {
        var sourceCount = GetRowCount(connection, "Bills");
        var shape = ReadTableShape(connection, "Bills");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = CanonicalBillsSql("Bills_canonical");
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
INSERT INTO Bills_canonical (
    Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring,
    RecurrenceEveryMonths, RecurrenceEndMode, RecurrenceEndDate, RecurrenceMaxOccurrences,
    RecurrenceFrequency, RecurrenceGroupId
)
SELECT
    Id,
    {BuildMonthToIntSql("Month")},
    CAST(Year AS INTEGER),
    Name,
    Amount,
    DueDate,
    {ColumnOrDefault(shape, "Paid", "Paid", "0")},
    {ColumnOrDefault(shape, "Category", "Category", "'General'")},
    {ColumnOrDefault(shape, "Recurring", "Recurring", "0")},
    {ColumnOrDefault(shape, "RecurrenceEveryMonths", "COALESCE(RecurrenceEveryMonths, 1)", "1")},
    {ColumnOrDefault(shape, "RecurrenceEndMode", "COALESCE(RecurrenceEndMode, 'None')", "'None'")},
    {ColumnOrDefault(shape, "RecurrenceEndDate", "RecurrenceEndDate", "NULL")},
    {ColumnOrDefault(shape, "RecurrenceMaxOccurrences", "RecurrenceMaxOccurrences", "NULL")},
    {ColumnOrDefault(shape, "RecurrenceFrequency", "COALESCE(RecurrenceFrequency, 'MonthlyInterval')", "'MonthlyInterval'")},
    {ColumnOrDefault(shape, "RecurrenceGroupId", "RecurrenceGroupId", "NULL")}
FROM Bills
WHERE {BuildMonthToIntSql("Month")} BETWEEN 1 AND 12
  AND CAST(Year AS INTEGER) > 0;";
            cmd.ExecuteNonQuery();
        }

        var migratedCount = GetRowCount(connection, "Bills_canonical");
        ValidateMigrationCounts("Bills", sourceCount, migratedCount);

        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE Bills;";
            drop.ExecuteNonQuery();
        }

        using (var rename = connection.CreateCommand())
        {
            rename.CommandText = "ALTER TABLE Bills_canonical RENAME TO Bills;";
            rename.ExecuteNonQuery();
        }
    }

    private static async Task MigrateIncomeToCanonicalAsync(SqliteConnection connection)
    {
        var sourceCount = await GetRowCountAsync(connection, "Income");

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Income_canonical (
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Amount REAL NOT NULL,
    PRIMARY KEY (Month, Year)
);";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
INSERT OR REPLACE INTO Income_canonical (Month, Year, Amount)
SELECT
    {BuildMonthToIntSql("Month")},
    CAST(Year AS INTEGER),
    Amount
FROM Income
WHERE {BuildMonthToIntSql("Month")} BETWEEN 1 AND 12
  AND CAST(Year AS INTEGER) > 0;";
            await cmd.ExecuteNonQueryAsync();
        }

        var migratedCount = await GetRowCountAsync(connection, "Income_canonical");
        ValidateMigrationCounts("Income", sourceCount, migratedCount);

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE Income;";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var rename = connection.CreateCommand())
        {
            rename.CommandText = "ALTER TABLE Income_canonical RENAME TO Income;";
            await rename.ExecuteNonQueryAsync();
        }
    }

    private static void MigrateIncomeToCanonical(SqliteConnection connection)
    {
        var sourceCount = GetRowCount(connection, "Income");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Income_canonical (
    Month INTEGER NOT NULL,
    Year INTEGER NOT NULL,
    Amount REAL NOT NULL,
    PRIMARY KEY (Month, Year)
);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
INSERT OR REPLACE INTO Income_canonical (Month, Year, Amount)
SELECT
    {BuildMonthToIntSql("Month")},
    CAST(Year AS INTEGER),
    Amount
FROM Income
WHERE {BuildMonthToIntSql("Month")} BETWEEN 1 AND 12
  AND CAST(Year AS INTEGER) > 0;";
            cmd.ExecuteNonQuery();
        }

        var migratedCount = GetRowCount(connection, "Income_canonical");
        ValidateMigrationCounts("Income", sourceCount, migratedCount);

        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE Income;";
            drop.ExecuteNonQuery();
        }

        using (var rename = connection.CreateCommand())
        {
            rename.CommandText = "ALTER TABLE Income_canonical RENAME TO Income;";
            rename.ExecuteNonQuery();
        }
    }

    private static async Task EnsureBillsColumnsAsync(SqliteConnection connection)
    {
        await EnsureColumnAsync(connection, "Bills", "RecurrenceEveryMonths", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(connection, "Bills", "RecurrenceEndMode", "TEXT NOT NULL DEFAULT 'None'");
        await EnsureColumnAsync(connection, "Bills", "RecurrenceEndDate", "TEXT NULL");
        await EnsureColumnAsync(connection, "Bills", "RecurrenceMaxOccurrences", "INTEGER NULL");
        await EnsureColumnAsync(connection, "Bills", "RecurrenceFrequency", "TEXT NOT NULL DEFAULT 'MonthlyInterval'");
        await EnsureColumnAsync(connection, "Bills", "RecurrenceGroupId", "TEXT NULL");
    }

    private static void EnsureBillsColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "Bills", "RecurrenceEveryMonths", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "Bills", "RecurrenceEndMode", "TEXT NOT NULL DEFAULT 'None'");
        EnsureColumn(connection, "Bills", "RecurrenceEndDate", "TEXT NULL");
        EnsureColumn(connection, "Bills", "RecurrenceMaxOccurrences", "INTEGER NULL");
        EnsureColumn(connection, "Bills", "RecurrenceFrequency", "TEXT NOT NULL DEFAULT 'MonthlyInterval'");
        EnsureColumn(connection, "Bills", "RecurrenceGroupId", "TEXT NULL");
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string ddl)
    {
        var shape = await ReadTableShapeAsync(connection, table);
        if (shape.ContainsKey(column))
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl};";
        await cmd.ExecuteNonQueryAsync();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string ddl)
    {
        var shape = ReadTableShape(connection, table);
        if (shape.ContainsKey(column))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl};";
        cmd.ExecuteNonQuery();
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, int value)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {value};";
        await cmd.ExecuteNonQueryAsync();
    }

    private static void SetUserVersion(SqliteConnection connection, int value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {value};";
        cmd.ExecuteNonQuery();
    }

    private static string CanonicalBillsSql(string tableName) => $@"
CREATE TABLE IF NOT EXISTS {tableName} (
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
    RecurrenceMaxOccurrences INTEGER NULL,
    RecurrenceFrequency TEXT NOT NULL DEFAULT 'MonthlyInterval',
    RecurrenceGroupId TEXT NULL
);";

    private static string BuildMonthToIntSql(string column)
    {
        var cases = new List<string>
        {
            $"WHEN TYPEOF({column}) = 'integer' THEN CAST({column} AS INTEGER)"
        };

        for (var i = 0; i < MonthNames.Length; i++)
        {
            var month = MonthNames[i];
            cases.Add($"WHEN LOWER(TRIM({column})) = '{month.ToLowerInvariant()}' THEN {i + 1}");
        }

        cases.Add($"WHEN CAST({column} AS INTEGER) BETWEEN 1 AND 12 THEN CAST({column} AS INTEGER)");
        return $"CASE {string.Join(" ", cases)} ELSE 0 END";
    }

    private static async Task<int> GetRowCountAsync(SqliteConnection connection, string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static int GetRowCount(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    private static void ValidateMigrationCounts(string tableName, int sourceCount, int migratedCount)
    {
        if (sourceCount == migratedCount)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{tableName} migration would drop {sourceCount - migratedCount} row(s). " +
            "The original table was left unchanged.");
    }

    private static string ColumnOrDefault(
        IReadOnlyDictionary<string, string> shape,
        string columnName,
        string expressionWhenPresent,
        string expressionWhenMissing)
    {
        return shape.ContainsKey(columnName) ? expressionWhenPresent : expressionWhenMissing;
    }
}
