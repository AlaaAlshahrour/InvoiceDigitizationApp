using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

public sealed class HttpAiServiceClient : IAiServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ISettingsRepository _settings;

    public HttpAiServiceClient(HttpClient http, ISettingsRepository settings)
    {
        _http = http;
        _settings = settings;
    }

    private async Task<Uri> ResolveBaseUriAsync(CancellationToken ct)
    {
        var url = await _settings
            .GetOrDefaultAsync(SettingKeys.PythonServiceUrl, "http://127.0.0.1:8765", ct)
            .ConfigureAwait(false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new AiServiceException(
                AiServiceException.TransportError,
                $"The configured service URL '{url}' is not a valid absolute URL. Fix it in Settings.");
        }

        return uri;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string filePath,
        ExtractionOptions options,
        PipelineConfiguration? config,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new AiServiceException(
                "FILE_NOT_FOUND", $"The file '{filePath}' no longer exists.");
        }

        var baseUri = await ResolveBaseUriAsync(ct).ConfigureAwait(false);
        var endpoint = new Uri(baseUri, "/api/v1/extract");

        using var content = new MultipartFormDataContent();

        // Stream from disk rather than buffering: invoice scans reach tens of megabytes.
        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(GuessContentType(filePath));
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        var optionsJson = JsonSerializer.Serialize(options, JsonOptions);
        content.Add(new StringContent(optionsJson, Encoding.UTF8, "application/json"), "options");

        // Its own part, not a field inside `options`. Omitting it entirely is meaningful:
        // it tells the service to run its own defaults, which is what an installation that
        // has never opened the settings page should do.
        if (config is not null)
        {
            var configJson = JsonSerializer.Serialize(config, JsonOptions);
            content.Add(
                new StringContent(configJson, Encoding.UTF8, "application/json"), "config");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // User cancellation is not a service failure.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AiServiceException(
                AiServiceException.TransportError,
                $"تعذّر الوصول إلى خدمة الاستخراج على {baseUri}. " +
                "تحقق من أنها قيد التشغيل وأن العنوان في الإعدادات صحيح.",
                ex.Message, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw ToException(response.StatusCode, body);

            try
            {
                return JsonSerializer.Deserialize<ExtractionResult>(body, JsonOptions)
                       ?? throw new AiServiceException(
                           "INVALID_RESPONSE", "أعادت خدمة الاستخراج استجابة فارغة.");
            }
            catch (JsonException ex)
            {
                throw new AiServiceException(
                    "INVALID_RESPONSE",
                    "أعادت خدمة الاستخراج استجابة تعذّر على هذا التطبيق تحليلها. " +
                    "قد يكون الطرفان يشغّلان إصدارين غير متطابقين.",
                    ex.Message, ex);
            }
        }
    }

    public async Task<HealthStatus?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUri = await ResolveBaseUriAsync(ct).ConfigureAwait(false);
            using var response = await _http
                .GetAsync(new Uri(baseUri, "/api/v1/health"), ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<HealthStatus>(body, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Health is a probe: "unreachable" is an expected answer, not an error.
            return null;
        }
    }

    public async Task<PipelineConfiguration?> GetDefaultConfigurationAsync(
        CancellationToken ct = default)
    {
        try
        {
            var baseUri = await ResolveBaseUriAsync(ct).ConfigureAwait(false);
            using var response = await _http
                .GetAsync(new Uri(baseUri, "/api/v1/configuration"), ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PipelineConfiguration>(body, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Same reasoning as the health probe: an unreachable service is an expected
            // answer here, and the caller falls back to the contract's own defaults.
            return null;
        }
    }

    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(250);

        while (deadline.Elapsed < timeout)
        {
            var health = await GetHealthAsync(ct).ConfigureAwait(false);
            if (health is { EngineReady: true }) return true;

            await Task.Delay(delay, ct).ConfigureAwait(false);

            // Back off up to 2s: a real OCR engine can take ~30s to load weights, and
            // polling every 250ms for that long is pointless work.
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));
        }

        return false;
    }

    private static AiServiceException ToException(HttpStatusCode status, string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(body, JsonOptions);
            if (envelope?.Error is { } error && !string.IsNullOrWhiteSpace(error.Code))
            {
                return new AiServiceException(
                    error.Code!,
                    error.Message ?? $"The extraction service returned {(int)status}.",
                    error.Detail);
            }
        }
        catch (JsonException)
        {
            // Not our error envelope — fall through to the generic message below.
        }

        return new AiServiceException(
            $"HTTP_{(int)status}",
            $"The extraction service returned {(int)status} ({status}).",
            body.Length > 500 ? body[..500] : body);
    }

    private static string GuessContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
}
