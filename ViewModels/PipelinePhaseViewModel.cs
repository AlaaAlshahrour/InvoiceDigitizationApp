using System.Collections.Generic;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// A group of steps on the settings page. Purely a UI convenience for readability: the
/// service fixes execution order itself, and the JSON the app sends is flat.
/// </summary>
public sealed class PipelinePhaseViewModel
{
    public PipelinePhaseViewModel(string title, IReadOnlyList<PipelineStepViewModel> steps)
    {
        Title = title;
        Steps = steps;
    }

    public string Title { get; }

    public IReadOnlyList<PipelineStepViewModel> Steps { get; }
}
