using System;
using System.Threading.Tasks;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace InvoiceDigitizationApp.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        ViewModel.ThemeChangeRequested += OnThemeChangeRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ThemeChangeRequested -= OnThemeChangeRequested;
        base.OnNavigatedFrom(e);
    }

    public string DatabaseLabel(string path) => $"قاعدة البيانات: {path}";

    private void OnThemeChangeRequested(object? sender, string theme) =>
        (Application.Current as App)?.MainWindowInstance?.ApplyTheme(theme);

    private async void BrowseExport_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null) ViewModel.ExportDirectory = folder;
    }

    private async void BrowseImages_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null) ViewModel.ImageStorageDirectory = folder;
    }

    private async Task<string?> PickFolderAsync()
    {
        var window = (Application.Current as App)?.MainWindowInstance;
        if (window is null) return null;

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };

        // FolderPicker throws without at least one filter, even though it picks folders.
        picker.FileTypeFilter.Add("*");

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "إعادة تعيين الإعدادات؟",
            Content = "ستعود جميع الإعدادات إلى قيمها الافتراضية. فواتيرك وبياناتك لن تتأثر.",
            PrimaryButtonText = "إعادة تعيين",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.ResetToDefaultsCommand.ExecuteAsync(null);
    }
}
