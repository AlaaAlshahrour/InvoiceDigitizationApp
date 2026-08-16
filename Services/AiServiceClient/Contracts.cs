using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

// These types are the C# half of docs/api-contract.md. The Pydantic models in
// ai-service/app/schemas/ are the other half. Change the doc first, then both sides.
// Unknown JSON properties are ignored, so additive server changes stay compatible.

/// <summary>
/// Envelope shared by every extracted field, carrying the value plus the confidence and
/// catalog-match metadata the verification screen renders.
/// </summary>
/// <typeparam name="T">string for text fields, decimal? for numeric ones.</typeparam>
public sealed class ExtractedField<T>
{
    [JsonPropertyName("value")]
    public T? Value { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Pre-normalization text, when normalization changed the value.</summary>
    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    /// <summary>
    /// Canonical catalog entry this value was fuzzy-matched to, if any. For a merchant
    /// this is always the Customers.Name, even when the hit came through an alias.
    /// </summary>
    [JsonPropertyName("matched_to")]
    public string? MatchedTo { get; set; }

    /// <summary>
    /// Primary key of the matched catalog row (CustomerId or ProductId), letting the app
    /// bind straight to the record instead of re-resolving the name.
    /// </summary>
    [JsonPropertyName("matched_id")]
    public int? MatchedId { get; set; }

    /// <summary>
    /// The specific name that scored the match — the alias rather than the canonical
    /// name when the invoice was printed with an alias.
    /// </summary>
    [JsonPropertyName("matched_name")]
    public string? MatchedName { get; set; }

    [JsonPropertyName("match_score")]
    public double? MatchScore { get; set; }

    /// <summary>[x1, y1, x2, y2] in preprocessed-image pixel space, or null.</summary>
    [JsonPropertyName("bbox")]
    public int[]? BoundingBox { get; set; }
}

public sealed class ExtractionSource
{
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("page_used")] public int PageUsed { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class ExtractedHeader
{
    [JsonPropertyName("merchant_name")] public ExtractedField<string>? MerchantName { get; set; }
    [JsonPropertyName("invoice_number")] public ExtractedField<string>? InvoiceNumber { get; set; }
    [JsonPropertyName("invoice_date")] public ExtractedField<string>? InvoiceDate { get; set; }
    [JsonPropertyName("city")] public ExtractedField<string>? City { get; set; }
    [JsonPropertyName("total_amount")] public ExtractedField<decimal>? TotalAmount { get; set; }
}

public sealed class ExtractedLineItem
{
    [JsonPropertyName("row_index")] public int RowIndex { get; set; }
    [JsonPropertyName("product_name")] public ExtractedField<string>? ProductName { get; set; }
    [JsonPropertyName("quantity")] public ExtractedField<decimal>? Quantity { get; set; }
    [JsonPropertyName("unit_price")] public ExtractedField<decimal>? UnitPrice { get; set; }
    [JsonPropertyName("total_price")] public ExtractedField<decimal>? TotalPrice { get; set; }

    /// <summary>
    /// The service's own arithmetic check. Advisory only — the C# side re-validates
    /// independently via IInvoiceValidationService and does not trust this flag.
    /// </summary>
    [JsonPropertyName("arithmetic_ok")] public bool ArithmeticOk { get; set; }
}

public sealed class ExtractionWarning
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("field")] public string? Field { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class ExtractionResult
{
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("processing_ms")] public long ProcessingMs { get; set; }
    [JsonPropertyName("source")] public ExtractionSource? Source { get; set; }
    [JsonPropertyName("header")] public ExtractedHeader? Header { get; set; }
    [JsonPropertyName("line_items")] public List<ExtractedLineItem> LineItems { get; set; } = new();
    [JsonPropertyName("warnings")] public List<ExtractionWarning> Warnings { get; set; } = new();
    [JsonPropertyName("raw_text")] public string? RawText { get; set; }

    /// <summary>
    /// Base64 PNG of the enhanced grayscale page — the preprocessing output just before
    /// binarization. Null unless <see cref="ExtractionOptions.ReturnDebugImages"/> was set.
    /// Downscaled to the service's debug_image_max_width, so
    /// <see cref="ExtractedField{T}.BoundingBox"/> coordinates do not map onto it 1:1.
    /// </summary>
    [JsonPropertyName("enhanced_image_png")] public string? EnhancedImagePng { get; set; }

    /// <summary>
    /// Base64 PNG of the exact image the OCR engine read (binarized). Same nullability and
    /// downscaling caveats as <see cref="EnhancedImagePng"/>.
    /// </summary>
    [JsonPropertyName("ocr_input_image_png")] public string? OcrInputImagePng { get; set; }
}

/// <summary>
/// One contact from the Customers table, sent as a match target. <see cref="Name"/> and
/// every entry in <see cref="Aliases"/> are equivalent names: a hit on any of them
/// identifies this record, and the service answers with <see cref="Name"/> plus
/// <see cref="CustomerId"/>.
/// </summary>
public sealed class KnownMerchant
{
    [JsonPropertyName("customer_id")] public int? CustomerId { get; set; }

    /// <summary>The canonical Customers.Name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Alternate spellings — currently Customers.AliasName, when set.</summary>
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
}

/// <summary>
/// One row of the Products table as a match target. The catalog carries nothing but an
/// id and a name, so the name is the entire matching surface.
/// </summary>
public sealed class KnownProduct
{
    [JsonPropertyName("product_id")] public int? ProductId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

/// <summary>Options sent alongside the file upload. All fields are optional server-side.</summary>
public sealed class ExtractionOptions
{
    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = new() { "ar", "en" };

    [JsonPropertyName("invoice_type")]
    public string? InvoiceType { get; set; }

    /// <summary>
    /// The Customers table as match targets, each carrying its full set of equivalent
    /// names (Name plus AliasName) so the service can match either and still answer with
    /// the record.
    /// </summary>
    [JsonPropertyName("known_merchants")]
    public List<KnownMerchant> KnownMerchants { get; set; } = new();

    /// <summary>The Products table as match targets, matched on name alone.</summary>
    [JsonPropertyName("known_products")]
    public List<KnownProduct> KnownProducts { get; set; } = new();

    [JsonPropertyName("return_debug_images")]
    public bool ReturnDebugImages { get; set; }
}

public sealed class HealthStatus
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("ocr_engine")] public string? OcrEngine { get; set; }
    [JsonPropertyName("engine_ready")] public bool EngineReady { get; set; }
    [JsonPropertyName("languages")] public List<string> Languages { get; set; } = new();
}

internal sealed class ErrorEnvelope
{
    [JsonPropertyName("error")] public ErrorBody? Error { get; set; }

    internal sealed class ErrorBody
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("detail")] public string? Detail { get; set; }
    }
}
