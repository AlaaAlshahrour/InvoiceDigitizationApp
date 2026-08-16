using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Data;

namespace InvoiceDigitizationApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settings;
    private readonly IAiServiceClient _aiService;
    private readonly IDbConnectionFactory _database;

    public SettingsViewModel(
        ISettingsRepository settings,
        IAiServiceClient aiService,
        IDbConnectionFactory database)
    {
        _settings = settings;
        _aiService = aiService;
        _database = database;
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Default", "Light", "Dark" };

    [ObservableProperty] private string _pythonServiceUrl = "http://127.0.0.1:8765";
    [ObservableProperty] private string? _pythonServiceExecutable;
    [ObservableProperty] private bool _autoStartPythonService;
    [ObservableProperty] private string _exportDirectory = string.Empty;
    [ObservableProperty] private string _imageStorageDirectory = string.Empty;
    [ObservableProperty] private string _selectedTheme = "Default";
    [ObservableProperty] private double _confidenceThreshold = 0.75;

    [ObservableProperty] private string? _serviceStatus;
    [ObservableProperty] private bool _isServiceReachable;

    public string DatabasePath => _database.DatabasePath;

    /// <summary>Raised when the theme changes so the View can apply it to the window.</summary>
    public event EventHandler<string>? ThemeChangeRequested;

    public async Task LoadAsync()
    {
        await RunGuardedAsync(async () =>
        {
            PythonServiceUrl = await _settings.GetOrDefaultAsync(
                SettingKeys.PythonServiceUrl, "http://127.0.0.1:8765");

            PythonServiceExecutable = await _settings.GetAsync(SettingKeys.PythonServiceExecutable);

            AutoStartPythonService = await _settings.GetBoolAsync(
                SettingKeys.AutoStartPythonService, false);

            ExportDirectory = await _settings.GetOrDefaultAsync(
                SettingKeys.ExportDirectory, AppPaths.DefaultExportDirectory);

            ImageStorageDirectory = await _settings.GetOrDefaultAsync(
                SettingKeys.ImageStorageDirectory, AppPaths.DefaultImageDirectory);

            SelectedTheme = await _settings.GetOrDefaultAsync(SettingKeys.AppTheme, "Default");

            ConfidenceThreshold = await _settings.GetDoubleAsync(
                SettingKeys.ConfidenceThreshold, 0.75);

            ClearStatus();
        }, "تعذّر تحميل الإعدادات");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!Uri.TryCreate(PythonServiceUrl, UriKind.Absolute, out _))
        {
            SetError("يجب أن يكون عنوان URL للخدمة رابطًا مطلقًا كاملاً، مثل http://127.0.0.1:8765");
            return;
        }

        if (ConfidenceThreshold is < 0 or > 1)
        {
            SetError("يجب أن يكون حد الثقة بين 0 و1.");
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _settings.SetAsync(SettingKeys.PythonServiceUrl, PythonServiceUrl.Trim());
            await _settings.SetAsync(SettingKeys.PythonServiceExecutable, PythonServiceExecutable?.Trim() ?? string.Empty);
            await _settings.SetAsync(SettingKeys.AutoStartPythonService, AutoStartPythonService.ToString());
            await _settings.SetAsync(SettingKeys.ExportDirectory, ExportDirectory.Trim());
            await _settings.SetAsync(SettingKeys.ImageStorageDirectory, ImageStorageDirectory.Trim());
            await _settings.SetAsync(SettingKeys.AppTheme, SelectedTheme);
            await _settings.SetAsync(
                SettingKeys.ConfidenceThreshold,
                ConfidenceThreshold.ToString("0.##", CultureInfo.InvariantCulture));

            // Create the directories now so an export later cannot fail on a missing path.
            Directory.CreateDirectory(ExportDirectory.Trim());
            Directory.CreateDirectory(ImageStorageDirectory.Trim());

            SetStatus("تم حفظ الإعدادات.");
        }, "تعذّر حفظ الإعدادات");
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        await RunGuardedAsync(async () =>
        {
            ServiceStatus = "جارٍ التحقق…";

            // Probe the URL currently in the box, not the saved one, so the user can
            // test a change before committing to it.
            var saved = await _settings.GetOrDefaultAsync(
                SettingKeys.PythonServiceUrl, "http://127.0.0.1:8765");

            await _settings.SetAsync(SettingKeys.PythonServiceUrl, PythonServiceUrl.Trim());
            try
            {
                var health = await _aiService.GetHealthAsync();

                if (health is null)
                {
                    IsServiceReachable = false;
                    ServiceStatus = "غير قابل للوصول. هل خدمة Python قيد التشغيل؟";
                    SetError("تعذّر الوصول إلى خدمة الاستخراج.");
                    return;
                }

                IsServiceReachable = health.EngineReady;
                ServiceStatus = health.EngineReady
                    ? $"متصل — الإصدار {health.Version}، المحرك '{health.OcrEngine}'، اللغات: {string.Join(", ", health.Languages)}"
                    : $"قابل للوصول، لكن المحرك '{health.OcrEngine}' ما زال قيد التحميل.";

                SetStatus(ServiceStatus);
            }
            finally
            {
                // Restore the saved value; only SaveAsync should persist a change.
                await _settings.SetAsync(SettingKeys.PythonServiceUrl, saved);
            }
        }, "فشل اختبار الاتصال");
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        PythonServiceUrl = "http://127.0.0.1:8765";
        PythonServiceExecutable = null;
        AutoStartPythonService = false;
        ExportDirectory = AppPaths.DefaultExportDirectory;
        ImageStorageDirectory = AppPaths.DefaultImageDirectory;
        SelectedTheme = "Default";
        ConfidenceThreshold = 0.75;

        await SaveAsync();
        SetStatus("تمت إعادة تعيين الإعدادات إلى الافتراضي.");
    }

    partial void OnSelectedThemeChanged(string value) =>
        ThemeChangeRequested?.Invoke(this, value);
}
