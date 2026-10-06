public sealed class DimensionLayoutPlanner
{
    private const double PrimaryCenterXFraction = 0.5;
    private const double PrimaryCenterYFraction = 0.68;
    private const double ThicknessViewCenterXFraction = 0.25;
    private const double ThicknessViewCenterYFraction = 0.22;

    private static readonly (DrawingSheetRecommendation Sheet, double WidthMm, double HeightMm)[] SheetSizes = DrawingSheetGeometry.Sizes;

    public DimensionLayoutPlan Plan(
        DrawingPlan drawingPlan,
        DimensionPlan dimensionPlan,
        ManufacturingFeatureSet features)
    {
        ArgumentNullException.ThrowIfNull(drawingPlan);
        ArgumentNullException.ThrowIfNull(dimensionPlan);
        ArgumentNullException.ThrowIfNull(features);
        if (!double.IsFinite(drawingPlan.TargetScale) || drawingPlan.TargetScale <= 0 || drawingPlan.PrimaryView is null)
        {
            throw new ArgumentException("Layout planning requires a primary view and a finite, positive drawing scale.", nameof(drawingPlan));
        }

        if (!double.IsFinite(features.OverallXmm) || features.OverallXmm <= 0 ||
            !double.IsFinite(features.OverallYmm) || features.OverallYmm <= 0 ||
            !double.IsFinite(features.OverallZmm) || features.OverallZmm <= 0)
        {
            throw new ArgumentException("Layout planning requires finite, positive model extents.", nameof(features));
        }

        var supported = dimensionPlan.Requirements.Where(IsCurrentlyExecutable).ToArray();
        var topCount = supported.Count(item => item.Kind is ManufacturingDimensionKind.OverallWidth or ManufacturingDimensionKind.HorizontalHolePatternPitch);
        var leftCount = supported.Count(item => item.Kind is ManufacturingDimensionKind.OverallHeight or ManufacturingDimensionKind.VerticalHolePatternPitch);
        var rightCount = supported.Count(item => item.Kind is ManufacturingDimensionKind.HoleDiameter or ManufacturingDimensionKind.CornerRadius);
        var thicknessCount = supported.Count(item => item.Kind == ManufacturingDimensionKind.Thickness);
        var reservedTop = 18.0 + topCount * 10.0;
        var reservedLeft = 18.0 + leftCount * 10.0;
        var reservedRight = 18.0 + rightCount * 12.0;
        var reservedBottom = 22.0 + thicknessCount * 10.0;
        var (horizontalAxis, verticalAxis) = DrawingViewGeometry.GetAxes(drawingPlan.PrimaryView.ModelViewName);
        var primaryWidth = DrawingViewGeometry.GetExtent(features, horizontalAxis) * drawingPlan.TargetScale;
        var primaryHeight = DrawingViewGeometry.GetExtent(features, verticalAxis) * drawingPlan.TargetScale;
        var secondarySizes = drawingPlan.RequestedSecondaryViews.Select(view =>
        {
            var axes = DrawingViewGeometry.GetAxes(view.ModelViewName);
            return (WidthMm: DrawingViewGeometry.GetExtent(features, axes.Horizontal) * drawingPlan.TargetScale,
                    HeightMm: DrawingViewGeometry.GetExtent(features, axes.Vertical) * drawingPlan.TargetScale);
        }).ToArray();
        var startIndex = Array.FindIndex(SheetSizes, size => size.Sheet == drawingPlan.SheetRecommendation);
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        var selectedSize = SheetSizes.Skip(startIndex).FirstOrDefault(size =>
            FitsAt(primaryWidth, primaryHeight, PrimaryCenterXFraction, PrimaryCenterYFraction,
                size.WidthMm, size.HeightMm, reservedLeft, reservedTop, reservedRight, reservedBottom) &&
            secondarySizes.All(secondary =>
                FitsAt(secondary.WidthMm, secondary.HeightMm, ThicknessViewCenterXFraction, ThicknessViewCenterYFraction,
                    size.WidthMm, size.HeightMm, 0, 0, 0, reservedBottom) &&
                !ViewsOverlap(primaryWidth, primaryHeight, secondary.WidthMm, secondary.HeightMm, size.WidthMm, size.HeightMm)));
        if (selectedSize.WidthMm == 0)
        {
            throw new InvalidOperationException("The planned views and dimension lanes do not fit any available company A-series sheet at the requested scale.");
        }

        var placements = new List<DimensionPlacement>();
        var laneCounts = new Dictionary<DimensionLane, int>();
        foreach (var requirement in dimensionPlan.Requirements)
        {
            var lane = GetLane(requirement);
            var executable = IsCurrentlyExecutable(requirement);
            var laneNumber = 0;
            if (executable)
            {
                laneCounts.TryGetValue(lane, out laneNumber);
                laneCounts[lane] = ++laneNumber;
            }
            placements.Add(new DimensionPlacement(
                requirement.SemanticId,
                lane,
                laneNumber,
                executable,
                requirement.UnresolvedReason));
        }

        var finalDrawingPlan = drawingPlan with { SheetRecommendation = selectedSize.Sheet };
        if (selectedSize.Sheet != drawingPlan.SheetRecommendation)
        {
            finalDrawingPlan = finalDrawingPlan with
            {
                Diagnostics = drawingPlan.Diagnostics
                    .Append($"Dimension layout sheet: {selectedSize.Sheet} (enlarged from {drawingPlan.SheetRecommendation} to fit the views and dimension lanes).")
                    .ToArray()
            };
        }

        return new DimensionLayoutPlan(
            finalDrawingPlan,
            PrimaryCenterXFraction,
            PrimaryCenterYFraction,
            ThicknessViewCenterXFraction,
            ThicknessViewCenterYFraction,
            reservedTop,
            reservedLeft,
            reservedRight,
            reservedBottom,
            placements);
    }

    private static bool FitsAt(
        double widthMm, double heightMm, double centerXFraction, double centerYFraction,
        double sheetWidthMm, double sheetHeightMm,
        double reservedLeftMm, double reservedTopMm, double reservedRightMm, double reservedBottomMm) =>
        centerXFraction * sheetWidthMm - widthMm / 2 >= reservedLeftMm &&
        centerXFraction * sheetWidthMm + widthMm / 2 + reservedRightMm <= sheetWidthMm &&
        centerYFraction * sheetHeightMm - heightMm / 2 >= reservedBottomMm &&
        centerYFraction * sheetHeightMm + heightMm / 2 + reservedTopMm <= sheetHeightMm;

    private static bool ViewsOverlap(
        double primaryWidthMm, double primaryHeightMm, double secondaryWidthMm, double secondaryHeightMm,
        double sheetWidthMm, double sheetHeightMm) =>
        Math.Abs(PrimaryCenterXFraction - ThicknessViewCenterXFraction) * sheetWidthMm < (primaryWidthMm + secondaryWidthMm) / 2 &&
        Math.Abs(PrimaryCenterYFraction - ThicknessViewCenterYFraction) * sheetHeightMm < (primaryHeightMm + secondaryHeightMm) / 2;

    private static bool IsCurrentlyExecutable(DimensionRequirement requirement) =>
        requirement.UnresolvedReason is null &&
        requirement.ValueMm is double valueMm && double.IsFinite(valueMm) && valueMm > 0 &&
        requirement.Kind is
            ManufacturingDimensionKind.OverallWidth or
            ManufacturingDimensionKind.OverallHeight or
            ManufacturingDimensionKind.Thickness or
            ManufacturingDimensionKind.CornerRadius or
            ManufacturingDimensionKind.HoleDiameter or
            ManufacturingDimensionKind.HorizontalHolePatternPitch or
            ManufacturingDimensionKind.VerticalHolePatternPitch;

    private static DimensionLane GetLane(DimensionRequirement requirement) => requirement.Kind switch
    {
        ManufacturingDimensionKind.OverallWidth or ManufacturingDimensionKind.HorizontalHolePosition or ManufacturingDimensionKind.HorizontalHolePatternPitch => DimensionLane.Top,
        ManufacturingDimensionKind.OverallHeight or ManufacturingDimensionKind.VerticalHolePosition or ManufacturingDimensionKind.VerticalHolePatternPitch => DimensionLane.Left,
        ManufacturingDimensionKind.Thickness => DimensionLane.ThicknessViewRight,
        ManufacturingDimensionKind.HoleDiameter => DimensionLane.HoleLeader,
        ManufacturingDimensionKind.CornerRadius => DimensionLane.CornerLeader,
        _ => DimensionLane.Leader
    };
}
