using SolidWorks.Interop.sldworks;

public sealed class ReferenceDrawingAnalyzer
{
    private readonly DrawingSnapshotExtractor _extractor = new();

    public DrawingSnapshot Analyze(ModelDoc2 referenceDrawing, string path) =>
        _extractor.Extract(referenceDrawing, path, DrawingSnapshotRole.Reference);
}
