public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("--HELP")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void HelpIsParsed(string option) =>
        Assert.IsType<ShowHelpCommand>(CommandLine.Parse(new[] { option }));

    [Fact]
    public void GenerationPreservesQuotedPathArguments()
    {
        var command = Assert.IsType<GenerateDrawingCommand>(CommandLine.Parse(new[] { "part with spaces.SLDPRT", "template.DRWDOT" }));
        Assert.Equal("part with spaces.SLDPRT", command.PartPath);
        Assert.Equal("template.DRWDOT", command.TemplatePath);
    }

    [Fact]
    public void RegressionUsesWorkingDirectoryDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "drawing-factory-cli");
        var command = Assert.IsType<RunRegressionCommand>(CommandLine.Parse(new[] { "--REGRESSION" }, root));
        Assert.Equal(Path.Combine(root, "TrainingSamples"), command.TrainingRoot);
        Assert.Equal(Path.Combine(root, "Generated"), command.GeneratedRoot);
    }

    [Fact]
    public void RegressionAcceptsIndependentOutputDirectory()
    {
        var command = Assert.IsType<RunRegressionCommand>(CommandLine.Parse(new[] { "--regression", "training", "output" }));
        Assert.Equal("training", command.TrainingRoot);
        Assert.Equal("output", command.GeneratedRoot);
    }

    [Fact]
    public void ComparisonParsesAllThreePaths()
    {
        var command = Assert.IsType<CompareDrawingsCommand>(CommandLine.Parse(new[] { "--COMPARE", "part.SLDPRT", "reference.SLDDRW", "generated.SLDDRW" }));
        Assert.Equal("part.SLDPRT", command.PartPath);
        Assert.Equal("reference.SLDDRW", command.ReferencePath);
        Assert.Equal("generated.SLDDRW", command.GeneratedPath);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArgumentsAreRejected(string[] args) =>
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args));

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "part.SLDPRT" },
        new[] { "part.SLDPRT", "template.DRWDOT", "extra" },
        new[] { "--regression", "training", "generated", "extra" },
        new[] { "--compare", "part.SLDPRT", "reference.SLDDRW" },
        new[] { "--unknown", "template.DRWDOT" },
        new[] { "--help", "extra" },
        new[] { "part.SLDPRT", " " }
    };
}
