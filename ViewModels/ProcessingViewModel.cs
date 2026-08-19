using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Batch;
using InvoiceDigitizationApp.Services.Data;
using InvoiceDigitizationApp.Services.Export;
using InvoiceDigitizationApp.Services.Pipeline;
using InvoiceDigitizationApp.Services.Validation;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>Which rendering of the invoice the image pane is showing.</summary>
public enum InvoiceImageView
{
    /// <summary>The imported copy of the user's own file.</summary>
    Original,

    /// <summary>The service's enhanced grayscale rendering, before binarization.</summary>
    Enhanced,

    /// <summary>The exact binarized image the OCR engine read.</summary>
    OcrInput
}

/// <summary>What a mouse drag inside the image pane does.</summary>
public enum InvoiceImageTool
{
    /// <summary>Drag anywhere to move the image under the pointer — the hand tool.</summary>
    Pan,

    /// <summary>Drag a rectangle over a region to zoom the pane into it.</summary>
    Marquee
}

/// <summary>
/// The side-by-side verification screen: original image on one side, editable extracted
/// data on the other. This is the core of the product — everything OCR produces passes
/// through here for human confirmation before it is saved.
/// </summary>
public partial class ProcessingViewModel : ViewModelBase
{
    private readonly IAiServiceClient _aiService;
    private readonly IInvoiceRepository _invoices;
    private readonly IProductRepository _products;
    private readonly ICustomerRepository _customers;
    private readonly ISettingsRepository _settings;
    private readonly IExportService _export;
    private readonly IInvoiceValidationService _validation;
    private readonly IDuplicateDetectionService _duplicates;
    private readonly IInvoiceBatchService _batch;
    private readonly IPipelineConfigurationStore _pipeline;
    private readonly IExtractionWarningBuilder _warnings;

    private int _editingInvoiceId;
    private string? _contentHash;

    /// <summary>
    /// How many ranked alternatives each picker offers. Read once per screen from
    /// settings rather than per row, which would be a database hit per line item.
    /// </summary>
    private int _maxCandidates = MatchChoiceBuilder.DefaultTopK;

    /// <summary>
    /// Paths of the preprocessed renderings for the invoice currently on screen, saved
    /// alongside it so the record keeps a copy of what the OCR engine actually read.
    /// </summary>
    private string? _enhancedImagePath;
    private string? _ocrImagePath;

    /// <summary>
    /// Set while a whole invoice is being loaded into the form, so the per-row edit
    /// handler does not run validation once for every row being populated.
    /// </summary>
    private bool _isApplyingInvoice;

    public ProcessingViewModel(
        IAiServiceClient aiService,
        IInvoiceRepository invoices,
        IProductRepository products,
        ICustomerRepository customers,
        ISettingsRepository settings,
        IExportService export,
        IInvoiceValidationService validation,
        IDuplicateDetectionService duplicates,
        IInvoiceBatchService batch,
        IPipelineConfigurationStore pipeline,
        IExtractionWarningBuilder warnings)
    {
        _pipeline = pipeline;
        _warnings = warnings;
        _aiService = aiService;
        _invoices = invoices;
        _products = products;
        _customers = customers;
        _settings = settings;
        _export = export;
        _validation = validation;
        _duplicates = duplicates;
        _batch = batch;

        Items.CollectionChanged += OnItemsCollectionChanged;
        _batch.Changed += OnBatchChanged;
    }

    /// <summary>
    /// Unsubscribes from the batch service. Called from the page's OnNavigatedFrom: the
    /// service is a singleton and would otherwise hold every ViewModel the user has ever
    /// navigated to alive for the life of the process.
    /// </summary>
    public void Detach()
    {
        _batch.Changed -= OnBatchChanged;
    }

    // ---- header fields ----------------------------------------------------

    /// <summary>
    /// The name written to Invoices.MerchantName. Mirrors the selected contact rather
    /// than being typed: an invoice always belongs to a row of the Customers table.
    /// </summary>
    [ObservableProperty] private string _merchantName = string.Empty;

    [ObservableProperty] private string? _invoiceNumber;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private DateTimeOffset? _invoiceDate;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private string _selectedInvoiceType = nameof(InvoiceType.Purchase);
    [ObservableProperty] private Customer? _selectedCustomer;

    /// <summary>
    /// What the counterparty picker is set to. Separate from
    /// <see cref="SelectedCustomer"/> because a picker entry carries a similarity score
    /// alongside the contact, and the score is display-only.
    /// </summary>
    [ObservableProperty] private CustomerChoice? _selectedCustomerChoice;

    partial void OnSelectedCustomerChoiceChanged(CustomerChoice? value)
    {
        if (value is not null) SelectedCustomer = value.Customer;
    }

    // ---- image ------------------------------------------------------------

    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private double _zoomFactor = 1.0;

    /// <summary>
    /// Which rendering the image pane shows. Defaults to the enhanced grayscale: it is
    /// the most legible of the three on a poorly-lit phone photo, which is what most
    /// imports are, and it is available for any source the service accepts.
    /// </summary>
    [ObservableProperty] private InvoiceImageView _selectedImageView = InvoiceImageView.Enhanced;

    /// <summary>
    /// What dragging inside the image pane does. Panning is the default because it is the
    /// only way to reach the rest of a zoomed-in invoice with a mouse — a ScrollViewer
    /// pans on touch and on the wheel, but ignores a mouse drag entirely.
    /// </summary>
    [ObservableProperty] private InvoiceImageTool _selectedImageTool = InvoiceImageTool.Pan;

    // Stored rather than computed. A File.Exists on every binding evaluation would hit
    // the disk during layout; these are refreshed explicitly whenever the invoice changes.
    [ObservableProperty] private bool _hasOriginalImage;
    [ObservableProperty] private bool _hasEnhancedImage;
    [ObservableProperty] private bool _hasOcrImage;

    /// <summary>True when at least one of the three renderings exists on disk.</summary>
    public bool HasAnyViewableImage => HasOriginalImage || HasEnhancedImage || HasOcrImage;

    /// <summary>The file backing <see cref="SelectedImageView"/>, or null if absent.</summary>
    public string? CurrentImagePath => SelectedImageView switch
    {
        InvoiceImageView.Enhanced => HasEnhancedImage ? _enhancedImagePath : null,
        InvoiceImageView.OcrInput => HasOcrImage ? _ocrImagePath : null,
        _ => HasOriginalImage ? ImagePath : null
    };

