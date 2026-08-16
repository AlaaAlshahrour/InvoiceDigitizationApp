using System;
using System.Collections.Generic;
using System.Linq;

namespace InvoiceDigitizationApp.Services.Pipeline;

/// <summary>How a parameter is edited, which decides the control the settings page shows.</summary>
public enum ParameterKind
{
    /// <summary>Whole number, edited with a stepper.</summary>
    Integer,

    /// <summary>Whole number restricted to odd values (an OpenCV kernel size).</summary>
    OddInteger,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>One of a fixed set of strings, edited with a dropdown.</summary>
    Choice,

    /// <summary>A pair of whole numbers, edited with two steppers.</summary>
    IntegerPair,

    /// <summary>On/off.</summary>
    Boolean
}

/// <summary>
/// One editable parameter of one algorithm: what it is called on the wire, what it
/// means, and the bounds the service will reject if crossed.
/// </summary>
/// <remarks>
/// The constraints here mirror the ones the Python steps validate. Enforcing them in the
/// UI is not a substitute for the service's own validation — it is what turns "the
/// service rejected your configuration" into a control the user cannot get wrong.
/// </remarks>
public sealed record PipelineParameter(
    string Key,
    string Label,
    ParameterKind Kind,
    object? DefaultValue,
    double Minimum = double.MinValue,
    double Maximum = double.MaxValue,
    string? Hint = null,
    IReadOnlyList<string>? Choices = null,
    object? SecondDefaultValue = null,
    string? SecondLabel = null);

/// <summary>One algorithm that can implement a step, and the parameters it accepts.</summary>
public sealed record PipelineAlgorithm(
    string Name,
    string Label,
    IReadOnlyList<PipelineParameter> Parameters,
    string? Hint = null,

    /// <summary>
    /// Registered in the service but deliberately unbuilt. Offered greyed out rather
    /// than hidden, so the user can see the option exists — the service refuses it at
    /// validation time, so it must never actually be submitted.
    /// </summary>
    bool IsImplemented = true);

