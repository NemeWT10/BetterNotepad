using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Tabular;
using BetterNotepad.Tabular.Wpf;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;
using ICSharpCode.AvalonEdit.Document;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Csv;

/// <summary>
/// <see cref="IPreviewView"/> around <see cref="CsvTablePreview"/>. The buffer is the source of truth: the table gets a copy of
/// the text only while it is visible (debounced by size), its cell edits come back as text edits that are applied to the buffer
/// as ONE undo step — and only when the buffer still holds exactly the text they were computed on. Ctrl+Z / Ctrl+Y in the
/// table undo / redo in the buffer; a locked file makes the table read only. Split view: the selected cell is selected in the
/// editor too, scrolling follows both ways. The dialect choice survives for the session per document; the parsed table is
/// released 20 s after the tab was hidden, at once in the tray.
/// </summary>
internal sealed class CsvPreviewView : IPreviewView, IReleasableView
{
    private static readonly Dictionary<Guid, CsvDialectOverride> Overrides = new();

    private readonly TextDocument _doc;
    private readonly CsvTablePreview _table;
    private Debouncer? _pull;
    private int _pullClass = -1;
    private Debouncer? _release;
    private bool _stale = true;
    private bool _released;
    private bool _applying;
    private bool _disposed;
    private bool _labelsStale;
    private string? _pushedText;
    private ITextSourceVersion? _pushedVersion;
    private long _ignoreSyncUntil;
    private long _lastBlockedToast;

    public CsvPreviewView(TextDocument doc)
    {
        _doc = doc;
        var editor = SettingsService.I.Get<EditorSettings>();
        _table = new CsvTablePreview(new CsvPreviewOptions
        {
            Labels = CsvStrings.Labels(),
            ReadOnly = doc.IsLocked,
            SetClipboard = SetClipboard,
            AnimationsEnabled = () => Motion.I.Enabled,
            MonospaceFont = $"{editor.FontFamily}, Cascadia Mono, Consolas",
        });
        if (Overrides.TryGetValue(doc.Id, out var chosen)) _table.Override = chosen;
        _table.LineRequested += OnLineRequested;
        _table.Scrolled += (line, fraction) => Scrolled?.Invoke(line, fraction);
        _table.EditRequested += ApplyEdits;
        _table.ActiveCellChanged += OnActiveCell;
        _table.UndoRequested += Undo;
        _table.EditBlocked += OnBlocked;
        _table.UnlockRequested += () => Owner?.SetLocked(_doc, false);
        _table.OverrideChanged += o =>
        {
            if (o.IsEmpty) Overrides.Remove(_doc.Id);
            else Overrides[_doc.Id] = o;
        };
        _table.Copied += n => Owner?.Toasts.Show(Loc.Plural("csv.copied", n), ToastKind.Success, seconds: 1.6);
        _table.IsVisibleChanged += OnVisibleChanged;
        _table.IsKeyboardFocusWithinChanged += OnFocusWithin;
        _table.Loaded += OnLoaded;
        _table.Unloaded += OnUnloaded;
        _doc.BufferChanged += OnBufferChanged;
        _doc.PropertyChanged += OnDocChanged;
    }

    public FrameworkElement Element => _table;

    public event Action<int>? LineRequested;

    public event Action<int, double>? Scrolled;

    /// <summary>For tests and snapshots.</summary>
    internal CsvTablePreview Table => _table;

    /// <summary>The parsed table (and its copy of the text) was dropped while hidden; the next show parses again (tests).</summary>
    internal bool IsReleased => _released;

    /// <summary>A copy of the buffer's text is scheduled for the table (tests).</summary>
    internal bool PullPending => _pull?.IsPending == true;

    /// <summary>Clipboard text of the last copy in test mode (the real clipboard is never touched there).</summary>
    internal static string? LastTestCopy { get; private set; }

    public void ScrollToLine(int line, double lineFraction, double scrollFraction)
    {
        // our own caret move scrolled the editor: do not let that pull the table away from the row the user picked
        if (Environment.TickCount64 < _ignoreSyncUntil) return;
        _table.ScrollToLine(line, lineFraction, scrollFraction);
    }

