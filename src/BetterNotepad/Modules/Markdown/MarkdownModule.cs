using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Markdown;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;
using BetterNotepad.UI.Windows;
using ICSharpCode.AvalonEdit.Highlighting;
using AvalonDocument = ICSharpCode.AvalonEdit.Document.TextDocument;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Markdown;

/// <summary>Markdown module: native preview (BetterNotepad.Markdown) next to the editor.</summary>
public static class MarkdownModule
{
    public static void Register() => DocumentViewRegistry.RegisterPreview(DocumentKind.Markdown, doc => new MarkdownPreviewView(doc));
}

internal static class MarkdownStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["md.copy"] = ("Kopiuj", "Copy"),
        ["md.copied"] = ("Skopiowano", "Copied"),
        ["md.selectAll"] = ("Zaznacz wszystko", "Select all"),
        ["md.remoteImage"] = ("Obraz z sieci (niewczytany)", "Remote image (not loaded)"),
        ["md.imageNotFound"] = ("Nie znaleziono obrazu", "Image not found"),
        ["md.imageUnsupported"] = ("Nieobsługiwany format obrazu", "Image format not supported"),
        ["md.backToText"] = ("Wróć do tekstu", "Back to text"),
        ["md.alert.note"] = ("Uwaga", "Note"),
        ["md.alert.tip"] = ("Wskazówka", "Tip"),
        ["md.alert.important"] = ("Ważne", "Important"),
        ["md.alert.warning"] = ("Ostrzeżenie", "Warning"),
        ["md.alert.caution"] = ("Ostrożnie", "Caution"),
        ["md.toc"] = ("Spis treści", "Contents"),
        ["md.tocToggle"] = ("Spis treści (pokaż / ukryj)", "Table of contents (show / hide)"),
        ["md.tocEmpty"] = ("Brak nagłówków", "No headings yet"),
        ["md.linkNotFound"] = ("Nie znaleziono: {0}", "Not found: {0}"),
        ["md.linkBlocked"] = ("Tego linku nie można otworzyć: {0}", "This link can’t be opened: {0}"),
        ["md.lockedTask"] = ("Plik jest zablokowany — odblokuj go, aby odhaczać zadania.", "The file is locked — unlock it to tick tasks."),
    });
}

/// <summary>
/// <see cref="IPreviewView"/> around <see cref="MarkdownPreview"/>: live update from the buffer (only while visible),
/// two-way scroll sync, clickable task boxes (one undo step), links (files → tab, web → system browser),
/// code blocks colored by the editor's highlighting definitions, a table of contents on the side,
/// theme / language follow-up, and release of the rendered document while hidden.
/// </summary>
internal sealed class MarkdownPreviewView : IPreviewView, IReleasableView
{
    private static bool _tocOpen; // remembered for the session, shared by all Markdown tabs
    private static readonly Regex TaskBox = new(@"^(\s*(?:>\s*)*(?:[-*+]|\d{1,9}[.)])\s+\[)([ xX])\]", RegexOptions.Compiled);

