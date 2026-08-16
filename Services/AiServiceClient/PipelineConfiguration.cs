using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

// The C# half of docs/settings-config-contract.md. The Pydantic models in
// Invoice-Extraction-pipeline/config/settings_contract.py are the other half.

/// <summary>
/// One named preprocessing step: whether it runs, which algorithm implements it, and
/// that algorithm's parameters.
/// </summary>
/// <remarks>
/// Disabling a step means <see cref="Enabled"/> = false, never omitting the key: the
/// configuration always describes the whole pipeline rather than a subset the service
/// has to guess at, and an omitted key is rejected as invalid.
/// </remarks>
public sealed class PipelineStep
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = string.Empty;

    /// <summary>
    /// Free-form, because the valid keys depend on <see cref="Algorithm"/> rather than
    /// on which step this is. <see cref="Pipeline.PipelineCatalog"/> holds the schema the
    /// settings UI renders a form from.
    /// </summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonNode?> Params { get; set; } = new();

    public PipelineStep Clone() => new()
    {
        Enabled = Enabled,
        Algorithm = Algorithm,
        Params = CloneParams(Params)
    };

    internal static Dictionary<string, JsonNode?> CloneParams(
        Dictionary<string, JsonNode?> source)
    {
        // JsonNode instances carry a parent pointer and cannot be shared between two
        // documents, so a clone has to deep-copy rather than alias them.
        var copy = new Dictionary<string, JsonNode?>(source.Count);
        foreach (var (key, value) in source) copy[key] = value?.DeepClone();
        return copy;
    }
}

/// <summary>
/// All eight preprocessing steps. Execution order is fixed by the service (Phase 1 →
/// Phase 2 → Phase 3) and is deliberately <b>not</b> derived from the order of these
/// properties — a JSON object has no order to rely on.
/// </summary>
public sealed class PreprocessingConfiguration
{
    // Phase 1 — get the document
    [JsonPropertyName("perspective_correction")] public PipelineStep PerspectiveCorrection { get; set; } = new();
    [JsonPropertyName("deskew")] public PipelineStep Deskew { get; set; } = new();

    // Phase 2 — normalize the surface
    [JsonPropertyName("channel_selection")] public PipelineStep ChannelSelection { get; set; } = new();
    [JsonPropertyName("illumination_normalization")] public PipelineStep IlluminationNormalization { get; set; } = new();
    [JsonPropertyName("contrast_enhancement")] public PipelineStep ContrastEnhancement { get; set; } = new();

    // Phase 3 — binarization and cleaning
    [JsonPropertyName("denoising")] public PipelineStep Denoising { get; set; } = new();
    [JsonPropertyName("thresholding")] public PipelineStep Thresholding { get; set; } = new();
    [JsonPropertyName("morphological_cleanup")] public PipelineStep MorphologicalCleanup { get; set; } = new();

    /// <summary>The eight steps keyed by their contract name, for generic iteration.</summary>
    public IReadOnlyDictionary<string, PipelineStep> ByKey() => new Dictionary<string, PipelineStep>
    {
        ["perspective_correction"] = PerspectiveCorrection,
        ["deskew"] = Deskew,
        ["channel_selection"] = ChannelSelection,
        ["illumination_normalization"] = IlluminationNormalization,
        ["contrast_enhancement"] = ContrastEnhancement,
        ["denoising"] = Denoising,
        ["thresholding"] = Thresholding,
        ["morphological_cleanup"] = MorphologicalCleanup
    };

    /// <summary>Replaces the step stored under <paramref name="key"/>.</summary>
    public void Set(string key, PipelineStep step)
    {
        switch (key)
        {
            case "perspective_correction": PerspectiveCorrection = step; break;
            case "deskew": Deskew = step; break;
            case "channel_selection": ChannelSelection = step; break;
            case "illumination_normalization": IlluminationNormalization = step; break;
            case "contrast_enhancement": ContrastEnhancement = step; break;
            case "denoising": Denoising = step; break;
            case "thresholding": Thresholding = step; break;
            case "morphological_cleanup": MorphologicalCleanup = step; break;
        }
    }
}

public sealed class OcrConfiguration
{
    [JsonPropertyName("engine")] public string Engine { get; set; } = "tesseract";

    [JsonPropertyName("engine_params")]
    public Dictionary<string, JsonNode?> EngineParams { get; set; } = new();
}

public sealed class TableExtractionConfiguration
{
    [JsonPropertyName("extractor")] public string Extractor { get; set; } = "grid_line";

    [JsonPropertyName("extractor_params")]
    public Dictionary<string, JsonNode?> ExtractorParams { get; set; } = new();
}

public sealed class StringMatchingConfiguration
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "levenshtein";

    [JsonPropertyName("algorithm_params")]
    public Dictionary<string, JsonNode?> AlgorithmParams { get; set; } = new();

    /// <summary>
    /// Service-local path to the keyword dictionary. Not user-editable — there is no
    /// file picker across the HTTP boundary — but it has to be round-tripped or the
    /// service would receive a configuration with no dictionary at all.
    /// </summary>
    [JsonPropertyName("dictionary_path")]
    public string DictionaryPath { get; set; } = "keywords/ar_invoice_terms.json";
}

public sealed class OutputConfiguration
{
    [JsonPropertyName("formatter")] public string Formatter { get; set; } = "ui_overlay_json";

    [JsonPropertyName("formatter_params")]
    public Dictionary<string, JsonNode?> FormatterParams { get; set; } = new();
}

public sealed class PersistenceConfiguration
{
    [JsonPropertyName("store")] public string Store { get; set; } = "file_result_store";

    [JsonPropertyName("store_params")]
    public Dictionary<string, JsonNode?> StoreParams { get; set; } = new();
}

/// <summary>
/// The whole <c>configuration</c> object sent with an extract request, and the thing the
/// settings page edits.
/// </summary>
public sealed class PipelineConfiguration
{
    [JsonPropertyName("preprocessing")]
    public PreprocessingConfiguration Preprocessing { get; set; } = new();

    [JsonPropertyName("ocr")] public OcrConfiguration Ocr { get; set; } = new();

    [JsonPropertyName("table_extraction")]
    public TableExtractionConfiguration TableExtraction { get; set; } = new();

    [JsonPropertyName("string_matching")]
    public StringMatchingConfiguration StringMatching { get; set; } = new();

    [JsonPropertyName("output")] public OutputConfiguration Output { get; set; } = new();

    [JsonPropertyName("persistence")]
    public PersistenceConfiguration Persistence { get; set; } = new();

    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// An independent copy. The settings page edits a copy so cancelling a change is
    /// simply discarding it, rather than undoing edits already made in place.
    /// </summary>
    public PipelineConfiguration Clone() =>
        JsonSerializer.Deserialize<PipelineConfiguration>(
            JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;
}
