// System is required for the WindowsRuntimeSystemExtensions awaiter that lets an
// IAsyncOperation be awaited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// The single place that opens a file picker for invoice images. Both entry points — the
/// verification screen's own "open" button and the dashboard's import — route through
/// here, so the accepted extension list and the unpackaged-app window-handle dance exist
/// once rather than twice.
/// </summary>
public static class FilePickerHelper
{
    /// <summary>
    /// The accepted extensions. Re-exposed from <see cref="InvoiceFileTypes"/>, which is
    /// where the list actually lives so it can be tested without the Windows App SDK.
    /// </summary>
    public static IReadOnlyList<string> InvoiceExtensions => InvoiceFileTypes.InvoiceExtensions;

    /// <summary>
    /// Prompts for one or more invoice images. Returns an empty list when the user
    /// cancels, so callers can treat cancellation as "nothing to do" without a null check.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickInvoiceFilesAsync(Window window)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        foreach (var extension in InvoiceExtensions)
            picker.FileTypeFilter.Add(extension);

        // An unpackaged app has no implicit window to parent the dialog to; without this
        // association the picker silently never appears.
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return [];

        return files.Select(f => f.Path).ToList();
    }
}
