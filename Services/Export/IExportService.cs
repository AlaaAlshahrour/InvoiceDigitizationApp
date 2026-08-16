using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Export;

public enum ExportFormat
{
    Csv,
    Excel
}

public interface IExportService
{
    /// <summary>
    /// Writes one row per invoice header. Returns the path written.
    /// </summary>
    Task<string> ExportInvoicesAsync(
        IReadOnlyList<Invoice> invoices, string destinationPath, ExportFormat format,
        CancellationToken ct = default);

    /// <summary>
    /// Writes a single invoice: header block plus its line items. For Excel this is two
    /// worksheets; for CSV, a header section followed by an items section.
    /// </summary>
    Task<string> ExportSingleInvoiceAsync(
        Invoice invoice, string destinationPath, ExportFormat format,
        CancellationToken ct = default);

    /// <summary>Conventional extension for the format, including the leading dot.</summary>
    string GetExtension(ExportFormat format);
}
