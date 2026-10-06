using System.Globalization;
using Xunit;

public sealed class PlanningTests
{
    [Theory]
    [InlineData(5, 100, 80, 1, 0, 0, "X", "*Right", "*Front")]
    [InlineData(5, 100, 80, -1, 0, 0, "X", "*Left", "*Front")]
    [InlineData(100, 5, 80, 0, 1, 0, "Y", "*Top", "*Front")]
    [InlineData(100, 5, 80, 0, -1, 0, "Y", "*Bottom", "*Front")]
    [InlineData(100, 80, 5, 0, 0, 1, "Z", "*Front", "*Top")]
    [InlineData(100, 80, 5, 0, 0, -1, "Z", "*Back", "*Top")]
    public void FlatPart_SelectsFaceNormalAndPerpendicularThicknessView(
        double x, double y, double z, double nx, double ny, double nz,
        string thicknessAxis, string primaryView, string thicknessView)
    {
        var analysis = CreateAnalysis(x, y, z, new PlanarFaceAnalysis(8000, new Vector3D(nx, ny, nz)));

        Assert.True(new FlatPartStrategy().TryCreatePlan(analysis, out var plan, out var diagnostics));
        Assert.NotNull(plan);
        Assert.Equal("FlatPart", plan.StrategyName);
        Assert.Equal(0.2, plan.TargetScale);
        Assert.Equal(thicknessAxis, plan.ThicknessAxis);
        Assert.Equal(primaryView, plan.PrimaryView!.ModelViewName);
        var secondary = Assert.Single(plan.RequestedSecondaryViews);
        Assert.Equal("Thickness", secondary.Purpose);
        Assert.Equal(thicknessView, secondary.ModelViewName);
        Assert.Same(plan.Diagnostics, diagnostics);
    }

    [Theory]
    [InlineData(8, 2800, true)]
    [InlineData(8.001, 2800, false)]
    [InlineData(8, 2799.99, false)]
    public void FlatPart_ThicknessAndFaceAreaThresholdsAreInclusive(double thickness, double area, bool expected)
    {
        var analysis = CreateAnalysis(100, 80, thickness, new PlanarFaceAnalysis(area, new Vector3D(0, 0, 1)));
        Assert.Equal(expected, new FlatPartStrategy().TryCreatePlan(analysis, out _, out _));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FlatPart_RejectsInvalidExtents(double extent)
    {
        var analysis = CreateAnalysis(extent, 80, 5, new PlanarFaceAnalysis(8000, new Vector3D(0, 0, 1)));
        Assert.False(new FlatPartStrategy().TryCreatePlan(analysis, out var plan, out var diagnostics));
        Assert.Null(plan);
        Assert.NotEmpty(diagnostics);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FlatPart_RejectsNonFiniteFaceAreas(double area)
    {
        var analysis = CreateAnalysis(100, 80, 5, new PlanarFaceAnalysis(area, new Vector3D(0, 0, 1)));
        Assert.False(new FlatPartStrategy().TryCreatePlan(analysis, out _, out _));
    }

    [Fact]
    public void FlatPart_RejectsNonFiniteFaceNormals()
    {
        var analysis = CreateAnalysis(100, 80, 5, new PlanarFaceAnalysis(8000, new Vector3D(double.PositiveInfinity, 0, 1)));
        Assert.False(new FlatPartStrategy().TryCreatePlan(analysis, out _, out _));
    }

    [Fact]
    public void FlatPart_ChoosesLargestQualifyingFaceRatherThanUnalignedFace()
    {
        var analysis = CreateAnalysis(100, 80, 5,
            new PlanarFaceAnalysis(7000, new Vector3D(1, 0, 0)),
            new PlanarFaceAnalysis(3000, new Vector3D(0, 0, 1)),
            new PlanarFaceAnalysis(3500, new Vector3D(0, 0, -1)));

        Assert.True(new FlatPartStrategy().TryCreatePlan(analysis, out var plan, out _));
        Assert.Equal(3500, plan!.DominantPlanarFaceAreaMm2);
        Assert.Equal("*Back", plan.PrimaryView!.ModelViewName);
    }

    [Theory]
    [InlineData(500, 1500, 5, 0, 0, 1, "*Front")]
    [InlineData(500, 5, 1500, 0, 1, 0, "*Top")]
    public void FlatPart_DoesNotRotateProjectedExtentsWhenSelectingSheet(
        double x, double y, double z, double nx, double ny, double nz, string viewName)
    {
        var analysis = CreateAnalysis(x, y, z, new PlanarFaceAnalysis(750000, new Vector3D(nx, ny, nz)));

        Assert.True(new FlatPartStrategy().TryCreatePlan(analysis, out var plan, out _));
        Assert.Equal(viewName, plan!.PrimaryView!.ModelViewName);
        Assert.Equal(DrawingSheetRecommendation.A0Landscape, plan.SheetRecommendation);
    }

    [Fact]
    public void Selector_UnknownPlanPreservesGeometryAndFailureDiagnostics()
    {
        var analysis = CreateAnalysis(100, 80, 5);
        var plan = new DrawingStrategySelector().Select(analysis);

        Assert.Equal("Unknown", plan.StrategyName);
        Assert.Equal(0, plan.TargetScale);
        Assert.Null(plan.PrimaryView);
        Assert.Empty(plan.RequestedSecondaryViews);
        Assert.Equal(100, plan.BoundingBoxXmm);
        Assert.Equal(80, plan.BoundingBoxYmm);
        Assert.Equal(5, plan.BoundingBoxZmm);
        Assert.Contains(plan.Diagnostics, message => message.Contains("no sufficiently large planar face", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanningEntryPoints_RejectNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new FlatPartStrategy().TryCreatePlan(null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => new DrawingStrategySelector().Select(null!));
        Assert.Throws<ArgumentNullException>(() => new DimensionStrategy().Create(null!, CreateDrawingPlan()));
        Assert.Throws<ArgumentNullException>(() => new DimensionStrategy().Create(CreateFeatures(), null!));
        Assert.Throws<ArgumentNullException>(() => new DimensionLayoutPlanner().Plan(null!, EmptyDimensions(), CreateFeatures()));
        Assert.Throws<ArgumentNullException>(() => new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), null!, CreateFeatures()));
        Assert.Throws<ArgumentNullException>(() => new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), EmptyDimensions(), null!));
    }

