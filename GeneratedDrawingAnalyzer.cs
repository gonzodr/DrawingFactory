using SolidWorks.Interop.sldworks;

public sealed class GeneratedDrawingAnalyzer
{
    private readonly DrawingSnapshotExtractor _extractor = new();

    public DrawingSnapshot Analyze(ModelDoc2 generatedDrawing, string path) =>
        _extractor.Extract(generatedDrawing, path, DrawingSnapshotRole.Generated);
}
