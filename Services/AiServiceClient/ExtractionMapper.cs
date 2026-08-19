using System;
using System.Collections.Generic;
using System.Globalization;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

/// <summary>
/// Converts a service <see cref="ExtractionResult"/> into a domain <see cref="Invoice"/>
/// for the verification screen. Deliberately lossy: confidences and bounding boxes are
/// UI concerns the ViewModel reads off the result directly, not values persisted with the
/// invoice.
/// </summary>
/// <remarks>
/// This is where the review threshold is applied to matched fields. The service sends the
/// raw reading and a ranked list and decides nothing; a candidate becomes the value here
/// only when it cleared <see cref="ExtractionThresholds.Review"/>, and otherwise the raw
/// text stands unlinked so the screen can flag it. That is the whole reason the wire shape
/// has no <c>value</c> on those fields — one side applying one threshold, visibly.
/// </remarks>
public static class ExtractionMapper
{
    public static Invoice ToInvoice(ExtractionResult result, InvoiceType invoiceType)
    {
        var customer = result.CustomerName?.Accepted;

        var invoice = new Invoice
        {
            // The catalog name when the match was strong enough to accept, else exactly
            // what OCR read — never a blend of the two.
            MerchantName = customer?.Value ?? result.CustomerName?.OriginalValue ?? string.Empty,

            // Only set when the match was accepted, so an unresolved counterparty stays
            // visibly unresolved rather than pointing at a record nobody chose.
            CustomerId = customer?.EntryId,

            InvoiceNumber = Trimmed(result.InvoiceId?.Value),
            InvoiceDate = ParseDate(result.Date?.Value),
            TotalAmount = result.TotalInvoicePrice?.Value ?? 0m,
            InvoiceType = invoiceType
        };

        var city = result.City?.Accepted;
        invoice.City = city?.Value ?? Trimmed(result.City?.OriginalValue);

        foreach (var row in result.Products)
        {
            var product = row.ProductName?.Accepted;

            invoice.Items.Add(new InvoiceItem
            {
                ProductName = product?.Value ?? row.ProductName?.OriginalValue ?? string.Empty,
                ProductId = product?.EntryId,
                Quantity = row.Quantity?.Value ?? 0m,
                UnitPrice = row.UnitPrice?.Value ?? 0m,
                TotalPrice = row.TotalPrice?.Value ?? 0m
            });
        }

        // When OCR found no printed total, fall back to the sum of the lines so the user
        // starts from a sensible number instead of zero.
        if (invoice.TotalAmount == 0m && invoice.Items.Count > 0)
            invoice.TotalAmount = invoice.ComputedTotal;

        return invoice;
    }

    /// <summary>
    /// Per-header-field OCR confidences keyed by the Invoice property they belong to, so
    /// the verification screen can flag anything the service was unsure about.
    /// </summary>
    public static Dictionary<string, double> CollectHeaderConfidences(ExtractionResult result)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (result.CustomerName is { } customer)
            map[nameof(Invoice.MerchantName)] = customer.OcrConfidence;
        if (result.InvoiceId is { } number)
            map[nameof(Invoice.InvoiceNumber)] = number.OcrConfidence;
        if (result.Date is { } date)
            map[nameof(Invoice.InvoiceDate)] = date.OcrConfidence;
        if (result.City is { } city)
            map[nameof(Invoice.City)] = city.OcrConfidence;
        if (result.TotalInvoicePrice is { } total)
            map[nameof(Invoice.TotalAmount)] = total.OcrConfidence;

        return map;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // The contract guarantees ISO-8601, but a stray format shouldn't lose the invoice.
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var exact)
            ? exact
            : DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
                ? loose
                : null;
    }
}
