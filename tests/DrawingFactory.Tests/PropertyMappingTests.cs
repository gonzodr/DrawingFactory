using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class PropertyMappingTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void BlankTemplateRevisionIsReplacedFromModel(string revision)
    {
        string? assigned = null;
        var drawing = Drawing(revision, value => assigned = value);
        var mappings = new PropertyMappingAnalyzer().ApplyExplicitDrawingPropertyMappings(drawing, Analysis());
        Assert.Equal("A", assigned);
        Assert.Single(mappings);
    }

    [Fact]
    public void ConflictingNonBlankRevisionIsStillRejected()
    {
        var drawing = Drawing("B", _ => throw new InvalidOperationException("Must not overwrite real revision"));
        Assert.Throws<InvalidOperationException>(() => new PropertyMappingAnalyzer().ApplyExplicitDrawingPropertyMappings(drawing, Analysis()));
    }

    [Fact]
    public void MatchingRevisionIsPreserved()
    {
        var drawing = Drawing("A", _ => throw new InvalidOperationException("Must not rewrite matching revision"));
        Assert.Single(new PropertyMappingAnalyzer().ApplyExplicitDrawingPropertyMappings(drawing, Analysis()));
    }

    private static ModelDoc2 Drawing(string revision, Action<string> assign)
    {
        var manager = DocumentScopeTests.InteropProxy.Create<CustomPropertyManager>((method, args) =>
        {
            if (method.Name == "GetNames") return new[] { "Revision" };
            if (method.Name == "Get6")
            {
                args[2] = revision;
                args[3] = revision;
                args[4] = true;
                args[5] = false;
                return 0;
            }
            if (method.Name == "Set2")
            {
                assign((string)args[1]!);
                return (int)swCustomInfoSetResult_e.swCustomInfoSetResult_OK;
            }
            throw new NotSupportedException(method.Name);
        });
        var extension = DocumentScopeTests.InteropProxy.Create<ModelDocExtension>((method, _) =>
            method.Name == "get_CustomPropertyManager" ? manager : throw new NotSupportedException(method.Name));
        return DocumentScopeTests.InteropProxy.Create<ModelDoc2>((method, _) =>
            method.Name == "get_Extension" ? extension : throw new NotSupportedException(method.Name));
    }

    private static PartAnalysis Analysis() => new("part", "part.SLDPRT", swDocumentTypes_e.swDocPART,
        new PartGeometryAnalysis(45, 30, 45, Array.Empty<PlanarFaceAnalysis>()), "Default", new[] { "Default" },
        new[] { new CustomPropertyAnalysis("Revízió", "A", "A") }, Array.Empty<ConfigurationAnalysis>());
}
