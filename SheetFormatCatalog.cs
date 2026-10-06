public sealed class SheetFormatCatalog
{
    public SheetFormatCatalog(string? directoryPath = null)
    {
        DirectoryPath = ResolveDirectory(directoryPath);
        Formats = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".SLDDRT", StringComparison.OrdinalIgnoreCase))
            .Select(CreateEntry)
            .OrderBy(entry => entry.Recommendation)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<SheetFormatCatalogEntry> Formats { get; }

    public SheetFormatCatalogEntry Resolve(DrawingSheetRecommendation recommendation, string? preferredFileName = null)
    {
        var matches = Formats
            .Where(format => format.Recommendation == recommendation)
            .OrderBy(format => format.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (preferredFileName is not null)
        {
            return matches.FirstOrDefault(format => string.Equals(format.FileName, preferredFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"The learned sheet format '{preferredFileName}' for {recommendation} is missing from '{DirectoryPath}'.");
        }

        var approvedDefault = recommendation switch
        {
            DrawingSheetRecommendation.A3Landscape => "A3 Fekvő JABIL.slddrt",
            DrawingSheetRecommendation.A1Landscape => "A1 Fekvő JABIL.slddrt",
            DrawingSheetRecommendation.A0Landscape => "A0 Fekvő JABIL.slddrt",
            _ => null
        };
        if (approvedDefault is not null)
        {
            var defaultFormat = matches.FirstOrDefault(format =>
                string.Equals(format.FileName, approvedDefault, StringComparison.OrdinalIgnoreCase));
            if (defaultFormat is not null)
            {
                return defaultFormat;
            }
        }

        return matches.FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"No company sheet format was found for {recommendation} in '{DirectoryPath}'.");
    }

    private static string ResolveDirectory(string? directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            var explicitPath = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(explicitPath))
            {
                throw new DirectoryNotFoundException($"Sheet-format directory not found: {explicitPath}");
            }

            return explicitPath;
        }

        var applicationDirectory = Path.Combine(AppContext.BaseDirectory, "Sheetformats");
        if (Directory.Exists(applicationDirectory))
        {
            return applicationDirectory;
        }

        var workingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Sheetformats");
        if (Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        throw new DirectoryNotFoundException(
            "Could not find a Sheetformats directory beside the application or in the current directory.");
    }

    private static SheetFormatCatalogEntry CreateEntry(string path)
    {
        var fileName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var recommendation = ParseRecommendation(stem);
        return new SheetFormatCatalogEntry(
            fileName,
            Path.GetFullPath(path),
            recommendation,
            stem.Contains("_SA", StringComparison.OrdinalIgnoreCase));
    }

    private static DrawingSheetRecommendation? ParseRecommendation(string stem)
    {
        if (!stem.Contains("Fekvő", StringComparison.OrdinalIgnoreCase) &&
            !stem.Contains("Landscape", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (stem.StartsWith("A4", StringComparison.OrdinalIgnoreCase))
        {
            return DrawingSheetRecommendation.A4Landscape;
        }

        if (stem.StartsWith("A3", StringComparison.OrdinalIgnoreCase))
        {
            return DrawingSheetRecommendation.A3Landscape;
        }

        if (stem.StartsWith("A2", StringComparison.OrdinalIgnoreCase))
        {
            return DrawingSheetRecommendation.A2Landscape;
        }

        if (stem.StartsWith("A1", StringComparison.OrdinalIgnoreCase))
        {
            return DrawingSheetRecommendation.A1Landscape;
        }

        return stem.StartsWith("A0", StringComparison.OrdinalIgnoreCase)
            ? DrawingSheetRecommendation.A0Landscape
            : null;
    }
}

public sealed record SheetFormatCatalogEntry(
    string FileName,
    string FullPath,
    DrawingSheetRecommendation? Recommendation,
    bool IsSaVariant);