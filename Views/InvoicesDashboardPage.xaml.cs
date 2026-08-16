using System;
using System.Linq;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Helpers;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace InvoiceDigitizationApp.Views;

public sealed partial class InvoicesDashboardPage : Page
{
    public InvoicesDashboardViewModel ViewModel { get; }

    public InvoicesDashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<InvoicesDashboardViewModel>();
        InitializeComponent();

        ViewModel.OpenInvoiceRequested += OnOpenInvoiceRequested;
        ViewModel.ImportRequested += OnImportRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Detach so a page that has been navigated away from cannot act on events.
        ViewModel.OpenInvoiceRequested -= OnOpenInvoiceRequested;
        ViewModel.ImportRequested -= OnImportRequested;
        base.OnNavigatedFrom(e);
    }

    /// <summary>Visibility helper for the empty state; x:Bind cannot invert a converter.</summary>
    public Visibility IsEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Brush ErrorBrush(bool hasError) => hasError
        ? new SolidColorBrush(Colors.OrangeRed)
        : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.SearchCommand.ExecuteAsync(null);
    }

    private void InvoicesGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.OpenSelectedCommand.CanExecute(null))
            ViewModel.OpenSelectedCommand.Execute(null);
    }

    private void OnOpenInvoiceRequested(object? sender, int invoiceId)
    {
        GetMainWindow()?.NavigateToProcessing(invoiceId);
    }

    private async void OnImportRequested(object? sender, EventArgs e) => await ImportAsync();

    private async Task ImportAsync()
    {
        var window = GetMainWindow();
        if (window is null) return;

        var paths = await FilePickerHelper.PickInvoiceFilesAsync(window);
        if (paths.Count == 0) return;

        // Always an array, even for one file: the verification screen treats a single
        // import as a batch of one so both routes share the same processing path.
        window.NavigateToProcessing(paths.ToArray());
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedInvoice is not { } invoice) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "حذف هذه الفاتورة؟",
            Content = $"سيتم حذف السجل رقم {invoice.InvoiceId} من {invoice.MerchantName} وجميع " +
                      "بنودها نهائيًا. لا يمكن التراجع عن هذا الإجراء.",
            PrimaryButtonText = "حذف",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
    }

    private static MainWindow? GetMainWindow() =>
        (Application.Current as App)?.MainWindowInstance;
}