    [Theory]
    [InlineData("*Front", "X", "Y", 100, 200)]
    [InlineData("*Back", "X", "Y", 100, 200)]
    [InlineData("*Top", "X", "Z", 100, 300)]
    [InlineData("*Bottom", "X", "Z", 100, 300)]
    [InlineData("*Left", "Y", "Z", 200, 300)]
    [InlineData("*Right", "Y", "Z", 200, 300)]
    [InlineData(" top view ", "X", "Z", 100, 300)]
    [InlineData(" RIGHT VIEW ", "Y", "Z", 200, 300)]
    public void Dimensions_ProjectOverallExtentsPitchesAndPositionsIntoPrimaryView(
        string viewName, string horizontalAxis, string verticalAxis, double width, double height)
    {
        var holes = new[]
        {
            CreateHole(ProjectedPoint(horizontalAxis, verticalAxis, 30, 45)),
            CreateHole(ProjectedPoint(horizontalAxis, verticalAxis, 10, 15)),
            CreateHole(ProjectedPoint(horizontalAxis, verticalAxis, 30, 15)),
            CreateHole(ProjectedPoint(horizontalAxis, verticalAxis, 10, 45))
        };
        var dimensions = new DimensionStrategy().Create(CreateFeatures(100, 200, 300, holes), CreateDrawingPlan(viewName));

        Assert.Equal(width, Find(dimensions, ManufacturingDimensionKind.OverallWidth).ValueMm);
        Assert.Equal(height, Find(dimensions, ManufacturingDimensionKind.OverallHeight).ValueMm);
        var horizontal = Find(dimensions, ManufacturingDimensionKind.HorizontalHolePatternPitch);
        var vertical = Find(dimensions, ManufacturingDimensionKind.VerticalHolePatternPitch);
        Assert.Equal(20, horizontal.ValueMm);
        Assert.Equal(30, vertical.ValueMm);
        Assert.Equal($"hole-pattern-pitch-{horizontalAxis.ToLowerInvariant()}-6.5", horizontal.SemanticId);
        Assert.Equal($"hole-pattern-pitch-{verticalAxis.ToLowerInvariant()}-6.5", vertical.SemanticId);
        Assert.Equal(ProjectedPoint(horizontalAxis, verticalAxis, 10, 15), horizontal.ReferenceCenter);
        Assert.Equal(ProjectedPoint(horizontalAxis, verticalAxis, 30, 15), horizontal.SecondaryReferenceCenter);
        Assert.Equal(ProjectedPoint(horizontalAxis, verticalAxis, 10, 45), vertical.SecondaryReferenceCenter);
        Assert.Equal(6.5, horizontal.ReferenceDiameterMm);
        Assert.Equal(6.5, vertical.ReferenceDiameterMm);

        var horizontalPositions = dimensions.Requirements.Where(item => item.Kind == ManufacturingDimensionKind.HorizontalHolePosition).ToArray();
        var verticalPositions = dimensions.Requirements.Where(item => item.Kind == ManufacturingDimensionKind.VerticalHolePosition).ToArray();
        Assert.Equal(new double?[] { 10, 30 }, horizontalPositions.Select(item => item.ValueMm).Distinct().OrderBy(value => value).ToArray());
        Assert.Equal(new double?[] { 15, 45 }, verticalPositions.Select(item => item.ValueMm).Distinct().OrderBy(value => value).ToArray());
        Assert.All(horizontalPositions.Concat(verticalPositions), item => Assert.NotNull(item.UnresolvedReason));
        Assert.Equal(dimensions.Requirements.Count, dimensions.Requirements.Select(item => item.SemanticId).Distinct().Count());
        Assert.Equal("4X ", Find(dimensions, ManufacturingDimensionKind.HoleDiameter).TextPrefix);
        Assert.NotNull(Find(dimensions, ManufacturingDimensionKind.RepeatedHoleCount).UnresolvedReason);
    }

