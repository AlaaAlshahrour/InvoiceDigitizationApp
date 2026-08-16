using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.UI.Controls;
using InvoiceDigitizationApp.Helpers;
using InvoiceDigitizationApp.Services.Validation;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;
using Windows.UI;

namespace InvoiceDigitizationApp.Views;

public sealed partial class ProcessingPage : Page
{
    /// <summary>
    /// True while a ContentDialog is on screen. A XamlRoot hosts exactly one dialog at a
    /// time and throws a COMException on the second, and this screen can now try for two:
    /// saving a batch item auto-advances to the next one, so a duplicate prompt for the
    /// item just saved can still be open when the next item's own prompt is raised.
    /// </summary>
    private bool _dialogOpen;

    public ProcessingViewModel ViewModel { get; }

    public ProcessingPage()
    {
        ViewModel = App.Services.GetRequiredService<ProcessingViewModel>();
        InitializeComponent();

        ViewModel.DuplicatesDetected += OnDuplicatesDetected;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        await ViewModel.InitializeAsync();

        // The dashboard passes an int to review a saved invoice, or an array of paths to
        // import. Navigating with no parameter means the user came back from another
        // page, so any batch in progress is picked up where it was left.
        switch (e.Parameter)
        {
            case int invoiceId:
                await ViewModel.LoadInvoiceAsync(invoiceId);
                await LoadImageAsync(ViewModel.CurrentImagePath);
                break;

            case string[] paths when paths.Length > 0:
                await ViewModel.StartBatchAsync(paths);
                await LoadImageAsync(ViewModel.CurrentImagePath);
                break;

            default:
                await ViewModel.ResumeBatchAsync();
                await LoadImageAsync(ViewModel.CurrentImagePath);
                break;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.DuplicatesDetected -= OnDuplicatesDetected;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // The batch service is a singleton and holds the ViewModel through its Changed
        // event; without this the whole screen leaks on every navigation.
        ViewModel.Detach();

        base.OnNavigatedFrom(e);
    }

    private async void OnViewModelPropertyChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.ZoomFactor))
            ImageScroller.ChangeView(null, null, (float)ViewModel.ZoomFactor);

        // Follows the ViewModel's choice of rendering, so the toggle and the batch both
        // repaint the pane through one path.
        else if (e.PropertyName == nameof(ViewModel.CurrentImagePath))
            await LoadImageAsync(ViewModel.CurrentImagePath);
    }

    // ---- rendering picker -------------------------------------------------

    // One predicate per RadioButton. x:Bind evaluates these one-way from the ViewModel's
    // choice; the Checked handlers below push a click back the other way. A converter
    // would need a ConverterParameter per button and would still only read one way.

    public bool IsOriginalView(InvoiceImageView view) => view == InvoiceImageView.Original;

    public bool IsEnhancedView(InvoiceImageView view) => view == InvoiceImageView.Enhanced;

    public bool IsOcrView(InvoiceImageView view) => view == InvoiceImageView.OcrInput;

    // Assigning the value the ViewModel already holds is a no-op on an ObservableProperty,
    // so the round trip from binding to Checked and back stops here rather than looping.
    private void OriginalView_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedImageView = InvoiceImageView.Original;

    private void EnhancedView_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedImageView = InvoiceImageView.Enhanced;

    private void OcrView_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedImageView = InvoiceImageView.OcrInput;

    // ---- x:Bind helpers ---------------------------------------------------

    public Visibility HasNoImage(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Visibility.Visible : Visibility.Collapsed;

    public string ZoomLabel(double zoom) => $"{zoom * 100:0}%";

    public string UnsavedLabel(bool unsaved) => unsaved ? "تغييرات غير محفوظة" : string.Empty;

    public Brush StatusBrush(bool hasError) => hasError
        ? new SolidColorBrush(Colors.OrangeRed)
        : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public Brush TotalBrush(bool disagree) => disagree
        ? new SolidColorBrush(Colors.OrangeRed)
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    // ---- image ------------------------------------------------------------

    /// <summary>
    /// Loads a rendering into the image pane.
    /// </summary>
    /// <remarks>
    /// Reads the bytes and decodes from a MemoryStream rather than using
    /// <c>new BitmapImage(new Uri(path))</c>. The URI form leaves the file open for as
    /// long as the bitmap lives, which turned skipping a batch item — that deletes the
    /// very file being displayed — into an IOException.
    /// </remarks>
    private async Task LoadImageAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            InvoiceImage.Source = null;
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            InvoiceImage.Source = bitmap;
        }
        catch (Exception)
        {
            // A preview failure must not prevent verifying the extracted data.
            InvoiceImage.Source = null;
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var window = (Application.Current as App)?.MainWindowInstance;
        if (window is null) return;

        var paths = await FilePickerHelper.PickInvoiceFilesAsync(window);
        if (paths.Count == 0) return;

        // One file or many, the same batch path runs — a single import is a batch of one.
        await ViewModel.StartBatchAsync(paths);
    }

    // ---- grid -------------------------------------------------------------

    /// <summary>
    /// Tints rows that fail the arithmetic check. Runs on row realization; the handler
    /// re-subscribes per row so a row edited later updates its own colour.
    /// </summary>
    private void ItemsGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not InvoiceItemRowViewModel row) return;

        ApplyRowStyle(e.Row, row);

        row.PropertyChanged -= OnRowPropertyChanged;
        row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(InvoiceItemRowViewModel.IsArithmeticValid)
                                or nameof(InvoiceItemRowViewModel.IsProductLinked)))
        {
            return;
        }

        if (sender is not InvoiceItemRowViewModel row) return;

        // Re-tint the realized row for this item, if it is currently on screen.
        foreach (var container in FindRows(ItemsGrid))
        {
            if (ReferenceEquals(container.DataContext, row))
            {
                ApplyRowStyle(container, row);
                break;
            }
        }
    }

    private static void ApplyRowStyle(DataGridRow container, InvoiceItemRowViewModel row)
    {
        container.Background = row switch
        {
            // An unlinked product blocks saving, so it outranks the arithmetic warning.
            { IsProductLinked: false } => new SolidColorBrush(Color.FromArgb(48, 220, 38, 38)),
            { IsArithmeticValid: false } => new SolidColorBrush(Color.FromArgb(48, 220, 38, 38)),
            { IsLowConfidence: true } => new SolidColorBrush(Color.FromArgb(40, 217, 164, 65)),
            _ => new SolidColorBrush(Colors.Transparent)
        };
    }

    private static IEnumerable<DataGridRow> FindRows(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is DataGridRow row) yield return row;

            foreach (var nested in FindRows(child))
                yield return nested;
        }
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is InvoiceItemRowViewModel row)
            ViewModel.RemoveItemCommand.Execute(row);
    }

    private async void AddProductToCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not InvoiceItemRowViewModel row)
        {
            await ShowMessageAsync("اختر صفًا", "حدّد البند الذي تريد إضافة منتجه إلى قائمة المنتجات.");
            return;
        }

        if (row.IsProductLinked)
        {
            await ShowMessageAsync(
                "المنتج مسجّل بالفعل",
                $"'{row.ProductName}' موجود في المنتجات ومرتبط بهذا البند.");
            return;
        }

        await ViewModel.AddProductToCatalogCommand.ExecuteAsync(row);
    }

    // ---- dialogs ----------------------------------------------------------

    /// <summary>
    /// Shows a dialog if this page can host one right now, and reports what the user
    /// chose. Returns <see cref="ContentDialogResult.None"/> — the same value a dismissed
    /// dialog gives — when another one is already up or the page has been navigated away
    /// from, so callers treat a suppressed prompt as "not confirmed" without a special case.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // XamlRoot is null before the page is loaded and after it is torn down. Setting it
        // on the dialog then throws, and this runs from async void handlers where that
        // throw would take the process down rather than surfacing as a failed save.
        if (_dialogOpen || XamlRoot is null) return ContentDialogResult.None;

        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "حسنًا"
        });
    }

    // ---- duplicates -------------------------------------------------------

    private async void OnDuplicatesDetected(object? sender, IReadOnlyList<DuplicateMatch> matches)
    {
        // The ViewModel raised this instead of saving, so a suppressed prompt simply
        // leaves the invoice unsaved and on screen — nothing is written unconfirmed.
        if (XamlRoot is null) return;

        var body = new StringBuilder();
        body.AppendLine("تبدو هذه الفاتورة مثل واحدة قمت بحفظها مسبقًا:");
        body.AppendLine();

        foreach (var match in matches.Take(5))
            body.AppendLine($"• {match.Reason}");

        if (matches.Count > 5)
            body.AppendLine($"…و{matches.Count - 5} أخرى.");

        body.AppendLine();
        body.Append("هل تريد الحفظ على أي حال؟");

        var dialog = new ContentDialog
        {
            Title = "احتمال وجود فاتورة مكررة",
            Content = new TextBlock { Text = body.ToString(), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "الحفظ على أي حال",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close
        };

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await ViewModel.SaveConfirmedAsync();
    }
}
