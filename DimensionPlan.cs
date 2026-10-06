public enum ManufacturingDimensionKind
{
    OverallWidth,
    OverallHeight,
    Thickness,
    CornerRadius,
    HoleDiameter,
    RepeatedHoleCount,
    HorizontalHolePosition,
    VerticalHolePosition,
    HorizontalHolePatternPitch,
    VerticalHolePatternPitch,
    ThreadCallout
}

public sealed record DimensionRequirement(
    string SemanticId,
    ManufacturingDimensionKind Kind,
    double? ValueMm,
    string ViewPurpose,
    string SourceFeature,
    int RelatedFeatureCount,
    string? UnresolvedReason,
    Point3D? ReferenceCenter = null,
    Point3D? SecondaryReferenceCenter = null,
    string? TextPrefix = null,
    double? ReferenceDiameterMm = null);

public sealed record DimensionPlan(
    IReadOnlyList<DimensionRequirement> Requirements,
    IReadOnlyList<string> IntentionalOmissions);

public enum DimensionLane
{
    Top,
    Left,
    Right,
    ThicknessViewRight,
    HoleLeader,
    CornerLeader,
    Leader
}

/// <summary>Executable placements have one-based lane numbers; unresolved placements use zero and reserve no lane.</summary>
public sealed record DimensionPlacement(
    string SemanticId,
    DimensionLane Lane,
    int LaneNumber,
    bool Executable,
    string? UnresolvedReason);

public sealed record DimensionLayoutPlan(
    DrawingPlan FinalDrawingPlan,
    double PrimaryCenterXFraction,
    double PrimaryCenterYFraction,
    double ThicknessViewCenterXFraction,
    double ThicknessViewCenterYFraction,
    double ReservedTopMm,
    double ReservedLeftMm,
    double ReservedRightMm,
    double ReservedBottomMm,
    IReadOnlyList<DimensionPlacement> Placements);
