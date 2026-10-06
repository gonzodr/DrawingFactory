using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class ManufacturingFeatureAnalyzer
{
    private const double PlanarFaceAlignment = 0.999;
    private const double CoordinateToleranceMm = 0.05;

    private readonly ModelDoc2 _model;

    public ManufacturingFeatureAnalyzer(ModelDoc2 model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public ManufacturingFeatureSet Analyze(PartGeometryAnalysis geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (_model is not IPartDoc partDocument)
        {
            throw new InvalidOperationException("Manufacturing feature analysis requires a SOLIDWORKS part.");
        }

        var dimensions = new[]
        {
            (Axis: "X", Value: geometry.BoundingBoxXmm),
            (Axis: "Y", Value: geometry.BoundingBoxYmm),
            (Axis: "Z", Value: geometry.BoundingBoxZmm)
        };
        var orderedDimensions = dimensions.OrderByDescending(dimension => dimension.Value).ToArray();
        var thicknessDimension = orderedDimensions[^1];
        var thicknessAxis = AxisVector(thicknessDimension.Axis);
        var planarBoundaries = new List<PlanarBoundaryEdge>();
        var circularBoundaries = new List<ManufacturingHole>();
        var cylindricalSurfaces = new List<CylindricalSurfaceAnalysis>();

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
                    if (faceItem is not Face2 face || face.GetSurface() is not Surface surface)
                    {
                        continue;
                    }

                    if (surface.IsCylinder())
                    {
                        var cylinder = ToDoubleArray(surface.CylinderParams);
                        if (cylinder.Length >= 7)
                        {
                            cylindricalSurfaces.Add(new CylindricalSurfaceAnalysis(
                                cylinder[6] * 2000.0,
                                Normalize(new Vector3D(cylinder[3], cylinder[4], cylinder[5])),
                                new Point3D(cylinder[0] * 1000.0, cylinder[1] * 1000.0, cylinder[2] * 1000.0),
                                face.GetArea() * 1_000_000.0));
                        }

                        continue;
                    }

                    if (!surface.IsPlane())
                    {
                        continue;
                    }

                    var plane = ToDoubleArray(surface.PlaneParams);
                    if (plane.Length < 6)
                    {
                        continue;
                    }

                    var normal = Normalize(new Vector3D(plane[3], plane[4], plane[5]));
                    if (Math.Abs(Dot(normal, thicknessAxis)) < PlanarFaceAlignment ||
                        face.GetArea() * 1_000_000.0 < orderedDimensions[0].Value * orderedDimensions[1].Value * 0.35)
                    {
                        continue;
                    }

                    if (face.GetLoops() is not Array loops)
                    {
                        continue;
                    }

                    foreach (var loopItem in loops)
                    {
                        if (loopItem is not Loop2 loop || loop.GetEdges() is not Array edges)
                        {
                            continue;
                        }

                        foreach (var edgeItem in edges)
                        {
                            if (edgeItem is not Edge edge || edge.GetCurve() is not Curve curve)
                            {
                                continue;
                            }

                            if (curve.IsCircle())
                            {
                                var circle = ToDoubleArray(curve.CircleParams);
                                if (circle.Length >= 7)
                                {
                                    var center = new Point3D(circle[0] * 1000.0, circle[1] * 1000.0, circle[2] * 1000.0);
                                    var axis = Normalize(new Vector3D(circle[3], circle[4], circle[5]));
                                    if (Math.Abs(Dot(axis, thicknessAxis)) >= PlanarFaceAlignment && !loop.IsOuter())
                                    {
                                        circularBoundaries.Add(new ManufacturingHole(
                                            circle[6] * 2000.0,
                                            center,
                                            axis,
                                            "Planar-face circular boundary"));
                                    }
                                    else if (loop.IsOuter() && HasDistinctEndpoints(edge))
                                    {
                                        planarBoundaries.Add(new PlanarBoundaryEdge(
                                            "Circular arc",
                                            ReadVertex(edge.GetStartVertex()),
                                            ReadVertex(edge.GetEndVertex()),
                                            new Point3D(circle[0] * 1000.0, circle[1] * 1000.0, circle[2] * 1000.0),
                                            circle[6] * 1000.0,
                                            true));
                                    }
                                }

                                continue;
                            }

                            var start = ReadVertex(edge.GetStartVertex());
                            var end = ReadVertex(edge.GetEndVertex());
                            if (start is not null && end is not null)
                            {
                                planarBoundaries.Add(new PlanarBoundaryEdge(
                                    curve.IsLine() ? "Line" : "Curve",
                                    start,
                                    end,
                                    null,
                                    null,
                                    loop.IsOuter()));
                            }
                        }
                    }
                }
            }
        }

        var holes = MergeHoleBoundaries(circularBoundaries, cylindricalSurfaces, thicknessAxis);
        var holeGroups = holes
            .GroupBy(hole => Math.Round(hole.DiameterMm, 2))
            .Select(group => new HoleGroup(
                group.Key,
                group.ToArray(),
                group.Count(),
                AreRepeated(group.ToArray())))
            .OrderBy(group => group.DiameterMm)
            .ToArray();
        var cornerRadii = planarBoundaries
            .Where(edge => edge.IsOuterBoundary && edge.RadiusMm.HasValue)
            .Select(edge => edge.RadiusMm!.Value)
            .DistinctBy(radius => Math.Round(radius, 2))
            .OrderBy(radius => radius)
            .ToArray();
        var threadedFeatures = AnalyzeHoleWizardFeatures();

        return new ManufacturingFeatureSet(
            geometry.BoundingBoxXmm,
            geometry.BoundingBoxYmm,
            geometry.BoundingBoxZmm,
            thicknessDimension.Value,
            thicknessDimension.Axis,
            planarBoundaries,
            holes,
            holeGroups,
            cornerRadii,
            threadedFeatures);
    }

    private IReadOnlyList<ManufacturingHole> MergeHoleBoundaries(
        IReadOnlyList<ManufacturingHole> boundaryCircles,
        IReadOnlyList<CylindricalSurfaceAnalysis> cylindricalSurfaces,
        Vector3D thicknessAxis)
    {
        var result = new List<ManufacturingHole>();
        foreach (var circle in boundaryCircles)
        {
            var matchedCylinder = cylindricalSurfaces
                .Where(cylinder => Math.Abs(Dot(cylinder.Axis, thicknessAxis)) >= PlanarFaceAlignment &&
                                   Math.Abs(cylinder.DiameterMm - circle.DiameterMm) <= CoordinateToleranceMm)
                .OrderBy(cylinder => Distance(cylinder.AxisPoint, circle.Center))
                .FirstOrDefault(cylinder => Distance(cylinder.AxisPoint, circle.Center) <= CoordinateToleranceMm);
            if (matchedCylinder is null)
            {
                continue;
            }

            if (result.Any(existing =>
                    Math.Abs(existing.DiameterMm - circle.DiameterMm) <= CoordinateToleranceMm &&
                    Distance(existing.Center, circle.Center) <= CoordinateToleranceMm))
            {
                continue;
            }

            result.Add(circle with { Source = "Planar inner loop matched to cylindrical face" });
        }

        return result;
    }

    private IReadOnlyList<ThreadedFeatureAnalysis> AnalyzeHoleWizardFeatures()
    {
        var results = new List<ThreadedFeatureAnalysis>();
        var feature = _model.FirstFeature() as Feature;
        while (feature is not null)
        {
            AnalyzeFeature(feature, results);
            var subFeature = feature.GetFirstSubFeature() as Feature;
            while (subFeature is not null)
            {
                AnalyzeFeature(subFeature, results);
                subFeature = subFeature.GetNextSubFeature() as Feature;
            }

            feature = feature.GetNextFeature() as Feature;
        }

        return results;
    }

    private void AnalyzeFeature(Feature feature, ICollection<ThreadedFeatureAnalysis> results)
    {
        if (!feature.GetTypeName2().Contains("HoleWzd", StringComparison.OrdinalIgnoreCase) ||
            feature.GetDefinition() is not WizardHoleFeatureData2 wizardFeature)
        {
            return;
        }

        var selectionAccess = false;
        try
        {
            selectionAccess = wizardFeature.AccessSelections(_model, null);
            var wizardTypeName = Enum.GetName(typeof(swWzdHoleTypes_e), wizardFeature.Type) ?? string.Empty;
            if (!wizardTypeName.StartsWith("swTap", StringComparison.OrdinalIgnoreCase) &&
                !wizardTypeName.StartsWith("swPipeTap", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var fastenerType = wizardFeature.FastenerType ?? string.Empty;
            var fastenerSize = wizardFeature.FastenerSize ?? string.Empty;
            var threadClass = wizardFeature.ThreadClass ?? string.Empty;
            var hasExplicitThreadMetadata =
                !string.IsNullOrWhiteSpace(fastenerSize) ||
                !string.IsNullOrWhiteSpace(threadClass);

            if (!hasExplicitThreadMetadata)
            {
                return;
            }

            results.Add(new ThreadedFeatureAnalysis(
                feature.Name,
                wizardTypeName,
                fastenerType,
                fastenerSize,
                threadClass,
                null,
                null));
        }
        catch (COMException)
        {
        }
        finally
        {
            if (selectionAccess)
            {
                wizardFeature.ReleaseSelectionAccess();
            }
        }
    }

    private static bool AreRepeated(IReadOnlyList<ManufacturingHole> holes)
    {
        if (holes.Count < 2)
        {
            return false;
        }

        var pairDistances = new List<double>();
        for (var first = 0; first < holes.Count; first++)
        {
            for (var second = first + 1; second < holes.Count; second++)
            {
                pairDistances.Add(Distance(holes[first].Center, holes[second].Center));
            }
        }

        return pairDistances.Count > 0 && pairDistances.All(distance => distance > CoordinateToleranceMm);
    }

    private static bool HasDistinctEndpoints(Edge edge)
    {
        var start = edge.GetStartVertex() as Vertex;
        var end = edge.GetEndVertex() as Vertex;
        return start is not null && end is not null && !ReferenceEquals(start, end);
    }

    private static Point3D? ReadVertex(object? value)
    {
        if (value is not Vertex vertex || vertex.GetPoint() is not Array point || point.Length < 3)
        {
            return null;
        }

        var lowerBound = point.GetLowerBound(0);
        return new Point3D(
            Convert.ToDouble(point.GetValue(lowerBound)) * 1000.0,
            Convert.ToDouble(point.GetValue(lowerBound + 1)) * 1000.0,
            Convert.ToDouble(point.GetValue(lowerBound + 2)) * 1000.0);
    }

    private static double[] ToDoubleArray(object? value)
    {
        if (value is not Array array)
        {
            return Array.Empty<double>();
        }

        var result = new double[array.Length];
        var lowerBound = array.GetLowerBound(0);
        for (var index = 0; index < array.Length; index++)
        {
            result[index] = Convert.ToDouble(array.GetValue(lowerBound + index));
        }

        return result;
    }

    private static Vector3D AxisVector(string axis) => axis switch
    {
        "X" => new Vector3D(1, 0, 0),
        "Y" => new Vector3D(0, 1, 0),
        _ => new Vector3D(0, 0, 1)
    };

    private static Vector3D Normalize(Vector3D vector)
    {
        var magnitude = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        return magnitude <= 0
            ? new Vector3D(0, 0, 0)
            : new Vector3D(vector.X / magnitude, vector.Y / magnitude, vector.Z / magnitude);
    }

    private static double Dot(Vector3D left, Vector3D right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static double Distance(Point3D left, Point3D right)
    {
        var x = left.Xmm - right.Xmm;
        var y = left.Ymm - right.Ymm;
        var z = left.Zmm - right.Zmm;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}

public sealed record ManufacturingFeatureSet(
    double OverallXmm,
    double OverallYmm,
    double OverallZmm,
    double ThicknessMm,
    string ThicknessAxis,
    IReadOnlyList<PlanarBoundaryEdge> PlanarBoundaries,
    IReadOnlyList<ManufacturingHole> Holes,
    IReadOnlyList<HoleGroup> HoleGroups,
    IReadOnlyList<double> CornerRadiiMm,
    IReadOnlyList<ThreadedFeatureAnalysis> ThreadedFeatures);

public sealed record PlanarBoundaryEdge(
    string GeometryType,
    Point3D? Start,
    Point3D? End,
    Point3D? Center,
    double? RadiusMm,
    bool IsOuterBoundary);

public sealed record ManufacturingHole(
    double DiameterMm,
    Point3D Center,
    Vector3D Axis,
    string Source);

public sealed record HoleGroup(
    double DiameterMm,
    IReadOnlyList<ManufacturingHole> Holes,
    int Count,
    bool IsRepeatedPattern);

public sealed record ThreadedFeatureAnalysis(
    string FeatureName,
    string WizardTypeName,
    string FastenerType,
    string FastenerSize,
    string ThreadClass,
    double? ThreadDiameterMm,
    double? ThreadDepthMm);

internal sealed record CylindricalSurfaceAnalysis(
    double DiameterMm,
    Vector3D Axis,
    Point3D AxisPoint,
    double AreaMm2);

public sealed record Point3D(double Xmm, double Ymm, double Zmm);
