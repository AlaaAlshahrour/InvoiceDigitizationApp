using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken ct = default);
    Task<Product?> GetByIdAsync(int productId, CancellationToken ct = default);
    Task<int> CreateAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
    Task DeleteAsync(int productId, CancellationToken ct = default);

    /// <summary>Exact, case-insensitive name lookup. Used to resolve OCR matches to catalog ids.</summary>
    Task<Product?> FindByNameAsync(string name, CancellationToken ct = default);
}
