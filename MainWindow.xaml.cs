using System;
using InvoiceDigitizationApp.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace InvoiceDigitizationApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ContentFrame.Navigate(typeof(InvoicesDashboardPage), null, new EntranceNavigationTransitionInfo());
    }

    private void Nav_SelectionChanged(
        NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var pageType = (item.Tag as string) switch
        {
            "invoices" => typeof(InvoicesDashboardPage),
            "processing" => typeof(ProcessingPage),
            "products" => typeof(ProductsPage),
            "customers" => typeof(CustomersPage),
            "analytics" => typeof(AnalyticsPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
    }

    /// <summary>Navigates to the verification screen with an invoice already loaded.</summary>
    public void NavigateToProcessing(object? parameter = null)
    {
        foreach (var menuItem in Nav.MenuItems)
        {
            if (menuItem is NavigationViewItem { Tag: "processing" } item)
            {
                // Setting the selection would re-navigate without the parameter, so the
                // frame is navigated directly and the selection synced to match.
                Nav.SelectionChanged -= Nav_SelectionChanged;
                Nav.SelectedItem = item;
                Nav.SelectionChanged += Nav_SelectionChanged;
                break;
            }
        }

        ContentFrame.Navigate(typeof(ProcessingPage), parameter, new EntranceNavigationTransitionInfo());
    }

    public void ShowErrorBanner(string message)
    {
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
    }

    /// <summary>Applies a theme chosen in Settings to the whole window.</summary>
    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }
}
