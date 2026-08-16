using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Data;
using InvoiceDigitizationApp.Services.Validation;

namespace InvoiceDigitizationApp.Services.Batch;

/// <summary>
/// Runs an imported batch through the AI service one file at a time and holds the results
/// while the user reviews them.
/// </summary>
/// <remarks>
/// <para>
/// The processing model is a plain sequential <c>foreach</c> with an <c>await</c> per
/// item — no queue, no channel, no background service, no <c>Task.WhenAll</c>. That is a
/// deliberate choice, not an unfinished one: the OCR service handles one request at a
/// time anyway, so parallelism would buy nothing while making progress reporting,
/// cancellation and failure isolation all harder to reason about.
/// </para>
/// <para>
/// Memory is the other reason. Each extraction comes back carrying two base64 PNGs; a
/// hundred of those held at once would be hundreds of megabytes. Instead each item's
/// images are written to disk and the base64 fields nulled out before the loop advances,
/// so peak memory is one item's worth regardless of batch size.
/// </para>
/// </remarks>
public sealed class InvoiceBatchService : IInvoiceBatchService
{
    /// <summary>
    /// A factory rather than the client itself. <see cref="IAiServiceClient"/> is
    /// registered through <c>AddHttpClient</c>, which makes it transient over a handler
    /// that is rotated periodically; capturing one instance inside a singleton would pin
    /// a handler forever and miss DNS changes. Resolving per file is the supported way.
    /// </summary>
    private readonly Func<IAiServiceClient> _clientFactory;

    private readonly IDuplicateDetectionService _duplicates;
    private readonly ISettingsRepository _settings;

    private readonly List<BatchItem> _items = new();

    private CancellationTokenSource? _cancellation;

    public InvoiceBatchService(
        Func<IAiServiceClient> clientFactory,
        IDuplicateDetectionService duplicates,
        ISettingsRepository settings)
    {
        _clientFactory = clientFactory;
        _duplicates = duplicates;
        _settings = settings;
    }

    public IReadOnlyList<BatchItem> Items => _items;

    public bool IsActive => _items.Count > 0;

    public bool IsProcessing { get; private set; }

    public int CurrentIndex { get; private set; }

    public BatchItem? Current =>
        CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

    public int TotalCount => _items.Count;

    public int ReviewedCount => _items.Count(i => i.IsFinished);

    public string PositionLabel =>
        _items.Count == 0 ? string.Empty : $"{CurrentIndex + 1} من {_items.Count}";

    public bool CanMoveNext => NextReviewableIndex(CurrentIndex, forward: true) is not null;

    public bool CanMovePrevious => NextReviewableIndex(CurrentIndex, forward: false) is not null;

    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- processing -------------------------------------------------------

    public async Task StartAsync(
        IReadOnlyList<string> paths,
        ExtractionOptions options,
        IProgress<BatchProgress>? progress,
        CancellationToken ct = default)
    {
        if (IsProcessing) return;

        // Anything left from a previous batch is discarded first, so its files do not
        // linger and its indices cannot be confused with the new run's.
        await ClearAsync().ConfigureAwait(false);

        // One timestamp for the whole batch keeps an import's files adjacent when sorted
        // by name; the per-item index is what makes each stem unique.
        var batchStamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");

        for (var i = 0; i < paths.Count; i++)
        {
            _items.Add(new BatchItem
            {
                Index = i,
                SourcePath = paths[i],
                Stem = $"{batchStamp}_{i:D3}"
            });
        }

        CurrentIndex = 0;
        IsProcessing = true;

        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cancellation.Token;

        var directory = await _settings
            .GetOrDefaultAsync(SettingKeys.ImageStorageDirectory, AppPaths.DefaultImageDirectory)
            .ConfigureAwait(false);

        Directory.CreateDirectory(directory);

        RaiseChanged();

        var completed = 0;
        var failed = 0;

        try
        {
            // Strictly sequential: one file is fully finished — extracted, decoded,
            // written to disk, stripped of its base64 payload — before the next begins.
            foreach (var item in _items)
            {
                // Checked between items, so cancelling stops the batch as soon as the
                // file in flight lands rather than after the whole list drains.
                if (token.IsCancellationRequested) break;

                item.State = BatchItemState.Processing;
                RaiseChanged();

                try
                {
                    await ProcessOneAsync(item, options, directory, token).ConfigureAwait(false);
                    item.State = BatchItemState.Ready;
                }
                catch (OperationCanceledException)
                {
                    // The user's own cancellation: leave the item Pending rather than
                    // marking it failed, and stop the loop.
                    item.State = BatchItemState.Pending;
                    break;
                }
                catch (AiServiceException ex)
                {
                    // One bad file must not take the batch down with it. The item is
                    // marked and the loop moves on; the user can still key it in by hand.
                    item.State = BatchItemState.Failed;
                    item.ErrorMessage = ex.Code == AiServiceException.TransportError
                        ? $"{ex.Message} لا يزال بإمكانك إدخال هذه الفاتورة يدويًا."
                        : $"فشل الاستخراج ({ex.Code}): {ex.Message}";
                }
                catch (Exception ex)
                {
                    item.State = BatchItemState.Failed;
                    item.ErrorMessage = ex.Message;
                }

                completed++;
                if (item.State == BatchItemState.Failed) failed++;

                // Reported per item, the instant it finishes — the counter has to move
                // while the work is happening, not jump to full at the end.
                progress?.Report(new BatchProgress(
                    completed, _items.Count, failed, Path.GetFileName(item.SourcePath)));

                RaiseChanged();
            }
        }
        finally
        {
            IsProcessing = false;

            // Land on the first item actually worth reviewing.
            CurrentIndex = FirstReviewableIndex() ?? 0;

            RaiseChanged();
        }
    }

