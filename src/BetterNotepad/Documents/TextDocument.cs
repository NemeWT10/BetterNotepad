using System.ComponentModel;
using System.IO;
using System.Text;
using BetterNotepad.Core;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents.Files;
using BetterNotepad.Editor.Highlighting;
using AvalonDocument = ICSharpCode.AvalonEdit.Document.TextDocument;

namespace BetterNotepad.Documents;

/// <summary>
/// A text buffer (plain text, code, Markdown, HTML, SVG, CSV). The AvalonEdit document is shared by all
/// views of this document (same file in two groups = one buffer, one undo stack).
/// </summary>
public sealed class TextDocument : Document
{
    private AvalonDocument? _buffer;
    private DocumentKind _kind;
    private string _languageId = "text";
    private bool _languageExplicit;
    private Encoding _encoding = TextFileIO.Utf8NoBom;
    private bool _hasBom;
    private LineEnding _eol = LineEnding.CRLF;
    private bool _forcedDirty;
    private bool _isLarge;
    private bool _tailMode;
    private ViewMode _viewMode = ViewMode.Split;

    public TextDocument(string? filePath, string untitledName = "", Guid? id = null) : base(id)
    {
        FilePath = filePath is null ? null : FileUtil.Normalize(filePath);
        UntitledName = untitledName;
        _kind = FileTypes.KindFor(NameForType);
        if (!FileTypes.IsTextual(_kind)) _kind = DocumentKind.Text;
        _languageId = Languages.ForFileName(NameForType);
        _viewMode = DefaultViewMode(_kind);
    }

    public override DocumentKind Kind => _kind;

    public override bool CanSave => true;

    /// <summary>The live buffer; created on load.</summary>
    public AvalonDocument Buffer => _buffer ?? throw new InvalidOperationException("Document not loaded.");

    public bool HasBuffer => _buffer is not null;

    public string LanguageId
    {
        get => _languageId;
        set
        {
            if (Set(ref _languageId, value)) LanguageChanged?.Invoke();
        }
    }

    /// <summary>The user picked the language by hand (rename / content detection no longer override it).</summary>
    public bool LanguageExplicit { get => _languageExplicit; set => Set(ref _languageExplicit, value); }

    public event Action? LanguageChanged;

    public Encoding Encoding { get => _encoding; set { if (Set(ref _encoding, value)) OnPropertyChanged(nameof(EncodingLabel)); } }

    public bool HasBom { get => _hasBom; set { if (Set(ref _hasBom, value)) OnPropertyChanged(nameof(EncodingLabel)); } }

    public string EncodingLabel => TextFileIO.Label(_encoding, _hasBom);

    public LineEnding Eol { get => _eol; set => Set(ref _eol, value); }

    /// <summary>Opened in light mode (large file: no highlighting, no folding).</summary>
    public bool IsLarge { get => _isLarge; set => Set(ref _isLarge, value); }

    /// <summary>"tail -f": reload automatically when the file grows and scroll to the end.</summary>
    public bool TailMode { get => _tailMode; set => Set(ref _tailMode, value); }

    public ViewMode ViewMode { get => _viewMode; set => Set(ref _viewMode, value); }

    /// <summary>Disk state at the last load/save (to tell our own writes from others').</summary>
    public long DiskLength { get; private set; }

    public DateTime DiskWriteUtc { get; private set; }

