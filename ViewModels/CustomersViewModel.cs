using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.ViewModels;

public partial class CustomersViewModel : ViewModelBase
{
    private readonly ICustomerRepository _customers;
    private readonly IInvoiceRepository _invoices;

    public CustomersViewModel(ICustomerRepository customers, IInvoiceRepository invoices)
    {
        _customers = customers;
        _invoices = invoices;
    }

    public ObservableCollection<Customer> Customers { get; } = new();

    /// <summary>Invoices linked to the selected customer.</summary>
    public ObservableCollection<Invoice> LinkedInvoices { get; } = new();

    public string[] CustomerTypeOptions { get; } =
        { nameof(CustomerType.Customer), nameof(CustomerType.Supplier) };

    [ObservableProperty] private Customer? _selectedCustomer;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _isEditing;

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string? _editAliasName;
    [ObservableProperty] private string? _editCity;
    [ObservableProperty] private string? _editPhone;
    [ObservableProperty] private string _editCustomerType = nameof(CustomerType.Customer);

    [ObservableProperty] private decimal _linkedInvoiceTotal;

    private bool _isNew;

    /// <summary>
    /// What the record being edited is called. Follows the editor's own type picker, not
    /// the grid selection: while adding a supplier the editor must say "مورد" even though
    /// the row still highlighted in the grid behind it may be a customer.
    /// </summary>
    public string EditorTypeLabel =>
        EditCustomerType == nameof(CustomerType.Supplier) ? "المورد" : "العميل";

    public string EditorTitle => _isNew
        ? (EditCustomerType == nameof(CustomerType.Supplier) ? "مورد جديد" : "عميل جديد")
        : $"تفاصيل {EditorTypeLabel}";

    partial void OnEditCustomerTypeChanged(string value)
    {
        OnPropertyChanged(nameof(EditorTypeLabel));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public async Task LoadAsync() =>
        await RunGuardedAsync(LoadCoreAsync, "تعذّر تحميل العملاء والموردين");

    private async Task LoadCoreAsync()
    {
        var all = await _customers.GetAllAsync();

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? all
            : all.Where(c =>
                    c.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    (c.AliasName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                 .ToList();

        Customers.Clear();
        foreach (var customer in filtered) Customers.Add(customer);

        SetStatus($"{Customers.Count} سجل.");
    }

    [RelayCommand]
    private async Task SearchAsync() => await RunGuardedAsync(LoadCoreAsync, "فشل البحث");

    private async Task LoadLinkedInvoicesAsync(int customerId)
    {
        var results = await _invoices.SearchAsync(new InvoiceFilter { CustomerId = customerId });

        LinkedInvoices.Clear();
        foreach (var invoice in results) LinkedInvoices.Add(invoice);

        LinkedInvoiceTotal = results.Sum(i => i.TotalAmount);
    }

    [RelayCommand]
    private void NewCustomer()
    {
        _isNew = true;
        IsEditing = true;
        SelectedCustomer = null;

        EditName = string.Empty;
        EditAliasName = null;
        EditCity = null;
        EditPhone = null;
        EditCustomerType = nameof(CustomerType.Customer);

        OnPropertyChanged(nameof(EditorTitle));
        ClearStatus();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditCustomer()
    {
        if (SelectedCustomer is not { } customer) return;

        _isNew = false;
        IsEditing = true;

        EditName = customer.Name;
        EditAliasName = customer.AliasName;
        EditCity = customer.City;
        EditPhone = customer.Phone;
        EditCustomerType = customer.CustomerType.ToString();

        OnPropertyChanged(nameof(EditorTitle));
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
            SetError("الاسم مطلوب.");
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var type = Enum.TryParse<CustomerType>(EditCustomerType, out var parsed)
                ? parsed : CustomerType.Customer;

            if (_isNew)
            {
                await _customers.CreateAsync(new Customer
                {
                    Name = EditName.Trim(),
                    AliasName = EditAliasName?.Trim(),
                    City = EditCity?.Trim(),
                    Phone = EditPhone?.Trim(),
                    CustomerType = type
                });
            }
            else
            {
                if (SelectedCustomer is not { } customer) return;

                customer.Name = EditName.Trim();
                customer.AliasName = EditAliasName?.Trim();
                customer.City = EditCity?.Trim();
                customer.Phone = EditPhone?.Trim();
                customer.CustomerType = type;

                await _customers.UpdateAsync(customer);
            }

            var label = type == CustomerType.Supplier ? "المورد" : "العميل";

            IsEditing = false;
            await LoadCoreAsync();
            SetStatus(_isNew ? $"تمت إضافة {label}." : $"تم تحديث {label}.");
        }, "تعذّر الحفظ");
    }

    /// <summary>Deletes the selected contact. The View confirms first.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedCustomer is not { } customer) return;

        await RunGuardedAsync(async () =>
        {
            // Invoices.CustomerId is ON DELETE SET NULL: invoice history survives and
            // simply becomes unlinked, keeping its MerchantName text.
            await _customers.DeleteAsync(customer.CustomerId);
            Customers.Remove(customer);
            SelectedCustomer = null;
            LinkedInvoices.Clear();

            SetStatus($"تم حذف '{customer.Name}'. تم الاحتفاظ بفواتيره مع إلغاء ارتباطها.");
        }, "تعذّر الحذف");
    }

    private bool HasSelection() => SelectedCustomer is not null;

    partial void OnSelectedCustomerChanged(Customer? value)
    {
        EditCustomerCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();

        LinkedInvoices.Clear();
        LinkedInvoiceTotal = 0m;

        if (value is not null)
        {
            // Fire-and-forget is acceptable here: RunGuardedAsync captures any failure
            // into the status line, and selection changes must not block the UI thread.
            _ = RunGuardedAsync(
                () => LoadLinkedInvoicesAsync(value.CustomerId),
                "تعذّر تحميل الفواتير المرتبطة");
        }
    }
}
