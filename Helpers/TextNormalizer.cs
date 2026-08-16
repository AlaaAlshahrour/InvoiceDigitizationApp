using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// Reduces text to the canonical form every similarity comparison runs on.
/// Mirrors the service's <c>string_matching/normalization.py</c>: if one side changes,
/// the other has to change with it or the two will disagree on what "the same string"
/// means, and a merchant the service matched would come back unmatched locally.
/// </summary>
/// <remarks>
/// The result is for comparison only and is never stored as the user-visible value.
/// </remarks>
public static class TextNormalizer
{
    /// <summary>
    /// Casefolds, collapses whitespace, strips Arabic diacritics and tatweel, unifies
    /// alef/yeh/teh-marbuta variants, folds Arabic-Indic digits to ASCII, and removes
    /// punctuation. Merchants spell their own names inconsistently across invoices;
    /// this removes the differences that carry no meaning.
    /// </summary>
    /// <remarks>
    /// Digits are deliberately kept: they are part of real product names — "سكر 1كغ",
    /// "A4 Paper" — and dropping them would merge products that differ only by size.
    /// </remarks>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // NFKC first: collapses presentation forms (ﻻ) into their canonical letters,
        // so a scanned ligature compares equal to the typed letters.
        var source = value.Normalize(NormalizationForm.FormKC);

        var builder = new StringBuilder(source.Length);
        var lastWasSpace = false;

        foreach (var raw in source.Trim())
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
                'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', // آ أ إ ٱ → ا
                'ى' => 'ي',                                // ى       → ي
                'ة' => 'ه',                                // ة       → ه
                _ => ch
            };

            if (char.IsWhiteSpace(ch) || IsStrippedPunctuation(ch))
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

    /// <summary><see cref="Normalize"/> split into words, for order-independent matching.</summary>
    public static string[] NormalizeWords(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0
            ? Array.Empty<string>()
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Reads a quantity cell as a whole number, keeping only the digits.
    /// </summary>
    /// <remarks>
    /// The rule the AI team specified, and the one the service applies: quantities on
    /// these invoices are integers, so a comma or a dot inside one is a mis-read stroke
    /// rather than a decimal point — and the digit it was printed beside is nearly
    /// always a zero. "٢.٠" therefore reads as 20, not 2. Returns null when the text
    /// carries no digit at all.
    /// </remarks>
    public static int? NormalizeQuantity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new StringBuilder(value.Length);

        foreach (var raw in value.Normalize(NormalizationForm.FormKC))
        {
            var ch = raw;
            if (ch is >= '٠' and <= '٩') ch = (char)('0' + (ch - '٠'));
            else if (ch is >= '۰' and <= '۹') ch = (char)('0' + (ch - '۰'));

            if (ch is >= '0' and <= '9') digits.Append(ch);
        }

        if (digits.Length == 0) return null;

        // A quantity long enough to overflow is a mis-read, not a number worth keeping.
        return int.TryParse(digits.ToString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var quantity)
            ? quantity
            : null;
    }

    /// <summary>Punctuation removed before comparison, Arabic and Latin alike.</summary>
    private static bool IsStrippedPunctuation(char ch) =>
        ch is '.' or ',' or ';' or ':' or '!' or '?' or '؟' or '،' or '؛'
            or '(' or ')' or '{' or '}' or '[' or ']' or '<' or '>'
            or '"' or '\'' or '`' or '«' or '»' or '/' or '\\' or '|'
            or '_' or '-' or '+' or '*' or '=' or '&' or '^' or '%'
            or '#' or '@' or '~' or '$';

    /// <summary>
    /// Every distinct, non-blank name in <paramref name="values"/>, trimmed, keeping the
    /// first spelling of each. Case-insensitive after normalization, so "متجر النور" and
    /// "متجر النـور" count as one name rather than two dropdown entries.
    /// </summary>
    public static IReadOnlyList<string> UniqueNames(params string?[] values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>(values.Length);

        foreach (var value in values)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            if (seen.Add(Normalize(text))) names.Add(text);
        }

        return names;
    }
}
