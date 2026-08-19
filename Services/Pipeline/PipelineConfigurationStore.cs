using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.Services.Pipeline;

/// <summary>
/// Reads and writes the pipeline configuration the app sends with every extraction.
/// </summary>
/// <remarks>
/// Stored as one JSON blob in AppSettings rather than a column per parameter: the shape
/// is owned by the AI team's contract and changes on their schedule, and a schema
/// migration for every new algorithm parameter would be a migration for something this
/// app never interprets.
/// </remarks>
public interface IPipelineConfigurationStore
{
    /// <summary>
    /// The configuration to send with an extraction, or null when the user has never
    /// saved one — in which case the request omits it and the service uses its own
    /// defaults, which is the correct behaviour for a fresh installation.
    /// </summary>
    Task<PipelineConfiguration?> GetAsync(CancellationToken ct = default);

    Task SaveAsync(PipelineConfiguration configuration, CancellationToken ct = default);

    /// <summary>Forgets the saved configuration, returning to the service's defaults.</summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// The configuration to open the settings page on: the saved one if there is one,
    /// otherwise the service's own defaults, otherwise the built-in ones.
    /// </summary>
    Task<PipelineConfiguration> GetForEditingAsync(CancellationToken ct = default);
}

public sealed class PipelineConfigurationStore : IPipelineConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly ISettingsRepository _settings;
    private readonly IAiServiceClient _aiService;

    public PipelineConfigurationStore(ISettingsRepository settings, IAiServiceClient aiService)
    {
        _settings = settings;
        _aiService = aiService;
    }

    public async Task<PipelineConfiguration?> GetAsync(CancellationToken ct = default)
    {
        var json = await _settings.GetAsync(SettingKeys.PipelineConfiguration, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<PipelineConfiguration>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A blob written by an older, incompatible version. Falling back to the
            // service's defaults beats refusing to extract anything at all.
            return null;
        }
    }

    public Task SaveAsync(PipelineConfiguration configuration, CancellationToken ct = default) =>
        _settings.SetAsync(
            SettingKeys.PipelineConfiguration,
            JsonSerializer.Serialize(configuration, JsonOptions),
            ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        _settings.SetAsync(SettingKeys.PipelineConfiguration, string.Empty, ct);

    public async Task<PipelineConfiguration> GetForEditingAsync(CancellationToken ct = default)
    {
        var saved = await GetAsync(ct).ConfigureAwait(false);
        if (saved is not null) return saved;

        // Asking the service means the user sees the defaults it actually runs, rather
        // than a second copy that drifts from them.
        var fromService = await _aiService.GetDefaultConfigurationAsync(ct).ConfigureAwait(false);
        return fromService ?? BuildDefault();
    }

    /// <summary>
    /// The contract's documented defaults, used when the service cannot be reached. The
    /// settings page has to open on something even with the service down.
    /// </summary>
    /// <remarks>
    /// These mirror <c>Invoice-Extraction-pipeline/config/default_config.yaml</c>:
    /// layout_driven + contour_based + bill_layout + padded_crop + qwen, the combination
    /// that actually reads this project's handwritten invoices. The first entry of each
    /// catalog list is the recommended one, so ordering the catalog is what sets the
    /// defaults rather than a second list of names here.
    /// </remarks>
    public static PipelineConfiguration BuildDefault()
    {
        var configuration = new PipelineConfiguration();

        foreach (var definition in PipelineCatalog.Steps)
        {
            var algorithm = definition.Algorithms[0];

            configuration.Preprocessing.Set(definition.Key, new PipelineStep
            {
                // The photometric steps the Qwen recognizer reads better without are
                // disabled, matching the service's own file: thresholding a handwritten
                // stroke is what breaks it into pieces.
                Enabled = !DisabledByDefault.Contains(definition.Key),
                Algorithm = algorithm.Name,
                Params = DefaultParams(algorithm)
            });
        }

        configuration.Flow = new FlowConfiguration
        {
            Name = PipelineCatalog.Flows[0].Name
        };

        // No engine: under layout_driven nothing consults one, and setting it anyway
        // would leave a value the UI shows and the service ignores.
        configuration.Ocr = new OcrConfiguration
        {
            Engine = null,
            Cropper = Component(PipelineCatalog.Croppers[0]),
            Recognizer = Component(PipelineCatalog.Recognizers[0])
        };

        configuration.TableExtraction = new TableExtractionConfiguration
        {
            Extractor = PipelineCatalog.TableExtractors[0].Name,
            ExtractorParams = DefaultParams(PipelineCatalog.TableExtractors[0]),
            Classifier = PipelineCatalog.TableClassifiers[0].Name,
            ClassifierParams = DefaultParams(PipelineCatalog.TableClassifiers[0])
        };

        configuration.StringMatching = new StringMatchingConfiguration
        {
            Algorithm = PipelineCatalog.StringMatchers[0].Name,
            AlgorithmParams = DefaultParams(PipelineCatalog.StringMatchers[0]),
            DictionaryPath = "keywords/ar_invoice_terms.json"
        };

        // Forced by the service on every request; round-tripped, never edited.
        configuration.Output = new OutputConfiguration { Formatter = "invoice_json" };

        configuration.Persistence = new PersistenceConfiguration
        {
            Store = "file_result_store",
            StoreParams = new Dictionary<string, JsonNode?> { ["output_dir"] = "results/" }
        };

        return configuration;
    }

    /// <summary>
    /// Preprocessing steps the shipped configuration ships switched off. They keep their
    /// parameters so turning one back on is a toggle rather than an edit.
    /// </summary>
    private static readonly HashSet<string> DisabledByDefault = new()
    {
        "contrast_enhancement", "denoising", "thresholding", "morphological_cleanup"
    };

    private static ComponentConfiguration Component(PipelineAlgorithm algorithm) => new()
    {
        Name = algorithm.Name,
        Params = DefaultParams(algorithm)
    };

    /// <summary>Every parameter of <paramref name="algorithm"/> at its documented default.</summary>
    public static Dictionary<string, JsonNode?> DefaultParams(PipelineAlgorithm algorithm)
    {
        var values = new Dictionary<string, JsonNode?>();

        foreach (var parameter in algorithm.Parameters)
        {
            values[parameter.Key] = parameter.Kind == ParameterKind.IntegerPair
                ? new JsonArray(
                    JsonValue.Create(Convert.ToInt32(parameter.DefaultValue ?? 0)),
                    JsonValue.Create(Convert.ToInt32(
                        parameter.SecondDefaultValue ?? parameter.DefaultValue ?? 0)))
                : ToNode(parameter.DefaultValue);
        }

        return values;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };
}
