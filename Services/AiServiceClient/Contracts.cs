using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

// These types are the C# half of docs/api-contract.md. The Pydantic models in
// Invoice-Extraction-pipeline/api/schemas.py are the other half. Change the doc first,
// then both sides. Unknown JSON properties are ignored, so additive server changes stay
// compatible.

/// <summary>
/// A box on the corrected page: origin plus size, not two corners.
/// </summary>
/// <remarks>
/// Coordinates are in the space of the geometrically corrected page, whose dimensions are
/// <see cref="ExtractionSource.Width"/> × <see cref="ExtractionSource.Height"/> — the same
/// image the verification screen displays, so a box needs no remapping to be drawn on it.
/// The debug renderings are downscaled, so boxes do <b>not</b> map 1:1 onto those.
/// </remarks>
public sealed class BoundingBox
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
}

/// <summary>
/// One ranked catalog entry offered for a matched field.
/// </summary>
public sealed class MatchResult
{
    /// <summary>
    /// The catalog row's primary key, sent as a string. Null for a match that came from no
    /// record — cities usually, since there is no Cities table.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// The canonical catalog name — always Customers.Name / Products.Name, even when an
    /// alias is what scored, so the invoice is filed consistently however it was printed.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>Similarity as a 0–1 fraction, not a percentage.</summary>
    [JsonPropertyName("string_matching_score")]
    public double StringMatchingScore { get; set; }

    /// <summary><see cref="Id"/> as the integer primary key it came from, when it parses.</summary>
    public int? EntryId =>
        int.TryParse(Id, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>What the picker shows: the name, with how well it scored.</summary>
    public string DisplayText => $"{Value}  ({StringMatchingScore * 100:0.#}%)";
}

/// <summary>
/// A field read straight off the page, with nothing to match it against — the invoice
/// number, the date, an amount.
/// </summary>
/// <typeparam name="T">
/// The reading's type, and always a <b>nullable</b> one — <c>string?</c>, <c>int?</c>,
/// <c>decimal?</c>. Null means the field was not found, which is a different thing from a
/// total of zero or an empty invoice number, and a non-nullable <c>decimal</c> here would
/// deserialize the contract's <c>null</c> into exactly that confusion.
/// </typeparam>
public sealed class ValueField<T>
{
    [JsonPropertyName("value")]
    public T Value { get; set; } = default!;

    [JsonPropertyName("ocr_confidence")]
    public double OcrConfidence { get; set; }

    [JsonPropertyName("bounding_box")]
    public BoundingBox? BoundingBox { get; set; }

    /// <summary>True when OCR was unsure enough that the screen should flag the field.</summary>
    public bool IsLowConfidence =>
        Value is not null && OcrConfidence < ExtractionThresholds.Review;
}

/// <summary>
/// A field matched against one of the catalogs the request carried — the counterparty, the
/// city, a product name.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Value</c>. <see cref="OriginalValue"/> is always the raw OCR
/// text and <see cref="Results"/> is what the catalog offered; choosing between them is
/// this app's decision, made here against <see cref="ExtractionThresholds.Review"/>. A
/// field carrying both a value and a candidate list invites two questions the payload
/// cannot answer — was that value read or chosen, and against what threshold — and gets
/// them wrong silently.
/// </remarks>
public sealed class MatchedField
{
    [JsonPropertyName("bounding_box")]
    public BoundingBox? BoundingBox { get; set; }

    /// <summary>Confidence of the OCR reading, not of the match.</summary>
    [JsonPropertyName("ocr_confidence")]
    public double OcrConfidence { get; set; }

    /// <summary>What the paper said. Never a candidate substituted for it.</summary>
    [JsonPropertyName("original_value")]
    public string? OriginalValue { get; set; }

    /// <summary>Ranked catalog entries, best first. Empty when nothing resembled the reading.</summary>
    [JsonPropertyName("results")]
    public List<MatchResult> Results { get; set; } = new();

    /// <summary>The top-ranked entry, or null when the catalog offered nothing.</summary>
    public MatchResult? Best => Results.Count > 0 ? Results[0] : null;

    /// <summary>
    /// True when the best entry was too weak to accept unseen — or when there was none.
    /// These are the fields the verification screen highlights in amber.
    /// </summary>
    public bool RequiresManualReview =>
        Best is null || Best.StringMatchingScore < ExtractionThresholds.Review;

    /// <summary>
    /// The entry to pre-select, or null to leave the raw text standing. This one property
    /// is where the review threshold is applied to a matched field.
    /// </summary>
    public MatchResult? Accepted => RequiresManualReview ? null : Best;

    /// <summary>True when OCR itself was unsure, independently of how the match scored.</summary>
    public bool IsLowConfidence =>
        !string.IsNullOrWhiteSpace(OriginalValue)
        && OcrConfidence < ExtractionThresholds.Review;
}

/// <summary>The confidence floor, shared by both halves of the contract.</summary>
public static class ExtractionThresholds
{
    /// <summary>
    /// Below this, a match is likelier wrong than right and the raw reading stands for the
    /// user to resolve. The same 0.75 the service documents; it applies to OCR confidence
    /// and to match scores alike, so the screen has one number to highlight against.
    /// </summary>
    public const double Review = 0.75;
}

/// <summary>One line of the item table.</summary>
public sealed class ProductRow
{
    [JsonPropertyName("product_name")]
    public MatchedField? ProductName { get; set; }

