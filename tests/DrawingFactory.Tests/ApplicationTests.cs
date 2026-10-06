public sealed class ApplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DrawingFactoryTests_{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private int _serviceCreations;

    public ApplicationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void HelpDoesNotLaunchSolidWorks()
    {
        Assert.Equal(ExitCode.Success, CreateApplication().Run(new[] { "--help" }));
        Assert.Contains(CommandLine.Usage, _output.ToString());
        Assert.Equal(string.Empty, _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void InvalidArgumentsDoNotLaunchSolidWorks()
    {
        Assert.Equal(ExitCode.InvalidArguments, CreateApplication().Run(Array.Empty<string>()));
        Assert.Contains("Usage:", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void MissingTrainingDirectoryFailsBeforeLaunchingSolidWorks()
    {
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { "--regression", Path.Combine(_root, "missing") }));
        Assert.Contains("Training-sample directory not found", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void EmptyTrainingDirectoryFailsBeforeLaunchingSolidWorks()
    {
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { "--regression", _root }));
        Assert.Contains("no sample folders", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void MissingPartFailsBeforeLaunchingSolidWorks()
    {
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { Path.Combine(_root, "missing.SLDPRT"), "template.DRWDOT" }));
        Assert.Contains("part file was not found", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void InvalidTemplateFailsBeforeLaunchingSolidWorks()
    {
        var part = CreateFile("part.SLDPRT");
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { part, Path.Combine(_root, "wrong.SLDDRW") }));
        Assert.Contains(".DRWDOT extension", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void ExistingOutputIsNeverOverwritten()
    {
        var part = CreateFile("part.SLDPRT");
        var template = CreateFile("template.DRWDOT");
        var outputPath = DrawingBuilder.GetOutputPath(part);
        File.WriteAllText(outputPath, "original drawing");
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { part, template }));
        Assert.Contains("Refusing to overwrite", _error.ToString());
        Assert.Equal("original drawing", File.ReadAllText(outputPath));
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void MissingComparisonDrawingFailsBeforeLaunchingSolidWorks()
    {
        var part = CreateFile("part.SLDPRT");
        var reference = CreateFile("reference.SLDDRW");
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { "--compare", part, reference, Path.Combine(_root, "missing.SLDDRW") }));
        Assert.Contains("generated drawing was not found", _error.ToString());
        Assert.Equal(0, _serviceCreations);
    }

    [Fact]
    public void NoComparisonsCanNeverCountAsSuccessfulRegression()
    {
        var result = new RegressionRunResult(_root, _root, Array.Empty<DrawingComparisonResult>(),
            Array.Empty<string>(), DrawingStyleProfile.Observe(Array.Empty<DrawingSnapshot>()));
        Assert.False(result.IsSuccessful);
        new ConsoleReporter(_output, _error).PrintRegression(result);
        Assert.Contains("no complete comparisons", _error.ToString());
    }

    [Fact]
    public void IncompleteTrainingFailsBeforeLaunchingSolidWorksOrReplacingProfile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "incomplete"));
        var profile = CreateFile("style.json");
        File.WriteAllText(profile, "existing profile");
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { "--train", _root, profile }));
        Assert.Equal(0, _serviceCreations);
        Assert.Equal("existing profile", File.ReadAllText(profile));
    }

    [Fact]
    public void InvalidStyleFailsBeforeLaunchingSolidWorks()
    {
        var part = CreateFile("part.SLDPRT");
        var template = CreateFile("template.DRWDOT");
        var profile = CreateFile("invalid.json");
        File.WriteAllText(profile, "null");
        Assert.Equal(ExitCode.Failure, CreateApplication().Run(new[] { part, template, "--style", profile }));
        Assert.Equal(0, _serviceCreations);
        Assert.Contains("profile is empty", _error.ToString());
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private DrawingFactoryApplication CreateApplication() => new(_output, _error, () =>
    {
        _serviceCreations++;
        throw new InvalidOperationException("Tests must not launch SOLIDWORKS.");
    });

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
