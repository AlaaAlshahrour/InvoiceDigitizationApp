using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

// These types are the C# half of docs/api-contract.md. The Pydantic models in
// Invoice-Extraction-pipeline/api/schemas.py are the other half. Change the doc first,
// then both sides. Unknown JSON properties are ignored, so additive server changes stay
// compatible.

/// <summary>
/// One entry of a field's ranked candidate list.
/// </summary>
/// <remarks>
/// <see cref="SimilarityScore"/> is a <b>percentage</b>, 0–100, which is what the
/// dropdown displays beside each entry. The field-level
/// <see cref="ExtractedField{T}.MatchScore"/> is the same number as a 0–1 fraction, kept
/// for the confidence rendering that already works that way.
/// </remarks>
public sealed class MatchCandidate
{
    [JsonPropertyName("matched_value")]
    public string MatchedValue { get; set; } = string.Empty;

    [JsonPropertyName("similarity_score")]
    public double SimilarityScore { get; set; }

    /// <summary>Primary key of the catalog record, when the candidate came from one.</summary>
    [JsonPropertyName("matched_id")]
    public int? MatchedId { get; set; }

    /// <summary>The specific name that scored, when it differs from the canonical value.</summary>
    [JsonPropertyName("matched_name")]
    public string? MatchedName { get; set; }

    /// <summary>What the picker shows: the name, with how well it scored.</summary>
    public string DisplayText => $"{MatchedValue}  ({SimilarityScore:0.#}%)";
}

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

    /// <summary>
    /// Up to five ranked alternatives, best first. The verification screen pre-selects
    /// the first in an editable combo box and offers the rest, so a near-miss costs one
    /// click rather than a re-typed line.
    /// </summary>
    [JsonPropertyName("candidates")]
    public List<MatchCandidate> Candidates { get; set; } = new();

    /// <summary>
    /// True when the top candidate was too weak to accept unseen — or when there was
    /// none. These are the fields the screen highlights for the user to confirm.
    /// </summary>
    [JsonPropertyName("requires_manual_review")]
    public bool RequiresManualReview { get; set; }

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

    /// <summary>
    /// A whole number by the service's normalization rules: a separator inside a
    /// handwritten quantity is a mis-read stroke, not a decimal point.
    /// </summary>
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

/// <summary>
/// One box on the page, for the overlay on the verification screen. Everything the OCR
/// engine found appears here, including text no invoice field claimed.
/// </summary>
public sealed class OverlayElement
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>"table_cell" or "free_field".</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }

    /// <summary>[x1, y1, x2, y2] in preprocessed-image pixel space.</summary>
    [JsonPropertyName("bbox")] public int[]? BoundingBox { get; set; }

    [JsonPropertyName("raw_text")] public string? RawText { get; set; }
    [JsonPropertyName("corrected_text")] public string? CorrectedText { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("candidates")] public List<MatchCandidate> Candidates { get; set; } = new();
    [JsonPropertyName("editable")] public bool Editable { get; set; } = true;

    [JsonPropertyName("table_id")] public string? TableId { get; set; }
    [JsonPropertyName("row")] public int? Row { get; set; }
    [JsonPropertyName("col")] public int? Column { get; set; }
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

    /// <summary>The pipeline's own id for this run, and the folder name under its
    /// results directory. Useful when reporting a bad extraction to the AI team.</summary>
    [JsonPropertyName("invoice_id")] public string? InvoiceId { get; set; }

    /// <summary>Which engine actually read the page, as opposed to which was configured.</summary>
    [JsonPropertyName("ocr_engine")] public string? OcrEngine { get; set; }

    /// <summary>Every detected box, for the image overlay.</summary>
    [JsonPropertyName("elements")] public List<OverlayElement> Elements { get; set; } = new();

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

/// <summary>
/// A city or governorate as a match target. There is no Cities table: the app sends the
/// distinct values of Customers.City, so the service scores the OCR text against places
/// this installation actually deals with rather than a generic gazetteer.
/// </summary>
public sealed class KnownCity
{
    [JsonPropertyName("city_id")] public int? CityId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
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

    /// <summary>The cities this installation knows, from the Customers table.</summary>
    [JsonPropertyName("known_cities")]
    public List<KnownCity> KnownCities { get; set; } = new();

    /// <summary>How many ranked alternatives to return per matched field.</summary>
    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; set; } = 5;

    [JsonPropertyName("return_debug_images")]
    public bool ReturnDebugImages { get; set; }

    /// <summary>
    /// The full pipeline configuration, per docs/settings-config-contract.md. Null means
    /// "use the service's own defaults", which is what an installation that has never
    /// opened the settings page sends.
    /// </summary>
    [JsonPropertyName("configuration")]
    public PipelineConfiguration? Configuration { get; set; }
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
