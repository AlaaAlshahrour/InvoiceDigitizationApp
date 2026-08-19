using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.Services.Validation;

/// <summary>
/// Codes for the warnings this app derives from an extraction. They match the names the
/// service used to send under its own <c>warnings</c> array, so a support conversation
/// about "a LOW_CONFIDENCE_FIELD warning" still means the same thing.
/// </summary>
public static class ExtractionWarningCodes
{
    /// <summary>OCR itself was unsure about a reading.</summary>
    public const string LowConfidenceField = "LOW_CONFIDENCE_FIELD";

    /// <summary>A catalog match was too weak to accept, or there was none.</summary>
    public const string ManualReviewRequired = "MANUAL_REVIEW_REQUIRED";

    /// <summary>The item table came back empty.</summary>
    public const string NoLineItems = "NO_LINE_ITEMS";

    /// <summary>A line's quantity × unit price does not equal its total.</summary>
    public const string ArithmeticMismatch = "ARITHMETIC_MISMATCH";

    /// <summary>The lines do not sum to the stated invoice total.</summary>
    public const string TotalMismatch = "TOTAL_MISMATCH";
}

/// <summary>
/// One thing about an extraction worth the user's attention.
/// </summary>
/// <param name="Code">One of <see cref="ExtractionWarningCodes"/>.</param>
/// <param name="Field">
/// The field it concerns, in the wire's own naming (<c>customer_name</c>,
/// <c>products[2].product_name</c>), or null for a whole-invoice warning.
/// </param>
public sealed record ExtractionWarning(string Code, string? Field, string Message);

public interface IExtractionWarningBuilder
{
    /// <summary>
    /// Everything worth flagging about one extraction, most serious first.
    /// </summary>
    /// <param name="invoice">
    /// The mapped invoice, for the arithmetic checks. Passed separately because those run
    /// against the values the screen will actually save — including any the user has
    /// already corrected — rather than against the raw response.
    /// </param>
    IReadOnlyList<ExtractionWarning> Build(ExtractionResult result, Invoice invoice);
}

/// <summary>
/// Derives the extraction's warnings on this side, from the confidences and scores the
/// response already carries plus the app's own <see cref="IInvoiceValidationService"/>.
/// </summary>
/// <remarks>
/// The service used to send a <c>warnings</c> array of its own. It no longer does, and
/// that is deliberate: every input needed to compute one is in the response, and the two
/// arithmetic warnings were being calculated twice — once there and once here, by
/// implementations free to disagree. The app cannot act on a verdict it did not compute
/// (it re-validates every invoice anyway, hand-typed ones included), so the service's copy
/// was a second source of truth with no reader.
///
/// The thresholds are <see cref="ExtractionThresholds.Review"/> on both sides of the
/// contract; the arithmetic tolerances belong to
/// <see cref="InvoiceValidationService"/> and are not repeated here.
/// </remarks>
public sealed class ExtractionWarningBuilder : IExtractionWarningBuilder
{
    private readonly IInvoiceValidationService _validation;

    public ExtractionWarningBuilder(IInvoiceValidationService validation) =>
        _validation = validation;

    public IReadOnlyList<ExtractionWarning> Build(ExtractionResult result, Invoice invoice)
    {
        var warnings = new List<ExtractionWarning>();

        AddMatchWarnings(result, warnings);
        AddConfidenceWarnings(result, warnings);
        AddArithmeticWarnings(invoice, warnings);

        if (result.Products.Count == 0)
        {
            warnings.Add(new ExtractionWarning(
                ExtractionWarningCodes.NoLineItems,
                null,
                "لم يُعثر على أي بند في جدول الفاتورة."));
        }

        // Arithmetic first: a number that does not add up is a fact about the invoice,
        // while a weak match is a suggestion the user can accept or replace in one click.
        return warnings
            .OrderBy(w => Rank(w.Code))
            .ToList();
    }

    private static int Rank(string code) => code switch
    {
        ExtractionWarningCodes.TotalMismatch => 0,
        ExtractionWarningCodes.ArithmeticMismatch => 1,
        ExtractionWarningCodes.NoLineItems => 2,
        ExtractionWarningCodes.ManualReviewRequired => 3,
        _ => 4
    };

