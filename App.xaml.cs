using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Batch;
using InvoiceDigitizationApp.Services.Data;
using InvoiceDigitizationApp.Services.Export;
using InvoiceDigitizationApp.Services.Pipeline;
using InvoiceDigitizationApp.Services.Validation;
using InvoiceDigitizationApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace InvoiceDigitizationApp;

public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>Service provider for the whole app. Set before the first window opens.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The shell window. Pages use this to navigate and to parent file pickers and
    /// dialogs — an unpackaged WinUI app has no Window.Current to fall back on.
    /// </summary>
    public MainWindow? MainWindowInstance => _window;

    public App()
    {
        // Arabic-primary product: controls like DatePicker read the thread culture for
        // month/day names, so this must be set before any window is created. ar-SA
        // defaults to the Hijri calendar, which would disagree with the Gregorian
        // DateOnly values stored in the database — force Gregorian explicitly.
        var arabic = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        arabic.DateTimeFormat.Calendar = new GregorianCalendar();
        CultureInfo.DefaultThreadCurrentCulture = arabic;
        CultureInfo.DefaultThreadCurrentUICulture = arabic;
        Thread.CurrentThread.CurrentCulture = arabic;
        Thread.CurrentThread.CurrentUICulture = arabic;

        InitializeComponent();

        // A failure in an async void handler or an unobserved task would otherwise
        // terminate the process with no explanation.
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureCreated();
        DapperTypeHandlers.Register();

        Services = ConfigureServices();

        // The schema must exist before any window binds to a repository. Blocking here
        // is deliberate: it is a few milliseconds against a local file, and letting the
        // UI open against a non-existent database would produce confusing errors.
        Services.GetRequiredService<DatabaseInitializer>()
            .InitializeAsync()
            .GetAwaiter()
            .GetResult();

        _window = new MainWindow();
        _window.Activate();

        // Fire-and-forget on purpose: the service takes the better part of a minute to
        // load its model weights, and the app is fully usable for manual entry and for
        // reviewing saved invoices while it does. Blocking here would hold the window
        // closed for a service the user may not even be about to use.
        _ = StartExtractionServiceAsync();
    }

    /// <summary>
    /// Starts the Python extraction service if the user asked for it, and stops it again
    /// when the window closes.
    /// </summary>
    private async Task StartExtractionServiceAsync()
    {
        var launcher = Services.GetRequiredService<IPythonServiceLauncher>();

        if (_window is not null) _window.Closed += (_, _) => launcher.Stop();

        try
        {
            await launcher.EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            // Never fatal. Without the service the app still opens every screen and saves
            // hand-entered invoices; the health probe already says it is unreachable.
            System.Diagnostics.Debug.WriteLine($"Extraction service did not start: {ex}");
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ---- data ----------------------------------------------------------
        services.AddSingleton<IDbConnectionFactory>(
            _ => new SqliteConnectionFactory(AppPaths.DatabasePath));

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IInvoiceRepository, InvoiceRepository>();
        services.AddSingleton<IProductRepository, ProductRepository>();
        services.AddSingleton<ICustomerRepository, CustomerRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        // ---- services ------------------------------------------------------
        services.AddSingleton<IInvoiceValidationService, InvoiceValidationService>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IExportService, ExportService>();

        // Derives the extraction's warnings here rather than trusting a copy from the
        // service — see docs/api-contract.md, "Warnings".
        services.AddSingleton<IExtractionWarningBuilder, ExtractionWarningBuilder>();

        // OCR on a large scan can legitimately take a while; the default 100s would
        // abort a valid request on a slower machine.
        //
        // Timeout.InfiniteTimeSpan, not a number. The layout-driven flow crops and reads
        // every cell of a handwritten invoice through a VLM on the CPU, and the first
        // request of a session also pays for loading ~1.7 GB of weights — a
        // wall-clock cost that depends on the machine and on the invoice, and that any
        // fixed ceiling eventually guesses too low. When it did, HttpClient cancelled a
        // request the service went on to complete: the app reported "فشلت العملية" for
        // an invoice that had in fact been extracted, which is the worst of both
        // outcomes. Cancellation is driven by the CancellationToken the caller passes
        // instead — the batch's cancel button — so the user is still the one who decides
        // when an extraction has gone on too long, and can see the progress it made.
        services.AddHttpClient<IAiServiceClient, HttpAiServiceClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // The batch service is a singleton but the client above is transient over a
        // handler the factory rotates. Handing it a factory instead of an instance lets
        // it resolve a fresh client per file rather than pinning one handler for the
        // life of the process.
        services.AddSingleton<Func<IAiServiceClient>>(provider =>
            provider.GetRequiredService<IAiServiceClient>);

        // Singleton, unlike the ViewModels below: the navigation Frame does not cache
        // pages, so a batch owned by the verification screen would be destroyed the
        // moment the user stepped over to another page mid-review.
        services.AddSingleton<IInvoiceBatchService, InvoiceBatchService>();

        // Transient, because it resolves IAiServiceClient — which the HTTP factory
        // rotates — to read the service's defaults.
        services.AddTransient<IPipelineConfigurationStore, PipelineConfigurationStore>();

        // Singleton: it owns the process handle, and a second instance would start a
        // second service against the same port.
        services.AddSingleton<IPythonServiceLauncher, PythonServiceLauncher>();

        // ---- view models ---------------------------------------------------
        // Transient: each navigation gets a clean screen with no leftover state.
        services.AddTransient<InvoicesDashboardViewModel>();
        services.AddTransient<ProcessingViewModel>();
        services.AddTransient<ProductsViewModel>();
        services.AddTransient<CustomersViewModel>();
        services.AddTransient<AnalyticsViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Marking it handled keeps the app alive; the user keeps whatever unsaved work
        // is on screen rather than losing it to a hard crash.
        e.Handled = true;

        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");

        _window?.ShowErrorBanner(e.Message);
    }
}
