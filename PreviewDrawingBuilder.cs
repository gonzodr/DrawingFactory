using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed record PreviewDrawingResult(string OutputPath, DrawingSheetRecommendation Sheet, double Scale, int ModelViewCount, int SaveWarnings, int OpenWarnings);

public sealed class PreviewDrawingBuilder
{
    private readonly ISldWorks _application;
    private readonly TextWriter _diagnostics;

    public PreviewDrawingBuilder(ISldWorks application, TextWriter? diagnostics = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? Console.Error;
    }

    public PreviewDrawingResult Build(string partPath, string templatePath, string outputPath, PartAnalysis analysis,
        PreviewDrawingLayout layout, SheetFormatCatalogEntry format)
    {
        partPath = InputFiles.RequireFile(partPath, ".SLDPRT", "preview part");
        templatePath = InputFiles.RequireFile(templatePath, ".DRWDOT", "drawing template");
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".SLDDRW", StringComparison.OrdinalIgnoreCase) || File.Exists(outputPath))
        {
            throw new IOException($"Preview output must be a new SLDDRW file: {outputPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var staging = Path.Combine(Path.GetDirectoryName(outputPath)!, $".preview_{Guid.NewGuid():N}.SLDDRW");
        ModelDoc2? ownedDrawing = null;
        var completed = false;
        try
        {
            ownedDrawing = _application.NewDocument(templatePath, (int)PaperSize(layout.Sheet), 0, 0) as ModelDoc2
                ?? throw new InvalidOperationException("SOLIDWORKS did not create the preview drawing.");
            if (ownedDrawing is not IDrawingDoc drawing || ownedDrawing.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            {
                throw new InvalidOperationException("The template did not create a drawing document.");
            }
            var sheetView = drawing.GetFirstView() as View
                ?? throw new InvalidOperationException("The preview sheet view is missing.");
            if (sheetView.GetNextView() is View)
            {
                throw new InvalidOperationException("The preview template must not already contain model views.");
            }
            var sheet = drawing.GetCurrentSheet() as Sheet
                ?? throw new InvalidOperationException("The preview sheet is missing.");
            sheet.SetTemplateName(format.FullPath);
            sheet.SetProperties2((int)PaperSize(layout.Sheet), (int)swDwgTemplates_e.swDwgTemplateCustom,
                1, 1 / layout.Scale, true, layout.WidthMm / 1000, layout.HeightMm / 1000, false);
            if ((swReloadTemplateResult_e)sheet.ReloadTemplate(false) != swReloadTemplateResult_e.swReloadTemplate_Success)
            {
                throw new InvalidOperationException("The preview sheet format could not be loaded.");
            }
            sheet.SetProperties2((int)PaperSize(layout.Sheet), (int)swDwgTemplates_e.swDwgTemplateCustom,
                1, 1 / layout.Scale, true, layout.WidthMm / 1000, layout.HeightMm / 1000, false);
            View? frontView = null;
            foreach (var planned in layout.Views)
            {
                var view = drawing.CreateDrawViewFromModelView3(partPath, planned.ModelViewName,
                    planned.CenterXmm / 1000, planned.CenterYmm / 1000, 0)
                    ?? throw new InvalidOperationException($"Could not create the {planned.Name} preview view.");
                view.ScaleDecimal = layout.Scale;
                frontView ??= view;
            }
            sheet.CustomPropertyView = frontView!.GetName2();
            var properties = new PropertyMappingAnalyzer();
            properties.ApplySheetNotePropertyMappings(sheetView, analysis);
            properties.ApplyExplicitDrawingPropertyMappings(ownedDrawing, analysis);
            ownedDrawing.ClearSelection2(true);
            drawing.ActivateView(string.Empty);
            var note = ownedDrawing.InsertNote("PREVIEW - manufacturing dimensions not included") as Note
                ?? throw new InvalidOperationException("Could not add the preview-only label.");
            if (note.GetAnnotation() is not Annotation annotation || !annotation.SetPosition2(0.012, (layout.HeightMm - 8) / 1000, 0))
            {
                throw new InvalidOperationException("Could not position the preview-only label.");
            }
            if (!ownedDrawing.EditRebuild3())
            {
                throw new InvalidOperationException("The preview drawing could not be rebuilt.");
            }
            Validate(ownedDrawing, layout, partPath, format);

            var saveErrors = 0;
            var saveWarnings = 0;
            if (!ownedDrawing.SaveAs4(staging, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings) || saveErrors != 0)
            {
                throw new InvalidOperationException($"Preview save failed: errors={saveErrors}, warnings={saveWarnings}.");
            }
            _application.CloseDoc(ownedDrawing.GetTitle());
            ownedDrawing = null;
            File.Move(staging, outputPath);
            var openErrors = 0;
            var openWarnings = 0;
            ownedDrawing = _application.OpenDoc6(outputPath, (int)swDocumentTypes_e.swDocDRAWING,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref openErrors, ref openWarnings);
            if (ownedDrawing is null || openErrors != 0)
            {
                throw new InvalidOperationException($"Preview reopen failed: errors={openErrors}, warnings={openWarnings}.");
            }
            Validate(ownedDrawing, layout, partPath, format);
            completed = true;
            return new PreviewDrawingResult(outputPath, layout.Sheet, layout.Scale, layout.Views.Count, saveWarnings, openWarnings);
        }
        finally
        {
            if (!completed && ownedDrawing is not null)
            {
                try { _application.CloseDoc(ownedDrawing.GetTitle()); }
                catch (COMException exception) { _diagnostics.WriteLine($"Could not close incomplete preview: {exception.Message}"); }
            }
            try { File.Delete(staging); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _diagnostics.WriteLine($"Could not remove preview staging file: {exception.Message}"); }
        }
    }

    private static void Validate(ModelDoc2 document, PreviewDrawingLayout layout, string partPath, SheetFormatCatalogEntry format)
    {
        if (document is not IDrawingDoc drawing || document.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
        {
            throw new InvalidOperationException("The saved preview is not a drawing.");
        }
        var sheet = drawing.GetCurrentSheet() as Sheet ?? throw new InvalidOperationException("Preview sheet missing.");
        var width = 0.0;
        var height = 0.0;
        sheet.GetSize(ref width, ref height);
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            Math.Abs(width * 1000 - layout.WidthMm) > 1 || Math.Abs(height * 1000 - layout.HeightMm) > 1 ||
            !string.Equals(Path.GetFullPath(sheet.GetTemplateName()), format.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The preview sheet size or format did not persist.");
        }
        var bounds = new List<DrawingBounds>();
        var orientations = new List<string>();
        var view = (drawing.GetFirstView() as View)?.GetNextView() as View;
        while (view is not null)
        {
            if (!double.IsFinite(view.ScaleDecimal) || Math.Abs(view.ScaleDecimal - layout.Scale) > 0.000001 ||
                !string.Equals(Path.GetFullPath(view.GetReferencedModelName()), partPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A preview view has an incorrect scale or referenced part.");
            }
            if (view.GetOutline() is not Array outline || outline.Length < 4)
            {
                throw new InvalidOperationException("Could not read preview view bounds.");
            }
            var start = outline.GetLowerBound(0);
            bounds.Add(new DrawingBounds(Convert.ToDouble(outline.GetValue(start)) * 1000,
                Convert.ToDouble(outline.GetValue(start + 1)) * 1000, Convert.ToDouble(outline.GetValue(start + 2)) * 1000,
                Convert.ToDouble(outline.GetValue(start + 3)) * 1000));
            orientations.Add(view.GetOrientationName().Trim().TrimStart('*').Replace(" View", "", StringComparison.OrdinalIgnoreCase));
            view = view.GetNextView() as View;
        }
        if (!PreviewLayoutPlanner.Fits(bounds, layout.WidthMm, layout.HeightMm) ||
            !orientations.OrderBy(name => name).SequenceEqual(new[] { "Front", "Right", "Top" }, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Preview views overlap, leave the drawing area, or have incorrect orientations.");
        }
    }

    private static swDwgPaperSizes_e PaperSize(DrawingSheetRecommendation sheet) => sheet switch
    {
        DrawingSheetRecommendation.A4Landscape => swDwgPaperSizes_e.swDwgPaperA4size,
        DrawingSheetRecommendation.A3Landscape => swDwgPaperSizes_e.swDwgPaperA3size,
        DrawingSheetRecommendation.A2Landscape => swDwgPaperSizes_e.swDwgPaperA2size,
        DrawingSheetRecommendation.A1Landscape => swDwgPaperSizes_e.swDwgPaperA1size,
        DrawingSheetRecommendation.A0Landscape => swDwgPaperSizes_e.swDwgPaperA0size,
        _ => throw new ArgumentOutOfRangeException(nameof(sheet))
    };
}
