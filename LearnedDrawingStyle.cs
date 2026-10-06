using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record LearnedDrawingStyle(
    int SchemaVersion,
    DateTimeOffset TrainedAtUtc,
    string TrainingSamplesRoot,
    IReadOnlyList<string> ReferencePaths,
    DrawingSheetRecommendation PreferredSheet,
    string PreferredSheetFormat,
    IReadOnlyList<double> PreferredScales,
    DrawingStyleProfile Observations)
{
    public static LearnedDrawingStyle Learn(IEnumerable<DrawingSnapshot> references, string trainingRoot)
    {
        ArgumentNullException.ThrowIfNull(references);
        var drawings = references.ToArray();
        if (drawings.Length == 0 || drawings.Any(drawing => drawing.Role != DrawingSnapshotRole.Reference))
        {
            throw new ArgumentException("Training requires at least one reference drawing, not generated drawings.", nameof(references));
        }

        var sheets = drawings.SelectMany(drawing => drawing.Sheets).ToArray();
        var supported = sheets.Select(sheet => (Snapshot: sheet, Size: DrawingSheetGeometry.Sizes.FirstOrDefault(size =>
            Math.Abs(size.WidthMm - sheet.WidthMm) <= 1 && Math.Abs(size.HeightMm - sheet.HeightMm) <= 1)))
            .Where(item => item.Size.WidthMm > 0 && double.IsFinite(DrawingSnapshotSemantics.SheetScale(item.Snapshot)) &&
                DrawingSnapshotSemantics.SheetScale(item.Snapshot) > 0)
            .ToArray();
        if (supported.Length == 0)
        {
            throw new InvalidOperationException("The references contain no supported landscape sheet with a valid scale.");
        }

        var preferred = supported.GroupBy(item => item.Size.Sheet)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First();
        var format = preferred.Select(item => Path.GetFileName(item.Snapshot.SheetFormatPath))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key).FirstOrDefault()
            ?? throw new InvalidOperationException("The preferred reference sheet has no sheet-format filename.");
        var scales = preferred.GroupBy(item => DrawingSnapshotSemantics.SheetScale(item.Snapshot))
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key)
            .Select(group => group.Key).ToArray();
        var style = new LearnedDrawingStyle(1, DateTimeOffset.UtcNow, Path.GetFullPath(trainingRoot),
            drawings.Select(drawing => drawing.FilePath).ToArray(), preferred.Key, format, scales,
            DrawingStyleProfile.Observe(drawings));
        style.Validate();
        return style;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Observations is null || Observations.ReferenceSampleCount <= 0 ||
            ReferencePaths is null || ReferencePaths.Count != Observations.ReferenceSampleCount ||
            ReferencePaths.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(TrainingSamplesRoot) ||
            !Enum.IsDefined(PreferredSheet) || PreferredScales is null || PreferredScales.Count == 0 ||
            PreferredScales.Any(scale => !double.IsFinite(scale) || scale <= 0) ||
            string.IsNullOrWhiteSpace(PreferredSheetFormat) || PreferredSheetFormat != Path.GetFileName(PreferredSheetFormat) ||
            !string.Equals(Path.GetExtension(PreferredSheetFormat), ".SLDDRT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The learned drawing style is invalid or uses an unsupported schema.");
        }
    }
}

public static class DrawingStyleStore
{
    public const string DefaultFileName = "DrawingStyle.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LearnedDrawingStyle Load(string path)
    {
        var fullPath = InputFiles.RequireFile(path, ".json", "learned style profile");
        var style = JsonSerializer.Deserialize<LearnedDrawingStyle>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException("The learned style profile is empty.");
        style.Validate();
        return style;
    }

    public static string GetOutputPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The style profile must have a .json extension.", nameof(path));
        }
        return fullPath;
    }

    public static void Save(string path, LearnedDrawingStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        var fullPath = GetOutputPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(style, JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

internal static class DrawingSheetGeometry
{
    internal static readonly (DrawingSheetRecommendation Sheet, double WidthMm, double HeightMm)[] Sizes =
    {
        (DrawingSheetRecommendation.A4Landscape, 297, 210),
        (DrawingSheetRecommendation.A3Landscape, 420, 297),
        (DrawingSheetRecommendation.A2Landscape, 594, 420),
        (DrawingSheetRecommendation.A1Landscape, 841, 594),
        (DrawingSheetRecommendation.A0Landscape, 1189, 841)
    };
}
