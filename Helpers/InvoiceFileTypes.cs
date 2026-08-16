using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// The set of file formats the app accepts, in one place so the picker, the drop target
/// and validation can never drift apart.
/// </summary>
/// <remarks>
/// Deliberately free of WinUI types: <see cref="FilePickerHelper"/> holds the picker code
/// that needs a Window, this holds the list itself so it stays unit-testable.
/// </remarks>
public static class InvoiceFileTypes
{
    /// <summary>
    /// Raster images only. PDF, BMP and TIFF were removed along with the multi-page and
    /// PDF-rendering code paths — the service reads a single decoded page and nothing else.
    /// </summary>
    public static readonly IReadOnlyList<string> InvoiceExtensions =
        new[] { ".png", ".jpg", ".jpeg" };

    /// <summary>True when the path carries one of the accepted extensions.</summary>
    public static bool IsSupported(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        InvoiceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
