using System.Globalization;
using System.Text;
using static DrawingSnapshotSemantics;

public sealed class DrawingEvaluator
{
    private const double LinearToleranceMm = 0.05;
    private const double AngularToleranceDegrees = 0.1;
    private const double PlacementToleranceFraction = 0.05;

    public DrawingComparisonResult Compare(
        string sampleName,
        DrawingSnapshot reference,
        DrawingSnapshot generated,
        ManufacturingFeatureSet? manufacturingFeatures = null,
        PartAnalysis? partAnalysis = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(generated);

        var missingViews = new List<string>();
        var unnecessaryViews = new List<string>();
        var wrongOrientations = new List<string>();
        var sheetDifferences = new List<string>();
        var scaleDifferences = new List<string>();
        var missingDimensions = new List<string>();
        var unnecessaryDimensions = new List<string>();
        var redundantDimensions = FindRedundantDimensions(generated);
        var dimensionPlacementDifferences = new List<string>();
        var missingNativeAnnotations = new List<string>();
        var unnecessaryNativeAnnotations = new List<string>();
        var titleBlockDifferences = new List<string>();
        var unresolvedManufacturingInformation = new List<string>();

        var sheetCount = Math.Max(reference.Sheets.Count, generated.Sheets.Count);
        for (var sheetIndex = 0; sheetIndex < sheetCount; sheetIndex++)
        {
            if (sheetIndex >= reference.Sheets.Count)
            {
                sheetDifferences.Add($"Sheet {sheetIndex + 1}: unnecessary sheet '{generated.Sheets[sheetIndex].Name}'.");
                unnecessaryViews.AddRange(GetModelViews(generated.Sheets[sheetIndex])
                    .Select(view => $"Sheet {sheetIndex + 1}: extra {ViewLabel(view)}"));
                continue;
            }

            if (sheetIndex >= generated.Sheets.Count)
            {
                sheetDifferences.Add($"Sheet {sheetIndex + 1}: missing sheet '{reference.Sheets[sheetIndex].Name}'.");
                missingViews.AddRange(GetModelViews(reference.Sheets[sheetIndex])
                    .Select(view => $"Sheet {sheetIndex + 1}: missing {ViewLabel(view)}"));
                continue;
            }

            var referenceSheet = reference.Sheets[sheetIndex];
            var generatedSheet = generated.Sheets[sheetIndex];
            CompareSheet(sheetIndex, referenceSheet, generatedSheet, sheetDifferences, scaleDifferences);
            CompareViews(
                sheetIndex,
                referenceSheet,
                generatedSheet,
                missingViews,
                unnecessaryViews,
                wrongOrientations,
                dimensionPlacementDifferences,
                scaleDifferences);
        }

        CompareDimensions(reference, generated, missingDimensions, unnecessaryDimensions, dimensionPlacementDifferences);
        CompareNativeAnnotations(reference, generated, missingNativeAnnotations, unnecessaryNativeAnnotations);
        ComparePropertyLinks(reference, generated, partAnalysis, titleBlockDifferences);
        FindUnresolvedManufacturingInformation(reference, generated, manufacturingFeatures, unresolvedManufacturingInformation);

        return new DrawingComparisonResult(
            sampleName,
            reference.FilePath,
            generated.FilePath,
            missingViews,
            unnecessaryViews,
            wrongOrientations,
            sheetDifferences,
            scaleDifferences,
            missingDimensions,
            unnecessaryDimensions,
            redundantDimensions,
            dimensionPlacementDifferences,
            missingNativeAnnotations,
            unnecessaryNativeAnnotations,
            titleBlockDifferences,
            unresolvedManufacturingInformation);
    }