    // ───────────── buffer → table ─────────────

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        if (_applying || _disposed) return;
        if (_table.IsVisible) SchedulePull();
        else _stale = true; // hidden (Text mode, other tab): no copy of the text per keystroke
    }

    private void SchedulePull()
    {
        // a copy of a big text costs: wait a little longer between copies the bigger it is
        var length = _doc.HasBuffer ? _doc.Buffer.TextLength : 0;
        var cls = length < 512 * 1024 ? 0 : length < 8 * 1024 * 1024 ? 1 : 2;
        if (cls != _pullClass || _pull is null)
        {
            _pull?.Cancel();
            _pullClass = cls;
            _pull = new Debouncer(TimeSpan.FromMilliseconds(cls switch { 0 => 70, 1 => 200, _ => 450 }), () => PushText(false));
        }
        _pull.Trigger();
    }

    /// <summary>Hands the table the buffer's current text (and remembers which version that was).</summary>
    internal void PushText(bool immediate)
    {
        if (_disposed || !_doc.HasBuffer) return;
        _pull?.Cancel();
        _stale = false;
        _released = false;
        var buffer = _doc.Buffer;
        var text = buffer.Text;
        _pushedVersion = buffer.Version;
        _table.Update(text, _doc.NameForType, immediate);
        _pushedText = _table.Text; // the table may keep its equal older copy: compare by reference later
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (_table.IsVisible)
        {
            _release?.Cancel();
            if (_labelsStale)
            {
                _labelsStale = false;
                _table.Labels = CsvStrings.Labels();
            }
            if (_stale || _released || _pushedText is null) PushText(immediate: true);
            return;
        }
        if (_pull?.IsPending == true)
        {
            _pull.Cancel();
            _stale = true;
        }
        // hidden for a while → free the parsed table and its copy of the text (parsed again when shown)
        (_release ??= new Debouncer(TimeSpan.FromSeconds(20), ReleaseIfHidden)).Trigger();
    }

    private void ReleaseIfHidden()
    {
        if (_disposed || _table.IsVisible || _released) return;
        _table.ReleaseContent(dropText: true);
        _pushedText = null;
        _pushedVersion = null;
        _released = true;
        _stale = true;
    }

    /// <summary>The window went to the tray: drop the table now.</summary>
    public void ReleaseRendering()
    {
        _release?.Cancel();
        if (_pull?.IsPending == true)
        {
            _pull.Cancel();
            _stale = true;
        }
        ReleaseIfHidden();
    }

    private void OnFocusWithin(object sender, DependencyPropertyChangedEventArgs e)
    {
        // the user turns to the table: it must show the latest text before a cell is edited
        if (_table.IsKeyboardFocusWithin && _pull?.IsPending == true) PushText(immediate: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Loc.LanguageChanged += OnLanguageChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) => Loc.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged()
    {
        if (_table.IsVisible) _table.Labels = CsvStrings.Labels();
        else _labelsStale = true;
    }

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Document.IsLocked):
                _table.IsReadOnly = _doc.IsLocked;
                break;
            case nameof(Document.FilePath):
                if (_table.IsVisible) PushText(immediate: true); // renamed: .tsv / .psv may mean another separator
                else _stale = true;
                break;
        }
    }

    // ───────────── table → buffer ─────────────

    private void ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        if (_disposed || !_doc.HasBuffer || edits.Count == 0) return;
        var buffer = _doc.Buffer;
        if (_doc.IsLocked)
        {
            OnBlocked(CsvEditBlock.Locked);
            PushText(immediate: true); // the table shows the edit already: back to the file's text
            return;
        }
        var baseText = _table.LastEditBase;
        var current = _pushedVersion is not null && buffer.Version is { } v && v.BelongsToSameDocumentAs(_pushedVersion) && v.CompareAge(_pushedVersion) == 0
                      && ReferenceEquals(baseText, _pushedText);
        if (!current) current = baseText is not null && buffer.TextLength == baseText.Length && buffer.Text == baseText;
        if (!current)
        {
            // the buffer moved on (typed in the editor a moment ago): edits against an older copy would land in the wrong place
            Log.Info("csv: cell edit dropped, the text changed meanwhile");
            OnBlocked(CsvEditBlock.Stale);
            PushText(immediate: true);
            return;
        }

        _applying = true;
        try
        {
            buffer.BeginUpdate(); // one undo step
            try
            {
                foreach (var edit in edits.OrderByDescending(x => x.Offset)) buffer.Replace(edit.Offset, edit.Length, edit.Replacement);
            }
            finally
            {
                buffer.EndUpdate();
            }
        }
        finally
        {
            _applying = false;
        }

        _pushedText = _table.Text;
        _pushedVersion = buffer.Version;
        if (buffer.TextLength != _pushedText.Length)
        {
            Log.Error("csv: buffer and table disagree after a cell edit; reloading the table");
            PushText(immediate: true);
        }
    }

    private void Undo(bool redo)
    {
        if (_disposed || !_doc.HasBuffer) return;
        if (_doc.IsLocked)
        {
            OnBlocked(CsvEditBlock.Locked);
            return;
        }
        var buffer = _doc.Buffer;
        var stack = buffer.UndoStack;
        if (redo ? !stack.CanRedo : !stack.CanUndo) return;
        var first = -1;
        void OnChanged(object? s, DocumentChangeEventArgs e)
        {
            if (first < 0 || e.Offset < first) first = e.Offset;
        }
        buffer.Changed += OnChanged;
        try
        {
            if (redo) stack.Redo();
            else stack.Undo();
        }
        finally
        {
            buffer.Changed -= OnChanged;
        }
        PushText(immediate: true);
        if (first >= 0) _table.SelectAtOffset(first); // show where the change was
    }

    private void OnBlocked(CsvEditBlock why)
    {
        var now = Environment.TickCount64;
        if (now - _lastBlockedToast < 1200) return; // typing into a locked table: one note, not one per key
        _lastBlockedToast = now;
        var toasts = Owner?.Toasts;
        if (toasts is null) return;
        switch (why)
        {
            case CsvEditBlock.Locked:
                toasts.Show(Loc.T("csv.locked"), ToastKind.Warning, Loc.T("csv.unlock"), () => Owner?.SetLocked(_doc, false));
                break;
            case CsvEditBlock.UnclosedQuote:
                toasts.Show(Loc.T("csv.unclosedEdit"), ToastKind.Warning);
                break;
            case CsvEditBlock.Stale:
                toasts.Show(Loc.T("csv.stale"), ToastKind.Info);
                break;
        }
    }

    private static bool SetClipboard(string text)
    {
        if (AppEnv.TestMode)
        {
            LastTestCopy = text; // never the user's clipboard from a test instance
            return true;
        }
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Error("csv: clipboard busy", ex);
            return false;
        }
    }

    // ───────────── table ↔ editor (split view) ─────────────

    private MainWindow? Owner => Window.GetWindow(_table) as MainWindow;

    private CodeEditor? Editor()
    {
        DependencyObject? d = _table;
        while (d is not null)
        {
            if (d is TextDocumentView view) return view.Editor;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>A row picked in the table is picked in the text too: the cell's characters selected, scrolled into view if needed.</summary>
    private void OnActiveCell(int offset, int length)
    {
        if (_doc.ViewMode != ViewMode.Split || _disposed) return;
        var editor = Editor();
        if (editor?.Document is not { } document || !editor.IsVisible || offset < 0 || offset + length > document.TextLength) return;
        _ignoreSyncUntil = Environment.TickCount64 + 400;
        editor.Select(offset, length);
        var location = document.GetLocation(offset);
        var view = editor.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(location.Line);
        if (top < editor.VerticalOffset || top + view.DefaultLineHeight > editor.VerticalOffset + editor.ViewportHeight)
            editor.ScrollTo(location.Line, location.Column);
    }

    private void OnLineRequested(int line)
    {
        if (_doc.ViewMode == ViewMode.Preview)
        {
            // "show in text" from the table alone: open the split, then go there once the editor is laid out
            _doc.ViewMode = ViewMode.Split;
            _table.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LineRequested?.Invoke(line));
            return;
        }
        LineRequested?.Invoke(line);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pull?.Cancel();
        _release?.Cancel();
        _doc.BufferChanged -= OnBufferChanged;
        _doc.PropertyChanged -= OnDocChanged;
        _table.IsVisibleChanged -= OnVisibleChanged;
        _table.IsKeyboardFocusWithinChanged -= OnFocusWithin;
        Loc.LanguageChanged -= OnLanguageChanged;
        _table.Dispose();
    }
}
