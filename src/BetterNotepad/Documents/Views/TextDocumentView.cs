using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Editor;
using BetterNotepad.Editor.Find;
using BetterNotepad.UI;

namespace BetterNotepad.Documents.Views;

/// <summary>
/// Editor (+ live preview for Markdown/HTML/SVG/CSV). Layout follows the user's sketch: preview on
/// the left, editor on the right (swappable), Preview | Split | Edit per tab.
/// </summary>
public sealed class TextDocumentView : Grid, IDocumentView, IReleasableView
{
    private readonly TextDocument _doc;
    private readonly Grid _content = new();
    private readonly Grid _editorHost = new();
    private readonly Border _previewHost = new();
    private readonly GridSplitter _splitter;
    private readonly TextBlock _loading;
    private CodeEditor? _editor;
    private FindBar? _find;
    private IPreviewView? _preview;
    private bool _syncingScroll;
    private double _expectedEditorOffset = double.NaN;
    private double _ratio = 0.5;
    private Debouncer? _scrollSync;

    public TextDocumentView(TextDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");

        _splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeWE,
            Focusable = false,
        };
        _splitter.Template = SplitterTemplate();
        _splitter.DragCompleted += (_, _) => CaptureRatio();

        _loading = new TextBlock
        {
            Text = Loc.T("view.loading"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        _loading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");

        Children.Add(_content);
        Children.Add(_loading);

        doc.PropertyChanged += OnDocChanged;
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguage;
        Loaded += (_, _) => Loc.LanguageChanged += OnLanguage;
    }

    public Document Document => _doc;

    public FrameworkElement Element => this;

    public CodeEditor? Editor => _editor;

    public FindBar? Find => _find;

    /// <summary>The live preview next to the editor, if this document type has one.</summary>
    public IPreviewView? Preview => _preview;

    private void OnLanguage() => _loading.Text = Loc.T("view.loading");

    private static ControlTemplate SplitterTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(Border.WidthProperty, 1.0);
        inner.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        inner.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        factory.AppendChild(inner);
        return new ControlTemplate(typeof(GridSplitter)) { VisualTree = factory };
    }

    public async void OnActivated()
    {
        if (!_doc.IsLoaded)
        {
            _loading.Visibility = Visibility.Visible;
            try
            {
                await _doc.LoadAsync();
                DocumentManager.RefineLanguage(_doc);
            }
            catch (Exception ex)
            {
                Log.Error($"load failed: {_doc.FilePath}", ex);
                _loading.Text = Loc.T("view.loadFailed", ex.Message);
                return;
            }
        }
        _loading.Visibility = Visibility.Collapsed;
        EnsureEditor();
        ApplyLayout();
    }

    public void OnDeactivated() => CaptureState();

    private void EnsureEditor()
    {
        if (_editor is not null) return;
        _editor = new CodeEditor();
        _editor.Attach(_doc);
        _find = new FindBar();
        _find.Attach(_editor);
        _editorHost.Children.Add(_editor);
        _editorHost.Children.Add(_find);
        _editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        _editor.TextArea.Caret.PositionChanged += (_, _) => CaretMoved?.Invoke();
        _editor.TextArea.SelectionChanged += (_, _) => CaretMoved?.Invoke();

        RestoreState();
    }

    /// <summary>Caret or selection moved (status bar).</summary>
    public event Action? CaretMoved;

