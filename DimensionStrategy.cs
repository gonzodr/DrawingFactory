public sealed class DimensionStrategy
{
    public DimensionPlan Create(ManufacturingFeatureSet features, DrawingPlan drawingPlan)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(drawingPlan);

        var requirements = new List<DimensionRequirement>();
        var omissions = new List<string>();
        var (horizontalAxis, verticalAxis) = DrawingViewGeometry.GetAxes(drawingPlan.PrimaryView?.ModelViewName);
        var horizontalId = horizontalAxis.ToLowerInvariant();
        var verticalId = verticalAxis.ToLowerInvariant();

        requirements.Add(new DimensionRequirement(
            "overall-width",
            ManufacturingDimensionKind.OverallWidth,
            DrawingViewGeometry.GetExtent(features, horizontalAxis),
            "Primary",
            $"Model {horizontalAxis} extents",
            1,
            null));
        requirements.Add(new DimensionRequirement(
            "overall-height",
            ManufacturingDimensionKind.OverallHeight,
            DrawingViewGeometry.GetExtent(features, verticalAxis),
            "Primary",
            $"Model {verticalAxis} extents",
            1,
            null));
        requirements.Add(new DimensionRequirement(
            "thickness",
            ManufacturingDimensionKind.Thickness,
            features.ThicknessMm,
            "Thickness",
            $"Model {features.ThicknessAxis} extents",
            1,
            null));

        foreach (var radius in features.CornerRadiiMm)
        {
            var radiusEdges = features.PlanarBoundaries
                .Where(edge => edge.IsOuterBoundary && edge.Center is not null && edge.RadiusMm.HasValue &&
                               Math.Abs(edge.RadiusMm.Value - radius) <= 0.05)
                .ToArray();
            requirements.Add(new DimensionRequirement(
                FormattableString.Invariant($"corner-radius-{radius:0.###}"),
                ManufacturingDimensionKind.CornerRadius,
                radius,
                "Primary",
                "Outer planar boundary arc",
                radiusEdges.Length,
                radiusEdges.Length == 0 ? "No outer arc center was captured from the model." : null,
                radiusEdges.FirstOrDefault()?.Center,
                null,
                radiusEdges.Length > 1 ? $"{radiusEdges.Length}X " : "R"));
        }

        foreach (var group in features.HoleGroups)
        {
            var firstHole = group.Holes.FirstOrDefault();
            var groupId = FormattableString.Invariant($"hole-diameter-{group.DiameterMm:0.###}");
            requirements.Add(new DimensionRequirement(
                groupId,
                ManufacturingDimensionKind.HoleDiameter,
                group.DiameterMm,
                "Primary",
                "Matched inner planar loop and cylindrical face",
                group.Count,
                firstHole is null ? "No hole center was captured from the model." : null,
                firstHole?.Center,
                null,
                group.Count > 1 ? $"{group.Count}X " : null));

            if (group.Count > 1)
            {
                requirements.Add(new DimensionRequirement(
                    FormattableString.Invariant($"hole-count-{group.DiameterMm:0.###}"),
                    ManufacturingDimensionKind.RepeatedHoleCount,
                    null,
                    "Primary",
                    groupId,
                    group.Count,
                    "Count is represented once by the grouped diameter prefix; a separate count note would be redundant."));
            }

            var horizontalPitch = FindAdjacentPair(group.Holes, horizontalAxis, verticalAxis);
            if (horizontalPitch is not null)
            {
                requirements.Add(new DimensionRequirement(
                    FormattableString.Invariant($"hole-pattern-pitch-{horizontalId}-{group.DiameterMm:0.###}"),
                    ManufacturingDimensionKind.HorizontalHolePatternPitch,
                    Math.Abs(DrawingViewGeometry.GetCoordinate(horizontalPitch.Value.Second.Center, horizontalAxis) -
                             DrawingViewGeometry.GetCoordinate(horizontalPitch.Value.First.Center, horizontalAxis)),
                    "Primary",
                    groupId,
                    1,
                    null,
                    horizontalPitch.Value.First.Center,
                    horizontalPitch.Value.Second.Center,
                    null,
                    group.DiameterMm));
            }

            var verticalPitch = FindAdjacentPair(group.Holes, verticalAxis, horizontalAxis);
            if (verticalPitch is not null)
            {
                requirements.Add(new DimensionRequirement(
                    FormattableString.Invariant($"hole-pattern-pitch-{verticalId}-{group.DiameterMm:0.###}"),
                    ManufacturingDimensionKind.VerticalHolePatternPitch,
                    Math.Abs(DrawingViewGeometry.GetCoordinate(verticalPitch.Value.Second.Center, verticalAxis) -
                             DrawingViewGeometry.GetCoordinate(verticalPitch.Value.First.Center, verticalAxis)),
                    "Primary",
                    groupId,
                    1,
                    null,
                    verticalPitch.Value.First.Center,
                    verticalPitch.Value.Second.Center,
                    null,
                    group.DiameterMm));
            }

            foreach (var hole in group.Holes.DistinctBy(hole =>
                         (DrawingViewGeometry.GetCoordinate(hole.Center, horizontalAxis),
                          DrawingViewGeometry.GetCoordinate(hole.Center, verticalAxis))))
            {
                var horizontalPosition = DrawingViewGeometry.GetCoordinate(hole.Center, horizontalAxis);
                var verticalPosition = DrawingViewGeometry.GetCoordinate(hole.Center, verticalAxis);
                var positionId = FormattableString.Invariant($"{group.DiameterMm:0.###}-{horizontalPosition:0.###}-{verticalPosition:0.###}");
                requirements.Add(new DimensionRequirement(
                    $"hole-{horizontalId}-{positionId}",
                    ManufacturingDimensionKind.HorizontalHolePosition,
                    horizontalPosition,
                    "Primary",
                    groupId,
                    1,
                    "The model origin has not been established as a manufacturing datum."));
                requirements.Add(new DimensionRequirement(
                    $"hole-{verticalId}-{positionId}",
                    ManufacturingDimensionKind.VerticalHolePosition,
                    verticalPosition,
                    "Primary",
                    groupId,
                    1,
                    "The model origin has not been established as a manufacturing datum."));
            }
        }

        foreach (var thread in features.ThreadedFeatures)
        {
            var explicitCallout = string.Join(" ", new[]
            {
                thread.FastenerType,
                thread.FastenerSize,
                thread.ThreadClass
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            requirements.Add(new DimensionRequirement(
                $"thread-{thread.FeatureName}",
                ManufacturingDimensionKind.ThreadCallout,
                thread.ThreadDiameterMm,
                "Primary",
                explicitCallout,
                1,
                "Explicit Hole Wizard data was found, but its hole edge has not been associated with a view callout target."));
        }

        if (features.HoleGroups.Count == 0)
        {
            omissions.Add("No hole diameters, repeated-hole groups, or hole positions were emitted: no broad-face circular boundaries matched cylindrical faces.");
        }

        if (features.CornerRadiiMm.Count == 0)
        {
            omissions.Add("No corner-radius dimensions were emitted: no outer circular boundary arcs were detected on the dominant planar face.");
        }

        if (features.ThreadedFeatures.Count == 0)
        {
            omissions.Add("No thread callouts were emitted: no explicit Hole Wizard thread/fastener metadata was found.");
        }

        return new DimensionPlan(requirements, omissions);
    }

    private static (ManufacturingHole First, ManufacturingHole Second)? FindAdjacentPair(
        IReadOnlyList<ManufacturingHole> holes,
        string measuredAxis,
        string alignmentAxis)
    {
        var coordinateGroups = holes.GroupBy(hole =>
            Math.Round(DrawingViewGeometry.GetCoordinate(hole.Center, alignmentAxis), 1));

        foreach (var group in coordinateGroups.OrderBy(group => group.Key))
        {
            var ordered = group
                .OrderBy(hole => DrawingViewGeometry.GetCoordinate(hole.Center, measuredAxis))
                .DistinctBy(hole => DrawingViewGeometry.GetCoordinate(hole.Center, measuredAxis))
                .Take(2)
                .ToArray();
            if (ordered.Length == 2)
            {
                return (ordered[0], ordered[1]);
            }
        }

        return null;
    }
}
