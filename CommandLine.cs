public abstract record DrawingFactoryCommand;

public sealed record GenerateDrawingCommand(string PartPath, string TemplatePath, string? StyleProfilePath = null, bool UseLearnedStyle = true) : DrawingFactoryCommand;

public sealed record TrainStyleCommand(string TrainingRoot, string ProfilePath) : DrawingFactoryCommand;

public sealed record PreviewSamplesCommand(string TrainingRoot, string OutputRoot, string? TemplatePath = null) : DrawingFactoryCommand;

public sealed record RunRegressionCommand(string TrainingRoot, string GeneratedRoot) : DrawingFactoryCommand;

public sealed record CompareDrawingsCommand(string PartPath, string ReferencePath, string GeneratedPath) : DrawingFactoryCommand;

public sealed record ShowHelpCommand : DrawingFactoryCommand;

public enum ExitCode
{
    Success = 0,
    Failure = 1,
    InvalidArguments = 2,
    UnsupportedStrategy = 4
}

public static class CommandLine
{
    public const string Usage = "Usage: DrawingFactory <part-file.SLDPRT> <drawing-template.DRWDOT> [--style <profile.json> | --no-style]\n" +
        "       DrawingFactory --train [TrainingSamplesRoot] [profile.json]\n" +
        "       DrawingFactory --preview [TrainingSamplesRoot] [OutputRoot] [drawing.DRWDOT]\n" +
        "       DrawingFactory --regression [TrainingSamplesRoot] [GeneratedRoot]\n" +
        "       DrawingFactory --compare <part.SLDPRT> <reference.SLDDRW> <generated.SLDDRW>\n" +
        "       DrawingFactory --help";

    public static DrawingFactoryCommand Parse(IReadOnlyList<string> args, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Arguments must not be empty or whitespace.", nameof(args));
        }

        if (args.Count == 1 && (args[0] is "-h" or "/?" || IsOption(args[0], "--help")))
        {
            return new ShowHelpCommand();
        }

        if (args.Count > 0 && IsOption(args[0], "--preview"))
        {
            if (args.Count > 4)
            {
                throw new ArgumentException("Preview accepts a sample directory, output directory and optional drawing template.", nameof(args));
            }
            var root = workingDirectory ?? Directory.GetCurrentDirectory();
            return new PreviewSamplesCommand(
                args.Count > 1 ? args[1] : Path.Combine(root, "TrainingSamples"),
                args.Count > 2 ? args[2] : Path.Combine(root, "TestOutput"),
                args.Count > 3 ? args[3] : null);
        }

        if (args.Count > 0 && IsOption(args[0], "--train"))
        {
            if (args.Count > 3)
            {
                throw new ArgumentException("Training accepts a sample directory and an output profile path.", nameof(args));
            }
            var root = workingDirectory ?? Directory.GetCurrentDirectory();
            return new TrainStyleCommand(
                args.Count > 1 ? args[1] : Path.Combine(root, "TrainingSamples"),
                args.Count > 2 ? args[2] : Path.Combine(root, DrawingStyleStore.DefaultFileName));
        }

        if (args.Count > 0 && IsOption(args[0], "--regression"))
        {
            if (args.Count > 3)
            {
                throw new ArgumentException("Regression accepts at most two directory arguments.", nameof(args));
            }

            var root = workingDirectory ?? Directory.GetCurrentDirectory();
            return new RunRegressionCommand(
                args.Count > 1 ? args[1] : Path.Combine(root, "TrainingSamples"),
                args.Count > 2 ? args[2] : Path.Combine(root, "Generated"));
        }

        if (args.Count > 0 && IsOption(args[0], "--compare"))
        {
            if (args.Count != 4)
            {
                throw new ArgumentException("Comparison requires a part, reference drawing and generated drawing.", nameof(args));
            }

            return new CompareDrawingsCommand(args[1], args[2], args[3]);
        }

        if (args.Count >= 2 && !args[0].StartsWith("--", StringComparison.Ordinal) && args[0] is not ("-h" or "/?"))
        {
            if (args.Count == 2)
            {
                return new GenerateDrawingCommand(args[0], args[1]);
            }
            if (args.Count == 3 && IsOption(args[2], "--no-style"))
            {
                return new GenerateDrawingCommand(args[0], args[1], UseLearnedStyle: false);
            }
            if (args.Count == 4 && IsOption(args[2], "--style"))
            {
                return new GenerateDrawingCommand(args[0], args[1], args[3]);
            }
        }

        throw new ArgumentException("Expected a part and drawing template, or a supported command.", nameof(args));
    }

    private static bool IsOption(string value, string option) =>
        string.Equals(value, option, StringComparison.OrdinalIgnoreCase);
}
