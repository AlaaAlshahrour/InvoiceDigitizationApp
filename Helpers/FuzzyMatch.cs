using System;
using System.Globalization;
using System.Text;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// Normalized Levenshtein similarity, used to match OCR merchant/product text against the
/// Customers and Products catalogs. Mirrors the Python service's matcher so both sides
/// agree on what counts as a match.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>
    /// Similarity in [0,1]: 1.0 is identical after normalization, 0.0 is nothing in common.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);

        if (left.Length == 0 && right.Length == 0) return 1.0;
        if (left.Length == 0 || right.Length == 0) return 0.0;
        if (left == right) return 1.0;

        var distance = Levenshtein(left, right);
        var longest = Math.Max(left.Length, right.Length);
        return 1.0 - (double)distance / longest;
    }

    /// <summary>
    /// Casefolds, collapses whitespace, strips Arabic diacritics and tatweel, unifies
    /// alef/yeh/teh-marbuta variants, and folds Arabic-Indic digits to ASCII. Merchants
    /// spell their own names inconsistently across invoices; this removes the differences
    /// that carry no meaning.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var raw in value.Trim())
        {
            var ch = raw;

            // Arabic-Indic (٠-٩) and extended Arabic-Indic (۰-۹) digits to ASCII.
            if (ch is >= '٠' and <= '٩') ch = (char)('0' + (ch - '٠'));
            else if (ch is >= '۰' and <= '۹') ch = (char)('0' + (ch - '۰'));

            // Harakat U+064B..U+0652 and tatweel U+0640 are decorative — drop them.
            // Written as escapes because combining marks are invisible in an editor and
            // are easily mangled by a copy-paste or an encoding change.
            if (ch is >= 'ً' and <= 'ْ' or 'ـ') continue;

            ch = ch switch
            {
                'آ' or 'أ' or 'إ' => 'ا', // آ أ إ → ا
                'ى' => 'ي',                        // ى     → ي
                'ة' => 'ه',                        // ة     → ه
                _ => ch
            };

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Two-row Levenshtein: O(n·m) time, O(min(n,m)) space. Invoice strings are short, so
    /// the simple implementation is the right one.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length < b.Length) (a, b) = (b, a);

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
