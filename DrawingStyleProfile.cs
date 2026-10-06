using System.Globalization;
using static DrawingSnapshotSemantics;

public sealed record DrawingStyleProfile(
    int ReferenceSampleCount,
    IReadOnlyDictionary<string, int> SheetFormatFrequencies,
    IReadOnlyDictionary<string, int> SheetSizeFrequencies,
    IReadOnlyDictionary<string, int> ScaleFrequencies,
    IReadOnlyDictionary<string, int> ViewTypeOrientationFrequencies,
    IReadOnlyDictionary<string, int> DimensionTypeFrequencies,
    IReadOnlyDictionary<string, int> DimensionLaneFrequencies,
    IReadOnlyDictionary<string, int> DimensionOrderingFrequencies,
    IReadOnlyDictionary<string, int> HoleCalloutStyleFrequencies,
    IReadOnlyDictionary<string, int> NativeAnnotationFrequencies,
    double CenterMarkPolicyRate,
    double CenterlinePolicyRate,
    double HoleCalloutPolicyRate,
    double AverageNormalizedDimensionX,
    double AverageNormalizedDimensionY,
    double AverageNearestDimensionSpacingMm)
{
    public static DrawingStyleProfile Observe(IEnumerable<DrawingSnapshot> referenceDrawings)
    {
        ArgumentNullException.ThrowIfNull(referenceDrawings);

        var drawings = referenceDrawings.ToArray();
        var sheets = drawings.SelectMany(drawing => drawing.Sheets).ToArray();
        var modelViews = sheets.SelectMany(GetModelViews).ToArray();
        var dimensionEntries = sheets.SelectMany(sheet => GetModelViews(sheet)
            .SelectMany(view => view.Dimensions.Select(dimension => (Sheet: sheet, View: view, Dimension: dimension))))
            .ToArray();
        var dimensions = dimensionEntries.Select(entry => entry.Dimension).ToArray();
        var nativeAnnotations = sheets.SelectMany(sheet => sheet.Annotations)
            .Where(annotation => annotation.IsNativeEngineeringAnnotation)
            .ToArray();
        var nearestSpacings = modelViews.SelectMany(view =>
        {
            var positions = view.Dimensions.Where(dimension => dimension.TextPosition is not null)
                .Select(dimension => dimension.TextPosition!)
                .ToArray();
            return positions.Length < 2
                ? Array.Empty<double>()
                : positions.Select((position, index) => positions
                    .Where((_, otherIndex) => otherIndex != index)
                    .Select(other => Distance(position, other))
                    .Min());
        }).ToArray();
        var sampleCount = drawings.Length;

        var normalizedPositions = dimensionEntries
            .Where(entry => entry.Dimension.TextPosition is not null)
            .Select(entry => (
                X: entry.Dimension.TextPosition!.Xmm / Math.Max(entry.Sheet.WidthMm, 1.0),
                Y: entry.Dimension.TextPosition!.Ymm / Math.Max(entry.Sheet.HeightMm, 1.0)))
            .ToArray();

        return new DrawingStyleProfile(
            sampleCount,
            CountBy(sheets, sheet => Path.GetFileName(sheet.SheetFormatPath)),
            CountBy(sheets, sheet => FormattableString.Invariant($"{sheet.WidthMm:0.#}x{sheet.HeightMm:0.#} mm")),
            CountBy(sheets, sheet =>
            {
                var scale = SheetScale(sheet);
                return $"1:{(scale <= 0 ? 0 : 1.0 / scale).ToString("0.###", CultureInfo.InvariantCulture)}";
            }),
            CountBy(modelViews, view => $"{view.TypeName}:{NormalizeOrientation(view.Orientation)}"),
            CountBy(dimensions, dimension => dimension.TypeName),
            CountBy(dimensionEntries, entry => GetDimensionLane(entry.View, entry.Dimension)),
            CountBy(modelViews.Where(view => view.Dimensions.Count > 0), view => string.Join(" > ",
                view.Dimensions
                    .OrderByDescending(dimension => dimension.TextPosition?.Ymm ?? 0)
                    .ThenBy(dimension => dimension.TextPosition?.Xmm ?? 0)
                    .Select(dimension => dimension.TypeName))),
            CountBy(dimensions.Where(dimension => dimension.IsHoleCallout), dimension =>
                System.Text.RegularExpressions.Regex.Replace(dimension.DisplayText, @"\d+(?:[.,]\d+)?", "#")),
            CountBy(nativeAnnotations, annotation => annotation.NativeKind),
            Rate(drawings, drawing => drawing.Sheets.SelectMany(sheet => sheet.Annotations).Any(annotation => annotation.NativeKind == "CenterMark")),
            Rate(drawings, drawing => drawing.Sheets.SelectMany(sheet => sheet.Annotations).Any(annotation => annotation.NativeKind == "Centerline")),
            Rate(drawings, drawing => drawing.Sheets.SelectMany(sheet => sheet.Views).SelectMany(view => view.Dimensions).Any(dimension => dimension.IsHoleCallout)),
            normalizedPositions.Length == 0 ? 0 : normalizedPositions.Average(position => position.X),
            normalizedPositions.Length == 0 ? 0 : normalizedPositions.Average(position => position.Y),
            nearestSpacings.Length == 0 ? 0 : nearestSpacings.Average());
    }

    private static IReadOnlyDictionary<string, int> CountBy<T>(IEnumerable<T> values, Func<T, string> keySelector) =>
        values.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static double Rate(IEnumerable<DrawingSnapshot> drawings, Func<DrawingSnapshot, bool> predicate)
    {
        var samples = drawings.ToArray();
        return samples.Length == 0 ? 0 : samples.Count(predicate) / (double)samples.Length;
    }

    private static string GetDimensionLane(DrawingViewSnapshot view, DrawingDimensionSnapshot dimension)
    {
        if (view.Outline is null || dimension.TextPosition is null)
        {
            return "Unknown";
        }

        if (dimension.TextPosition.Ymm > view.Outline.TopMm)
        {
            return "AboveView";
        }

        if (dimension.TextPosition.Ymm < view.Outline.BottomMm)
        {
            return "BelowView";
        }

        if (dimension.TextPosition.Xmm < view.Outline.LeftMm)
        {
            return "LeftOfView";
        }

        return dimension.TextPosition.Xmm > view.Outline.RightMm ? "RightOfView" : "InsideView";
    }

    private static double Distance(DrawingPoint first, DrawingPoint second)
    {
        var x = first.Xmm - second.Xmm;
        var y = first.Ymm - second.Ymm;
        return Math.Sqrt(x * x + y * y);
    }
}
