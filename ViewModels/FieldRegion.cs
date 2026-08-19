using System;
using System.Collections.Generic;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// Which field of the invoice a box on the image belongs to. Used to tie a cell in the
/// form to its region on the page, in both directions.
/// </summary>
/// <remarks>
/// A row's cells carry the row index; the header fields do not, and use -1. The pair
/// (<see cref="FieldKind"/>, row index) is the whole identity — it is what the click
/// handler looks a region up by, so it has to be derivable from the control that was
/// clicked without holding a reference to the region itself.
/// </remarks>
public enum FieldKind
{
    MerchantName,
    InvoiceNumber,
    InvoiceDate,
    City,
    TotalAmount,
    ProductName,
    Quantity,
    UnitPrice,
    LineTotal
}

/// <summary>
/// One cell's box on the corrected page, in the coordinate space the service reported —
/// <c>source.width</c> × <c>source.height</c>, the same space every box in a response
/// shares because geometric correction runs once upstream of both pipeline branches.
/// </summary>
/// <remarks>
/// Deliberately free of WinUI types. The overlay converts these to control coordinates at
/// draw time, against whatever the image is scaled to right then; storing pixels here
/// would bake in a zoom level and be wrong the moment the pane resized.
/// </remarks>
public sealed class FieldRegion
{
    public FieldRegion(
        FieldKind kind, int rowIndex, int x, int y, int width, int height, string label)
    {
        Kind = kind;
        RowIndex = rowIndex;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Label = label;
    }

    public FieldKind Kind { get; }

    /// <summary>The line item this box belongs to, or -1 for a header field.</summary>
    public int RowIndex { get; }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>What the box is called in the overlay's tooltip, in Arabic.</summary>
    public string Label { get; }

    /// <summary>True when this region names the same cell as <paramref name="other"/>.</summary>
    public bool Matches(FieldKind kind, int rowIndex) =>
        Kind == kind && RowIndex == rowIndex;
}

/// <summary>
/// Every box an extraction reported, plus the page size they are measured against.
/// </summary>
public sealed class FieldRegionMap
{
    public static readonly FieldRegionMap Empty = new(0, 0, Array.Empty<FieldRegion>());

    public FieldRegionMap(int pageWidth, int pageHeight, IReadOnlyList<FieldRegion> regions)
    {
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        Regions = regions;
    }

    /// <summary>The corrected page's width, and the space <see cref="Regions"/> use.</summary>
    public int PageWidth { get; }

    public int PageHeight { get; }

    public IReadOnlyList<FieldRegion> Regions { get; }

    /// <summary>
    /// False when there is nothing to draw — no boxes, or a page size of zero that would
    /// make every scale factor a division by zero.
    /// </summary>
    public bool HasRegions => Regions.Count > 0 && PageWidth > 0 && PageHeight > 0;

    /// <summary>
    /// Collects every box in a response, tagged with the field it came from.
    /// </summary>
    /// <remarks>
    /// Boxes come off <c>bounding_box</c>, which the contract allows to be null for any
    /// field — a value the parser inferred rather than read has no place on the page. A
    /// degenerate box is dropped too: a zero-width rectangle is not something the user can
    /// click, and scaling one produces a highlight that cannot be seen.
    /// </remarks>
    public static FieldRegionMap From(ExtractionResult? result)
    {
        if (result?.Source is not { } source) return Empty;

        var regions = new List<FieldRegion>();

        Add(FieldKind.MerchantName, -1, result.CustomerName?.BoundingBox, "التاجر");
        Add(FieldKind.InvoiceNumber, -1, result.InvoiceId?.BoundingBox, "رقم الفاتورة");
        Add(FieldKind.InvoiceDate, -1, result.Date?.BoundingBox, "التاريخ");
        Add(FieldKind.City, -1, result.City?.BoundingBox, "المدينة");
        Add(FieldKind.TotalAmount, -1, result.TotalInvoicePrice?.BoundingBox, "الإجمالي");

        for (var i = 0; i < result.Products.Count; i++)
        {
            var row = result.Products[i];
            var line = i + 1;

            Add(FieldKind.ProductName, i, row.ProductName?.BoundingBox, $"المنتج (بند {line})");
            Add(FieldKind.Quantity, i, row.Quantity?.BoundingBox, $"الكمية (بند {line})");
            Add(FieldKind.UnitPrice, i, row.UnitPrice?.BoundingBox, $"سعر الوحدة (بند {line})");
            Add(FieldKind.LineTotal, i, row.TotalPrice?.BoundingBox, $"إجمالي البند (بند {line})");
        }

        return new FieldRegionMap(source.Width, source.Height, regions);

        void Add(FieldKind kind, int rowIndex, BoundingBox? box, string label)
        {
            if (box is null || box.W <= 0 || box.H <= 0) return;

            regions.Add(new FieldRegion(kind, rowIndex, box.X, box.Y, box.W, box.H, label));
        }
    }
}