    private static void CompareSheet(
        int index,
        DrawingSheetSnapshot reference,
        DrawingSheetSnapshot generated,
        ICollection<string> sheetDifferences,
        ICollection<string> scaleDifferences)
    {
        if (!string.Equals(
                Path.GetFileName(reference.SheetFormatPath),
                Path.GetFileName(generated.SheetFormatPath),
                StringComparison.OrdinalIgnoreCase))
        {
            sheetDifferences.Add(
                $"Sheet {index + 1}: format '{Path.GetFileName(reference.SheetFormatPath)}' vs '{Path.GetFileName(generated.SheetFormatPath)}'.");
        }

        if (Math.Abs(reference.WidthMm - generated.WidthMm) > 1.0 ||
            Math.Abs(reference.HeightMm - generated.HeightMm) > 1.0)
        {
            sheetDifferences.Add(
                $"Sheet {index + 1}: size {reference.WidthMm:0.##} x {reference.HeightMm:0.##} mm vs {generated.WidthMm:0.##} x {generated.HeightMm:0.##} mm.");
        }

        var referenceScale = SheetScale(reference);
        var generatedScale = SheetScale(generated);
        if (Math.Abs(referenceScale - generatedScale) > 0.000001)
        {
            scaleDifferences.Add(
                $"Sheet {index + 1}: scale 1:{SafeReciprocal(referenceScale):0.###} vs 1:{SafeReciprocal(generatedScale):0.###}.");
        }
    }

    private static void CompareViews(
        int sheetIndex,
        DrawingSheetSnapshot reference,
        DrawingSheetSnapshot generated,
        ICollection<string> missing,
        ICollection<string> unnecessary,
        ICollection<string> wrongOrientation,
        ICollection<string> placementDifferences,
        ICollection<string> scaleDifferences)
    {
        var unmatchedReference = new List<DrawingViewSnapshot>();
        var unmatchedGenerated = GetModelViews(generated).ToList();

        foreach (var expected in GetModelViews(reference))
        {
            var match = unmatchedGenerated
                .Where(actual => SameViewSignature(expected, actual))
                .OrderBy(actual => ViewPlacementDistance(expected, actual, reference, generated))
                .ThenBy(actual => Math.Abs(expected.Scale - actual.Scale))
                .FirstOrDefault();
            if (match is null)
            {
                unmatchedReference.Add(expected);
                continue;
            }

            unmatchedGenerated.Remove(match);
            CompareMatchedView(sheetIndex, expected, match, reference, generated, placementDifferences, scaleDifferences);
        }

        // Reserve all exact matches before using a view to diagnose a wrong orientation.
        foreach (var expected in unmatchedReference)
        {
            var wrong = unmatchedGenerated.FirstOrDefault(actual => actual.TypeCode == expected.TypeCode);
            if (wrong is not null)
            {
                wrongOrientation.Add(
                    $"Sheet {sheetIndex + 1}: {expected.TypeName} orientation '{expected.Orientation}' expected, got '{wrong.Orientation}'.");
                unmatchedGenerated.Remove(wrong);
                CompareMatchedView(sheetIndex, expected, wrong, reference, generated, placementDifferences, scaleDifferences);
            }
            else
            {
                missing.Add($"Sheet {sheetIndex + 1}: missing {ViewLabel(expected)}.");
            }
        }

        foreach (var extra in unmatchedGenerated)
        {
            unnecessary.Add($"Sheet {sheetIndex + 1}: unnecessary {ViewLabel(extra)}.");
        }
    }

    private static void CompareMatchedView(
        int sheetIndex,
        DrawingViewSnapshot reference,
        DrawingViewSnapshot generated,
        DrawingSheetSnapshot referenceSheet,
        DrawingSheetSnapshot generatedSheet,
        ICollection<string> placementDifferences,
        ICollection<string> scaleDifferences)
    {
        CompareViewPlacement(sheetIndex, reference, generated, referenceSheet, generatedSheet, placementDifferences);
        if (Math.Abs(reference.Scale - generated.Scale) > 0.000001)
        {
            scaleDifferences.Add(
                $"Sheet {sheetIndex + 1}, {ViewLabel(reference)}: scale 1:{SafeReciprocal(reference.Scale):0.###} vs 1:{SafeReciprocal(generated.Scale):0.###}.");
        }
    }

    private static double ViewPlacementDistance(
        DrawingViewSnapshot reference,
        DrawingViewSnapshot generated,
        DrawingSheetSnapshot referenceSheet,
        DrawingSheetSnapshot generatedSheet)
    {
        if (reference.Position is null || generated.Position is null)
        {
            return double.PositiveInfinity;
        }

        var x = reference.Position.Xmm / Math.Max(referenceSheet.WidthMm, 1.0) -
                generated.Position.Xmm / Math.Max(generatedSheet.WidthMm, 1.0);
        var y = reference.Position.Ymm / Math.Max(referenceSheet.HeightMm, 1.0) -
                generated.Position.Ymm / Math.Max(generatedSheet.HeightMm, 1.0);
        return Math.Sqrt(x * x + y * y);
    }