    [Fact]
    public void Dimensions_SkipCoincidentProjectedCentersWhenFindingPitch()
    {
        var holes = new[]
        {
            CreateHole(new Point3D(10, 1, 15)),
            CreateHole(new Point3D(10, 2, 15)),
            CreateHole(new Point3D(30, 1, 15))
        };
        var dimensions = new DimensionStrategy().Create(CreateFeatures(holes: holes), CreateDrawingPlan());

        Assert.Equal(20, Find(dimensions, ManufacturingDimensionKind.HorizontalHolePatternPitch).ValueMm);
        Assert.DoesNotContain(dimensions.Requirements, item => item.Kind == ManufacturingDimensionKind.VerticalHolePatternPitch);
        Assert.Equal(2, dimensions.Requirements.Count(item => item.Kind == ManufacturingDimensionKind.HorizontalHolePosition));
        Assert.Equal(2, dimensions.Requirements.Count(item => item.Kind == ManufacturingDimensionKind.VerticalHolePosition));
        Assert.Equal(dimensions.Requirements.Count, dimensions.Requirements.Select(item => item.SemanticId).Distinct().Count());
    }

    [Fact]
    public void Dimensions_EmptyHoleGroupIsUnresolvedRatherThanIndexingMissingCenter()
    {
        var features = CreateFeatures() with
        {
            HoleGroups = new[] { new HoleGroup(6.5, Array.Empty<ManufacturingHole>(), 0, false) }
        };
        var dimensions = new DimensionStrategy().Create(features, CreateDrawingPlan());
        var diameter = Find(dimensions, ManufacturingDimensionKind.HoleDiameter);
        Assert.Null(diameter.ReferenceCenter);
        Assert.NotNull(diameter.UnresolvedReason);
        var layout = new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), dimensions, features);
        Assert.False(Assert.Single(layout.Placements, item => item.SemanticId == diameter.SemanticId).Executable);
    }

    [Fact]
    public void Dimensions_SemanticIdsAreCultureIndependent()
    {
        var features = CreateFeatures(holes: new[] { CreateHole(new Point3D(10.25, 2, 15.75)), CreateHole(new Point3D(30.25, 2, 15.75)) }) with
        {
            CornerRadiiMm = new[] { 2.5 },
            PlanarBoundaries = new[] { new PlanarBoundaryEdge("Circular arc", null, null, new Point3D(1, 2, 3), 2.5, true) }
        };
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = new DimensionStrategy().Create(features, CreateDrawingPlan());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = new DimensionStrategy().Create(features, CreateDrawingPlan());

            Assert.Equal(english.Requirements.Select(item => item.SemanticId), french.Requirements.Select(item => item.SemanticId));
            Assert.Contains(french.Requirements, item => item.SemanticId == "corner-radius-2.5");
            Assert.Contains(french.Requirements, item => item.SemanticId == "hole-x-6.5-10.25-15.75");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Dimensions_ReportOmissionsAndLeaveMissingArcTargetsUnresolved()
    {
        var empty = new DimensionStrategy().Create(CreateFeatures(), CreateDrawingPlan());
        Assert.Equal(3, empty.Requirements.Count);
        Assert.Equal(3, empty.IntentionalOmissions.Count);
        Assert.Equal("Thickness", Find(empty, ManufacturingDimensionKind.Thickness).ViewPurpose);

        var missingArc = new DimensionStrategy().Create(CreateFeatures() with { CornerRadiiMm = new[] { 2.5 } }, CreateDrawingPlan());
        var radius = Find(missingArc, ManufacturingDimensionKind.CornerRadius);
        Assert.Null(radius.ReferenceCenter);
        Assert.NotNull(radius.UnresolvedReason);
    }

    [Theory]
    [InlineData("*Front", 500, 1200, 5)]
    [InlineData("*Back", 500, 1200, 5)]
    [InlineData("*Top", 500, 5, 1200)]
    [InlineData("*Bottom", 500, 5, 1200)]
    [InlineData("*Left", 5, 500, 1200)]
    [InlineData("*Right", 5, 500, 1200)]
    public void Layout_UsesActualProjectedHeightAndReturnedViewCenter(string viewName, double x, double y, double z)
    {
        var features = CreateFeatures(x, y, z);
        var originalPlan = CreateDrawingPlan(viewName);
        var dimensions = new DimensionStrategy().Create(features, originalPlan);
        var layout = new DimensionLayoutPlanner().Plan(originalPlan, dimensions, features);

        Assert.Equal(DrawingSheetRecommendation.A1Landscape, layout.FinalDrawingPlan.SheetRecommendation);
        Assert.Equal(DrawingSheetRecommendation.A3Landscape, originalPlan.SheetRecommendation);
        Assert.Equal(0.68, layout.PrimaryCenterYFraction);
        Assert.True(layout.PrimaryCenterYFraction * 594 + 120 + layout.ReservedTopMm <= 594);
        Assert.Contains(layout.FinalDrawingPlan.Diagnostics, message => message.Contains("enlarged from A3Landscape", StringComparison.Ordinal));
        Assert.Empty(originalPlan.Diagnostics);
    }

    [Fact]
    public void Layout_ChecksSecondaryViewBoundsAtItsReturnedCenter()
    {
        var features = CreateFeatures(1500, 5, 100);
        var plan = CreateDrawingPlan("*Top", secondaryViewName: "*Front");
        var dimensions = new DimensionStrategy().Create(features, plan);
        var layout = new DimensionLayoutPlanner().Plan(plan, dimensions, features);

        Assert.Equal(DrawingSheetRecommendation.A1Landscape, layout.FinalDrawingPlan.SheetRecommendation);
        Assert.True(layout.ThicknessViewCenterXFraction * 841 - 150 >= 0);
        Assert.True(layout.ThicknessViewCenterYFraction * 594 - 0.5 >= layout.ReservedBottomMm);
    }

    [Fact]
    public void Layout_RejectsSecondaryViewThatLeavesEvenLargestSheet()
    {
        var features = CreateFeatures(4000, 5, 500);
        var plan = CreateDrawingPlan("*Top", DrawingSheetRecommendation.A0Landscape, "*Front");
        Assert.Throws<InvalidOperationException>(() => new DimensionLayoutPlanner().Plan(plan, EmptyDimensions(), features));
    }

    [Fact]
    public void Layout_DoesNotShrinkExistingSheetRecommendation()
    {
        var plan = CreateDrawingPlan(sheet: DrawingSheetRecommendation.A2Landscape);
        var layout = new DimensionLayoutPlanner().Plan(plan, EmptyDimensions(), CreateFeatures());
        Assert.Equal(DrawingSheetRecommendation.A2Landscape, layout.FinalDrawingPlan.SheetRecommendation);
        Assert.Same(plan.Diagnostics, layout.FinalDrawingPlan.Diagnostics);
    }

    [Fact]
    public void Layout_UnresolvedRequirementsDoNotConsumeExecutableLanes()
    {
        var dimensions = new DimensionPlan(new[]
        {
            Requirement("width", ManufacturingDimensionKind.OverallWidth, 100),
            Requirement("position", ManufacturingDimensionKind.HorizontalHolePosition, 10, "No manufacturing datum."),
            Requirement("unresolved-width", ManufacturingDimensionKind.OverallWidth, 100, "No target."),
            Requirement("pitch", ManufacturingDimensionKind.HorizontalHolePatternPitch, 20) with
            {
                ReferenceCenter = new Point3D(0, 0, 0),
                SecondaryReferenceCenter = new Point3D(20, 0, 0),
                ReferenceDiameterMm = 6.5
            }
        }, Array.Empty<string>());
        var layout = new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), dimensions, CreateFeatures());

        Assert.Equal(38, layout.ReservedTopMm);
        Assert.Equal(18, layout.ReservedLeftMm);
        Assert.Equal(18, layout.ReservedRightMm);
        Assert.Equal(22, layout.ReservedBottomMm);
        Assert.Equal(new[] { 1, 0, 0, 2 }, layout.Placements.Select(item => item.LaneNumber).ToArray());
        Assert.Equal(new[] { true, false, false, true }, layout.Placements.Select(item => item.Executable).ToArray());
        Assert.All(layout.Placements, placement => Assert.Equal(DimensionLane.Top, placement.Lane));
        Assert.Equal("No manufacturing datum.", layout.Placements[1].UnresolvedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Layout_NonNumericOrInvalidMeasurementsAreNotExecutable(double? value)
    {
        var dimensions = new DimensionPlan(new[] { Requirement("invalid", ManufacturingDimensionKind.OverallWidth, value) }, Array.Empty<string>());
        var layout = new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), dimensions, CreateFeatures());
        Assert.False(Assert.Single(layout.Placements).Executable);
        Assert.Equal(0, layout.Placements[0].LaneNumber);
        Assert.Equal(18, layout.ReservedTopMm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Layout_RejectsInvalidScale(double scale)
    {
        var plan = CreateDrawingPlan() with { TargetScale = scale };
        Assert.Throws<ArgumentException>(() => new DimensionLayoutPlanner().Plan(plan, EmptyDimensions(), CreateFeatures()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Layout_RejectsInvalidExtents(double extent)
    {
        Assert.Throws<ArgumentException>(() => new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), EmptyDimensions(), CreateFeatures(x: extent)));
    }

    [Fact]
    public void Layout_RejectsMissingPrimaryViewAndOversizedGeometry()
    {
        var missingPrimary = CreateDrawingPlan() with { PrimaryView = null };
        Assert.Throws<ArgumentException>(() => new DimensionLayoutPlanner().Plan(missingPrimary, EmptyDimensions(), CreateFeatures()));
        Assert.Throws<InvalidOperationException>(() => new DimensionLayoutPlanner().Plan(CreateDrawingPlan(), EmptyDimensions(), CreateFeatures(10000, 5, 10000)));
    }

    private static PartAnalysis CreateAnalysis(double x, double y, double z, params PlanarFaceAnalysis[] faces) =>
        new("Part", string.Empty, default, new PartGeometryAnalysis(x, y, z, faces), string.Empty,
            Array.Empty<string>(), Array.Empty<CustomPropertyAnalysis>(), Array.Empty<ConfigurationAnalysis>());

    private static DrawingPlan CreateDrawingPlan(
        string viewName = "*Top",
        DrawingSheetRecommendation sheet = DrawingSheetRecommendation.A3Landscape,
        string? secondaryViewName = null) =>
        new("FlatPart", sheet, 0.2, new PrimaryViewPlan(viewName, new Vector3D(0, 1, 0), "Test view"),
            secondaryViewName is null
                ? Array.Empty<SecondaryViewPlan>()
                : new[] { new SecondaryViewPlan("Thickness", secondaryViewName, "Test thickness view") },
            Array.Empty<string>(), 100, 5, 80, "Y", 8000);

    private static ManufacturingFeatureSet CreateFeatures(
        double x = 100, double y = 5, double z = 80, ManufacturingHole[]? holes = null)
    {
        holes ??= Array.Empty<ManufacturingHole>();
        return new ManufacturingFeatureSet(x, y, z, 5, "Y", Array.Empty<PlanarBoundaryEdge>(), holes,
            holes.Length == 0 ? Array.Empty<HoleGroup>() : new[] { new HoleGroup(6.5, holes, holes.Length, holes.Length > 1) },
            Array.Empty<double>(), Array.Empty<ThreadedFeatureAnalysis>());
    }

    private static ManufacturingHole CreateHole(Point3D center) => new(6.5, center, new Vector3D(0, 1, 0), "Test circle");

    private static Point3D ProjectedPoint(string horizontalAxis, string verticalAxis, double horizontal, double vertical)
    {
        double Coordinate(string axis) => axis == horizontalAxis ? horizontal : axis == verticalAxis ? vertical : 2;
        return new Point3D(Coordinate("X"), Coordinate("Y"), Coordinate("Z"));
    }

    private static DimensionRequirement Find(DimensionPlan plan, ManufacturingDimensionKind kind) =>
        Assert.Single(plan.Requirements, requirement => requirement.Kind == kind);

    private static DimensionRequirement Requirement(string id, ManufacturingDimensionKind kind, double? value, string? unresolvedReason = null) =>
        new(id, kind, value, "Primary", "Test feature", 1, unresolvedReason);

    private static DimensionPlan EmptyDimensions() => new(Array.Empty<DimensionRequirement>(), Array.Empty<string>());
}