    // ---- field regions ----------------------------------------------------

    /// <summary>
    /// Every bounding box the current extraction reported, in the corrected page's
    /// coordinate space. <see cref="FieldRegionMap.Empty"/> for a hand-typed or
    /// previously-saved invoice, which has no extraction behind it and so nothing to draw.
    /// </summary>
    /// <remarks>
    /// Boxes are not persisted with the invoice — they describe one reading of one image,
    /// not the record — so reopening a saved invoice shows the image without an overlay.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFieldRegions))]
    private FieldRegionMap _fieldRegions = FieldRegionMap.Empty;

    public bool HasFieldRegions => FieldRegions.HasRegions;

    /// <summary>
    /// The cell the user last clicked in the form, or null. The overlay highlights this
    /// box and the image pane zooms to fit it.
    /// </summary>
    [ObservableProperty] private FieldRegion? _selectedRegion;

    /// <summary>
    /// Whether the boxes are drawn at all. On by default: seeing what the engine read
    /// each value from is the fastest way to check it, which is the whole job of this
    /// screen.
    /// </summary>
    [ObservableProperty] private bool _showFieldRegions = true;

    /// <summary>
    /// Selects the region for a cell, if the extraction reported one. Called from the
    /// View when a field or a grid cell is clicked; a field the service placed no box on
    /// simply clears the selection rather than leaving the previous box highlighted, which
    /// would point the user at the wrong cell.
    /// </summary>
    public void SelectRegion(FieldKind kind, int rowIndex = -1)
    {
        SelectedRegion = FieldRegions.Regions
            .FirstOrDefault(region => region.Matches(kind, rowIndex));
    }

    /// <summary>Clears the highlight, leaving the boxes drawn.</summary>
    public void ClearRegionSelection() => SelectedRegion = null;

    // ---- pane layout ------------------------------------------------------

    [ObservableProperty] private bool _isFormPaneVisible = true;
    [ObservableProperty] private bool _isImagePaneVisible = true;

    // ---- state ------------------------------------------------------------

    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private string? _rawOcrText;