    private static void CompareViewPlacement(
        int sheetIndex,
        DrawingViewSnapshot reference,
        DrawingViewSnapshot generated,
        DrawingSheetSnapshot referenceSheet,
        DrawingSheetSnapshot generatedSheet,
        ICollection<string> differences)
    {
        if (reference.Position is null || generated.Position is null)
        {
            return;
        }

        var distance = ViewPlacementDistance(reference, generated, referenceSheet, generatedSheet);
        if (distance > PlacementToleranceFraction)
        {
            differences.Add(
                $"Sheet {sheetIndex + 1}, {ViewLabel(reference)}: normalized view position differs by {distance:P1} of sheet diagonal.");
        }
    }

    private static void CompareDimensions(
        DrawingSnapshot reference,
        DrawingSnapshot generated,
        ICollection<string> missing,
        ICollection<string> unnecessary,
        ICollection<string> placementDifferences)
    {
        var referenceDimensions = reference.Sheets.SelectMany(sheet => sheet.Views).SelectMany(view => view.Dimensions).ToArray();
        var generatedDimensions = generated.Sheets.SelectMany(sheet => sheet.Views).SelectMany(view => view.Dimensions).ToArray();
        var candidates = referenceDimensions.Select(expected => generatedDimensions
            .Select((actual, index) => (Dimension: actual, Index: index))
            .Where(item => string.Equals(item.Dimension.TypeName, expected.TypeName, StringComparison.Ordinal) &&
                           item.Dimension.IsHoleCallout == expected.IsHoleCallout &&
                           string.Equals(item.Dimension.Unit, expected.Unit, StringComparison.Ordinal) &&
                           Math.Abs(item.Dimension.Value - expected.Value) <= ValueTolerance(expected.Unit))
            .OrderBy(item => Math.Abs(item.Dimension.Value - expected.Value))
            .ThenByDescending(item => expected.ReferencePoints.Count > 0 &&
                                      SameReferencePoints(expected.ReferencePoints, item.Dimension.ReferencePoints))
            .ThenBy(item => expected.TextPosition is not null && item.Dimension.TextPosition is not null
                ? PointDistance(expected.TextPosition, item.Dimension.TextPosition)
                : double.PositiveInfinity)
            .Select(item => item.Index)
            .ToArray()).ToArray();
        var matches = MatchCandidates(candidates, generatedDimensions.Length);
        var matchedGenerated = new HashSet<int>(matches.Where(index => index >= 0));

        for (var index = 0; index < referenceDimensions.Length; index++)
        {
            var expected = referenceDimensions[index];
            if (matches[index] < 0)
            {
                missing.Add($"Missing native {expected.TypeName} {expected.Value:0.###} {expected.Unit} ({expected.DisplayText}).");
                continue;
            }

            var match = generatedDimensions[matches[index]];
            if (expected.ReferencePoints.Count > 0 && match.ReferencePoints.Count > 0 &&
                !SameReferencePoints(expected.ReferencePoints, match.ReferencePoints))
            {
                placementDifferences.Add(
                    $"{expected.TypeName} {expected.Value:0.###} {expected.Unit}: matched value but referenced geometry points differ.");
            }

            if (expected.TextPosition is not null && match.TextPosition is not null)
            {
                var distance = PointDistance(expected.TextPosition, match.TextPosition);
                if (distance > 10.0)
                {
                    placementDifferences.Add(
                        $"{expected.TypeName} {expected.Value:0.###} {expected.Unit}: text position differs by {distance:0.##} mm.");
                }
            }
        }

        for (var index = 0; index < generatedDimensions.Length; index++)
        {
            if (!matchedGenerated.Contains(index))
            {
                var extra = generatedDimensions[index];
                unnecessary.Add($"Unnecessary native {extra.TypeName} {extra.Value:0.###} {extra.Unit} ({extra.DisplayText}).");
            }
        }
    }

