using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class DrawingBuilder
{
    private readonly ISldWorks _application;
    private readonly TextWriter _diagnostics;

    public DrawingBuilder(ISldWorks application, TextWriter? diagnostics = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? Console.Error;
    }

    public static string GetOutputPath(string partPath)
    {
        var fullPartPath = GetFullPartPath(partPath);
        var outputFileName = $"{Path.GetFileNameWithoutExtension(fullPartPath)}_AUTO_V4D.SLDDRW";
        return Path.Combine(Path.GetDirectoryName(fullPartPath)!, outputFileName);
    }

    public DrawingBuildResult Build(
        string partPath,
        string templatePath,
        DrawingPlan plan,
        SheetFormatCatalogEntry sheetFormat,
        DimensionPlan dimensionPlan,
        DimensionLayoutPlan layoutPlan,
        ManufacturingFeatureSet features,
        PartAnalysis partAnalysis)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sheetFormat);
        ArgumentNullException.ThrowIfNull(dimensionPlan);
        ArgumentNullException.ThrowIfNull(layoutPlan);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(partAnalysis);
        plan = layoutPlan.FinalDrawingPlan;
        var primaryPlan = ValidatePlan(plan);

        var fullPartPath = GetFullPartPath(partPath);
        if (!File.Exists(fullPartPath))
        {
            throw new FileNotFoundException("The SOLIDWORKS part file was not found.", fullPartPath);
        }

        var fullTemplatePath = InputFiles.RequireFile(templatePath, ".DRWDOT", "SOLIDWORKS drawing template");

        var outputPath = GetOutputPath(fullPartPath);
        if (File.Exists(outputPath))
        {
            throw new IOException($"Refusing to overwrite the existing drawing: {outputPath}");
        }

        var stagingPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.SLDDRW");
        using (new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        ModelDoc2? ownedDrawing = null;
        var completed = false;
        try
        {
            var (expectedSheetWidthMm, expectedSheetHeightMm) = GetSheetDimensions(plan.SheetRecommendation);
            var createdDocument = _application.NewDocument(
                fullTemplatePath,
                (int)ToSolidWorksPaperSize(plan.SheetRecommendation),
                0.0,
                0.0);
            ownedDrawing = createdDocument as ModelDoc2;
            if (createdDocument is not ModelDoc2 drawing ||
                drawing.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            {
                throw new InvalidOperationException("SOLIDWORKS did not create a drawing document from the template.");
            }

            if (drawing is not IDrawingDoc drawingApi)
            {
                throw new InvalidOperationException("The new document does not expose the drawing API.");
            }

            var sheetView = drawingApi.GetFirstView() as View
                ?? throw new InvalidOperationException("Could not access the drawing sheet view.");
            if (sheetView.GetNextView() is View)
            {
                throw new InvalidOperationException("The drawing template already contains a model view.");
            }

            var sheet = drawingApi.GetCurrentSheet() as Sheet
                ?? throw new InvalidOperationException("Could not access the active drawing sheet.");
            sheet.SetProperties(
                (int)ToSolidWorksPaperSize(plan.SheetRecommendation),
                (int)swDwgTemplates_e.swDwgTemplateCustom,
                1.0,
                1.0,
                true,
                expectedSheetWidthMm / 1000.0,
                expectedSheetHeightMm / 1000.0);
            sheet.SetTemplateName(sheetFormat.FullPath);
            var reloadStatus = (swReloadTemplateResult_e)sheet.ReloadTemplate(false);
            if (reloadStatus != swReloadTemplateResult_e.swReloadTemplate_Success)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS could not reload sheet format '{sheetFormat.FileName}': {reloadStatus}.");
            }

            sheet.SetProperties2(
                (int)ToSolidWorksPaperSize(plan.SheetRecommendation),
                (int)swDwgTemplates_e.swDwgTemplateCustom,
                1.0,
                1.0 / plan.TargetScale,
                true,
                expectedSheetWidthMm / 1000.0,
                expectedSheetHeightMm / 1000.0,
                false);
            ValidateSheetScale(sheet, plan.TargetScale);

            var loadedTemplatePath = sheet.GetTemplateName();
            if (!string.Equals(
                    Path.GetFullPath(loadedTemplatePath),
                    sheetFormat.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unexpected sheet format after reload: '{loadedTemplatePath}'. Expected '{sheetFormat.FullPath}'.");
            }

            var formatSketch = sheet.GetTemplateSketch()
                ?? throw new InvalidOperationException("The selected sheet format has no template sketch.");
            var formatSegmentCount = (formatSketch.GetSketchSegments() as Array)?.Length ?? 0;
            if (formatSegmentCount == 0)
            {
                throw new InvalidOperationException("The selected sheet format contains no border/title-block sketch geometry.");
            }

            var sheetWidth = 0.0;
            var sheetHeight = 0.0;
            sheet.GetSize(ref sheetWidth, ref sheetHeight);
            if (sheetWidth <= 0 || sheetHeight <= 0)
            {
                throw new InvalidOperationException("SOLIDWORKS returned invalid drawing sheet dimensions.");
            }

            ValidateSheetSize(sheetWidth * 1000.0, sheetHeight * 1000.0, expectedSheetWidthMm, expectedSheetHeightMm);

            var createdView = drawingApi.CreateDrawViewFromModelView3(
                fullPartPath,
                primaryPlan.ModelViewName,
                sheetWidth * layoutPlan.PrimaryCenterXFraction,
                sheetHeight * layoutPlan.PrimaryCenterYFraction,
                0.0);
            if (createdView is null)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS did not create the planned model view '{primaryPlan.ModelViewName}'.");
            }

            createdView.ScaleDecimal = plan.TargetScale;
            var createdSecondaryViews = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
            foreach (var secondaryPlan in plan.RequestedSecondaryViews)
            {
                var secondaryView = drawingApi.CreateDrawViewFromModelView3(
                    fullPartPath,
                    secondaryPlan.ModelViewName,
                    sheetWidth * layoutPlan.ThicknessViewCenterXFraction,
                    sheetHeight * layoutPlan.ThicknessViewCenterYFraction,
                    0.0);
                if (secondaryView is null)
                {
                    throw new InvalidOperationException(
                        $"SOLIDWORKS did not create the planned {secondaryPlan.Purpose} view '{secondaryPlan.ModelViewName}'.");
                }

                secondaryView.ScaleDecimal = plan.TargetScale;
                createdSecondaryViews.Add(secondaryPlan.Purpose, secondaryView);
            }

            sheet.CustomPropertyView = createdView.GetName2();
            if (!drawing.EditRebuild3())
            {
                throw new InvalidOperationException("SOLIDWORKS failed to rebuild the drawing after linking its sheet properties to the primary view.");
            }

            var propertyMappingAnalyzer = new PropertyMappingAnalyzer();
            _ = propertyMappingAnalyzer.ApplySheetNotePropertyMappings(sheetView, partAnalysis);
            if (!drawing.EditRebuild3())
            {
                throw new InvalidOperationException("SOLIDWORKS failed to rebuild after mapping company sheet notes to model properties.");
            }

            _ = propertyMappingAnalyzer.ApplyExplicitDrawingPropertyMappings(drawing, partAnalysis);
            if (!drawing.EditRebuild3())
            {
                throw new InvalidOperationException("SOLIDWORKS failed to rebuild after applying explicit drawing property mappings.");
            }

            var propertyMappings = propertyMappingAnalyzer.Analyze(sheetView, partAnalysis, drawing);
            if (!propertyMappings.AllExpectedValuesResolve)
            {
                throw new InvalidOperationException("One or more title-block property links did not resolve to the matching model property value.");
            }

            if (!createdSecondaryViews.TryGetValue("Thickness", out var thicknessView))
            {
                throw new InvalidOperationException("The thickness view was not created or could not be identified.");
            }
            var dimensionReport = new DrawingDimensionExecutor().Execute(
                drawing,
                dimensionPlan,
                layoutPlan,
                features,
                createdView,
                thicknessView,
                sheetWidth * 1000.0,
                sheetHeight * 1000.0);
            if (!dimensionReport.SemanticIdsUnique)
            {
                throw new InvalidOperationException("The generated dimension set contains duplicate semantic requirements.");
            }

            sheet.SetProperties2(
                (int)ToSolidWorksPaperSize(plan.SheetRecommendation),
                (int)swDwgTemplates_e.swDwgTemplateCustom,
                1.0,
                1.0 / plan.TargetScale,
                true,
                expectedSheetWidthMm / 1000.0,
                expectedSheetHeightMm / 1000.0,
                false);
            ValidateSheetScale(sheet, plan.TargetScale);
            if (!drawing.EditRebuild3())
            {
                throw new InvalidOperationException("SOLIDWORKS failed to rebuild after reapplying the planned sheet scale.");
            }

            _ = ValidateDrawingView(
                drawingApi,
                plan,
                sheetWidth * 1000.0,
                sheetHeight * 1000.0);

            var saveErrors = 0;
            var saveWarnings = 0;
            var saveSucceeded = drawing.SaveAs4(
                stagingPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                ref saveErrors,
                ref saveWarnings);
            if (!saveSucceeded || saveErrors != 0)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS could not save the drawing. Save errors: {saveErrors}; warnings: {saveWarnings}.");
            }

            _application.CloseDoc(drawing.GetTitle());
            ownedDrawing = null;
            File.Move(stagingPath, outputPath);

            var openErrors = 0;
            var openWarnings = 0;
            var savedDrawing = _application.OpenDoc6(
                outputPath,
                (int)swDocumentTypes_e.swDocDRAWING,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref openErrors,
                ref openWarnings);
            if ((openWarnings & (int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen) == 0)
            {
                ownedDrawing = savedDrawing;
            }
            if (savedDrawing is null || openErrors != 0)
            {
                throw new InvalidOperationException(
                    $"The saved drawing could not be reopened. Load errors: {openErrors}; warnings: {openWarnings}.");
            }

            var reopenedDrawingApi = savedDrawing as IDrawingDoc;
            if (savedDrawing.GetType() != (int)swDocumentTypes_e.swDocDRAWING ||
                reopenedDrawingApi is null)
            {
                throw new InvalidOperationException("The saved file did not reopen as a SOLIDWORKS drawing.");
            }

            var reopenedSheet = reopenedDrawingApi.GetCurrentSheet() as Sheet
                ?? throw new InvalidOperationException("Could not access the reopened drawing sheet.");
            var reopenedSheetWidth = 0.0;
            var reopenedSheetHeight = 0.0;
            reopenedSheet.GetSize(ref reopenedSheetWidth, ref reopenedSheetHeight);
            ValidateSheetSize(
                reopenedSheetWidth * 1000.0,
                reopenedSheetHeight * 1000.0,
                expectedSheetWidthMm,
                expectedSheetHeightMm);
            ValidateSheetScale(reopenedSheet, plan.TargetScale);

            var reopenedFormatPath = reopenedSheet.GetTemplateName();
            if (!string.Equals(
                    Path.GetFullPath(reopenedFormatPath),
                    sheetFormat.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The reopened drawing uses sheet format '{reopenedFormatPath}', expected '{sheetFormat.FullPath}'.");
            }

            var reopenedFormatSketch = reopenedSheet.GetTemplateSketch()
                ?? throw new InvalidOperationException("The reopened drawing has no sheet-format sketch.");
            var reopenedFormatSegmentCount = (reopenedFormatSketch.GetSketchSegments() as Array)?.Length ?? 0;
            if (reopenedFormatSegmentCount == 0)
            {
                throw new InvalidOperationException("The reopened sheet format has no border/title-block sketch geometry.");
            }

            var reopenedValidation = ValidateDrawingView(
                reopenedDrawingApi,
                plan,
                reopenedSheetWidth * 1000.0,
                reopenedSheetHeight * 1000.0);
            if (reopenedValidation.TotalDimensionCount < dimensionReport.Created.Count)
            {
                throw new InvalidOperationException(
                    $"Expected at least {dimensionReport.Created.Count} model dimensions after reopen; found {reopenedValidation.TotalDimensionCount}.");
            }

            var reopenedSheetView = reopenedDrawingApi.GetFirstView() as View
                ?? throw new InvalidOperationException("Could not access the reopened sheet-format notes.");
            var reopenedPropertyMappings = new PropertyMappingAnalyzer().Analyze(reopenedSheetView, partAnalysis, savedDrawing);
            if (!reopenedPropertyMappings.AllExpectedValuesResolve)
            {
                throw new InvalidOperationException("A linked title-block property did not resolve after reopening the saved drawing.");
            }

            completed = true;
            return new DrawingBuildResult(
                outputPath,
                true,
                sheetFormat.FileName,
                reopenedFormatPath,
                reopenedFormatSegmentCount,
                (int)reloadStatus,
                reopenedSheetWidth * 1000.0,
                reopenedSheetHeight * 1000.0,
                plan.TargetScale,
                reopenedValidation.ModelViewCount,
                reopenedValidation.PrimaryViewExists,
                reopenedValidation.ThicknessViewExists,
                reopenedValidation.NoOverlap,
                reopenedValidation.AllViewsFit,
                reopenedValidation.PrimaryViewWidthMm,
                reopenedValidation.PrimaryViewHeightMm,
                reopenedValidation.ProjectedAreaRatio,
                saveErrors,
                saveWarnings,
                openErrors,
                openWarnings,
                reopenedValidation.TotalDimensionCount,
                reopenedPropertyMappings,
                dimensionReport);
        }
        finally
        {
            if (!completed && ownedDrawing is not null)
            {
                try
                {
                    _application.CloseDoc(ownedDrawing.GetTitle());
                }
                catch (COMException exception)
                {
                    _diagnostics.WriteLine($"Could not close the incomplete drawing: {exception.Message}");
                }
            }

            if (File.Exists(stagingPath))
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _diagnostics.WriteLine($"Could not remove staging drawing '{stagingPath}': {exception.Message}");
                }
            }
        }
    }

    public DrawingBuildResult ValidateExistingDrawing(
        string drawingPath,
        DrawingPlan plan,
        SheetFormatCatalogEntry sheetFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sheetFormat);

        ValidatePlan(plan);
        var fullDrawingPath = InputFiles.RequireFile(drawingPath, ".SLDDRW", "drawing to validate");
        using var documents = new SolidWorksDocumentScope(_application, _diagnostics);
        var drawing = documents.Open(fullDrawingPath, swDocumentTypes_e.swDocDRAWING, readOnly: true, out var warnings);
        var openErrors = 0;
        var openWarnings = (int)warnings;

        var drawingApi = drawing as IDrawingDoc
            ?? throw new InvalidOperationException("The validated document does not expose the drawing API.");
        var sheet = drawingApi.GetCurrentSheet() as Sheet
            ?? throw new InvalidOperationException("Could not access the validated drawing sheet.");
        var (expectedSheetWidthMm, expectedSheetHeightMm) = GetSheetDimensions(plan.SheetRecommendation);
        var sheetWidth = 0.0;
        var sheetHeight = 0.0;
        sheet.GetSize(ref sheetWidth, ref sheetHeight);
        ValidateSheetSize(
            sheetWidth * 1000.0,
            sheetHeight * 1000.0,
            expectedSheetWidthMm,
            expectedSheetHeightMm);
        ValidateSheetScale(sheet, plan.TargetScale);

        var loadedFormatPath = sheet.GetTemplateName();
        if (!string.Equals(
                Path.GetFullPath(loadedFormatPath),
                sheetFormat.FullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The drawing uses sheet format '{loadedFormatPath}', expected '{sheetFormat.FullPath}'.");
        }

        var formatSketch = sheet.GetTemplateSketch()
            ?? throw new InvalidOperationException("The drawing has no sheet-format sketch.");
        var formatSegmentCount = (formatSketch.GetSketchSegments() as Array)?.Length ?? 0;
        if (formatSegmentCount == 0)
        {
            throw new InvalidOperationException("The sheet format has no border/title-block sketch geometry.");
        }

        var validation = ValidateDrawingView(
            drawingApi,
            plan,
            sheetWidth * 1000.0,
            sheetHeight * 1000.0);
        return new DrawingBuildResult(
            fullDrawingPath,
            true,
            sheetFormat.FileName,
            loadedFormatPath,
            formatSegmentCount,
            (int)swReloadTemplateResult_e.swReloadTemplate_Success,
            sheetWidth * 1000.0,
            sheetHeight * 1000.0,
            plan.TargetScale,
            validation.ModelViewCount,
            validation.PrimaryViewExists,
            validation.ThicknessViewExists,
            validation.NoOverlap,
            validation.AllViewsFit,
            validation.PrimaryViewWidthMm,
            validation.PrimaryViewHeightMm,
            validation.ProjectedAreaRatio,
            0,
            0,
            openErrors,
            openWarnings);
    }

    private static PrimaryViewPlan ValidatePlan(DrawingPlan plan)
    {
        if (!string.Equals(plan.StrategyName, "FlatPart", StringComparison.Ordinal) ||
            plan.PrimaryView is null || !double.IsFinite(plan.TargetScale) || plan.TargetScale <= 0)
        {
            throw new InvalidOperationException("DrawingBuilder requires a valid FlatPart drawing plan.");
        }

        return plan.PrimaryView;
    }

    private static swDwgPaperSizes_e ToSolidWorksPaperSize(DrawingSheetRecommendation recommendation) =>
        recommendation switch
        {
            DrawingSheetRecommendation.A4Landscape => swDwgPaperSizes_e.swDwgPaperA4size,
            DrawingSheetRecommendation.A3Landscape => swDwgPaperSizes_e.swDwgPaperA3size,
            DrawingSheetRecommendation.A2Landscape => swDwgPaperSizes_e.swDwgPaperA2size,
            DrawingSheetRecommendation.A1Landscape => swDwgPaperSizes_e.swDwgPaperA1size,
            DrawingSheetRecommendation.A0Landscape => swDwgPaperSizes_e.swDwgPaperA0size,
            _ => throw new ArgumentOutOfRangeException(nameof(recommendation))
        };

    private static (double WidthMm, double HeightMm) GetSheetDimensions(
        DrawingSheetRecommendation recommendation) => recommendation switch
    {
        DrawingSheetRecommendation.A4Landscape => (297.0, 210.0),
        DrawingSheetRecommendation.A3Landscape => (420.0, 297.0),
        DrawingSheetRecommendation.A2Landscape => (594.0, 420.0),
        DrawingSheetRecommendation.A1Landscape => (841.0, 594.0),
        DrawingSheetRecommendation.A0Landscape => (1189.0, 841.0),
        _ => throw new ArgumentOutOfRangeException(nameof(recommendation))
    };

    private static void ValidateSheetSize(
        double actualWidthMm,
        double actualHeightMm,
        double expectedWidthMm,
        double expectedHeightMm)
    {
        if (!double.IsFinite(actualWidthMm) || !double.IsFinite(actualHeightMm) ||
            actualWidthMm <= 0 || actualHeightMm <= 0 ||
            Math.Abs(actualWidthMm - expectedWidthMm) > 1.0 ||
            Math.Abs(actualHeightMm - expectedHeightMm) > 1.0)
        {
            throw new InvalidOperationException(
                $"Expected sheet size {expectedWidthMm:0.#} x {expectedHeightMm:0.#} mm, " +
                $"but SOLIDWORKS reports {actualWidthMm:0.##} x {actualHeightMm:0.##} mm.");
        }
    }

    private static void ValidateSheetScale(Sheet sheet, double expectedScale)
    {
        if (sheet.GetProperties2() is not Array properties || properties.Length < 4)
        {
            throw new InvalidOperationException("SOLIDWORKS did not return complete sheet scale properties.");
        }

        var lowerBound = properties.GetLowerBound(0);
        var numerator = Convert.ToDouble(properties.GetValue(lowerBound + 2));
        var denominator = Convert.ToDouble(properties.GetValue(lowerBound + 3));
        var actualScale = denominator == 0 ? 0 : numerator / denominator;
        if (!double.IsFinite(actualScale) || actualScale <= 0 || Math.Abs(actualScale - expectedScale) > 0.000001)
        {
            throw new InvalidOperationException(
                $"Expected sheet scale {expectedScale:0.###}, but SOLIDWORKS reports {actualScale:0.###}.");
        }
    }

    private static DrawingViewValidation ValidateDrawingView(
        IDrawingDoc drawing,
        DrawingPlan plan,
        double sheetWidthMm,
        double sheetHeightMm)
    {
        var sheetView = drawing.GetFirstView() as View
            ?? throw new InvalidOperationException("Could not access the drawing sheet view.");
        var modelViews = new List<View>();
        var currentView = sheetView.GetNextView() as View;
        while (currentView is not null)
        {
            modelViews.Add(currentView);
            currentView = currentView.GetNextView() as View;
        }

        var expectedViewCount = 1 + plan.RequestedSecondaryViews.Count;
        if (modelViews.Count != expectedViewCount)
        {
            throw new InvalidOperationException(
                $"Expected {expectedViewCount} planned model views; found {modelViews.Count}.");
        }

        var viewExtents = modelViews.Select(ReadViewExtents).ToArray();
        var primaryOrientation = NormalizeOrientation(plan.PrimaryView!.ModelViewName);
        var primaryIndex = Array.FindIndex(
            viewExtents,
            extent => string.Equals(extent.OrientationName, primaryOrientation, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex < 0)
        {
            throw new InvalidOperationException(
                $"The planned primary orientation '{primaryOrientation}' is missing from the drawing.");
        }

        foreach (var secondaryPlan in plan.RequestedSecondaryViews)
        {
            var secondaryOrientation = NormalizeOrientation(secondaryPlan.ModelViewName);
            var secondaryIndex = Array.FindIndex(
                viewExtents,
                extent => string.Equals(extent.OrientationName, secondaryOrientation, StringComparison.OrdinalIgnoreCase));
            if (secondaryIndex < 0 || secondaryIndex == primaryIndex)
            {
                throw new InvalidOperationException(
                    $"The planned {secondaryPlan.Purpose} orientation '{secondaryOrientation}' is missing.");
            }
        }

        var allViewsFit = viewExtents.All(extent =>
            extent.WidthMm > 0 && extent.HeightMm > 0 &&
            extent.XMinMm >= -0.5 && extent.YMinMm >= -0.5 &&
            extent.XMaxMm <= sheetWidthMm + 0.5 && extent.YMaxMm <= sheetHeightMm + 0.5);
        var noOverlap = true;
        for (var firstIndex = 0; firstIndex < viewExtents.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < viewExtents.Length; secondIndex++)
            {
                var overlapWidth = Math.Min(viewExtents[firstIndex].XMaxMm, viewExtents[secondIndex].XMaxMm) -
                                   Math.Max(viewExtents[firstIndex].XMinMm, viewExtents[secondIndex].XMinMm);
                var overlapHeight = Math.Min(viewExtents[firstIndex].YMaxMm, viewExtents[secondIndex].YMaxMm) -
                                    Math.Max(viewExtents[firstIndex].YMinMm, viewExtents[secondIndex].YMinMm);
                if (overlapWidth > 0.5 && overlapHeight > 0.5)
                {
                    noOverlap = false;
                }
            }
        }

        var primaryView = modelViews[primaryIndex];
        var primaryExtents = viewExtents[primaryIndex];
        var expectedFaceArea = plan.DominantPlanarFaceAreaMm2 *
                               primaryView.ScaleDecimal * primaryView.ScaleDecimal;
        var projectedAreaRatio = expectedFaceArea > 0
            ? primaryExtents.WidthMm * primaryExtents.HeightMm / expectedFaceArea
            : 0;
        var notEdgeOn = projectedAreaRatio >= 0.75;
        var scaleCorrect = viewExtents.All(extent => Math.Abs(extent.Scale - plan.TargetScale) <= 0.000001);

        if (!allViewsFit)
        {
            throw new InvalidOperationException("One or more planned views do not fit within the sheet boundary.");
        }

        if (!noOverlap)
        {
            throw new InvalidOperationException("The planned model views overlap.");
        }

        if (!notEdgeOn)
        {
            throw new InvalidOperationException($"The primary view appears edge-on; projected-area ratio {projectedAreaRatio:0.###}.");
        }

        if (!scaleCorrect)
        {
            throw new InvalidOperationException($"Not all model views retained scale {plan.TargetScale:0.###}.");
        }

        var thicknessViewExists = plan.RequestedSecondaryViews
            .Where(secondary => string.Equals(secondary.Purpose, "Thickness", StringComparison.OrdinalIgnoreCase))
            .All(secondary => viewExtents.Any(extent => string.Equals(
                extent.OrientationName,
                NormalizeOrientation(secondary.ModelViewName),
                StringComparison.OrdinalIgnoreCase)));
        var totalDimensionCount = modelViews.Sum(view => view.GetDimensionCount4());

        return new DrawingViewValidation(
            modelViews.Count,
            true,
            thicknessViewExists,
            noOverlap,
            allViewsFit,
            primaryExtents.WidthMm,
            primaryExtents.HeightMm,
            projectedAreaRatio,
            totalDimensionCount);
    }

    private static ViewExtents ReadViewExtents(View view)
    {
        var outline = view.GetOutline() as Array;
        if (outline is null || outline.Length < 4)
        {
            throw new InvalidOperationException("SOLIDWORKS did not return model-view extents.");
        }

        var lowerBound = outline.GetLowerBound(0);
        var xMin = Convert.ToDouble(outline.GetValue(lowerBound));
        var yMin = Convert.ToDouble(outline.GetValue(lowerBound + 1));
        var xMax = Convert.ToDouble(outline.GetValue(lowerBound + 2));
        var yMax = Convert.ToDouble(outline.GetValue(lowerBound + 3));
        return new ViewExtents(
            NormalizeOrientation(view.GetOrientationName()),
            Math.Min(xMin, xMax) * 1000.0,
            Math.Min(yMin, yMax) * 1000.0,
            Math.Max(xMin, xMax) * 1000.0,
            Math.Max(yMin, yMax) * 1000.0,
            Math.Abs(xMax - xMin) * 1000.0,
            Math.Abs(yMax - yMin) * 1000.0,
            view.ScaleDecimal);
    }

    private static string NormalizeOrientation(string orientationName) =>
        orientationName.Trim().TrimStart('*').Replace(" View", string.Empty, StringComparison.OrdinalIgnoreCase);

    private sealed record ViewExtents(
        string OrientationName,
        double XMinMm,
        double YMinMm,
        double XMaxMm,
        double YMaxMm,
        double WidthMm,
        double HeightMm,
        double Scale);

    private sealed record DrawingViewValidation(
        int ModelViewCount,
        bool PrimaryViewExists,
        bool ThicknessViewExists,
        bool NoOverlap,
        bool AllViewsFit,
        double PrimaryViewWidthMm,
        double PrimaryViewHeightMm,
        double ProjectedAreaRatio,
        int TotalDimensionCount);

    private static string GetFullPartPath(string partPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);

        var fullPartPath = Path.GetFullPath(partPath);
        if (!string.Equals(Path.GetExtension(fullPartPath), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The input file must have an .SLDPRT extension.", nameof(partPath));
        }

        return fullPartPath;
    }
}

public sealed record DrawingBuildResult(
    string OutputPath,
    bool DrawingOpened,
    string SelectedSheetFormat,
    string LoadedSheetFormat,
    int SheetFormatSketchSegmentCount,
    int SheetFormatReloadStatus,
    double SheetWidthMm,
    double SheetHeightMm,
    double TargetScale,
    int ModelViewCount,
    bool PrimaryViewExists,
    bool ThicknessViewExists,
    bool NoOverlap,
    bool AllViewsFit,
    double PrimaryViewWidthMm,
    double PrimaryViewHeightMm,
    double ProjectedAreaRatio,
    int SaveErrors,
    int SaveWarnings,
    int OpenErrors,
    int OpenWarnings,
    int ReopenedDimensionCount = 0,
    PropertyMappingAnalysis? PropertyMappings = null,
    DimensionExecutionReport? DimensionReport = null);