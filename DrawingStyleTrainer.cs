using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class DrawingStyleTrainer
{
    private readonly ISldWorks _application;
    private readonly TextWriter _diagnostics;

    public DrawingStyleTrainer(ISldWorks application, TextWriter? diagnostics = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? Console.Error;
    }

    public static IReadOnlyList<(string Part, string Reference)> GetSamples(string trainingSamplesRoot)
    {
        var root = InputFiles.RequireTrainingDirectory(trainingSamplesRoot);
        return Directory.EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(directory => (
                Part: InputFiles.RequireFile(Path.Combine(directory, "part.SLDPRT"), ".SLDPRT", "training part"),
                Reference: InputFiles.RequireFile(Path.Combine(directory, "reference.SLDDRW"), ".SLDDRW", "training reference")))
            .ToArray();
    }

    public LearnedDrawingStyle Train(string trainingSamplesRoot)
    {
        var root = InputFiles.RequireTrainingDirectory(trainingSamplesRoot);
        var samples = GetSamples(root);
        var analyzer = new ReferenceDrawingAnalyzer();
        var snapshots = new List<DrawingSnapshot>();
        foreach (var sample in samples)
        {
            using var documents = new SolidWorksDocumentScope(_application, _diagnostics);
            documents.Open(sample.Part, swDocumentTypes_e.swDocPART, readOnly: true, out _);
            var drawing = documents.Open(sample.Reference, swDocumentTypes_e.swDocDRAWING, readOnly: true, out _);
            snapshots.Add(analyzer.Analyze(drawing, sample.Reference));
        }

        return LearnedDrawingStyle.Learn(snapshots, root);
    }
}

public sealed class LearnedStyleLayoutPlanner
{
    public DimensionLayoutPlan Plan(DrawingPlan plan, DimensionPlan dimensions, ManufacturingFeatureSet features, LearnedDrawingStyle style)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        var layouts = new List<DimensionLayoutPlan>();
        var scales = style.PreferredScales.Append(plan.TargetScale).Distinct();
        foreach (var scale in scales)
        {
            var styled = plan with
            {
                SheetRecommendation = style.PreferredSheet,
                TargetScale = scale,
                Diagnostics = plan.Diagnostics.Append(FormattableString.Invariant(
                    $"Learned style: {style.Observations.ReferenceSampleCount} references; preferred {style.PreferredSheet}; candidate scale {scale:0.###}.")).ToArray()
            };
            DimensionLayoutPlan layout;
            try
            {
                layout = new DimensionLayoutPlanner().Plan(styled, dimensions, features);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (layout.FinalDrawingPlan.SheetRecommendation == style.PreferredSheet)
            {
                return layout;
            }
            layouts.Add(layout);
        }

        return layouts.OrderBy(layout => layout.FinalDrawingPlan.SheetRecommendation).FirstOrDefault()
            ?? throw new InvalidOperationException("Neither the learned scales nor the default scale fit the available sheets.");
    }
}
