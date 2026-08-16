using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.Services.Batch;

public enum BatchItemState
{
    /// <summary>Queued but not yet reached by the sequential loop.</summary>
    Pending,

    /// <summary>The loop is on this item right now. Exactly one item is ever in this state.</summary>
    Processing,

    /// <summary>Extraction succeeded; waiting for the user to review and save it.</summary>
    Ready,

    /// <summary>Extraction threw. <see cref="BatchItem.ErrorMessage"/> says why.</summary>
    Failed,

    /// <summary>The user saved it. Terminal — navigation skips over it.</summary>
    Saved,

    /// <summary>The user discarded it and its files were deleted. Terminal.</summary>
    Skipped
}

/// <summary>
/// One imported file as it travels through the batch. The state and the results are set
/// by the service alone (<c>internal set</c>); the ViewModel reads them.
/// </summary>
public sealed class BatchItem
{
    /// <summary>Position in the batch, zero-based. Also the suffix in the file stem.</summary>
    public int Index { get; init; }

    /// <summary>Where the user picked the file from — still their own copy, not imported yet.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// The shared filename stem for this item's three files, e.g.
    /// <c>20260808_142233417_000</c>. Assigned up front so the enhanced/OCR renderings
    /// written during processing and the original copied at save time all agree.
    /// </summary>
    public string Stem { get; init; } = string.Empty;

    public BatchItemState State { get; internal set; } = BatchItemState.Pending;

    /// <summary>
    /// The extraction, with the two base64 image fields already nulled out — they were
    /// written to disk and are not carried in memory. Null until processed.
    /// </summary>
    public ExtractionResult? Result { get; internal set; }

    public string? ContentHash { get; internal set; }
    public string? EnhancedImagePath { get; internal set; }
    public string? OcrImagePath { get; internal set; }

    /// <summary>Set only in <see cref="BatchItemState.Failed"/>.</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>Terminal states, which navigation skips over.</summary>
    public bool IsFinished => State is BatchItemState.Saved or BatchItemState.Skipped;
}

/// <summary>Progress after one item finishes, success or failure.</summary>
public sealed record BatchProgress(
    int CompletedCount,
    int TotalCount,
    int FailedCount,
    string CurrentFileName);

/// <summary>
/// Holds a multi-file import while the user reviews it one invoice at a time.
/// </summary>
/// <remarks>
/// Registered as a singleton, unlike the per-navigation ViewModels: the navigation Frame
/// does not cache pages, so a ViewModel-owned batch would be destroyed the moment the
/// user stepped over to the products list mid-review.
/// </remarks>
public interface IInvoiceBatchService
{
    IReadOnlyList<BatchItem> Items { get; }

    /// <summary>True once a batch has been started and not yet cleared.</summary>
    bool IsActive { get; }

    /// <summary>True while <see cref="StartAsync"/> is still walking the list.</summary>
    bool IsProcessing { get; }

    int CurrentIndex { get; }
    BatchItem? Current { get; }

    int TotalCount { get; }

    /// <summary>Items the user has finished with — saved or skipped.</summary>
    int ReviewedCount { get; }

    /// <summary>Human-readable position, e.g. "3 من 5".</summary>
    string PositionLabel { get; }

    bool CanMoveNext { get; }
    bool CanMovePrevious { get; }

    /// <summary>
    /// Raised whenever anything observable changes: an item completes, the cursor moves,
    /// an item is saved or skipped. The ViewModel re-reads state rather than being handed
    /// a diff.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Processes <paramref name="paths"/> strictly one at a time, in order. Returns when
    /// the last item finishes or cancellation takes effect.
    /// </summary>
    Task StartAsync(
        IReadOnlyList<string> paths,
        ExtractionOptions options,
        IProgress<BatchProgress>? progress,
        CancellationToken ct = default);

    /// <summary>Asks the running loop to stop after the item currently in flight.</summary>
    void RequestCancel();

    /// <summary>Moves to the next reviewable item. Returns false when there is none.</summary>
    bool MoveNext();

    bool MovePrevious();

    /// <summary>Marks an item saved so navigation stops offering it.</summary>
    void MarkSaved(int index);

    /// <summary>Discards an item and deletes the files written for it.</summary>
    Task SkipAsync(int index);

    /// <summary>Ends the batch, deleting the files of everything not saved.</summary>
    Task ClearAsync();
}
