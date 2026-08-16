using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>One bar in the sales-vs-purchases chart.</summary>
public sealed class MonthlyBar
{
    public string Label { get; init; } = string.Empty;
    public decimal Sales { get; init; }
    public decimal Purchases { get; init; }

    /// <summary>Bar height in pixels, scaled by the ViewModel against the largest value.</summary>
    public double SalesHeight { get; set; }
    public double PurchasesHeight { get; set; }
}

/// <summary>One slice of the best-selling-products chart.</summary>
public sealed class ProductShare
{
    public string ProductName { get; init; } = string.Empty;
    public decimal Revenue { get; init; }
    public double Percentage { get; set; }

    /// <summary>Bar width in pixels; a proportional bar reads more precisely than a pie.</summary>
    public double BarWidth { get; set; }
    public string ColorHex { get; set; } = "#4C7DE0";

    /// <summary>Value and share, e.g. "1,240.00 (32%)".</summary>
    public string ShareLabel =>
        $"{Revenue.ToString("N2", CultureInfo.CurrentCulture)} ({Percentage:0}%)";
}

public partial class AnalyticsViewModel : ViewModelBase
{
    private const double MaxBarHeight = 160.0;
    private const double MaxBarWidth = 320.0;

    /// <summary>
    /// Colour-blind-safe categorical palette. Distinguishable under deuteranopia and
    /// protanopia, unlike an evenly-spaced hue rotation.
    /// </summary>
    private static readonly string[] Palette =
    {
        "#4C7DE0", "#E0724C", "#3FA7A7", "#B45CC7", "#D9A441", "#5FBFA8"
    };

    private readonly IInvoiceRepository _invoices;

    public AnalyticsViewModel(IInvoiceRepository invoices) => _invoices = invoices;

    [ObservableProperty] private decimal _totalSales;
    [ObservableProperty] private decimal _totalPurchases;
    [ObservableProperty] private int _invoicesThisMonth;
    [ObservableProperty] private decimal _netPosition;
    [ObservableProperty] private int _totalInvoices;
    [ObservableProperty] private bool _hasData;

    public ObservableCollection<MonthlyBar> MonthlyBars { get; } = new();
    public ObservableCollection<ProductShare> TopProducts { get; } = new();

    public async Task LoadAsync()
    {
        await RunGuardedAsync(LoadCoreAsync, "تعذّر تحميل التحليلات");
    }

    [RelayCommand]
    private async Task RefreshAsync() =>
        await RunGuardedAsync(LoadCoreAsync, "تعذّر تحديث التحليلات");

    private async Task LoadCoreAsync()
    {
        // Load headers once, then compute every card and chart from that one set —
        // six separate aggregate queries would be slower and could disagree if an
        // invoice were written between them.
        var all = await _invoices.SearchAsync(new InvoiceFilter());

        TotalInvoices = all.Count;
        HasData = all.Count > 0;

        TotalSales = all.Where(i => i.InvoiceType == InvoiceType.Sale).Sum(i => i.TotalAmount);
        TotalPurchases = all.Where(i => i.InvoiceType == InvoiceType.Purchase).Sum(i => i.TotalAmount);
        NetPosition = TotalSales - TotalPurchases;

        var today = DateOnly.FromDateTime(DateTime.Today);
        InvoicesThisMonth = all.Count(i =>
            i.InvoiceDate is { } date && date.Year == today.Year && date.Month == today.Month);

        BuildMonthlyBars(all);
        await BuildTopProductsAsync(all);

        SetStatus(HasData
            ? $"تم تحليل {all.Count} فاتورة."
            : "لا توجد فواتير بعد — ستظهر التحليلات بمجرد حفظ بعضها.");
    }

    private void BuildMonthlyBars(IReadOnlyList<Invoice> invoices)
    {
        MonthlyBars.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var months = Enumerable.Range(0, 6)
            .Select(offset => new DateOnly(today.Year, today.Month, 1).AddMonths(-5 + offset))
            .ToList();

        var bars = months.Select(month =>
        {
            var inMonth = invoices
                .Where(i => i.InvoiceDate is { } d && d.Year == month.Year && d.Month == month.Month)
                .ToList();

            return new MonthlyBar
            {
                Label = month.ToString("MMM", CultureInfo.GetCultureInfo("ar")),
                Sales = inMonth.Where(i => i.InvoiceType == InvoiceType.Sale).Sum(i => i.TotalAmount),
                Purchases = inMonth.Where(i => i.InvoiceType == InvoiceType.Purchase).Sum(i => i.TotalAmount)
            };
        }).ToList();

        // Scale against the largest single bar so the tallest always fills the plot.
        var max = bars.SelectMany(b => new[] { b.Sales, b.Purchases }).DefaultIfEmpty(0m).Max();

        foreach (var bar in bars)
        {
            bar.SalesHeight = ScaleHeight(bar.Sales, max);
            bar.PurchasesHeight = ScaleHeight(bar.Purchases, max);
            MonthlyBars.Add(bar);
        }
    }

    private static double ScaleHeight(decimal value, decimal max) =>
        max <= 0m ? 0.0 : (double)(value / max) * MaxBarHeight;

    private async Task BuildTopProductsAsync(IReadOnlyList<Invoice> invoices)
    {
        TopProducts.Clear();

        // Line items are not loaded by SearchAsync, so fetch the full invoices. Capped
        // at the 200 most recent: this is a dashboard, not an accounting report, and
        // loading an unbounded history would stall the UI.
        var recent = invoices.Take(200).ToList();

        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in recent)
        {
            var full = await _invoices.GetByIdAsync(header.InvoiceId);
            if (full is null) continue;

            foreach (var item in full.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ProductName)) continue;

                totals.TryGetValue(item.ProductName, out var running);
                totals[item.ProductName] = running + item.TotalPrice;
            }
        }

        var top = totals
            .OrderByDescending(pair => pair.Value)
            .Take(6)
            .Select(pair => new ProductShare { ProductName = pair.Key, Revenue = pair.Value })
            .ToList();

        var grandTotal = top.Sum(p => p.Revenue);
        var largest = top.Select(p => p.Revenue).DefaultIfEmpty(0m).Max();

        for (var i = 0; i < top.Count; i++)
        {
            var share = top[i];
            share.Percentage = grandTotal > 0m ? (double)(share.Revenue / grandTotal) * 100.0 : 0.0;
            share.BarWidth = largest > 0m ? (double)(share.Revenue / largest) * MaxBarWidth : 0.0;
            share.ColorHex = Palette[i % Palette.Length];
            TopProducts.Add(share);
        }
    }
}
