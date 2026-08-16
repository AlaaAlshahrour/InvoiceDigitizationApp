using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public interface IInvoiceRepository
{
    /// <summary>Returns invoice headers matching the filter, newest first. Items are not loaded.</summary>
    Task<IReadOnlyList<Invoice>> SearchAsync(InvoiceFilter filter, CancellationToken ct = default);

    /// <summary>Returns a single invoice with its line items, or null if not found.</summary>
    Task<Invoice?> GetByIdAsync(int invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Inserts the invoice and all its line items in one transaction and returns the new
    /// InvoiceId. A partially written invoice is not possible.
    /// </summary>
    Task<int> CreateAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Updates the header and replaces the line items wholesale, in one transaction.
    /// </summary>
    Task UpdateAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>Deletes the invoice; line items cascade.</summary>
    Task DeleteAsync(int invoiceId, CancellationToken ct = default);

    /// <summary>Invoices whose stored ContentHash equals the given hash (exact re-import).</summary>
    Task<IReadOnlyList<Invoice>> FindByContentHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Invoices with the same merchant, a date within <paramref name="dayTolerance"/> days,
    /// and a total within <paramref name="amountTolerance"/>. Used for near-duplicate detection.
    /// </summary>
    Task<IReadOnlyList<Invoice>> FindSimilarAsync(
        Invoice candidate, int dayTolerance, decimal amountTolerance, CancellationToken ct = default);

    /// <summary>Distinct non-empty city values, for populating the dashboard filter.</summary>
    Task<IReadOnlyList<string>> GetDistinctCitiesAsync(CancellationToken ct = default);
}
