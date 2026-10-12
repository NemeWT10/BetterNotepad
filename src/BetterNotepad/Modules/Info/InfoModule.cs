using System.IO;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;

namespace BetterNotepad.Modules.Info;

/// <summary>
/// Files that are not text: binary files open as a read-only hex view (BetterNotepad.Hex); files nothing can show
/// (docx, zip…, and PDF / images until their modules exist) open as an "Info" tab with size, dates, hashes and metadata.
/// </summary>
public static class InfoModule
{
    public static void Register()
    {
        DocumentManager.I.RegisterFactory(new Factory(DocumentKind.Hex));
        DocumentManager.I.RegisterFactory(new Factory(DocumentKind.Info));
        DocumentViewRegistry.RegisterView(DocumentKind.Hex, d => new HexDocumentView((FileInfoDocument)d));
        DocumentViewRegistry.RegisterView(DocumentKind.Info, d => new InfoView((FileInfoDocument)d));
    }

    private sealed class Factory(DocumentKind kind) : IDocumentFactory
    {
        public DocumentKind Kind => kind;

        public Document Create(string path, Guid? id = null) => new FileInfoDocument(path, kind, id);
    }
}

/// <summary>A file shown read-only (hex or info): nothing to save, nothing to back up.</summary>
public sealed class FileInfoDocument : Document
{
    private readonly DocumentKind _kind;

    public FileInfoDocument(string path, DocumentKind kind, Guid? id = null) : base(id)
    {
        _kind = kind;
        FilePath = path;
        IsLoaded = true;
    }

    public override DocumentKind Kind => _kind;

    public override bool IsDirty => false;

    public bool Exists => FilePath is { } p && File.Exists(p);
}
