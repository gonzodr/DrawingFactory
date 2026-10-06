using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed class SolidWorksDocumentScope : IDisposable
{
    private readonly ISldWorks _application;
    private readonly TextWriter _diagnostics;
    private readonly List<OpenedDocument> _documents = new();
    private bool _disposed;

    public SolidWorksDocumentScope(ISldWorks application, TextWriter diagnostics)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public ModelDoc2 Open(string path, swDocumentTypes_e type, bool readOnly, out swFileLoadWarning_e loadWarnings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var extension = type == swDocumentTypes_e.swDocPART ? ".SLDPRT" : ".SLDDRW";
        var fullPath = InputFiles.RequireFile(path, extension, "SOLIDWORKS document");
        var alreadyTracked = _documents.FirstOrDefault(opened =>
            string.Equals(opened.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyTracked is not null)
        {
            loadWarnings = swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen;
            return alreadyTracked.Document;
        }

        var existingDocument = _application.GetOpenDocumentByName(fullPath);
        var options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
        if (readOnly)
        {
            options |= (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;
        }

        var errors = 0;
        var warnings = 0;
        string? scratchPath = null;
        ModelDoc2? document = null;
        OpenedDocument? opened = null;
        try
        {
            document = _application.OpenDoc6(fullPath, (int)type, options, string.Empty, ref errors, ref warnings);
            if (readOnly && type == swDocumentTypes_e.swDocPART &&
                (errors & (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen) != 0)
            {
                scratchPath = Path.Combine(Path.GetTempPath(),
                    $"DrawingFactory_{Path.GetFileNameWithoutExtension(fullPath)}_{Guid.NewGuid():N}.SLDPRT");
                File.Copy(fullPath, scratchPath, false);
                errors = 0;
                warnings = 0;
                document = _application.OpenDoc6(scratchPath, (int)type, options, string.Empty, ref errors, ref warnings);
            }

            loadWarnings = (swFileLoadWarning_e)warnings;
            var owned = scratchPath is not null ||
                (existingDocument is null && (warnings & (int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen) == 0);
            if (document is not null)
            {
                // Only claim a returned document if it is the requested file, not a same-title collision.
                var openedPath = document.GetPathName();
                owned &= string.Equals(openedPath, scratchPath ?? fullPath, StringComparison.OrdinalIgnoreCase);
                opened = new OpenedDocument(fullPath, document, document.GetTitle(), owned, scratchPath);
            }

            if (document is null || errors != 0 || document.GetType() != (int)type)
            {
                throw new InvalidOperationException(
                    $"Could not open '{fullPath}'. SOLIDWORKS errors={errors}, warnings={warnings}.");
            }

            _documents.Add(opened!);
            return document;
        }
        catch
        {
            var closed = opened is null || Close(opened);
            if (closed)
            {
                DeleteScratchCopy(scratchPath);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = _documents.Count - 1; index >= 0; index--)
        {
            var opened = _documents[index];
            if (Close(opened))
            {
                DeleteScratchCopy(opened.ScratchPath);
            }
        }
        _documents.Clear();
    }

    private bool Close(OpenedDocument opened)
    {
        if (!opened.Owned)
        {
            return true;
        }

        try
        {
            _application.CloseDoc(opened.Title);
            return true;
        }
        catch (COMException exception)
        {
            _diagnostics.WriteLine($"Could not close document '{opened.Title}': {exception.Message}");
            return false;
        }
    }

    private void DeleteScratchCopy(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _diagnostics.WriteLine($"Could not remove temporary part '{path}': {exception.Message}");
        }
    }

    private sealed record OpenedDocument(string SourcePath, ModelDoc2 Document, string Title, bool Owned, string? ScratchPath);
}