    private static void CompareNativeAnnotations(
        DrawingSnapshot reference,
        DrawingSnapshot generated,
        ICollection<string> missing,
        ICollection<string> unnecessary)
    {
        var nativeKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "HoleCallout", "CosmeticThread", "GeometricTolerance", "DatumTag", "DatumTarget",
            "DatumOrigin", "SurfaceFinish", "CenterMark", "Centerline"
        };
        var expectedAnnotations = reference.Sheets.SelectMany(sheet => sheet.Annotations)
            .Where(annotation => nativeKinds.Contains(annotation.NativeKind)).ToList();
        var actualAnnotations = generated.Sheets.SelectMany(sheet => sheet.Annotations)
            .Where(annotation => nativeKinds.Contains(annotation.NativeKind)).ToList();

        foreach (var kind in nativeKinds)
        {
            var expectedOfKind = expectedAnnotations.Where(annotation => annotation.NativeKind == kind).ToList();
            var actualOfKind = actualAnnotations.Where(annotation => annotation.NativeKind == kind).ToList();
            if (actualOfKind.Count < expectedOfKind.Count)
            {
                missing.Add($"{kind}: {expectedOfKind.Count - actualOfKind.Count} native annotation(s) missing.");
            }

            if (actualOfKind.Count > expectedOfKind.Count)
            {
                unnecessary.Add($"{kind}: {actualOfKind.Count - expectedOfKind.Count} extra native annotation(s).");
            }

            if (expectedOfKind.Count > 0 && actualOfKind.Count > 0 &&
                (kind is "GeometricTolerance" or "DatumTag" or "DatumTarget" or "SurfaceFinish" or "HoleCallout"))
            {
                var unmatchedText = actualOfKind.Select(annotation => NormalizeNativeText(annotation.Text)).ToList();
                var missingContent = false;
                foreach (var annotation in expectedOfKind)
                {
                    if (!unmatchedText.Remove(NormalizeNativeText(annotation.Text)))
                    {
                        missingContent = true;
                    }
                }

                if (missingContent)
                {
                    missing.Add($"{kind}: native annotation content differs from the reference.");
                }

                if (unmatchedText.Count > 0)
                {
                    unnecessary.Add($"{kind}: native annotation content differs from the reference.");
                }
            }
        }
    }

    private static void ComparePropertyLinks(
        DrawingSnapshot reference,
        DrawingSnapshot generated,
        PartAnalysis? partAnalysis,
        ICollection<string> differences)
    {
        var expected = BuildPropertyEvidence(reference, partAnalysis);
        var actual = BuildPropertyEvidence(generated, partAnalysis);

        foreach (var link in expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase))
        {
            differences.Add($"Missing title-block property link: {link}.");
        }

        foreach (var link in actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase))
        {
            differences.Add($"Unnecessary title-block property link: {link}.");
        }

        foreach (var link in expected.Keys.Intersect(actual.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var expectedValue = expected[link].ExpectedModelValue;
            var actualValue = actual[link].ExpectedModelValue;
            if (!string.IsNullOrEmpty(expectedValue) && !string.IsNullOrEmpty(actualValue))
            {
                var referenceResolves = expected[link].ResolvedTexts.Any(text => ContainsExpectedPropertyValue(text, expectedValue));
                var generatedResolves = actual[link].ResolvedTexts.Any(text => ContainsExpectedPropertyValue(text, actualValue));
                if (!referenceResolves || !generatedResolves)
                {
                    differences.Add(
                        $"Title-block property '{link}' expects '{expectedValue}'; reference note text='{string.Join(" | ", expected[link].ResolvedTexts)}', generated note text='{string.Join(" | ", actual[link].ResolvedTexts)}'.");
                }
            }
            else
            {
                var expectedValues = expected[link].ResolvedTexts
                    .Select(value => NormalizeSystemPropertyValue(link, value))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var actualValues = actual[link].ResolvedTexts
                    .Select(value => NormalizeSystemPropertyValue(link, value))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (expectedValues.SequenceEqual(actualValues, StringComparer.Ordinal))
                {
                    continue;
                }

                differences.Add(
                    $"System/title-block link '{link}' resolves differently: reference='{string.Join(" | ", expected[link].ResolvedTexts)}', generated='{string.Join(" | ", actual[link].ResolvedTexts)}'.");
            }
        }
    }

    private static Dictionary<string, PropertyLinkEvidence> BuildPropertyEvidence(
        DrawingSnapshot snapshot,
        PartAnalysis? partAnalysis)
    {
        var evidence = new Dictionary<string, PropertyLinkEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in snapshot.Sheets.SelectMany(sheet => sheet.PropertyLinks))
        {
            foreach (var token in link.PropertyTokens)
            {
                var canonicalName = CanonicalPropertyName(token.Name);
                if (canonicalName.Length == 0)
                {
                    continue;
                }

                var expectedModelValue = partAnalysis is null
                    ? null
                    : ResolveModelPropertyValue(partAnalysis, canonicalName);
                if (!evidence.TryGetValue(canonicalName, out var item))
                {
                    item = new PropertyLinkEvidence(expectedModelValue, new List<string>());
                    evidence.Add(canonicalName, item);
                }

                if (!item.ResolvedTexts.Contains(link.ResolvedText, StringComparer.Ordinal))
                {
                    item.ResolvedTexts.Add(link.ResolvedText);
                }
            }
        }

        return evidence;
    }

    private static string? ResolveModelPropertyValue(PartAnalysis analysis, string canonicalName)
    {
        var property = analysis.DocumentProperties
            .Concat(analysis.ConfigurationProperties.SelectMany(configuration => configuration.CustomProperties))
            .FirstOrDefault(candidate => CanonicalPropertyName(candidate.Name) == canonicalName);
        if (property is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(property.ResolvedValue) ? property.Value : property.ResolvedValue;
    }

    private static string CanonicalPropertyName(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var key = new string(decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();

        return key switch
        {
            "REVISION" or "REVIZIO" => "REVIZIO",
            "DESCRIPTION" or "MEGNEVEZES" => "DESCRIPTION",
            "PROJECT" or "PROJEKT" or "PROJEKTNEV" => "PROJECT",
            "RAJZSZAMSA" or "ALKATRESZSZAM" => "RAJZSZAMSA",
            "GYARTO" or "GYARTOSA" => "GYARTOSA",
            _ => key
        };
    }

    private static bool ContainsExpectedPropertyValue(string noteText, string expectedValue) =>
        NormalizeNumericSeparators(noteText).Contains(NormalizeNumericSeparators(expectedValue), StringComparison.Ordinal);

    private static string NormalizeNumericSeparators(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"(?<=\d),(?=\d)", ".");

    private static string NormalizeSystemPropertyValue(string propertyName, string resolvedText)
    {
        if (propertyName == "SWSHEETSCALE")
        {
            var scale = System.Text.RegularExpressions.Regex.Match(resolvedText, @"\d+\s*:\s*\d+");
            return scale.Success
                ? string.Concat(scale.Value.Where(character => !char.IsWhiteSpace(character)))
                : resolvedText;
        }

        return NormalizeNumericSeparators(resolvedText);
    }

    private sealed record PropertyLinkEvidence(
        string? ExpectedModelValue,
        List<string> ResolvedTexts);

    private static void FindUnresolvedManufacturingInformation(
        DrawingSnapshot reference,
        DrawingSnapshot generated,
        ManufacturingFeatureSet? features,
        ICollection<string> unresolved)
    {
        foreach (var information in reference.UnresolvedInformation)
        {
            unresolved.Add($"Reference drawing: {information}");
        }

        foreach (var information in generated.UnresolvedInformation)
        {
            unresolved.Add($"Generated drawing: {information}");
        }

        if (features is null)
        {
            unresolved.Add("Manufacturing feature analysis was not supplied to the evaluator.");
            return;
        }

        var generatedDimensions = generated.Sheets.SelectMany(sheet => sheet.Views).SelectMany(view => view.Dimensions).ToArray();
        if (features.HoleGroups.Count > 0 && !generatedDimensions.Any(dimension => dimension.IsHoleCallout))
        {
            unresolved.Add($"{features.Holes.Count} detected hole(s) have no native hole callout in the generated drawing.");
        }

        if (features.ThreadedFeatures.Count > 0 &&
            !generated.Sheets.SelectMany(sheet => sheet.Annotations).Any(annotation =>
                annotation.NativeKind == "CosmeticThread"))
        {
            unresolved.Add($"{features.ThreadedFeatures.Count} explicitly threaded feature(s) are not represented by a native thread/hole callout.");
        }

        var referenceNativeThreadCount = reference.Sheets.SelectMany(sheet => sheet.Annotations)
            .Count(annotation => annotation.NativeKind == "CosmeticThread");
        if (referenceNativeThreadCount > 0 && features.ThreadedFeatures.Count == 0)
        {
            unresolved.Add("The reference contains native thread annotations, but model feature analysis found no explicit threaded feature metadata.");
        }
    }

    private static IReadOnlyList<string> FindRedundantDimensions(DrawingSnapshot drawing)
    {
        var redundant = new List<string>();
        var dimensions = drawing.Sheets.SelectMany(sheet => sheet.Views).SelectMany(view => view.Dimensions).ToArray();
        for (var first = 0; first < dimensions.Length; first++)
        {
            for (var second = first + 1; second < dimensions.Length; second++)
            {
                var a = dimensions[first];
                var b = dimensions[second];
                if (a.TypeName == b.TypeName && a.Unit == b.Unit && a.IsHoleCallout == b.IsHoleCallout &&
                    Math.Abs(a.Value - b.Value) <= ValueTolerance(a.Unit) &&
                    a.ReferencePoints.Count > 0 && SameReferencePoints(a.ReferencePoints, b.ReferencePoints))
                {
                    redundant.Add($"Duplicate native {a.TypeName} {a.Value:0.###} {a.Unit} references the same geometry twice.");
                }
            }
        }

        return redundant;
    }

    private static bool SameViewSignature(DrawingViewSnapshot left, DrawingViewSnapshot right) =>
        left.TypeCode == right.TypeCode &&
        string.Equals(NormalizeOrientation(left.Orientation), NormalizeOrientation(right.Orientation), StringComparison.OrdinalIgnoreCase);

    private static string ViewLabel(DrawingViewSnapshot view) =>
        $"{view.TypeName} view '{view.Orientation}' ({view.ReferencedConfiguration})";

    private static string NormalizeNativeText(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool SameReferencePoints(IReadOnlyList<DrawingPoint> left, IReadOnlyList<DrawingPoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var candidates = left.Select(point => right
            .Select((candidate, index) => (Point: candidate, Index: index))
            .Where(item => PointDistance(point, item.Point) <= LinearToleranceMm)
            .OrderBy(item => PointDistance(point, item.Point))
            .Select(item => item.Index)
            .ToArray()).ToArray();
        return MatchCandidates(candidates, right.Count).All(index => index >= 0);
    }

    private static int[] MatchCandidates(int[][] candidates, int actualCount)
    {
        var matches = Enumerable.Repeat(-1, candidates.Length).ToArray();
        var owners = Enumerable.Repeat(-1, actualCount).ToArray();

        bool TryMatch(int expectedIndex, bool[] visited)
        {
            foreach (var actualIndex in candidates[expectedIndex])
            {
                if (!visited[actualIndex] && owners[actualIndex] < 0)
                {
                    owners[actualIndex] = expectedIndex;
                    matches[expectedIndex] = actualIndex;
                    return true;
                }
            }

            foreach (var actualIndex in candidates[expectedIndex])
            {
                if (visited[actualIndex])
                {
                    continue;
                }

                visited[actualIndex] = true;
                if (TryMatch(owners[actualIndex], visited))
                {
                    owners[actualIndex] = expectedIndex;
                    matches[expectedIndex] = actualIndex;
                    return true;
                }
            }

            return false;
        }

        // Match constrained items first; augmenting paths prevent greedy tolerance matches from losing valid pairs.
        foreach (var index in Enumerable.Range(0, candidates.Length).OrderBy(index => candidates[index].Length))
        {
            TryMatch(index, new bool[actualCount]);
        }

        return matches;
    }

    private static double PointDistance(DrawingPoint left, DrawingPoint right)
    {
        var x = left.Xmm - right.Xmm;
        var y = left.Ymm - right.Ymm;
        var z = left.Zmm - right.Zmm;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double ValueTolerance(string unit) => unit == "deg" ? AngularToleranceDegrees : LinearToleranceMm;

    private static double SafeReciprocal(double scale) => scale <= 0 ? 0 : 1.0 / scale;
}
