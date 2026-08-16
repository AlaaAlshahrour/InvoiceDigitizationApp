using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceDigitizationApp.Services.AiServiceClient;
using InvoiceDigitizationApp.Services.Data;
using InvoiceDigitizationApp.Services.Pipeline;

namespace InvoiceDigitizationApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settings;
    private readonly IAiServiceClient _aiService;
    private readonly IDbConnectionFactory _database;
    private readonly IPipelineConfigurationStore _pipeline;

    /// <summary>
    /// The configuration being edited. Held so the parts the settings page does not
    /// expose — the keyword dictionary path, the output formatter, the results
    /// directory — are round-tripped rather than dropped on save.
    /// </summary>
    private PipelineConfiguration _configuration =
        PipelineConfigurationStore.BuildDefault();

    public SettingsViewModel(
        ISettingsRepository settings,
        IAiServiceClient aiService,
        IDbConnectionFactory database,
        IPipelineConfigurationStore pipeline)
    {
        _settings = settings;
        _aiService = aiService;
        _database = database;
        _pipeline = pipeline;
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Default", "Light", "Dark" };

    [ObservableProperty] private string _pythonServiceUrl = "http://127.0.0.1:8765";
    [ObservableProperty] private string? _pythonServiceExecutable;
    [ObservableProperty] private bool _autoStartPythonService;
    [ObservableProperty] private string _exportDirectory = string.Empty;
    [ObservableProperty] private string _imageStorageDirectory = string.Empty;
    [ObservableProperty] private string _selectedTheme = "Default";
    [ObservableProperty] private double _confidenceThreshold = 0.75;

    /// <summary>
    /// How many alternatives each matched field offers. Five is what the matching spec
    /// asks for and what fits a dropdown without scrolling.
    /// </summary>
    [ObservableProperty] private int _maxMatchCandidates = 5;

    [ObservableProperty] private string? _serviceStatus;
    [ObservableProperty] private bool _isServiceReachable;

    // ---- extraction pipeline ----------------------------------------------

    /// <summary>The three preprocessing phases, each holding its step cards.</summary>
    public ObservableCollection<PipelinePhaseViewModel> Phases { get; } = new();

    /// <summary>OCR, table extraction and keyword matching — one choice each.</summary>
    public ObservableCollection<PipelineStepViewModel> RecognitionStages { get; } = new();

    /// <summary>True once the pipeline cards have been built, so the page can show them.</summary>
    [ObservableProperty] private bool _isPipelineLoaded;

    [ObservableProperty] private string? _pipelineSource;

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

            MaxMatchCandidates = (int)await _settings.GetDoubleAsync(
                SettingKeys.MaxMatchCandidates, 5);

            await LoadPipelineAsync();

            ClearStatus();
        }, "تعذّر تحميل الإعدادات");
    }

    /// <summary>
    /// Builds the pipeline cards from the saved configuration, or from the service's own
    /// defaults when nothing has been saved yet.
    /// </summary>
    private async Task LoadPipelineAsync()
    {
        var saved = await _pipeline.GetAsync();

        if (saved is not null)
        {
            _configuration = saved;
            PipelineSource = "الإعدادات المحفوظة على هذا الجهاز.";
        }
        else
        {
            var fromService = await _aiService.GetDefaultConfigurationAsync();
            _configuration = fromService ?? PipelineConfigurationStore.BuildDefault();

            PipelineSource = fromService is not null
                ? "الإعدادات الافتراضية كما تشغّلها الخدمة."
                : "تعذّر الوصول إلى الخدمة؛ هذه هي القيم الافتراضية الموثّقة.";
        }

        BuildPipelineCards();
        IsPipelineLoaded = true;
    }

    private void BuildPipelineCards()
    {
        Phases.Clear();
        RecognitionStages.Clear();

        var steps = _configuration.Preprocessing.ByKey();

        foreach (var phase in PipelineCatalog.Steps.GroupBy(step => step.Phase))
        {
            var cards = phase
                .Select(definition =>
                {
                    steps.TryGetValue(definition.Key, out var stored);

                    return new PipelineStepViewModel(
                        definition.Key,
                        definition.Label,
                        definition.Description,
                        definition.Algorithms,
                        stored?.Algorithm ?? definition.Algorithms[0].Name,
                        stored?.Params ?? PipelineConfigurationStore.DefaultParams(definition.Algorithms[0]),
                        stored?.Enabled ?? true,
                        // Every later step expects a single-channel image, so turning
                        // this one off breaks the rest of the pipeline.
                        canDisable: definition.Key != "channel_selection");
                })
                .ToList();

            Phases.Add(new PipelinePhaseViewModel(phase.Key, cards));
        }

        RecognitionStages.Add(new PipelineStepViewModel(
            "ocr",
            "محرك التعرّف الضوئي",
            "البرنامج الذي يقرأ نص الفاتورة من الصورة.",
            PipelineCatalog.OcrEngines,
            _configuration.Ocr.Engine,
            _configuration.Ocr.EngineParams,
            enabled: true,
            canDisable: false));

        RecognitionStages.Add(new PipelineStepViewModel(
            "table_extraction",
            "استخراج الجدول",
            "يحدّد خلايا جدول البنود ليُقرأ كل عمود على حدة.",
            PipelineCatalog.TableExtractors,
            _configuration.TableExtraction.Extractor,
            _configuration.TableExtraction.ExtractorParams,
            enabled: true,
            canDisable: false));

        RecognitionStages.Add(new PipelineStepViewModel(
            "string_matching",
            "مطابقة النصوص بقاموس الكلمات",
            "تصحيح المصطلحات الشائعة في الفاتورة. مطابقة أسماء الزبائن والمنتجات بقاعدة البيانات تجري دائمًا وليست من ضمن هذا الخيار.",
            PipelineCatalog.StringMatchers,
            _configuration.StringMatching.Algorithm,
            _configuration.StringMatching.AlgorithmParams,
            enabled: true,
            canDisable: false));
    }

    /// <summary>Collects the cards back into the configuration object.</summary>
    private PipelineConfiguration BuildConfiguration()
    {
        // Cloned so the parts the page does not expose survive untouched.
        var configuration = _configuration.Clone();

        foreach (var step in Phases.SelectMany(phase => phase.Steps))
            configuration.Preprocessing.Set(step.Key, step.ToStep());

        foreach (var stage in RecognitionStages)
        {
            switch (stage.Key)
            {
                case "ocr":
                    configuration.Ocr.Engine = stage.SelectedName;
                    configuration.Ocr.EngineParams = stage.ToParams();
                    break;

                case "table_extraction":
                    configuration.TableExtraction.Extractor = stage.SelectedName;
                    configuration.TableExtraction.ExtractorParams = stage.ToParams();
                    break;

                case "string_matching":
                    configuration.StringMatching.Algorithm = stage.SelectedName;
                    configuration.StringMatching.AlgorithmParams = stage.ToParams();
                    break;
            }
        }

        return configuration;
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

        if (MaxMatchCandidates is < 1 or > 25)
        {
            SetError("يجب أن يكون عدد الاقتراحات بين 1 و25.");
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
            await _settings.SetAsync(
                SettingKeys.MaxMatchCandidates,
                MaxMatchCandidates.ToString(CultureInfo.InvariantCulture));

            if (IsPipelineLoaded)
            {
                _configuration = BuildConfiguration();
                await _pipeline.SaveAsync(_configuration);
                PipelineSource = "الإعدادات المحفوظة على هذا الجهاز.";
            }

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

    /// <summary>
    /// Replaces the edited pipeline with the service's current defaults. Kept separate
    /// from the general reset: a user tuning preprocessing wants to start over on that
    /// without losing their service URL and export folder.
    /// </summary>
    [RelayCommand]
    private async Task ReloadPipelineDefaultsAsync()
    {
        await RunGuardedAsync(async () =>
        {
            var fromService = await _aiService.GetDefaultConfigurationAsync();
            _configuration = fromService ?? PipelineConfigurationStore.BuildDefault();

            PipelineSource = fromService is not null
                ? "الإعدادات الافتراضية كما تشغّلها الخدمة."
                : "تعذّر الوصول إلى الخدمة؛ هذه هي القيم الافتراضية الموثّقة.";

            BuildPipelineCards();
            SetStatus("تمت استعادة إعدادات المعالجة الافتراضية. اضغط حفظ لاعتمادها.");
        }, "تعذّر تحميل الإعدادات الافتراضية للمعالجة");
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
        MaxMatchCandidates = 5;

        _configuration = PipelineConfigurationStore.BuildDefault();
        BuildPipelineCards();
        PipelineSource = "القيم الافتراضية الموثّقة.";

        await SaveAsync();
        SetStatus("تمت إعادة تعيين الإعدادات إلى الافتراضي.");
    }

    partial void OnSelectedThemeChanged(string value) =>
        ThemeChangeRequested?.Invoke(this, value);
}
