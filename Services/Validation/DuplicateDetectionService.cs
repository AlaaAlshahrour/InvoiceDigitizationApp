using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Helpers;
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
    /// Merchant names below this similarity are treated as different businesses. Set high
    /// because a false duplicate warning on every save trains the user to dismiss them.
    /// </summary>
    private const double MerchantSimilarityThreshold = 0.85;

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

            // The SQL already matched merchant exactly; re-check fuzzily so a trailing
            // space or an alef variant doesn't silently drop a real duplicate.
            var similarity = FuzzyMatch.Similarity(candidate.MerchantName, existing.MerchantName);
            if (similarity < MerchantSimilarityThreshold) continue;

            var reason =
                $"السجل رقم {existing.InvoiceId} يحمل الاسم نفسه " +
                $"({existing.MerchantName}) وإجماليًا قدره " +
                $"{existing.TotalAmount.ToString("0.##", CultureInfo.InvariantCulture)}" +
                (existing.InvoiceDate is { } d ? $"، بتاريخ {d:yyyy-MM-dd}" : string.Empty) + ".";

            matches.Add(new DuplicateMatch(
                existing, DuplicateKind.LikelySameInvoice, similarity, reason));
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
