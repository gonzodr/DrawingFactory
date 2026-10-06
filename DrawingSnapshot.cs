public enum DrawingSnapshotRole
{
    Reference,
    Generated
}

public sealed record DrawingSnapshot(
    string FilePath,
    string DocumentTitle,
    DrawingSnapshotRole Role,
    IReadOnlyList<DrawingSheetSnapshot> Sheets,
    IReadOnlyList<string> UnresolvedInformation);

public sealed record DrawingSheetSnapshot(
    string Name,
    string SheetFormatPath,
    double WidthMm,
    double HeightMm,
    double ScaleNumerator,
    double ScaleDenominator,
    IReadOnlyList<DrawingViewSnapshot> Views,
    IReadOnlyList<DrawingAnnotationSnapshot> Annotations,
    IReadOnlyList<DrawingPropertyLinkSnapshot> PropertyLinks);

public sealed record DrawingViewSnapshot(
    string Name,
    int TypeCode,
    string TypeName,
    string Orientation,
    string ReferencedModelPath,
    string ReferencedConfiguration,
    double Scale,
    DrawingPoint? Position,
    DrawingBounds? Outline,
    IReadOnlyList<DrawingDimensionSnapshot> Dimensions,
    IReadOnlyList<DrawingAnnotationSnapshot> Annotations);

public sealed record DrawingDimensionSnapshot(
    string Name,
    int TypeCode,
    string TypeName,
    double Value,
    string Unit,
    string DisplayText,
    DrawingPoint? TextPosition,
    IReadOnlyList<DrawingPoint> ReferencePoints,
    IReadOnlyList<string> ReferencedEntityTypes,
    bool IsHoleCallout,
    bool IsReferenceDimension);

public sealed record DrawingAnnotationSnapshot(
    string NativeKind,
    int NativeTypeCode,
    string Name,
    string Text,
    string PropertyLinkedText,
    DrawingPoint? Position,
    DrawingBounds? Bounds,
    IReadOnlyList<string> ReferencedEntityTypes,
    bool IsNativeEngineeringAnnotation);

public sealed record DrawingPropertyLinkSnapshot(
    string NoteName,
    string LinkExpression,
    string ResolvedText,
    IReadOnlyList<DrawingPropertyLinkToken> PropertyTokens);

public sealed record DrawingPropertyLinkToken(string Scope, string Name);

public sealed record DrawingPoint(double Xmm, double Ymm, double Zmm);

public sealed record DrawingBounds(double LeftMm, double BottomMm, double RightMm, double TopMm);

internal static class DrawingSnapshotSemantics
{
    internal static IEnumerable<DrawingViewSnapshot> GetModelViews(DrawingSheetSnapshot sheet) =>
        sheet.Views.Where(view => view.TypeCode != (int)SolidWorks.Interop.swconst.swDrawingViewTypes_e.swDrawingSheet);

    internal static string NormalizeOrientation(string value) =>
        value.Trim().TrimStart('*').Trim().ToUpperInvariant();

    internal static double SheetScale(DrawingSheetSnapshot sheet) =>
        sheet.ScaleDenominator == 0 ? 0 : sheet.ScaleNumerator / sheet.ScaleDenominator;
}
