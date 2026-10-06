using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public sealed class SolidWorksService
{
    private readonly SldWorks _application;

    public SolidWorksService()
    {
        _application = new SldWorks
        {
            Visible = true
        };
    }

    public string Revision => _application.RevisionNumber();

    public ISldWorks Application => _application;

    public void CloseDocument(ModelDoc2 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _application.CloseDoc(document.GetTitle());
    }

    public ModelDoc2 OpenPart(string path, out swFileLoadWarning_e warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The input file must have an .SLDPRT extension.", nameof(path));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The SOLIDWORKS part file was not found.", fullPath);
        }

        var errorCode = 0;
        var warningCode = 0;
        var document = _application.OpenDoc6(
            fullPath,
            (int)swDocumentTypes_e.swDocPART,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            string.Empty,
            ref errorCode,
            ref warningCode);

        warnings = (swFileLoadWarning_e)warningCode;
        if (document is null || errorCode != 0)
        {
            var error = (swFileLoadError_e)errorCode;
            throw new InvalidOperationException(
                $"SOLIDWORKS could not open the part. Load error: {error} ({errorCode}); " +
                $"warnings: {warnings} ({warningCode}).");
        }

        if (document.GetType() != (int)swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException("SOLIDWORKS opened the file, but it is not a part document.");
        }

        return document;
    }
}