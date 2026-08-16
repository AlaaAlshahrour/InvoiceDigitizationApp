using System.Collections.Generic;
using System.Linq;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Helpers;

/// <summary>
/// Resolves OCR text to a catalog record. Mirrors the Python service's record-level
/// matcher (<c>app/postprocessing/matching.py</c>), and is what the app falls back to
/// when the service returns no match — or returns one from a catalog that has since
/// changed under it.
/// </summary>
public static class CatalogMatcher
{
    /// <summary>
    /// Default similarity floor, matching the service's <c>fuzzy_match_threshold</c>.
    /// Below this a wrong-but-confident match is likelier than a right one, and leaving
    /// the OCR text for the user to resolve is the safer answer.
    /// </summary>
    public const double DefaultThreshold = 0.80;

    /// <summary>A catalog record and the specific name that scored the hit.</summary>
    public sealed record CustomerMatch(Customer Customer, string MatchedName, double Score);

    public sealed record ProductMatch(Product Product, double Score);

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
        if (string.IsNullOrWhiteSpace(text) || customers is null) return null;

        CustomerMatch? best = null;

        foreach (var customer in customers)
        {
            foreach (var name in NamesOf(customer))
            {
                var score = FuzzyMatch.Similarity(text, name);
                if (score >= threshold && (best is null || score > best.Score))
                    best = new CustomerMatch(customer, name, score);
            }
        }

        return best;
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
        if (string.IsNullOrWhiteSpace(text) || products is null) return null;

        ProductMatch? best = null;

        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Name)) continue;

            var score = FuzzyMatch.Similarity(text, product.Name);
            if (score >= threshold && (best is null || score > best.Score))
                best = new ProductMatch(product, score);
        }

        return best;
    }

    /// <summary>
    /// Every name a contact answers to, canonical name first, without blanks or
    /// case-insensitive duplicates. This is also the shape sent to the AI service.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(Customer customer) =>
        new[] { customer.Name, customer.AliasName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
}