    public ObservableCollection<InvoiceItemRowViewModel> Items { get; } = new();
    public ObservableCollection<string> ValidationMessages { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Product> Products { get; } = new();

    /// <summary>
    /// The counterparty picker's entries: the extraction's ranked suggestions first,
    /// each with its similarity score, then the rest of the Customers table.
    /// </summary>
    public ObservableCollection<CustomerChoice> CustomerChoices { get; } = new();

    /// <summary>
    /// True when the service could not match the merchant confidently, so the
    /// pre-selected contact is a guess the user is being asked to confirm.
    /// </summary>
    [ObservableProperty] private bool _merchantNeedsReview;

    public string MerchantReviewText =>
        $"لم يُطابَق اسم التاجر '{MerchantName}' بثقة كافية. اختر السجل الصحيح من القائمة.";

    public IReadOnlyList<string> InvoiceTypeOptions { get; } =
        new[] { nameof(InvoiceType.Purchase), nameof(InvoiceType.Sale) };

    // ---- counterparty naming ----------------------------------------------

    /// <summary>
    /// What the other party to this invoice is called. A sale is made to a customer, a
    /// purchase is made from a supplier — the same Customers row, named for its role.
    /// </summary>
    public string CounterpartyLabel =>
        SelectedInvoiceType == nameof(InvoiceType.Sale) ? "العميل" : "المورد";

    public string CounterpartyPlaceholder =>
        SelectedInvoiceType == nameof(InvoiceType.Sale)
            ? "اختر العميل"
            : "اختر المورد";

    /// <summary>Sum of the line items, shown in the footer and recomputed on every edit.</summary>
    public decimal ComputedTotal => Items.Sum(i => i.TotalPrice);

    /// <summary>True when the line items disagree with the header total.</summary>
    public bool TotalsDisagree =>
        Items.Count > 0 && Math.Abs(ComputedTotal - TotalAmount) > 0.05m;

    public string TotalComparisonText => TotalsDisagree
        ? $"مجموع البنود {ComputedTotal:N2} لكن الفاتورة تشير إلى {TotalAmount:N2}"
        : $"الإجمالي: {ComputedTotal:N2}";

    public bool HasValidationMessages => ValidationMessages.Count > 0;

    /// <summary>
    /// The validation banner's title. It carries the count because the list under it is
    /// height-capped and scrolls: without a total, a long run of warnings looks like
    /// however many happen to fit.
    /// </summary>
    public string ValidationSummaryTitle => ValidationMessages.Count switch
    {
        0 or 1 => "تحقق من هذه العناصر قبل الحفظ",
        _ => $"تحقق من هذه العناصر قبل الحفظ ({ValidationMessages.Count})"
    };

    /// <summary>
    /// Raises everything the validation banner reads. Its visibility and the count in its
    /// title come from one collection and always move together.
    /// </summary>
    private void RefreshValidationSummary()
    {
        OnPropertyChanged(nameof(HasValidationMessages));
        OnPropertyChanged(nameof(ValidationSummaryTitle));
    }

    public int InvalidRowCount => Items.Count(i => !i.IsArithmeticValid);

    /// <summary>Rows still naming a product that is not in the catalog.</summary>
    public int UnlinkedRowCount => Items.Count(i => !i.IsProductLinked);

    public bool HasUnlinkedRows => UnlinkedRowCount > 0;

    public string UnlinkedRowsText =>
        $"{UnlinkedRowCount} بند غير مرتبط بمنتج مسجّل. اختر منتجًا من القائمة أو أضِف المنتج إلى قائمة المنتجات.";

    /// <summary>True once a contact is chosen; saving is blocked until then.</summary>
    public bool IsCustomerLinked => SelectedCustomer is not null;

    // ---- batch ------------------------------------------------------------
    // Read-through projections of the batch singleton. The View binds to these in a
    // later phase; they are kept in step by the Changed subscription below.

    public bool IsBatchActive => _batch.IsActive;
    public bool IsBatchProcessing => _batch.IsProcessing;
    public string BatchPositionLabel => _batch.PositionLabel;
    public int BatchTotalCount => _batch.TotalCount;
    public int BatchReviewedCount => _batch.ReviewedCount;

    private void OnBatchChanged(object? sender, EventArgs e) => RefreshBatchState();

    private void RefreshBatchState()
    {
        OnPropertyChanged(nameof(IsBatchActive));
        OnPropertyChanged(nameof(IsBatchProcessing));
        OnPropertyChanged(nameof(BatchPositionLabel));
        OnPropertyChanged(nameof(BatchTotalCount));
        OnPropertyChanged(nameof(BatchReviewedCount));

        NextBatchItemCommand.NotifyCanExecuteChanged();
        PreviousBatchItemCommand.NotifyCanExecuteChanged();
        SkipBatchItemCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when duplicates are found on save; the View shows the dialog.</summary>
    public event EventHandler<IReadOnlyList<DuplicateMatch>>? DuplicatesDetected;

    /// <summary>Raised after a successful save, carrying the invoice id.</summary>
    public event EventHandler<int>? InvoiceSaved;

    // ---- loading ----------------------------------------------------------

    public async Task InitializeAsync()
    {
        await RunGuardedAsync(async () =>
        {
            await LoadCatalogsAsync();
        }, "تعذّر تحميل البيانات المرجعية");
    }

    /// <summary>
    /// Refreshes the pickers' backing catalogs. Always called before rows are built, so
    /// a row's product list is never emptied out from under an open selection.
    /// </summary>
    private async Task LoadCatalogsAsync()
    {
        var customers = await _customers.GetAllAsync();
        Customers.Clear();
        foreach (var customer in customers) Customers.Add(customer);

        var products = await _products.GetAllAsync();
        Products.Clear();
        foreach (var product in products) Products.Add(product);

        _maxCandidates = (int)await _settings.GetDoubleAsync(
            SettingKeys.MaxMatchCandidates, MatchChoiceBuilder.DefaultTopK);

        // Until an extraction supplies ranked results, the picker is simply the
        // catalog in its own order.
        RebuildCustomerChoices(results: null);
    }

    /// <summary>
    /// Rebuilds the counterparty picker so the extraction's suggestions sit at the top.
    /// </summary>
    private void RebuildCustomerChoices(IReadOnlyList<MatchResult>? results)
    {
        var previous = SelectedCustomer;

        CustomerChoices.Clear();
        foreach (var choice in MatchChoiceBuilder.ForCustomers(Customers, results))
        {
            CustomerChoices.Add(choice);
        }

        // Re-point the picker at whatever was selected, since the entries it held are
        // gone. Assigning the field would skip the sync back to SelectedCustomer, which
        // is exactly what is wanted here: the contact has not changed.
        SelectedCustomerChoice = previous is null
            ? null
            : CustomerChoices.FirstOrDefault(c => c.Customer.CustomerId == previous.CustomerId);
    }

    /// <summary>Loads an existing invoice from the database for review or editing.</summary>
    public async Task LoadInvoiceAsync(int invoiceId)
    {
        await RunGuardedAsync(async () =>
        {
            await LoadCatalogsAsync();

            var invoice = await _invoices.GetByIdAsync(invoiceId);
            if (invoice is null)
            {
                SetError($"لم يتم العثور على السجل رقم {invoiceId}.");
                return;
            }

            ApplyInvoice(invoice);
            _editingInvoiceId = invoice.InvoiceId;
            _contentHash = invoice.ContentHash;
            HasUnsavedChanges = false;

            SetStatus($"تم تحميل السجل رقم {invoiceId}.");
        }, "تعذّر تحميل الفاتورة");
    }

    /// <summary>
    /// Sends a file to the AI service and populates the form from the result. The image
    /// is displayed regardless of whether extraction succeeds, so the user can always
    /// fall back to typing the invoice in by hand.
    /// </summary>
    public async Task ProcessFileAsync(string filePath, CancellationToken ct = default) =>
        await RunGuardedAsync(() => ProcessFileCoreAsync(filePath, ct), "فشلت المعالجة");

    /// <summary>
    /// The body of <see cref="ProcessFileAsync"/> without the busy guard, so batch entry
    /// points — which are guarded once at their own top level — can reuse it. A guarded
    /// method calling another guarded method would deadlock on the IsBusy flag.
    /// </summary>
    private async Task ProcessFileCoreAsync(string filePath, CancellationToken ct = default)
    {
        await LoadCatalogsAsync();

        ImagePath = filePath;
        _editingInvoiceId = 0;
        _enhancedImagePath = null;
        _ocrImagePath = null;
        _contentHash = await _duplicates.ComputeFileHashAsync(filePath, ct);

        var options = BuildExtractionOptions();
        var configuration = await _pipeline.GetAsync(ct);

        SetStatus("جارٍ استخراج بيانات الفاتورة…");

        ExtractionResult result;
        try
        {
            result = await _aiService.ExtractAsync(filePath, options, configuration, ct);
        }
        catch (AiServiceException ex)
        {
            // Keep the image loaded so manual entry remains possible.
            SetError(ex.Code == AiServiceException.TransportError
                ? $"{ex.Message} لا يزال بإمكانك إدخال هذه الفاتورة يدويًا."
                : $"فشل الاستخراج ({ex.Code}): {ex.Message}");

            RefreshImageAvailability();
            return;
        }

        var invoice = ExtractionMapper.ToInvoice(
            result,
            Enum.TryParse<InvoiceType>(SelectedInvoiceType, out var type)
                ? type : InvoiceType.Purchase);

        invoice.ImagePath = filePath;
        invoice.ContentHash = _contentHash;

        // A freshly extracted invoice carries no stored city snapshot, so the city
        // follows whichever contact the merchant resolves to. ApplyInvoice assigns City
        // after ResolveCustomer, and a null here is what lets the contact's value stand.
        invoice.City = null;

        ApplyInvoice(invoice, result);

        // Assembled from the fields' own readings: the service no longer sends a raw OCR
        // dump, and a reading attributed to the field it came from is more use than one.
        RawOcrText = result.DetectedText();
        HasUnsavedChanges = true;

        Validate();
        ReportExtraction(result, invoice);
    }

    /// <summary>
    /// Builds the options sent with every extraction. Shared by the single-file path and
    /// the batch, which must send exactly the same catalogs or the two would match
    /// merchants and products differently.
    /// </summary>
    /// <remarks>
    /// The pipeline configuration is deliberately not here: it travels as its own part of
    /// the request, fetched separately by each caller, because it comes from the settings
    /// page rather than from this screen's catalogs.
    /// </remarks>
    private ExtractionOptions BuildExtractionOptions() => new()
    {
        // Ask for the preprocessed renderings so they can be persisted alongside the
        // original and shown when the original is hard to read.
        ReturnDebugImages = true,

        MaxCandidates = _maxCandidates,

        // Each contact travels as one record carrying every name it answers to. Name and
        // AliasName are equivalent match targets — invoices are printed with whichever
        // the merchant happens to use — and the service replies with the CustomerId
        // either way, so the match resolves to a record rather than to a loose string.
        KnownMerchants = Customers
            .Select(c => new KnownMerchant
            {
                CustomerId = c.CustomerId,
                Name = c.Name,
                Aliases = string.IsNullOrWhiteSpace(c.AliasName)
                          || string.Equals(c.AliasName, c.Name, StringComparison.OrdinalIgnoreCase)
                    ? new List<string>()
                    : new List<string> { c.AliasName!.Trim() }
            })
            .ToList(),

        KnownProducts = Products
            .Select(p => new KnownProduct { ProductId = p.ProductId, Name = p.Name })
            .ToList(),

        // There is no Cities table: the places this installation deals with are exactly
        // the distinct Customers.City values, and matching against those beats matching
        // against a generic gazetteer of every city in the region.
        KnownCities = Customers
            .Select(c => c.City)
            .Where(city => !string.IsNullOrWhiteSpace(city))
            .Select(city => city!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(city => new KnownCity { Name = city })
            .ToList()
    };

    // ---- batch ------------------------------------------------------------

    /// <summary>
    /// Runs an imported set of files through the service, then shows the first one for
    /// review. A single-file import comes through here too, as a batch of one, so there
    /// is only one processing path to reason about.
    /// </summary>
    public async Task StartBatchAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return;

        await RunGuardedAsync(async () =>
        {
            await LoadCatalogsAsync();

            // Guards the status line against Progress<T>'s asynchrony. Progress posts
            // each report to the UI thread through the synchronization context, so a
            // report queued for the last file can still be delivered *after* the batch
            // has finished and ShowBatchItemCoreAsync has written the real status —
            // overwriting "extracted 7 items, review and save" with a stale
            // "processing 1 of 3…" that then sat there looking like a hang.
            var reportProgress = true;

            var progress = new Progress<BatchProgress>(p =>
            {
                if (!reportProgress) return;
                SetStatus($"جارٍ معالجة {p.CompletedCount} من {p.TotalCount}… ({p.CurrentFileName})");
            });

            try
            {
                await _batch.StartAsync(
                    paths,
                    BuildExtractionOptions(),
                    await _pipeline.GetAsync(ct),
                    progress,
                    ct);
            }
            finally
            {
                reportProgress = false;
            }

            if (_batch.TotalCount == 0)
            {
                SetStatus("لم يتم استيراد أي ملفات.");
                return;
            }

            // Last, so the item actually on screen has the final word on the status
            // line. It reports its own success or its own failure; the batch-wide
            // summary below only speaks when it has something to add that the current
            // item does not already say.
            await ShowBatchItemCoreAsync();

            var failed = _batch.Items.Count(i => i.State == BatchItemState.Failed);

            // Only when the file being shown is *not* itself the failure. Reporting
            // "1 of 1 failed" over a successfully extracted invoice is what made a
            // working extraction look broken: the user saw the red banner, and the form
            // underneath it was correctly filled in the whole time. ShowBatchItemCoreAsync
            // has already set an error for a current item that did fail.
            if (failed > 0 && _batch.Current?.State != BatchItemState.Failed)
            {
                SetError(
                    $"تمت معالجة {_batch.TotalCount} ملف، وفشل {failed} منها. " +
                    "الفاتورة المعروضة سليمة — استخدم 'التالي' لمراجعة الباقي.");
            }
        }, "فشلت معالجة الدفعة");
    }

    /// <summary>
    /// Re-displays the batch item the user was on. Called when the verification screen is
    /// navigated to with no parameter — the Frame does not cache pages, so returning from
    /// another screen rebuilds this one and it has to pick the review back up.
    /// </summary>
    public async Task ResumeBatchAsync()
    {
        if (!_batch.IsActive) return;

        await RunGuardedAsync(async () =>
        {
            await LoadCatalogsAsync();
            await ShowBatchItemCoreAsync();
        }, "تعذّر استئناف الدفعة");
    }

    /// <summary>
    /// Loads <see cref="IInvoiceBatchService.Current"/> into the form. Unguarded: every
    /// caller is already inside a guard, or is itself running under one.
    /// </summary>
    private async Task ShowBatchItemCoreAsync()
    {
        // A finished item means the cursor has nowhere left to go — the last one was just
        // saved or skipped and MoveNext found no successor.
        if (_batch.Current is not { IsFinished: false } item)
        {
            ClearForm();
            SetStatus("اكتملت مراجعة كل فواتير الدفعة.");
            RefreshBatchState();
            return;
        }

        _editingInvoiceId = 0;
        ImagePath = item.SourcePath;
        _contentHash = item.ContentHash;
        _enhancedImagePath = item.EnhancedImagePath;
        _ocrImagePath = item.OcrImagePath;

        if (item.State == BatchItemState.Failed || item.Result is null)
        {
            // Nothing was extracted, but the image is still on screen and the form is
            // still editable, so the invoice can be keyed in by hand.
            ApplyInvoice(new Invoice
            {
                InvoiceType = Enum.TryParse<InvoiceType>(SelectedInvoiceType, out var fallbackType)
                    ? fallbackType : InvoiceType.Purchase,
                ImagePath = item.SourcePath,
                ContentHash = item.ContentHash,
                EnhancedImagePath = item.EnhancedImagePath,
                OcrImagePath = item.OcrImagePath
            });

            RawOcrText = null;
            HasUnsavedChanges = false;

            SetError(item.ErrorMessage ?? "فشل استخراج هذا الملف. يمكنك إدخاله يدويًا.");
            RefreshBatchState();
            return;
        }

        var invoice = ExtractionMapper.ToInvoice(
            item.Result,
            Enum.TryParse<InvoiceType>(SelectedInvoiceType, out var type)
                ? type : InvoiceType.Purchase);

        invoice.ImagePath = item.SourcePath;
        invoice.ContentHash = item.ContentHash;
        invoice.EnhancedImagePath = item.EnhancedImagePath;
        invoice.OcrImagePath = item.OcrImagePath;

        // As in ProcessFileCoreAsync: no stored snapshot yet, so the city follows the
        // contact the merchant resolves to.
        invoice.City = null;

        ApplyInvoice(invoice, item.Result);

        RawOcrText = item.Result.DetectedText();
        HasUnsavedChanges = true;

        Validate();
        ReportExtraction(item.Result, invoice, $"الفاتورة {_batch.PositionLabel} — ");

        RefreshBatchState();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Empties the form once there is nothing left to review, so the last saved invoice
    /// does not sit there looking like it still needs attention.
    /// </summary>
    private void ClearForm()
    {
        _isApplyingInvoice = true;
        try
        {
            _editingInvoiceId = 0;
            _contentHash = null;
            _enhancedImagePath = null;
            _ocrImagePath = null;

            MerchantName = string.Empty;
            InvoiceNumber = null;
            City = null;
            InvoiceDate = null;
            TotalAmount = 0m;
            SelectedCustomer = null;
            SelectedCustomerChoice = null;
            MerchantNeedsReview = false;
            ImagePath = null;
            RawOcrText = null;

            Items.Clear();
            ValidationMessages.Clear();

            FieldRegions = FieldRegionMap.Empty;
            SelectedRegion = null;
        }
        finally
        {
            _isApplyingInvoice = false;
        }

        HasUnsavedChanges = false;

        RefreshValidationSummary();
        RefreshTotals();
        RefreshImageAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanMoveToNextBatchItem))]
    private async Task NextBatchItemAsync() =>
        await RunGuardedAsync(async () =>
        {
            if (_batch.MoveNext()) await ShowBatchItemCoreAsync();
        }, "تعذّر الانتقال إلى الفاتورة التالية");

    private bool CanMoveToNextBatchItem() => _batch.CanMoveNext;

    [RelayCommand(CanExecute = nameof(CanMoveToPreviousBatchItem))]
    private async Task PreviousBatchItemAsync() =>
        await RunGuardedAsync(async () =>
        {
            if (_batch.MovePrevious()) await ShowBatchItemCoreAsync();
        }, "تعذّر الانتقال إلى الفاتورة السابقة");

    private bool CanMoveToPreviousBatchItem() => _batch.CanMovePrevious;

    /// <summary>Discards the current batch item without saving and moves on.</summary>
    [RelayCommand(CanExecute = nameof(CanSkipBatchItem))]
    private async Task SkipBatchItemAsync() =>
        await RunGuardedAsync(async () =>
        {
            await SkipCurrentAndAdvanceCoreAsync();
        }, "تعذّر تخطي هذه الفاتورة");

    private bool CanSkipBatchItem() => _batch.Current is not null;

    private async Task SkipCurrentAndAdvanceCoreAsync()
    {
        if (_batch.Current is not { } item) return;

        await _batch.SkipAsync(item.Index);

        // Detach from the deleted files before advancing so nothing tries to re-read them.
        _enhancedImagePath = null;
        _ocrImagePath = null;

        // Either way the form is rebuilt: MoveNext lands on the next unreviewed item, and
        // when there is none, Current is the just-skipped item and the "batch complete"
        // branch of ShowBatchItemCoreAsync takes over.
        _batch.MoveNext();
        await ShowBatchItemCoreAsync();
    }

    /// <summary>
    /// Stops the batch after the file currently being extracted.
    /// </summary>
    /// <remarks>
    /// Intentionally synchronous and unguarded. IsBusy stays true for the whole batch, so
    /// a guarded command would refuse to run for exactly as long as there is something to
    /// cancel — the one thing it exists to do.
    /// </remarks>
    [RelayCommand]
    private void CancelBatch()
    {
        _batch.RequestCancel();
        SetStatus("جارٍ إلغاء الدفعة بعد انتهاء الملف الحالي…");
    }

    /// <summary>Ends the batch and cleans up everything that was not saved.</summary>
    [RelayCommand]
    private async Task EndBatchAsync() =>
        await RunGuardedAsync(async () =>
        {
            await _batch.ClearAsync();
            _enhancedImagePath = null;
            _ocrImagePath = null;
            RefreshImageAvailability();
            SetStatus("تم إنهاء الدفعة.");
        }, "تعذّر إنهاء الدفعة");

    private void ApplyInvoice(Invoice invoice, ExtractionResult? extraction = null)
    {
        _isApplyingInvoice = true;
        try
        {
            // Derived from the extraction being applied, so the overlay can never show
            // boxes belonging to a previously reviewed invoice. A null extraction — a
            // saved record reopened, or a file the user is keying in by hand — clears it.
            FieldRegions = FieldRegionMap.From(extraction);
            SelectedRegion = null;

            MerchantName = invoice.MerchantName;
            InvoiceNumber = invoice.InvoiceNumber;
            InvoiceDate = invoice.InvoiceDate is { } date
                ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue))
                : null;
            TotalAmount = invoice.TotalAmount;
            SelectedInvoiceType = invoice.InvoiceType.ToString();
            ImagePath = invoice.ImagePath ?? ImagePath;
            _enhancedImagePath = invoice.EnhancedImagePath ?? _enhancedImagePath;
            _ocrImagePath = invoice.OcrImagePath ?? _ocrImagePath;

            // The picker is rebuilt before the contact is resolved: ResolveCustomer sets
            // SelectedCustomer, and the entry it should light up has to exist by then.
            RebuildCustomerChoices(extraction?.CustomerName?.Results);

            MerchantNeedsReview =
                extraction?.CustomerName?.RequiresManualReview ?? false;

            ResolveCustomer(invoice);

            // Deliberately after ResolveCustomer, which populates City from the chosen
            // contact. A saved invoice carries its own City and that stored snapshot wins;
            // a freshly extracted one arrives with City null, so the contact's value
            // stands. Assigning before the resolve would let the contact overwrite the
            // snapshot every time a saved invoice was reopened.
            if (invoice.City is not null) City = invoice.City;

            Items.Clear();
            for (var i = 0; i < invoice.Items.Count; i++)
            {
                // Confidence and ranked results come from the extraction when present.
                // A hand-entered or previously-saved row is treated as fully confident
                // and its picker is simply the catalog: there is nothing to rank it
                // against, and nothing here re-derives a ranking the service did not send.
                var extracted = extraction is not null && i < extraction.Products.Count
                    ? extraction.Products[i].ProductName
                    : null;

                AddRow(new InvoiceItemRowViewModel(
                    invoice.Items[i],
                    Products,
                    extracted?.OcrConfidence ?? 1.0,
                    extracted?.Results,
                    extracted?.RequiresManualReview ?? false));
            }

            LinkProductsToCatalog();
        }
        finally
        {
            _isApplyingInvoice = false;
        }

