using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InvoiceDigitizationApp.Helpers;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>When true, false maps to Visible instead of Collapsed.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary>Collapses an element when the bound string is null or whitespace.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Red background for rows whose arithmetic does not check out. Bound to
/// IsArithmeticValid, so an edit that fixes the row clears the highlight immediately.
/// </summary>
public sealed class ArithmeticToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isValid = value is bool b && b;
        return isValid
            ? new SolidColorBrush(Colors.Transparent)
            // Low alpha so the row text stays readable in both light and dark themes.
            : new SolidColorBrush(Color.FromArgb(48, 220, 38, 38));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Amber tint for values the OCR engine was unsure about.</summary>
public sealed class ConfidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isLow = value is bool b && b;
        return isLow
            ? new SolidColorBrush(Color.FromArgb(40, 217, 164, 65))
            : new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Formats a decimal as a 2-dp amount using the current culture.</summary>
public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is decimal amount
            ? amount.ToString("N2", CultureInfo.CurrentCulture)
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        decimal.TryParse(value as string, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : 0m;
}

/// <summary>
/// Bridges a decimal ViewModel property to <c>NumberBox.Value</c>, which is a double.
/// Money is held as decimal everywhere else, so the widening happens here, at the one
/// place a numeric control needs it, rather than in the model.
/// </summary>
public sealed class DecimalToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is decimal amount ? (double)amount : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // NumberBox reports NaN for an emptied box; that is "no value entered", not zero
        // arithmetic, and mapping it to 0 keeps the row's totals well-defined.
        if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
            return 0m;

        return (decimal)number;
    }
}

/// <summary>Turns a "#RRGGBB" string into a brush, for the analytics chart palette.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.Gray);

        hex = hex.TrimStart('#');
        if (hex.Length != 6) return new SolidColorBrush(Colors.Gray);

        try
        {
            return new SolidColorBrush(Color.FromArgb(
                255,
                System.Convert.ToByte(hex[..2], 16),
                System.Convert.ToByte(hex.Substring(2, 2), 16),
                System.Convert.ToByte(hex.Substring(4, 2), 16)));
        }
        catch (Exception)
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when a count is zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps the English domain literals (InvoiceType, CustomerType, theme names — persisted
/// as-is to satisfy DB CHECK constraints and settings storage) to Arabic display labels.
/// Display-only: the underlying bound value never changes.
/// </summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Labels = new()
    {
        ["All"] = "الكل",
        ["Purchase"] = "شراء",
        ["Sale"] = "بيع",
        ["Customer"] = "عميل",
        ["Supplier"] = "مورد",
        ["Default"] = "افتراضي",
        ["Light"] = "فاتح",
        ["Dark"] = "داكن",
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // Accepts either the raw enum (DataGrid bindings) or its string form (ComboBox
        // option lists), since both are used to carry the same English literals.
        var key = value?.ToString();
        return key is not null && Labels.TryGetValue(key, out var label) ? label : key ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
