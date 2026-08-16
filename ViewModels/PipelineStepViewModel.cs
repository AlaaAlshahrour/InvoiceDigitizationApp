using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Pipeline;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// One card on the settings page: an enable toggle, an algorithm picker when there is a
/// choice, and a parameter form that swaps when the algorithm changes.
/// </summary>
/// <remarks>
/// Used for both the eight fixed preprocessing steps and the single-choice stages (OCR,
/// table extraction, string matching) — the shape is identical, which is why the same
/// card renders all of them and adding a second OCR engine later is a catalog entry
/// rather than a redesign.
/// </remarks>
public partial class PipelineStepViewModel : ObservableObject
{
    private readonly IReadOnlyList<PipelineAlgorithm> _algorithms;

    /// <summary>
    /// Parameters as they were when this step was loaded, keyed by algorithm. Switching
    /// algorithms and switching back restores what the user had typed instead of
    /// resetting it to the defaults.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> _savedParams = new();

    /// <summary>Set while the algorithm is being applied, so the change handler does
    /// not rebuild the form a second time from a half-updated state.</summary>
    private bool _isApplying;

    public PipelineStepViewModel(
        string key,
        string label,
        string description,
        IReadOnlyList<PipelineAlgorithm> algorithms,
        string selectedAlgorithm,
        Dictionary<string, JsonNode?> parameters,
        bool enabled = true,
        bool canDisable = true)
    {
        Key = key;
        Label = label;
        Description = description;
        CanDisable = canDisable;

        _algorithms = algorithms;

        // Not-implemented algorithms are shown but never selectable: the service refuses
        // them at validation time, so letting one be submitted would fail the extraction
        // rather than the save.
        Algorithms = new ObservableCollection<PipelineAlgorithm>(algorithms);

        _isEnabled = enabled;
        _savedParams[selectedAlgorithm] = parameters;

        _selectedAlgorithm =
            algorithms.FirstOrDefault(a => a.Name == selectedAlgorithm && a.IsImplemented)
            ?? algorithms.First(a => a.IsImplemented);

        BuildParameters();
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }

    /// <summary>
    /// False for the steps every later step depends on. The toggle is still shown — the
    /// contract asks for one on every step — but flipping it off is discouraged in the UI.
    /// </summary>
    public bool CanDisable { get; }

    public ObservableCollection<PipelineAlgorithm> Algorithms { get; }

    public ObservableCollection<PipelineParameterViewModel> Parameters { get; } = new();

    public bool HasAlgorithmChoice => Algorithms.Count(a => a.IsImplemented) > 1;

    public bool HasParameters => Parameters.Count > 0;

    /// <summary>Shown in place of the form when the selected algorithm takes no settings.</summary>
    public string NoParametersHint =>
        SelectedAlgorithm?.Hint ?? "لا توجد إعدادات لهذه الخوارزمية.";

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty] private PipelineAlgorithm? _selectedAlgorithm;

    partial void OnSelectedAlgorithmChanged(PipelineAlgorithm? value)
    {
        if (_isApplying || value is null) return;

        if (!value.IsImplemented)
        {
            // Bounce the selection back. The option is visible so the user can see the
            // algorithm exists, but the service would reject a configuration naming it.
            _isApplying = true;
            SelectedAlgorithm = _algorithms.First(a => a.IsImplemented);
            _isApplying = false;
            return;
        }

        BuildParameters();
    }

    private void BuildParameters()
    {
        // Remember what the outgoing algorithm's form held, so switching back restores it.
        if (Parameters.Count > 0 && _lastBuiltAlgorithm is { } previous)
            _savedParams[previous] = CollectParameters();

        Parameters.Clear();

        var algorithm = SelectedAlgorithm;
        if (algorithm is null) return;

        _savedParams.TryGetValue(algorithm.Name, out var stored);

        foreach (var parameter in algorithm.Parameters)
        {
            var saved = stored is not null && stored.TryGetValue(parameter.Key, out var value)
                ? value
                : null;

            Parameters.Add(new PipelineParameterViewModel(parameter, saved));
        }

        _lastBuiltAlgorithm = algorithm.Name;

        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(NoParametersHint));
    }

    private string? _lastBuiltAlgorithm;

    private Dictionary<string, JsonNode?> CollectParameters()
    {
        var values = new Dictionary<string, JsonNode?>(Parameters.Count);
        foreach (var parameter in Parameters) values[parameter.Key] = parameter.ToJson();
        return values;
    }

    /// <summary>This card's current state, in the shape the contract sends.</summary>
    public PipelineStep ToStep() => new()
    {
        Enabled = IsEnabled,
        Algorithm = SelectedAlgorithm?.Name ?? Algorithms.First(a => a.IsImplemented).Name,
        Params = CollectParameters()
    };

    /// <summary>The selected algorithm's name, for the single-choice stages.</summary>
    public string SelectedName =>
        SelectedAlgorithm?.Name ?? Algorithms.First(a => a.IsImplemented).Name;

    /// <summary>The parameter values alone, for stages that serialize them separately.</summary>
    public Dictionary<string, JsonNode?> ToParams() => CollectParameters();

    /// <summary>Restores every parameter of the current algorithm to its default.</summary>
    public void ResetParameters()
    {
        foreach (var parameter in Parameters) parameter.ResetToDefault();
    }
}
