using SolidWorks.Interop.swconst;

public sealed class ConsoleReporter
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public ConsoleReporter(TextWriter output, TextWriter error)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public void PrintCatalog(SheetFormatCatalog sheetFormatCatalog)
    {
	_output.WriteLine($"Sheet-format catalog: {sheetFormatCatalog.DirectoryPath}");
	foreach (var format in sheetFormatCatalog.Formats)
	{
		_output.WriteLine($"  {format.FileName}: {format.Recommendation?.ToString() ?? "unmapped"}");
	}

    }

    public void PrintPart(string revision, PartAnalysis analysis, ManufacturingFeatureSet manufacturingFeatures, swFileLoadWarning_e warnings)
    {
	_output.WriteLine($"SOLIDWORKS revision: {revision}");
	_output.WriteLine("Part opened successfully.");
	_output.WriteLine("Part analysis:");
	_output.WriteLine($"  Title: {analysis.Title}");
	_output.WriteLine($"  Full path: {analysis.FullPath}");
	_output.WriteLine($"  Document type: {analysis.DocumentType}");
	_output.WriteLine($"  Active configuration: {analysis.ActiveConfiguration}");
	_output.WriteLine("  Configurations:");
	foreach (var configurationName in analysis.ConfigurationNames)
	{
		_output.WriteLine($"    {configurationName}");
	}

	PrintProperties("Document custom properties", analysis.DocumentProperties);
	foreach (var configuration in analysis.ConfigurationProperties)
	{
		PrintProperties($"Configuration '{configuration.Name}' custom properties", configuration.CustomProperties);
	}

	if ((int)warnings != 0)
	{
		_error.WriteLine($"SOLIDWORKS reported load warnings: {warnings} ({(int)warnings}).");
	}

	_output.WriteLine("Manufacturing features:");
	_output.WriteLine($"  Overall extents: {manufacturingFeatures.OverallXmm:0.###} x {manufacturingFeatures.OverallYmm:0.###} x {manufacturingFeatures.OverallZmm:0.###} mm");
	_output.WriteLine($"  Thickness: {manufacturingFeatures.ThicknessMm:0.###} mm on {manufacturingFeatures.ThicknessAxis}");
	_output.WriteLine($"  Planar boundary edges: {manufacturingFeatures.PlanarBoundaries.Count}");
	_output.WriteLine($"  Cylindrical holes detected: {manufacturingFeatures.Holes.Count}");
	foreach (var group in manufacturingFeatures.HoleGroups)
	{
		_output.WriteLine($"    Diameter {group.DiameterMm:0.###} mm: {group.Count} hole(s); repeated group={group.IsRepeatedPattern}");
	}
	_output.WriteLine($"  Corner radii: {(manufacturingFeatures.CornerRadiiMm.Count == 0 ? "none detected" : string.Join(", ", manufacturingFeatures.CornerRadiiMm.Select(radius => $"{radius:0.###} mm")))}");
	_output.WriteLine($"  Explicit threaded features: {manufacturingFeatures.ThreadedFeatures.Count}");
	foreach (var thread in manufacturingFeatures.ThreadedFeatures)
	{
		_output.WriteLine($"    {thread.FeatureName}: {thread.FastenerType} {thread.FastenerSize} class={thread.ThreadClass}; diameter={thread.ThreadDiameterMm?.ToString("0.###") ?? "unknown"} mm");
	}

    }

    public void PrintStrategy(DrawingPlan plan)
    {
        _output.WriteLine($"Strategy: {plan.StrategyName}");
        foreach (var diagnostic in plan.Diagnostics)
        {
            _output.WriteLine(diagnostic);
        }
    }

    public void PrintDimensionPlan(DrawingPlan plan, DimensionPlan dimensionPlan, DimensionLayoutPlan layoutPlan)
    {
	_output.WriteLine("DimensionPlan:");
	foreach (var requirement in dimensionPlan.Requirements)
	{
		var value = requirement.ValueMm is double millimeters ? $"{millimeters:0.###} mm" : "value from explicit metadata";
		_output.WriteLine($"  {requirement.SemanticId}: {requirement.Kind} = {value}; count={requirement.RelatedFeatureCount}; view={requirement.ViewPurpose}");
		if (requirement.UnresolvedReason is not null)
		{
			_output.WriteLine($"    Unresolved: {requirement.UnresolvedReason}");
		}
	}
	foreach (var omission in dimensionPlan.IntentionalOmissions)
	{
		_output.WriteLine($"  Omitted: {omission}");
	}
	_output.WriteLine($"Dimension layout reserves top/left/right/bottom: {layoutPlan.ReservedTopMm:0.#}/{layoutPlan.ReservedLeftMm:0.#}/{layoutPlan.ReservedRightMm:0.#}/{layoutPlan.ReservedBottomMm:0.#} mm");
	_output.WriteLine($"Final logical sheet: {plan.SheetRecommendation}; scale 1:{1.0 / plan.TargetScale:0.###}");

    }

    public void PrintBuildResult(DrawingBuildResult drawingResult)
    {
	_output.WriteLine($"Drawing opened successfully: {drawingResult.DrawingOpened}");
	_output.WriteLine($"Loaded sheet format: {drawingResult.LoadedSheetFormat}");
	_output.WriteLine($"Sheet-format reload status: {drawingResult.SheetFormatReloadStatus}");
	_output.WriteLine($"Title-block/border sketch segments: {drawingResult.SheetFormatSketchSegmentCount}");
	_output.WriteLine($"Sheet dimensions: {drawingResult.SheetWidthMm:0.##} x {drawingResult.SheetHeightMm:0.##} mm");
	_output.WriteLine($"Drawing scale: 1:{1.0 / drawingResult.TargetScale:0.###}");
	_output.WriteLine($"Primary model view exists: {drawingResult.PrimaryViewExists}");
	_output.WriteLine($"Thickness view exists: {drawingResult.ThicknessViewExists}");
	_output.WriteLine($"Model views created: {drawingResult.ModelViewCount}");
	_output.WriteLine($"Views overlap: {!drawingResult.NoOverlap}");
	_output.WriteLine($"Both views fit: {drawingResult.AllViewsFit}");
	_output.WriteLine($"Primary view extents: {drawingResult.PrimaryViewWidthMm:0.##} x {drawingResult.PrimaryViewHeightMm:0.##} mm");
	_output.WriteLine($"Projected-to-face area ratio: {drawingResult.ProjectedAreaRatio:0.###}");
	_output.WriteLine($"Drawing dimensions after reopen: {drawingResult.ReopenedDimensionCount}");
	_output.WriteLine($"Dimension semantic IDs unique: {drawingResult.DimensionReport?.SemanticIdsUnique}");
	foreach (var createdDimension in drawingResult.DimensionReport?.Created ?? Array.Empty<CreatedDimension>())
	{
		_output.WriteLine($"  Created {createdDimension.SemanticId}: {createdDimension.MeasuredValueMm:0.###} mm in {createdDimension.ViewName}");
	}
	foreach (var unresolved in drawingResult.DimensionReport?.Unresolved ?? Array.Empty<UnresolvedDimension>())
	{
		_output.WriteLine($"  Not created {unresolved.SemanticId}: {unresolved.Reason}");
	}
	_output.WriteLine("Title-block property mappings:");
	foreach (var mapping in drawingResult.PropertyMappings?.Mappings ?? Array.Empty<PropertyMapping>())
	{
		_output.WriteLine($"  {mapping.NoteName}: {mapping.LinkExpression} -> {mapping.SourceScope}.{mapping.PropertyName}; expected='{mapping.ExpectedValue ?? ""}', note='{mapping.ResolvedNoteText}', status={mapping.Status}");
	}
	_output.WriteLine($"Save result: errors={drawingResult.SaveErrors}, warnings={drawingResult.SaveWarnings}");
	_output.WriteLine($"Reopen result: errors={drawingResult.OpenErrors}, warnings={drawingResult.OpenWarnings}");
	_output.WriteLine($"Drawing saved: {drawingResult.OutputPath}");
	if (drawingResult.SaveWarnings != 0)
	{
		_error.WriteLine($"SOLIDWORKS reported drawing save warnings: {drawingResult.SaveWarnings}.");
	}

	if (drawingResult.OpenWarnings != 0)
	{
		_error.WriteLine($"SOLIDWORKS reported drawing open warnings: {drawingResult.OpenWarnings}.");
	}

    }

    public void PrintRegression(RegressionRunResult result)
    {
        _output.WriteLine($"Training samples: {result.TrainingSamplesRoot}");
        _output.WriteLine($"Generated drawings: {result.GeneratedRoot}");
        _output.WriteLine($"Complete comparisons: {result.Comparisons.Count}");
        foreach (var comparison in result.Comparisons)
        {
            PrintComparison(comparison);
        }

        foreach (var skipped in result.SkippedSamples)
        {
            _output.WriteLine($"Skipped: {skipped}");
        }

        PrintStyleProfile(result.StyleProfile);
        if (result.Comparisons.Count == 0)
        {
            _error.WriteLine("Regression failed: no complete comparisons were performed.");
        }
    }

    public void PrintProperties(string heading, IReadOnlyList<CustomPropertyAnalysis> properties)
    {
    	_output.WriteLine($"  {heading}:");
    	if (properties.Count == 0)
    	{
    		_output.WriteLine("    (none)");
    		return;
    	}

    	foreach (var property in properties)
    	{
    		_output.WriteLine($"    {property.Name}: {property.Value}");
    		if (!string.IsNullOrEmpty(property.ResolvedValue) && property.ResolvedValue != property.Value)
    		{
    			_output.WriteLine($"      Resolved: {property.ResolvedValue}");
    		}
    	}
    }

    public void PrintComparison(DrawingComparisonResult comparison)
    {
    	_output.WriteLine($"Comparison: {comparison.SampleName} ({(comparison.HasDifferences ? "DIFFERENCES" : "MATCH")})");
    	_output.WriteLine($"  Reference: {comparison.ReferencePath}");
    	_output.WriteLine($"  Generated: {comparison.GeneratedPath}");
    	PrintIssues("Missing views", comparison.MissingViews);
    	PrintIssues("Unnecessary views", comparison.UnnecessaryViews);
    	PrintIssues("Wrong orientations", comparison.WrongOrientations);
    	PrintIssues("Sheet differences", comparison.SheetDifferences);
    	PrintIssues("Scale differences", comparison.ScaleDifferences);
    	PrintIssues("Missing dimensions", comparison.MissingDimensions);
    	PrintIssues("Unnecessary dimensions", comparison.UnnecessaryDimensions);
    	PrintIssues("Redundant dimensions", comparison.RedundantDimensions);
    	PrintIssues("Dimension placement differences", comparison.DimensionPlacementDifferences);
    	PrintIssues("Missing native annotations", comparison.MissingNativeAnnotations);
    	PrintIssues("Unnecessary native annotations", comparison.UnnecessaryNativeAnnotations);
    	PrintIssues("Title-block/property differences", comparison.TitleBlockDifferences);
    	PrintIssues("Unresolved manufacturing information", comparison.UnresolvedManufacturingInformation);
    }

    public void PrintIssues(string heading, IReadOnlyList<string> issues)
    {
    	_output.WriteLine($"  {heading}: {issues.Count}");
    	foreach (var issue in issues)
    	{
    		_output.WriteLine($"    - {issue}");
    	}
    }

    public void PrintStyleProfile(DrawingStyleProfile profile)
    {
    	_output.WriteLine($"Style profile from {profile.ReferenceSampleCount} reference sample(s):");
    	PrintFrequencies("Sheets", profile.SheetFormatFrequencies);
    	PrintFrequencies("Sheet sizes", profile.SheetSizeFrequencies);
    	PrintFrequencies("Scales", profile.ScaleFrequencies);
    	PrintFrequencies("View types/orientations", profile.ViewTypeOrientationFrequencies);
    	PrintFrequencies("Dimension types", profile.DimensionTypeFrequencies);
    	PrintFrequencies("Native annotations", profile.NativeAnnotationFrequencies);
    	_output.WriteLine($"  Center-mark policy rate: {profile.CenterMarkPolicyRate:P0}");
    	_output.WriteLine($"  Centerline policy rate: {profile.CenterlinePolicyRate:P0}");
    	_output.WriteLine($"  Hole-callout policy rate: {profile.HoleCalloutPolicyRate:P0}");
    	_output.WriteLine($"  Average normalized dimension position: ({profile.AverageNormalizedDimensionX:0.###}, {profile.AverageNormalizedDimensionY:0.###})");
    }

    public void PrintFrequencies(string heading, IReadOnlyDictionary<string, int> frequencies)
    {
    	_output.WriteLine($"  {heading}:");
    	foreach (var item in frequencies)
    	{
    		_output.WriteLine($"    {item.Key}: {item.Value}");
    	}
    }
}
