using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.Services.AiServiceClient;

public interface IPythonServiceLauncher
{
    /// <summary>
    /// Starts the extraction service if <c>AutoStartPythonService</c> is on and it is not
    /// already answering, then waits for it to report ready. Returns false when it was not
    /// started, could not be started, or did not come up in time — never throws, because
    /// the app is fully usable for manual entry either way.
    /// </summary>
    Task<bool> EnsureRunningAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the process this launcher started. A service the user was already running is
    /// left alone: it was not ours to start and it is not ours to stop.
    /// </summary>
    void Stop();
}

/// <summary>
/// Starts the Python extraction service alongside the app.
/// </summary>
/// <remarks>
/// The two settings this reads — <c>AutoStartPythonService</c> and
/// <c>PythonServiceExecutable</c> — have existed since the first schema and were shown on
/// the settings page, but nothing ever launched a process: turning the checkbox on did
/// nothing at all, which is worse than not offering it.
///
/// <c>PythonServiceExecutable</c> is the path to the pipeline's own interpreter
/// (<c>&lt;pipeline&gt;\.venv\Scripts\python.exe</c>). The module and the working directory
/// are derived from it rather than configured separately: the service must run as
/// <c>-m api.main</c> from the pipeline root or its relative paths — the keyword dictionary,
/// the model weights, the results directory — resolve against wherever the app happened to
/// be launched from and silently disappear.
/// </remarks>
public sealed class PythonServiceLauncher : IPythonServiceLauncher, IDisposable
{
    /// <summary>
    /// The module the service runs as. Not configurable: the settings page asks for an
    /// interpreter, and a second free-text field for arguments would be one more thing to
    /// get subtly wrong for no gain.
    /// </summary>
    private const string ServiceModule = "api.main";

    /// <summary>
    /// How long to wait for readiness. The layout-driven pipeline loads about 1.7 GB of
    /// model weights on the first request, which takes the better part of a minute on a
    /// laptop with no GPU; failing at 30 seconds would report a working install as broken.
    /// </summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private readonly ISettingsRepository _settings;
    private readonly IAiServiceClient _client;

    private Process? _process;

    public PythonServiceLauncher(ISettingsRepository settings, IAiServiceClient client)
    {
        _settings = settings;
        _client = client;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        var autoStart = await _settings
            .GetBoolAsync(SettingKeys.AutoStartPythonService, false, ct).ConfigureAwait(false);

        if (!autoStart) return false;

        // Already answering — the user started it themselves, or a previous run of this
        // app left it up. Starting a second one would bind the same port and fail.
        if (await _client.GetHealthAsync(ct).ConfigureAwait(false) is not null)
            return await WaitAsync(ct).ConfigureAwait(false);

        var executable = (await _settings
            .GetAsync(SettingKeys.PythonServiceExecutable, ct).ConfigureAwait(false))?.Trim();

        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return false;

        // <pipeline>\.venv\Scripts\python.exe → <pipeline>. Three levels up, because the
        // module's imports and every relative path in its config resolve from there.
        var pipelineRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(executable)!, "..", ".."));

        if (!Directory.Exists(pipelineRoot)) return false;

        try
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = pipelineRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            }.WithArguments("-m", ServiceModule));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A bad path or a file that is not executable. The app still runs; the user
            // gets "service unreachable" from the health probe, which is the same message
            // they would get for a service they forgot to start by hand.
            _process = null;
            return false;
        }

        return await WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<bool> WaitAsync(CancellationToken ct) =>
        _client.WaitUntilReadyAsync(ReadyTimeout, ct);

    public void Stop()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                // The whole tree: the qwen recognizer runs its model in a child process of
                // its own, and killing only the parent would leave that holding the weights.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill. Either way there is nothing left to do.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();
}

internal static class ProcessStartInfoExtensions
{
    /// <summary>
    /// Adds arguments through <see cref="ProcessStartInfo.ArgumentList"/>, which quotes
    /// each one itself. Building a single command line by hand is what breaks the moment
    /// the interpreter sits under a path with a space in it — which on Windows it usually
    /// does.
    /// </summary>
    internal static ProcessStartInfo WithArguments(
        this ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
