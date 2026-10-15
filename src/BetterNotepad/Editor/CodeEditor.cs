using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor;

/// <summary>
/// AvalonEdit with BetterNotepad's look and settings. One per view; several editors may share one
/// <see cref="TextDocument.Buffer"/> (same file shown twice).
/// </summary>
public class CodeEditor : TextEditor
{
    private TextDocument? _model;

    public CodeEditor()
    {
        var o = Options;
        o.EnableRectangularSelection = true;
        o.EnableTextDragDrop = true;
        o.CutCopyWholeLine = true;
        o.AllowScrollBelowDocument = true;
        o.EnableHyperlinks = true;
        o.EnableEmailHyperlinks = false;
        o.RequireControlModifierForHyperlinkClick = true;
        o.EnableImeSupport = true;
        o.HighlightCurrentLine = true;
        o.InheritWordWrapIndentation = true;
        o.EnableVirtualSpace = false;

        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(0, 6, 0, 0); // air below the tab strip before the first line
        TextArea.SelectionCornerRadius = 3;
        TextArea.SelectionBorder = null;
        TextArea.SelectionForeground = null;
        TextArea.TextView.Margin = new Thickness(6, 0, 0, 0);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // our own gutter (bookmarks | line numbers | fold chevrons), multi-caret and decorations
        ShowLineNumbers = false;
        Features = new EditorFeatures(this);
        TextArea.TextView.BackgroundRenderers.Add(new EmptyHintRenderer(this));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseWheel += OnPreviewMouseWheel;

        // filled by the window each time it opens (commands, shortcuts, enabled state)
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, e) =>
        {
            if (Window.GetWindow(this) is UI.Windows.MainWindow w) w.FillEditorMenu(this, ContextMenu);
            else e.Handled = true;
        };
    }

    /// <summary>The document model this editor shows.</summary>
    public TextDocument? Model => _model;

    /// <summary>Multi-caret, folding, bookmarks, occurrences, bracket match, indent guides.</summary>
    public EditorFeatures Features { get; }

    public void Attach(TextDocument model)
    {
        _model = model;
        Document = model.Buffer;
        IsReadOnly = model.IsLocked;
        ApplySettings();
        ApplyLanguage();
        Features.Attach();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.I.Changed += OnThemeChanged;
        HighlightingProvider.Invalidated += ApplyLanguage;
        SettingsService.I.Changed += OnSettingsChanged;
        Core.Localization.Loc.LanguageChanged += OnUiLanguageChanged;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            _model.LanguageChanged += ApplyLanguage;
        }
        ApplyTheme();
        ApplySettings();
        ApplyLanguage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.I.Changed -= OnThemeChanged;
        HighlightingProvider.Invalidated -= ApplyLanguage;
        SettingsService.I.Changed -= OnSettingsChanged;
        Core.Localization.Loc.LanguageChanged -= OnUiLanguageChanged;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.LanguageChanged -= ApplyLanguage;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TextDocument.IsLocked):
                IsReadOnly = _model!.IsLocked;
                break;
            case nameof(TextDocument.Kind) or nameof(TextDocument.IsLarge):
                ApplySettings();
                ApplyLanguage();
                break;
        }
    }

    private void OnUiLanguageChanged() => TextArea.TextView.InvalidateLayer(KnownLayer.Background); // empty-tab hint

    private void OnThemeChanged()
    {
        ApplyTheme();
        ApplyLanguage();
    }

    private void OnSettingsChanged(SettingsSection section, string? property)
    {
        if (section is EditorSettings) ApplySettings();
    }

    public void ApplyTheme()
    {
        var p = ThemeManager.I.Current;
        Background = Brush(p.EditorBg);
        Foreground = Brush(p.EditorText);
        LineNumbersForeground = Brush(p.LineNumber);
        TextArea.SelectionBrush = Brush(p.Selection);
        TextArea.Caret.CaretBrush = Brush(p.Caret);
        TextArea.TextView.CurrentLineBackground = Brush(p.CurrentLine);
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        TextArea.TextView.LinkTextForegroundBrush = Brush(ThemeManager.I.Syntax[SyntaxToken.Link].Color);
        TextArea.TextView.LinkTextUnderline = true;
        TextArea.TextView.NonPrintableCharacterBrush = Brush(p.Whitespace);
        Features.ApplyTheme();
        TextArea.TextView.Redraw();
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void ApplySettings()
    {
        var s = SettingsService.I.Get<EditorSettings>();
        var isCode = _model is null || Languages.Get(_model.LanguageId).IsCode;
        var o = Options;
        o.IndentationSize = s.TabSize;
        o.ConvertTabsToSpaces = s.InsertSpaces;
        o.HighlightCurrentLine = s.HighlightCurrentLine;
        o.ShowTabs = s.ShowWhitespace;
        o.ShowSpaces = s.ShowWhitespace;
        o.ShowBoxForControlCharacters = true;
        WordWrap = isCode ? s.WordWrapCode : s.WordWrapText;
        if (_model?.IsLarge == true) WordWrap = false;
        FontFamily = new FontFamily($"{s.FontFamily}, Cascadia Mono, Cascadia Code, Consolas, Courier New");
        FontSize = s.FontSize * 96.0 / 72.0 * s.Zoom;
        Features.ApplySettings(s);
    }

    public void ApplyLanguage()
    {
        if (_model is null) return;
        SyntaxHighlighting = _model.IsLarge ? null : HighlightingProvider.ForLanguage(_model.LanguageId);
        Features.ApplyLanguage();
    }

    /// <summary>Ctrl+wheel zooms (persisted for all editors).</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        Zoom(e.Delta > 0 ? 0.1 : -0.1);
    }

    public static void Zoom(double delta)
    {
        var s = SettingsService.I.Get<EditorSettings>();
        s.Zoom = Math.Round(s.Zoom + delta, 2);
    }

    public static void ResetZoom() => SettingsService.I.Get<EditorSettings>().Zoom = 1.0;

    /// <summary>Moves the caret to a 1-based line (and column) and centers it.</summary>
    public void GoTo(int line, int column = 1)
    {
        if (Document is null) return;
        line = Math.Clamp(line, 1, Document.LineCount);
        var docLine = Document.GetLineByNumber(line);
        column = Math.Clamp(column, 1, docLine.Length + 1);
        TextArea.Caret.Location = new ICSharpCode.AvalonEdit.Document.TextLocation(line, column);
        TextArea.ClearSelection();
        ScrollTo(line, column, VisualYPosition.LineMiddle, ViewportHeight / 2, 0.3);
        TextArea.Caret.BringCaretToView();
        Focus();
    }

    public TextArea Area => TextArea;
}
