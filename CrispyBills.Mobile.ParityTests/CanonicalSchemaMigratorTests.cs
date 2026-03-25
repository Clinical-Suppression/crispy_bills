using CrispyBills.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrispyBills.Mobile.ParityTests;

public sealed class CanonicalSchemaMigratorTests
{
    [Fact]
    public void EnsureCanonicalSchema_MigratesLegacyDesktopSchema_AndPreservesRows()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            using (var connection = Open(dbPath))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE Bills (
    Id TEXT,
    Month TEXT,
    Year TEXT,
    Name TEXT,
    Amount REAL,
    DueDate TEXT,
    Paid INT,
    Category TEXT,
    Recurring INT NOT NULL DEFAULT 0
);
CREATE TABLE Income (
    Month TEXT,
    Year TEXT,
    Amount REAL,
    PRIMARY KEY (Month, Year)
);
INSERT INTO Bills (Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring)
VALUES
('11111111-1111-1111-1111-111111111111', 'January', '2027', 'Rent', 1200.0, '2027-01-01', 0, 'Housing', 1),
('22222222-2222-2222-2222-222222222222', 'February', '2027', 'Internet', 80.0, '2027-02-15', 1, 'Utilities', 0);
INSERT INTO Income (Month, Year, Amount)
VALUES ('January', '2027', 3000.0), ('February', '2027', 3000.0);";
                cmd.ExecuteNonQuery();
            }

            using (var connection = Open(dbPath))
            {
                CanonicalSchemaMigrator.EnsureCanonicalSchema(connection);
            }

            using (var connection = Open(dbPath))
            {
                using var info = connection.CreateCommand();
                info.CommandText = "PRAGMA table_info(Bills);";
                using var reader = info.ExecuteReader();
                var monthType = string.Empty;
                var yearType = string.Empty;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "Month") monthType = reader.GetString(2);
                    if (reader.GetString(1) == "Year") yearType = reader.GetString(2);
                }

                Assert.Equal("INTEGER", monthType);
                Assert.Equal("INTEGER", yearType);

                using var bills = connection.CreateCommand();
                bills.CommandText = "SELECT COUNT(*), MIN(Month), MAX(Month), MIN(Year), MAX(Year) FROM Bills;";
                using var billsReader = bills.ExecuteReader();
                Assert.True(billsReader.Read());
                Assert.Equal(2L, billsReader.GetInt64(0));
                Assert.Equal(1L, billsReader.GetInt64(1));
                Assert.Equal(2L, billsReader.GetInt64(2));
                Assert.Equal(2027L, billsReader.GetInt64(3));
                Assert.Equal(2027L, billsReader.GetInt64(4));

                using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(1L, (long)(version.ExecuteScalar() ?? 0L));
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public void EnsureCanonicalSchema_ThrowsWhenLegacyRowsCannotBeMapped()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            using (var connection = Open(dbPath))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE Bills (
    Id TEXT,
    Month TEXT,
    Year TEXT,
    Name TEXT,
    Amount REAL,
    DueDate TEXT,
    Paid INT,
    Category TEXT,
    Recurring INT NOT NULL DEFAULT 0
);
INSERT INTO Bills (Id, Month, Year, Name, Amount, DueDate, Paid, Category, Recurring)
VALUES ('33333333-3333-3333-3333-333333333333', 'Janvier', '2027', 'Rent', 1200.0, '2027-01-01', 0, 'Housing', 1);";
                cmd.ExecuteNonQuery();
            }

            using (var connection = Open(dbPath))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => CanonicalSchemaMigrator.EnsureCanonicalSchema(connection));
                Assert.Contains("drop", ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            using (var connection = Open(dbPath))
            {
                using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM Bills;";
                Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));

                using var info = connection.CreateCommand();
                info.CommandText = "PRAGMA table_info(Bills);";
                using var reader = info.ExecuteReader();
                var monthType = string.Empty;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "Month")
                    {
                        monthType = reader.GetString(2);
                    }
                }

                Assert.Equal("TEXT", monthType);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"crispy_migrator_{Guid.NewGuid():N}.db");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