    /// <summary>
    /// A whole number by the service's normalization rules: a separator inside a
    /// handwritten quantity is a mis-read stroke, not a decimal point.
    /// </summary>
    [JsonPropertyName("quantity")] public ValueField<int?>? Quantity { get; set; }
    [JsonPropertyName("unit_price")] public ValueField<decimal?>? UnitPrice { get; set; }
    [JsonPropertyName("total_price")] public ValueField<decimal?>? TotalPrice { get; set; }
}

public sealed class ExtractionSource
{
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("page_used")] public int PageUsed { get; set; }

    /// <summary>The corrected page's size, and the space every box is measured in.</summary>
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

/// <summary>
/// The whole 200 body. These properties and no others — there is no request id, no
/// warnings array, no raw OCR dump and no overlay element list.
/// </summary>
/// <remarks>
/// Warnings are computed here rather than sent: every input for them is already in this
/// body, and two sides deriving the same warning from the same numbers is one side too
/// many. See <c>Services/Validation/ExtractionWarningBuilder.cs</c>.
/// </remarks>
public sealed class ExtractionResult
{
    [JsonPropertyName("processing_ms")] public long ProcessingMs { get; set; }
    [JsonPropertyName("source")] public ExtractionSource? Source { get; set; }

    /// <summary>
    /// The invoice number printed on the paper — <b>not</b> the pipeline's own id for the
    /// run, which is never sent.
    /// </summary>
    [JsonPropertyName("invoice_id")] public ValueField<string?>? InvoiceId { get; set; }

    [JsonPropertyName("customer_name")] public MatchedField? CustomerName { get; set; }
    [JsonPropertyName("date")] public ValueField<string?>? Date { get; set; }
    [JsonPropertyName("city")] public MatchedField? City { get; set; }
    [JsonPropertyName("products")] public List<ProductRow> Products { get; set; } = new();

    [JsonPropertyName("total_invoice_price")]
    public ValueField<decimal?>? TotalInvoicePrice { get; set; }

    /// <summary>
    /// Base64 PNG of the enhanced grayscale page — the preprocessing output just before
    /// binarization. Null unless <see cref="ExtractionOptions.ReturnDebugImages"/> was set.
    /// Downscaled to the service's debug_image_max_width, so
    /// <see cref="BoundingBox"/> coordinates do not map onto it 1:1.
    /// </summary>
    [JsonPropertyName("enhanced_image_png")] public string? EnhancedImagePng { get; set; }

    /// <summary>
    /// Base64 PNG of the exact image the OCR engine read. Same nullability and downscaling
    /// caveats as <see cref="EnhancedImagePng"/>.
    /// </summary>
    [JsonPropertyName("ocr_input_image_png")] public string? OcrInputImagePng { get; set; }

    /// <summary>
    /// Every matched field on the page — the two header fields plus one per product row.
    /// The warning builder and the status line both walk this rather than repeating the
    /// list of which fields happen to be matched ones.
    /// </summary>
    public IEnumerable<(string Field, MatchedField Value)> MatchedFields()
    {
        if (CustomerName is { } customer) yield return (nameof(CustomerName), customer);
        if (City is { } city) yield return (nameof(City), city);

        for (var i = 0; i < Products.Count; i++)
        {
            if (Products[i].ProductName is { } name)
                yield return ($"{nameof(Products)}[{i}].{nameof(ProductRow.ProductName)}", name);
        }
    }

    /// <summary>
    /// What the paper said, assembled from every field's own reading. Replaces the raw OCR
    /// dump the service used to send: the same text, attributed to the fields it came from
    /// instead of run together into one blob.
    /// </summary>
    public string DetectedText()
    {
        var lines = new List<string?>
        {
            InvoiceId?.Value,
            CustomerName?.OriginalValue,
            Date?.Value,
            City?.OriginalValue
        };

        lines.AddRange(Products.Select(p => string.Join("  ", new[]
        {
            p.ProductName?.OriginalValue,
            Text(p.Quantity?.Value),
            Text(p.UnitPrice?.Value),
            Text(p.TotalPrice?.Value)
        }.Where(part => !string.IsNullOrWhiteSpace(part)))));

        lines.Add(Text(TotalInvoicePrice?.Value));

        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    /// <summary>
    /// Invariant text for a number that may not have been read at all. Invariant because
    /// this is a diagnostic view of what the page said, not a value the user edits — and
    /// the app's thread culture is Arabic, which would render the digits Arabic-Indic and
    /// undo the folding the service just did.
    /// </summary>
    private static string? Text<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One contact from the Customers table, sent as a match target. <see cref="Name"/> and
/// every entry in <see cref="Aliases"/> are equivalent names: a hit on any of them
/// identifies this record, and the service answers with <see cref="Name"/> plus the
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

/// <summary>
/// The <c>options</c> part of an extract request: what this batch is being matched
/// against. The pipeline configuration travels as its own <c>config</c> part, because the
/// two are owned by different screens and change on different schedules.
/// </summary>
/// <remarks>
/// There is no Languages and no InvoiceType. The pipeline is Arabic-primary — every prompt
/// and keyword list in it is written for Arabic — so a language list changed nothing; and
/// whether an invoice is a sale or a purchase is a property of the record this app files,
/// not of the paper being read.
/// </remarks>
public sealed class ExtractionOptions
{
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
