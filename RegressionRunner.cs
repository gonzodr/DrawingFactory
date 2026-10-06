using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class RegressionRunner
{
    private readonly ISldWorks _application;
    private readonly ReferenceDrawingAnalyzer _referenceAnalyzer = new();
    private readonly GeneratedDrawingAnalyzer _generatedAnalyzer = new();
    private readonly DrawingEvaluator _evaluator = new();

    private readonly TextWriter _diagnostics;

    public RegressionRunner(ISldWorks application, TextWriter? diagnostics = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? Console.Error;
    }

    public RegressionRunResult Run(string trainingSamplesRoot, string generatedRoot)
    {
        var trainingRoot = InputFiles.RequireTrainingDirectory(trainingSamplesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedRoot);
        var outputRoot = Path.GetFullPath(generatedRoot);

        var comparisons = new List<DrawingComparisonResult>();
        var skipped = new List<string>();
        var referenceSnapshots = new List<DrawingSnapshot>();
        foreach (var sampleDirectory in Directory.EnumerateDirectories(trainingRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var sampleName = Path.GetFileName(sampleDirectory);
            var partPath = Path.Combine(sampleDirectory, "part.SLDPRT");
            var referencePath = Path.Combine(sampleDirectory, "reference.SLDDRW");
            var generatedPath = Path.Combine(outputRoot, sampleName, "generated.SLDDRW");
            if (!File.Exists(partPath) || !File.Exists(referencePath))
            {
                skipped.Add($"{sampleName}: incomplete training sample; part.SLDPRT or reference.SLDDRW is missing.");
                continue;
            }

            try
            {
                using var documents = new SolidWorksDocumentScope(_application, _diagnostics);
                var part = documents.Open(partPath, swDocumentTypes_e.swDocPART, readOnly: true, out _);
                var partAnalysis = new PartAnalyzer(part).Analyze();
                var manufacturingFeatures = new ManufacturingFeatureAnalyzer(part).Analyze(partAnalysis.Geometry);
                var referenceDocument = documents.Open(referencePath, swDocumentTypes_e.swDocDRAWING, readOnly: true, out _);
                var referenceSnapshot = _referenceAnalyzer.Analyze(referenceDocument, referencePath);
                referenceSnapshots.Add(referenceSnapshot);
                if (!File.Exists(generatedPath))
                {
                    skipped.Add($"{sampleName}: generated drawing is missing at '{generatedPath}'. Reference was added to the style profile.");
                    continue;
                }

                var generatedDocument = documents.Open(generatedPath, swDocumentTypes_e.swDocDRAWING, readOnly: true, out _);
                var generatedSnapshot = _generatedAnalyzer.Analyze(generatedDocument, generatedPath);
                comparisons.Add(_evaluator.Compare(
                    sampleName,
                    referenceSnapshot,
                    generatedSnapshot,
                    manufacturingFeatures,
                    partAnalysis));
            }
            catch (Exception exception)
            {
                skipped.Add($"{sampleName}: analysis failed: {exception.Message}");
            }
        }

        return new RegressionRunResult(
            trainingRoot,
            outputRoot,
            comparisons,
            skipped,
            DrawingStyleProfile.Observe(referenceSnapshots));
    }

    public DrawingComparisonResult ComparePair(
        string sampleName,
        string partPath,
        string referencePath,
        string generatedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        partPath = InputFiles.RequireFile(partPath, ".SLDPRT", "SOLIDWORKS part file");
        referencePath = InputFiles.RequireFile(referencePath, ".SLDDRW", "reference drawing");
        generatedPath = InputFiles.RequireFile(generatedPath, ".SLDDRW", "generated drawing");

        using var documents = new SolidWorksDocumentScope(_application, _diagnostics);
        var part = documents.Open(partPath, swDocumentTypes_e.swDocPART, readOnly: true, out _);
        var partAnalysis = new PartAnalyzer(part).Analyze();
        var manufacturingFeatures = new ManufacturingFeatureAnalyzer(part).Analyze(partAnalysis.Geometry);
        var referenceDocument = documents.Open(referencePath, swDocumentTypes_e.swDocDRAWING, readOnly: true, out _);
        var generatedDocument = documents.Open(generatedPath, swDocumentTypes_e.swDocDRAWING, readOnly: true, out _);
        var reference = _referenceAnalyzer.Analyze(referenceDocument, referencePath);
        var generated = _generatedAnalyzer.Analyze(generatedDocument, generatedPath);
        return _evaluator.Compare(sampleName, reference, generated, manufacturingFeatures, partAnalysis);
    }

}

public sealed record RegressionRunResult(
    string TrainingSamplesRoot,
    string GeneratedRoot,
    IReadOnlyList<DrawingComparisonResult> Comparisons,
    IReadOnlyList<string> SkippedSamples,
    DrawingStyleProfile StyleProfile)
{
    public bool IsSuccessful => Comparisons.Count > 0 && SkippedSamples.Count == 0 &&
        Comparisons.All(comparison => !comparison.HasDifferences);
}
