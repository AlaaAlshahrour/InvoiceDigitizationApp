namespace InvoiceDigitizationApp.Models;

/// <summary>
/// Direction of an invoice from the business's point of view. Persisted as the literal
/// strings "Sale"/"Purchase" to match the CHECK constraint on Invoices.InvoiceType.
/// </summary>
public enum InvoiceType
{
    Purchase,
    Sale
}

/// <summary>
/// Persisted as "Customer"/"Supplier" to match the CHECK constraint on
/// Customers.CustomerType.
/// </summary>
public enum CustomerType
{
    Customer,
    Supplier
}
