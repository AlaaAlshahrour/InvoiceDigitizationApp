using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Validation;

public interface IInvoiceValidationService
{
    /// <summary>
    /// Checks invoice arithmetic and required fields. Pure and synchronous — no I/O — so
    /// it can run on every keystroke in the verification grid.
    /// </summary>
    /// <param name="requireCatalogLinks">
    /// When true, also requires the invoice to name a stored contact and every line to
    /// point at a stored product. The verification screen sets this, because that is the
    /// screen where those links can still be established; an invoice read back out of
    /// the database is judged on its own contents.
    /// </param>
    ValidationResult Validate(Invoice invoice, bool requireCatalogLinks = false);

    /// <summary>True when Quantity × UnitPrice equals TotalPrice within tolerance.</summary>
    bool IsLineArithmeticValid(InvoiceItem item);
}
