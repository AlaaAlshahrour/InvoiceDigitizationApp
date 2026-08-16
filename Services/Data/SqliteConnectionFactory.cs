using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace InvoiceDigitizationApp.Services.Data;

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps the WAL file warm and avoids re-opening cost per call.
            Pooling = true
        }.ToString();
    }

    public async Task<IDbConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // foreign_keys is per-connection in SQLite and defaults to OFF, so it must be
        // set every time — setting it once at startup would silently not apply here.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }
}
