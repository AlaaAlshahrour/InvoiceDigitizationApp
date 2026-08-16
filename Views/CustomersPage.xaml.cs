using System;
using System.Globalization;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InvoiceDigitizationApp.Views;

public sealed partial class CustomersPage : Page
{
    public CustomersViewModel ViewModel { get; }

    public CustomersPage()
    {
        ViewModel = App.Services.GetRequiredService<CustomersViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    public string LinkedTotalLabel(decimal total) =>
        total == 0m ? string.Empty : $"الإجمالي: {total.ToString("N2", CultureInfo.CurrentCulture)}";

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCustomer is not { } customer) return;

        var isSupplier = customer.CustomerType == Models.CustomerType.Supplier;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isSupplier ? "حذف هذا المورد؟" : "حذف هذا العميل؟",
            Content = $"سيتم حذف '{customer.Name}'. سيتم الاحتفاظ بفواتيره، لكنها " +
                      "ستصبح غير مرتبطة بأي سجل.",
            PrimaryButtonText = "حذف",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteCommand.ExecuteAsync(null);
    }
}
