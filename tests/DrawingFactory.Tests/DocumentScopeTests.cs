using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class DocumentScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DrawingFactoryScopeTests_{Guid.NewGuid():N}");
    private readonly List<string> _closed = new();
    private readonly StringWriter _diagnostics = new();

    public DocumentScopeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OwnedDocumentsAreClosedInReverseOrderOnce()
    {
        var firstPath = CreateFile("first.SLDPRT");
        var secondPath = CreateFile("second.SLDDRW");
        var api = CreateApi((path, args) => CreateDocument(path), _ => null);
        var scope = new SolidWorksDocumentScope(api, _diagnostics);
        scope.Open(firstPath, swDocumentTypes_e.swDocPART, true, out _);
        scope.Open(secondPath, swDocumentTypes_e.swDocDRAWING, true, out _);
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(new[] { "second.SLDDRW", "first.SLDPRT" }, _closed);
        Assert.Throws<ObjectDisposedException>(() => scope.Open(firstPath, swDocumentTypes_e.swDocPART, true, out _));
    }

    [Fact]
    public void PreExistingDocumentsAreNotClosedEvenWithoutAlreadyOpenWarning()
    {
        var path = CreateFile("existing.SLDPRT");
        var document = CreateDocument(path);
        using (var scope = new SolidWorksDocumentScope(CreateApi((_, _) => document, _ => document), _diagnostics))
        {
            scope.Open(path, swDocumentTypes_e.swDocPART, true, out _);
        }
        Assert.Empty(_closed);
    }

    [Fact]
    public void AlreadyOpenWarningPreventsClosingUserDocument()
    {
        var path = CreateFile("existing.SLDPRT");
        var api = CreateApi((openedPath, args) =>
        {
            args[5] = (int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen;
            return CreateDocument(openedPath);
        }, _ => null);
        using (var scope = new SolidWorksDocumentScope(api, _diagnostics))
        {
            scope.Open(path, swDocumentTypes_e.swDocPART, true, out _);
        }
        Assert.Empty(_closed);
    }

    [Fact]
    public void DuplicatePathsReuseTrackedDocument()
    {
        var path = CreateFile("part.SLDPRT");
        var openCount = 0;
        var api = CreateApi((openedPath, _) => { openCount++; return CreateDocument(openedPath); }, _ => null);
        using (var scope = new SolidWorksDocumentScope(api, _diagnostics))
        {
            var first = scope.Open(path, swDocumentTypes_e.swDocPART, true, out _);
            var second = scope.Open(path, swDocumentTypes_e.swDocPART, true, out var warnings);
            Assert.Same(first, second);
            Assert.Equal(swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen, warnings);
        }
        Assert.Equal(1, openCount);
        Assert.Single(_closed);
    }

    [Fact]
    public void ReadOnlyPartTitleCollisionUsesAndRemovesUniqueScratchCopy()
    {
        var path = CreateFile("part.SLDPRT");
        string? scratch = null;
        var api = CreateApi((openedPath, args) =>
        {
            if (openedPath == path)
            {
                args[4] = (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen;
                return null;
            }
            scratch = openedPath;
            Assert.True(File.Exists(scratch));
            return CreateDocument(scratch);
        }, _ => null);
        using (var scope = new SolidWorksDocumentScope(api, _diagnostics))
        {
            scope.Open(path, swDocumentTypes_e.swDocPART, true, out _);
        }
        Assert.NotNull(scratch);
        Assert.False(File.Exists(scratch));
        Assert.True(File.Exists(path));
        Assert.Equal(Path.GetFileName(scratch), Assert.Single(_closed));
    }

    [Fact]
    public void FailedScratchOpenStillDeletesScratchCopy()
    {
        var path = CreateFile("part.SLDPRT");
        string? scratch = null;
        var api = CreateApi((openedPath, args) =>
        {
            if (openedPath == path)
            {
                args[4] = (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen;
                return null;
            }
            scratch = openedPath;
            throw new COMException("Simulated open failure");
        }, _ => null);
        using var scope = new SolidWorksDocumentScope(api, _diagnostics);
        Assert.Throws<COMException>(() => scope.Open(path, swDocumentTypes_e.swDocPART, true, out _));
        Assert.NotNull(scratch);
        Assert.False(File.Exists(scratch));
    }

    [Fact]
    public void CloseFailureDoesNotPreventOtherDocumentCleanup()
    {
        var first = CreateFile("first.SLDPRT");
        var second = CreateFile("second.SLDPRT");
        var api = CreateApi((path, _) => CreateDocument(path), _ => null, title =>
        {
            if (title == "second.SLDPRT") throw new COMException("Simulated close failure");
        });
        using (var scope = new SolidWorksDocumentScope(api, _diagnostics))
        {
            scope.Open(first, swDocumentTypes_e.swDocPART, true, out _);
            scope.Open(second, swDocumentTypes_e.swDocPART, true, out _);
        }
        Assert.Contains("first.SLDPRT", _closed);
        Assert.Contains("Simulated close failure", _diagnostics.ToString());
    }

    public void Dispose()
    {
        _diagnostics.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private ISldWorks CreateApi(Func<string, object?[], ModelDoc2?> open, Func<string, ModelDoc2?> existing, Action<string>? close = null) =>
        InteropProxy.Create<ISldWorks>((method, args) => method.Name switch
        {
            "GetOpenDocumentByName" => existing((string)args[0]!),
            "OpenDoc6" => open((string)args[0]!, args),
            "CloseDoc" => Close((string)args[0]!, close),
            _ => throw new NotSupportedException(method.Name)
        });

    private object? Close(string title, Action<string>? close)
    {
        _closed.Add(title);
        close?.Invoke(title);
        return null;
    }

    private static ModelDoc2 CreateDocument(string path) => InteropProxy.Create<ModelDoc2>((method, _) => method.Name switch
    {
        "GetPathName" => path,
        "GetTitle" => Path.GetFileName(path),
        "GetType" => (int)(Path.GetExtension(path) == ".SLDPRT" ? swDocumentTypes_e.swDocPART : swDocumentTypes_e.swDocDRAWING),
        _ => throw new NotSupportedException(method.Name)
    });

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "sample");
        return path;
    }

    public class InteropProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        public static T Create<T>(Func<MethodInfo, object?[], object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, InteropProxy>();
            ((InteropProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args!);
    }
}
