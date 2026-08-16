using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace InvoiceDigitizationApp.Services.Data;

/// <summary>
/// Applies forward-only schema migrations at startup. The applied version is tracked in
/// AppSettings['SchemaVersion'] so upgrades are idempotent across app launches.
/// </summary>
public sealed class DatabaseInitializer
{
    private const string SchemaVersionKey = "SchemaVersion";

    /// <summary>
    /// Ordered migrations. Append only — never edit or reorder an existing entry, since
    /// databases in the field have already applied it.
    /// </summary>
    private static readonly IReadOnlyList<string> Migrations = new[]
    {
        // ---- v1: initial schema -------------------------------------------------
        """
        CREATE TABLE IF NOT EXISTS Customers (
            CustomerId   INTEGER PRIMARY KEY AUTOINCREMENT,
            Name         TEXT    NOT NULL,
            AliasName    TEXT,
            City         TEXT,
            Phone        TEXT,
            CustomerType TEXT    NOT NULL DEFAULT 'Customer'
                         CHECK (CustomerType IN ('Customer', 'Supplier'))
        );
        CREATE INDEX IF NOT EXISTS IX_Customers_Name  ON Customers(Name);
        CREATE INDEX IF NOT EXISTS IX_Customers_Alias ON Customers(AliasName);

        -- Name is the whole product record: it is the only thing OCR can match against,
        -- and line prices belong to the invoice, not to the catalog.
        CREATE TABLE IF NOT EXISTS Products (
            ProductId    INTEGER PRIMARY KEY AUTOINCREMENT,
            Name         TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Products_Name ON Products(Name);

        CREATE TABLE IF NOT EXISTS Invoices (
            InvoiceId     INTEGER PRIMARY KEY AUTOINCREMENT,
            InvoiceNumber TEXT,
            MerchantName  TEXT    NOT NULL,
            City          TEXT,
            InvoiceDate   TEXT,
            TotalAmount   REAL    NOT NULL DEFAULT 0,
            InvoiceType   TEXT    NOT NULL DEFAULT 'Purchase'
                          CHECK (InvoiceType IN ('Sale', 'Purchase')),
            ImagePath     TEXT,
            -- Preprocessed renderings kept beside the original: the enhanced grayscale
            -- page, and the exact binary image the OCR engine read.
            EnhancedImagePath TEXT,
            OcrImagePath      TEXT,
            CustomerId    INTEGER REFERENCES Customers(CustomerId) ON DELETE SET NULL,
            CreatedAt     TEXT    NOT NULL DEFAULT (datetime('now')),
            ContentHash   TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Invoices_Date     ON Invoices(InvoiceDate);
        CREATE INDEX IF NOT EXISTS IX_Invoices_Merchant ON Invoices(MerchantName);
        CREATE INDEX IF NOT EXISTS IX_Invoices_Customer ON Invoices(CustomerId);
        CREATE INDEX IF NOT EXISTS IX_Invoices_Hash     ON Invoices(ContentHash);

        CREATE TABLE IF NOT EXISTS InvoiceItems (
            ItemId      INTEGER PRIMARY KEY AUTOINCREMENT,
            InvoiceId   INTEGER NOT NULL REFERENCES Invoices(InvoiceId) ON DELETE CASCADE,
            ProductName TEXT    NOT NULL,
            ProductId   INTEGER REFERENCES Products(ProductId) ON DELETE SET NULL,
            Quantity    REAL    NOT NULL DEFAULT 0,
            UnitPrice   REAL    NOT NULL DEFAULT 0,
            TotalPrice  REAL    NOT NULL DEFAULT 0,
            Notes       TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_InvoiceItems_Invoice ON InvoiceItems(InvoiceId);
        CREATE INDEX IF NOT EXISTS IX_InvoiceItems_Product ON InvoiceItems(ProductId);

        CREATE TABLE IF NOT EXISTS AppSettings (
            Key   TEXT PRIMARY KEY,
            Value TEXT
        );
        """
    };

    private readonly IDbConnectionFactory _factory;

    public DatabaseInitializer(IDbConnectionFactory factory) => _factory = factory;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // AppSettings must exist before it can record the version, and the very first
        // migration is what creates it — so bootstrap it separately.
        connection.Execute(
            "CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");

        var current = GetSchemaVersion(connection);

        for (var version = current; version < Migrations.Count; version++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Migrations[version], transaction: transaction);
            SetSchemaVersion(connection, transaction, version + 1);
            transaction.Commit();
        }

        SeedDefaultSettings(connection);
    }

    private static int GetSchemaVersion(IDbConnection connection)
    {
        var raw = connection.QuerySingleOrDefault<string>(
            "SELECT Value FROM AppSettings WHERE Key = @Key", new { Key = SchemaVersionKey });

        return int.TryParse(raw, out var version) ? version : 0;
    }

    private static void SetSchemaVersion(IDbConnection connection, IDbTransaction transaction, int version)
    {
        connection.Execute(
            """
            INSERT INTO AppSettings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """,
            new { Key = SchemaVersionKey, Value = version.ToString() },
            transaction);
    }

    /// <summary>
    /// Inserts defaults for any setting the user has not configured. Uses DO NOTHING so a
    /// user's existing choice is never overwritten on a later launch.
    /// </summary>
    private static void SeedDefaultSettings(IDbConnection connection)
    {
        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.PythonServiceUrl] = "http://127.0.0.1:8765",
            [SettingKeys.PythonServiceExecutable] = string.Empty,
            [SettingKeys.AutoStartPythonService] = "false",
            [SettingKeys.ExportDirectory] = AppPaths.DefaultExportDirectory,
            [SettingKeys.ImageStorageDirectory] = AppPaths.DefaultImageDirectory,
            [SettingKeys.AppTheme] = "Default",
            [SettingKeys.ConfidenceThreshold] = "0.75"
        };

        foreach (var (key, value) in defaults)
        {
            connection.Execute(
                "INSERT INTO AppSettings (Key, Value) VALUES (@Key, @Value) ON CONFLICT(Key) DO NOTHING;",
                new { Key = key, Value = value });
        }
    }
}
