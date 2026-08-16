using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Validation;

public enum DuplicateKind
{
    /// <summary>Byte-identical source file already imported (matching ContentHash).</summary>
    ExactFile,

    /// <summary>Same merchant, near-identical date and total — likely the same paper invoice.</summary>
    LikelySameInvoice
}

public sealed record DuplicateMatch(Invoice Existing, DuplicateKind Kind, double Confidence, string Reason);

public interface IDuplicateDetectionService
{
    /// <summary>
    /// Finds invoices that appear to duplicate the candidate. Returns an empty list when
    /// there is nothing suspicious. Never blocks a save — the caller decides what to do.
    /// </summary>
    Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(
        Invoice candidate, CancellationToken ct = default);

    /// <summary>SHA-256 of a file's bytes, for the ContentHash column.</summary>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default);
}
