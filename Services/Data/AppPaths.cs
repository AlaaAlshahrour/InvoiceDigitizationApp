using System;
using System.IO;

namespace InvoiceDigitizationApp.Services.Data;

/// <summary>
/// Filesystem locations owned by the app. Everything lives under LocalApplicationData so
/// the app works unpackaged, needs no elevation, and survives app updates.
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InvoiceDigitization");

    public static string DatabasePath => Path.Combine(RootDirectory, "invoices.db");

    /// <summary>
    /// Imported invoice images are copied here so the database never references a file in
    /// the user's Downloads folder that they might later move or delete.
    /// </summary>
    public static string DefaultImageDirectory => Path.Combine(RootDirectory, "images");

    public static string DefaultExportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "InvoiceDigitization", "Exports");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DefaultImageDirectory);
    }
}
