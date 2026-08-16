namespace InvoiceDigitizationApp.Models;

public sealed class Customer
{
    public int CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The merchant name as it appears on paper invoices, which is often an
    /// abbreviation or alternate transliteration of <see cref="Name"/>. This is the
    /// primary fuzzy-match target for OCR merchant output.
    /// </summary>
    public string? AliasName { get; set; }

    public string? City { get; set; }
    public string? Phone { get; set; }
    public CustomerType CustomerType { get; set; } = CustomerType.Customer;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(AliasName) ? Name : $"{Name} ({AliasName})";
}
