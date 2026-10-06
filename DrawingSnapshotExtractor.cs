using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed class DrawingSnapshotExtractor
{
    private const double MetersToMillimeters = 1000.0;
    private static readonly Regex PropertyTokenPattern = new(
        "\\$(?<scope>PRPSHEET|PRP):\"(?<name>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DrawingSnapshot Extract(ModelDoc2 document, string path, DrawingSnapshotRole role)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.GetType() != (int)swDocumentTypes_e.swDocDRAWING || document is not IDrawingDoc drawing)
        {
            throw new ArgumentException("The supplied document is not a SOLIDWORKS drawing.", nameof(document));
        }

        var unresolved = new List<string>();
        var sheets = new List<DrawingSheetSnapshot>();
        var originalSheetName = (drawing.GetCurrentSheet() as Sheet)?.GetName();
        var sheetNames = drawing.GetSheetNames() as Array
            ?? throw new InvalidOperationException("SOLIDWORKS did not return drawing sheet names.");

        try
        {
            foreach (var sheetItem in sheetNames)
            {
                if (sheetItem is not string sheetName || string.IsNullOrWhiteSpace(sheetName))
                {
                    continue;
                }

                if (!drawing.ActivateSheet(sheetName))
                {
                    unresolved.Add($"Could not activate sheet '{sheetName}' for read-only analysis.");
                    continue;
                }

                var sheet = drawing.GetCurrentSheet() as Sheet;
                if (sheet is null)
                {
                    unresolved.Add($"Could not access sheet '{sheetName}'.");
                    continue;
                }

                sheets.Add(ExtractSheet(drawing, sheet, unresolved));
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(originalSheetName))
            {
                drawing.ActivateSheet(originalSheetName);
            }
        }

        return new DrawingSnapshot(
            Path.GetFullPath(path),
            document.GetTitle(),
            role,
            sheets,
            unresolved);
    }

    private static DrawingSheetSnapshot ExtractSheet(
        IDrawingDoc drawing,
        Sheet sheet,
        ICollection<string> unresolved)
    {
        var width = 0.0;
        var height = 0.0;
        sheet.GetSize(ref width, ref height);
        var properties = sheet.GetProperties2() as Array;
        var lowerBound = properties?.GetLowerBound(0) ?? 0;
        var numerator = ReadArrayDouble(properties, lowerBound + 2);
        var denominator = ReadArrayDouble(properties, lowerBound + 3);
        var views = new List<DrawingViewSnapshot>();
        var allAnnotations = new List<DrawingAnnotationSnapshot>();
        var propertyLinks = new List<DrawingPropertyLinkSnapshot>();

        var view = drawing.GetFirstView() as View;
        while (view is not null)
        {
            var extracted = ExtractView(view, unresolved);
            views.Add(extracted);
            allAnnotations.AddRange(extracted.Annotations);
            foreach (var annotation in extracted.Annotations)
            {
                var tokens = ExtractPropertyTokens(annotation.PropertyLinkedText);
                if (tokens.Count == 0)
                {
                    continue;
                }

                propertyLinks.Add(new DrawingPropertyLinkSnapshot(
                    annotation.Name,
                    annotation.PropertyLinkedText,
                    annotation.Text,
                    tokens));
            }

            view = view.GetNextView() as View;
        }

        return new DrawingSheetSnapshot(
            sheet.GetName(),
            sheet.GetTemplateName() ?? string.Empty,
            width * MetersToMillimeters,
            height * MetersToMillimeters,
            numerator,
            denominator,
            views,
            allAnnotations,
            propertyLinks);
    }

    private static IReadOnlyList<DrawingPropertyLinkToken> ExtractPropertyTokens(string linkText) =>
        PropertyTokenPattern.Matches(linkText)
            .Select(match => new DrawingPropertyLinkToken(
                match.Groups["scope"].Value.ToUpperInvariant(),
                match.Groups["name"].Value))
            .ToArray();

    private static DrawingViewSnapshot ExtractView(View view, ICollection<string> unresolved)
    {
        var annotations = ReadAnnotations(view, unresolved);
        var dimensions = annotations
            .Where(annotation => annotation.NativeTypeCode == (int)swAnnotationType_e.swDisplayDimension)
            .Select(annotation => ExtractDimension(annotation, view, unresolved))
            .Where(dimension => dimension is not null)
            .Cast<DrawingDimensionSnapshot>()
            .ToArray();

        return new DrawingViewSnapshot(
            view.GetName2(),
            view.Type,
            Enum.IsDefined(typeof(swDrawingViewTypes_e), view.Type)
                ? ((swDrawingViewTypes_e)view.Type).ToString()
                : $"Unknown({view.Type})",
            view.GetOrientationName() ?? string.Empty,
            view.GetReferencedModelName() ?? string.Empty,
            view.ReferencedConfiguration ?? string.Empty,
            view.ScaleDecimal,
            ReadPoint(view.Position),
            ReadBounds(view.GetOutline()),
            dimensions,
            annotations);
    }

    private static IReadOnlyList<DrawingAnnotationSnapshot> ReadAnnotations(
        View view,
        ICollection<string> unresolved)
    {
        var annotations = new List<DrawingAnnotationSnapshot>();
        var items = view.GetAnnotations() as Array;
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is Annotation annotation)
                {
                    annotations.Add(ExtractAnnotation(annotation, unresolved));
                }
            }

            return annotations;
        }

        var current = view.GetFirstAnnotation3();
        while (current is not null)
        {
            annotations.Add(ExtractAnnotation(current, unresolved));
            current = current.GetNext3();
        }

        return annotations;
    }

    private static DrawingAnnotationSnapshot ExtractAnnotation(
        Annotation annotation,
        ICollection<string> unresolved)
    {
        var typeCode = annotation.GetType();
        var specific = annotation.GetSpecificAnnotation();
        var nativeKind = GetNativeKind(typeCode);
        if (typeCode == (int)swAnnotationType_e.swDisplayDimension &&
            specific is DisplayDimension nativeDimension && nativeDimension.IsHoleCallout())
        {
            nativeKind = "HoleCallout";
        }

        var text = string.Empty;
        var link = string.Empty;
        var bounds = (DrawingBounds?)null;
        var isNativeEngineeringAnnotation = typeCode is
            (int)swAnnotationType_e.swDisplayDimension or
            (int)swAnnotationType_e.swGTol or
            (int)swAnnotationType_e.swDatumTag or
            (int)swAnnotationType_e.swDatumTargetSym or
            (int)swAnnotationType_e.swDatumOrigin or
            (int)swAnnotationType_e.swSFSymbol or
            (int)swAnnotationType_e.swCenterMarkSym or
            (int)swAnnotationType_e.swCenterLine or
            (int)swAnnotationType_e.swCThread;

        try
        {
            switch ((swAnnotationType_e)typeCode)
            {
                case swAnnotationType_e.swNote when specific is Note note:
                    text = note.GetText() ?? string.Empty;
                    link = note.PropertyLinkedText ?? string.Empty;
                    bounds = ReadBounds(note.GetExtent());
                    break;
                case swAnnotationType_e.swDisplayDimension when specific is DisplayDimension dimension:
                    text = dimension.GetText((int)swDimensionTextParts_e.swDimensionTextAll) ?? string.Empty;
                    break;
                case swAnnotationType_e.swGTol when specific is Gtol gtol:
                    text = JoinIndexedText(gtol.GetTextCount(), gtol.GetTextAtIndex);
                    break;
                case swAnnotationType_e.swDatumTag when specific is DatumTag datum:
                    text = JoinIndexedText(datum.GetTextCount(), datum.GetTextAtIndex);
                    break;
                case swAnnotationType_e.swSFSymbol when specific is SFSymbol surfaceFinish:
                    text = JoinIndexedText(surfaceFinish.GetTextCount(), surfaceFinish.GetTextAtIndex);
                    break;
                case swAnnotationType_e.swCenterMarkSym when specific is CenterMark centerMark:
                    text = $"Native center mark; position points={(centerMark.GetPosition(0) is Array positions ? positions.Length : 0)}";
                    break;
                case swAnnotationType_e.swCenterLine when specific is Centerline:
                    text = "Native centerline";
                    break;
                case swAnnotationType_e.swCThread:
                    text = "Native cosmetic thread annotation";
                    break;
            }
        }
        catch (COMException exception)
        {
            unresolved.Add($"Could not read {nativeKind} annotation '{annotation.GetName()}': {exception.Message}");
        }

        return new DrawingAnnotationSnapshot(
            nativeKind,
            typeCode,
            annotation.GetName() ?? string.Empty,
            text,
            link,
            ReadPoint(annotation.GetPosition()),
            bounds,
            ReadAttachedEntityTypes(annotation, unresolved),
            isNativeEngineeringAnnotation);
    }

    private static DrawingDimensionSnapshot? ExtractDimension(
        DrawingAnnotationSnapshot annotationSnapshot,
        View view,
        ICollection<string> unresolved)
    {
        var annotation = FindAnnotation(view, annotationSnapshot.Name);
        if (annotation?.GetSpecificAnnotation() is not DisplayDimension displayDimension ||
            displayDimension.GetDimension() is not Dimension dimension)
        {
            unresolved.Add($"Could not retrieve the native dimension object for '{annotationSnapshot.Name}'.");
            return null;
        }

        var typeCode = displayDimension.Type2;
        var typeName = GetSemanticDimensionTypeName(typeCode);
        var nativeValue = dimension.SystemValue;
        var unit = "model units";
        if (IsLengthDimension(typeCode))
        {
            nativeValue *= MetersToMillimeters;
            unit = "mm";
        }
        else if (IsAngularDimension(typeCode))
        {
            nativeValue *= 180.0 / Math.PI;
            unit = "deg";
        }

        return new DrawingDimensionSnapshot(
            annotationSnapshot.Name,
            typeCode,
            typeName,
            nativeValue,
            unit,
            annotationSnapshot.Text,
            annotationSnapshot.Position,
            ReadReferencePoints(dimension.ReferencePoints),
            annotationSnapshot.ReferencedEntityTypes,
            displayDimension.IsHoleCallout(),
            dimension.IsReference());
    }

    private static Annotation? FindAnnotation(View view, string name)
    {
        if (view.GetAnnotations() is Array items)
        {
            return items.OfType<Annotation>().FirstOrDefault(annotation =>
                string.Equals(annotation.GetName(), name, StringComparison.OrdinalIgnoreCase));
        }

        var current = view.GetFirstAnnotation3();
        while (current is not null)
        {
            if (string.Equals(current.GetName(), name, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = current.GetNext3();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadAttachedEntityTypes(
        Annotation annotation,
        ICollection<string> unresolved)
    {
        try
        {
            return (annotation.GetAttachedEntities3() as Array ?? Array.Empty<object>())
                .Cast<object>()
                .Select(entity => entity.GetType().Name)
                .ToArray();
        }
        catch (COMException exception)
        {
            unresolved.Add($"Could not inspect attached entities for annotation '{annotation.GetName()}': {exception.Message}");
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<DrawingPoint> ReadReferencePoints(object? referencePoints)
    {
        if (referencePoints is not Array points)
        {
            return Array.Empty<DrawingPoint>();
        }

        return points.Cast<object>()
            .Select(ReadPoint)
            .Where(point => point is not null)
            .Cast<DrawingPoint>()
            .ToArray();
    }

    private static DrawingPoint? ReadPoint(object? value)
    {
        object? coordinateData = value;
        if (value is MathPoint point)
        {
            coordinateData = point.ArrayData;
        }

        if (coordinateData is not Array coordinates || coordinates.Length < 2)
        {
            return null;
        }

        var lowerBound = coordinates.GetLowerBound(0);
        var x = Convert.ToDouble(coordinates.GetValue(lowerBound), CultureInfo.InvariantCulture);
        var y = Convert.ToDouble(coordinates.GetValue(lowerBound + 1), CultureInfo.InvariantCulture);
        var z = coordinates.Length > 2
            ? Convert.ToDouble(coordinates.GetValue(lowerBound + 2), CultureInfo.InvariantCulture)
            : 0.0;
        return new DrawingPoint(x * MetersToMillimeters, y * MetersToMillimeters, z * MetersToMillimeters);
    }

    private static DrawingBounds? ReadBounds(object? value)
    {
        if (value is not Array bounds || bounds.Length < 4)
        {
            return null;
        }

        var lowerBound = bounds.GetLowerBound(0);
        return new DrawingBounds(
            Convert.ToDouble(bounds.GetValue(lowerBound), CultureInfo.InvariantCulture) * MetersToMillimeters,
            Convert.ToDouble(bounds.GetValue(lowerBound + 1), CultureInfo.InvariantCulture) * MetersToMillimeters,
            Convert.ToDouble(bounds.GetValue(lowerBound + 2), CultureInfo.InvariantCulture) * MetersToMillimeters,
            Convert.ToDouble(bounds.GetValue(lowerBound + 3), CultureInfo.InvariantCulture) * MetersToMillimeters);
    }

    private static double ReadArrayDouble(Array? values, int index)
    {
        if (values is null || index > values.GetUpperBound(0))
        {
            return 0;
        }

        return Convert.ToDouble(values.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static string JoinIndexedText(int count, Func<int, string> getText)
    {
        var text = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var item = getText(index);
            if (!string.IsNullOrWhiteSpace(item))
            {
                text.Add(item);
            }
        }

        return string.Join(" | ", text);
    }

    private static string GetNativeKind(int typeCode) => (swAnnotationType_e)typeCode switch
    {
        swAnnotationType_e.swCThread => "CosmeticThread",
        swAnnotationType_e.swDatumTag => "DatumTag",
        swAnnotationType_e.swDatumTargetSym => "DatumTarget",
        swAnnotationType_e.swDisplayDimension => "DisplayDimension",
        swAnnotationType_e.swGTol => "GeometricTolerance",
        swAnnotationType_e.swNote => "Note",
        swAnnotationType_e.swSFSymbol => "SurfaceFinish",
        swAnnotationType_e.swCenterMarkSym => "CenterMark",
        swAnnotationType_e.swCenterLine => "Centerline",
        swAnnotationType_e.swDatumOrigin => "DatumOrigin",
        _ => $"Annotation({typeCode})"
    };

    private static bool IsLengthDimension(int typeCode) => typeCode is
        (int)swDimensionType_e.swLinearDimension or
        (int)swDimensionType_e.swOrdinateDimension or
        (int)swDimensionType_e.swRadialDimension or
        (int)swDimensionType_e.swDiameterDimension or
        (int)swDimensionType_e.swHorOrdinateDimension or
        (int)swDimensionType_e.swVertOrdinateDimension or
        (int)swDimensionType_e.swZAxisDimension or
        (int)swDimensionType_e.swHorLinearDimension or
        (int)swDimensionType_e.swVertLinearDimension or
        (int)swDimensionType_e.swRadialLinearDimension or
        (int)swDimensionType_e.swDiametricLinearDimension;

    private static bool IsAngularDimension(int typeCode) => typeCode is
        (int)swDimensionType_e.swAngularDimension or
        (int)swDimensionType_e.swAngularOrdinateDimension;

    private static string GetSemanticDimensionTypeName(int typeCode) => typeCode switch
    {
        (int)swDimensionType_e.swDiametricLinearDimension => swDimensionType_e.swDiameterDimension.ToString(),
        (int)swDimensionType_e.swRadialLinearDimension => swDimensionType_e.swRadialDimension.ToString(),
        _ when Enum.IsDefined(typeof(swDimensionType_e), typeCode) => ((swDimensionType_e)typeCode).ToString(),
        _ => $"Unknown({typeCode})"
    };
}
