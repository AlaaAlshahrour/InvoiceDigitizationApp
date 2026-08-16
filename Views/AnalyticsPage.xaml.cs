using System.Globalization;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace InvoiceDigitizationApp.Views;

public sealed partial class AnalyticsPage : Page
{
    public AnalyticsViewModel ViewModel { get; }

    public AnalyticsPage()
    {
        ViewModel = App.Services.GetRequiredService<AnalyticsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    public string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Red when purchases exceed sales, so the sign is readable at a glance.</summary>
    public Brush NetBrush(decimal net) => net < 0
        ? new SolidColorBrush(Colors.OrangeRed)
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public Visibility NoProducts(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
