using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.ViewModels;

public partial class ProductsViewModel : ViewModelBase
{
    private readonly IProductRepository _products;

    public ProductsViewModel(IProductRepository products) => _products = products;

    public ObservableCollection<Product> Products { get; } = new();

    [ObservableProperty] private Product? _selectedProduct;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _isEditing;

    private bool _isNew;

    public async Task LoadAsync()
    {
        await RunGuardedAsync(LoadCoreAsync, "تعذّر تحميل المنتجات");
    }

    private async Task LoadCoreAsync()
    {
        var all = await _products.GetAllAsync();

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? all
            : all.Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                 .ToList();

        Products.Clear();
        foreach (var product in filtered) Products.Add(product);

        SetStatus($"{Products.Count} منتج.");
    }

    [RelayCommand]
    private async Task SearchAsync() =>
        await RunGuardedAsync(LoadCoreAsync, "فشل البحث");

    [RelayCommand]
    private void NewProduct()
    {
        _isNew = true;
        IsEditing = true;
        SelectedProduct = null;

        EditName = string.Empty;

        ClearStatus();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditProduct()
    {
        if (SelectedProduct is not { } product) return;

        _isNew = false;
        IsEditing = true;

        EditName = product.Name;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        ClearStatus();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            SetError("اسم المنتج مطلوب.");
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var name = EditName.Trim();

            // Products.Name has a UNIQUE index; check first so the user gets a clear
            // message instead of a raw constraint violation. A rename can collide too,
            // so the check runs for edits as well — excluding the row being edited.
            var existing = await _products.FindByNameAsync(name);
            if (existing is not null && existing.ProductId != SelectedProduct?.ProductId)
            {
                SetError($"يوجد بالفعل منتج باسم '{name}'.");
                return;
            }

            if (_isNew)
            {
                await _products.CreateAsync(new Product { Name = name });
            }
            else
            {
                if (SelectedProduct is not { } product) return;

                product.Name = name;
                await _products.UpdateAsync(product);
            }

            IsEditing = false;
            await LoadCoreAsync();
            SetStatus(_isNew ? "تمت إضافة المنتج." : "تم تحديث المنتج.");
        }, "تعذّر حفظ المنتج");
    }

    /// <summary>Deletes the selected product. The View confirms first.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedProduct is not { } product) return;

        await RunGuardedAsync(async () =>
        {
            // InvoiceItems.ProductId is ON DELETE SET NULL, so historical invoice lines
            // keep their ProductName text and simply become unlinked.
            await _products.DeleteAsync(product.ProductId);
            Products.Remove(product);
            SelectedProduct = null;

            SetStatus($"تم حذف '{product.Name}'.");
        }, "تعذّر حذف المنتج");
    }

    private bool HasSelection() => SelectedProduct is not null;

    partial void OnSelectedProductChanged(Product? value)
    {
        EditProductCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }
}