/// <summary>One of the eight fixed preprocessing steps.</summary>
public sealed record PipelineStepDefinition(
    string Key,
    string Label,
    string Phase,
    string Description,
    IReadOnlyList<PipelineAlgorithm> Algorithms)
{
    /// <summary>True when there is nothing to choose and only a parameter form to show.</summary>
    public bool HasAlgorithmChoice => Algorithms.Count(a => a.IsImplemented) > 1;

    public PipelineAlgorithm? Find(string? name) =>
        Algorithms.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The schema the settings page renders itself from: every step, every algorithm, every
/// parameter with its default and its bounds.
/// </summary>
/// <remarks>
/// This is the C# transcription of sections 2–4 of docs/settings-config-contract.md.
/// Adding an algorithm to the service means adding an entry here — the settings page
/// itself has no per-step code and does not change.
/// </remarks>
public static class PipelineCatalog
{
    public const string PhaseOne = "المرحلة ١ — تجهيز المستند";
    public const string PhaseTwo = "المرحلة ٢ — توحيد سطح الصورة";
    public const string PhaseThree = "المرحلة ٣ — التحويل الثنائي والتنظيف";

    /// <summary>The eight steps, in the order the service executes them.</summary>
    public static IReadOnlyList<PipelineStepDefinition> Steps { get; } = new[]
    {
        new PipelineStepDefinition(
            "perspective_correction",
            "تصحيح المنظور",
            PhaseOne,
            "يقتطع الفاتورة من الخلفية ويصحّح الميلان الناتج عن تصويرها بزاوية.",
            new[]
            {
                new PipelineAlgorithm("perspective_correction", "تصحيح المنظور", new[]
                {
                    new PipelineParameter("canny_low", "الحد الأدنى لكشف الحواف",
                        ParameterKind.Integer, 30, 1, 500,
                        "يجب أن يكون أقل من الحد الأعلى."),
                    new PipelineParameter("canny_high", "الحد الأعلى لكشف الحواف",
                        ParameterKind.Integer, 150, 1, 1000,
                        "يجب أن يكون أعلى من الحد الأدنى.")
                })
            }),

        new PipelineStepDefinition(
            "deskew",
            "تعديل الميلان",
            PhaseOne,
            "يدوّر الصفحة حتى تصبح سطورها أفقية.",
            new[]
            {
                new PipelineAlgorithm("deskew_hough", "تحويل هَف للخطوط", new[]
                {
                    new PipelineParameter("hough_threshold", "عتبة هَف",
                        ParameterKind.Integer, 100, 1, 1000),
                    new PipelineParameter("min_line_length", "أقصر خط معتبَر",
                        ParameterKind.Integer, 150, 1, 2000),
                    new PipelineParameter("max_line_gap", "أكبر فجوة داخل الخط",
                        ParameterKind.Integer, 10, 0, 500),
                    new PipelineParameter("max_angle_deg", "أقصى زاوية تصحيح (درجة)",
                        ParameterKind.Decimal, 20.0, 0.1, 90)
                }),
                new PipelineAlgorithm("deskew_min_area_rect",
                    "أصغر مستطيل محيط (غير متاح بعد)",
                    Array.Empty<PipelineParameter>(),
                    "لم يُنفَّذ في الخدمة بعد.",
                    IsImplemented: false)
            }),

        new PipelineStepDefinition(
            "channel_selection",
            "اختيار القناة اللونية",
            PhaseTwo,
            "يحوّل الصورة إلى قناة واحدة. كل الخطوات التالية تتوقع صورة رمادية، لذا يُنصح بإبقائه مفعّلاً.",
            new[]
            {
                new PipelineAlgorithm("channel_selection", "اختيار القناة", new[]
                {
                    new PipelineParameter("channel", "القناة",
                        ParameterKind.Choice, "gray",
                        Choices: new[] { "gray", "b", "g", "r" })
                })
            }),

        new PipelineStepDefinition(
            "illumination_normalization",
            "توحيد الإضاءة",
            PhaseTwo,
            "يزيل الظلال والبقع الساطعة الناتجة عن التصوير بالهاتف.",
            new[]
            {
                new PipelineAlgorithm("illumination_normalization_blur_divide",
                    "التمويه والقسمة", new[]
                {
                    new PipelineParameter("blur_kernel", "حجم نواة التمويه",
                        ParameterKind.OddInteger, 95, 3, 501,
                        "عدد فردي موجب.")
                }),
                new PipelineAlgorithm("illumination_normalization_blackhat",
                    "المرشّح المورفولوجي (غير متاح بعد)",
                    Array.Empty<PipelineParameter>(),
                    "لم يُنفَّذ في الخدمة بعد.",
                    IsImplemented: false)
            }),

        new PipelineStepDefinition(
            "contrast_enhancement",
            "تحسين التباين",
            PhaseTwo,
            "يزيد وضوح الحبر مقابل الورق.",
            new[]
            {
                new PipelineAlgorithm("clahe", "CLAHE (تكيّفي)", new[]
                {
                    new PipelineParameter("clip_limit", "حد القصّ",
                        ParameterKind.Decimal, 2.5, 0.1, 40),
                    new PipelineParameter("tile_grid_size", "شبكة المربعات",
                        ParameterKind.IntegerPair, 8, 1, 64,
                        SecondDefaultValue: 8,
                        SecondLabel: "الارتفاع")
                }),
                new PipelineAlgorithm("plain_equalization", "معادلة المدرج التكراري",
                    Array.Empty<PipelineParameter>(),
                    "لا تحتاج إلى أي إعداد.")
            }),

        new PipelineStepDefinition(
            "denoising",
            "إزالة الضجيج",
            PhaseThree,
            "ينعّم حبيبات الورق قبل التحويل الثنائي.",
            new[]
            {
                new PipelineAlgorithm("bilateral_filter", "المرشّح الثنائي", new[]
                {
                    new PipelineParameter("d", "قطر الجوار",
                        ParameterKind.Integer, 20, 1, 50),
                    new PipelineParameter("sigma_color", "سيغما اللون",
                        ParameterKind.Decimal, 25.0, 0.1, 200),
                    new PipelineParameter("sigma_space", "سيغما المسافة",
                        ParameterKind.Decimal, 50.0, 0.1, 200)
                }),
                new PipelineAlgorithm("median_blur", "التمويه الوسيط", new[]
                {
                    new PipelineParameter("k", "حجم النواة",
                        ParameterKind.OddInteger, 5, 1, 99, "عدد فردي موجب.")
                }),
                new PipelineAlgorithm("gaussian_blur", "التمويه الغاوسي", new[]
                {
                    new PipelineParameter("ksize", "حجم النواة",
                        ParameterKind.IntegerPair, 5, 1, 99,
                        SecondDefaultValue: 5,
                        SecondLabel: "الارتفاع"),
                    new PipelineParameter("sigma", "سيغما",
                        ParameterKind.Decimal, 0.0, 0, 100,
                        "الصفر يعني الحساب التلقائي من حجم النواة.")
                }),
                new PipelineAlgorithm("nlm_denoise", "الوسائل غير المحلية (NLM)", new[]
                {
                    new PipelineParameter("h", "قوة المرشّح",
                        ParameterKind.Decimal, 10.0, 0.1, 100),
                    new PipelineParameter("template_window_size", "نافذة القالب",
                        ParameterKind.OddInteger, 7, 1, 99),
                    new PipelineParameter("search_window_size", "نافذة البحث",
                        ParameterKind.OddInteger, 21, 1, 99)
                },
                    "أبطأ بكثير من الخيارات الأخرى، وأعلى جودة. انتبه على الأجهزة الضعيفة.")
            }),

        new PipelineStepDefinition(
            "thresholding",
            "التحويل الثنائي",
            PhaseThree,
            "يحوّل الصورة إلى أبيض وأسود، وهي الصورة التي يقرؤها محرك التعرّف.",
            new[]
            {
                new PipelineAlgorithm("adaptive_threshold", "عتبة تكيّفية", new[]
                {
                    new PipelineParameter("block_size", "حجم الكتلة",
                        ParameterKind.OddInteger, 51, 3, 999,
                        "عدد فردي لا يقل عن ٣."),
                    new PipelineParameter("c", "الثابت المطروح",
                        ParameterKind.Decimal, 35.0, -100, 100)
                }),
                new PipelineAlgorithm("otsu_threshold", "أوتسو (تلقائي)",
                    Array.Empty<PipelineParameter>(),
                    "يختار العتبة تلقائيًا، فلا إعدادات له."),
                new PipelineAlgorithm("fixed_threshold", "عتبة ثابتة", new[]
                {
                    new PipelineParameter("t", "العتبة",
                        ParameterKind.Integer, 127, 0, 255)
                })
            }),

        new PipelineStepDefinition(
            "morphological_cleanup",
            "التنظيف المورفولوجي",
            PhaseThree,
            "يزيل النقاط المتناثرة أو يسدّ الفجوات داخل الحروف بعد التحويل الثنائي.",
            new[]
            {
                new PipelineAlgorithm("morphological_cleanup", "التنظيف المورفولوجي", new[]
                {
                    new PipelineParameter("operation", "العملية",
                        ParameterKind.Choice, "open",
                        Choices: new[] { "open", "close", "erode", "dilate" }),
                    new PipelineParameter("kernel_size", "حجم النواة",
                        ParameterKind.Integer, 2, 1, 50)
                })
            })
    };

    /// <summary>
    /// OCR engines the service can be asked for. `stub` is included deliberately: it
    /// fabricates a deterministic invoice and is the only way to verify the desktop app
    /// talks to the service correctly before a real engine is installed.
    /// </summary>
    public static IReadOnlyList<PipelineAlgorithm> OcrEngines { get; } = new[]
    {
        new PipelineAlgorithm("tesseract", "Tesseract", new[]
        {
            new PipelineParameter("lang", "لغة المحرك",
                ParameterKind.Choice, "ara",
                Choices: new[] { "ara", "eng", "ara+eng" })
        },
            "خفيف ولا يحتاج إلى نماذج، لكنه يتطلّب تثبيت برنامج Tesseract على الجهاز."),

        new PipelineAlgorithm("surya", "Surya", Array.Empty<PipelineParameter>(),
            "يحتاج إلى تنزيل نماذج. دقّته على الخط العربي اليدوي محدودة."),

        new PipelineAlgorithm("surya_qwen", "Surya + Qwen (هجين)",
            Array.Empty<PipelineParameter>(),
            "الأدقّ على الخط اليدوي، لكنه يتطلّب نماذج محلية وبيئة Python ثانية مُعدّة في ملف إعدادات الخدمة."),

        new PipelineAlgorithm("stub", "محرك تجريبي (بيانات وهمية)",
            Array.Empty<PipelineParameter>(),
            "يولّد فاتورة وهمية ثابتة لاختبار الاتصال فقط. لا تستخدمه لفواتير حقيقية.")
    };

    public static IReadOnlyList<PipelineAlgorithm> TableExtractors { get; } = new[]
    {
        new PipelineAlgorithm("grid_line", "استخراج بخطوط الجدول", new[]
        {
            new PipelineParameter("dot_bridge_scale", "جسر النقاط",
                ParameterKind.Integer, 150, 1, 1000),
            new PipelineParameter("main_kernel_scale", "مقياس النواة",
                ParameterKind.Integer, 30, 1, 500),
            new PipelineParameter("min_line_length_ratio", "أقصر خط (نسبة)",
                ParameterKind.Decimal, 0.05, 0.001, 1),
            new PipelineParameter("intersection_tolerance", "تسامح التقاطع",
                ParameterKind.Integer, 15, 0, 200),
            new PipelineParameter("merge_proximity", "مسافة الدمج",
                ParameterKind.Integer, 15, 0, 200),
            new PipelineParameter("min_extend_length_ratio", "أقل امتداد (نسبة)",
                ParameterKind.Decimal, 0.5, 0.01, 1),
            new PipelineParameter("coverage_ratio", "نسبة التغطية",
                ParameterKind.Decimal, 0.5, 0.01, 1)
        }),

        new PipelineAlgorithm("contour_based", "استخراج بالمحيطات",
            Array.Empty<PipelineParameter>())
    };

    /// <summary>
    /// The keyword-dictionary matcher. This is the generic keyword correction applied to
    /// every detected cell; matching invoice fields against the Customers and Products
    /// tables always happens and is not configurable here.
    /// </summary>
    public static IReadOnlyList<PipelineAlgorithm> StringMatchers { get; } = new[]
    {
        new PipelineAlgorithm("levenshtein", "مسافة ليفنشتاين (كلمات متصلة)", new[]
        {
            new PipelineParameter("max_distance", "أقصى عدد تعديلات",
                ParameterKind.Integer, 2, 0, 10,
                "عدد الحروف التي يُسمح باختلافها قبل رفض المطابقة.")
        },
            "للأسماء التي تُقرأ كسلسلة حروف واحدة: أسماء الزبائن والمدن والمحافظات."),

        new PipelineAlgorithm("order_independent", "مطابقة مستقلة عن الترتيب", new[]
        {
            new PipelineParameter("min_score", "أقل نسبة قبول",
                ParameterKind.Decimal, 0.75, 0, 1)
        },
            "لأسماء المنتجات، حيث تكون الكلمات صحيحة لكن ترتيبها مختلف.")
    };

    public static PipelineStepDefinition StepFor(string key) =>
        Steps.First(step => step.Key == key);
}
