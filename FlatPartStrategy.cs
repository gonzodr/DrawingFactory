using System.Globalization;

public sealed class FlatPartStrategy
{
    private const double MaximumThicknessRatio = 0.1;
    private const double MinimumNormalAlignment = 0.999;
    private const double MinimumFaceAreaRatio = 0.35;
    private const double TargetScale = 0.2;

    public bool TryCreatePlan(
        PartAnalysis analysis,
        out DrawingPlan? plan,
        out IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var geometry = analysis.Geometry;
        var dimensions = new[]
        {
            (Axis: "X", Value: geometry.BoundingBoxXmm),
            (Axis: "Y", Value: geometry.BoundingBoxYmm),
            (Axis: "Z", Value: geometry.BoundingBoxZmm)
        };
        var orderedDimensions = dimensions.OrderByDescending(dimension => dimension.Value).ToArray();
        var thickness = orderedDimensions[2];
        var middle = orderedDimensions[1];
        var boundingBoxText = FormatBoundingBox(geometry);

        if (dimensions.Any(dimension => !double.IsFinite(dimension.Value) || dimension.Value <= 0) ||
            thickness.Value > middle.Value * MaximumThicknessRatio)
        {
            plan = null;
            diagnostics = new[]
            {
                $"Bounding box: {boundingBoxText} mm.",
                "FlatPart not selected: no bounding-box dimension is dramatically smaller than the other two."
            };
            return false;
        }

        var thicknessAxis = AxisVector(thickness.Axis);
        var minimumFaceArea = orderedDimensions[0].Value * middle.Value * MinimumFaceAreaRatio;
        var dominantFace = geometry.PlanarFaces
            .Where(face => double.IsFinite(face.AreaMm2) &&
                           double.IsFinite(face.Normal.X) && double.IsFinite(face.Normal.Y) && double.IsFinite(face.Normal.Z) &&
                           Math.Abs(Dot(face.Normal, thicknessAxis)) >= MinimumNormalAlignment &&
                           face.AreaMm2 >= minimumFaceArea)
            .OrderByDescending(face => face.AreaMm2)
            .FirstOrDefault();

        if (dominantFace is null)
        {
            plan = null;
            diagnostics = new[]
            {
                $"Bounding box: {boundingBoxText} mm.",
                $"Thickness axis: {thickness.Axis} ({Format(thickness.Value)} mm).",
                "FlatPart not selected: no sufficiently large planar face is normal to the thin axis."
            };
            return false;
        }

        var viewName = GetStandardViewName(dominantFace.Normal);
        var (horizontalAxis, verticalAxis) = DrawingViewGeometry.GetAxes(viewName);
        var sheetRecommendation = SelectSheet(
            DrawingViewGeometry.GetExtent(geometry, horizontalAxis),
            DrawingViewGeometry.GetExtent(geometry, verticalAxis));
        if (sheetRecommendation is null)
        {
            plan = null;
            diagnostics = new[]
            {
                $"Bounding box: {boundingBoxText} mm.",
                "FlatPart not selected: the projected views do not fit the available A-series landscape sizes at 1:5."
            };
            return false;
        }

        var thicknessViewAxis = dimensions
            .Where(dimension => dimension.Axis != thickness.Axis)
            .OrderBy(dimension => dimension.Value)
            .First();
        var thicknessViewName = GetStandardViewName(AxisVector(thicknessViewAxis.Axis));
        var reason =
            $"The dominant planar face normal ({FormatVector(dominantFace.Normal)}) aligns with the {thickness.Axis} thickness axis; the planned view is normal to that face.";
        var sheetName = sheetRecommendation.Value.ToString();
        var secondaryReason =
            $"The {thicknessViewAxis.Axis}-axis view is perpendicular to the primary view and exposes the {thickness.Axis}-axis thickness.";

        plan = new DrawingPlan(
            "FlatPart",
            sheetRecommendation.Value,
            TargetScale,
            new PrimaryViewPlan(viewName, dominantFace.Normal, reason),
            new[]
            {
                new SecondaryViewPlan("Thickness", thicknessViewName, secondaryReason)
            },
            new[]
            {
                $"Bounding box: {boundingBoxText} mm.",
                $"Thickness axis: {thickness.Axis} ({Format(thickness.Value)} mm; {Format(thickness.Value / middle.Value)} of the next-largest dimension).",
                $"Dominant planar face area: {Format(dominantFace.AreaMm2)} mm^2.",
                $"Dominant planar face normal: {FormatVector(dominantFace.Normal)}.",
                $"Primary view: {viewName}; {reason}",
                $"Thickness view: {thicknessViewName}; {secondaryReason}",
                $"Scale: 1:5 ({TargetScale.ToString("0.###", CultureInfo.InvariantCulture)}).",
                $"Sheet: {sheetName}."
            },
            geometry.BoundingBoxXmm,
            geometry.BoundingBoxYmm,
            geometry.BoundingBoxZmm,
            thickness.Axis,
            dominantFace.AreaMm2);

        diagnostics = plan.Diagnostics;
        return true;
    }

    private static Vector3D AxisVector(string axis) => axis switch
    {
        "X" => new Vector3D(1, 0, 0),
        "Y" => new Vector3D(0, 1, 0),
        _ => new Vector3D(0, 0, 1)
    };

    private static DrawingSheetRecommendation? SelectSheet(double primaryWidthMm, double primaryHeightMm)
    {
        var candidates = new[]
        {
            (DrawingSheetRecommendation.A3Landscape, WidthMm: 420.0, HeightMm: 297.0),
            (DrawingSheetRecommendation.A2Landscape, WidthMm: 594.0, HeightMm: 420.0),
            (DrawingSheetRecommendation.A1Landscape, WidthMm: 841.0, HeightMm: 594.0),
            (DrawingSheetRecommendation.A0Landscape, WidthMm: 1189.0, HeightMm: 841.0)
        };

        foreach (var candidate in candidates)
        {
            if (primaryWidthMm * TargetScale <= candidate.WidthMm * 0.8 &&
                primaryHeightMm * TargetScale <= candidate.HeightMm * 0.45)
            {
                return candidate.Item1;
            }
        }

        return null;
    }

    private static double Dot(Vector3D left, Vector3D right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static string GetStandardViewName(Vector3D normal)
    {
        var components = new[]
        {
            (Axis: "X", Value: normal.X),
            (Axis: "Y", Value: normal.Y),
            (Axis: "Z", Value: normal.Z)
        };
        var dominant = components.OrderByDescending(component => Math.Abs(component.Value)).First();
        return (dominant.Axis, dominant.Value >= 0) switch
        {
            ("X", true) => "*Right",
            ("X", false) => "*Left",
            ("Y", true) => "*Top",
            ("Y", false) => "*Bottom",
            ("Z", true) => "*Front",
            _ => "*Back"
        };
    }

    private static string FormatBoundingBox(PartGeometryAnalysis geometry) =>
        $"{Format(geometry.BoundingBoxXmm)} x {Format(geometry.BoundingBoxYmm)} x {Format(geometry.BoundingBoxZmm)}";

    private static string FormatVector(Vector3D vector) =>
        $"({Format(vector.X)}, {Format(vector.Y)}, {Format(vector.Z)})";

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}