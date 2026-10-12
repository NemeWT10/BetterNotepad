using System.Collections.ObjectModel;
using System.IO;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents.Files;
using BetterNotepad.Editor.Highlighting;

namespace BetterNotepad.Documents;

/// <summary>Creates non-text documents (PDF, images, hex, info). Registered by modules.</summary>
public interface IDocumentFactory
{
    DocumentKind Kind { get; }

    Document Create(string path, Guid? id = null);
}

/// <summary>All open documents (across every window and tab group).</summary>
public sealed class DocumentManager
{
    public static DocumentManager I { get; } = new();

    private readonly Dictionary<DocumentKind, IDocumentFactory> _factories = new();
    private int _untitledCounter;

    public ObservableCollection<Document> Documents { get; } = new();

    private DocumentManager()
    {
        // numbered untitled titles ("nowy 1" / "new 1") follow the UI language
        Loc.LanguageChanged += () =>
        {
            foreach (var d in Documents)
                if (d.IsUntitled && d.UntitledNumber > 0) d.RefreshTitle();
        };
    }

    public event Action<Document>? Opened;
    public event Action<Document>? Closed;

    public void RegisterFactory(IDocumentFactory factory) => _factories[factory.Kind] = factory;

    public bool HasFactory(DocumentKind kind) => _factories.ContainsKey(kind);

    public int UntitledCounter { get => _untitledCounter; set => _untitledCounter = Math.Max(_untitledCounter, value); }

    public Document? FindByPath(string path)
    {
        var norm = FileUtil.Normalize(path);
        return Documents.FirstOrDefault(d => d.FilePath is not null && string.Equals(d.FilePath, norm, StringComparison.OrdinalIgnoreCase));
    }

    public Document? FindById(Guid id) => Documents.FirstOrDefault(d => d.Id == id);

    public TextDocument NewUntitled(string? content = null, string? languageId = null)
    {
        var number = ++_untitledCounter;
        var doc = new TextDocument(null) { UntitledNumber = number }; // title built from Loc at display time
        if (content is not null) doc.PendingBackupText = content;
        if (languageId is not null)
        {
            doc.LanguageId = languageId;
            doc.LanguageExplicit = true;
        }
        Add(doc);
        return doc;
    }

    /// <summary>Opens (or returns the already open) document for <paramref name="path"/>. Content loads lazily.</summary>
    public Document Open(string path)
    {
        var existing = FindByPath(path);
        if (existing is not null) return existing;
        var doc = CreateFor(FileUtil.Normalize(path));
        Add(doc);
        return doc;
    }

    /// <summary>Picks the document type: by extension, then by sniffing for binary content.</summary>
    public Document CreateFor(string path, Guid? id = null)
    {
        var kind = FileTypes.KindFor(path);
        if (!FileTypes.IsTextual(kind))
        {
            if (_factories.TryGetValue(kind, out var f)) return f.Create(path, id);
            if (_factories.TryGetValue(DocumentKind.Info, out var info)) return info.Create(path, id);
        }
        else if (File.Exists(path) && IsBinary(path))
        {
            if (_factories.TryGetValue(DocumentKind.Hex, out var hex)) return hex.Create(path, id);
            if (_factories.TryGetValue(DocumentKind.Info, out var info)) return info.Create(path, id);
        }
        return new TextDocument(path, id: id);
    }

    private static bool IsBinary(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[8192];
            var n = fs.Read(head);
            head = head[..n];
            TextFileIO.Detect(head, out var bom, out _);
            return !bom && TextFileIO.LooksBinary(head);
        }
        catch
        {
            return false;
        }
    }

    public void Add(Document doc)
    {
        if (Documents.Contains(doc)) return;
        Documents.Add(doc);
        Opened?.Invoke(doc);
    }

    public void Close(Document doc)
    {
        if (!Documents.Remove(doc)) return;
        doc.Dispose();
        Closed?.Invoke(doc);
    }

    /// <summary>After loading: refine the ASM dialect from the content.</summary>
    public static void RefineLanguage(TextDocument doc)
    {
        if (doc.LanguageExplicit || !doc.HasBuffer) return;
        if (doc.LanguageId.StartsWith("asm", StringComparison.Ordinal))
        {
            var text = doc.Buffer.TextLength > 20000 ? doc.Buffer.GetText(0, 20000) : doc.Buffer.Text;
            doc.LanguageId = Languages.DetectAsmDialect(text);
        }
    }
}
