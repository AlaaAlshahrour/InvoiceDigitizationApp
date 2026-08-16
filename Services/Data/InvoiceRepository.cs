using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly IDbConnectionFactory _factory;

    public InvoiceRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string HeaderColumns = """
        InvoiceId, InvoiceNumber, MerchantName, City, InvoiceDate, TotalAmount,
        InvoiceType, ImagePath, EnhancedImagePath, OcrImagePath, CustomerId,
        CreatedAt, ContentHash
        """;

    public async Task<IReadOnlyList<Invoice>> SearchAsync(InvoiceFilter filter, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {HeaderColumns} FROM Invoices WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            // Escape LIKE wildcards so a user searching for "50%" doesn't match everything.
            var escaped = filter.SearchText
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

            sql.Append("""
                 AND (MerchantName LIKE @Search ESCAPE '\'
                   OR InvoiceNumber LIKE @Search ESCAPE '\')
                """);
            parameters.Add("Search", $"%{escaped}%");
        }

        if (filter.DateFrom is { } from)
        {
            sql.Append(" AND InvoiceDate IS NOT NULL AND InvoiceDate >= @DateFrom");
            parameters.Add("DateFrom", from.ToString("yyyy-MM-dd"));
        }

        if (filter.DateTo is { } to)
        {
            sql.Append(" AND InvoiceDate IS NOT NULL AND InvoiceDate <= @DateTo");
            parameters.Add("DateTo", to.ToString("yyyy-MM-dd"));
        }

        if (filter.Type is { } type)
        {
            sql.Append(" AND InvoiceType = @Type");
            parameters.Add("Type", type.ToString());
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            sql.Append(" AND City = @City");
            parameters.Add("City", filter.City);
        }

        if (filter.CustomerId is { } customerId)
        {
            sql.Append(" AND CustomerId = @CustomerId");
            parameters.Add("CustomerId", customerId);
        }

        sql.Append(" ORDER BY COALESCE(InvoiceDate, '') DESC, InvoiceId DESC");

        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<InvoiceRow>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<Invoice?> GetByIdAsync(int invoiceId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<InvoiceRow>(
            new CommandDefinition(
                $"SELECT {HeaderColumns} FROM Invoices WHERE InvoiceId = @Id",
                new { Id = invoiceId }, cancellationToken: ct)).ConfigureAwait(false);

        if (row is null) return null;

        var invoice = row.ToModel();
        var items = await connection.QueryAsync<InvoiceItem>(
            new CommandDefinition(
                """
                SELECT ItemId, InvoiceId, ProductName, ProductId, Quantity, UnitPrice, TotalPrice, Notes
                FROM InvoiceItems WHERE InvoiceId = @Id ORDER BY ItemId
                """,
                new { Id = invoiceId }, cancellationToken: ct)).ConfigureAwait(false);

        invoice.Items = items.ToList();
        return invoice;
    }

    public async Task<int> CreateAsync(Invoice invoice, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var invoiceId = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                INSERT INTO Invoices
                    (InvoiceNumber, MerchantName, City, InvoiceDate, TotalAmount,
                     InvoiceType, ImagePath, EnhancedImagePath, OcrImagePath,
                     CustomerId, CreatedAt, ContentHash)
                VALUES
                    (@InvoiceNumber, @MerchantName, @City, @InvoiceDate, @TotalAmount,
                     @InvoiceType, @ImagePath, @EnhancedImagePath, @OcrImagePath,
                     @CustomerId, @CreatedAt, @ContentHash);
                SELECT last_insert_rowid();
                """,
                ToHeaderParameters(invoice), transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        await InsertItemsAsync(connection, transaction, invoiceId, invoice.Items, ct).ConfigureAwait(false);

        transaction.Commit();

        invoice.InvoiceId = invoiceId;
        return invoiceId;
    }

    public async Task UpdateAsync(Invoice invoice, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Invoices SET
                InvoiceNumber = @InvoiceNumber,
                MerchantName  = @MerchantName,
                City          = @City,
                InvoiceDate   = @InvoiceDate,
                TotalAmount   = @TotalAmount,
                InvoiceType   = @InvoiceType,
                ImagePath     = @ImagePath,
                EnhancedImagePath = @EnhancedImagePath,
                OcrImagePath      = @OcrImagePath,
                CustomerId    = @CustomerId,
                ContentHash   = @ContentHash
            WHERE InvoiceId = @InvoiceId
            """,
            ToHeaderParameters(invoice, includeId: true), transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        // Replace lines wholesale: the verification screen lets the user add, remove and
        // reorder rows freely, so diffing them would be more code for no user-visible gain.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM InvoiceItems WHERE InvoiceId = @Id",
            new { Id = invoice.InvoiceId }, transaction, cancellationToken: ct)).ConfigureAwait(false);

        await InsertItemsAsync(connection, transaction, invoice.InvoiceId, invoice.Items, ct)
            .ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task DeleteAsync(int invoiceId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Invoices WHERE InvoiceId = @Id",
            new { Id = invoiceId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Invoice>> FindByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return Array.Empty<Invoice>();

        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<InvoiceRow>(new CommandDefinition(
            $"SELECT {HeaderColumns} FROM Invoices WHERE ContentHash = @Hash",
            new { Hash = contentHash }, cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<Invoice>> FindSimilarAsync(
        Invoice candidate, int dayTolerance, decimal amountTolerance, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var parameters = new DynamicParameters();
        parameters.Add("Merchant", candidate.MerchantName);
        parameters.Add("Total", (double)candidate.TotalAmount);
        parameters.Add("Tolerance", (double)amountTolerance);
        parameters.Add("ExcludeId", candidate.InvoiceId);

        var sql = new StringBuilder($"""
            SELECT {HeaderColumns} FROM Invoices
            WHERE InvoiceId <> @ExcludeId
              AND MerchantName = @Merchant COLLATE NOCASE
              AND ABS(TotalAmount - @Total) <= @Tolerance
            """);

        // A candidate with no date can still duplicate on merchant + amount alone, so an
        // absent date widens the match rather than excluding everything.
        if (candidate.InvoiceDate is { } date)
        {
            sql.Append("""
                  AND (InvoiceDate IS NULL
                    OR (julianday(InvoiceDate) BETWEEN julianday(@DateFrom) AND julianday(@DateTo)))
                """);
            parameters.Add("DateFrom", date.AddDays(-dayTolerance).ToString("yyyy-MM-dd"));
            parameters.Add("DateTo", date.AddDays(dayTolerance).ToString("yyyy-MM-dd"));
        }

        var rows = await connection.QueryAsync<InvoiceRow>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctCitiesAsync(CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var cities = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT City FROM Invoices
            WHERE City IS NOT NULL AND TRIM(City) <> ''
            ORDER BY City
            """, cancellationToken: ct)).ConfigureAwait(false);

        return cities.ToList();
    }

    private static async Task InsertItemsAsync(
        IDbConnection connection, IDbTransaction transaction,
        int invoiceId, IEnumerable<InvoiceItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            item.InvoiceId = invoiceId;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO InvoiceItems
                    (InvoiceId, ProductName, ProductId, Quantity, UnitPrice, TotalPrice, Notes)
                VALUES
                    (@InvoiceId, @ProductName, @ProductId, @Quantity, @UnitPrice, @TotalPrice, @Notes)
                """,
                new
                {
                    InvoiceId = invoiceId,
                    item.ProductName,
                    item.ProductId,
                    Quantity = (double)item.Quantity,
                    UnitPrice = (double)item.UnitPrice,
                    TotalPrice = (double)item.TotalPrice,
                    item.Notes
                },
                transaction, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    private static DynamicParameters ToHeaderParameters(Invoice invoice, bool includeId = false)
    {
        var parameters = new DynamicParameters();
        if (includeId) parameters.Add("InvoiceId", invoice.InvoiceId);

        parameters.Add("InvoiceNumber", invoice.InvoiceNumber);
        parameters.Add("MerchantName", invoice.MerchantName);
        parameters.Add("City", invoice.City);
        parameters.Add("InvoiceDate", invoice.InvoiceDate?.ToString("yyyy-MM-dd"));
        parameters.Add("TotalAmount", (double)invoice.TotalAmount);
        parameters.Add("InvoiceType", invoice.InvoiceType.ToString());
        parameters.Add("ImagePath", invoice.ImagePath);
        parameters.Add("EnhancedImagePath", invoice.EnhancedImagePath);
        parameters.Add("OcrImagePath", invoice.OcrImagePath);
        parameters.Add("CustomerId", invoice.CustomerId);
        parameters.Add("CreatedAt", invoice.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        parameters.Add("ContentHash", invoice.ContentHash);
        return parameters;
    }

    /// <summary>
    /// Flat row shape matching the SELECT list. Kept separate from the domain model so
    /// enum and date parsing happens in one place rather than via Dapper conventions.
    /// </summary>
    private sealed class InvoiceRow
    {
        public int InvoiceId { get; set; }
        public string? InvoiceNumber { get; set; }
        public string MerchantName { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? InvoiceDate { get; set; }
        public double TotalAmount { get; set; }
        public string? InvoiceType { get; set; }
        public string? ImagePath { get; set; }
        public string? EnhancedImagePath { get; set; }
        public string? OcrImagePath { get; set; }
        public int? CustomerId { get; set; }
        public string? CreatedAt { get; set; }
        public string? ContentHash { get; set; }

        public Invoice ToModel() => new()
        {
            InvoiceId = InvoiceId,
            InvoiceNumber = InvoiceNumber,
            MerchantName = MerchantName,
            City = City,
            InvoiceDate = DateOnly.TryParse(InvoiceDate, out var d) ? d : null,
            TotalAmount = (decimal)TotalAmount,
            InvoiceType = Enum.TryParse<InvoiceType>(InvoiceType, out var t)
                ? t : Models.InvoiceType.Purchase,
            ImagePath = ImagePath,
            EnhancedImagePath = EnhancedImagePath,
            OcrImagePath = OcrImagePath,
            CustomerId = CustomerId,
            CreatedAt = DateTime.TryParse(CreatedAt, out var c) ? c : DateTime.Now,
            ContentHash = ContentHash
        };
    }
}
