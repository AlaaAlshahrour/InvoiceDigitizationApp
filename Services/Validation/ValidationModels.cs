using System.Collections.Generic;
using System.Linq;

namespace InvoiceDigitizationApp.Services.Validation;

public enum ValidationSeverity
{
    /// <summary>Worth the user's attention but does not prevent saving.</summary>
    Warning,

    /// <summary>Data is internally inconsistent. Still savable — the paper may be wrong.</summary>
    Error
}

public sealed record ValidationIssue(
    string Code,
    string Message,
    ValidationSeverity Severity,
    /// <summary>Index into Invoice.Items, or null for a header-level issue.</summary>
    int? ItemIndex = null,
    string? FieldName = null);

public sealed class ValidationResult
{
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public ValidationResult(IReadOnlyList<ValidationIssue> issues) => Issues = issues;

    public static ValidationResult Empty { get; } = new(new List<ValidationIssue>());

    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);
    public bool IsClean => Issues.Count == 0;

    /// <summary>Row indices with at least one error, for red-highlighting the grid.</summary>
    public IReadOnlySet<int> ErrorRowIndices => Issues
        .Where(i => i.Severity == ValidationSeverity.Error && i.ItemIndex.HasValue)
        .Select(i => i.ItemIndex!.Value)
        .ToHashSet();
}

public static class ValidationCodes
{
    public const string LineArithmeticMismatch = "LINE_ARITHMETIC_MISMATCH";
    public const string TotalMismatch = "TOTAL_MISMATCH";
    public const string MissingMerchant = "MISSING_MERCHANT";
    public const string MissingDate = "MISSING_DATE";
    public const string NoLineItems = "NO_LINE_ITEMS";
    public const string NegativeQuantity = "NEGATIVE_QUANTITY";
    public const string NegativeUnitPrice = "NEGATIVE_UNIT_PRICE";
    public const string MissingProductName = "MISSING_PRODUCT_NAME";
    public const string FutureDate = "FUTURE_DATE";

    /// <summary>A line names a product that is not in the Products table.</summary>
    public const string UnlinkedProduct = "UNLINKED_PRODUCT";

    /// <summary>The invoice is not tied to a stored contact in Customers.</summary>
    public const string UnlinkedCustomer = "UNLINKED_CUSTOMER";
}
