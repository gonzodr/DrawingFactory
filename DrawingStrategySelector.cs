public sealed class DrawingStrategySelector
{
    private readonly FlatPartStrategy _flatPartStrategy = new();

    public DrawingPlan Select(PartAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (_flatPartStrategy.TryCreatePlan(analysis, out var flatPartPlan, out var diagnostics))
        {
            return flatPartPlan!;
        }

        var geometry = analysis.Geometry;
        return new DrawingPlan(
            "Unknown",
            DrawingSheetRecommendation.A3Landscape,
            0,
            null,
            Array.Empty<SecondaryViewPlan>(),
            diagnostics,
            geometry.BoundingBoxXmm,
            geometry.BoundingBoxYmm,
            geometry.BoundingBoxZmm,
            string.Empty,
            0);
    }
}