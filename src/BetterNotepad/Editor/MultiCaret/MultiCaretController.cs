using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using BetterNotepad.Editor.Text;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace BetterNotepad.Editor.MultiCaret;

/// <summary>One caret with its selection anchor (Anchor == Caret: no selection). Offsets are document offsets.</summary>
public struct CaretRange(int anchor, int caret, bool primary = false)
{
    public int Anchor = anchor;
    public int Caret = caret;
    public double DesiredX = double.NaN;
    public bool IsPrimary = primary;

    public readonly int Start => Math.Min(Anchor, Caret);
    public readonly int End => Math.Max(Anchor, Caret);
    public readonly int Length => End - Start;
    public readonly bool IsEmpty => Anchor == Caret;
}

/// <summary>One caret's edit: replace [Start, Start+Length) with Text; afterwards the selection runs Start+AnchorAt → Start+CaretAt.</summary>
public readonly record struct CaretEdit(int Start, int Length, string Text, int AnchorAt, int CaretAt);

/// <summary>Caret movements, named exactly like AvalonEdit's internal CaretMovementType.</summary>
public enum CaretMove
{
    CharLeft, CharRight, Backspace, WordLeft, WordRight, LineUp, LineDown, PageUp, PageDown, LineStart, LineEnd, DocumentStart, DocumentEnd,
}

/// <summary>
/// VS Code style multiple carets on top of AvalonEdit. The primary caret is AvalonEdit's own caret and selection;
/// the others live here. Every edit applies to all carets inside one document update (one undo step).
/// Column (rectangular) selection stays AvalonEdit's (Alt+drag); Alt+click adds or removes a caret.
/// </summary>
public sealed class MultiCaretController
{
    private const int MaxCarets = 10_000;

    private readonly CodeEditor _editor;
    private readonly TextArea _area;
    private readonly List<CaretRange> _extra = [];
    private TextDocument? _doc;
    private bool _internal;
    private double _primaryX = double.NaN;
    private bool _wordMode;
    private string? _wordModeText;
    private (Point Point, CaretRange Primary, List<CaretRange> Extra)? _altClick;
    private string? _textCache;
    private ITextSourceVersion? _textVersion;

