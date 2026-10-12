using System.IO;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Documents;

/// <summary>What changed on disk behind an open document's back.</summary>
public enum ExternalChange
{
    None,
    Modified,
    Deleted,
}

/// <summary>
/// An open file or untitled buffer. One document can be shown in several tab groups / windows
/// at once (views are separate from documents).
/// </summary>
public abstract class Document : ObservableObject
{
    private string? _filePath;
    private string _untitledName = "";
    private int _untitledNumber;
    private bool _isPinned;
    private bool _isLocked;
    private ExternalChange _external;

    protected Document(Guid? id = null) => Id = id ?? Guid.NewGuid();

    public Guid Id { get; }

    public abstract DocumentKind Kind { get; }

    /// <summary>Full path, or null for an untitled (temporary) buffer.</summary>
    public string? FilePath
    {
        get => _filePath;
        protected set
        {
            if (!Set(ref _filePath, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsUntitled));
            OnPropertyChanged(nameof(ToolTipText));
            OnFilePathChanged();
        }
    }

    /// <summary>
    /// Name for untitled buffers: "new 1" built from <see cref="UntitledNumber"/> in the current UI language,
    /// or a custom name after rename (setting a non-empty name drops the number).
    /// </summary>
    public string UntitledName
    {
        get => _untitledName.Length == 0 && _untitledNumber > 0 ? Loc.T("tab.untitledName", _untitledNumber) : _untitledName;
        set
        {
            if (value.Length > 0) _untitledNumber = 0;
            if (!Set(ref _untitledName, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToolTipText));
            OnFilePathChanged();
        }
    }

    /// <summary>Number of an automatically named untitled buffer ("new 3"), 0 for a custom name.</summary>
    public int UntitledNumber
    {
        get => _untitledNumber;
        set
        {
            if (value > 0) _untitledName = "";
            if (!Set(ref _untitledNumber, value)) return;
            RefreshTitle();
            OnFilePathChanged();
        }
    }

    /// <summary>The UI language changed: a numbered untitled title is rebuilt.</summary>
    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(UntitledName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTipText));
    }

    public bool IsUntitled => _filePath is null;

    public string Title => _filePath is null ? UntitledName : Path.GetFileName(_filePath);

    /// <summary>File name used for type detection (untitled buffers may carry an extension after rename).</summary>
    public string NameForType => _filePath ?? _untitledName;

    public virtual string ToolTipText => _filePath ?? Loc.T("tab.untitledTip", UntitledName);

    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    /// <summary>Read-only lock (in-app, remembered per file).</summary>
    public bool IsLocked { get => _isLocked; set => Set(ref _isLocked, value); }

    public ExternalChange External
    {
        get => _external;
        set
        {
            if (Set(ref _external, value)) OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>Unsaved state shown on the tab (different icon).</summary>
    public abstract bool IsDirty { get; }

    public virtual bool CanSave => false;

    /// <summary>Whether content has been loaded (tabs restore lazily: content loads on first activation).</summary>
    public bool IsLoaded { get; protected set; }

    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Releases heavy content while no view shows the document (PDF pages, images…).</summary>
    public virtual void ReleaseHeavyContent() { }

    public virtual void Dispose() { }

    protected virtual void OnFilePathChanged() { }

    /// <summary>The file was renamed / moved on disk by us.</summary>
    public void ChangePath(string newPath) => FilePath = FileUtil.Normalize(newPath);

    protected void RaiseDirtyChanged() => OnPropertyChanged(nameof(IsDirty));
}
