using System;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// The two similarity algorithms, mirroring the service's
/// <c>string_matching/algorithms.py</c> so both sides agree on what counts as a match.
/// </summary>
/// <remarks>
/// Two algorithms, because names fail in two different ways:
/// <list type="bullet">
/// <item><see cref="Similarity"/> — character edits. Right for merchant, city and
/// governorate names, which are read as one run of characters and go wrong one letter
/// at a time.</item>
/// <item><see cref="ProductSimilarity"/> — word-set overlap scored with the same edit
/// distance underneath. Right for product names, where the words are correct but their
/// order is not: "جاكيت صوف أزرق" and "جاكيت أزرق صوف" are the same product and score
/// ~1.0 here while scoring poorly on raw edit distance.</item>
/// </list>
/// Both normalize their inputs through <see cref="TextNormalizer"/> first, so callers
/// pass raw OCR text and raw catalog text without folding either themselves.
/// </remarks>
public static class FuzzyMatch
{
    /// <summary>
    /// Edit-distance similarity in [0,1]: 1.0 is identical after normalization, 0.0 is
    /// nothing in common. An empty string on either side scores 0.0 — "nothing matched"
    /// rather than "matched perfectly".
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var left = TextNormalizer.Normalize(a);
        var right = TextNormalizer.Normalize(b);

        return ScoreNormalized(left, right);
    }

    /// <summary>
    /// Order-independent similarity in [0,1], for product names. Each word of
    /// <paramref name="ocrProduct"/> is scored against its best match among the catalog
    /// words, and the total is divided by the larger word count.
    /// </summary>
    /// <remarks>
    /// Dividing by the larger count is what stops a two-word OCR read from scoring 1.0
    /// against a five-word catalog entry just because both its words appear in it: extra
    /// words on either side dilute the score, as they should.
    /// </remarks>
    public static double ProductSimilarity(string? ocrProduct, string? catalogProduct)
    {
        var ocrWords = TextNormalizer.NormalizeWords(ocrProduct);
        var catalogWords = TextNormalizer.NormalizeWords(catalogProduct);

        if (ocrWords.Length == 0 || catalogWords.Length == 0) return 0.0;

        var total = 0.0;

        foreach (var ocrWord in ocrWords)
        {
            var best = 0.0;
            foreach (var catalogWord in catalogWords)
            {
                var score = ScoreNormalized(ocrWord, catalogWord);
                if (score > best)
                {
                    best = score;
                    if (best >= 1.0) break;
                }
            }

            total += best;
        }

        return total / Math.Max(ocrWords.Length, catalogWords.Length);
    }

    /// <summary>
    /// Similarity between two strings that are already normalized. Used internally for
    /// word-level scoring, where re-folding each word would repeat work the caller did.
    /// </summary>
    public static string Normalize(string? value) => TextNormalizer.Normalize(value);

    private static double ScoreNormalized(string left, string right)
    {
        // Both empty means the two strings really are identical — two invoices with no
        // merchant read are "equally unknown", not "different", which is what duplicate
        // detection depends on. One side empty is the genuine no-match case.
        if (left.Length == 0 && right.Length == 0) return 1.0;
        if (left.Length == 0 || right.Length == 0) return 0.0;
        if (left == right) return 1.0;

        var distance = Levenshtein(left, right);
        var longest = Math.Max(left.Length, right.Length);
        return (double)(longest - distance) / longest;
    }

    /// <summary>
    /// Two-row Levenshtein: O(n·m) time, O(min(n,m)) space. Invoice strings are short, so
    /// the simple implementation is the right one.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length < b.Length) (a, b) = (b, a);
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
