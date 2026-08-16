using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InvoiceDigitizationApp.Helpers;
using InvoiceDigitizationApp.Models;
using InvoiceDigitizationApp.Services.AiServiceClient;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// One entry in a match picker: a catalog record, and how well the OCR text scored
/// against it when it came from the ranked candidate list.
/// </summary>
/// <remarks>
/// The pickers put the suggestions at the top with their scores and the rest of the
/// catalog underneath, so the user confirms a correct read without scrolling and
/// corrects a wrong one from a short list — which is the whole point of returning five
/// candidates rather than one answer.
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
/// Builds the ordered picker lists: the service's ranked candidates first, then the rest
/// of the catalog.
/// </summary>
/// <remarks>
/// The local fallback drops zero-scoring entries. A suggestion is a claim that something
/// resembles what was read; offering "حذاء جلد أسود (0%)" at the top of the list for a
/// line that says "منتج" is worse than offering nothing, because it puts an unrelated
/// product one careless click away from being saved.
/// </remarks>
public static class MatchChoiceBuilder
{
    /// <summary>
    /// Suggestions first, in the order the service ranked them, then every other contact
    /// in catalog order. Falls back to matching locally when the service sent no
    /// candidates — which is what happens when the user has added a contact since the
    /// extraction ran.
    /// </summary>
    public static IReadOnlyList<CustomerChoice> ForCustomers(
        IEnumerable<Customer> catalog,
        IReadOnlyList<MatchCandidate>? candidates,
        string? ocrText,
        int topK)
    {
        var customers = catalog.ToList();
        var ranked = Resolve(customers, candidates, c => c.CustomerId, c => c.Name);

        if (ranked.Count == 0 && !string.IsNullOrWhiteSpace(ocrText))
        {
            ranked = CatalogMatcher.RankCustomers(ocrText, customers, topK)
                .Where(match => match.Score > 0)
                .Select(match => (match.Customer, match.Score * 100.0))
                .ToList();
        }

        var suggested = ranked.Select(entry => entry.Item1.CustomerId).ToHashSet();

        return ranked
            .Select(entry => new CustomerChoice(entry.Item1, entry.Item2))
            .Concat(customers
                .Where(customer => !suggested.Contains(customer.CustomerId))
                .Select(customer => new CustomerChoice(customer)))
            .ToList();
    }

    public static IReadOnlyList<ProductChoice> ForProducts(
        IEnumerable<Product> catalog,
        IReadOnlyList<MatchCandidate>? candidates,
        string? ocrText,
        int topK)
    {
        var products = catalog.ToList();
        var ranked = Resolve(products, candidates, p => p.ProductId, p => p.Name);

        if (ranked.Count == 0 && !string.IsNullOrWhiteSpace(ocrText))
        {
            ranked = CatalogMatcher.RankProducts(ocrText, products, topK)
                .Where(match => match.Score > 0)
                .Select(match => (match.Product, match.Score * 100.0))
                .ToList();
        }

        var suggested = ranked.Select(entry => entry.Item1.ProductId).ToHashSet();

        return ranked
            .Select(entry => new ProductChoice(entry.Item1, entry.Item2))
            .Concat(products
                .Where(product => !suggested.Contains(product.ProductId))
                .Select(product => new ProductChoice(product)))
            .ToList();
    }

    /// <summary>
    /// Maps the service's candidates onto catalog rows, by id where the service supplied
    /// one and by name otherwise. A candidate naming a record that no longer exists is
    /// dropped rather than shown: the picker must only offer rows that can actually be
    /// saved.
    /// </summary>
    private static List<(T, double)> Resolve<T>(
        List<T> catalog,
        IReadOnlyList<MatchCandidate>? candidates,
        Func<T, int> idOf,
        Func<T, string> nameOf)
    {
        var resolved = new List<(T, double)>();
        if (candidates is null) return resolved;

        var seen = new HashSet<int>();

        foreach (var candidate in candidates)
        {
            var match = candidate.MatchedId is { } id
                ? catalog.FirstOrDefault(entry => idOf(entry) == id)
                : catalog.FirstOrDefault(entry =>
                    string.Equals(nameOf(entry), candidate.MatchedValue,
                        StringComparison.OrdinalIgnoreCase));

            if (match is null || !seen.Add(idOf(match))) continue;

            resolved.Add((match, candidate.SimilarityScore));
        }

        return resolved;
    }
}
