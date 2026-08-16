using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace InvoiceDigitizationApp.Services.Data;

public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a connection with foreign keys enabled and WAL journalling active.
    /// Callers own the returned connection and must dispose it.
    /// </summary>
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>Full path of the database file backing this factory.</summary>
    string DatabasePath { get; }
}
