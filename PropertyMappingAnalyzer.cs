using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class PropertyMappingAnalyzer
{
    private static readonly IReadOnlyDictionary<string, string> SheetNotePropertySources =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alkatrész szám"] = "RajzszámSA",
            ["Projekt név"] = "Project",
            ["Projekt"] = "Project",
            ["Gyártó"] = "GyartoSA"
        };

    private static readonly IReadOnlyDictionary<string, string> DrawingPropertySources =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Revision"] = "Revízió"
        };

    private static readonly Regex PropertyLinkPattern = new(
        "\\$(?<scope>PRPSHEET|PRP):\"(?<name>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<PropertyMapping> ApplyExplicitDrawingPropertyMappings(
        ModelDoc2 drawing,
        PartAnalysis partAnalysis)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(partAnalysis);

        var documentProperties = partAnalysis.DocumentProperties
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var drawingPropertyManager = drawing.Extension.CustomPropertyManager[string.Empty];
        var propertyNames = drawingPropertyManager.GetNames() as Array ?? Array.Empty<string>();
        var existingNames = propertyNames.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appliedMappings = new List<PropertyMapping>();

        foreach (var mapping in DrawingPropertySources)
        {
            if (!documentProperties.TryGetValue(mapping.Value, out var sourceProperty))
            {
                continue;
            }

            var sourceValue = string.IsNullOrEmpty(sourceProperty.ResolvedValue)
                ? sourceProperty.Value
                : sourceProperty.ResolvedValue;
            if (string.IsNullOrEmpty(sourceValue))
            {
                appliedMappings.Add(new PropertyMapping(
                    mapping.Key, $"$PRP:\"{mapping.Key}\"", "PRP", mapping.Key,
                    $"Document:{mapping.Value}", sourceValue, sourceProperty.Value, string.Empty,
                    "Source model property is empty; drawing property was left unchanged."));
                continue;
            }

            string existingValue = string.Empty;
            string existingResolvedValue = string.Empty;
            if (existingNames.Contains(mapping.Key))
            {
                drawingPropertyManager.Get6(
                    mapping.Key,
                    false,
                    out existingValue,
                    out existingResolvedValue,
                    out _,
                    out _);
                var currentValue = string.IsNullOrEmpty(existingResolvedValue) ? existingValue : existingResolvedValue;
                if (!string.IsNullOrWhiteSpace(currentValue) &&
                    !string.Equals(currentValue, sourceValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Drawing property '{mapping.Key}' already contains '{currentValue}', which conflicts with model property '{mapping.Value}' value '{sourceValue}'.");
                }

                if (string.IsNullOrWhiteSpace(currentValue))
                {
                    var setResult = drawingPropertyManager.Set2(mapping.Key, sourceValue);
                    if (setResult != (int)swCustomInfoSetResult_e.swCustomInfoSetResult_OK)
                    {
                        throw new InvalidOperationException(
                            $"Could not map model property '{mapping.Value}' to drawing property '{mapping.Key}': {setResult}.");
                    }
                }
            }
            else
            {
                var addResult = drawingPropertyManager.Add3(
                    mapping.Key,
                    (int)swCustomInfoType_e.swCustomInfoText,
                    sourceValue,
                    (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
                if (addResult != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged)
                {
                    throw new InvalidOperationException(
                        $"Could not add mapped drawing property '{mapping.Key}' from model property '{mapping.Value}': {addResult}.");
                }
            }

            appliedMappings.Add(new PropertyMapping(
                mapping.Key, $"$PRP:\"{mapping.Key}\"", "PRP", mapping.Key,
                $"Document:{mapping.Value}", sourceValue, sourceProperty.Value, sourceValue,
                "Explicitly mapped from the exact nonempty model property."));
        }

        return appliedMappings;
    }

    public IReadOnlyList<PropertyMapping> ApplySheetNotePropertyMappings(
        View sheetView,
        PartAnalysis partAnalysis)
    {
        ArgumentNullException.ThrowIfNull(sheetView);
        ArgumentNullException.ThrowIfNull(partAnalysis);

        if (sheetView.GetNotes() is not Array notes)
        {
            return Array.Empty<PropertyMapping>();
        }

        var documentProperties = partAnalysis.DocumentProperties
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var mappings = new List<PropertyMapping>();
        foreach (var noteItem in notes)
        {
            if (noteItem is not Note note)
            {
                continue;
            }

            var linkedText = note.PropertyLinkedText ?? string.Empty;
            foreach (var alias in SheetNotePropertySources.OrderByDescending(item => item.Key.Length))
            {
                var aliasToken = $"$PRPSHEET:\"{alias.Key}\"";
                var aliasPattern = new Regex(
                    Regex.Escape(aliasToken),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!aliasPattern.IsMatch(linkedText))
                {
                    continue;
                }

                if (!documentProperties.TryGetValue(alias.Value, out var sourceProperty))
                {
                    mappings.Add(new PropertyMapping(
                        note.GetName(), linkedText, "PRPSHEET", alias.Key, "Missing", null, null,
                        note.GetText() ?? string.Empty, "Configured source property was not found; existing note link was preserved."));
                    continue;
                }

                var sourceValue = string.IsNullOrEmpty(sourceProperty.ResolvedValue)
                    ? sourceProperty.Value
                    : sourceProperty.ResolvedValue;
                if (string.IsNullOrEmpty(sourceValue))
                {
                    mappings.Add(new PropertyMapping(
                        note.GetName(), linkedText, "PRPSHEET", alias.Key,
                        $"Document:{alias.Value}", sourceValue, sourceProperty.Value,
                        note.GetText() ?? string.Empty,
                        "Source model property is empty; existing note link was left unchanged."));
                    continue;
                }

                var sourceToken = $"$PRPSHEET:\"{alias.Value}\"";
                var updatedLinkedText = Regex.IsMatch(
                    linkedText,
                    Regex.Escape(sourceToken),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    ? aliasPattern.Replace(linkedText, string.Empty, 1)
                    : aliasPattern.Replace(linkedText, sourceToken, 1);
                if (!string.Equals(updatedLinkedText, linkedText, StringComparison.Ordinal))
                {
                    note.PropertyLinkedText = updatedLinkedText;
                    linkedText = updatedLinkedText;
                }

                mappings.Add(new PropertyMapping(
                    note.GetName(), linkedText, "PRPSHEET", alias.Value,
                    $"Document:{alias.Value}", sourceValue, sourceProperty.Value,
                    note.GetText() ?? string.Empty,
                    "Title-block link mapped to the exact nonempty model property."));
            }
        }

        return mappings;
    }

    public PropertyMappingAnalysis Analyze(View sheetView, PartAnalysis partAnalysis, ModelDoc2? drawing = null)
    {
        ArgumentNullException.ThrowIfNull(sheetView);
        ArgumentNullException.ThrowIfNull(partAnalysis);

        if (sheetView.GetNotes() is not Array notes)
        {
            return new PropertyMappingAnalysis(Array.Empty<PropertyMapping>(), 0);
        }

        var documentProperties = partAnalysis.DocumentProperties
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var activeConfiguration = partAnalysis.ConfigurationProperties
            .FirstOrDefault(configuration => string.Equals(
                configuration.Name,
                partAnalysis.ActiveConfiguration,
                StringComparison.OrdinalIgnoreCase));
        var configurationProperties = activeConfiguration?.CustomProperties
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CustomPropertyAnalysis>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<PropertyMapping>();

        foreach (var noteItem in notes)
        {
            if (noteItem is not Note note)
            {
                continue;
            }

            var linkedText = note.PropertyLinkedText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(linkedText))
            {
                continue;
            }

            var resolvedText = note.GetText() ?? string.Empty;
            foreach (Match match in PropertyLinkPattern.Matches(linkedText))
            {
                var scope = match.Groups["scope"].Value.ToUpperInvariant();
                var propertyName = match.Groups["name"].Value;
                if (scope != "PRPSHEET")
                {
                    if (scope == "PRP" && DrawingPropertySources.TryGetValue(propertyName, out var sourceName) &&
                        documentProperties.TryGetValue(sourceName, out var mappedSource))
                    {
                        var expectedValue = string.IsNullOrEmpty(mappedSource.ResolvedValue)
                            ? mappedSource.Value
                            : mappedSource.ResolvedValue;
                        var mappedStatus = string.IsNullOrEmpty(expectedValue)
                            ? "Source model property is empty; no value was invented."
                            : resolvedText.Contains(expectedValue, StringComparison.Ordinal)
                                ? "Resolved from the explicitly mapped model property."
                                : "Mapped model property value is not present in the resolved note.";
                        mappings.Add(new PropertyMapping(
                            note.GetName(), linkedText, scope, propertyName,
                            $"Document:{sourceName}", expectedValue, mappedSource.Value,
                            resolvedText, mappedStatus));
                        continue;
                    }

                    mappings.Add(new PropertyMapping(
                        note.GetName(), linkedText, scope, propertyName, "Drawing", null, null,
                        resolvedText, "Drawing-level link preserved; not treated as a model property."));
                    continue;
                }

                var sourceScope = "";
                CustomPropertyAnalysis? sourceProperty = null;
                if (configurationProperties.TryGetValue(propertyName, out var configurationProperty))
                {
                    sourceScope = $"Configuration:{partAnalysis.ActiveConfiguration}";
                    sourceProperty = configurationProperty;
                }
                else if (documentProperties.TryGetValue(propertyName, out var documentProperty))
                {
                    sourceScope = "Document";
                    sourceProperty = documentProperty;
                }
                else if (SheetNotePropertySources.TryGetValue(propertyName, out var aliasSource) &&
                         documentProperties.TryGetValue(aliasSource, out var aliasedDocumentProperty))
                {
                    sourceScope = $"Document:{aliasSource}";
                    sourceProperty = aliasedDocumentProperty;
                }

                if (sourceProperty is null)
                {
                    mappings.Add(new PropertyMapping(
                        note.GetName(), linkedText, scope, propertyName, "Missing", null, null,
                        resolvedText, "No exact matching model property was found; existing link was preserved."));
                    continue;
                }

                var sourceValue = string.IsNullOrEmpty(sourceProperty.ResolvedValue)
                    ? sourceProperty.Value
                    : sourceProperty.ResolvedValue;
                var status = string.IsNullOrEmpty(sourceValue)
                    ? "Source property is empty; existing link was preserved without substitution."
                    : resolvedText.Contains(sourceValue, StringComparison.Ordinal)
                        ? "Resolved from model property."
                        : "Expected model value is not present in the resolved note text.";

                mappings.Add(new PropertyMapping(
                    note.GetName(), linkedText, scope, propertyName, sourceScope, sourceValue,
                    sourceProperty.Value, resolvedText, status));
            }
        }

        var expectedMappings = mappings.Count(mapping =>
            mapping.Scope == "PRPSHEET" && !string.IsNullOrEmpty(mapping.ExpectedValue));
        return new PropertyMappingAnalysis(mappings, expectedMappings);
    }
}

public sealed record PropertyMapping(
    string NoteName,
    string LinkExpression,
    string Scope,
    string PropertyName,
    string SourceScope,
    string? ExpectedValue,
    string? StoredValue,
    string ResolvedNoteText,
    string Status);

public sealed record PropertyMappingAnalysis(
    IReadOnlyList<PropertyMapping> Mappings,
    int ExpectedModelValueCount)
{
    public bool AllExpectedValuesResolve => Mappings
        .Where(mapping => mapping.Scope is "PRPSHEET" or "PRP" && !string.IsNullOrEmpty(mapping.ExpectedValue))
        .All(mapping => mapping.ResolvedNoteText.Contains(mapping.ExpectedValue!, StringComparison.Ordinal));
}
