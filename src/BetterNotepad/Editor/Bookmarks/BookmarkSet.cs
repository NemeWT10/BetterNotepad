using System.Runtime.CompilerServices;
using ICSharpCode.AvalonEdit.Document;
using Model = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Editor.Bookmarks;

/// <summary>
/// Line bookmarks of one document, shared by every view of it. Anchors follow the text while editing; the
/// line numbers are mirrored into <see cref="Model.Bookmarks"/>, which the session persists.
/// </summary>
public sealed class BookmarkSet
{
    private static readonly ConditionalWeakTable<Model, BookmarkSet> Sets = new();

    private readonly Model _model;
    private readonly List<TextAnchor> _anchors = [];
    private TextDocument? _buffer;

    private BookmarkSet(Model model) => _model = model;

    public static BookmarkSet For(Model model)
    {
        var set = Sets.GetValue(model, m => new BookmarkSet(m));
        set.Bind();
        return set;
    }

    /// <summary>Bookmarks added, removed or moved to other lines.</summary>
    public event Action? Changed;

    public int Count => _model.Bookmarks.Count;

    /// <summary>Sorted, 1-based line numbers.</summary>
    public IReadOnlyList<int> Lines => _model.Bookmarks;

    public bool Contains(int line) => _model.Bookmarks.BinarySearch(line) >= 0;

    private void Bind()
    {
        if (!_model.HasBuffer) return;
        var buffer = _model.Buffer;
        if (buffer == _buffer) return;
        if (_buffer is not null) _buffer.Changed -= OnBufferChanged;
        _buffer = buffer;
        _anchors.Clear();
        foreach (var line in _model.Bookmarks.Distinct().Where(l => l >= 1 && l <= buffer.LineCount)) _anchors.Add(Anchor(buffer, line));
        buffer.Changed += OnBufferChanged;
        Sync(raise: false);
    }

    private static TextAnchor Anchor(TextDocument buffer, int line)
    {
        // after-insertion: Enter at the start of the line carries the bookmark down with the text
        var a = buffer.CreateAnchor(buffer.GetLineByNumber(line).Offset);
        a.MovementType = AnchorMovementType.AfterInsertion;
        a.SurviveDeletion = true;
        return a;
    }

    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchors.Count > 0) Sync(raise: true);
    }

    /// <summary>Rebuilds the line list from the anchors (lines merged by a deletion keep one bookmark).</summary>
    private void Sync(bool raise)
    {
        if (_buffer is null) return;
        var lines = _anchors.Select(a => _buffer.GetLineByOffset(Math.Min(a.Offset, _buffer.TextLength)).LineNumber).Distinct().Order().ToList();
        if (lines.SequenceEqual(_model.Bookmarks)) return;
        _model.Bookmarks.Clear();
        _model.Bookmarks.AddRange(lines);
        if (_anchors.Count != lines.Count)
        {
            _anchors.Clear();
            foreach (var l in lines) _anchors.Add(Anchor(_buffer, l));
        }
        if (raise) Changed?.Invoke();
    }

    public void Toggle(int line)
    {
        Bind();
        if (_buffer is null || line < 1 || line > _buffer.LineCount) return;
        var i = _model.Bookmarks.BinarySearch(line);
        if (i >= 0)
        {
            _anchors.RemoveAll(a => _buffer.GetLineByOffset(Math.Min(a.Offset, _buffer.TextLength)).LineNumber == line);
        }
        else
        {
            _anchors.Add(Anchor(_buffer, line));
        }
        Sync(raise: false);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_model.Bookmarks.Count == 0) return;
        _anchors.Clear();
        _model.Bookmarks.Clear();
        Changed?.Invoke();
    }

    /// <summary>The next (or previous) bookmarked line after <paramref name="line"/>, wrapping around; null without bookmarks.</summary>
    public int? Next(int line, bool backward)
    {
        var lines = _model.Bookmarks;
        if (lines.Count == 0) return null;
        if (backward)
        {
            for (var i = lines.Count - 1; i >= 0; i--) if (lines[i] < line) return lines[i];
            return lines[^1];
        }
        foreach (var l in lines) if (l > line) return l;
        return lines[0];
    }
}
