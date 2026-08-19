using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.Services.Validation;

public sealed class DuplicateDetectionService : IDuplicateDetectionService
{
    /// <summary>
    /// The same purchase can be dated a day apart between the printed date and what the
    /// user reads off a smudged receipt, so allow ±1 day.
    /// </summary>
    private const int DayTolerance = 1;

    private const decimal AmountTolerance = 0.01m;

    /// <summary>
    /// Confidence reported for a match found by the SQL query below. Fixed, because the
    /// query matches the merchant name exactly — there is no similarity to report, and a
    /// varying number would suggest a fuzziness that is not there.
    /// </summary>
    private const double SameInvoiceConfidence = 0.9;

    private readonly IInvoiceRepository _invoices;

    public DuplicateDetectionService(IInvoiceRepository invoices) => _invoices = invoices;

    public async Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(
        Invoice candidate, CancellationToken ct = default)
    {
        var matches = new List<DuplicateMatch>();
        var seen = new HashSet<int>();

        // Signal 1: the exact same file was imported before. Indexed lookup, no ambiguity.
        if (!string.IsNullOrWhiteSpace(candidate.ContentHash))
        {
            var exact = await _invoices
                .FindByContentHashAsync(candidate.ContentHash, ct).ConfigureAwait(false);

            foreach (var existing in exact)
            {
                if (existing.InvoiceId == candidate.InvoiceId) continue;
                if (!seen.Add(existing.InvoiceId)) continue;

                matches.Add(new DuplicateMatch(
                    existing, DuplicateKind.ExactFile, 1.0,
                    $"سبق استيراد ملف الصورة نفسه في السجل رقم {existing.InvoiceId}."));
            }
        }

        // Signal 2: same invoice photographed twice — different bytes, same content.
        var similar = await _invoices
            .FindSimilarAsync(candidate, DayTolerance, AmountTolerance, ct).ConfigureAwait(false);

        foreach (var existing in similar)
        {
            if (existing.InvoiceId == candidate.InvoiceId) continue;
            if (!seen.Add(existing.InvoiceId)) continue;

            // No second, fuzzy pass. FindSimilarAsync already matched the merchant name
            // exactly, so re-scoring it here could only ever reject a row the query had
            // accepted — and it did that with the app's own copy of the similarity rules,
            // which no longer exists. Duplicate detection now rests on what the query
            // matched: same name, same total within a cent, within a day.

            var reason =
                $"السجل رقم {existing.InvoiceId} يحمل الاسم نفسه " +
                $"({existing.MerchantName}) وإجماليًا قدره " +
                $"{existing.TotalAmount.ToString("0.##", CultureInfo.InvariantCulture)}" +
                (existing.InvoiceDate is { } d ? $"، بتاريخ {d:yyyy-MM-dd}" : string.Empty) + ".";

            matches.Add(new DuplicateMatch(
                existing, DuplicateKind.LikelySameInvoice, SameInvoiceConfidence, reason));
        }

        return matches
            .OrderByDescending(m => m.Kind == DuplicateKind.ExactFile)
            .ThenByDescending(m => m.Confidence)
            .ToList();
    }

    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
