using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// One entry in a match picker: a catalog record, and how well the OCR text scored
/// against it when it came from the service's ranked results.
/// </summary>
/// <remarks>
/// The pickers put the suggestions at the top with their scores and the rest of the
/// catalog underneath, so the user confirms a correct read without scrolling and
/// corrects a wrong one from a short list — which is the whole point of returning five
/// results rather than one answer.
/// </remarks>
public sealed class CustomerChoice
{
    public CustomerChoice(Customer customer, double? score = null)
    {
        Customer = customer;
        Score = score;
    }

    public Customer Customer { get; }

    /// <summary>Similarity as a percentage, or null for a plain catalog entry.</summary>
    public double? Score { get; }

    public bool IsSuggestion => Score is not null;

    public string DisplayName => Score is { } score
        ? $"{Customer.DisplayName}  ({score.ToString("0.#", CultureInfo.InvariantCulture)}%)"
        : Customer.DisplayName;
}

public sealed class ProductChoice
{
    public ProductChoice(Product product, double? score = null)
    {
        Product = product;
        Score = score;
    }

    public Product Product { get; }

    public double? Score { get; }

    public bool IsSuggestion => Score is not null;

    public string DisplayName => Score is { } score
        ? $"{Product.Name}  ({score.ToString("0.#", CultureInfo.InvariantCulture)}%)"
        : Product.Name;
}

/// <summary>
/// Builds the ordered picker lists: the service's ranked results first, then — only when
/// asked for — the rest of the catalog in its own order.
/// </summary>
/// <remarks>
/// <para>
/// There is no local fallback matcher any more. The app used to re-rank the catalog itself
/// when the service returned nothing — with its own copies of the normalization and
/// similarity rules, which could and did drift from the service's. Two implementations of
/// "the same string" meant the suggestion a user saw depended on which side had computed
/// it, and the disagreement was invisible until a merchant the service had matched came
/// back unmatched in the app. When there are no results, the picker is simply the catalog,
/// which is honest about the app having nothing to suggest.
/// </para>
/// <para>
/// <b>The full catalog is no longer the default.</b> A line-items grid builds one picker
/// per row, and each picker used to materialize a <see cref="ProductChoice"/> for every
/// product in the table — so an invoice with 20 lines against a catalog of a few thousand
/// built tens of thousands of objects, then handed each ComboBox a list that long to
/// realize and lay out on first open. That is what made a click on "اختر المنتج" take
/// several seconds and appear to need pressing more than once: the first click *had*
/// opened the drop-down, it was simply still building it. Showing the ranked matches
/// alone keeps every list short, which is also the list the user actually wants — the
/// whole point of asking the service for ranked results.
/// </para>
/// </remarks>
public static class MatchChoiceBuilder
{
    /// <summary>How many ranked alternatives a picker shows by default.</summary>
    public const int DefaultTopK = 5;

    /// <summary>
    /// Suggestions first, in the order the service ranked them, then every other contact
    /// in catalog order when <paramref name="includeCatalog"/> is set.
    /// </summary>
    public static IReadOnlyList<CustomerChoice> ForCustomers(
        IEnumerable<Customer> catalog,
        IReadOnlyList<MatchResult>? results,
        bool includeCatalog = true)
    {
        var customers = catalog.ToList();
        var ranked = Resolve(customers, results, c => c.CustomerId, c => c.Name);
        var suggested = ranked.Select(entry => entry.Item1.CustomerId).ToHashSet();

        var choices = ranked
            .Select(entry => new CustomerChoice(entry.Item1, entry.Item2))
            .ToList();

        // With nothing ranked there is no short list to offer, so the catalog stands in
        // regardless — an empty picker would leave the user no way to link the invoice.
        if (includeCatalog || choices.Count == 0)
        {
            choices.AddRange(customers
                .Where(customer => !suggested.Contains(customer.CustomerId))
                .Select(customer => new CustomerChoice(customer)));
        }

        return choices;
    }

    /// <summary>
    /// The row picker's entries.
    /// </summary>
    /// <param name="includeCatalog">
    /// False for the line-items grid, where only the ranked matches are shown and the
    /// full catalog is reached through the row's own "show all" toggle. See the type's
    /// remarks for why this is not the default any more.
    /// </param>
    public static IReadOnlyList<ProductChoice> ForProducts(
        IEnumerable<Product> catalog,
        IReadOnlyList<MatchResult>? results,
        bool includeCatalog = true)
    {
        var products = catalog.ToList();
        var ranked = Resolve(products, results, p => p.ProductId, p => p.Name);
        var suggested = ranked.Select(entry => entry.Item1.ProductId).ToHashSet();

        var choices = ranked
            .Select(entry => new ProductChoice(entry.Item1, entry.Item2))
            .ToList();

        if (includeCatalog || choices.Count == 0)
        {
            choices.AddRange(products
                .Where(product => !suggested.Contains(product.ProductId))
                .Select(product => new ProductChoice(product)));
        }

        return choices;
    }

    /// <summary>
    /// Maps the service's results onto catalog rows, by id where it supplied one and by
    /// name otherwise. A result naming a record that no longer exists is dropped rather
    /// than shown: the picker must only offer rows that can actually be saved.
    /// </summary>
    private static List<(T, double)> Resolve<T>(
        List<T> catalog,
        IReadOnlyList<MatchResult>? results,
        Func<T, int> idOf,
        Func<T, string> nameOf)
    {
        var resolved = new List<(T, double)>();
        if (results is null) return resolved;

        var seen = new HashSet<int>();

        foreach (var result in results)
        {
            var match = result.EntryId is { } id
                ? catalog.FirstOrDefault(entry => idOf(entry) == id)
                : catalog.FirstOrDefault(entry =>
                    string.Equals(nameOf(entry), result.Value, StringComparison.OrdinalIgnoreCase));

            if (match is null || !seen.Add(idOf(match))) continue;

            // The wire carries a 0–1 fraction; the picker shows a percentage.
            resolved.Add((match, result.StringMatchingScore * 100.0));
        }

        return resolved;
    }
}
