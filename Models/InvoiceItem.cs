namespace InvoiceDigitizationApp.Models;

public sealed class InvoiceItem
{
    public int ItemId { get; set; }
    public int InvoiceId { get; set; }
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Set when the line was matched to the Products catalog; null otherwise.</summary>
    public int? ProductId { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The line total as printed on the invoice. Stored rather than computed so the
    /// verification screen can show the user where the paper disagrees with itself.
    /// </summary>
    public decimal TotalPrice { get; set; }

    public string? Notes { get; set; }

    public decimal ExpectedTotal => Quantity * UnitPrice;
}