        RefreshTotals();
        RefreshImageAvailability();
    }

    /// <summary>
    /// Re-checks which of the three renderings exist on disk and falls back to one that
    /// does when the selected view has no file — otherwise the pane would go blank on an
    /// invoice the service returned no diagnostic images for.
    /// </summary>
    private void RefreshImageAvailability()
    {
        HasOriginalImage = Exists(ImagePath);
        HasEnhancedImage = Exists(_enhancedImagePath);
        HasOcrImage = Exists(_ocrImagePath);

        OnPropertyChanged(nameof(HasAnyViewableImage));

        // Enhanced first: it is the default and the most legible for most scans.
        if (CurrentImagePath is null)
        {
            if (HasEnhancedImage) SelectedImageView = InvoiceImageView.Enhanced;
            else if (HasOriginalImage) SelectedImageView = InvoiceImageView.Original;
            else if (HasOcrImage) SelectedImageView = InvoiceImageView.OcrInput;
        }

        OnPropertyChanged(nameof(CurrentImagePath));

        static bool Exists(string? path) =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    partial void OnSelectedImageViewChanged(InvoiceImageView value) =>
        OnPropertyChanged(nameof(CurrentImagePath));

    partial void OnImagePathChanged(string? value) =>
        OnPropertyChanged(nameof(CurrentImagePath));

    /// <summary>
    /// Picks the contact this invoice belongs to: the record the service already matched
    /// if it is still there, otherwise a local match on the merchant text.
    /// </summary>
    private void ResolveCustomer(Invoice invoice)
    {
        if (invoice.CustomerId is { } id)
        {
            var stored = Customers.FirstOrDefault(c => c.CustomerId == id);
            if (stored is not null)
            {
                SelectedCustomer = stored;
                return;
            }
        }

        // No local fuzzy fallback. The service already matched this text against the
        // catalog it was handed and reported what it found; re-matching here with a second
        // implementation would answer the same question differently, and the invoice would
        // be filed under whichever side ran last. An exact name is still resolved, since
        // that needs no interpretation — anything less is the user's choice to make from
        // the picker.
        var exact = Customers.FirstOrDefault(c =>
            string.Equals(c.Name, invoice.MerchantName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.AliasName, invoice.MerchantName, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            SelectedCustomer = exact;
            return;
        }

        // Still nothing, but the service ranked the catalog against what it read. Select
        // the best of those even when it fell under the review threshold — the same rule
        // the product rows follow. This is not the app second-guessing the threshold:
        // MerchantNeedsReview is set from RequiresManualReview independently, so the
        // review banner and the amber highlight are unaffected, and the picker's own text
        // carries the score. An empty box for a merchant the service *did* rank simply
        // hid a usable answer behind a scroll through the whole contacts table.
        SelectedCustomer = CustomerChoices.FirstOrDefault(c => c.IsSuggestion)?.Customer;
    }

    /// <summary>
    /// Links each unlinked row to the catalog product whose name it matches exactly. Rows
    /// that resolve to nothing stay unlinked and are reported by validation; they are
    /// never silently saved as free text.
    /// </summary>
    /// <remarks>
    /// Exact only. A close-but-not-equal name is what the row's picker is for, already
    /// ranked by the service — linking it here on a second opinion would silently commit
    /// the user to a product they never chose, which is the one outcome the ranked list
    /// exists to prevent.
    /// </remarks>
    private void LinkProductsToCatalog()
    {
        foreach (var row in Items)
        {
            if (row.IsProductLinked) continue;

            var match = Products.FirstOrDefault(p =>
                string.Equals(p.Name, row.ProductName, StringComparison.OrdinalIgnoreCase));

            if (match is not null) row.LinkTo(match);
        }

        RefreshLinkState();
    }

    /// <summary>
    /// Sets the status line from the extraction's own warnings, computed here rather than
    /// sent by the service.
    /// </summary>
    private void ReportExtraction(ExtractionResult result, Invoice invoice, string prefix = "")
    {
        var warnings = _warnings.Build(result, invoice);

        if (warnings.Count == 0)
        {
            SetStatus(
                $"{prefix}تم استخراج {Items.Count} بند خلال {result.ProcessingMs} مللي ثانية. راجع واحفظ.");
            return;
        }

        // The most serious one by name, not just a count: "3 warnings" tells the user to
        // go looking, while the headline says what for.
        SetStatus(
            $"{prefix}تم استخراج {Items.Count} بند مع {warnings.Count} تحذير — {warnings[0].Message} راجع بعناية.");
    }

    // ---- row management ---------------------------------------------------

    private void AddRow(InvoiceItemRowViewModel row)
    {
        row.RowChanged += OnRowChanged;
        Items.Add(row);
    }

    private void OnRowChanged(object? sender, EventArgs e)
    {
        HasUnsavedChanges = true;
        RefreshTotals();

        // Edits now land on the row ViewModel directly rather than through a grid
        // cell-commit event, so re-validation hangs off the row itself. Validation is
        // pure and synchronous, so running it per commit is cheap.
        if (!_isApplyingInvoice) Validate();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Detach handlers from removed rows so they cannot keep the ViewModel alive or
        // fire after removal.
        if (e.OldItems is not null)
        {
            foreach (InvoiceItemRowViewModel row in e.OldItems)
                row.RowChanged -= OnRowChanged;
        }

        RefreshTotals();
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(ComputedTotal));
        OnPropertyChanged(nameof(TotalsDisagree));
        OnPropertyChanged(nameof(TotalComparisonText));
        OnPropertyChanged(nameof(InvalidRowCount));
        RefreshLinkState();
    }

    private void RefreshLinkState()
    {
        OnPropertyChanged(nameof(UnlinkedRowCount));
        OnPropertyChanged(nameof(HasUnlinkedRows));
        OnPropertyChanged(nameof(UnlinkedRowsText));
        AddProductToCatalogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddItem()
    {
        AddRow(new InvoiceItemRowViewModel(
            new InvoiceItem { ProductName = string.Empty }, Products));

        HasUnsavedChanges = true;
        RefreshLinkState();
    }

    /// <summary>
    /// Registers a row's OCR text as a new catalog product and links the row to it.
    /// This is the only route from "the invoice mentions something new" to a saved line:
    /// the product enters the Products table first, then the row points at it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddProductToCatalog))]
    private async Task AddProductToCatalogAsync(InvoiceItemRowViewModel? row)
    {
        if (row is null || row.IsProductLinked) return;

        var name = row.ProductName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("لا يوجد اسم منتج في هذا الصف لإضافته.");
            return;
        }

        await RunGuardedAsync(async () =>
        {
            // Products.Name is UNIQUE; an existing entry is linked rather than duplicated.
            var product = await _products.FindByNameAsync(name);
            if (product is null)
            {
                product = new Product { Name = name };
                await _products.CreateAsync(product);
            }

            // Inserted rather than reloaded: refilling the collection would clear every
            // other row's picker, since their selections point into this very list.
            if (Products.All(p => p.ProductId != product.ProductId))
            {
                InsertProductSorted(product);

                // Every row's picker has to offer the new product, not just this one.
                foreach (var candidateRow in Items) candidateRow.RefreshChoices();
            }

            // Other rows naming the same thing get linked in the same pass.
            LinkProductsToCatalog();

            HasUnsavedChanges = true;
            SetStatus($"تمت إضافة '{product.Name}' إلى المنتجات وربط البند به.");
            Validate();
        }, "تعذّرت إضافة المنتج إلى قائمة المنتجات");
    }

    private static bool CanAddProductToCatalog(InvoiceItemRowViewModel? row) =>
        row is { IsProductLinked: false };

    /// <summary>Keeps the picker list in the Name order the repository returns.</summary>
    private void InsertProductSorted(Product product)
    {
        var index = 0;
        while (index < Products.Count &&
               string.Compare(Products[index].Name, product.Name,
                   StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }

        Products.Insert(index, product);
    }

    [RelayCommand]
    private void RemoveItem(InvoiceItemRowViewModel? row)
    {
        if (row is null) return;
        Items.Remove(row);
        HasUnsavedChanges = true;
    }

    /// <summary>Sets the header total from the sum of the lines.</summary>
    [RelayCommand]
    private void UseComputedTotal()
    {
        TotalAmount = ComputedTotal;
        HasUnsavedChanges = true;
        Validate();
    }

    /// <summary>Fixes every arithmetic-mismatched row by accepting quantity × unit price.</summary>
    [RelayCommand]
    private void FixLineTotals()
    {
        var fixedCount = 0;
        foreach (var row in Items.Where(r => !r.IsArithmeticValid).ToList())
        {
            row.AcceptComputedTotal();
            fixedCount++;
        }

        if (fixedCount > 0)
        {
            HasUnsavedChanges = true;
            SetStatus($"تمت إعادة حساب {fixedCount} إجمالي بند.");
        }

        Validate();
    }

    // ---- validation -------------------------------------------------------

    [RelayCommand]
    private void Validate()
    {
        // Strict mode: this is the screen where the contact and the product links can
        // still be established, so it is the screen that insists on them.
        var result = _validation.Validate(BuildInvoice(), requireCatalogLinks: true);

        ValidationMessages.Clear();
        foreach (var issue in result.Issues)
            ValidationMessages.Add(issue.Message);

        RefreshValidationSummary();
        RefreshTotals();

        if (result.IsClean)
            SetStatus("اجتاز التحقق — لم يتم العثور على مشاكل.");
        else if (result.HasErrors)
            SetError($"تم العثور على {result.Issues.Count} مشكلة. الصفوف التي بها أخطاء مميزة.");
        else
            SetStatus($"{result.Issues.Count} تحذير. راجع قبل الحفظ.");
    }

    // ---- saving -----------------------------------------------------------

    /// <summary>
    /// Checks for duplicates and raises <see cref="DuplicatesDetected"/> if any are
    /// found; otherwise saves immediately. The View decides whether to proceed.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunGuardedAsync(async () =>
        {
            var invoice = BuildInvoice();

            var result = _validation.Validate(invoice, requireCatalogLinks: true);
            ValidationMessages.Clear();
            foreach (var issue in result.Issues)
                ValidationMessages.Add(issue.Message);
            RefreshValidationSummary();

            if (result.HasErrors)
            {
                SetError("أصلح المشاكل المميزة قبل الحفظ.");
                return;
            }

            var duplicates = await _duplicates.FindDuplicatesAsync(invoice);
            if (duplicates.Count > 0)
            {
                // Hand the decision to the user rather than blocking: two identical
                // receipts from the same shop on the same day are legitimate.
                DuplicatesDetected?.Invoke(this, duplicates);
                return;
            }

            await PersistAsync(invoice);
        }, "فشل الحفظ");
    }

    /// <summary>Saves without re-checking duplicates. Called after the user confirms.</summary>
    public async Task SaveConfirmedAsync()
    {
        await RunGuardedAsync(async () => await PersistAsync(BuildInvoice()),
            "فشل الحفظ");
    }

    private async Task PersistAsync(Invoice invoice)
    {
        // Copy the source image into app storage so the record does not depend on a
        // file the user might move or delete. The stem ties the imported original to the
        // enhanced and OCR renderings already written next to it during processing.
        if (!string.IsNullOrWhiteSpace(invoice.ImagePath) && _editingInvoiceId == 0)
        {
            var stem = _batch.Current?.Stem
                ?? $"{DateTime.Now:yyyyMMdd_HHmmssfff}_000";

            invoice.ImagePath = await ImportImageAsync(invoice.ImagePath!, stem)
                ?? invoice.ImagePath;
        }

        if (_editingInvoiceId > 0)
        {
            invoice.InvoiceId = _editingInvoiceId;
            await _invoices.UpdateAsync(invoice);
        }
        else
        {
            _editingInvoiceId = await _invoices.CreateAsync(invoice);
        }

        ImagePath = invoice.ImagePath;
        HasUnsavedChanges = false;

        SetStatus($"تم حفظ السجل رقم {_editingInvoiceId}.");
        InvoiceSaved?.Invoke(this, _editingInvoiceId);

        // Auto-advance through the batch. Deliberately the unguarded core: PersistAsync
        // already runs inside SaveAsync's guard, so calling the guarded
        // NextBatchItemCommand here would silently do nothing and strand the review.
        if (_batch.Current is { } item && !_batch.IsProcessing)
        {
            _batch.MarkSaved(item.Index);

            // MoveNext lands on the next unreviewed item; when there is none, Current is
            // the item just marked saved and ShowBatchItemCoreAsync clears the form.
            _batch.MoveNext();
            await ShowBatchItemCoreAsync();
        }
    }

    /// <summary>
    /// Copies the user's file into app storage as <c>{stem}_orig{ext}</c>, beside the
    /// <c>_enh</c> and <c>_ocr</c> renderings written during processing.
    /// </summary>
    /// <returns>
    /// The destination path, or null when the source has gone. Null rather than a throw
    /// because a batch may be saved minutes after import, by which time the user may have
    /// moved or deleted the originals — that should cost the image, not the invoice.
    /// </returns>
    private async Task<string?> ImportImageAsync(string sourcePath, string stem)
    {
        if (!File.Exists(sourcePath)) return null;

        var directory = await _settings.GetOrDefaultAsync(
            SettingKeys.ImageStorageDirectory, AppPaths.DefaultImageDirectory);

        Directory.CreateDirectory(directory);

        var destination = Path.Combine(
            directory, $"{stem}_orig{Path.GetExtension(sourcePath)}");

        // Collision guard: two saves of the same batch item, or a stem reused after a
        // clock change, must not overwrite an image another invoice row points at.
        var attempt = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(
                directory, $"{stem}_orig_{attempt++}{Path.GetExtension(sourcePath)}");
        }

        try
        {
            await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return destination;
    }

    [RelayCommand]
    private async Task ExportAsync(string? formatName)
    {
        var format = string.Equals(formatName, "csv", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.Csv
            : ExportFormat.Excel;

        await RunGuardedAsync(async () =>
        {
            var directory = await _settings.GetOrDefaultAsync(
                SettingKeys.ExportDirectory, AppPaths.DefaultExportDirectory);

            var stem = string.IsNullOrWhiteSpace(InvoiceNumber)
                ? $"invoice_{DateTime.Now:yyyyMMdd_HHmmss}"
                : $"invoice_{SanitizeFileName(InvoiceNumber!)}";

            var path = Path.Combine(directory, stem + _export.GetExtension(format));

            await _export.ExportSingleInvoiceAsync(BuildInvoice(), path, format);
            SetStatus($"تم التصدير إلى {path}");
        }, "فشل التصدير");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private Invoice BuildInvoice() => new()
    {
        InvoiceId = _editingInvoiceId,
        InvoiceNumber = InvoiceNumber,

        // The contact is the source of truth for the merchant name once one is chosen,
        // so the text stored on the invoice always matches the Customers row it links to.
        MerchantName = SelectedCustomer?.Name ?? MerchantName ?? string.Empty,
        City = City,
        InvoiceDate = InvoiceDate is { } date ? DateOnly.FromDateTime(date.Date) : null,
        TotalAmount = TotalAmount,
        InvoiceType = Enum.TryParse<InvoiceType>(SelectedInvoiceType, out var type)
            ? type : InvoiceType.Purchase,
        ImagePath = ImagePath,
        EnhancedImagePath = _enhancedImagePath,
        OcrImagePath = _ocrImagePath,
        CustomerId = SelectedCustomer?.CustomerId,
        ContentHash = _contentHash,
        Items = Items.Select(i => i.ToModel()).ToList()
    };

    // ---- image viewer -----------------------------------------------------

    [RelayCommand]
    private void ZoomIn() => ZoomFactor = Math.Min(ZoomFactor * 1.25, 8.0);

    [RelayCommand]
    private void ZoomOut() => ZoomFactor = Math.Max(ZoomFactor / 1.25, 0.1);

    [RelayCommand]
    private void ZoomReset() => ZoomFactor = 1.0;

    [RelayCommand]
    private void ToggleFormPane() => IsFormPaneVisible = !IsFormPaneVisible;

    [RelayCommand]
    private void ToggleImagePane() => IsImagePaneVisible = !IsImagePaneVisible;

    // ---- change tracking --------------------------------------------------

    partial void OnMerchantNameChanged(string value) => HasUnsavedChanges = true;
    partial void OnInvoiceNumberChanged(string? value) => HasUnsavedChanges = true;
    partial void OnCityChanged(string? value) => HasUnsavedChanges = true;
    partial void OnInvoiceDateChanged(DateTimeOffset? value) => HasUnsavedChanges = true;

    partial void OnSelectedCustomerChanged(Customer? value)
    {
        HasUnsavedChanges = true;

        // The invoice records the contact's canonical name, not whatever OCR read off
        // the letterhead.
        if (value is not null) MerchantName = value.Name;

        // The city belongs to the contact, so picking one fills it in. Invoices.City is
        // still written as a snapshot at save time — that is what the dashboard filters
        // and the analytics read, and it must not shift when a contact is later edited.
        City = value?.City;

        // Keeps the picker in step when the contact is set from code — resolved from a
        // match, or loaded with a saved invoice.
        var choice = value is null
            ? null
            : CustomerChoices.FirstOrDefault(c => c.Customer.CustomerId == value.CustomerId);

        if (!ReferenceEquals(choice, SelectedCustomerChoice)) SelectedCustomerChoice = choice;

        // Once a contact is confirmed there is nothing left to review.
        if (value is not null) MerchantNeedsReview = false;

        OnPropertyChanged(nameof(IsCustomerLinked));
    }

    partial void OnSelectedInvoiceTypeChanged(string value)
    {
        OnPropertyChanged(nameof(CounterpartyLabel));
        OnPropertyChanged(nameof(CounterpartyPlaceholder));

        // ApplyInvoice sets the type before the line items are populated. Without this
        // guard, the type change would validate a half-built form and print errors about
        // rows that are about to be added.
        if (_isApplyingInvoice) return;

        HasUnsavedChanges = true;
        Validate();
    }

    partial void OnTotalAmountChanged(decimal value)
    {
        HasUnsavedChanges = true;
        RefreshTotals();
    }
}
