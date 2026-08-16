using System;
using System.Collections.Generic;

namespace InvoiceDigitizationApp.Models;

public sealed class Invoice
{
    public int InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public string? City { get; set; }

    /// <summary>Date printed on the invoice. Null when OCR could not find one.</summary>
    public DateOnly? InvoiceDate { get; set; }

    public decimal TotalAmount { get; set; }
    public InvoiceType InvoiceType { get; set; } = InvoiceType.Purchase;

    /// <summary>
    /// Absolute path to the imported copy of the source image under the app's image
    /// storage directory — never the user's original file location.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// Absolute path to the saved enhanced grayscale rendering (preprocessing output just
    /// before binarization), or null when the service returned no diagnostic image.
    /// </summary>
    public string? EnhancedImagePath { get; set; }

    /// <summary>
    /// Absolute path to the saved copy of the exact binarized image the OCR engine read,
    /// or null when the service returned no diagnostic image.
    /// </summary>
    public string? OcrImagePath { get; set; }

    public int? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>SHA-256 of the source file bytes, used for exact duplicate detection.</summary>
    public string? ContentHash { get; set; }

    public List<InvoiceItem> Items { get; set; } = new();

    /// <summary>
    /// Sum of the line items. Compared against <see cref="TotalAmount"/> (the total
    /// printed on the invoice) during validation; a divergence is reported, not corrected.
    /// </summary>
    public decimal ComputedTotal
    {
        get
        {
            decimal sum = 0m;
            foreach (var item in Items)
                sum += item.TotalPrice;
            return sum;
        }
    }
}
