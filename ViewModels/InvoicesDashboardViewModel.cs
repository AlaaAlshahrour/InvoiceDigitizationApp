using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.Data;
using InvoiceDigitizationApp.Services.Export;

namespace InvoiceDigitizationApp.ViewModels;

public partial class InvoicesDashboardViewModel : ViewModelBase
{
    private readonly IInvoiceRepository _invoices;
    private readonly ISettingsRepository _settings;
    private readonly IExportService _export;

    public InvoicesDashboardViewModel(
        IInvoiceRepository invoices,
        ISettingsRepository settings,
        IExportService export)
    {
        _invoices = invoices;
        _settings = settings;
        _export = export;
    }

    public ObservableCollection<Invoice> Invoices { get; } = new();
    public ObservableCollection<string> Cities { get; } = new();

    /// <summary>Filter values, including the "any" sentinel at index 0.</summary>
    public IReadOnlyList<string> TypeOptions { get; } = new[] { "All", "Purchase", "Sale" };

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private DateTimeOffset? _dateFrom;
    [ObservableProperty] private DateTimeOffset? _dateTo;
    [ObservableProperty] private string _selectedType = "All";
    [ObservableProperty] private string? _selectedCity;
    [ObservableProperty] private Invoice? _selectedInvoice;
    [ObservableProperty] private bool _isFilterPaneOpen;

    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private decimal _resultTotal;

    /// <summary>True when any filter beyond the default is active.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) ||
        DateFrom is not null || DateTo is not null ||
        SelectedType != "All" ||
        !string.IsNullOrWhiteSpace(SelectedCity);

    /// <summary>Raised when the user asks to open an invoice; the View handles navigation.</summary>
    public event EventHandler<int>? OpenInvoiceRequested;

    /// <summary>Raised when the user asks to import a new invoice.</summary>
    public event EventHandler? ImportRequested;

    public async Task LoadAsync()
    {
        await RunGuardedAsync(async () =>
        {
            await RefreshCitiesAsync();
            await ApplyFilterCoreAsync();
        }, "تعذّر تحميل الفواتير");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await RunGuardedAsync(ApplyFilterCoreAsync, "فشل البحث");
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        SearchText = null;
        DateFrom = null;
        DateTo = null;
        SelectedType = "All";
        SelectedCity = null;

        await RunGuardedAsync(ApplyFilterCoreAsync, "تعذّر إعادة تحميل الفواتير");
    }

    private async Task ApplyFilterCoreAsync()
    {
        var filter = new InvoiceFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            DateFrom = DateFrom is { } from ? DateOnly.FromDateTime(from.Date) : null,
            DateTo = DateTo is { } to ? DateOnly.FromDateTime(to.Date) : null,
            Type = SelectedType switch
            {
                "Purchase" => InvoiceType.Purchase,
                "Sale" => InvoiceType.Sale,
                _ => null
            },
            City = string.IsNullOrWhiteSpace(SelectedCity) ? null : SelectedCity
        };

        var results = await _invoices.SearchAsync(filter);

        Invoices.Clear();
        foreach (var invoice in results)
            Invoices.Add(invoice);

        ResultCount = results.Count;
        ResultTotal = results.Sum(i => i.TotalAmount);

        OnPropertyChanged(nameof(HasActiveFilters));

        SetStatus(results.Count == 0
            ? (HasActiveFilters ? "لا توجد فواتير تطابق عوامل التصفية هذه." : "لا توجد فواتير بعد. استورد واحدة للبدء.")
            : $"{results.Count} فاتورة، بإجمالي {ResultTotal.ToString("N2", CultureInfo.CurrentCulture)}.");
    }

    private async Task RefreshCitiesAsync()
    {
        var cities = await _invoices.GetDistinctCitiesAsync();

        Cities.Clear();
        foreach (var city in cities)
            Cities.Add(city);
    }

    [RelayCommand]
    private void ImportInvoice() => ImportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private void OpenSelected()
    {
        if (SelectedInvoice is { } invoice)
            OpenInvoiceRequested?.Invoke(this, invoice.InvoiceId);
    }

    private bool CanOperateOnSelection() => SelectedInvoice is not null;

    /// <summary>
    /// Deletes the selected invoice. The View is responsible for confirming first —
    /// this method performs the delete unconditionally.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedInvoice is not { } invoice) return;

        await RunGuardedAsync(async () =>
        {
            await _invoices.DeleteAsync(invoice.InvoiceId);
            Invoices.Remove(invoice);

            ResultCount = Invoices.Count;
            ResultTotal = Invoices.Sum(i => i.TotalAmount);
            SelectedInvoice = null;

            SetStatus($"تم حذف السجل رقم {invoice.InvoiceId}.");
        }, "تعذّر حذف الفاتورة");
    }

    [RelayCommand]
    private async Task ExportAsync(string? formatName)
    {
        var format = string.Equals(formatName, "csv", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.Csv
            : ExportFormat.Excel;

        await RunGuardedAsync(async () =>
        {
            if (Invoices.Count == 0)
            {
                SetError("لا يوجد شيء للتصدير.");
                return;
            }

            var directory = await _settings.GetOrDefaultAsync(
                SettingKeys.ExportDirectory, AppPaths.DefaultExportDirectory);

            var fileName = $"invoices_{DateTime.Now:yyyyMMdd_HHmmss}{_export.GetExtension(format)}";
            var path = Path.Combine(directory, fileName);

            await _export.ExportInvoicesAsync(Invoices.ToList(), path, format);
            SetStatus($"تم تصدير {Invoices.Count} فاتورة إلى {path}");
        }, "فشل التصدير");
    }

    partial void OnSelectedInvoiceChanged(Invoice? value)
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}
