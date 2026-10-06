public sealed class StyleLearningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DrawingFactoryStyleTests_{Guid.NewGuid():N}");

    public StyleLearningTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LearnsMostFrequentSheetAndExactScales()
    {
        var style = Learn();
        Assert.Equal(DrawingSheetRecommendation.A4Landscape, style.PreferredSheet);
        Assert.Equal("A4 Fekvő JABIL.slddrt", style.PreferredSheetFormat);
        Assert.Equal(new[] { 2.0, 1.5 }, style.PreferredScales);
        Assert.Equal(3, style.Observations.ReferenceSampleCount);
    }

    [Fact]
    public void SavedProfileIsReusableWithoutSolidWorks()
    {
        var path = Path.Combine(_root, "style.json");
        DrawingStyleStore.Save(path, Learn());
        var restored = DrawingStyleStore.Load(path);
        Assert.Equal(DrawingSheetRecommendation.A4Landscape, restored.PreferredSheet);
        Assert.Equal(new[] { 2.0, 1.5 }, restored.PreferredScales);
        Assert.Equal(3, restored.ReferencePaths.Count);
        Assert.Contains("A4Landscape", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void RetrainingReplacesProfileAtomically()
    {
        var path = Path.Combine(_root, "style.json");
        DrawingStyleStore.Save(path, Learn());
        var updated = Learn() with { PreferredScales = new[] { 0.5 } };
        DrawingStyleStore.Save(path, updated);
        Assert.Equal(0.5, DrawingStyleStore.Load(path).PreferredScales[0]);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidLearnedScaleCannotReplaceExistingProfile(double scale)
    {
        var path = Path.Combine(_root, "style.json");
        DrawingStyleStore.Save(path, Learn());
        var original = File.ReadAllText(path);
        Assert.Throws<InvalidDataException>(() => DrawingStyleStore.Save(path, Learn() with { PreferredScales = new[] { scale } }));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void UnknownSchemaIsRejected() =>
        Assert.Throws<InvalidDataException>(() => (Learn() with { SchemaVersion = 99 }).Validate());

    [Fact]
    public void EmptyOrGeneratedDataCannotTrain()
    {
        Assert.Throws<ArgumentException>(() => LearnedDrawingStyle.Learn(Array.Empty<DrawingSnapshot>(), _root));
        Assert.Throws<ArgumentException>(() => LearnedDrawingStyle.Learn(new[] { Snapshot(2) with { Role = DrawingSnapshotRole.Generated } }, _root));
    }

    [Fact]
    public void UnsupportedPaperCannotTrain()
    {
        var reference = Snapshot(2);
        reference = reference with { Sheets = new[] { reference.Sheets[0] with { WidthMm = 123, HeightMm = 456 } } };
        Assert.Throws<InvalidOperationException>(() => LearnedDrawingStyle.Learn(new[] { reference }, _root));
    }

    [Fact]
    public void LearnedSheetAndScaleActuallyChangeGenerationPlan()
    {
        var features = Features(20, 5, 15);
        var plan = Plan(features);
        var dimensions = new DimensionStrategy().Create(features, plan);
        var layout = new LearnedStyleLayoutPlanner().Plan(plan, dimensions, features, Learn());
        Assert.Equal(DrawingSheetRecommendation.A4Landscape, layout.FinalDrawingPlan.SheetRecommendation);
        Assert.Equal(2.0, layout.FinalDrawingPlan.TargetScale);
        Assert.Equal(0.2, plan.TargetScale);
        Assert.Equal(DrawingSheetRecommendation.A3Landscape, plan.SheetRecommendation);
    }

    [Fact]
    public void TriesOtherLearnedScalesBeforeEnlargingPreferredPaper()
    {
        var features = Features(98, 2, 40);
        var plan = Plan(features);
        var layout = new LearnedStyleLayoutPlanner().Plan(plan, new DimensionStrategy().Create(features, plan), features, Learn());
        Assert.Equal(DrawingSheetRecommendation.A4Landscape, layout.FinalDrawingPlan.SheetRecommendation);
        Assert.Equal(1.5, layout.FinalDrawingPlan.TargetScale);
    }

    [Fact]
    public void OversizedModelFallsBackToSafeScaleAndLargerSheet()
    {
        var features = Features(1000, 5, 800);
        var plan = Plan(features);
        var layout = new LearnedStyleLayoutPlanner().Plan(plan, new DimensionStrategy().Create(features, plan), features, Learn());
        Assert.Equal(0.2, layout.FinalDrawingPlan.TargetScale);
        Assert.NotEqual(DrawingSheetRecommendation.A4Landscape, layout.FinalDrawingPlan.SheetRecommendation);
    }

    [Fact]
    public void UsesExactLearnedSheetFormatInsteadOfCatalogAlphabeticalDefault()
    {
        File.WriteAllText(Path.Combine(_root, "A4 Fekvő AAA.slddrt"), "");
        File.WriteAllText(Path.Combine(_root, "A4 Fekvő JABIL.slddrt"), "");
        var catalog = new SheetFormatCatalog(_root);
        Assert.Equal("A4 Fekvő JABIL.slddrt", catalog.Resolve(DrawingSheetRecommendation.A4Landscape, Learn().PreferredSheetFormat).FileName);
        Assert.Throws<FileNotFoundException>(() => catalog.Resolve(DrawingSheetRecommendation.A4Landscape, "A4 Fekvő missing.slddrt"));
    }

    [Fact]
    public void ParsesTrainingAndGenerationStyleOptions()
    {
        var train = Assert.IsType<TrainStyleCommand>(CommandLine.Parse(new[] { "--TRAIN" }, _root));
        Assert.Equal(Path.Combine(_root, "TrainingSamples"), train.TrainingRoot);
        Assert.Equal(Path.Combine(_root, "DrawingStyle.json"), train.ProfilePath);
        var custom = Assert.IsType<TrainStyleCommand>(CommandLine.Parse(new[] { "--train", "samples", "custom.json" }));
        Assert.Equal("custom.json", custom.ProfilePath);
        var generate = Assert.IsType<GenerateDrawingCommand>(CommandLine.Parse(new[] { "part.SLDPRT", "template.DRWDOT", "--style", "custom.json" }));
        Assert.Equal("custom.json", generate.StyleProfilePath);
        Assert.False(Assert.IsType<GenerateDrawingCommand>(CommandLine.Parse(new[] { "part.SLDPRT", "template.DRWDOT", "--no-style" })).UseLearnedStyle);
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--train", "samples", "style.json", "extra" }));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "part.SLDPRT", "template.DRWDOT", "--style" }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LearnedDrawingStyle Learn() => LearnedDrawingStyle.Learn(new[] { Snapshot(2), Snapshot(2), Snapshot(1.5) }, _root);

    private DrawingSnapshot Snapshot(double scale) => new(Path.Combine(_root, $"reference-{Guid.NewGuid():N}.SLDDRW"), "reference", DrawingSnapshotRole.Reference,
        new[] { new DrawingSheetSnapshot("Sheet1", "A4 Fekvő JABIL.slddrt", 297, 210, scale, 1,
            Array.Empty<DrawingViewSnapshot>(), Array.Empty<DrawingAnnotationSnapshot>(), Array.Empty<DrawingPropertyLinkSnapshot>()) }, Array.Empty<string>());

    private static ManufacturingFeatureSet Features(double x, double y, double z) => new(x, y, z, y, "Y",
        Array.Empty<PlanarBoundaryEdge>(), Array.Empty<ManufacturingHole>(), Array.Empty<HoleGroup>(), Array.Empty<double>(), Array.Empty<ThreadedFeatureAnalysis>());

    private static DrawingPlan Plan(ManufacturingFeatureSet features) => new("FlatPart", DrawingSheetRecommendation.A3Landscape, 0.2,
        new PrimaryViewPlan("*Top", new Vector3D(0, 1, 0), "Broad face"),
        new[] { new SecondaryViewPlan("Thickness", "*Front", "Thickness") }, Array.Empty<string>(),
        features.OverallXmm, features.OverallYmm, features.OverallZmm, "Y", features.OverallXmm * features.OverallZmm);
}
