using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

public interface IAiServiceClient
{
    /// <summary>
    /// Sends an image (JPG/JPEG/PNG) for extraction. Throws <see cref="AiServiceException"/> on
    /// any service-reported failure or transport error.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        string filePath, ExtractionOptions options, CancellationToken ct = default);

    /// <summary>Returns null when the service is unreachable, rather than throwing.</summary>
    Task<HealthStatus?> GetHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// The service's own default pipeline configuration, so the settings page opens on
    /// the values it actually runs rather than a second copy hardcoded here.
    /// Returns null when the service is unreachable — the page falls back to the
    /// contract's documented defaults.
    /// </summary>
    Task<PipelineConfiguration?> GetDefaultConfigurationAsync(CancellationToken ct = default);

    /// <summary>
    /// Polls health until the engine reports ready or the timeout elapses.
    /// Returns false on timeout.
    /// </summary>
    Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Carries the service's error <c>code</c> so ViewModels can branch on the code rather
/// than parse human-readable messages.
/// </summary>
public sealed class AiServiceException : Exception
{
    /// <summary>A code from docs/api-contract.md, or "TRANSPORT_ERROR" if the call never landed.</summary>
    public string Code { get; }

    public string? Detail { get; }

    public AiServiceException(string code, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Detail = detail;
    }

    public const string TransportError = "TRANSPORT_ERROR";
    public const string EngineNotReady = "ENGINE_NOT_READY";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";

    /// <summary>True when retrying the same request might succeed.</summary>
    public bool IsRetryable => Code is TransportError or EngineNotReady;
}
