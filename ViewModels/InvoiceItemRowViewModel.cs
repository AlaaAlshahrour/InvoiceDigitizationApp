using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// One editable row in the verification grid. Wraps an <see cref="InvoiceItem"/> and
/// adds the per-row UI state the grid needs: the catalog the product must be chosen
/// from, live arithmetic, and the OCR confidence that drives low-confidence highlighting.
/// </summary>
public partial class InvoiceItemRowViewModel : ObservableObject
{
    /// <summary>Half a minor unit, matching InvoiceValidationService.LineTolerance.</summary>
    private const decimal Tolerance = 0.01m;

    public InvoiceItemRowViewModel(
        InvoiceItem item,
        ObservableCollection<Product> productCatalog,
        double confidence = 1.0,
        IReadOnlyList<MatchResult>? results = null,
        bool requiresManualReview = false)
    {
        ProductCatalog = productCatalog;
        RequiresManualReview = requiresManualReview;

        // Backing fields, not properties: loading a row must not be mistaken for the
        // user editing it, which would recalculate away the total printed on the paper.
        _productName = item.ProductName;
        _productId = item.ProductId;
        _quantity = item.Quantity;
        _unitPrice = item.UnitPrice;
        _totalPrice = item.TotalPrice;
        _notes = item.Notes;

        ItemId = item.ItemId;
        Confidence = confidence;
        DetectedProductName = item.ProductName;

        _results = results;

        // The service's ranked results first, then the rest of the catalog. Built before
        // the selection below so the picker has something to select into.
        RebuildChoices();

        // A row arriving with an id is already catalog-backed; select it so the picker
        // opens on the right entry instead of looking empty.
        _selectedProduct = FindInCatalog(item.ProductId, item.ProductName);
        if (_selectedProduct is not null)
        {
            _productId = _selectedProduct.ProductId;
            _productName = _selectedProduct.Name;
        }

        _selectedChoice = FindChoice(_selectedProduct);
    }

    /// <summary>
    /// The picker's entries: the extraction's suggestions at the top with their
    /// similarity scores, then every other catalog product.
    /// </summary>
    public ObservableCollection<ProductChoice> ProductChoices { get; } = new();

    /// <summary>
    /// True when the service could not match this line confidently. The row is
    /// highlighted so the user knows the pre-selected product is a guess.
    /// </summary>
    public bool RequiresManualReview { get; }

    /// <summary>How many of <see cref="ProductChoices"/> came from the extraction.</summary>

    public int SuggestionCount => ProductChoices.Count(choice => choice.IsSuggestion);

    public bool HasSuggestions => SuggestionCount > 0;

    /// <summary>The extraction's ranked results, kept so the picker can be rebuilt when
    /// the catalog changes without losing the ranking the service produced.</summary>
    private readonly IReadOnlyList<MatchResult>? _results;

    /// <summary>
    /// Rebuilds the picker against the current catalog. Called when a product is added
    /// mid-review: the new entry has to appear in every row's list, not only the row it
    /// was added from.
    /// </summary>
    public void RefreshChoices() => RebuildChoices();

