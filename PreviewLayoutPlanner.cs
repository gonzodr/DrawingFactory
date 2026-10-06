public sealed record PreviewViewPlan(string Name, string ModelViewName, double CenterXmm, double CenterYmm, double WidthMm, double HeightMm);

public sealed record PreviewDrawingLayout(DrawingSheetRecommendation Sheet, double Scale, double WidthMm, double HeightMm, IReadOnlyList<PreviewViewPlan> Views);

public sealed class PreviewLayoutPlanner
{
    public PreviewDrawingLayout Plan(PartGeometryAnalysis geometry, LearnedDrawingStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        style?.Validate();
        if (new[] { geometry.BoundingBoxXmm, geometry.BoundingBoxYmm, geometry.BoundingBoxZmm }.Any(value => !double.IsFinite(value) || value <= 0))
        {
            throw new ArgumentException("Preview drawing requires finite, positive model extents.", nameof(geometry));
        }

        var preferredSheet = style?.PreferredSheet ?? DrawingSheetRecommendation.A4Landscape;
        var start = Array.FindIndex(DrawingSheetGeometry.Sizes, size => size.Sheet == preferredSheet);
        var scales = (style?.PreferredScales ?? Array.Empty<double>())
            .Concat(new[] { 1.0, 0.5, 0.25, 0.2, 0.1, 0.05, 0.02, 0.01 }).Distinct().ToArray();
        foreach (var sheet in DrawingSheetGeometry.Sizes.Skip(start))
        {
            foreach (var scale in scales)
            {
                const double outlinePadding = 8;
                const double gap = 12;
                var x = geometry.BoundingBoxXmm * scale;
                var y = geometry.BoundingBoxYmm * scale;
                var z = geometry.BoundingBoxZmm * scale;
                var usedWidth = x + z + 4 * outlinePadding + gap;
                var usedHeight = y + z + 4 * outlinePadding + gap;
                var left = 12 + (sheet.WidthMm - 24 - usedWidth) / 2;
                var bottom = 40 + (sheet.HeightMm - 52 - usedHeight) / 2;
                var frontX = left + z + 3 * outlinePadding + gap + x / 2;
                var frontY = bottom + z + 3 * outlinePadding + gap + y / 2;
                var views = new[]
                {
                    new PreviewViewPlan("Front", "*Front", frontX, frontY, x, y),
                    new PreviewViewPlan("Top", "*Top", frontX, bottom + outlinePadding + z / 2, x, z),
                    new PreviewViewPlan("Right", "*Right", left + outlinePadding + z / 2, frontY, z, y)
                };
                var bounds = views.Select(view => new DrawingBounds(view.CenterXmm - view.WidthMm / 2 - outlinePadding,
                    view.CenterYmm - view.HeightMm / 2 - outlinePadding, view.CenterXmm + view.WidthMm / 2 + outlinePadding,
                    view.CenterYmm + view.HeightMm / 2 + outlinePadding)).ToArray();
                if (Fits(bounds, sheet.WidthMm, sheet.HeightMm))
                {
                    return new PreviewDrawingLayout(sheet.Sheet, scale, sheet.WidthMm, sheet.HeightMm, views);
                }
            }
        }
        throw new InvalidOperationException("The three preview views do not fit any supported sheet.");
    }

    internal static bool Fits(IReadOnlyList<DrawingBounds> bounds, double widthMm, double heightMm)
    {
        if (bounds.Count != 3 || bounds.Any(box => !double.IsFinite(box.LeftMm) || !double.IsFinite(box.BottomMm) ||
            !double.IsFinite(box.RightMm) || !double.IsFinite(box.TopMm) || box.RightMm <= box.LeftMm || box.TopMm <= box.BottomMm ||
            box.LeftMm < 12 || box.BottomMm < 40 || box.RightMm > widthMm - 12 || box.TopMm > heightMm - 12))
        {
            return false;
        }
        for (var first = 0; first < bounds.Count; first++)
        {
            for (var second = first + 1; second < bounds.Count; second++)
            {
                var a = bounds[first];
                var b = bounds[second];
                if (a.LeftMm < b.RightMm + 6 && a.RightMm + 6 > b.LeftMm && a.BottomMm < b.TopMm + 6 && a.TopMm + 6 > b.BottomMm)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
