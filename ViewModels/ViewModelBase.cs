using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InvoiceDigitizationApp.ViewModels;

/// <summary>
/// Shared state for screen ViewModels: a busy flag and a single status/error line.
/// Deliberately free of WinUI types so ViewModels can be unit-tested headlessly.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    /// <summary>
    /// The inverse of <see cref="IsBusy"/>, for binding a toolbar's IsEnabled.
    /// </summary>
    /// <remarks>
    /// <see cref="RunGuardedAsync"/> refuses to start while other work is running and
    /// returns silently, which during a long batch made every command on screen look
    /// broken — clicking did nothing and said nothing. Greying the controls out is that
    /// refusal made visible before the click.
    /// </remarks>
    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasError;

    protected void SetStatus(string message)
    {
        HasError = false;
        StatusMessage = message;
    }

    protected void SetError(string message)
    {
        HasError = true;
        StatusMessage = message;
    }

    protected void ClearStatus()
    {
        HasError = false;
        StatusMessage = null;
    }

    /// <summary>
    /// Runs work with the busy flag set, turning any exception into an error message
    /// rather than an unhandled crash. Returns false when the work threw.
    /// </summary>
    protected async Task<bool> RunGuardedAsync(Func<Task> work, string? failureContext = null)
    {
        if (IsBusy) return false;

        IsBusy = true;
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a user action, not a failure — but it still has to be
            // acknowledged. Swallowing it silently left the status line showing "جارٍ
            // المعالجة…" forever, so the screen looked hung rather than stopped.
            SetStatus("تم إلغاء العملية.");
            return false;
        }
        catch (Exception ex)
        {
            SetError(failureContext is null ? ex.Message : $"{failureContext}: {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