    private void RestoreState()
    {
        if (_editor is null) return;
        var len = _doc.Buffer.TextLength;
        _editor.CaretOffset = Math.Clamp(_doc.CaretOffset, 0, len);
        if (_doc.SelectionLength > 0 && _doc.SelectionStart + _doc.SelectionLength <= len)
            _editor.Select(_doc.SelectionStart, _doc.SelectionLength);
        var y = _doc.ScrollY;
        var x = _doc.ScrollX;
        if (y > 0 || x > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _editor.ScrollToVerticalOffset(y);
                _editor.ScrollToHorizontalOffset(x);
            });
    }

    public void CaptureState()
    {
        if (_editor is null) return;
        _doc.CaretOffset = _editor.CaretOffset;
        _doc.SelectionStart = _editor.SelectionStart;
        _doc.SelectionLength = _editor.SelectionLength;
        _doc.ScrollY = _editor.VerticalOffset;
        _doc.ScrollX = _editor.HorizontalOffset;
    }

    public void FocusContent()
    {
        if (_editor is not null && _editor.IsVisible) _editor.Focus();
        else _preview?.Element.Focus();
    }

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextDocument.ViewMode) or nameof(TextDocument.Kind)) ApplyLayout();
    }

    /// <summary>Builds the editor / preview columns for the current view mode.</summary>
    private void ApplyLayout()
    {
        if (_editor is null) return;
        var hasPreview = FileTypes.HasPreview(_doc.Kind) && DocumentViewRegistry.HasPreview(_doc.Kind);
        if (hasPreview && _preview is null)
        {
            _preview = DocumentViewRegistry.CreatePreview(_doc);
            if (_preview is not null)
            {
                _previewHost.Child = _preview.Element;
                _preview.LineRequested += line => _editor?.GoTo(line);
                _preview.Scrolled += OnPreviewScrolled;
            }
        }
        else if (!hasPreview && _preview is not null)
        {
            _previewHost.Child = null;
            _preview.Dispose();
            _preview = null;
        }

        var mode = _preview is null ? ViewMode.Edit : _doc.ViewMode;
        var previewLeft = SettingsService.I.Get<FilesSettings>().PreviewOnLeft;

        _content.Children.Clear();
        _content.ColumnDefinitions.Clear();
        if (mode == ViewMode.Edit || _preview is null)
        {
            _content.Children.Add(_editorHost);
            return;
        }
        if (mode == ViewMode.Preview)
        {
            _content.Children.Add(_previewHost);
            // keep the editor alive (find, undo) but out of sight
            return;
        }

        var first = previewLeft ? (UIElement)_previewHost : _editorHost;
        var second = previewLeft ? (UIElement)_editorHost : _previewHost;
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_ratio, GridUnitType.Star), MinWidth = 120 });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - _ratio, GridUnitType.Star), MinWidth = 120 });
        SetColumn(first, 0);
        SetColumn(_splitter, 1);
        SetColumn(second, 2);
        _content.Children.Add(first);
        _content.Children.Add(_splitter);
        _content.Children.Add(second);
    }

    private void CaptureRatio()
    {
        if (_content.ColumnDefinitions.Count != 3) return;
        var a = _content.ColumnDefinitions[0].ActualWidth;
        var b = _content.ColumnDefinitions[2].ActualWidth;
        if (a + b > 0) _ratio = a / (a + b);
    }

    private void OnEditorScrolled(object? sender, EventArgs e)
    {
        if (_preview is null || _syncingScroll || _doc.ViewMode != ViewMode.Split || _editor is null) return;
        if (!double.IsNaN(_expectedEditorOffset))
        {
            // our own scroll from the preview: do not bounce it back
            var ours = Math.Abs(_editor.VerticalOffset - _expectedEditorOffset) < 1.5;
            _expectedEditorOffset = double.NaN;
            if (ours) return;
        }
        (_scrollSync ??= new Debouncer(TimeSpan.FromMilliseconds(30), SyncPreview, DispatcherPriority.Render)).Trigger();
    }

    private void SyncPreview()
    {
        if (_editor is null || _preview is null) return;
        var view = _editor.TextArea.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0) return;
        var first = view.VisualLines[0];
        var line = first.FirstDocumentLine.LineNumber;
        var lineFraction = first.Height > 0 ? Math.Clamp((view.VerticalOffset - first.VisualTop) / first.Height, 0, 1) : 0;
        var max = Math.Max(1, _editor.ExtentHeight - _editor.ViewportHeight);
        _syncingScroll = true;
        _preview.ScrollToLine(line, lineFraction, Math.Clamp(_editor.VerticalOffset / max, 0, 1));
        _syncingScroll = false;
    }

    /// <summary>The user scrolled the preview: bring the same source line to the top of the editor.</summary>
    private void OnPreviewScrolled(int line, double fraction)
    {
        if (_editor?.Document is null || _syncingScroll || _doc.ViewMode != ViewMode.Split) return;
        line = Math.Clamp(line, 1, _editor.Document.LineCount);
        var view = _editor.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(line);
        var next = line < _editor.Document.LineCount ? view.GetVisualTopByDocumentLine(line + 1) : top + view.DefaultLineHeight;
        var target = Math.Max(0, top + (next - top) * Math.Clamp(fraction, 0, 1));
        if (Math.Abs(target - _editor.VerticalOffset) < 1) return;
        _scrollSync?.Cancel();
        _expectedEditorOffset = target;
        _editor.ScrollToVerticalOffset(target);
    }

    /// <summary>Tray: the preview drops its rendered document (the editor itself stays as it is).</summary>
    public void ReleaseRendering() => (_preview as IReleasableView)?.ReleaseRendering();

    public void Dispose()
    {
        CaptureState();
        _doc.PropertyChanged -= OnDocChanged;
        _preview?.Dispose();
        if (_editor is not null)
        {
            _editor.TextArea.TextView.ScrollOffsetChanged -= OnEditorScrolled;
            _editor.Document = null;
        }
    }
}