    private void RebuildChoices()
    {
        var previous = SelectedProduct;

        ProductChoices.Clear();

        foreach (var choice in MatchChoiceBuilder.ForProducts(ProductCatalog, _results))
        {
            ProductChoices.Add(choice);
        }

        // The entry the picker pointed at is gone; point it at the equivalent new one so
        // a rebuild does not look like the user clearing the row.
        SelectedChoice = FindChoice(previous);

        OnPropertyChanged(nameof(SuggestionCount));
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private ProductChoice? FindChoice(Product? product) =>
        product is null
            ? null
            : ProductChoices.FirstOrDefault(choice => choice.Product.ProductId == product.ProductId);

    public int ItemId { get; set; }

    /// <summary>
    /// The products this row may be set to. Selection is restricted to this list —
    /// anything not in the Products table has to be added there first.
    /// </summary>
    public ObservableCollection<Product> ProductCatalog { get; }

    /// <summary>OCR confidence for this row; 1.0 for rows the user added by hand.</summary>
    public double Confidence { get; }

    public bool IsLowConfidence => Confidence < ExtractionThresholds.Review;

    /// <summary>
    /// What OCR read for this line, kept after the row is linked to a catalog product so
    /// the user can still see what the paper actually said.
    /// </summary>
    public string DetectedProductName { get; }

    /// <summary>True when the OCR text was replaced by a different catalog name.</summary>
    public bool WasProductRenamed =>
        IsProductLinked
        && !string.IsNullOrWhiteSpace(DetectedProductName)
        && !string.Equals(DetectedProductName, ProductName, StringComparison.OrdinalIgnoreCase);

    // ---- product ----------------------------------------------------------

    /// <summary>
    /// The catalog entry this row is set to. Setting it is the only supported way to
    /// change the product, which is what keeps every saved line pointing at a real
    /// Products row.
    /// </summary>
    [ObservableProperty] private Product? _selectedProduct;

    /// <summary>
    /// What the picker is set to. A separate property from <see cref="SelectedProduct"/>
    /// because the picker's entries carry a similarity score alongside the product, and
    /// the score is display-only — it must not reach the saved line.
    /// </summary>
    [ObservableProperty] private ProductChoice? _selectedChoice;

    partial void OnSelectedChoiceChanged(ProductChoice? value)
    {
        if (value is not null) SelectedProduct = value.Product;
    }

    /// <summary>
    /// The name written to InvoiceItems. Mirrors the selected catalog entry once the row
    /// is linked, and holds the raw OCR text until then.
    /// </summary>
    [ObservableProperty] private string _productName;

    /// <summary>Products.ProductId, or null while the row is still unresolved.</summary>
    [ObservableProperty] private int? _productId;

    public bool IsProductLinked => ProductId is not null;

    public string ProductStatusHint => IsProductLinked
        ? string.Empty
        : "غير مسجّل في المنتجات";

    partial void OnSelectedProductChanged(Product? value)
    {
        // Null arrives when the catalog is reloaded out from under the picker; that is
        // not the user unlinking the row, so the existing values stand.
        if (value is null) return;

        ProductId = value.ProductId;
        ProductName = value.Name;

        // Keeps the picker in step when the product is set from code — a catalog link
        // resolved after loading, or a product just added from this row.
        var choice = FindChoice(value);
        if (choice is not null && !ReferenceEquals(choice, SelectedChoice))
            SelectedChoice = choice;
    }

    partial void OnProductIdChanged(int? value)
    {
        OnPropertyChanged(nameof(IsProductLinked));
        OnPropertyChanged(nameof(ProductStatusHint));
        OnPropertyChanged(nameof(WasProductRenamed));
    }

    partial void OnProductNameChanged(string value)
    {
        OnPropertyChanged(nameof(WasProductRenamed));
        RowChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points the row at a catalog entry, used when a match is resolved after loading.
    /// </summary>
    public void LinkTo(Product product) => SelectedProduct = product;

    private Product? FindInCatalog(int? productId, string? name)
    {
        if (productId is { } id)
        {
            var byId = ProductCatalog.FirstOrDefault(p => p.ProductId == id);
            if (byId is not null) return byId;
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : ProductCatalog.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- amounts ----------------------------------------------------------

    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private decimal _totalPrice;
    [ObservableProperty] private string? _notes;

    /// <summary>Quantity × UnitPrice — what the line total should be.</summary>
    public decimal ExpectedTotal => Quantity * UnitPrice;

    /// <summary>
    /// False when the line total disagrees with quantity × unit price. Only reachable
    /// from OCR output or a hand-typed total, since editing either factor recalculates.
    /// </summary>
    public bool IsArithmeticValid => Math.Abs(ExpectedTotal - TotalPrice) <= Tolerance;

    public string ArithmeticHint => IsArithmeticValid
        ? string.Empty
        : $"المتوقع {ExpectedTotal:0.##}";

    // Editing a factor re-derives the total: once a human touches the quantity or the
    // unit price, their product is the truth, not the number OCR read off the paper.
    partial void OnQuantityChanged(decimal value) => RecalculateTotal();
    partial void OnUnitPriceChanged(decimal value) => RecalculateTotal();

    // Editing the total itself is an explicit override and is left exactly as typed.
    partial void OnTotalPriceChanged(decimal value) => RecomputeDerived();

    private void RecalculateTotal()
    {
        TotalPrice = decimal.Round(ExpectedTotal, 2, MidpointRounding.AwayFromZero);

        // Also raised directly: an unchanged total (0 × 5 is still 0) fires no property
        // change of its own, and the row still needs to report the edit.
        RecomputeDerived();
    }

    private void RecomputeDerived()
    {
        OnPropertyChanged(nameof(ExpectedTotal));
        OnPropertyChanged(nameof(IsArithmeticValid));
        OnPropertyChanged(nameof(ArithmeticHint));
        RowChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on any value edit so the parent can refresh the grand total.</summary>
    public event EventHandler? RowChanged;

    /// <summary>Fills TotalPrice from Quantity × UnitPrice, accepting the computed value.</summary>
    public void AcceptComputedTotal() =>
        TotalPrice = decimal.Round(ExpectedTotal, 2, MidpointRounding.AwayFromZero);

    partial void OnNotesChanged(string? value) => RowChanged?.Invoke(this, EventArgs.Empty);

    public InvoiceItem ToModel() => new()
    {
        ItemId = ItemId,
        ProductId = ProductId,
        ProductName = ProductName,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        TotalPrice = TotalPrice,
        Notes = Notes
    };
}
