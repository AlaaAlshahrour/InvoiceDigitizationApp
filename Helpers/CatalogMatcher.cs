using System;
using System.Collections.Generic;
using System.Linq;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// Resolves OCR text to a catalog record, returning a ranked list rather than a single
/// answer. Mirrors the service's <c>string_matching/catalog.py</c>, and is what the app
/// falls back to when the service returns no candidates — or returns them from a catalog
/// that has since changed under it, which happens whenever the user adds a product
/// mid-batch.
/// </summary>
/// <remarks>
/// A record can answer to several names: a customer has a canonical <see cref="Customer.Name"/>
/// and an <see cref="Customer.AliasName"/>, and an invoice may be printed with either.
/// Both compete on equal terms, the record wins on its best-scoring name, and the answer
/// is always reported under the canonical name so the invoice is filed consistently.
/// </remarks>
public static class CatalogMatcher
{
    /// <summary>
    /// Default similarity floor, matching the service's review threshold. Below this a
    /// wrong-but-confident match is likelier than a right one, and leaving the OCR text
    /// for the user to resolve is the safer answer.
    /// </summary>
    public const double DefaultThreshold = 0.75;

    /// <summary>How many ranked alternatives a dropdown shows.</summary>
    public const int DefaultTopK = 5;

    /// <summary>A catalog record and the specific name that scored the hit.</summary>
    public sealed record CustomerMatch(Customer Customer, string MatchedName, double Score);

    public sealed record ProductMatch(Product Product, double Score);

    // ---- ranked results ---------------------------------------------------

    /// <summary>
    /// The best <paramref name="topK"/> contacts for <paramref name="text"/>, highest
    /// first. Unlike <see cref="FindCustomer"/> this applies no threshold: the list is
    /// what the user picks from, and hiding a weak-but-correct entry would defeat it.
    /// </summary>
    public static IReadOnlyList<CustomerMatch> RankCustomers(
        string? text,
        IEnumerable<Customer>? customers,
        int topK = DefaultTopK)
    {
        if (string.IsNullOrWhiteSpace(text) || customers is null)
            return Array.Empty<CustomerMatch>();

        return customers
            // One entry per record, scored on its best name, so a contact whose alias
            // and canonical name both score does not occupy two slots.
            .Select(customer => NamesOf(customer)
                .Select(name => new CustomerMatch(customer, name, FuzzyMatch.Similarity(text, name)))
                .OrderByDescending(match => match.Score)
                .FirstOrDefault())
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// The best <paramref name="topK"/> products for <paramref name="text"/>, scored
    /// order-independently so a re-ordered description still finds its product.
    /// </summary>
    public static IReadOnlyList<ProductMatch> RankProducts(
        string? text,
        IEnumerable<Product>? products,
        int topK = DefaultTopK)
    {
        if (string.IsNullOrWhiteSpace(text) || products is null)
            return Array.Empty<ProductMatch>();

        return products
            .Where(product => !string.IsNullOrWhiteSpace(product.Name))
            .Select(product => new ProductMatch(product, FuzzyMatch.ProductSimilarity(text, product.Name)))
            .OrderByDescending(match => match.Score)
            .Take(topK)
            .ToList();
    }

    // ---- single best ------------------------------------------------------

    /// <summary>
    /// Best contact for <paramref name="text"/>, or null when nothing clears the
    /// threshold. <see cref="Customer.Name"/> and <see cref="Customer.AliasName"/> are
    /// treated as equivalent names: a hit on either identifies the same record, since an
    /// invoice may be printed with whichever the merchant happens to use.
    /// </summary>
    public static CustomerMatch? FindCustomer(
        string? text,
        IEnumerable<Customer>? customers,
        double threshold = DefaultThreshold)
    {
        var best = RankCustomers(text, customers, topK: 1).FirstOrDefault();
        return best is not null && best.Score >= threshold ? best : null;
    }

    /// <summary>
    /// Best catalog product for <paramref name="text"/>. Products carry nothing but a
    /// name, so the name is the entire matching surface.
    /// </summary>
    public static ProductMatch? FindProduct(
        string? text,
        IEnumerable<Product>? products,
        double threshold = DefaultThreshold)
    {
        var best = RankProducts(text, products, topK: 1).FirstOrDefault();
        return best is not null && best.Score >= threshold ? best : null;
    }

    /// <summary>
    /// Every name a contact answers to, canonical name first, without blanks or
    /// duplicates. This is also the shape sent to the AI service.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(Customer customer) =>
        TextNormalizer.UniqueNames(customer.Name, customer.AliasName);
}
