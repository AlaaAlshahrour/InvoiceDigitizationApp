namespace InvoiceDigitizationApp.Services.Data;

/// <summary>
/// Keys used in the AppSettings table. Constants rather than raw strings so a typo is a
/// compile error instead of a silently-missing setting.
/// </summary>
public static class SettingKeys
{
    public const string PythonServiceUrl = "PythonServiceUrl";
    public const string PythonServiceExecutable = "PythonServiceExecutable";
    public const string AutoStartPythonService = "AutoStartPythonService";
    public const string ExportDirectory = "ExportDirectory";
    public const string ImageStorageDirectory = "ImageStorageDirectory";
    public const string AppTheme = "AppTheme";
    public const string ConfidenceThreshold = "ConfidenceThreshold";

    /// <summary>
    /// The extraction pipeline configuration, stored as one JSON blob matching
    /// docs/settings-config-contract.md. Empty means "let the service use its defaults".
    /// </summary>
    public const string PipelineConfiguration = "PipelineConfiguration";

    /// <summary>How many ranked candidates each matched field offers in its picker.</summary>
    public const string MaxMatchCandidates = "MaxMatchCandidates";
}