    // view state persisted in the session
    public int CaretOffset { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public double ScrollY { get; set; }
    public double ScrollX { get; set; }
    public List<int> Bookmarks { get; } = new();

    /// <summary>Initial text that is not on disk (pasted into a new tab, read from a backup).</summary>
    public string? PendingBackupText { get; set; }

    /// <summary>The session holds a backup of unsaved changes for this document (read on first load).</summary>
    public bool RestoreFromBackup { get; set; }

    /// <summary>Set when the buffer changed after the last backup write.</summary>
    public bool BackupStale { get; set; }

    public override bool IsDirty =>
        External != ExternalChange.None || _forcedDirty ||
        (_buffer is not null && (!_buffer.UndoStack.IsOriginalFile || IsUntitled && _buffer.TextLength > 0)) ||
        (_buffer is null && (PendingBackupText is not null || RestoreFromBackup));

    /// <summary>Unsaved content that must survive a restart (untitled with text, or changes vs. the file on disk).</summary>
    public bool NeedsBackup => _buffer is not null
        ? IsDirty && (_buffer.TextLength > 0 || !IsUntitled)
        : PendingBackupText is not null || RestoreFromBackup;

    public event EventHandler? BufferChanged;

    public static ViewMode DefaultViewMode(DocumentKind kind)
    {
        var views = SettingsService.I?.Get<FilesSettings>().DefaultViews;
        if (views is not null && views.TryGetValue(kind.ToString().ToLowerInvariant(), out var v) && Enum.TryParse<ViewMode>(v, true, out var mode))
            return mode;
        return kind switch
        {
            DocumentKind.Markdown => ViewMode.Split,
            DocumentKind.Html => ViewMode.Split,
            DocumentKind.Svg => ViewMode.Split,
            DocumentKind.Csv => ViewMode.Preview,
            _ => ViewMode.Edit,
        };
    }

    protected override void OnFilePathChanged()
    {
        var kind = FileTypes.KindFor(NameForType);
        if (!FileTypes.IsTextual(kind)) kind = DocumentKind.Text;
        if (kind != _kind)
        {
            _kind = kind;
            OnPropertyChanged(nameof(Kind));
            ViewMode = DefaultViewMode(kind);
        }
        if (!_languageExplicit)
        {
            var lang = Languages.ForFileName(NameForType);
            if (lang != "text" || FilePath is not null || Path.HasExtension(NameForType)) LanguageId = lang;
        }
    }

    /// <summary>Creates the buffer from disk (or from the pending backup).</summary>
    public override async Task LoadAsync()
    {
        if (IsLoaded) return;
        var fromBackupFile = false;
        if (PendingBackupText is null && RestoreFromBackup)
        {
            var file = Session.BackupStore.FileFor(Id);
            if (File.Exists(file))
            {
                PendingBackupText = await File.ReadAllTextAsync(file, Encoding.UTF8);
                fromBackupFile = true;
            }
            RestoreFromBackup = false;
        }
        if (PendingBackupText is { } backup)
        {
            CreateBuffer(backup);
            BackupStale = !fromBackupFile;
            PendingBackupText = null;
            _forcedDirty = FilePath is not null;
            if (FilePath is not null && File.Exists(FilePath))
            {
                var fi = new FileInfo(FilePath);
                // keep the disk stamp from the session so an external change made meanwhile is still detected
                if (DiskLength == 0 && DiskWriteUtc == default) { DiskLength = fi.Length; DiskWriteUtc = fi.LastWriteTimeUtc; }
            }
            IsLoaded = true;
            RaiseDirtyChanged();
            return;
        }

        if (FilePath is null)
        {
            CreateBuffer("");
            IsLoaded = true;
            return;
        }

        var path = FilePath;
        var large = SettingsService.I.Get<FilesSettings>().LargeFileMb * 1024L * 1024L;
        var content = await Task.Run(() => TextFileIO.Read(path));
        ApplyLoaded(content, large);
        IsLoaded = true;
    }

    private void ApplyLoaded(TextFileContent content, long largeThreshold)
    {
        Encoding = content.Encoding;
        HasBom = content.HasBom;
        Eol = content.Eol;
        IsLarge = content.Length > largeThreshold;
        DiskLength = content.Length;
        DiskWriteUtc = content.LastWriteUtc;
        if (_buffer is null) CreateBuffer(content.Text);
        else ReplaceAll(content.Text);
        _buffer!.UndoStack.MarkAsOriginalFile();
        _forcedDirty = false;
        External = ExternalChange.None;
        RaiseDirtyChanged();
    }

    private void CreateBuffer(string text)
    {
        _buffer = new AvalonDocument(text);
        _buffer.UndoStack.PropertyChanged += OnUndoChanged;
        _buffer.TextChanged += OnTextChanged;
        if (IsLarge) _buffer.UndoStack.SizeLimit = 64;
        OnPropertyChanged(nameof(HasBuffer));
    }

    private void ReplaceAll(string text)
    {
        var doc = _buffer!;
        doc.Replace(0, doc.TextLength, text);
    }

    private void OnUndoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICSharpCode.AvalonEdit.Document.UndoStack.IsOriginalFile)) RaiseDirtyChanged();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        BackupStale = true;
        if (IsUntitled) RaiseDirtyChanged();
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reloads from disk (discarding the in-app version), optionally with a forced encoding.</summary>
    public async Task ReloadAsync(Encoding? forced = null)
    {
        if (FilePath is null) return;
        var path = FilePath;
        var content = await Task.Run(() => TextFileIO.Read(path, forced));
        if (forced is not null) content = content with { Encoding = forced };
        ApplyLoaded(content, SettingsService.I.Get<FilesSettings>().LargeFileMb * 1024L * 1024L);
        IsLoaded = true;
    }

    /// <summary>Writes the buffer to <paramref name="path"/> (Save / Save As).</summary>
    public void SaveTo(string path)
    {
        var text = _buffer?.Text ?? PendingBackupText ?? "";
        TextFileIO.Write(path, text, Encoding, HasBom);
        var fi = new FileInfo(path);
        DiskLength = fi.Length;
        DiskWriteUtc = fi.LastWriteTimeUtc;
        if (!FileUtil.PathEquals(FilePath, path))
        {
            FilePath = FileUtil.Normalize(path);
            UntitledName = "";
        }
        _buffer?.UndoStack.MarkAsOriginalFile();
        _forcedDirty = false;
        External = ExternalChange.None;
        RaiseDirtyChanged();
    }

    /// <summary>After an elevated helper wrote the file for us.</summary>
    public void SaveToCompleted(string path)
    {
        if (!FileUtil.PathEquals(FilePath, path))
        {
            FilePath = FileUtil.Normalize(path);
            UntitledName = "";
        }
        RefreshDiskStamp();
        _buffer?.UndoStack.MarkAsOriginalFile();
        _forcedDirty = false;
        External = ExternalChange.None;
        RaiseDirtyChanged();
    }

    /// <summary>Forget unsaved state (the user chose to discard).</summary>
    public void MarkClean()
    {
        _forcedDirty = false;
        _buffer?.UndoStack.MarkAsOriginalFile();
        RaiseDirtyChanged();
    }

    /// <summary>Keep the in-app version after an external change: stays unsaved, disk stamp refreshed.</summary>
    public void KeepMine()
    {
        RefreshDiskStamp();
        _forcedDirty = true;
        External = ExternalChange.None;
        RaiseDirtyChanged();
    }

    public void RefreshDiskStamp()
    {
        if (FilePath is null || !File.Exists(FilePath)) return;
        var fi = new FileInfo(FilePath);
        DiskLength = fi.Length;
        DiskWriteUtc = fi.LastWriteTimeUtc;
    }

    /// <summary>True when the file on disk differs from what we last loaded/saved.</summary>
    public bool DiskDiffers(out bool deleted)
    {
        deleted = false;
        if (FilePath is null) return false;
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists)
            {
                deleted = true;
                return true;
            }
            return fi.Length != DiskLength || fi.LastWriteTimeUtc != DiskWriteUtc;
        }
        catch
        {
            return false;
        }
    }

    public void RestoreDiskStamp(long length, DateTime writeUtc)
    {
        DiskLength = length;
        DiskWriteUtc = writeUtc;
    }

    /// <summary>Converts every line break to <paramref name="eol"/> (one undo step).</summary>
    public void ConvertLineEndings(LineEnding eol)
    {
        Eol = eol;
        if (_buffer is null) return;
        var target = eol.Text();
        var doc = _buffer;
        doc.BeginUpdate();
        try
        {
            for (var i = doc.LineCount; i >= 1; i--)
            {
                var line = doc.GetLineByNumber(i);
                if (line.DelimiterLength == 0) continue;
                var delim = doc.GetText(line.EndOffset, line.DelimiterLength);
                if (delim != target) doc.Replace(line.EndOffset, line.DelimiterLength, target);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    public void MarkForcedDirty()
    {
        _forcedDirty = true;
        RaiseDirtyChanged();
    }

    public override void Dispose()
    {
        if (_buffer is not null)
        {
            _buffer.UndoStack.PropertyChanged -= OnUndoChanged;
            _buffer.TextChanged -= OnTextChanged;
        }
    }
}
