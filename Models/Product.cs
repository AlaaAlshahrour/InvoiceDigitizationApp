namespace InvoiceDigitizationApp.Models;

/// <summary>
/// A catalog product. The name is the whole record: it is the only field OCR can be
/// matched against, and prices belong to the invoice line rather than to the catalog.
/// </summary>
public sealed class Product
{
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
}
