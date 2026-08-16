using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Export;

public sealed class ExportService : IExportService
{
    /// <summary>
    /// UTF-8 *with* BOM. Excel on Windows assumes the ANSI codepage for a BOM-less CSV,
    /// which renders Arabic merchant names as mojibake. The BOM is what makes the file
    /// open correctly on a double-click.
    /// </summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public string GetExtension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Excel => ".xlsx",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public async Task<string> ExportInvoicesAsync(
        IReadOnlyList<Invoice> invoices, string destinationPath, ExportFormat format,
        CancellationToken ct = default)
    {
        EnsureDirectory(destinationPath);

        if (format == ExportFormat.Csv)
            await WriteInvoicesCsvAsync(invoices, destinationPath, ct).ConfigureAwait(false);
        else
            await Task.Run(() => WriteInvoicesXlsx(invoices, destinationPath), ct).ConfigureAwait(false);

        return destinationPath;
    }

    public async Task<string> ExportSingleInvoiceAsync(
        Invoice invoice, string destinationPath, ExportFormat format, CancellationToken ct = default)
    {
        EnsureDirectory(destinationPath);

        if (format == ExportFormat.Csv)
            await WriteSingleInvoiceCsvAsync(invoice, destinationPath, ct).ConfigureAwait(false);
        else
            await Task.Run(() => WriteSingleInvoiceXlsx(invoice, destinationPath), ct).ConfigureAwait(false);

        return destinationPath;
    }

    // ---- CSV ---------------------------------------------------------------

    private static async Task WriteInvoicesCsvAsync(
        IReadOnlyList<Invoice> invoices, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, append: false, CsvEncoding);

        await writer.WriteLineAsync(Row(
            "InvoiceId", "InvoiceNumber", "MerchantName", "City", "InvoiceDate",
            "InvoiceType", "TotalAmount", "ItemCount", "CreatedAt")).ConfigureAwait(false);

        foreach (var invoice in invoices)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(Row(
                invoice.InvoiceId.ToString(CultureInfo.InvariantCulture),
                invoice.InvoiceNumber,
                invoice.MerchantName,
                invoice.City,
                invoice.InvoiceDate?.ToString("yyyy-MM-dd"),
                invoice.InvoiceType.ToString(),
                Num(invoice.TotalAmount),
                invoice.Items.Count.ToString(CultureInfo.InvariantCulture),
                invoice.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteSingleInvoiceCsvAsync(Invoice invoice, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, append: false, CsvEncoding);

        await writer.WriteLineAsync(Row("Field", "Value")).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("Invoice Number", invoice.InvoiceNumber)).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("Merchant", invoice.MerchantName)).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("City", invoice.City)).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("Date", invoice.InvoiceDate?.ToString("yyyy-MM-dd"))).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("Type", invoice.InvoiceType.ToString())).ConfigureAwait(false);
        await writer.WriteLineAsync(Row("Total", Num(invoice.TotalAmount))).ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        await writer.WriteLineAsync(Row(
            "#", "Product", "Quantity", "UnitPrice", "TotalPrice", "Notes")).ConfigureAwait(false);

        for (var i = 0; i < invoice.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = invoice.Items[i];
            await writer.WriteLineAsync(Row(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                item.ProductName,
                Num(item.Quantity),
                Num(item.UnitPrice),
                Num(item.TotalPrice),
                item.Notes)).ConfigureAwait(false);
        }
    }

    // ---- Excel -------------------------------------------------------------

    private static void WriteInvoicesXlsx(IReadOnlyList<Invoice> invoices, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Invoices");

        var headers = new[]
        {
            "InvoiceId", "InvoiceNumber", "Merchant", "City", "Date",
            "Type", "Total", "Items", "CreatedAt"
        };

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        sheet.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var invoice in invoices)
        {
            sheet.Cell(row, 1).Value = invoice.InvoiceId;
            sheet.Cell(row, 2).Value = invoice.InvoiceNumber;
            sheet.Cell(row, 3).Value = invoice.MerchantName;
            sheet.Cell(row, 4).Value = invoice.City;

            // Write a real date so Excel sorts and filters it as one, not as text.
            if (invoice.InvoiceDate is { } date)
            {
                sheet.Cell(row, 5).Value = date.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row, 5).Style.DateFormat.Format = "yyyy-mm-dd";
            }

            sheet.Cell(row, 6).Value = invoice.InvoiceType.ToString();
            sheet.Cell(row, 7).Value = invoice.TotalAmount;
            sheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 8).Value = invoice.Items.Count;
            sheet.Cell(row, 9).Value = invoice.CreatedAt;
            sheet.Cell(row, 9).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            row++;
        }

        sheet.RangeUsed()?.SetAutoFilter();
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    private static void WriteSingleInvoiceXlsx(Invoice invoice, string path)
    {
        using var workbook = new XLWorkbook();

        var header = workbook.AddWorksheet("Invoice");
        header.Cell(1, 1).Value = "Field";
        header.Cell(1, 2).Value = "Value";
        header.Row(1).Style.Font.Bold = true;

        var fields = new (string Label, XLCellValue Value)[]
        {
            ("Invoice Number", invoice.InvoiceNumber ?? string.Empty),
            ("Merchant", invoice.MerchantName),
            ("City", invoice.City ?? string.Empty),
            ("Date", invoice.InvoiceDate?.ToString("yyyy-MM-dd") ?? string.Empty),
            ("Type", invoice.InvoiceType.ToString()),
            ("Total", invoice.TotalAmount)
        };

        for (var i = 0; i < fields.Length; i++)
        {
            header.Cell(i + 2, 1).Value = fields[i].Label;
            header.Cell(i + 2, 2).Value = fields[i].Value;
        }
        header.Columns().AdjustToContents();

        var items = workbook.AddWorksheet("Items");
        var columns = new[] { "#", "Product", "Quantity", "Unit Price", "Total Price", "Notes" };
        for (var c = 0; c < columns.Length; c++)
            items.Cell(1, c + 1).Value = columns[c];
        items.Row(1).Style.Font.Bold = true;

        for (var i = 0; i < invoice.Items.Count; i++)
        {
            var item = invoice.Items[i];
            var row = i + 2;
            items.Cell(row, 1).Value = i + 1;
            items.Cell(row, 2).Value = item.ProductName;
            items.Cell(row, 3).Value = item.Quantity;
            items.Cell(row, 4).Value = item.UnitPrice;
            items.Cell(row, 5).Value = item.TotalPrice;
            items.Cell(row, 6).Value = item.Notes ?? string.Empty;
            items.Range(row, 4, row, 5).Style.NumberFormat.Format = "#,##0.00";
        }

        if (invoice.Items.Count > 0)
        {
            var totalRow = invoice.Items.Count + 2;
            items.Cell(totalRow, 4).Value = "Total";
            items.Cell(totalRow, 5).FormulaA1 = $"SUM(E2:E{totalRow - 1})";
            items.Row(totalRow).Style.Font.Bold = true;
            items.Cell(totalRow, 5).Style.NumberFormat.Format = "#,##0.00";
        }

        items.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    // ---- helpers -----------------------------------------------------------

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static string Num(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Row(params string?[] values)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Escape(values[i]));
        }
        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting. Also quotes any value starting with =, +, - or @, which Excel
    /// would otherwise evaluate as a formula — a CSV injection vector when the text came
    /// from OCR of an untrusted document.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var needsQuotes =
            value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ||
            value[0] is '=' or '+' or '-' or '@';

        if (!needsQuotes) return value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
