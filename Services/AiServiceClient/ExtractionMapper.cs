using System;
using System.Collections.Generic;
using System.Globalization;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

/// <summary>
/// Converts a service <see cref="ExtractionResult"/> into a domain <see cref="Invoice"/>
/// for the verification screen. Deliberately lossy: confidences and bounding boxes are
/// UI concerns tracked separately by the ViewModel, not persisted with the invoice.
/// </summary>
public static class ExtractionMapper
{
    public static Invoice ToInvoice(ExtractionResult result, InvoiceType invoiceType)
    {
        var header = result.Header;

        var invoice = new Invoice
        {
            MerchantName = PreferMatch(header?.MerchantName) ?? string.Empty,
            InvoiceNumber = header?.InvoiceNumber?.Value,
            City = header?.City?.Value,
            InvoiceDate = ParseDate(header?.InvoiceDate?.Value),
            TotalAmount = header?.TotalAmount?.Value ?? 0m,
            InvoiceType = invoiceType,

            // The service matched the merchant — by name or by alias — against the
            // Customers catalog it was handed, so the contact is already resolved.
            CustomerId = header?.MerchantName?.MatchedId
        };

        foreach (var line in result.LineItems)
        {
            invoice.Items.Add(new InvoiceItem
            {
                ProductName = PreferMatch(line.ProductName) ?? string.Empty,
                ProductId = line.ProductName?.MatchedId,
                Quantity = line.Quantity?.Value ?? 0m,
                UnitPrice = line.UnitPrice?.Value ?? 0m,
                TotalPrice = line.TotalPrice?.Value ?? 0m
            });
        }

        // When OCR found no printed total, fall back to the sum of the lines so the user
        // starts from a sensible number instead of zero.
        if (invoice.TotalAmount == 0m && invoice.Items.Count > 0)
            invoice.TotalAmount = invoice.ComputedTotal;

        return invoice;
    }

    /// <summary>
    /// Collects per-field confidences keyed by field name, so the verification screen can
    /// flag anything the service was unsure about.
    /// </summary>
    public static Dictionary<string, double> CollectHeaderConfidences(ExtractionResult result)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var header = result.Header;
        if (header is null) return map;

        if (header.MerchantName is { } m) map[nameof(Invoice.MerchantName)] = m.Confidence;
        if (header.InvoiceNumber is { } n) map[nameof(Invoice.InvoiceNumber)] = n.Confidence;
        if (header.InvoiceDate is { } d) map[nameof(Invoice.InvoiceDate)] = d.Confidence;
        if (header.City is { } c) map[nameof(Invoice.City)] = c.Confidence;
        if (header.TotalAmount is { } t) map[nameof(Invoice.TotalAmount)] = t.Confidence;

        return map;
    }

    /// <summary>
    /// Prefers the catalog entry the value was matched to over the raw OCR text: a
    /// confident match to a known merchant is more useful than the OCR's spelling of it.
    /// </summary>
    private static string? PreferMatch(ExtractedField<string>? field)
    {
        if (field is null) return null;
        if (!string.IsNullOrWhiteSpace(field.MatchedTo)) return field.MatchedTo;
        return string.IsNullOrWhiteSpace(field.Value) ? null : field.Value;
    }

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
