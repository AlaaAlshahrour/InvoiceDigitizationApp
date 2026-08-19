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
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
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

    /// <summary>Pointer position where the current drag began, in ImageScroller space.</summary>
    private Point _dragOrigin;

    private bool _isPanning;
    private bool _isMarqueeing;
    private bool _pointerOverImage;

    /// <summary>Scroll offsets when a pan began; every move is measured against these.</summary>
    private double _panOriginHorizontal;
    private double _panOriginVertical;

    /// <summary>
    /// Set while the view's own zoom is being copied into the ViewModel, so the
    /// ViewModel's change notification does not turn around and re-apply it to the view.
    /// </summary>
    private bool _syncingZoomFromView;

    public ProcessingViewModel ViewModel { get; }

    public ProcessingPage()
    {
        ViewModel = App.Services.GetRequiredService<ProcessingViewModel>();
        InitializeComponent();

        ViewModel.DuplicatesDetected += OnDuplicatesDetected;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Attached here rather than in the markup, with handledEventsToo set: a
        // ScrollViewer marks pointer events handled for its own manipulation logic, and a
        // plain XAML event attribute on it never fires for the drags this screen needs.
        ImageScroller.AddHandler(
            PointerPressedEvent, new PointerEventHandler(ImageScroller_PointerPressed), true);
        ImageScroller.AddHandler(
            PointerMovedEvent, new PointerEventHandler(ImageScroller_PointerMoved), true);
        ImageScroller.AddHandler(
            PointerReleasedEvent, new PointerEventHandler(ImageScroller_PointerReleased), true);
        ImageScroller.AddHandler(
            PointerCaptureLostEvent, new PointerEventHandler(ImageScroller_PointerCaptureLost), true);
        ImageScroller.AddHandler(
            PointerEnteredEvent, new PointerEventHandler(ImageScroller_PointerEntered), true);
        ImageScroller.AddHandler(
            PointerExitedEvent, new PointerEventHandler(ImageScroller_PointerExited), true);

        ApplyToolCursor();
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
        // Skipped while the view is the one reporting a zoom it already applied —
        // re-applying it here would drop the offsets that came with it.
        if (e.PropertyName == nameof(ViewModel.ZoomFactor))
        {
            if (!_syncingZoomFromView)
                ImageScroller.ChangeView(null, null, (float)ViewModel.ZoomFactor);
        }

        else if (e.PropertyName == nameof(ViewModel.SelectedImageTool))
            ApplyToolCursor();

        // Follows the ViewModel's choice of rendering, so the toggle and the batch both
        // repaint the pane through one path.
        else if (e.PropertyName == nameof(ViewModel.CurrentImagePath))
            await LoadImageAsync(ViewModel.CurrentImagePath);

        else if (e.PropertyName is nameof(ViewModel.FieldRegions)
                               or nameof(ViewModel.ShowFieldRegions))
            DrawFieldRegions();

        // Redrawn rather than re-styled: the highlight is a brush and stroke difference
        // on rectangles the layer already holds, and rebuilding a few dozen of them is
        // cheaper to reason about than tracking which one was previously selected.
        else if (e.PropertyName == nameof(ViewModel.SelectedRegion))
        {
            DrawFieldRegions();
            ZoomToSelectedRegion();
        }
    }

    // ---- field regions ----------------------------------------------------

    /// <summary>
    /// Rectangles currently on the overlay, keyed by the region they were drawn for, so
    /// the zoom can find the one it needs to bring into view without a visual-tree walk.
    /// </summary>
    private readonly Dictionary<FieldRegion, Rectangle> _regionShapes = new();

    /// <summary>
    /// Repaints the box overlay.
    /// </summary>
    /// <remarks>
    /// Boxes arrive in the corrected page's coordinate space — <c>source.width</c> ×
    /// <c>source.height</c> — and the pane renders that page scaled to fit. One scale
    /// factor per axis converts between them; they are equal under a Uniform stretch, but
    /// computing both keeps this correct if the stretch ever changes.
    ///
    /// The Canvas is sized to the Image's *rendered* size, not to the page's pixel size,
    /// which is why this runs from SizeChanged as well as from the ViewModel: a pane
    /// resize changes the rendered size without changing a single box.
    /// </remarks>
    private void DrawFieldRegions()
    {
        RegionLayer.Children.Clear();
        _regionShapes.Clear();

        var map = ViewModel.FieldRegions;

        if (!ViewModel.ShowFieldRegions || !map.HasRegions) return;

        // ActualWidth is zero until the image has been measured; SizeChanged calls back
        // in once it has, so there is nothing to do on this pass.
        var renderedWidth = InvoiceImage.ActualWidth;
        var renderedHeight = InvoiceImage.ActualHeight;
        if (renderedWidth <= 0 || renderedHeight <= 0) return;

        RegionLayer.Width = renderedWidth;
        RegionLayer.Height = renderedHeight;

        var scaleX = renderedWidth / map.PageWidth;
        var scaleY = renderedHeight / map.PageHeight;

        foreach (var region in map.Regions)
        {
            var selected = ReferenceEquals(region, ViewModel.SelectedRegion);

            var shape = new Rectangle
            {
                Width = Math.Max(1, region.Width * scaleX),
                Height = Math.Max(1, region.Height * scaleY),

                // The selected box is filled as well as outlined; the rest are outlines
                // only, or they would obscure the very text the user is checking.
                Fill = selected
                    ? new SolidColorBrush(Color.FromArgb(56, 47, 111, 228))
                    : new SolidColorBrush(Colors.Transparent),
                Stroke = new SolidColorBrush(selected
                    ? Color.FromArgb(255, 47, 111, 228)
                    : Color.FromArgb(150, 217, 164, 65)),
                StrokeThickness = selected ? 2.5 : 1.2
            };

            ToolTipService.SetToolTip(shape, region.Label);

            Canvas.SetLeft(shape, region.X * scaleX);
            Canvas.SetTop(shape, region.Y * scaleY);

            RegionLayer.Children.Add(shape);
            _regionShapes[region] = shape;
        }
    }

    private void InvoiceImage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DrawFieldRegions();

    // ---- form → image -----------------------------------------------------

    // The field a control stands for travels in its Tag, parsed back to the enum here.
    // A Tag rather than a handler per field: nine fields would otherwise be nine
    // near-identical methods, and the grid's cells are inside a DataTemplate where each
    // one would need the row looked up anyway.

    private void HeaderField_Focused(object sender, RoutedEventArgs e) =>
        SelectRegionFrom(sender, rowIndex: -1);

    private void HeaderField_Tapped(object sender, TappedRoutedEventArgs e) =>
        SelectRegionFrom(sender, rowIndex: -1);

    /// <summary>
    /// Selects the region for a cell inside the items grid, resolving which line it
    /// belongs to from the row ViewModel the template is bound to.
    /// </summary>
    private void RowField_Focused(object sender, RoutedEventArgs e) =>
        SelectRegionFrom(sender, RowIndexOf(sender));

    private void RowField_Tapped(object sender, TappedRoutedEventArgs e) =>
        SelectRegionFrom(sender, RowIndexOf(sender));

    private void SelectRegionFrom(object sender, int rowIndex)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<FieldKind>(tag, out var kind)) return;

        // Only meaningful for a row cell that could not be tied back to a line; a header
        // field passes -1 deliberately.
        if (rowIndex == int.MinValue) return;

        ViewModel.SelectRegion(kind, rowIndex);
    }

    /// <summary>
    /// The index of the line item a templated cell belongs to, or
    /// <see cref="int.MinValue"/> when it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Taken from the row ViewModel's position in the Items collection rather than from
    /// the DataGrid's own row index: the grid's index follows its display order, and the
    /// boxes are keyed by the order the service reported the products in. They agree
    /// today — nothing sorts this grid — but the collection is the one that is guaranteed
    /// to, because it is the same list ApplyInvoice built the regions alongside.
    /// </remarks>
    private int RowIndexOf(object sender)
    {
        if (sender is not FrameworkElement { DataContext: InvoiceItemRowViewModel row })
            return int.MinValue;

        var index = ViewModel.Items.IndexOf(row);
        return index >= 0 ? index : int.MinValue;
    }

    /// <summary>
    /// Zooms and scrolls the pane so the selected box fits inside it, with a margin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The box's position is taken from the overlay rectangle rather than recomputed from
    /// the page coordinates: the rectangle has already been through the same scaling the
    /// image was, so reading it back cannot drift from what the user can see.
    /// </para>
    /// <para>
    /// The rectangle's position is read from the Canvas attached properties it was drawn
    /// with, then offset by where the image sits inside the scroller's content. Both are
    /// *unzoomed* content coordinates — the ScrollViewer's zoom is applied above them, so
    /// neither is affected by the current zoom factor, and the conversion to a scroll
    /// offset is one multiply by the target zoom. Reading the position back through
    /// <c>TransformToVisual</c> instead would have folded the live zoom in and needed it
    /// divided straight back out.
    /// </para>
    /// <para>
    /// The image is centred in its cell, so when the content is narrower than the viewport
    /// there is a gap to its left that the box's own coordinates know nothing about. That
    /// gap is exactly what <c>ImageSurface</c>-relative positioning accounts for.
    /// </para>
    /// </remarks>
    private void ZoomToSelectedRegion()
    {
        if (ViewModel.SelectedRegion is not { } region) return;
        if (!_regionShapes.TryGetValue(region, out var shape)) return;

        var viewportWidth = ImageScroller.ViewportWidth;
        var viewportHeight = ImageScroller.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        var boxWidth = shape.Width;
        var boxHeight = shape.Height;
        if (boxWidth <= 0 || boxHeight <= 0) return;

        // A quarter of the viewport left over, so the box sits in context rather than
        // filling the pane edge to edge — the surrounding text is usually what tells the
        // user whether the box is on the right cell.
        const double Fill = 0.75;

        var target = Math.Clamp(
            Math.Min(viewportWidth * Fill / boxWidth, viewportHeight * Fill / boxHeight),
            ImageScroller.MinZoomFactor,
            ImageScroller.MaxZoomFactor);

        // Unzoomed content coordinates: where the box's centre sits on the laid-out
        // surface. RegionLayer and InvoiceImage share a cell in ImageSurface and are the
        // same size, so a Canvas coordinate is already an ImageSurface coordinate.
        var centreX = Canvas.GetLeft(shape) + boxWidth / 2;
        var centreY = Canvas.GetTop(shape) + boxHeight / 2;

        // ImageSurface is centred inside the scroller's content when the image is smaller
        // than the pane; that leading gap is part of the offset and is not in the box's
        // own coordinates.
        if (ImageScroller.Content is UIElement content && content != ImageSurface)
        {
            var origin = ImageSurface
                .TransformToVisual(content)
                .TransformPoint(new Point(0, 0));

            centreX += origin.X;
            centreY += origin.Y;
        }

        // Scroll offsets are in zoomed pixels; the coordinates above are not.
        ImageScroller.ChangeView(
            centreX * target - viewportWidth / 2,
            centreY * target - viewportHeight / 2,
            (float)target,
            disableAnimation: false);
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

    // ---- tool picker ------------------------------------------------------

    public bool IsPanTool(InvoiceImageTool tool) => tool == InvoiceImageTool.Pan;

    public bool IsMarqueeTool(InvoiceImageTool tool) => tool == InvoiceImageTool.Marquee;

    private void PanTool_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedImageTool = InvoiceImageTool.Pan;

    private void MarqueeTool_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedImageTool = InvoiceImageTool.Marquee;

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

    // ---- image navigation -------------------------------------------------

    /// <summary>
    /// Starts a drag: a pan with the hand tool or the middle button, a marquee otherwise.
    /// </summary>
    /// <remarks>
    /// A ScrollViewer pans on touch, on the wheel and by its scrollbars, but a mouse drag
    /// does nothing at all — which on a zoomed-in invoice leaves the scrollbars as the
    /// only way to reach the rest of the page. These handlers are that missing gesture.
    /// </remarks>
    private void ImageScroller_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (InvoiceImage.Source is null) return;

        // Touch is left to the ScrollViewer's own manipulation, which already pans and
        // pinch-zooms. A touch contact reports itself as a left button press, so without
        // this guard both would run and the image would travel twice as far as the finger.
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;

        var point = e.GetCurrentPoint(ImageScroller);
        var properties = point.Properties;

        // Middle-drag pans under either tool: the marquee would otherwise have no way to
        // move the view without switching back to the hand first.
        var pan = properties.IsMiddleButtonPressed
                  || (properties.IsLeftButtonPressed
                      && ViewModel.SelectedImageTool == InvoiceImageTool.Pan);

        var marquee = properties.IsLeftButtonPressed
                      && ViewModel.SelectedImageTool == InvoiceImageTool.Marquee;

        if (!pan && !marquee) return;

        _dragOrigin = point.Position;

        if (pan)
        {
            _isPanning = true;
            _panOriginHorizontal = ImageScroller.HorizontalOffset;
            _panOriginVertical = ImageScroller.VerticalOffset;
        }
        else
        {
            _isMarqueeing = true;
            DrawMarquee(_dragOrigin, _dragOrigin);
            MarqueeRect.Visibility = Visibility.Visible;
        }

        ImageScroller.CapturePointer(e.Pointer);
        ApplyToolCursor();
        e.Handled = true;
    }

    private void ImageScroller_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning && !_isMarqueeing) return;

        var position = e.GetCurrentPoint(ImageScroller).Position;

        if (_isPanning)
        {
            // The image follows the pointer, so the viewport travels the opposite way.
            // Out-of-range offsets are clamped by ChangeView, which is what stops the pan
            // at the edges of the image.
            ImageScroller.ChangeView(
                _panOriginHorizontal - (position.X - _dragOrigin.X),
                _panOriginVertical - (position.Y - _dragOrigin.Y),
                null,
                disableAnimation: true);
        }
        else
        {
            DrawMarquee(_dragOrigin, ClampToScroller(position));
        }

        e.Handled = true;
    }

    private void ImageScroller_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning && !_isMarqueeing) return;

        if (_isMarqueeing)
            ZoomToMarquee(ClampToScroller(e.GetCurrentPoint(ImageScroller).Position));

        ImageScroller.ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    /// <summary>
    /// Ends a drag the pointer was taken away from — another window, a touch cancelled by
    /// the system. The marquee is abandoned rather than applied: no release was seen, so
    /// there is no rectangle the user actually chose.
    /// </summary>
    private void ImageScroller_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndDrag();

    private void EndDrag()
    {
        _isPanning = false;
        _isMarqueeing = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        ApplyToolCursor();
    }

    /// <summary>
    /// Keeps a dragged point inside the pane. The pointer is captured, so it reports
    /// positions well outside the scroller once the drag leaves it — and the overlay
    /// Canvas does not clip, so an unclamped rectangle would be drawn across the form.
    /// </summary>
    private Point ClampToScroller(Point point) => new(
        Math.Clamp(point.X, 0, ImageScroller.ActualWidth),
        Math.Clamp(point.Y, 0, ImageScroller.ActualHeight));

    private void DrawMarquee(Point from, Point to)
    {
        Canvas.SetLeft(MarqueeRect, Math.Min(from.X, to.X));
        Canvas.SetTop(MarqueeRect, Math.Min(from.Y, to.Y));
        MarqueeRect.Width = Math.Abs(to.X - from.X);
        MarqueeRect.Height = Math.Abs(to.Y - from.Y);
    }

    /// <summary>
    /// Zooms the pane so the dragged rectangle fills it.
    /// </summary>
    private void ZoomToMarquee(Point end)
    {
        var width = Math.Abs(end.X - _dragOrigin.X);
        var height = Math.Abs(end.Y - _dragOrigin.Y);

        // A click, or a hand that slipped during one, is not a selection.
        if (width < 16 || height < 16) return;

        var viewportWidth = ImageScroller.ViewportWidth;
        var viewportHeight = ImageScroller.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        var current = ImageScroller.ZoomFactor;
        var target = Math.Clamp(
            current * Math.Min(viewportWidth / width, viewportHeight / height),
            ImageScroller.MinZoomFactor,
            ImageScroller.MaxZoomFactor);

        // Scroll offsets are measured in zoomed content pixels, so the selection's corner
        // moves by exactly the ratio between the old zoom and the new one.
        var scale = target / current;
        var left = Math.Min(_dragOrigin.X, end.X);
        var top = Math.Min(_dragOrigin.Y, end.Y);

        ImageScroller.ChangeView(
            (ImageScroller.HorizontalOffset + left) * scale,
            (ImageScroller.VerticalOffset + top) * scale,
            (float)target,
            disableAnimation: false);
    }

    /// <summary>
    /// Copies a zoom the view applied on its own back into the ViewModel, so the
    /// percentage label stays honest after a marquee, a ctrl+wheel or a pinch — none of
    /// which pass through the zoom commands.
    /// </summary>
    private void ImageScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Math.Abs(ImageScroller.ZoomFactor - ViewModel.ZoomFactor) < 0.0001) return;

        _syncingZoomFromView = true;
        try
        {
            ViewModel.ZoomFactor = ImageScroller.ZoomFactor;
        }
        finally
        {
            _syncingZoomFromView = false;
        }
    }

    private void ImageScroller_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverImage = true;
        ApplyToolCursor();
    }

    private void ImageScroller_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverImage = false;
        ApplyToolCursor();
    }

    /// <summary>
    /// Names the active tool in the pointer while it is over the image pane.
    /// </summary>
    /// <remarks>
    /// The cursor is assigned to the page, not to the scroller. ScrollViewer is sealed and
    /// <c>UIElement.ProtectedCursor</c> is protected, so only an element's own class can
    /// set its cursor, and the page is the nearest ancestor this screen owns — hence the
    /// hover tracking that restores the arrow on the way out. Controls that set a cursor
    /// of their own, a text box for one, still win: they sit deeper in the tree.
    /// </remarks>
    private void ApplyToolCursor()
    {
        var shape = (_isPanning, _isMarqueeing, _pointerOverImage) switch
        {
            (true, _, _) => InputSystemCursorShape.SizeAll,
            (_, true, _) => InputSystemCursorShape.Cross,
            (_, _, true) => ViewModel.SelectedImageTool == InvoiceImageTool.Marquee
                ? InputSystemCursorShape.Cross
                : InputSystemCursorShape.Hand,
            _ => InputSystemCursorShape.Arrow
        };

        ProtectedCursor = InputSystemCursor.Create(shape);
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
