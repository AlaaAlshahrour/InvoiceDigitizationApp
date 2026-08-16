using System;
using System.Data;
using System.Globalization;
using Dapper;

namespace InvoiceDigitizationApp.Services.Data;

/// <summary>
/// SQLite has no native date or boolean type, and Dapper has no built-in DateOnly
/// support, so these handlers pin the on-disk representation explicitly. Registered once
/// at startup via <see cref="Register"/>.
/// </summary>
public static class DapperTypeHandlers
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    /// <summary>ISO-8601 'yyyy-MM-dd' — sorts and compares correctly with BETWEEN.</summary>
    private const string DateFormat = "yyyy-MM-dd";

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        public override DateOnly Parse(object value) => ParseCore(value)
            ?? throw new FormatException($"Cannot parse '{value}' as a date.");
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value?.ToString(DateFormat, CultureInfo.InvariantCulture)
                              ?? (object)DBNull.Value;
        }

        public override DateOnly? Parse(object value) => ParseCore(value);
    }

    private static DateOnly? ParseCore(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case DateTime dt:
                return DateOnly.FromDateTime(dt);
            case DateOnly d:
                return d;
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Exact format first; fall back to a general parse for rows written by an older
        // build or edited by hand outside the app.
        if (DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return exact;

        return DateOnly.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var loose) ? loose : null;
    }
}
