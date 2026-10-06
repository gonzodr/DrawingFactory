using System.Runtime.InteropServices;
using SolidWorks.Interop.swconst;

public sealed class DrawingFactoryApplication
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly ConsoleReporter _reporter;
    private readonly Func<SolidWorksService> _createService;

    public DrawingFactoryApplication(TextWriter output, TextWriter error, Func<SolidWorksService>? createService = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _reporter = new ConsoleReporter(output, error);
        _createService = createService ?? (() => new SolidWorksService());
    }

    public ExitCode Run(IReadOnlyList<string> args)
    {
        DrawingFactoryCommand command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (ArgumentException exception)
        {
            _error.WriteLine(exception.Message);
            _error.WriteLine(CommandLine.Usage);
            return ExitCode.InvalidArguments;
        }

        try
        {
            return command switch
            {
                ShowHelpCommand => ShowHelp(),
                GenerateDrawingCommand generate => Generate(generate),
                TrainStyleCommand train => Train(train),
                PreviewSamplesCommand preview => Preview(preview),
                RunRegressionCommand regression => RunRegression(regression),
                CompareDrawingsCommand compare => Compare(compare),
                _ => throw new InvalidOperationException("Unsupported command.")
            };
        }
        catch (COMException exception)
        {
            _error.WriteLine($"SOLIDWORKS API error (0x{exception.HResult:X8}): {exception.Message}");
            return ExitCode.Failure;
        }
        catch (Exception exception)
        {
            _error.WriteLine($"DrawingFactory error: {exception}");
            return ExitCode.Failure;
        }
    }

    private ExitCode ShowHelp()
    {
        _output.WriteLine(CommandLine.Usage);
        return ExitCode.Success;
    }

    private ExitCode Generate(GenerateDrawingCommand command)
    {
        var partPath = InputFiles.RequireFile(command.PartPath, ".SLDPRT", "SOLIDWORKS part file");
        var templatePath = InputFiles.RequireFile(command.TemplatePath, ".DRWDOT", "drawing template");
        var outputPath = DrawingBuilder.GetOutputPath(partPath);
        if (File.Exists(outputPath))
        {
            throw new IOException($"Refusing to overwrite the existing drawing: {outputPath}");
        }

        var learnedStyle = command.UseLearnedStyle ? LoadLearnedStyle(command.StyleProfilePath) : null;

        var catalog = new SheetFormatCatalog();
        _reporter.PrintCatalog(catalog);
        var service = _createService();
        using var documents = new SolidWorksDocumentScope(service.Application, _error);
        var document = documents.Open(partPath, swDocumentTypes_e.swDocPART, readOnly: false, out var warnings);
        var analysis = new PartAnalyzer(document).Analyze();
        var features = new ManufacturingFeatureAnalyzer(document).Analyze(analysis.Geometry);
        _reporter.PrintPart(service.Revision, analysis, features, warnings);

        var plan = new DrawingStrategySelector().Select(analysis);
        _reporter.PrintStrategy(plan);
        if (!string.Equals(plan.StrategyName, "FlatPart", StringComparison.Ordinal))
        {
            _error.WriteLine("No drawing was generated: the current generator supports FlatPart plans only.");
            return ExitCode.UnsupportedStrategy;
        }

        var dimensionPlan = new DimensionStrategy().Create(features, plan);
        var layoutPlan = learnedStyle is null
            ? new DimensionLayoutPlanner().Plan(plan, dimensionPlan, features)
            : new LearnedStyleLayoutPlanner().Plan(plan, dimensionPlan, features, learnedStyle);
        plan = layoutPlan.FinalDrawingPlan;
        _reporter.PrintDimensionPlan(plan, dimensionPlan, layoutPlan);
        var sheetFormat = catalog.Resolve(plan.SheetRecommendation,
            learnedStyle?.PreferredSheet == plan.SheetRecommendation ? learnedStyle.PreferredSheetFormat : null);
        _output.WriteLine($"Selected sheet format: {sheetFormat.FileName}");
        var result = new DrawingBuilder(service.Application, _error).Build(
            partPath, templatePath, plan, sheetFormat, dimensionPlan, layoutPlan, features, analysis);
        _reporter.PrintBuildResult(result);
        return ExitCode.Success;
    }

    private LearnedDrawingStyle? LoadLearnedStyle(string? explicitPath = null)
    {
        var path = explicitPath ?? new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), DrawingStyleStore.DefaultFileName),
            Path.Combine(AppContext.BaseDirectory, DrawingStyleStore.DefaultFileName)
        }.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }
        var style = DrawingStyleStore.Load(path);
        _output.WriteLine($"Loaded learned style: {Path.GetFullPath(path)} ({style.Observations.ReferenceSampleCount} references).");
        return style;
    }

    private ExitCode Preview(PreviewSamplesCommand command)
    {
        var root = InputFiles.RequireTrainingDirectory(command.TrainingRoot);
        var samples = DrawingStyleTrainer.GetSamples(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OutputRoot);
        var outputRoot = Path.GetFullPath(command.OutputRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(outputRoot), normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            outputRoot.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Preview output must be outside the training-sample directory.");
        }
        var explicitTemplate = command.TemplatePath is null ? null : InputFiles.RequireFile(command.TemplatePath, ".DRWDOT", "preview template");
        var style = LoadLearnedStyle();
        var catalog = new SheetFormatCatalog();
        var service = _createService();
        var template = explicitTemplate ?? InputFiles.RequireFile(
            service.Application.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing), ".DRWDOT", "default drawing template");
        var runId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var runFolder = Path.Combine(outputRoot, "TrainingSamples_" + runId);
        Directory.CreateDirectory(runFolder);
        _output.WriteLine($"Preview output folder: {runFolder}");
        _output.WriteLine("PREVIEW ONLY: Front/Top/Right views; manufacturing dimensions are not automatically added.");
        var results = new List<SamplePreviewResult>();
        foreach (var sample in samples)
        {
            var name = Path.GetFileName(Path.GetDirectoryName(sample.Part))!;
            var folder = Path.Combine(runFolder, name);
            var partCopy = Path.Combine(folder, $"{name}_PREVIEW_{runId}.SLDPRT");
            try
            {
                Directory.CreateDirectory(folder);
                File.Copy(sample.Part, partCopy, overwrite: false);
                using var documents = new SolidWorksDocumentScope(service.Application, _error);
                var model = documents.Open(partCopy, swDocumentTypes_e.swDocPART, readOnly: true, out _);
                var analysis = new PartAnalyzer(model).Analyze();
                var layout = new PreviewLayoutPlanner().Plan(analysis.Geometry, style);
                var format = catalog.Resolve(layout.Sheet, style?.PreferredSheet == layout.Sheet ? style.PreferredSheetFormat : null);
                var result = new PreviewDrawingBuilder(service.Application, _error).Build(partCopy, template,
                    Path.Combine(folder, name + "_PREVIEW_" + runId[^8..] + ".SLDDRW"), analysis, layout, format);
                results.Add(new SamplePreviewResult(name, sample.Part, partCopy, result, null));
                _output.WriteLine(FormattableString.Invariant($"Preview saved: {result.OutputPath}; sheet={result.Sheet}; scale={result.Scale:0.###}; views={result.ModelViewCount}; saveWarnings={result.SaveWarnings}; openWarnings={result.OpenWarnings}."));
            }
            catch (Exception exception)
            {
                results.Add(new SamplePreviewResult(name, sample.Part, partCopy, null, exception.Message));
                _error.WriteLine($"Preview failed for {name}: {exception.Message}");
            }
        }
        File.WriteAllText(Path.Combine(runFolder, "preview-results.json"), System.Text.Json.JsonSerializer.Serialize(results,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var succeeded = results.Count(result => result.Drawing is not null);
        _output.WriteLine($"Preview drawings completed: {succeeded}/{samples.Count}.");
        return succeeded == samples.Count ? ExitCode.Success : ExitCode.Failure;
    }

    private sealed record SamplePreviewResult(string SampleName, string SourcePartPath, string PartCopyPath, PreviewDrawingResult? Drawing, string? Error);

    private ExitCode Train(TrainStyleCommand command)
    {
        var root = InputFiles.RequireTrainingDirectory(command.TrainingRoot);
        var profilePath = DrawingStyleStore.GetOutputPath(command.ProfilePath);
        DrawingStyleTrainer.GetSamples(root);
        var style = new DrawingStyleTrainer(_createService().Application, _error).Train(root);
        DrawingStyleStore.Save(profilePath, style);
        _reporter.PrintStyleProfile(style.Observations);
        _output.WriteLine($"Learned style saved: {profilePath}");
        _output.WriteLine(FormattableString.Invariant($"Preferred sheet: {style.PreferredSheet}; format: {style.PreferredSheetFormat}; scale: {style.PreferredScales[0]:0.###}."));
        return ExitCode.Success;
    }

    private ExitCode RunRegression(RunRegressionCommand command)
    {
        var trainingRoot = InputFiles.RequireTrainingDirectory(command.TrainingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GeneratedRoot);
        var generatedRoot = Path.GetFullPath(command.GeneratedRoot);
        var result = new RegressionRunner(_createService().Application, _error).Run(trainingRoot, generatedRoot);
        _reporter.PrintRegression(result);
        return result.IsSuccessful ? ExitCode.Success : ExitCode.Failure;
    }

    private ExitCode Compare(CompareDrawingsCommand command)
    {
        var partPath = InputFiles.RequireFile(command.PartPath, ".SLDPRT", "SOLIDWORKS part file");
        var referencePath = InputFiles.RequireFile(command.ReferencePath, ".SLDDRW", "reference drawing");
        var generatedPath = InputFiles.RequireFile(command.GeneratedPath, ".SLDDRW", "generated drawing");
        var sampleName = Path.GetFileName(Path.GetDirectoryName(partPath)) ?? Path.GetFileNameWithoutExtension(partPath);
        var result = new RegressionRunner(_createService().Application, _error)
            .ComparePair(sampleName, partPath, referencePath, generatedPath);
        _reporter.PrintComparison(result);
        return result.HasDifferences ? ExitCode.Failure : ExitCode.Success;
    }
}