    /// <summary>
    /// Extracts one file and persists its two diagnostic renderings, then drops the
    /// base64 payloads. This last step is what bounds the batch's memory use.
    /// </summary>
    private async Task ProcessOneAsync(
        BatchItem item, ExtractionOptions options, string directory, CancellationToken ct)
    {
        item.ContentHash = await _duplicates
            .ComputeFileHashAsync(item.SourcePath, ct).ConfigureAwait(false);

        var client = _clientFactory();
        var result = await client.ExtractAsync(item.SourcePath, options, ct).ConfigureAwait(false);

        item.EnhancedImagePath = await WriteImageAsync(
            result.EnhancedImagePng, directory, $"{item.Stem}_enh.png").ConfigureAwait(false);

        item.OcrImagePath = await WriteImageAsync(
            result.OcrInputImagePng, directory, $"{item.Stem}_ocr.png").ConfigureAwait(false);

        // Now on disk and reachable by path. Holding the strings as well would mean a
        // 100-file batch kept 100 full-page PNGs alive in managed memory at once.
        result.EnhancedImagePng = null;
        result.OcrInputImagePng = null;

        item.Result = result;
    }

    /// <summary>
    /// Decodes a base64 PNG to <paramref name="fileName"/>. Returns null when there is
    /// nothing to write or the write fails — a diagnostic rendering is never worth
    /// failing an otherwise-good extraction over.
    /// </summary>
    private static async Task<string?> WriteImageAsync(
        string? base64, string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var path = Path.Combine(directory, fileName);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return path;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void RequestCancel() => _cancellation?.Cancel();

    // ---- navigation -------------------------------------------------------

    /// <summary>
    /// The next index in <paramref name="forward"/> direction that still needs review.
    /// Saved and skipped items are stepped over; failed ones are not, because the user
    /// may still want to key them in by hand.
    /// </summary>
    private int? NextReviewableIndex(int from, bool forward)
    {
        var step = forward ? 1 : -1;

        for (var i = from + step; i >= 0 && i < _items.Count; i += step)
        {
            if (!_items[i].IsFinished) return i;
        }

        return null;
    }

    private int? FirstReviewableIndex()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (!_items[i].IsFinished) return i;
        }

        return null;
    }

    public bool MoveNext()
    {
        if (NextReviewableIndex(CurrentIndex, forward: true) is not { } index) return false;

        CurrentIndex = index;
        RaiseChanged();
        return true;
    }

    public bool MovePrevious()
    {
        if (NextReviewableIndex(CurrentIndex, forward: false) is not { } index) return false;

        CurrentIndex = index;
        RaiseChanged();
        return true;
    }

    public void MarkSaved(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        _items[index].State = BatchItemState.Saved;
        RaiseChanged();
    }

    public async Task SkipAsync(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];
        await DeleteGeneratedFilesAsync(item).ConfigureAwait(false);

        item.State = BatchItemState.Skipped;

        // Free the extraction too: a skipped item is never shown again.
        item.Result = null;

        RaiseChanged();
    }

    public async Task ClearAsync()
    {
        // Only unsaved items are cleaned up. A saved invoice's images are referenced by
        // a database row and deleting them would leave the record pointing at nothing.
        foreach (var item in _items.Where(i => i.State != BatchItemState.Saved))
            await DeleteGeneratedFilesAsync(item).ConfigureAwait(false);

        _items.Clear();
        CurrentIndex = 0;

        _cancellation?.Dispose();
        _cancellation = null;

        RaiseChanged();
    }

    /// <summary>
    /// Removes the enhanced/OCR renderings written for an item. The user's own source
    /// file is never touched — only the copies this service created.
    /// </summary>
    private static Task DeleteGeneratedFilesAsync(BatchItem item)
    {
        TryDelete(item.EnhancedImagePath);
        TryDelete(item.OcrImagePath);

        item.EnhancedImagePath = null;
        item.OcrImagePath = null;

        return Task.CompletedTask;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file still held open by the image viewer must not turn the user's
            // "skip this one" into an error dialog. The stale file is the lesser evil.
        }
    }
}
