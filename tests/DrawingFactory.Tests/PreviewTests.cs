public sealed class PreviewTests
{
    [Fact]
    public void CreatesThreeFirstAngleViewsForNonFlatPart()
    {
        var geometry = Geometry(45, 30, 45);
        var layout = new PreviewLayoutPlanner().Plan(geometry, Style());
        Assert.Equal(DrawingSheetRecommendation.A4Landscape, layout.Sheet);
        Assert.Equal(1.5, layout.Scale);
        Assert.Equal(new[] { "*Front", "*Top", "*Right" }, layout.Views.Select(view => view.ModelViewName));
        Assert.True(layout.Views[1].CenterYmm < layout.Views[0].CenterYmm);
        Assert.True(layout.Views[2].CenterXmm < layout.Views[0].CenterXmm);
        AssertFits(layout);
    }

    [Theory]
    [InlineData(45, 30, 45)]
    [InlineData(100, 80, 60)]
    [InlineData(800, 3, 434)]
    [InlineData(5000, 3000, 5000)]
    public void ScalesToFitAllViewsWithoutOverlap(double x, double y, double z) =>
        AssertFits(new PreviewLayoutPlanner().Plan(Geometry(x, y, z)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidGeometryIsRejected(double extent) =>
        Assert.Throws<ArgumentException>(() => new PreviewLayoutPlanner().Plan(Geometry(extent, 30, 45)));

    [Fact]
    public void ParsesBatchPreviewPaths()
    {
        var root = Path.GetTempPath();
        var defaults = Assert.IsType<PreviewSamplesCommand>(CommandLine.Parse(new[] { "--PREVIEW" }, root));
        Assert.Equal(Path.Combine(root, "TrainingSamples"), defaults.TrainingRoot);
        Assert.Equal(Path.Combine(root, "TestOutput"), defaults.OutputRoot);
        var custom = Assert.IsType<PreviewSamplesCommand>(CommandLine.Parse(new[] { "--preview", "samples", "output", "template.DRWDOT" }));
        Assert.Equal("samples", custom.TrainingRoot);
        Assert.Equal("output", custom.OutputRoot);
        Assert.Equal("template.DRWDOT", custom.TemplatePath);
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--preview", "samples", "output", "template.DRWDOT", "extra" }));
    }

    private static void AssertFits(PreviewDrawingLayout layout)
    {
        var bounds = layout.Views.Select(view => new DrawingBounds(view.CenterXmm - view.WidthMm / 2,
            view.CenterYmm - view.HeightMm / 2, view.CenterXmm + view.WidthMm / 2, view.CenterYmm + view.HeightMm / 2)).ToArray();
        Assert.True(PreviewLayoutPlanner.Fits(bounds, layout.WidthMm, layout.HeightMm));
    }

    private static PartGeometryAnalysis Geometry(double x, double y, double z) => new(x, y, z, Array.Empty<PlanarFaceAnalysis>());

    private static LearnedDrawingStyle Style()
    {
        DrawingSnapshot Reference(double scale) => new("reference.SLDDRW", "reference", DrawingSnapshotRole.Reference,
            new[] { new DrawingSheetSnapshot("Sheet1", "A4 Fekvő JABIL.slddrt", 297, 210, scale, 1,
                Array.Empty<DrawingViewSnapshot>(), Array.Empty<DrawingAnnotationSnapshot>(), Array.Empty<DrawingPropertyLinkSnapshot>()) }, Array.Empty<string>());
        return LearnedDrawingStyle.Learn(new[] { Reference(2), Reference(2), Reference(1.5) }, Path.GetTempPath());
    }
}
