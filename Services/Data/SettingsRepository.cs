using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace InvoiceDigitizationApp.Services.Data;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _factory;

    public SettingsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT Value FROM AppSettings WHERE Key = @Key",
            new { Key = key }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<string> GetOrDefaultAsync(string key, string fallback, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AppSettings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """,
            new { Key = key, Value = value }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct).ConfigureAwait(false);
        // InvariantCulture: settings are written by the app, not localized by the user.
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct).ConfigureAwait(false);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }
}
