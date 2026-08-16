using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Models;

namespace InvoiceDigitizationApp.Services.Data;

public interface ICustomerRepository
{
    Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default);
    Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default);
    Task<int> CreateAsync(Customer customer, CancellationToken ct = default);
    Task UpdateAsync(Customer customer, CancellationToken ct = default);
    Task DeleteAsync(int customerId, CancellationToken ct = default);
}
