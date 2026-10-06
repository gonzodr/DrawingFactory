using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class DrawingDimensionExecutor
{
    private const double MillimetersPerMeter = 1000.0;
    private const double GeometryMatchToleranceMm = 0.25;

    public DimensionExecutionReport Execute(
        ModelDoc2 drawing,
        DimensionPlan dimensionPlan,
        DimensionLayoutPlan layoutPlan,
        ManufacturingFeatureSet features,
        View primaryView,
        View thicknessView,
        double sheetWidthMm,
        double sheetHeightMm)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(dimensionPlan);
        ArgumentNullException.ThrowIfNull(layoutPlan);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(primaryView);
        ArgumentNullException.ThrowIfNull(thicknessView);

        var placements = layoutPlan.Placements.ToDictionary(placement => placement.SemanticId, StringComparer.Ordinal);
        var created = new List<CreatedDimension>();
        var unresolved = new List<UnresolvedDimension>();
        var semanticIds = new HashSet<string>(StringComparer.Ordinal);
        var occupiedAnnotationBounds = new List<SheetOutline>();
        var views = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase)
        {
            ["Primary"] = primaryView,
            ["Thickness"] = thicknessView
        };
        var viewOutlines = views.Values.Distinct().Select(ReadOutline).ToArray();

        foreach (var requirement in dimensionPlan.Requirements)
        {
            if (!semanticIds.Add(requirement.SemanticId))
            {
                throw new InvalidOperationException($"Duplicate dimension semantic id '{requirement.SemanticId}'.");
            }

            if (!placements.TryGetValue(requirement.SemanticId, out var placement) || !placement.Executable)
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    requirement.UnresolvedReason ?? placement?.UnresolvedReason ?? "No executable layout placement was produced."));
                continue;
            }

            if (requirement.ValueMm is not double expectedValueMm ||
                !views.TryGetValue(requirement.ViewPurpose, out var targetView))
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "The requirement has no numeric value or mapped target view."));
                continue;
            }

            if (requirement.Kind is ManufacturingDimensionKind.HoleDiameter or ManufacturingDimensionKind.CornerRadius)
            {
                var circularDimension = CreateCircularDimension(
                    drawing,
                    targetView,
                    requirement,
                    expectedValueMm,
                    viewOutlines,
                    occupiedAnnotationBounds,
                    sheetWidthMm,
                    sheetHeightMm);
                if (circularDimension.Created is not null)
                {
                    created.Add(circularDimension.Created);
                }
                else
                {
                    unresolved.Add(new UnresolvedDimension(
                        requirement.SemanticId,
                        requirement.Kind.ToString(),
                        circularDimension.Reason!));
                }

                continue;
            }

            if (requirement.Kind is ManufacturingDimensionKind.HorizontalHolePatternPitch or
                ManufacturingDimensionKind.VerticalHolePatternPitch)
            {
                var pitchDimension = CreatePitchDimension(
                    drawing,
                    targetView,
                    requirement,
                    expectedValueMm,
                    viewOutlines,
                    occupiedAnnotationBounds,
                    sheetWidthMm,
                    sheetHeightMm);
                if (pitchDimension.Created is not null)
                {
                    created.Add(pitchDimension.Created);
                }
                else
                {
                    unresolved.Add(new UnresolvedDimension(
                        requirement.SemanticId,
                        requirement.Kind.ToString(),
                        pitchDimension.Reason!));
                }

                continue;
            }

            var viewAxes = DrawingViewGeometry.GetAxes(targetView.GetOrientationName());
            var axis = requirement.Kind switch
            {
                ManufacturingDimensionKind.OverallWidth => viewAxes.Horizontal,
                ManufacturingDimensionKind.OverallHeight => viewAxes.Vertical,
                ManufacturingDimensionKind.Thickness => features.ThicknessAxis,
                _ => string.Empty
            };
            var vertices = GetVisibleVertices(targetView);
            var selectedVertices = FindExtremeVertices(vertices, axis, expectedValueMm);
            if (selectedVertices is null)
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "Visible drawing-view vertices do not provide the planned overall extent within tolerance."));
                continue;
            }

            var isHorizontal = requirement.Kind == ManufacturingDimensionKind.OverallWidth ||
                               requirement.Kind == ManufacturingDimensionKind.Thickness &&
                               viewAxes.Horizontal == features.ThicknessAxis;
            var isVertical = requirement.Kind == ManufacturingDimensionKind.OverallHeight ||
                             requirement.Kind == ManufacturingDimensionKind.Thickness &&
                             viewAxes.Vertical == features.ThicknessAxis;
            if (!isHorizontal && !isVertical)
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "The measured axis is not in the planned drawing-view plane."));
                continue;
            }

            var outline = ReadOutline(targetView);
            var offsetMm = requirement.Kind == ManufacturingDimensionKind.Thickness ? 20.0 : 22.0;
            var textXmm = isHorizontal
                ? (outline.LeftMm + outline.RightMm) / 2.0
                : requirement.Kind == ManufacturingDimensionKind.Thickness
                    ? outline.RightMm + offsetMm
                    : outline.LeftMm - offsetMm;
            var textYmm = isHorizontal
                ? outline.TopMm + offsetMm
                : (outline.BottomMm + outline.TopMm) / 2.0;

            var annotationBounds = GetEstimatedTextBounds(textXmm, textYmm, expectedValueMm, isHorizontal);
            if (annotationBounds.LeftMm < 0 || annotationBounds.BottomMm < 0 ||
                annotationBounds.RightMm > sheetWidthMm || annotationBounds.TopMm > sheetHeightMm ||
                viewOutlines.Any(viewOutline => annotationBounds.Intersects(viewOutline, 0.5)) ||
                occupiedAnnotationBounds.Any(existing => annotationBounds.Intersects(existing, 2.0)))
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "The planned dimension text would leave the sheet or overlap a model-view outline."));
                continue;
            }

            var selectionManager = drawing.SelectionManager as SelectionMgr
                ?? throw new InvalidOperationException("Could not access SOLIDWORKS selection data for drawing dimensions.");
            var selectData = selectionManager.CreateSelectData();
            selectData.View = targetView;
            drawing.ClearSelection2(true);
            if (!((IEntity)selectedVertices.Value.Minimum.Vertex).Select4(false, selectData) ||
                !((IEntity)selectedVertices.Value.Maximum.Vertex).Select4(true, selectData))
            {
                drawing.ClearSelection2(true);
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "SOLIDWORKS did not accept both visible extreme vertices for dimension selection."));
                continue;
            }

            var dimensionObject = isHorizontal
                ? drawing.AddHorizontalDimension2(textXmm / MillimetersPerMeter, textYmm / MillimetersPerMeter, 0.0)
                : drawing.AddVerticalDimension2(textXmm / MillimetersPerMeter, textYmm / MillimetersPerMeter, 0.0);
            drawing.ClearSelection2(true);

            if (dimensionObject is not DisplayDimension displayDimension ||
                displayDimension.GetDimension() is not Dimension dimension)
            {
                unresolved.Add(new UnresolvedDimension(
                    requirement.SemanticId,
                    requirement.Kind.ToString(),
                    "SOLIDWORKS did not return a verifiable display dimension."));
                continue;
            }

            var measuredValueMm = Math.Abs(dimension.SystemValue) * MillimetersPerMeter;
            if (Math.Abs(measuredValueMm - expectedValueMm) > GeometryMatchToleranceMm)
            {
                throw new InvalidOperationException(
                    $"Dimension '{requirement.SemanticId}' measured {measuredValueMm:0.###} mm, expected {expectedValueMm:0.###} mm. The staged drawing will not be saved.");
            }

            created.Add(new CreatedDimension(
                requirement.SemanticId,
                requirement.Kind.ToString(),
                measuredValueMm,
                targetView.GetName2(),
                textXmm,
                textYmm));
            occupiedAnnotationBounds.Add(annotationBounds);
        }

        if (!drawing.EditRebuild3())
        {
            throw new InvalidOperationException("SOLIDWORKS failed to rebuild the drawing after creating dimensions.");
        }

        return new DimensionExecutionReport(created, unresolved, true);
    }

    private static DimensionAttempt CreateCircularDimension(
        ModelDoc2 drawing,
        View view,
        DimensionRequirement requirement,
        double expectedValueMm,
        IReadOnlyList<SheetOutline> viewOutlines,
        ICollection<SheetOutline> occupiedAnnotationBounds,
        double sheetWidthMm,
        double sheetHeightMm)
    {
        if (requirement.ReferenceCenter is null)
        {
            return new DimensionAttempt(null, "No model-space center was recorded for the circular feature.");
        }

        var expectedDiameterMm = requirement.Kind == ManufacturingDimensionKind.CornerRadius
            ? expectedValueMm * 2.0
            : expectedValueMm;
        var edge = FindVisibleCircularEdge(view, requirement.ReferenceCenter, expectedDiameterMm);
        if (edge is null)
        {
            return new DimensionAttempt(null, "No visible circular view edge matched the analyzed center and diameter.");
        }

        var outline = ReadOutline(view);
        var textX = outline.RightMm + 24.0;
        var textY = requirement.Kind == ManufacturingDimensionKind.CornerRadius
            ? outline.TopMm - 16.0
            : (outline.BottomMm + outline.TopMm) / 2.0;
        var labelWidth = Math.Max(22.0, (requirement.TextPrefix?.Length ?? 0) * 3.0 + 20.0);
        var bounds = new SheetOutline(textX - labelWidth / 2.0, textY - 5.0, textX + labelWidth / 2.0, textY + 5.0);
        if (!FitsAndDoesNotOverlap(bounds, viewOutlines, occupiedAnnotationBounds, sheetWidthMm, sheetHeightMm))
        {
            return new DimensionAttempt(null, "The circular callout lane overlaps a view, another dimension, or the sheet boundary.");
        }

        var selectData = CreateSelectData(drawing, view);
        drawing.ClearSelection2(true);
        if (!((IEntity)edge).Select4(false, selectData))
        {
            drawing.ClearSelection2(true);
            return new DimensionAttempt(null, "SOLIDWORKS rejected the matched circular edge for dimension selection.");
        }

        var isRadius = requirement.Kind == ManufacturingDimensionKind.CornerRadius;
        var result = isRadius
            ? drawing.AddRadialDimension2(textX / MillimetersPerMeter, textY / MillimetersPerMeter, 0.0)
            : drawing.AddDiameterDimension2(textX / MillimetersPerMeter, textY / MillimetersPerMeter, 0.0);
        drawing.ClearSelection2(true);
        if (result is not DisplayDimension displayDimension || displayDimension.GetDimension() is not Dimension dimension)
        {
            return new DimensionAttempt(null, "SOLIDWORKS did not return a verifiable circular display dimension.");
        }

        if (!string.IsNullOrEmpty(requirement.TextPrefix))
        {
            displayDimension.SetText((int)swDimensionTextParts_e.swDimensionTextPrefix, requirement.TextPrefix);
        }

        var measuredValueMm = Math.Abs(dimension.SystemValue) * MillimetersPerMeter;
        if (Math.Abs(measuredValueMm - expectedValueMm) > GeometryMatchToleranceMm)
        {
            throw new InvalidOperationException(
                $"Dimension '{requirement.SemanticId}' measured {measuredValueMm:0.###} mm, expected {expectedValueMm:0.###} mm. The staged drawing will not be saved.");
        }

        occupiedAnnotationBounds.Add(bounds);
        return new DimensionAttempt(
            new CreatedDimension(requirement.SemanticId, requirement.Kind.ToString(), measuredValueMm, view.GetName2(), textX, textY),
            null);
    }

    private static DimensionAttempt CreatePitchDimension(
        ModelDoc2 drawing,
        View view,
        DimensionRequirement requirement,
        double expectedValueMm,
        IReadOnlyList<SheetOutline> viewOutlines,
        ICollection<SheetOutline> occupiedAnnotationBounds,
        double sheetWidthMm,
        double sheetHeightMm)
    {
        if (requirement.ReferenceCenter is null || requirement.SecondaryReferenceCenter is null ||
            requirement.ReferenceDiameterMm is not double diameterMm)
        {
            return new DimensionAttempt(null, "The plan does not contain both hole centers and their analyzed diameter.");
        }

        var firstEdge = FindVisibleCircularEdge(view, requirement.ReferenceCenter, diameterMm);
        var secondEdge = FindVisibleCircularEdge(view, requirement.SecondaryReferenceCenter, diameterMm);
        if (firstEdge is null || secondEdge is null || ReferenceEquals(firstEdge, secondEdge))
        {
            return new DimensionAttempt(null, "The two planned hole circles could not be matched to distinct visible view edges.");
        }

        var horizontal = requirement.Kind == ManufacturingDimensionKind.HorizontalHolePatternPitch;
        var outline = ReadOutline(view);
        var textX = horizontal ? (outline.LeftMm + outline.RightMm) / 2.0 : outline.LeftMm - 42.0;
        var textY = horizontal ? outline.TopMm + 12.0 : (outline.BottomMm + outline.TopMm) / 2.0;
        var bounds = GetEstimatedTextBounds(textX, textY, expectedValueMm, horizontal);
        if (!FitsAndDoesNotOverlap(bounds, viewOutlines, occupiedAnnotationBounds, sheetWidthMm, sheetHeightMm))
        {
            return new DimensionAttempt(null, "The hole-pitch dimension lane overlaps a view, another dimension, or the sheet boundary.");
        }

        var selectData = CreateSelectData(drawing, view);
        drawing.ClearSelection2(true);
        if (!((IEntity)firstEdge).Select4(false, selectData) || !((IEntity)secondEdge).Select4(true, selectData))
        {
            drawing.ClearSelection2(true);
            return new DimensionAttempt(null, "SOLIDWORKS rejected the matched hole circles for pitch dimension selection.");
        }

        var result = horizontal
            ? drawing.AddHorizontalDimension2(textX / MillimetersPerMeter, textY / MillimetersPerMeter, 0.0)
            : drawing.AddVerticalDimension2(textX / MillimetersPerMeter, textY / MillimetersPerMeter, 0.0);
        drawing.ClearSelection2(true);
        if (result is not DisplayDimension displayDimension || displayDimension.GetDimension() is not Dimension dimension)
        {
            return new DimensionAttempt(null, "SOLIDWORKS did not return a verifiable pitch dimension.");
        }

        var measuredValueMm = Math.Abs(dimension.SystemValue) * MillimetersPerMeter;
        if (Math.Abs(measuredValueMm - expectedValueMm) > GeometryMatchToleranceMm)
        {
            throw new InvalidOperationException(
                $"Hole pitch '{requirement.SemanticId}' measured {measuredValueMm:0.###} mm, expected {expectedValueMm:0.###} mm. The staged drawing will not be saved.");
        }

        occupiedAnnotationBounds.Add(bounds);
        return new DimensionAttempt(
            new CreatedDimension(requirement.SemanticId, requirement.Kind.ToString(), measuredValueMm, view.GetName2(), textX, textY),
            null);
    }

    private static SelectData CreateSelectData(ModelDoc2 drawing, View view)
    {
        var selectionManager = drawing.SelectionManager as SelectionMgr
            ?? throw new InvalidOperationException("Could not access SOLIDWORKS selection data for drawing dimensions.");
        var selectData = selectionManager.CreateSelectData();
        selectData.View = view;
        return selectData;
    }

    private static Edge? FindVisibleCircularEdge(View view, Point3D center, double diameterMm)
    {
        if (view.GetVisibleComponents() is not Array components)
        {
            return null;
        }

        foreach (var componentItem in components)
        {
            if (componentItem is not Component2 component ||
                view.GetVisibleEntities2(component, (int)swViewEntityType_e.swViewEntityType_Edge) is not Array entities)
            {
                continue;
            }

            foreach (var entity in entities)
            {
                if (entity is not Edge edge || edge.GetCurve() is not Curve curve || !curve.IsCircle() ||
                    curve.CircleParams is not Array circle || circle.Length < 7)
                {
                    continue;
                }

                var lowerBound = circle.GetLowerBound(0);
                var circleCenter = new Point3D(
                    Convert.ToDouble(circle.GetValue(lowerBound)) * MillimetersPerMeter,
                    Convert.ToDouble(circle.GetValue(lowerBound + 1)) * MillimetersPerMeter,
                    Convert.ToDouble(circle.GetValue(lowerBound + 2)) * MillimetersPerMeter);
                var actualDiameterMm = Convert.ToDouble(circle.GetValue(lowerBound + 6)) * 2.0 * MillimetersPerMeter;
                if (Distance(circleCenter, center) <= GeometryMatchToleranceMm &&
                    Math.Abs(actualDiameterMm - diameterMm) <= GeometryMatchToleranceMm)
                {
                    return edge;
                }
            }
        }

        return null;
    }

    private static bool FitsAndDoesNotOverlap(
        SheetOutline bounds,
        IReadOnlyList<SheetOutline> viewOutlines,
        IEnumerable<SheetOutline> occupiedAnnotationBounds,
        double sheetWidthMm,
        double sheetHeightMm) =>
        bounds.LeftMm >= 0 && bounds.BottomMm >= 0 &&
        bounds.RightMm <= sheetWidthMm && bounds.TopMm <= sheetHeightMm &&
        !viewOutlines.Any(viewOutline => bounds.Intersects(viewOutline, 0.5)) &&
        !occupiedAnnotationBounds.Any(existing => bounds.Intersects(existing, 2.0));

    private static double Distance(Point3D left, Point3D right)
    {
        var x = left.Xmm - right.Xmm;
        var y = left.Ymm - right.Ymm;
        var z = left.Zmm - right.Zmm;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private sealed record DimensionAttempt(CreatedDimension? Created, string? Reason);

    private static IReadOnlyList<VisibleVertex> GetVisibleVertices(View view)
    {
        if (view.GetVisibleComponents() is not Array components || components.Length == 0)
        {
            return Array.Empty<VisibleVertex>();
        }

        var component = components.GetValue(components.GetLowerBound(0)) as Component2;
        if (component is null || view.GetVisibleEntities2(component, (int)swViewEntityType_e.swViewEntityType_Vertex) is not Array entities)
        {
            return Array.Empty<VisibleVertex>();
        }

        var vertices = new List<VisibleVertex>();
        foreach (var entity in entities)
        {
            if (entity is not Vertex vertex || vertex.GetPoint() is not Array point || point.Length < 3)
            {
                continue;
            }

            var lowerBound = point.GetLowerBound(0);
            vertices.Add(new VisibleVertex(
                vertex,
                Convert.ToDouble(point.GetValue(lowerBound)) * MillimetersPerMeter,
                Convert.ToDouble(point.GetValue(lowerBound + 1)) * MillimetersPerMeter,
                Convert.ToDouble(point.GetValue(lowerBound + 2)) * MillimetersPerMeter));
        }

        return vertices;
    }

    private static (VisibleVertex Minimum, VisibleVertex Maximum)? FindExtremeVertices(
        IReadOnlyList<VisibleVertex> vertices,
        string axis,
        double expectedValueMm)
    {
        if (vertices.Count < 2)
        {
            return null;
        }

        var ordered = axis switch
        {
            "X" => vertices.OrderBy(vertex => vertex.Xmm).ToArray(),
            "Y" => vertices.OrderBy(vertex => vertex.Ymm).ToArray(),
            "Z" => vertices.OrderBy(vertex => vertex.Zmm).ToArray(),
            _ => Array.Empty<VisibleVertex>()
        };
        if (ordered.Length < 2)
        {
            return null;
        }

        var actualExtent = axis switch
        {
            "X" => ordered[^1].Xmm - ordered[0].Xmm,
            "Y" => ordered[^1].Ymm - ordered[0].Ymm,
            _ => ordered[^1].Zmm - ordered[0].Zmm
        };
        return Math.Abs(actualExtent - expectedValueMm) <= GeometryMatchToleranceMm
            ? (ordered[0], ordered[^1])
            : null;
    }

    private static SheetOutline ReadOutline(View view)
    {
        if (view.GetOutline() is not Array outline || outline.Length < 4)
        {
            throw new InvalidOperationException($"Could not read the outline of drawing view '{view.GetName2()}'.");
        }

        var lowerBound = outline.GetLowerBound(0);
        return new SheetOutline(
            Convert.ToDouble(outline.GetValue(lowerBound)) * MillimetersPerMeter,
            Convert.ToDouble(outline.GetValue(lowerBound + 1)) * MillimetersPerMeter,
            Convert.ToDouble(outline.GetValue(lowerBound + 2)) * MillimetersPerMeter,
            Convert.ToDouble(outline.GetValue(lowerBound + 3)) * MillimetersPerMeter);
    }

    private static SheetOutline GetEstimatedTextBounds(double xMm, double yMm, double valueMm, bool horizontal)
    {
        var labelWidth = Math.Max(18.0, valueMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).Length * 2.5);
        const double labelHeight = 8.0;
        return new SheetOutline(
            xMm - (horizontal ? labelWidth / 2.0 : labelHeight / 2.0),
            yMm - (horizontal ? labelHeight / 2.0 : labelWidth / 2.0),
            xMm + (horizontal ? labelWidth / 2.0 : labelHeight / 2.0),
            yMm + (horizontal ? labelHeight / 2.0 : labelWidth / 2.0));
    }

    private sealed record VisibleVertex(Vertex Vertex, double Xmm, double Ymm, double Zmm);

    private sealed record SheetOutline(double LeftMm, double BottomMm, double RightMm, double TopMm)
    {
        public bool Intersects(SheetOutline other, double clearanceMm) =>
            LeftMm < other.RightMm + clearanceMm && RightMm > other.LeftMm - clearanceMm &&
            BottomMm < other.TopMm + clearanceMm && TopMm > other.BottomMm - clearanceMm;
    }
}

public sealed record CreatedDimension(
    string SemanticId,
    string Kind,
    double MeasuredValueMm,
    string ViewName,
    double TextXmm,
    double TextYmm);

public sealed record UnresolvedDimension(
    string SemanticId,
    string Kind,
    string Reason);

public sealed record DimensionExecutionReport(
    IReadOnlyList<CreatedDimension> Created,
    IReadOnlyList<UnresolvedDimension> Unresolved,
    bool SemanticIdsUnique);
