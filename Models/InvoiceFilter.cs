using System;

namespace InvoiceDigitizationApp.Models;

/// <summary>
/// Search and filter criteria for the Invoices Dashboard. Every property is optional;
/// null means "no constraint on this field".
/// </summary>
public sealed class InvoiceFilter
{
    /// <summary>Matched against invoice number and merchant name.</summary>
    public string? SearchText { get; set; }

    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public InvoiceType? Type { get; set; }
    public string? City { get; set; }
    public int? CustomerId { get; set; }
}
