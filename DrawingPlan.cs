public enum DrawingSheetRecommendation
{
    A4Landscape,
    A3Landscape,
    A2Landscape,
    A1Landscape,
    A0Landscape
}

public sealed record DrawingPlan(
    string StrategyName,
    DrawingSheetRecommendation SheetRecommendation,
    double TargetScale,
    PrimaryViewPlan? PrimaryView,
    IReadOnlyList<SecondaryViewPlan> RequestedSecondaryViews,
    IReadOnlyList<string> Diagnostics,
    double BoundingBoxXmm,
    double BoundingBoxYmm,
    double BoundingBoxZmm,
    string ThicknessAxis,
    double DominantPlanarFaceAreaMm2);

public sealed record PrimaryViewPlan(
    string ModelViewName,
    Vector3D Normal,
    string Reason);

public sealed record SecondaryViewPlan(
    string Purpose,
    string ModelViewName,
    string Reason);

internal static class DrawingViewGeometry
{
    public static (string Horizontal, string Vertical) GetAxes(string? viewName) =>
        viewName?.Trim().TrimStart('*').ToUpperInvariant() switch
        {
            "TOP" or "BOTTOM" or "TOP VIEW" or "BOTTOM VIEW" => ("X", "Z"),
            "LEFT" or "RIGHT" or "LEFT VIEW" or "RIGHT VIEW" => ("Y", "Z"),
            _ => ("X", "Y")
        };

    public static double GetExtent(PartGeometryAnalysis geometry, string axis) => axis switch
    {
        "X" => geometry.BoundingBoxXmm,
        "Y" => geometry.BoundingBoxYmm,
        "Z" => geometry.BoundingBoxZmm,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    public static double GetExtent(ManufacturingFeatureSet features, string axis) => axis switch
    {
        "X" => features.OverallXmm,
        "Y" => features.OverallYmm,
        "Z" => features.OverallZmm,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    public static double GetCoordinate(Point3D point, string axis) => axis switch
    {
        "X" => point.Xmm,
        "Y" => point.Ymm,
        "Z" => point.Zmm,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };
}