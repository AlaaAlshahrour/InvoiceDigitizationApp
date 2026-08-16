using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceDigitizationApp.Services.Pipeline;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// One editable parameter on the settings page. Adapts a loosely-typed JSON value to the
/// strongly-typed properties a XAML control can bind to, and back again on save.
/// </summary>
/// <remarks>
/// The typed properties are all present regardless of <see cref="Kind"/>; the View shows
/// whichever control the kind calls for and ignores the rest. That keeps the parameter
/// form data-driven, so adding an algorithm to
/// <see cref="Services.Pipeline.PipelineCatalog"/> needs no XAML change at all.
/// </remarks>
public partial class PipelineParameterViewModel : ObservableObject
{
    private readonly PipelineParameter _definition;

    public PipelineParameterViewModel(PipelineParameter definition, JsonNode? value)
    {
        _definition = definition;

        Choices = definition.Choices ?? Array.Empty<string>();

        // A saved configuration may predate a parameter, or carry a value of the wrong
        // shape after a contract change. Either way the documented default is the right
        // thing to fall back to rather than refusing to render the form.
        Load(value);
    }

    public string Key => _definition.Key;
    public string Label => _definition.Label;
    public string? Hint => _definition.Hint;
    public ParameterKind Kind => _definition.Kind;

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    public double Minimum => _definition.Minimum;
    public double Maximum => _definition.Maximum;

    /// <summary>NumberBox steps by 2 for odd-only values, so it can never land on an even one.</summary>
    public double SmallChange => Kind == ParameterKind.OddInteger ? 2 : 1;

    public IReadOnlyList<string> Choices { get; }

    public string SecondLabel => _definition.SecondLabel ?? "الثاني";

    // ---- kind flags, for the View's template selection ---------------------

    public bool IsNumeric =>
        Kind is ParameterKind.Integer or ParameterKind.OddInteger or ParameterKind.Decimal;

    public bool IsChoice => Kind == ParameterKind.Choice;
    public bool IsPair => Kind == ParameterKind.IntegerPair;
    public bool IsBoolean => Kind == ParameterKind.Boolean;

    // ---- values ------------------------------------------------------------

    [ObservableProperty] private double _numberValue;
    [ObservableProperty] private double _secondNumberValue;
    [ObservableProperty] private string _choiceValue = string.Empty;
    [ObservableProperty] private bool _booleanValue;

    private void Load(JsonNode? value)
    {
        switch (Kind)
        {
            case ParameterKind.Choice:
                var stored = TryString(value);
                ChoiceValue = stored is { Length: > 0 } && Choices.Any(c => c == stored)
                    ? stored
                    : _definition.DefaultValue?.ToString() ?? string.Empty;
                break;

            case ParameterKind.Boolean:
                BooleanValue = TryBool(value) ?? Convert.ToBoolean(_definition.DefaultValue ?? false);
                break;

            case ParameterKind.IntegerPair:
                var pair = value as JsonArray;
                NumberValue = TryNumber(pair?.Count > 0 ? pair[0] : null)
                              ?? ToDouble(_definition.DefaultValue);
                SecondNumberValue = TryNumber(pair?.Count > 1 ? pair[1] : null)
                                    ?? ToDouble(_definition.SecondDefaultValue ?? _definition.DefaultValue);
                break;

            default:
                NumberValue = TryNumber(value) ?? ToDouble(_definition.DefaultValue);
                break;
        }
    }

    /// <summary>The value as it goes back on the wire, in the type the service expects.</summary>
    public JsonNode? ToJson() => Kind switch
    {
        ParameterKind.Choice => JsonValue.Create(ChoiceValue),
        ParameterKind.Boolean => JsonValue.Create(BooleanValue),
        ParameterKind.IntegerPair => new JsonArray(
            JsonValue.Create(ClampToInt(NumberValue)),
            JsonValue.Create(ClampToInt(SecondNumberValue))),

        // Integers must serialize as JSON integers, not as 51.0: the Python steps use
        // them as OpenCV kernel sizes and array indices, where a float is a type error.
        ParameterKind.Integer => JsonValue.Create(ClampToInt(NumberValue)),
        ParameterKind.OddInteger => JsonValue.Create(MakeOdd(ClampToInt(NumberValue))),

        _ => JsonValue.Create(Math.Clamp(NumberValue, Minimum, Maximum))
    };

    /// <summary>Restores the documented default for this one parameter.</summary>
    public void ResetToDefault() => Load(null);

    private int ClampToInt(double value) =>
        (int)Math.Round(Math.Clamp(value, Minimum, Maximum), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Nudges an even value up to the next odd one. The NumberBox steps by two, but a
    /// user can still type an even number straight into it, and the service rejects the
    /// whole configuration over it.
    /// </summary>
    private int MakeOdd(int value)
    {
        if (value % 2 != 0) return value;

        var raised = value + 1;
        return raised <= Maximum ? raised : value - 1;
    }

    private static double ToDouble(object? value) =>
        value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static double? TryNumber(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? TryString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool? TryBool(JsonNode? node)
    {
        try
        {
            return node?.GetValue<bool>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
