using System;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InvoiceDigitizationApp.Views;

public sealed partial class ProductsPage : Page
{
    public ProductsViewModel ViewModel { get; }

    public ProductsPage()
    {
        ViewModel = App.Services.GetRequiredService<ProductsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProduct is not { } product) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "حذف هذا المنتج؟",
            Content = $"سيتم إزالة '{product.Name}' من قائمة المنتجات. بنود الفواتير " +
                      "التي أشارت إليه تحتفظ باسم المنتج لكنها تصبح غير مرتبطة.",
            PrimaryButtonText = "حذف",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteCommand.ExecuteAsync(null);
    }
}