    private readonly TextDocument _doc;
    private readonly MarkdownPreview _md;
    private readonly Grid _root = new();
    private readonly Border _tocPanel = new();
    private readonly StackPanel _tocList = new() { Margin = new Thickness(8, 4, 8, 16) };
    private readonly TextBlock _tocTitle = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 14, 12, 6) };
    private readonly Button _tocButton;
    private bool _bufferStale = true;
    private bool _optionsStale;
    private Debouncer? _release;
    private bool _released;
    private bool _disposed;

    public MarkdownPreviewView(TextDocument doc)
    {
        _doc = doc;
        _md = new MarkdownPreview(BuildOptions());
        _md.LineRequested += line => LineRequested?.Invoke(line);
        _md.TopLineChanged += (line, fraction) => Scrolled?.Invoke(line, fraction);
        _md.TaskToggled += OnTaskToggled;
        _md.LinkClicked += OnLinkClicked;
        _md.HeadingsChanged += RebuildToc;

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.SetResourceReference(Panel.BackgroundProperty, "Brush.Editor.Bg");
        _root.Children.Add(_md);

        // table of contents: a quiet column on the right, toggled by a small button in the corner
        var tocScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var tocStack = new StackPanel();
        tocStack.Children.Add(_tocTitle);
        tocStack.Children.Add(_tocList);
        tocScroll.Content = tocStack;
        _tocTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _tocPanel.Width = 232;
        _tocPanel.BorderThickness = new Thickness(1, 0, 0, 0);
        _tocPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _tocPanel.SetResourceReference(Border.BackgroundProperty, "Brush.Md.TableStripe");
        _tocPanel.Child = tocScroll;
        _tocPanel.Visibility = _tocOpen ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_tocPanel, 1);
        _root.Children.Add(_tocPanel);

        _tocButton = new Button
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 14, 0),
            Content = new Icon { Glyph = Glyphs.List, FontSize = 13 },
        };
        _tocButton.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        _tocButton.Click += (_, _) => ToggleToc();
        _root.Children.Add(_tocButton);
        UpdateTocButton();

        _doc.BufferChanged += OnBufferChanged;
        _root.IsVisibleChanged += OnVisibleChanged;
        _root.Loaded += OnLoaded;
        _root.Unloaded += OnUnloaded;
        ApplyTexts();
    }

    public FrameworkElement Element => _root;

    public event Action<int>? LineRequested;

    public event Action<int, double>? Scrolled;

    /// <summary>For tests and snapshots.</summary>
    internal MarkdownPreview Preview => _md;

    internal bool TocOpen => _tocPanel.Visibility == Visibility.Visible;

    /// <summary>The rendered document was dropped (hidden in the tray / for a while); the next show renders again (tests).</summary>
    internal bool IsReleased => _released;

    public void ScrollToLine(int line, double lineFraction, double scrollFraction) => _md.ScrollToLine(line, lineFraction, scrollFraction);

    // ───────────── content ─────────────

    private string? BaseFolder => _doc.FilePath is { } p ? Path.GetDirectoryName(p) : null;

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        if (_root.IsVisible) PushText();
        else _bufferStale = true; // hidden (Edit mode, other tab): no copy of the text per keystroke
    }

    private void PushText()
    {
        if (_disposed || !_doc.HasBuffer) return;
        _bufferStale = false;
        _released = false;
        _md.Update(_doc.Buffer.Text, BaseFolder);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (_root.IsVisible)
        {
            _release?.Cancel();
            if (_optionsStale)
            {
                _optionsStale = false;
                ApplyTexts();
                if (_bufferStale) PushText();
                _md.Refresh();
            }
            else if (_bufferStale)
            {
                PushText();
            }
        }
        else
        {
            // hidden for a while → free the rendered document and images (re-rendered when shown again)
            (_release ??= new Debouncer(TimeSpan.FromSeconds(20), ReleaseIfHidden)).Trigger();
        }
    }

    private void ReleaseIfHidden()
    {
        if (_disposed || _root.IsVisible || _released) return;
        // the next render comes back to the same reading position (Preview mode has no editor to sync it from)
        var line = _md.GetTopLine(out var fraction);
        _md.ReleaseContent();
        if (line > 1 || fraction > 0) _md.ScrollToLine(line, fraction);
        _bufferStale = true;
        _released = true;
    }

    /// <summary>The window went to the tray: free the rendered document now (instead of 20 s later, after the memory trim).</summary>
    public void ReleaseRendering()
    {
        _release?.Cancel();
        ReleaseIfHidden();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.I.Changed += OnThemeChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        if (_bufferStale) PushText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.I.Changed -= OnThemeChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnThemeChanged()
    {
        // code colors use Brush.Syntax.* references, but weights/italics and the definitions are per theme
        if (_root.IsVisible) _md.Refresh();
        else _optionsStale = true;
    }

    private void OnLanguageChanged()
    {
        ApplyTexts();
        if (_root.IsVisible) _md.Refresh();
        else _optionsStale = true;
    }

    private void ApplyTexts()
    {
        _md.Options.Labels = Labels();
        _tocTitle.Text = Loc.T("md.toc").ToUpper(Loc.I.Culture);
        _tocButton.ToolTip = Loc.T("md.tocToggle");
        RebuildToc();
    }

    private static MarkdownLabels Labels() => new()
    {
        Copy = Loc.T("md.copy"),
        Copied = Loc.T("md.copied"),
        SelectAll = Loc.T("md.selectAll"),
        RemoteImage = Loc.T("md.remoteImage"),
        ImageNotFound = Loc.T("md.imageNotFound"),
        ImageUnsupported = Loc.T("md.imageUnsupported"),
        BackToReference = Loc.T("md.backToText"),
    };

    private static MarkdownOptions BuildOptions()
    {
        var editor = SettingsService.I.Get<EditorSettings>();
        return new MarkdownOptions
        {
            Labels = Labels(),
            AlertTitle = k => Loc.T("md.alert." + k.ToString().ToLowerInvariant()),
            Highlighter = CodeHighlighter.Highlight,
            CodeFont = $"{editor.FontFamily}, Cascadia Mono, Consolas",
        };
    }

    // ───────────── table of contents ─────────────

    internal void ToggleToc()
    {
        _tocOpen = !TocOpen;
        _tocPanel.Visibility = _tocOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateTocButton();
        if (_tocOpen)
        {
            RebuildToc();
            Motion.SlideIn(_tocPanel, fromX: 16, ms: 220);
        }
        Motion.Bounce((UIElement)_tocButton.Content, 1.15);
    }

    /// <summary>The corner button wears the accent while the contents panel is open.</summary>
    private void UpdateTocButton()
    {
        if (_tocButton.Content is not Icon icon) return;
        if (TocOpen) icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        else icon.ClearValue(TextBlock.ForegroundProperty);
    }

    private void RebuildToc()
    {
        if (!TocOpen) return;
        _tocList.Children.Clear();
        var headings = _md.Headings;
        if (headings.Count == 0)
        {
            var empty = new TextBlock { Text = Loc.T("md.tocEmpty"), Margin = new Thickness(10, 4, 0, 0), FontSize = 12 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            _tocList.Children.Add(empty);
            return;
        }
        var minLevel = headings.Min(h => h.Level);
        foreach (var h in headings)
        {
            var depth = Math.Clamp(h.Level - minLevel, 0, 4);
            var text = new TextBlock
            {
                Text = h.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = depth == 0 ? 12.5 : 12,
                FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var item = new Button
            {
                Content = text,
                ToolTip = h.Text,
                Padding = new Thickness(10 + depth * 12, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            item.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
            var heading = h;
            item.Click += (_, _) => _md.ScrollToHeading(heading);
            _tocList.Children.Add(item);
        }
    }

    // ───────────── interaction ─────────────

    /// <summary>For tests: as if the user clicked a task box / a link.</summary>
    internal void SimulateTask(int line, bool isChecked) => OnTaskToggled(line, isChecked);

    internal void SimulateLink(string url) => OnLinkClicked(url);

    private void OnTaskToggled(int line, bool isChecked)
    {
        if (!_doc.HasBuffer) return;
        var buffer = _doc.Buffer;
        if (_doc.IsLocked || line < 1 || line > buffer.LineCount)
        {
            if (_doc.IsLocked) Owner?.Toasts.Show(Loc.T("md.lockedTask"), ToastKind.Warning);
            _md.Refresh(); // put the box back
            return;
        }
        var docLine = buffer.GetLineByNumber(line);
        var m = TaskBox.Match(buffer.GetText(docLine));
        if (!m.Success)
        {
            _md.Refresh();
            return;
        }
        var offset = docLine.Offset + m.Groups[2].Index;
        var mark = isChecked ? "x" : " ";
        if (buffer.GetText(offset, 1) == mark) return;
        buffer.BeginUpdate(); // one undo step
        try
        {
            buffer.Replace(offset, 1, mark);
        }
        finally
        {
            buffer.EndUpdate();
        }
    }

    private MainWindow? Owner => Window.GetWindow(_root) as MainWindow;

    private void OnLinkClicked(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !abs.IsFile)
        {
            if (abs.Scheme is "http" or "https" or "mailto") Shell(abs.AbsoluteUri);
            else Owner?.Toasts.Show(Loc.T("md.linkBlocked", url), ToastKind.Warning);
            return;
        }

        // a file next to this one: open it in a tab (fragment and query dropped)
        string path;
        try
        {
            if (abs is { IsFile: true }) path = abs.LocalPath;
            else
            {
                var rel = Uri.UnescapeDataString(url.Split('#', '?')[0]).Replace('/', Path.DirectorySeparatorChar);
                if (rel.Length == 0) return;
                path = Path.IsPathRooted(rel) || BaseFolder is null ? rel : Path.GetFullPath(Path.Combine(BaseFolder, rel));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Owner?.Toasts.Show(Loc.T("md.linkNotFound", url), ToastKind.Warning);
            return;
        }

        if (File.Exists(path)) Owner?.OpenFile(path);
        else if (Directory.Exists(path)) Shell(path);
        else Owner?.Toasts.Show(Loc.T("md.linkNotFound", Path.GetFileName(path)), ToastKind.Warning);
    }

    private void Shell(string target)
    {
        if (AppEnv.TestMode)
        {
            Log.Info("md: link not opened in test mode: " + target);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("md: cannot open " + target, ex);
            Owner?.Toasts.Show(Loc.T("md.linkBlocked", target), ToastKind.Error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release?.Cancel();
        _doc.BufferChanged -= OnBufferChanged;
        _root.IsVisibleChanged -= OnVisibleChanged;
        ThemeManager.I.Changed -= OnThemeChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        _md.Dispose();
    }
}

/// <summary>Colors fenced code with the editor's own highlighting definitions (theme tokens as live resource references).</summary>
internal static class CodeHighlighter
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c#"] = "csharp", ["cs"] = "csharp", ["c++"] = "cpp", ["py"] = "python", ["js"] = "javascript", ["ts"] = "typescript",
        ["shell"] = "sh", ["bash"] = "sh", ["zsh"] = "sh", ["console"] = "sh", ["ps"] = "ps1", ["powershell"] = "ps1",
        ["yml"] = "yaml", ["rs"] = "rust", ["golang"] = "go", ["md"] = "markdown", ["htm"] = "html", ["jsonc"] = "json",
    };

    public static IEnumerable<Inline>? Highlight(string language, string code)
    {
        var id = Resolve(language);
        if (id is null) return null;
        IHighlightingDefinition? def;
        try
        {
            def = HighlightingProvider.ForLanguage(id);
        }
        catch (Exception ex)
        {
            Log.Error("md: highlighting " + id, ex);
            return null;
        }
        if (def is null) return null;

        var doc = new AvalonDocument(code);
        var highlighter = new DocumentHighlighter(doc, def);
        var list = new List<Inline>();
        var sb = new System.Text.StringBuilder();
        foreach (var line in doc.Lines)
        {
            var text = doc.GetText(line);
            var colors = new HighlightingColor?[text.Length];
            try
            {
                var hl = highlighter.HighlightLine(line.LineNumber);
                foreach (var s in hl.Sections) // sorted, nested: inner sections come later and win
                {
                    var from = Math.Max(0, s.Offset - line.Offset);
                    var to = Math.Min(text.Length, s.Offset + s.Length - line.Offset);
                    for (var i = from; i < to; i++) colors[i] = s.Color;
                }
            }
            catch (Exception ex)
            {
                Log.Error("md: highlighting line failed", ex);
            }

            var start = 0;
            for (var i = 1; i <= text.Length; i++)
            {
                if (i < text.Length && ReferenceEquals(colors[i], colors[start])) continue;
                list.Add(MakeRun(text[start..i], colors[start]));
                start = i;
            }
            if (line.NextLine is not null) list.Add(new Run("\n"));
        }
        return list;
    }

    private static Run MakeRun(string text, HighlightingColor? color)
    {
        var run = new Run(text);
        if (color is null) return run;
        if (color.Name is { Length: > 0 } name)
            run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Syntax." + HighlightingProvider.TokenFor(name));
        else if (color.Foreground?.GetBrush(null) is { } brush)
            run.Foreground = brush;
        if (color.FontWeight is { } weight) run.FontWeight = weight;
        if (color.FontStyle is { } style) run.FontStyle = style;
        if (color.Underline == true) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private static string? Resolve(string fence)
    {
        var f = fence.Trim();
        if (f.Length == 0) return null;
        f = Aliases.GetValueOrDefault(f, f);
        var byId = Languages.Get(f);
        if (!string.Equals(byId.Id, Languages.PlainText, StringComparison.Ordinal) && string.Equals(byId.Id, f, StringComparison.OrdinalIgnoreCase)) return byId.Id;
        var byExt = Languages.ForFileName("code." + f);
        return string.Equals(byExt, Languages.PlainText, StringComparison.Ordinal) ? null : byExt;
    }
}
