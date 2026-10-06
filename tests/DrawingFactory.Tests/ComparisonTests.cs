using System.Globalization;
using SolidWorks.Interop.swconst;
using Xunit;

namespace DrawingFactory.Tests;

public sealed class ComparisonTests
{
    [Fact]
    public void Compare_IdenticalSnapshotsHaveNoDifferences()
    {
        var drawing = Drawing(Sheet(View("Front", Dimension(referencePoints: [Point(10)]))));

        Assert.False(Compare(drawing, drawing).HasDifferences);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RegressionRun_IsSuccessfulWhenAllComparisonsMatchAndNoSamplesAreSkipped(int comparisonCount)
    {
        var drawing = Drawing(Sheet(View()));
        var comparison = Compare(drawing, drawing);
        var result = new RegressionRunResult(
            "training", "output", Enumerable.Repeat(comparison, comparisonCount).ToArray(), [],
            DrawingStyleProfile.Observe([]));

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void RegressionRun_IsUnsuccessfulWhenAnyComparisonDiffers()
    {
        var drawing = Drawing(Sheet(View()));
        var matching = Compare(drawing, drawing);
        var differing = Compare(drawing, Drawing(Sheet(View("Top"))));
        var result = new RegressionRunResult(
            "training", "output", [matching, differing], [], DrawingStyleProfile.Observe([]));

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void RegressionRun_IsUnsuccessfulWhenSamplesAreSkippedDespiteMatchingComparisons()
    {
        var drawing = Drawing(Sheet(View()));
        var comparison = Compare(drawing, drawing);
        var result = new RegressionRunResult(
            "training", "output", [comparison], ["Sample could not be analyzed."], DrawingStyleProfile.Observe([]));

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Compare_RejectsNullSnapshots()
    {
        var evaluator = new DrawingEvaluator();
        var drawing = Drawing();

        Assert.Throws<ArgumentNullException>(() => evaluator.Compare("sample", null!, drawing));
        Assert.Throws<ArgumentNullException>(() => evaluator.Compare("sample", drawing, null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compare_ReportsUnmatchedEmptySheets(bool extra)
    {
        var oneSheet = Drawing(Sheet());
        var twoSheets = Drawing(Sheet(), Sheet() with { Name = "Second" });

        var result = extra ? Compare(oneSheet, twoSheets) : Compare(twoSheets, oneSheet);

        Assert.Single(result.SheetDifferences);
        Assert.Contains(extra ? "unnecessary sheet" : "missing sheet", result.SheetDifferences[0]);
        Assert.True(result.HasDifferences);
    }

    [Theory]
    [InlineData(2, 2, 0, 0)]
    [InlineData(1, 2, 0, 1)]
    [InlineData(2, 1, 1, 0)]
    public void Compare_PreservesViewMultiplicity(int expectedCount, int actualCount, int missing, int extra)
    {
        var view = View();
        var reference = Drawing(Sheet(Enumerable.Repeat(view, expectedCount).ToArray()));
        var generated = Drawing(Sheet(Enumerable.Repeat(view, actualCount).ToArray()));

        var result = Compare(reference, generated);

        Assert.Equal(missing, result.MissingViews.Count);
        Assert.Equal(extra, result.UnnecessaryViews.Count);
        Assert.Empty(result.WrongOrientations);
    }

    [Fact]
    public void Compare_ReservesExactViewsBeforeDiagnosingWrongOrientations()
    {
        var reference = Drawing(Sheet(View("Front"), View("Top")));
        var generated = Drawing(Sheet(View("Top"), View("Right")));

        var result = Compare(reference, generated);

        var difference = Assert.Single(result.WrongOrientations);
        Assert.Contains("'Front' expected, got 'Right'", difference);
        Assert.Empty(result.MissingViews);
        Assert.Empty(result.UnnecessaryViews);
    }

    [Fact]
    public void Compare_MatchesRepeatedViewSignaturesByPlacement()
    {
        var left = View() with { Position = Point(20, 50) };
        var right = View() with { Position = Point(80, 50) };

        var result = Compare(Drawing(Sheet(left, right)), Drawing(Sheet(right, left)));

        Assert.False(result.HasDifferences);
    }

    [Theory]
    [InlineData("*Front")]
    [InlineData(" * Front ")]
    [InlineData("front")]
    public void Compare_NormalizesStandardOrientationNames(string orientation)
    {
        var result = Compare(Drawing(Sheet(View("Front"))), Drawing(Sheet(View(orientation))));

        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Compare_ReportsViewScaleEvenWhenSheetScaleMatches()
    {
        var view = View();

        var result = Compare(Drawing(Sheet(view)), Drawing(Sheet(view with { Scale = 0.5 })));

        Assert.Single(result.ScaleDifferences);
        Assert.Contains("view", result.ScaleDifferences[0]);
    }

    [Theory]
    [InlineData(2, 2, 0, 0)]
    [InlineData(1, 2, 0, 1)]
    [InlineData(2, 1, 1, 0)]
    public void Compare_PreservesDimensionMultiplicity(int expectedCount, int actualCount, int missing, int extra)
    {
        var dimension = Dimension();
        var reference = Drawing(Sheet(View("Front", Enumerable.Repeat(dimension, expectedCount).ToArray())));
        var generated = Drawing(Sheet(View("Front", Enumerable.Repeat(dimension, actualCount).ToArray())));

        var result = Compare(reference, generated);

        Assert.Equal(missing, result.MissingDimensions.Count);
        Assert.Equal(extra, result.UnnecessaryDimensions.Count);
    }

    [Fact]
    public void Compare_MatchesEqualDimensionValuesByGeometry()
    {
        var first = Dimension(textPosition: Point(10), referencePoints: [Point(10)]);
        var second = Dimension(textPosition: Point(80), referencePoints: [Point(80)]);

        var result = Compare(
            Drawing(Sheet(View("Front", first, second))),
            Drawing(Sheet(View("Front", second, first))));

        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Compare_MatchesEqualDimensionValuesByTextPositionWhenGeometryIsUnavailable()
    {
        var first = Dimension(textPosition: Point(10));
        var second = Dimension(textPosition: Point(80));

        var result = Compare(
            Drawing(Sheet(View("Front", first, second))),
            Drawing(Sheet(View("Front", second, first))));

        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Compare_ToleranceMatchingDoesNotLoseValidDimensionPairs()
    {
        var reference = Drawing(Sheet(View("Front", Dimension(10.04), Dimension(10))));
        var generated = Drawing(Sheet(View("Front", Dimension(10), Dimension(10.08))));

        Assert.False(Compare(reference, generated).HasDifferences);
    }

    [Theory]
    [InlineData("mm", 10.04, false)]
    [InlineData("mm", 10.08, true)]
    [InlineData("deg", 10.08, false)]
    [InlineData("deg", 10.11, true)]
    public void Compare_UsesExistingUnitSpecificTolerances(string unit, double actualValue, bool differs)
    {
        var result = Compare(
            Drawing(Sheet(View("Front", Dimension(10, unit: unit)))),
            Drawing(Sheet(View("Front", Dimension(actualValue, unit: unit)))));

        Assert.Equal(differs, result.HasDifferences);
        Assert.Equal(differs ? 1 : 0, result.MissingDimensions.Count);
        Assert.Equal(differs ? 1 : 0, result.UnnecessaryDimensions.Count);
    }

    [Fact]
    public void Compare_ReferencePointMatchingCanReassignEarlierToleranceMatches()
    {
        var expected = Dimension(referencePoints: [Point(0.04), Point(-0.04), Point(-0.04)]);
        var actual = Dimension(referencePoints: [Point(0), Point(0.08), Point(-0.08)]);

        var result = Compare(Drawing(Sheet(View("Front", expected))), Drawing(Sheet(View("Front", actual))));

        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Compare_ReportsDifferentGeometryAndTrueDuplicateDimensions()
    {
        var expected = Dimension(referencePoints: [Point(10)]);
        var actual = Dimension(referencePoints: [Point(80)]);

        var result = Compare(Drawing(Sheet(View("Front", expected))), Drawing(Sheet(View("Front", actual, actual))));

        Assert.Single(result.DimensionPlacementDifferences);
        Assert.Single(result.RedundantDimensions);
        Assert.Single(result.UnnecessaryDimensions);
    }

    [Fact]
    public void Compare_ExtraNativeAnnotationIsNotReportedAsMissing()
    {
        var reference = Drawing(Sheet());
        var generated = Drawing(Sheet() with { Annotations = [Annotation("SurfaceFinish", "Ra 3.2")] });

        var result = Compare(reference, generated);

        Assert.Empty(result.MissingNativeAnnotations);
        Assert.Single(result.UnnecessaryNativeAnnotations);
    }

    [Fact]
    public void Compare_NativeContentReplacementReportsBothMissingAndExtraContent()
    {
        var reference = Drawing(Sheet() with { Annotations = [Annotation("SurfaceFinish", "Ra 3.2")] });
        var generated = Drawing(Sheet() with { Annotations = [Annotation("SurfaceFinish", "Ra 6.3")] });

        var result = Compare(reference, generated);

        Assert.Single(result.MissingNativeAnnotations);
        Assert.Single(result.UnnecessaryNativeAnnotations);
    }

    [Fact]
    public void Compare_NativeContentNormalizationIgnoresWhitespaceAndCase()
    {
        var reference = Drawing(Sheet() with { Annotations = [Annotation("DatumTag", "Datum A")] });
        var generated = Drawing(Sheet() with { Annotations = [Annotation("DatumTag", " datum\tA ")] });

        Assert.False(Compare(reference, generated).HasDifferences);
    }

    [Fact]
    public void Compare_IncludesExtractionDiagnosticsEvenWithoutManufacturingAnalysis()
    {
        var reference = Drawing() with { UnresolvedInformation = ["Reference extraction failed."] };
        var generated = Drawing() with { UnresolvedInformation = ["Generated extraction failed."] };

        var result = new DrawingEvaluator().Compare("sample", reference, generated);

        Assert.Equal(3, result.UnresolvedManufacturingInformation.Count);
        Assert.Contains("Reference drawing: Reference extraction failed.", result.UnresolvedManufacturingInformation);
        Assert.Contains("Generated drawing: Generated extraction failed.", result.UnresolvedManufacturingInformation);
        Assert.True(result.HasDifferences);
    }

    [Fact]
    public void Compare_OrdinaryHoleCalloutDoesNotImplyThreadMetadata()
    {
        var drawing = Drawing(Sheet() with { Annotations = [Annotation("HoleCallout", "Diameter 8 THRU")] });

        Assert.False(Compare(drawing, drawing).HasDifferences);
    }

    [Fact]
    public void Compare_CosmeticThreadStillRequiresExplicitThreadMetadata()
    {
        var drawing = Drawing(Sheet() with { Annotations = [Annotation("CosmeticThread")] });

        Assert.Single(Compare(drawing, drawing).UnresolvedManufacturingInformation);
    }

    [Fact]
    public void Compare_DetectedHolesStillRequireNativeHoleCallouts()
    {
        var hole = new ManufacturingHole(8, new Point3D(10, 10, 0), new Vector3D(0, 0, 1), "model");
        var features = EmptyFeatures with
        {
            Holes = [hole],
            HoleGroups = [new HoleGroup(8, [hole], 1, false)]
        };
        var drawing = Drawing(Sheet(View()));

        var result = new DrawingEvaluator().Compare("sample", drawing, drawing, features);

        Assert.Single(result.UnresolvedManufacturingInformation);
    }

    [Theory]
    [InlineData("Révision", "Revision", "A", "A")]
    [InlineData("Weight", "Weight", "12,5", "12.5")]
    [InlineData("SW-Sheet Scale", "SW-Sheet Scale", "Scale 1 : 2", "1:2")]
    public void Compare_PreservesTitleBlockAliasAndNumericNormalization(
        string expectedName, string actualName, string expectedText, string actualText)
    {
        var reference = Drawing(Sheet() with { PropertyLinks = [Link(expectedName, expectedText)] });
        var generated = Drawing(Sheet() with { PropertyLinks = [Link(actualName, actualText)] });

        Assert.False(Compare(reference, generated).HasDifferences);
    }

    [Fact]
    public void Compare_ReportsMissingTitleBlockLink()
    {
        var reference = Drawing(Sheet() with { PropertyLinks = [Link("Revision", "A")] });

        Assert.Single(Compare(reference, Drawing(Sheet())).TitleBlockDifferences);
    }

    [Fact]
    public void Observe_UsesTheOwningSheetForEveryDimensionOccurrence()
    {
        var view = View("Front", Dimension(textPosition: Point(50, 100)));
        var first = Sheet(view) with { WidthMm = 100, HeightMm = 200 };
        var second = Sheet(view) with { WidthMm = 200, HeightMm = 400 };

        var profile = DrawingStyleProfile.Observe([Drawing(first, second)]);

        Assert.Equal(0.375, profile.AverageNormalizedDimensionX, 10);
        Assert.Equal(0.375, profile.AverageNormalizedDimensionY, 10);
    }

    [Fact]
    public void Observe_SheetSizeAndScaleKeysAreCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("hu-HU");
            var sheet = Sheet() with { WidthMm = 210.5, HeightMm = 297.5, ScaleDenominator = 2 };

            var profile = DrawingStyleProfile.Observe([Drawing(sheet)]);

            Assert.Equal(1, profile.SheetSizeFrequencies["210.5x297.5 mm"]);
            Assert.Equal(1, profile.ScaleFrequencies["1:2"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Observe_GroupsEquivalentOrientationNames()
    {
        var profile = DrawingStyleProfile.Observe([Drawing(Sheet(View("*Front"), View(" front "), View("* Front")))]);

        Assert.Single(profile.ViewTypeOrientationFrequencies);
        Assert.Equal(3, profile.ViewTypeOrientationFrequencies["Model:FRONT"]);
    }

    [Fact]
    public void Observe_IncludesZeroNearestSpacingButExcludesIsolatedDimensions()
    {
        var view = View("Front",
            Dimension(textPosition: Point(0)),
            Dimension(textPosition: Point(0)),
            Dimension(textPosition: Point(10)));
        var isolated = View("Top", Dimension(textPosition: Point(100)));

        var profile = DrawingStyleProfile.Observe([Drawing(Sheet(view, isolated))]);

        Assert.Equal(10.0 / 3.0, profile.AverageNearestDimensionSpacingMm, 10);
    }

    [Fact]
    public void Observe_PolicyRatesCountDrawingsRatherThanAnnotations()
    {
        var sheet = Sheet(View("Front", Dimension() with { IsHoleCallout = true })) with
        {
            Annotations = [Annotation("CenterMark"), Annotation("CenterMark"), Annotation("Centerline")]
        };

        var profile = DrawingStyleProfile.Observe([Drawing(sheet), Drawing(Sheet())]);

        Assert.Equal(2, profile.ReferenceSampleCount);
        Assert.Equal(0.5, profile.CenterMarkPolicyRate);
        Assert.Equal(0.5, profile.CenterlinePolicyRate);
        Assert.Equal(0.5, profile.HoleCalloutPolicyRate);
        Assert.Equal(2, profile.NativeAnnotationFrequencies["CenterMark"]);
    }

    [Fact]
    public void Observe_PreservesDimensionLanesAndTopToBottomOrdering()
    {
        var above = Dimension(textPosition: Point(50, 90)) with { TypeName = "Above" };
        var below = Dimension(textPosition: Point(50, 10)) with { TypeName = "Below" };
        var left = Dimension(textPosition: Point(10, 50)) with { TypeName = "Left" };
        var right = Dimension(textPosition: Point(90, 50)) with { TypeName = "Right" };
        var inside = Dimension(textPosition: Point(50, 50)) with { TypeName = "Inside" };
        var unknown = Dimension() with { TypeName = "Unknown" };
        var view = View("Front", below, right, unknown, inside, left, above) with
        {
            Outline = new DrawingBounds(20, 20, 80, 80)
        };

        var profile = DrawingStyleProfile.Observe([Drawing(Sheet(view))]);

        Assert.Equal(1, profile.DimensionLaneFrequencies["AboveView"]);
        Assert.Equal(1, profile.DimensionLaneFrequencies["BelowView"]);
        Assert.Equal(1, profile.DimensionLaneFrequencies["LeftOfView"]);
        Assert.Equal(1, profile.DimensionLaneFrequencies["RightOfView"]);
        Assert.Equal(1, profile.DimensionLaneFrequencies["InsideView"]);
        Assert.Equal(1, profile.DimensionLaneFrequencies["Unknown"]);
        Assert.Equal(1, profile.DimensionOrderingFrequencies["Above > Left > Inside > Right > Below > Unknown"]);
    }

    [Fact]
    public void Observe_NormalizesHoleCalloutNumbersWithoutLosingStyle()
    {
        var hole = Dimension() with { IsHoleCallout = true, DisplayText = "4x D8,5 THRU" };

        var profile = DrawingStyleProfile.Observe([Drawing(Sheet(View("Front", hole)))]);

        Assert.Equal(1, profile.HoleCalloutStyleFrequencies["#x D# THRU"]);
    }

    [Fact]
    public void Observe_ExcludesSheetViewsFromDimensionStatistics()
    {
        var sheetView = View("Sheet", Dimension(textPosition: Point(50))) with
        {
            TypeCode = (int)swDrawingViewTypes_e.swDrawingSheet
        };

        var profile = DrawingStyleProfile.Observe([Drawing(Sheet(sheetView))]);

        Assert.Empty(profile.DimensionTypeFrequencies);
        Assert.Empty(profile.ViewTypeOrientationFrequencies);
        Assert.Equal(0, profile.AverageNormalizedDimensionX);
    }

    [Fact]
    public void Observe_EmptySamplesProduceAnEmptyFiniteProfile()
    {
        var profile = DrawingStyleProfile.Observe([]);

        Assert.Equal(0, profile.ReferenceSampleCount);
        Assert.Empty(profile.SheetFormatFrequencies);
        Assert.Empty(profile.SheetSizeFrequencies);
        Assert.Empty(profile.ScaleFrequencies);
        Assert.Empty(profile.ViewTypeOrientationFrequencies);
        Assert.Empty(profile.DimensionTypeFrequencies);
        Assert.Equal(0, profile.CenterMarkPolicyRate);
        Assert.Equal(0, profile.CenterlinePolicyRate);
        Assert.Equal(0, profile.HoleCalloutPolicyRate);
        Assert.Equal(0, profile.AverageNormalizedDimensionX);
        Assert.Equal(0, profile.AverageNormalizedDimensionY);
        Assert.Equal(0, profile.AverageNearestDimensionSpacingMm);
    }

    [Fact]
    public void Observe_RejectsNullSamples()
    {
        Assert.Throws<ArgumentNullException>(() => DrawingStyleProfile.Observe(null!));
    }

    private static ManufacturingFeatureSet EmptyFeatures => new(100, 100, 10, 10, "Z", [], [], [], [], []);

    private static DrawingComparisonResult Compare(DrawingSnapshot reference, DrawingSnapshot generated) =>
        new DrawingEvaluator().Compare("sample", reference, generated, EmptyFeatures);

    private static DrawingSnapshot Drawing(params DrawingSheetSnapshot[] sheets) =>
        new("drawing.SLDDRW", "Drawing", DrawingSnapshotRole.Reference, sheets, []);

    private static DrawingSheetSnapshot Sheet(params DrawingViewSnapshot[] views) =>
        new("Sheet", "format.slddrt", 100, 100, 1, 1, views, [], []);

    private static DrawingViewSnapshot View(string orientation = "Front", params DrawingDimensionSnapshot[] dimensions) =>
        new("View", 100, "Model", orientation, "part.SLDPRT", "Default", 1, null, null, dimensions, []);

    private static DrawingDimensionSnapshot Dimension(
        double value = 10,
        DrawingPoint? textPosition = null,
        IReadOnlyList<DrawingPoint>? referencePoints = null,
        string unit = "mm") =>
        new("Dimension", 1, unit == "deg" ? "Angular" : "Linear", value, unit, "Dimension", textPosition,
            referencePoints ?? [], [], false, false);

    private static DrawingPoint Point(double x, double y = 0) => new(x, y, 0);

    private static DrawingAnnotationSnapshot Annotation(string kind, string text = "") =>
        new(kind, 1, kind, text, "", null, null, [], true);

    private static DrawingPropertyLinkSnapshot Link(string name, string resolvedText) =>
        new("Note", $"$PRPSHEET:\"{name}\"", resolvedText, [new DrawingPropertyLinkToken("PRPSHEET", name)]);
}
