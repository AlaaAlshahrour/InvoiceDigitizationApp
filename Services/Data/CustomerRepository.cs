using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly IDbConnectionFactory _factory;

    public CustomerRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string Columns = "CustomerId, Name, AliasName, City, Phone, CustomerType";

    public async Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Customers ORDER BY Name", cancellationToken: ct))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Customers WHERE CustomerId = @Id",
            new { Id = customerId }, cancellationToken: ct)).ConfigureAwait(false);

        return row?.ToModel();
    }

    public async Task<int> CreateAsync(Customer customer, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO Customers (Name, AliasName, City, Phone, CustomerType)
            VALUES (@Name, @AliasName, @City, @Phone, @CustomerType);
            SELECT last_insert_rowid();
            """,
            new
            {
                customer.Name,
                customer.AliasName,
                customer.City,
                customer.Phone,
                CustomerType = customer.CustomerType.ToString()
            },
            cancellationToken: ct)).ConfigureAwait(false);

        customer.CustomerId = id;
        return id;
    }

    public async Task UpdateAsync(Customer customer, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Customers SET
                Name = @Name, AliasName = @AliasName, City = @City,
                Phone = @Phone, CustomerType = @CustomerType
            WHERE CustomerId = @CustomerId
            """,
            new
            {
                customer.CustomerId,
                customer.Name,
                customer.AliasName,
                customer.City,
                customer.Phone,
                CustomerType = customer.CustomerType.ToString()
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int customerId, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Invoices.CustomerId is ON DELETE SET NULL, so invoice history survives; the
        // invoice simply becomes unlinked while keeping its MerchantName text.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Customers WHERE CustomerId = @Id",
            new { Id = customerId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class Row
    {
        public int CustomerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? AliasName { get; set; }
        public string? City { get; set; }
        public string? Phone { get; set; }
        public string? CustomerType { get; set; }

        public Customer ToModel() => new()
        {
            CustomerId = CustomerId,
            Name = Name,
            AliasName = AliasName,
            City = City,
            Phone = Phone,
            CustomerType = Enum.TryParse<CustomerType>(CustomerType, out var t)
                ? t : Models.CustomerType.Customer
        };
    }
}