    public MultiCaretController(CodeEditor editor)
    {
        _editor = editor;
        _area = editor.TextArea;
        _area.PreviewKeyDown += OnPreviewKeyDown;
        _area.PreviewTextInput += OnPreviewTextInput;
        _area.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        _area.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp), handledEventsToo: true);
        CommandManager.AddPreviewExecutedHandler(_area, OnPreviewExecuted);
        _area.DocumentChanged += (_, _) => Rebind();
        _area.Caret.PositionChanged += (_, _) =>
        {
            if (!_internal) _primaryX = double.NaN;
        };
        _area.SelectionChanged += (_, _) =>
        {
            if (!_internal && _extra.Count > 0 && _area.Selection is RectangleSelection) Clear();
        };
        RemoveBuiltInCtrlD();
        Rebind();
    }

    /// <summary>Carets or their selections changed (renderers, line numbers).</summary>
    public event Action? Changed;

    /// <summary>The carets besides AvalonEdit's own.</summary>
    public IReadOnlyList<CaretRange> Extra => _extra;

    public bool HasExtra => _extra.Count > 0;

    public int Count => 1 + _extra.Count;

    /// <summary>What typing does here (auto-close, smart indent…); kept current by <see cref="EditorFeatures"/>.</summary>
    public TypingContext Typing { get; set; } = TypingContext.Default;

    private static bool _ctrlDRemoved;

    /// <summary>AvalonEdit binds Ctrl+D to "delete line"; Ctrl+D is "add next occurrence" here (VS Code).</summary>
    private void RemoveBuiltInCtrlD()
    {
        static bool IsCtrlD(InputGesture g) => g is KeyGesture { Key: Key.D, Modifiers: ModifierKeys.Control };
        var bindings = _area.DefaultInputHandler.Editing.InputBindings;
        foreach (var b in bindings.Where(b => b.Gesture is { } g && IsCtrlD(g)).ToList()) bindings.Remove(b);
        foreach (var b in _area.InputBindings.OfType<InputBinding>().Where(b => b.Gesture is { } g && IsCtrlD(g)).ToList()) _area.InputBindings.Remove(b);
        if (_ctrlDRemoved) return;
        _ctrlDRemoved = true;
        // WPF also matches a routed command's own gestures through its CommandBinding
        var gestures = AvalonEditCommands.DeleteLine.InputGestures;
        foreach (var g in gestures.OfType<InputGesture>().Where(IsCtrlD).ToList()) gestures.Remove(g);
    }

    private void Rebind()
    {
        if (_doc is not null) _doc.Changed -= OnDocumentChanged;
        _doc = _area.Document;
        _extra.Clear();
        _textCache = null;
        _textVersion = null;
        if (_doc is not null) _doc.Changed += OnDocumentChanged;
        Changed?.Invoke();
    }

    /// <summary>Someone else edited the buffer (another view, a command acting on the primary caret): carets follow the text.</summary>
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_internal || _extra.Count == 0) return;
        for (var i = 0; i < _extra.Count; i++)
        {
            var r = _extra[i];
            r.Anchor = e.GetNewOffset(r.Anchor);
            r.Caret = e.GetNewOffset(r.Caret);
            _extra[i] = r;
        }
        Changed?.Invoke();
    }

    // ───────────── caret state ─────────────

    /// <summary>AvalonEdit's caret and selection as a range.</summary>
    public CaretRange Primary()
    {
        var caret = _area.Caret.Offset;
        var sel = _area.Selection;
        if (sel.IsEmpty || sel is RectangleSelection) return new CaretRange(caret, caret, true) { DesiredX = _primaryX };
        var seg = sel.SurroundingSegment;
        var anchor = caret == seg.Offset ? seg.EndOffset : seg.Offset;
        if (caret != seg.Offset && caret != seg.EndOffset) caret = seg.EndOffset;
        return new CaretRange(anchor, caret, true) { DesiredX = _primaryX };
    }

    /// <summary>All carets, sorted, overlapping ones merged.</summary>
    public List<CaretRange> All()
    {
        var list = new List<CaretRange>(_extra.Count + 1) { Primary() };
        list.AddRange(_extra);
        return Merge(list);
    }

    /// <summary>Line numbers of the extra carets.</summary>
    public IEnumerable<int> ExtraLines()
    {
        if (_doc is null) yield break;
        var len = _doc.TextLength;
        foreach (var r in _extra) yield return _doc.GetLineByOffset(Math.Clamp(r.Caret, 0, len)).LineNumber;
    }

    private static List<CaretRange> Merge(List<CaretRange> list)
    {
        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var result = new List<CaretRange>(list.Count);
        foreach (var r in list)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (r.Start < last.End || r.Start == last.Start || r.IsEmpty && r.Start == last.End)
                {
                    var keep = r.IsPrimary ? r : last.IsPrimary ? last : r.Length >= last.Length ? r : last;
                    var start = Math.Min(last.Start, r.Start);
                    var end = Math.Max(last.End, r.End);
                    var forward = keep.Anchor <= keep.Caret;
                    result[^1] = new CaretRange(forward ? start : end, forward ? end : start, r.IsPrimary || last.IsPrimary) { DesiredX = keep.DesiredX };
                    continue;
                }
            }
            result.Add(r);
        }
        return result;
    }

    /// <summary>Makes <paramref name="ranges"/> the carets (the primary one becomes AvalonEdit's caret).</summary>
    public void Commit(List<CaretRange> ranges, bool reveal = true)
    {
        if (_doc is null || ranges.Count == 0) return;
        var len = _doc.TextLength;
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            r.Anchor = Math.Clamp(r.Anchor, 0, len);
            r.Caret = Math.Clamp(r.Caret, 0, len);
            ranges[i] = r;
        }
        ranges = Merge(ranges);
        var pi = ranges.FindIndex(r => r.IsPrimary);
        if (pi < 0) pi = ranges.Count - 1;
        var p = ranges[pi];
        _internal = true;
        try
        {
            _area.Caret.Offset = p.Caret;
            if (p.IsEmpty) _area.ClearSelection();
            else _area.Selection = Selection.Create(_area, p.Anchor, p.Caret);
        }
        finally
        {
            _internal = false;
        }
        _primaryX = p.DesiredX;
        _extra.Clear();
        for (var i = 0; i < ranges.Count && _extra.Count < MaxCarets; i++)
        {
            if (i == pi) continue;
            var r = ranges[i];
            r.IsPrimary = false;
            _extra.Add(r);
        }
        if (reveal) _area.Caret.BringCaretToView();
        Changed?.Invoke();
    }

    /// <summary>Back to one caret (the primary).</summary>
    public void Clear()
    {
        _wordMode = false;
        if (_extra.Count == 0) return;
        _extra.Clear();
        Changed?.Invoke();
    }

    // ───────────── edits ─────────────

    /// <summary>Applies one edit per caret (null = leave that caret) as a single undo step.</summary>
    public bool Edit(Func<CaretRange, int, CaretEdit?> make)
    {
        if (_doc is null || _editor.IsReadOnly) return false;
        var ranges = All();
        var edits = new CaretEdit?[ranges.Count];
        for (var i = 0; i < ranges.Count; i++) edits[i] = make(ranges[i], i);
        var length = _doc.TextLength;
        _internal = true;
        try
        {
            using (_doc.RunUpdate())
            {
                var delta = 0;
                var lastEnd = -1;
                for (var i = 0; i < ranges.Count; i++)
                {
                    var r = ranges[i];
                    if (edits[i] is not { } ed || ed.Start < lastEnd || ed.Start < 0 || ed.Start + ed.Length > length)
                    {
                        r.Anchor += delta;
                        r.Caret += delta;
                        ranges[i] = r;
                        continue;
                    }
                    var start = ed.Start + delta;
                    if (ed.Length > 0 || ed.Text.Length > 0) _doc.Replace(start, ed.Length, ed.Text);
                    lastEnd = ed.Start + ed.Length;
                    r.Anchor = start + ed.AnchorAt;
                    r.Caret = start + ed.CaretAt;
                    r.DesiredX = double.NaN;
                    ranges[i] = r;
                    delta += ed.Text.Length - ed.Length;
                }
            }
        }
        finally
        {
            _internal = false;
        }
        Commit(ranges);
        return true;
    }

    /// <summary>Types text at every caret (auto-close, type-over, wrap, smart Enter included).</summary>
    public bool TypeText(string text)
    {
        if (_doc is null) return false;
        var ctx = Typing;
        var doc = _doc;
        return Edit((r, _) => MultiCaret.Typing.Type(doc, r, text, ctx).Edit);
    }

    /// <summary>Backspace / Delete (whole words with <paramref name="word"/>) at every caret.</summary>
    public bool Delete(bool backward, bool word)
    {
        if (_doc is null) return false;
        var doc = _doc;
        var ctx = Typing;
        return Edit((r, _) =>
        {
            if (!r.IsEmpty) return new CaretEdit(r.Start, r.Length, "", 0, 0);
            if (backward && !word && MultiCaret.Typing.DeletePair(doc, r, ctx) is { } pair) return pair;
            var x = double.NaN;
            var target = MoveOffset(r.Caret, backward ? (word ? CaretMove.WordLeft : CaretMove.Backspace) : word ? CaretMove.WordRight : CaretMove.CharRight, ref x);
            if (target == r.Caret) return null;
            var s = Math.Min(target, r.Caret);
            return new CaretEdit(s, Math.Abs(target - r.Caret), "", 0, 0);
        });
    }

    public bool InsertTab()
    {
        if (_doc is null) return false;
        var doc = _doc;
        var ctx = Typing;
        return Edit((r, _) => MultiCaret.Typing.Tab(doc, r, ctx));
    }

    /// <summary>Shift+Tab: removes one indentation level from every line that holds a caret.</summary>
    public bool Outdent()
    {
        if (_doc is null || _editor.IsReadOnly) return false;
        var doc = _doc;
        var ranges = All();
        var anchors = ranges.Select(r => (A: doc.CreateAnchor(r.Anchor), C: doc.CreateAnchor(r.Caret))).ToList();
        var lines = new SortedSet<int>();
        foreach (var r in ranges)
            for (var n = doc.GetLineByOffset(r.Start).LineNumber; n <= doc.GetLineByOffset(r.End).LineNumber; n++) lines.Add(n);
        var size = Typing.IndentSize;
        using (doc.RunUpdate())
        {
            foreach (var n in lines.Reverse())
            {
                var line = doc.GetLineByNumber(n);
                var remove = 0;
                while (remove < size && line.Offset + remove < line.EndOffset && doc.GetCharAt(line.Offset + remove) == ' ') remove++;
                if (remove == 0 && line.Length > 0 && doc.GetCharAt(line.Offset) == '\t') remove = 1;
                if (remove > 0) doc.Remove(line.Offset, remove);
            }
        }
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            r.Anchor = anchors[i].A.Offset;
            r.Caret = anchors[i].C.Offset;
            ranges[i] = r;
        }
        Commit(ranges);
        return true;
    }

    /// <summary>Paste: one line per caret when the clipboard has exactly as many lines as there are carets.</summary>
    public bool Paste(string text)
    {
        if (_doc is null) return false;
        var doc = _doc;
        var ranges = All();
        var nl = TextUtilities.GetNewLineFromDocument(doc, doc.GetLineByOffset(ranges[0].Start).LineNumber);
        var unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = unified.TrimEnd('\n').Split('\n');
        if (ranges.Count > 1 && parts.Length == ranges.Count)
            return Edit((r, i) => new CaretEdit(r.Start, r.Length, parts[i], parts[i].Length, parts[i].Length));
        var all = unified.Replace("\n", nl);
        return Edit((r, _) => new CaretEdit(r.Start, r.Length, all, all.Length, all.Length));
    }

    /// <summary>The selected texts, one per line (null when no caret has a selection).</summary>
    public string? SelectedTexts()
    {
        if (_doc is null) return null;
        var ranges = All().Where(r => !r.IsEmpty).ToList();
        if (ranges.Count == 0) return null;
        var nl = TextUtilities.GetNewLineFromDocument(_doc, 1);
        return string.Join(nl, ranges.Select(r => _doc.GetText(r.Start, r.Length)));
    }

    // ───────────── movement ─────────────

    public void Move(CaretMove move, bool extend)
    {
        if (_doc is null) return;
        var ranges = All();
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            int target;
            if (!extend && !r.IsEmpty && move is CaretMove.CharLeft or CaretMove.CharRight)
            {
                target = move == CaretMove.CharLeft ? r.Start : r.End;
                r.DesiredX = double.NaN;
            }
            else
            {
                var x = r.DesiredX;
                target = MoveOffset(r.Caret, move, ref x);
                r.DesiredX = x;
            }
            r.Caret = target;
            if (!extend) r.Anchor = target;
            ranges[i] = r;
        }
        Commit(ranges);
    }

    private static readonly MethodInfo? NavMethod = typeof(TextArea).Assembly
        .GetType("ICSharpCode.AvalonEdit.Editing.CaretNavigationCommandHandler")
        ?.GetMethod("GetNewCaretPosition", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly Type? NavEnum = typeof(TextArea).Assembly.GetType("ICSharpCode.AvalonEdit.Editing.CaretMovementType");

    /// <summary>Where a caret at <paramref name="offset"/> goes (AvalonEdit's own navigation: wrapping, folding, smart Home).</summary>
    public int MoveOffset(int offset, CaretMove move, ref double desiredX)
    {
        var doc = _doc!;
        var view = _area.TextView;
        offset = Math.Clamp(offset, 0, doc.TextLength);
        if (NavMethod is not null && NavEnum is not null && view.Document == doc && Enum.TryParse(NavEnum, move.ToString(), out var kind))
        {
            try
            {
                var loc = doc.GetLocation(offset);
                var visual = view.GetOrConstructVisualLine(doc.GetLineByNumber(loc.Line));
                var pos = new TextViewPosition(loc, visual.GetVisualColumn(offset - visual.FirstDocumentLine.Offset));
                var args = new object?[] { view, pos, kind, false, desiredX };
                var result = (TextViewPosition)NavMethod.Invoke(null, args)!;
                desiredX = (double)args[4]!;
                return Math.Clamp(doc.GetOffset(result.Location), 0, doc.TextLength);
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
            {
                // fall back to logical movement below
            }
        }
        return FallbackMove(doc, offset, move);
    }

    private static int FallbackMove(TextDocument doc, int offset, CaretMove move)
    {
        var line = doc.GetLineByOffset(offset);
        int Next(LogicalDirection dir, CaretPositioningMode mode)
        {
            var o = TextUtilities.GetNextCaretPosition(doc, offset, dir, mode);
            return o < 0 ? offset : o;
        }
        switch (move)
        {
            case CaretMove.CharLeft or CaretMove.Backspace: return Next(LogicalDirection.Backward, CaretPositioningMode.Normal);
            case CaretMove.CharRight: return Next(LogicalDirection.Forward, CaretPositioningMode.Normal);
            case CaretMove.WordLeft: return Next(LogicalDirection.Backward, CaretPositioningMode.WordStart);
            case CaretMove.WordRight: return Next(LogicalDirection.Forward, CaretPositioningMode.WordStart);
            case CaretMove.LineStart:
                var ws = TextUtilities.GetLeadingWhitespace(doc, line);
                return offset == ws.EndOffset ? line.Offset : ws.EndOffset;
            case CaretMove.LineEnd: return line.EndOffset;
            case CaretMove.DocumentStart: return 0;
            case CaretMove.DocumentEnd: return doc.TextLength;
            case CaretMove.LineUp or CaretMove.LineDown or CaretMove.PageUp or CaretMove.PageDown:
                var step = move is CaretMove.LineUp or CaretMove.LineDown ? 1 : 30;
                var n = Math.Clamp(line.LineNumber + (move is CaretMove.LineUp or CaretMove.PageUp ? -step : step), 1, doc.LineCount);
                var target = doc.GetLineByNumber(n);
                return target.Offset + Math.Min(offset - line.Offset, target.Length);
            default: return offset;
        }
    }

    // ───────────── commands ─────────────

    private string DocText()
    {
        var v = _doc!.Version;
        if (_textCache is null || _textVersion is null || !v.BelongsToSameDocumentAs(_textVersion) || v.CompareAge(_textVersion) != 0)
        {
            _textCache = _doc.Text;
            _textVersion = v;
        }
        return _textCache;
    }

    /// <summary>Ctrl+D: selects the word at the caret, then adds the next occurrence of the selection as a new caret.</summary>
    public void AddNextOccurrence()
    {
        if (_doc is null) return;
        var all = All();
        var primary = all.First(r => r.IsPrimary);
        if (primary.IsEmpty)
        {
            // first press: select the word at every caret
            var changed = false;
            for (var i = 0; i < all.Count; i++)
            {
                if (!all[i].IsEmpty || TextHelpers.WordAt(_doc, all[i].Caret) is not { } w) continue;
                all[i] = new CaretRange(w.Start, w.End, all[i].IsPrimary);
                changed = true;
            }
            if (!changed) return;
            Commit(all);
            var p = Primary();
            _wordMode = true;
            _wordModeText = _doc.GetText(p.Start, p.Length);
            return;
        }
        var needle = _doc.GetText(primary.Start, primary.Length);
        if (_wordMode && needle != _wordModeText) _wordMode = false;
        var text = DocText();
        var taken = new HashSet<int>(all.Select(r => r.Start));
        var found = -1;
        for (int from = primary.End, pass = 0; pass < 2 && found < 0; pass++, from = 0)
        {
            for (var i = text.IndexOf(needle, from, StringComparison.Ordinal); i >= 0; i = i + 1 <= text.Length ? text.IndexOf(needle, i + 1, StringComparison.Ordinal) : -1)
            {
                if (pass == 1 && i >= primary.End) break;
                if (taken.Contains(i) || _wordMode && !TextHelpers.IsWholeWord(text, i, needle.Length)) continue;
                found = i;
                break;
            }
        }
        if (found < 0 || all.Count >= MaxCarets) return;
        for (var i = 0; i < all.Count; i++)
        {
            var r = all[i];
            r.IsPrimary = false;
            all[i] = r;
        }
        all.Add(new CaretRange(found, found + needle.Length, true));
        Commit(all);
    }

    /// <summary>Ctrl+Shift+L: a caret on every occurrence of the selection (or of the word at the caret).</summary>
    public void SelectAllOccurrences()
    {
        if (_doc is null) return;
        var primary = Primary();
        int start;
        string needle;
        bool whole;
        if (primary.IsEmpty)
        {
            if (TextHelpers.WordAt(_doc, primary.Caret) is not { } w) return;
            start = w.Start;
            needle = _doc.GetText(w.Start, w.End - w.Start);
            whole = true;
        }
        else
        {
            start = primary.Start;
            needle = _doc.GetText(primary.Start, primary.Length);
            whole = _wordMode && needle == _wordModeText;
        }
        var text = DocText();
        var ranges = new List<CaretRange>();
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0 && ranges.Count < MaxCarets; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            if (whole && !TextHelpers.IsWholeWord(text, i, needle.Length)) continue;
            ranges.Add(new CaretRange(i, i + needle.Length, i == start));
        }
        if (ranges.Count == 0) return;
        if (!ranges.Any(r => r.IsPrimary))
        {
            var nearest = ranges.Select((r, i) => (D: Math.Abs(r.Start - start), I: i)).Min().I;
            var r = ranges[nearest];
            r.IsPrimary = true;
            ranges[nearest] = r;
        }
        Commit(ranges, reveal: false);
        _wordMode = whole;
        _wordModeText = needle;
    }

    /// <summary>Ctrl+Alt+Up / Down: a new caret on the line above the topmost (below the bottommost) caret.</summary>
    public void AddCaretVertical(int dir)
    {
        if (_doc is null) return;
        var all = All();
        var si = dir < 0 ? 0 : all.Count - 1;
        var src = all[si];
        var x = src.DesiredX;
        var target = MoveOffset(src.Caret, dir < 0 ? CaretMove.LineUp : CaretMove.LineDown, ref x);
        if (_doc.GetLineByOffset(target).LineNumber == _doc.GetLineByOffset(src.Caret).LineNumber || all.Count >= MaxCarets) return;
        for (var i = 0; i < all.Count; i++)
        {
            var r = all[i];
            r.IsPrimary = false;
            if (i == si) r.DesiredX = x;
            all[i] = r;
        }
        all.Add(new CaretRange(target, target, true) { DesiredX = x });
        Commit(all);
    }

    /// <summary>Alt+click: adds a caret at <paramref name="offset"/>, or removes the caret that is already there.</summary>
    public void ToggleCaretAt(int offset, CaretRange primary, List<CaretRange> extra)
    {
        var ranges = new List<CaretRange>(extra.Count + 2) { primary };
        ranges.AddRange(extra);
        var hit = ranges.FindIndex(r => r.IsEmpty ? r.Caret == offset : offset >= r.Start && offset <= r.End);
        if (hit >= 0)
        {
            if (ranges.Count > 1)
            {
                var wasPrimary = ranges[hit].IsPrimary;
                ranges.RemoveAt(hit);
                if (wasPrimary)
                {
                    var last = ranges[^1];
                    last.IsPrimary = true;
                    ranges[^1] = last;
                }
            }
        }
        else
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var r = ranges[i];
                r.IsPrimary = false;
                ranges[i] = r;
            }
            ranges.Add(new CaretRange(offset, offset, true));
        }
        Commit(ranges, reveal: false);
    }

    // ───────────── input ─────────────

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Handled || _doc is null || string.IsNullOrEmpty(e.Text) || _editor.IsReadOnly) return;
        if (e.Text.Any(char.IsControl) || _area.Selection is RectangleSelection) return;
        if (_extra.Count > 0)
        {
            TypeText(e.Text);
            e.Handled = true;
            return;
        }
        // one caret: only the special cases are ours, plain typing stays AvalonEdit's (overstrike, IME…)
        if (MultiCaret.Typing.Type(_doc, Primary(), e.Text, Typing).Special)
        {
            TypeText(e.Text);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _doc is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt) || mods.HasFlag(ModifierKeys.Windows)) return;
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var ctrl = mods.HasFlag(ModifierKeys.Control);

        if (_extra.Count == 0)
        {
            if (_editor.IsReadOnly || _area.Selection is RectangleSelection || ctrl) return;
            if (key == Key.Enter)
            {
                e.Handled = TypeText("\n");
            }
            else if (key == Key.Back && !shift && MultiCaret.Typing.DeletePair(_doc, Primary(), Typing) is not null)
            {
                e.Handled = Delete(backward: true, word: false);
            }
            return;
        }

        switch (key)
        {
            case Key.Left: Move(ctrl ? CaretMove.WordLeft : CaretMove.CharLeft, shift); break;
            case Key.Right: Move(ctrl ? CaretMove.WordRight : CaretMove.CharRight, shift); break;
            case Key.Up when !ctrl: Move(CaretMove.LineUp, shift); break;
            case Key.Down when !ctrl: Move(CaretMove.LineDown, shift); break;
            case Key.Home when !ctrl: Move(CaretMove.LineStart, shift); break;
            case Key.End when !ctrl: Move(CaretMove.LineEnd, shift); break;
            case Key.Back: Delete(backward: true, word: ctrl); break;
            case Key.Delete when !shift: Delete(backward: false, word: ctrl); break;
            case Key.Enter when !ctrl: TypeText("\n"); break;
            case Key.Tab when !ctrl:
                if (shift) Outdent();
                else InsertTab();
                break;
            case Key.PageUp or Key.PageDown or Key.Home or Key.End:
                Clear(); // page / document jumps continue with the primary caret only
                return;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Undo || e.Command == ApplicationCommands.Redo || e.Command == ApplicationCommands.SelectAll)
        {
            Clear();
            return;
        }
        if (_extra.Count == 0) return;
        if (e.Command == ApplicationCommands.Paste)
        {
            e.Handled = true;
            if (_editor.IsReadOnly) return;
            string? text = null;
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // clipboard busy: nothing to paste
            }
            if (text is not null) Paste(text);
        }
        else if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
        {
            if (SelectedTexts() is not { } text) return; // no selection: AvalonEdit copies the primary line
            e.Handled = true;
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return;
            }
            if (e.Command == ApplicationCommands.Cut) Edit((r, _) => r.IsEmpty ? null : new CaretEdit(r.Start, r.Length, "", 0, 0));
        }
    }

    private bool InTextView(MouseEventArgs e, out Point p)
    {
        var view = _area.TextView;
        p = e.GetPosition(view);
        return p.X >= 0 && p.Y >= 0 && p.X < view.ActualWidth && p.Y < view.ActualHeight;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!InTextView(e, out var p)) return;
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.ClickCount == 1)
        {
            // AvalonEdit starts a column selection; a click without dragging becomes "add / remove a caret" on mouse up
            _altClick = (p, Primary(), [.. _extra]);
            return;
        }
        _altClick = null;
        Clear();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_altClick is not { } click) return;
        _altClick = null;
        var p = e.GetPosition(_area.TextView);
        if (Math.Abs(p.X - click.Point.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(p.Y - click.Point.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            Clear(); // Alt+drag made a column selection
            return;
        }
        var offset = OffsetAt(click.Point);
        if (offset >= 0) ToggleCaretAt(offset, click.Primary, click.Extra);
    }

    /// <summary>Document offset under a point in TextView coordinates.</summary>
    public int OffsetAt(Point p)
    {
        if (_doc is null) return -1;
        var view = _area.TextView;
        var pos = view.GetPosition(p + view.ScrollOffset);
        return pos is { } tp ? _doc.GetOffset(tp.Location) : _doc.TextLength;
    }
}
