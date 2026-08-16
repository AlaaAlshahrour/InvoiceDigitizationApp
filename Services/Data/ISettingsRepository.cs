using System.Threading;
using System.Threading.Tasks;

namespace InvoiceDigitizationApp.Services.Data;

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<string> GetOrDefaultAsync(string key, string fallback, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
}