    private static void AddMatchWarnings(
        ExtractionResult result, List<ExtractionWarning> warnings)
    {
        foreach (var (field, matched) in result.MatchedFields())
        {
            if (!matched.RequiresManualReview) continue;

            var reading = string.IsNullOrWhiteSpace(matched.OriginalValue)
                ? "الحقل"
                : $"«{matched.OriginalValue}»";

            warnings.Add(new ExtractionWarning(
                ExtractionWarningCodes.ManualReviewRequired,
                WireName(field),
                matched.Results.Count == 0
                    ? $"{reading} لا يطابق أي سجلّ محفوظ. اختر السجلّ الصحيح أو أضِفه."
                    : $"{reading} لم يطابق أي سجلّ بثقة كافية؛ " +
                      $"{matched.Results.Count} اقتراح متاح للاختيار."));
        }
    }

    private static void AddConfidenceWarnings(
        ExtractionResult result, List<ExtractionWarning> warnings)
    {
        void Check(string field, bool isLow, double confidence)
        {
            if (!isLow) return;

            warnings.Add(new ExtractionWarning(
                ExtractionWarningCodes.LowConfidenceField,
                field,
                $"ثقة القراءة {confidence.ToString("0.00", CultureInfo.InvariantCulture)} " +
                $"أقل من {ExtractionThresholds.Review.ToString("0.00", CultureInfo.InvariantCulture)}؛ " +
                "راجع هذا الحقل."));
        }

        if (result.InvoiceId is { } number)
            Check("invoice_id", number.IsLowConfidence, number.OcrConfidence);
        if (result.Date is { } date)
            Check("date", date.IsLowConfidence, date.OcrConfidence);
        if (result.TotalInvoicePrice is { } total)
            Check("total_invoice_price", total.IsLowConfidence, total.OcrConfidence);

        // A weak match already warns about these; only flag them here when the reading
        // itself was poor and the match happened to succeed anyway, which is the case a
        // reviewer would otherwise never look at.
        foreach (var (field, matched) in result.MatchedFields())
        {
            if (matched.RequiresManualReview) continue;
            Check(WireName(field), matched.IsLowConfidence, matched.OcrConfidence);
        }

        for (var i = 0; i < result.Products.Count; i++)
        {
            var row = result.Products[i];

            if (row.Quantity is { } quantity)
                Check($"products[{i}].quantity", quantity.IsLowConfidence, quantity.OcrConfidence);
            if (row.UnitPrice is { } price)
                Check($"products[{i}].unit_price", price.IsLowConfidence, price.OcrConfidence);
            if (row.TotalPrice is { } lineTotal)
                Check($"products[{i}].total_price", lineTotal.IsLowConfidence, lineTotal.OcrConfidence);
        }
    }

    /// <summary>
    /// The two arithmetic warnings, taken from the validation service rather than
    /// recomputed, so there is exactly one implementation of "these numbers do not add up"
    /// and it is the same one that judges a hand-typed invoice.
    /// </summary>
    private void AddArithmeticWarnings(Invoice invoice, List<ExtractionWarning> warnings)
    {
        var result = _validation.Validate(invoice);

        foreach (var issue in result.Issues)
        {
            var code = issue.Code switch
            {
                ValidationCodes.LineArithmeticMismatch =>
                    ExtractionWarningCodes.ArithmeticMismatch,
                ValidationCodes.TotalMismatch => ExtractionWarningCodes.TotalMismatch,
                _ => null
            };

            if (code is null) continue;

            warnings.Add(new ExtractionWarning(
                code,
                issue.ItemIndex is { } index
                    ? $"products[{index}]"
                    : "total_invoice_price",
                issue.Message));
        }
    }

    /// <summary>
    /// The wire's name for a field <see cref="ExtractionResult.MatchedFields"/> reported
    /// under its C# property name, so a warning names what the contract names.
    /// </summary>
    private static string WireName(string field) => field switch
    {
        nameof(ExtractionResult.CustomerName) => "customer_name",
        nameof(ExtractionResult.City) => "city",
        _ => field
            .Replace(nameof(ExtractionResult.Products), "products", StringComparison.Ordinal)
            .Replace(nameof(ProductRow.ProductName), "product_name", StringComparison.Ordinal)
    };
}
