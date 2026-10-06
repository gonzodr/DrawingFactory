public sealed record DrawingComparisonResult(
    string SampleName,
    string ReferencePath,
    string GeneratedPath,
    IReadOnlyList<string> MissingViews,
    IReadOnlyList<string> UnnecessaryViews,
    IReadOnlyList<string> WrongOrientations,
    IReadOnlyList<string> SheetDifferences,
    IReadOnlyList<string> ScaleDifferences,
    IReadOnlyList<string> MissingDimensions,
    IReadOnlyList<string> UnnecessaryDimensions,
    IReadOnlyList<string> RedundantDimensions,
    IReadOnlyList<string> DimensionPlacementDifferences,
    IReadOnlyList<string> MissingNativeAnnotations,
    IReadOnlyList<string> UnnecessaryNativeAnnotations,
    IReadOnlyList<string> TitleBlockDifferences,
    IReadOnlyList<string> UnresolvedManufacturingInformation)
{
    public bool HasDifferences =>
        MissingViews.Count > 0 || UnnecessaryViews.Count > 0 || WrongOrientations.Count > 0 ||
        SheetDifferences.Count > 0 || ScaleDifferences.Count > 0 || MissingDimensions.Count > 0 || UnnecessaryDimensions.Count > 0 ||
        RedundantDimensions.Count > 0 || DimensionPlacementDifferences.Count > 0 ||
        MissingNativeAnnotations.Count > 0 || UnnecessaryNativeAnnotations.Count > 0 ||
        TitleBlockDifferences.Count > 0 || UnresolvedManufacturingInformation.Count > 0;
}
