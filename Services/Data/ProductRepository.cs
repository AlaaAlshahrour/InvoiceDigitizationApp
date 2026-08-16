using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public sealed class ProductRepository : IProductRepository
{
    private readonly IDbConnectionFactory _factory;

    public ProductRepository(IDbConnectionFactory factory) => _factory = factory;

    // The table is ProductId + Name only, and both map straight onto the model, so no
    // intermediate row type is needed here.
    private const string Columns = "ProductId, Name";

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Product>(new CommandDefinition(
            $"SELECT {Columns} FROM Products ORDER BY Name",
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<Product?> GetByIdAsync(int productId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Product>(new CommandDefinition(
            $"SELECT {Columns} FROM Products WHERE ProductId = @Id",
            new { Id = productId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> CreateAsync(Product product, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO Products (Name) VALUES (@Name);
            SELECT last_insert_rowid();
            """,
            new { product.Name },
            cancellationToken: ct)).ConfigureAwait(false);

        product.ProductId = id;
        return id;
    }

    public async Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Products SET Name = @Name WHERE ProductId = @ProductId",
            new { product.ProductId, product.Name },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int productId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Products WHERE ProductId = @Id",
            new { Id = productId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Product?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Product>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM Products
            WHERE Name = @Name COLLATE NOCASE LIMIT 1
            """,
            new { Name = name }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
