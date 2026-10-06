using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class PartAnalyzer
{
    private readonly ModelDoc2 _model;

    public PartAnalyzer(ModelDoc2 model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public PartAnalysis Analyze()
    {
        var path = _model.GetPathName();
        var fullPath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        var activeConfiguration = _model.ConfigurationManager.ActiveConfiguration;
        var geometry = AnalyzeGeometry();
        var configurationNames = GetConfigurationNames();
        var configurationProperties = new List<ConfigurationAnalysis>(configurationNames.Count);

        foreach (var configurationName in configurationNames)
        {
            var propertyManager = _model.Extension.CustomPropertyManager[configurationName];
            configurationProperties.Add(new ConfigurationAnalysis(
                configurationName,
                ReadProperties(propertyManager)));
        }

        return new PartAnalysis(
            _model.GetTitle(),
            fullPath,
            (swDocumentTypes_e)_model.GetType(),
            geometry,
            activeConfiguration?.Name ?? string.Empty,
            configurationNames,
            ReadProperties(_model.Extension.CustomPropertyManager[string.Empty]),
            configurationProperties);
    }

    private PartGeometryAnalysis AnalyzeGeometry()
    {
        if (_model is not IPartDoc partDocument)
        {
            throw new InvalidOperationException("Geometry analysis requires a SOLIDWORKS part document.");
        }

        var box = ToDoubleArray(partDocument.GetPartBox(true));
        if (box.Length < 6)
        {
            throw new InvalidOperationException("SOLIDWORKS did not return a complete part bounding box.");
        }

        const double metersToMillimeters = 1000.0;
        var boundingBoxXmm = Math.Abs(box[3] - box[0]) * metersToMillimeters;
        var boundingBoxYmm = Math.Abs(box[4] - box[1]) * metersToMillimeters;
        var boundingBoxZmm = Math.Abs(box[5] - box[2]) * metersToMillimeters;
        var planarFaces = new List<PlanarFaceAnalysis>();

        if (partDocument.GetBodies2((int)swBodyType_e.swSolidBody, false) is Array bodies)
        {
            foreach (var bodyItem in bodies)
            {
                if (bodyItem is not Body2 body || body.GetFaces() is not Array faces)
                {
                    continue;
                }

                foreach (var faceItem in faces)
                {
                    if (faceItem is not Face2 face || face.GetSurface() is not Surface surface || !surface.IsPlane())
                    {
                        continue;
                    }

                    var planeParameters = ToDoubleArray(surface.PlaneParams);
                    if (planeParameters.Length < 6)
                    {
                        continue;
                    }

                    var normal = new Vector3D(planeParameters[3], planeParameters[4], planeParameters[5]);
                    var normalLength = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
                    if (normalLength <= 0)
                    {
                        continue;
                    }

                    planarFaces.Add(new PlanarFaceAnalysis(
                        face.GetArea() * 1_000_000.0,
                        new Vector3D(normal.X / normalLength, normal.Y / normalLength, normal.Z / normalLength)));
                }
            }
        }

        return new PartGeometryAnalysis(
            boundingBoxXmm,
            boundingBoxYmm,
            boundingBoxZmm,
            planarFaces);
    }

    private static double[] ToDoubleArray(object? value)
    {
        if (value is not Array array)
        {
            return Array.Empty<double>();
        }

        var values = new double[array.Length];
        var index = 0;
        foreach (var item in array)
        {
            values[index++] = Convert.ToDouble(item, System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }

    private IReadOnlyList<string> GetConfigurationNames()
    {
        if (_model.GetConfigurationNames() is not Array names)
        {
            return Array.Empty<string>();
        }

        var configurationNames = new List<string>(names.Length);
        foreach (var item in names)
        {
            if (item is string name && !string.IsNullOrWhiteSpace(name))
            {
                configurationNames.Add(name);
            }
        }

        return configurationNames;
    }

    private static IReadOnlyList<CustomPropertyAnalysis> ReadProperties(CustomPropertyManager propertyManager)
    {
        if (propertyManager.GetNames() is not Array names)
        {
            return Array.Empty<CustomPropertyAnalysis>();
        }

        var properties = new List<CustomPropertyAnalysis>(names.Length);
        foreach (var item in names)
        {
            if (item is not string name || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            propertyManager.Get6(
                name,
                false,
                out var value,
                out var resolvedValue,
                out _,
                out _);
            properties.Add(new CustomPropertyAnalysis(name, value, resolvedValue));
        }

        return properties;
    }
}

public sealed record PartAnalysis(
    string Title,
    string FullPath,
    swDocumentTypes_e DocumentType,
    PartGeometryAnalysis Geometry,
    string ActiveConfiguration,
    IReadOnlyList<string> ConfigurationNames,
    IReadOnlyList<CustomPropertyAnalysis> DocumentProperties,
    IReadOnlyList<ConfigurationAnalysis> ConfigurationProperties);

public sealed record PartGeometryAnalysis(
    double BoundingBoxXmm,
    double BoundingBoxYmm,
    double BoundingBoxZmm,
    IReadOnlyList<PlanarFaceAnalysis> PlanarFaces);

public sealed record PlanarFaceAnalysis(double AreaMm2, Vector3D Normal);

public sealed record Vector3D(double X, double Y, double Z);

public sealed record ConfigurationAnalysis(
    string Name,
    IReadOnlyList<CustomPropertyAnalysis> CustomProperties);

public sealed record CustomPropertyAnalysis(
    string Name,
    string Value,
    string ResolvedValue);