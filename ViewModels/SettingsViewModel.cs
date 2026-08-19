using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// The reading strategy, as a single card. Its own collection rather than the first
    /// entry of <see cref="RecognitionStages"/> because changing it rebuilds that list:
    /// which OCR components exist at all is the flow's decision.
    /// </summary>
    public ObservableCollection<PipelineStepViewModel> ReadingStrategy { get; } = new();

    /// <summary>
    /// The stages the selected flow actually uses, plus table extraction, its classifier,
    /// and keyword matching — one choice each.
    /// </summary>
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
        ReadingStrategy.Clear();

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

        var flow = new PipelineStepViewModel(
            PipelineCatalog.FlowStage,
            "طريقة القراءة",
            "كيف تُقرأ الصفحة. هذا الاختيار يحدّد أي مكوّنات التعرّف تظهر أدناه أصلاً.",
            PipelineCatalog.Flows,
            _configuration.Flow.Name,
            _configuration.Flow.Params,
            enabled: true,
            canDisable: false);

        // Rebuild the stages below whenever the strategy changes: a component the new flow
        // does not use is not merely irrelevant, it is never built, and leaving its card on
        // screen would invite tuning something with no effect.
        flow.PropertyChanged += OnFlowChanged;
        ReadingStrategy.Add(flow);

        BuildRecognitionStages();
    }

    private void OnFlowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineStepViewModel.SelectedAlgorithm))
            BuildRecognitionStages();
    }

    /// <summary>The current flow's name, from the card if it is built and the configuration otherwise.</summary>
    private string SelectedFlowName =>
        ReadingStrategy.FirstOrDefault()?.SelectedName ?? _configuration.Flow.Name;

    /// <summary>
    /// Builds one card per stage the selected flow uses, then the table and matching
    /// stages every flow shares.
    /// </summary>
    private void BuildRecognitionStages()
    {
        // Keep whatever the user had typed into the cards being replaced, so switching
        // flow and back does not silently reset a recognizer's parameters.
        CollectRecognitionStages();
        RecognitionStages.Clear();

        foreach (var stage in PipelineCatalog.OcrStagesFor(SelectedFlowName))
        {
            var card = stage switch
            {
                PipelineCatalog.OcrStage => Card(
                    stage, "محرك التعرّف الضوئي",
                    "البرنامج الذي يقرأ نص الفاتورة من الصورة كاملة.",
                    PipelineCatalog.OcrEngines,
                    _configuration.Ocr.Engine, _configuration.Ocr.EngineParams),

                PipelineCatalog.DetectorStage => ComponentCard(
                    stage, "كاشف النص",
                    "يجد مواضع الحبر في الصفحة قبل قراءتها.",
                    PipelineCatalog.Detectors, _configuration.Ocr.Detector),

                PipelineCatalog.RefinerStage => ComponentCard(
                    stage, "تقويم المربّعات",
                    "يقوّم مربّعات الكاشف على حدود الأعمدة.",
                    PipelineCatalog.Refiners, _configuration.Ocr.Refiner),

                PipelineCatalog.CropperStage => ComponentCard(
                    stage, "اقتطاع الخانات",
                    "يقتطع كل خانة من الصفحة ويهيّئها للقراءة.",
                    PipelineCatalog.Croppers, _configuration.Ocr.Cropper),

                _ => ComponentCard(
                    PipelineCatalog.RecognizerStage, "قارئ الخانات",
                    "يقرأ خانةً واحدة، بالمطالبة التي يقتضيها دورها في الفاتورة.",
                    PipelineCatalog.Recognizers, _configuration.Ocr.Recognizer)
            };

            RecognitionStages.Add(card);
        }

        RecognitionStages.Add(Card(
            PipelineCatalog.TableExtractionStage,
            "استخراج الجدول",
            "يحدّد خلايا جدول البنود ليُقرأ كل عمود على حدة.",
            PipelineCatalog.TableExtractors,
            _configuration.TableExtraction.Extractor,
            _configuration.TableExtraction.ExtractorParams));

        RecognitionStages.Add(Card(
            PipelineCatalog.TableClassifierStage,
            "تصنيف الخانات",
            "يحدّد ما تعنيه كل خانة: رقم الفاتورة، اسم الزبون، عمود الكمية… بدونه تبقى الأدوار مجهولة.",
            PipelineCatalog.TableClassifiers,
            _configuration.TableExtraction.Classifier,
            _configuration.TableExtraction.ClassifierParams));

        RecognitionStages.Add(Card(
            PipelineCatalog.StringMatchingStage,
            "مطابقة النصوص بقاموس الكلمات",
            "تصحيح المصطلحات الشائعة في الفاتورة. مطابقة أسماء الزبائن والمنتجات بقاعدة البيانات تجري دائمًا وليست من ضمن هذا الخيار.",
            PipelineCatalog.StringMatchers,
            _configuration.StringMatching.Algorithm,
            _configuration.StringMatching.AlgorithmParams));
    }

    private static PipelineStepViewModel Card(
        string key,
        string label,
        string description,
        IReadOnlyList<PipelineAlgorithm> algorithms,
        string? selected,
        Dictionary<string, JsonNode?>? parameters) =>
        new(key, label, description, algorithms,
            selected ?? algorithms[0].Name,
            parameters ?? PipelineConfigurationStore.DefaultParams(algorithms[0]),
            enabled: true,
            canDisable: false);

    /// <summary>
    /// A card for a nullable component. Null means the configuration was written for a
    /// flow that does not use this stage, so the catalog's recommended entry is what the
    /// card opens on.
    /// </summary>
    private static PipelineStepViewModel ComponentCard(
        string key,
        string label,
        string description,
        IReadOnlyList<PipelineAlgorithm> algorithms,
        ComponentConfiguration? component) =>
        Card(key, label, description, algorithms, component?.Name, component?.Params);

    /// <summary>
    /// Writes the recognition cards back into <see cref="_configuration"/>, so their values
    /// survive a rebuild of the list.
    /// </summary>
    private void CollectRecognitionStages()
    {
        foreach (var stage in RecognitionStages)
        {
            switch (stage.Key)
            {
                case PipelineCatalog.OcrStage:
                    _configuration.Ocr.Engine = stage.SelectedName;
                    _configuration.Ocr.EngineParams = stage.ToParams();
                    break;

                case PipelineCatalog.DetectorStage:
                    _configuration.Ocr.Detector = ToComponent(stage);
                    break;

                case PipelineCatalog.RefinerStage:
                    _configuration.Ocr.Refiner = ToComponent(stage);
                    break;

                case PipelineCatalog.CropperStage:
                    _configuration.Ocr.Cropper = ToComponent(stage);
                    break;

                case PipelineCatalog.RecognizerStage:
                    _configuration.Ocr.Recognizer = ToComponent(stage);
                    break;

                case PipelineCatalog.TableExtractionStage:
                    _configuration.TableExtraction.Extractor = stage.SelectedName;
                    _configuration.TableExtraction.ExtractorParams = stage.ToParams();
                    break;

                case PipelineCatalog.TableClassifierStage:
                    _configuration.TableExtraction.Classifier = stage.SelectedName;
                    _configuration.TableExtraction.ClassifierParams = stage.ToParams();
                    break;

                case PipelineCatalog.StringMatchingStage:
                    _configuration.StringMatching.Algorithm = stage.SelectedName;
                    _configuration.StringMatching.AlgorithmParams = stage.ToParams();
                    break;
            }
        }
    }

    private static ComponentConfiguration ToComponent(PipelineStepViewModel stage) => new()
    {
        Name = stage.SelectedName,
        Params = stage.ToParams()
    };

    /// <summary>Collects the cards back into the configuration object.</summary>
    private PipelineConfiguration BuildConfiguration()
    {
        foreach (var step in Phases.SelectMany(phase => phase.Steps))
            _configuration.Preprocessing.Set(step.Key, step.ToStep());

        _configuration.Flow.Name = SelectedFlowName;
        _configuration.Flow.Params =
            ReadingStrategy.FirstOrDefault()?.ToParams() ?? new Dictionary<string, JsonNode?>();

        CollectRecognitionStages();

        // The components the selected flow does not use are cleared rather than left
        // behind. A stale detector under layout_driven is not merely unused — it is a
        // value the settings page no longer shows and nobody can see to correct.
        var used = PipelineCatalog.OcrStagesFor(SelectedFlowName);

        if (!used.Contains(PipelineCatalog.OcrStage)) _configuration.Ocr.Engine = null;
        if (!used.Contains(PipelineCatalog.DetectorStage)) _configuration.Ocr.Detector = null;
        if (!used.Contains(PipelineCatalog.RefinerStage)) _configuration.Ocr.Refiner = null;
        if (!used.Contains(PipelineCatalog.CropperStage)) _configuration.Ocr.Cropper = null;
        if (!used.Contains(PipelineCatalog.RecognizerStage)) _configuration.Ocr.Recognizer = null;

        // Cloned so the page keeps editing its own copy: saving must not hand the store an
        // object the cards go on mutating.
        return _configuration.Clone();
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
                await _pipeline.SaveAsync(BuildConfiguration());
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
