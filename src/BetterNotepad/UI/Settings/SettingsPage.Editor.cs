using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI.Controls;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;

namespace BetterNotepad.UI;

/// <summary>Editor page: font (with a live sample), indentation, wrapping, aids, minimap.</summary>
public sealed partial class SettingsPage
{
    private void BuildEditor(StackPanel panel)
    {
        var e = Section<EditorSettings>();

        // font
        var font = Group(panel, Loc.T("settings.editor.fontGroup"));
        var picker = new FontPicker("editor.fontFamily", () => e.FontFamily, v => e.FontFamily = v);
        _refresh.Add(picker.Refresh);
        var family = font.Add(new SettingRow(Glyphs.Text, Loc.T("settings.editor.fontFamily"), Loc.T("settings.editor.fontFamilyDesc"), picker));
        picker.InstalledChanged += installed => family.SetNotice(installed ? null : "missing",
            () => SettingsUi.Notice("warning", Loc.T("settings.editor.fontMissing")));
        font.Add(new SettingRow("\uE8E9", Loc.T("settings.editor.fontSize"), Loc.T("settings.editor.fontSizeDesc"),
            Number("editor.fontSize", 6, 48, Stepper.Linear(1), () => e.FontSize, v => e.FontSize = v, v => Loc.T("settings.unit.pt", v))));
        var zoomReset = SettingsUi.Button(Loc.T("settings.editor.zoomReset"), (_, _) => BetterNotepad.Editor.CodeEditor.ResetZoom(), glyph: Glyphs.Refresh, id: "editor.zoomReset");
        var zoom = font.Add(new SettingRow("\uE71E", Loc.T("settings.editor.zoom"), null, zoomReset));
        Bind(() =>
        {
            zoom.SetDescription(Loc.T("settings.editor.zoomDesc", Math.Round(e.Zoom * 100)));
            zoomReset.IsEnabled = Math.Abs(e.Zoom - 1) > 0.001;
        });
        font.Add(FontSample(e));

        // indentation
        var indent = Group(panel, Loc.T("settings.editor.indentGroup"));
        indent.Add(new SettingRow("\uE8E2", Loc.T("settings.editor.tabSize"), Loc.T("settings.editor.tabSizeDesc"),
            Number("editor.tabSize", 1, 16, Stepper.Linear(1), () => e.TabSize, v => e.TabSize = (int)v, v => v.ToString("0"), 44)));
        indent.Add(new SettingRow("\uE75D", Loc.T("settings.editor.insertSpaces"), Loc.T("settings.editor.insertSpacesDesc"),
            Switch("editor.insertSpaces", () => e.InsertSpaces, v => e.InsertSpaces = v)));

        // wrapping and line numbers: code and plain text separately
        var wrap = Group(panel, Loc.T("settings.editor.wrapGroup"));
        wrap.Add(new SettingRow(Glyphs.Code, Loc.T("settings.editor.wrapCode"), Loc.T("settings.editor.wrapCodeDesc"),
            Switch("editor.wordWrapCode", () => e.WordWrapCode, v => e.WordWrapCode = v)));
        wrap.Add(new SettingRow(Glyphs.Document, Loc.T("settings.editor.wrapText"), Loc.T("settings.editor.wrapTextDesc"),
            Switch("editor.wordWrapText", () => e.WordWrapText, v => e.WordWrapText = v)));
        wrap.Add(new SettingRow(Glyphs.Code, Loc.T("settings.editor.lineNumbersCode"), Loc.T("settings.editor.lineNumbersCodeDesc"),
            Switch("editor.lineNumbersCode", () => e.LineNumbersCode, v => e.LineNumbersCode = v)));
        wrap.Add(new SettingRow(Glyphs.Document, Loc.T("settings.editor.lineNumbersText"), Loc.T("settings.editor.lineNumbersTextDesc"),
            Switch("editor.lineNumbersText", () => e.LineNumbersText, v => e.LineNumbersText = v)));

        // what the editor shows
        var show = Group(panel, Loc.T("settings.editor.showGroup"));
        show.Add(new SettingRow("\uE8BC", Loc.T("settings.editor.currentLine"), Loc.T("settings.editor.currentLineDesc"),
            Switch("editor.currentLine", () => e.HighlightCurrentLine, v => e.HighlightCurrentLine = v)));
        show.Add(new SettingRow(Glyphs.View, Loc.T("settings.editor.whitespace"), Loc.T("settings.editor.whitespaceDesc"),
            Switch("editor.whitespace", () => e.ShowWhitespace, v => e.ShowWhitespace = v)));
        show.Add(new SettingRow("\uE8E4", Loc.T("settings.editor.indentGuides"), Loc.T("settings.editor.indentGuidesDesc"),
            Switch("editor.indentGuides", () => e.IndentGuides, v => e.IndentGuides = v)));
        show.Add(new SettingRow(Glyphs.Selection, Loc.T("settings.editor.occurrences"), Loc.T("settings.editor.occurrencesDesc"),
            Switch("editor.occurrences", () => e.HighlightOccurrences, v => e.HighlightOccurrences = v)));
        show.Add(new SettingRow(Glyphs.Color, Loc.T("settings.editor.colorPreview"), Loc.T("settings.editor.colorPreviewDesc"),
            Switch("editor.colorPreview", () => e.ColorPreview, v => e.ColorPreview = v)));
        show.Add(new SettingRow(Glyphs.ChevronDown, Loc.T("settings.editor.folding"), Loc.T("settings.editor.foldingDesc"),
            Switch("editor.folding", () => e.Folding, v => e.Folding = v)));

        // typing aids
        var typing = Group(panel, Loc.T("settings.editor.typingGroup"));
        typing.Add(new SettingRow(Glyphs.Code, Loc.T("settings.editor.autoClose"), Loc.T("settings.editor.autoCloseDesc"),
            Switch("editor.autoClose", () => e.AutoCloseBrackets, v => e.AutoCloseBrackets = v)));
        typing.Add(new SettingRow("\uE8FD", Loc.T("settings.editor.completion"), Loc.T("settings.editor.completionDesc"),
            Switch("editor.completion", () => e.Completion, v => e.Completion = v)));
        var completionChars = typing.Add(new SettingRow(null, Loc.T("settings.editor.completionChars"), Loc.T("settings.editor.completionCharsDesc"),
            Number("editor.completionChars", 0, 9, Stepper.Linear(1), () => e.CompletionChars, v => e.CompletionChars = (int)v,
                v => v < 0.5 ? Loc.T("settings.editor.completionChars.manual") : Loc.Plural("settings.unit.letters", (long)v), 104), indent: true));
        Bind(() => completionChars.SetActive(e.Completion));
        typing.Add(new SettingRow(Glyphs.Checkmark, Loc.T("settings.editor.spellCheck"), Loc.T("settings.editor.spellCheckDesc"),
            Switch("editor.spellCheck", () => e.SpellCheck, v => e.SpellCheck = v)));

        // copy with formatting (Word / Google Docs); the thesis listing keeps its options in its own dialog
        var cs = Section<BetterNotepad.Editor.Export.CopySettings>();
        var copy = Group(panel, Loc.T("settings.editor.copyGroup"));
        copy.Add(new SettingRow(Glyphs.Copy, Loc.T("settings.editor.copyLineNumbers"), Loc.T("settings.editor.copyLineNumbersDesc"),
            Switch("copy.lineNumbers", () => cs.LineNumbers, v => cs.LineNumbers = v)));
        copy.Add(new SettingRow(Glyphs.Color, Loc.T("settings.editor.copyThemeColors"), Loc.T("settings.editor.copyThemeColorsDesc"),
            Switch("copy.themeColors", () => cs.ThemeColors, v => cs.ThemeColors = v)));
        copy.Add(new SettingRow("", Loc.T("settings.editor.copyDedent"), Loc.T("settings.editor.copyDedentDesc"),
            Switch("copy.dedent", () => cs.Dedent, v => cs.Dedent = v)));
        copy.Add(new SettingRow(Glyphs.Code, Loc.T("listing.title"), Loc.T("settings.editor.copyListingHint"), null));

        // copy as image (a PNG card for slides and chats)
        var image = Group(panel, Loc.T("settings.editor.imageGroup"));
        image.Add(new SettingRow(Glyphs.LineNumbers, Loc.T("settings.editor.imageLineNumbers"), Loc.T("settings.editor.imageLineNumbersDesc"),
            Switch("copy.imageLineNumbers", () => cs.ImageLineNumbers, v => cs.ImageLineNumbers = v)));
        image.Add(new SettingRow(Glyphs.Color, Loc.T("settings.editor.imageLight"), Loc.T("settings.editor.imageLightDesc"),
            Switch("copy.imageLight", () => cs.ImageLightTheme, v => cs.ImageLightTheme = v)));
        image.Add(new SettingRow(Glyphs.Image, Loc.T("settings.editor.imageHintTitle"), Loc.T("settings.editor.imageHint"), null));

        // minimap
        var minimap = Group(panel, Loc.T("settings.editor.minimapGroup"));
        var modeRow = minimap.Add(new SettingRow("\uE81E", Loc.T("settings.editor.minimap"), null,
            Segment("editor.minimapMode",
                [("auto", Loc.T("settings.editor.minimap.auto")), ("always", Loc.T("settings.editor.minimap.always")), ("never", Loc.T("settings.editor.minimap.never"))],
                () => e.MinimapMode, v => e.MinimapMode = v)));
        var threshold = minimap.Add(new SettingRow(null, Loc.T("settings.editor.minimapThreshold"), Loc.T("settings.editor.minimapThresholdDesc"),
            Number("editor.minimapThreshold", 1, 50, Stepper.Linear(1), () => e.MinimapThresholdScreens, v => e.MinimapThresholdScreens = v,
                v => Loc.Plural("settings.unit.screens", (long)v), 92), indent: true));
        Bind(() =>
        {
            modeRow.SetDescription(Loc.T("settings.editor.minimap." + (e.MinimapMode is "always" or "never" ? e.MinimapMode : "auto") + ".desc"));
            threshold.SetActive(e.MinimapMode is not ("always" or "never"));
        });
    }

    /// <summary>A few lines of code in the chosen font and size, colored by the active theme.</summary>
    private FrameworkElement FontSample(EditorSettings e)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 10, 16, 10),
            Margin = new Thickness(46, 0, 0, 16),
        };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.Editor.Bg");
        box.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        AutomationProperties.SetAutomationId(box, "editor.fontSample");

        (string Text, string Token)[][] code =
        [
            [("// ", "Comment"), (Loc.T("settings.editor.sampleComment"), "Comment")],
            [("const", "Keyword"), (" greeting ", "Variable"), ("=", "Operator"), (" ", "Variable"), ("\"zażółć gęślą jaźń\"", "String"), (";", "Punctuation")],
            [("if", "Control"), (" (", "Punctuation"), ("count", "Variable"), (" >= ", "Operator"), ("0x1F", "Number"), (") ", "Punctuation"), ("render", "Function"), ("(", "Punctuation"), ("0O 1lI", "String"), (");", "Punctuation")],
        ];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var blocks = new List<TextBlock>();
        for (var i = 0; i < code.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = new TextBlock { Text = (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0) };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Editor.LineNumber");
            Grid.SetRow(n, i);
            grid.Children.Add(n);
            var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            foreach (var (text, token) in code[i])
            {
                var run = new Run(text);
                run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Syntax." + token);
                if (token == "Comment") run.FontStyle = FontStyles.Italic;
                line.Inlines.Add(run);
            }
            Grid.SetRow(line, i);
            Grid.SetColumn(line, 1);
            grid.Children.Add(line);
            blocks.Add(n);
            blocks.Add(line);
        }
        box.Child = grid;
        Bind(() =>
        {
            var family = new FontFamily($"{e.FontFamily}, Cascadia Mono, Cascadia Code, Consolas, Courier New");
            var size = Math.Clamp(e.FontSize, 6, 28) * 96.0 / 72.0;
            foreach (var b in blocks)
            {
                b.FontFamily = family;
                b.FontSize = size;
            }
        });
        return box;
    }
}

/// <summary>
/// Font family field: free text plus a drop-down of the installed monospace fonts (each shown in itself),
/// filtered while typing. Commits on Enter, on pick and when the field loses focus.
/// </summary>
internal sealed class FontPicker : Grid
{
    private static Task<(List<string> Mono, HashSet<string> All)>? _fonts;

    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly TextBox _box;
    private readonly ToggleButton _drop = new();
    private readonly Popup _popup = new();
    private readonly ListBox _list = new();
    private bool _committing;

    public FontPicker(string id, Func<string> get, Action<string> set)
    {
        _get = get;
        _set = set;
        Width = 240;
        AutomationProperties.SetAutomationId(this, id);

        _box = new TextBox { Text = get(), Height = 34, Padding = new Thickness(10, 0, 34, 0) };
        _box.SetResourceReference(StyleProperty, "Field");
        AutomationProperties.SetAutomationId(_box, id + ".text");
        _box.KeyDown += OnBoxKey;
        _box.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && _popup.Child is { } c && c.IsAncestorOf(d)) return;
            Commit(_box.Text);
        };
        _box.TextChanged += (_, _) =>
        {
            if (_popup.IsOpen && !_committing) Filter(_box.Text);
        };
        Children.Add(_box);

        _drop.Content = new Icon { Glyph = Glyphs.ChevronDown, FontSize = 10 };
        _drop.Width = 28;
        _drop.Height = 28;
        _drop.HorizontalAlignment = HorizontalAlignment.Right;
        _drop.Margin = new Thickness(0, 0, 3, 0);
        _drop.ToolTip = Loc.T("settings.editor.fontList");
        _drop.SetResourceReference(StyleProperty, "IconButton");
        _drop.Checked += (_, _) => Open();
        _drop.Unchecked += (_, _) => _popup.IsOpen = false;
        Children.Add(_drop);

        _list.MaxHeight = 300;
        _list.SetResourceReference(StyleProperty, "BareList");
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemAt(e.OriginalSource as DependencyObject) is { } name) Pick(name);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is ListBoxItem { Tag: string name })
            {
                e.Handled = true;
                Pick(name);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(focusBox: true);
            }
        };
        var panel = new Border
        {
            Margin = new Thickness(0, 4, 10, 12),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = _list,
        };
        panel.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        panel.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        panel.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.35,
            Color = Themes.ThemeManager.ShadowColor,
        };
        _popup.PlacementTarget = this;
        _popup.Placement = PlacementMode.Bottom;
        _popup.AllowsTransparency = true;
        _popup.StaysOpen = false;
        _popup.PopupAnimation = PopupAnimation.Fade;
        _popup.Child = panel;
        _popup.Opened += (_, _) => _drop.IsHitTestVisible = false;
        _popup.Closed += (_, _) =>
        {
            _drop.IsChecked = false;
            _drop.IsHitTestVisible = true;
        };
        Loaded += (_, _) => _ = CheckInstalledAsync();
    }

    /// <summary>Raised with false when the chosen font is not installed (a fallback is used).</summary>
    public event Action<bool>? InstalledChanged;

    public void Refresh()
    {
        if (_box.IsKeyboardFocusWithin) return;
        _box.Text = _get();
        _ = CheckInstalledAsync();
    }

    /// <summary>Installed monospace fonts (by equal advance widths), computed once off the UI thread.</summary>
    public static Task<(List<string> Mono, HashSet<string> All)> LoadFonts() => _fonts ??= Task.Run(() =>
    {
        var mono = new List<string>();
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            try
            {
                var name = family.Source;
                if (!all.Add(name)) continue;
                var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!face.TryGetGlyphTypeface(out var g) || g.Symbol) continue;
                double W(char c) => g.CharacterToGlyphMap.TryGetValue(c, out var i) ? g.AdvanceWidths[i] : -1;
                var w = W('i');
                if (w > 0 && Math.Abs(W('M') - w) < 1e-3 && Math.Abs(W('W') - w) < 1e-3 && Math.Abs(W('.') - w) < 1e-3)
                    mono.Add(name);
            }
            catch
            {
                // a broken font must not break the list
            }
        }
        mono.Sort(StringComparer.CurrentCultureIgnoreCase);
        return (mono, all);
    });

    private async Task CheckInstalledAsync()
    {
        try
        {
            var (_, all) = await LoadFonts();
            InstalledChanged?.Invoke(all.Contains(_get().Trim()));
        }
        catch (Exception ex)
        {
            Log.Warn("font list unavailable: " + ex.Message);
        }
    }

    private async void Open()
    {
        if (AppEnv.TestMode) // popups are separate windows: never show one from a test instance
        {
            _drop.IsChecked = false;
            return;
        }
        var (mono, _) = await LoadFonts();
        Fill(mono);
        _popup.MinWidth = ActualWidth;
        _popup.IsOpen = true;
        if (_list.SelectedItem is ListBoxItem selected) _list.ScrollIntoView(selected);
    }

    private void Fill(IEnumerable<string> names)
    {
        _list.Items.Clear();
        var current = _get();
        foreach (var name in names)
        {
            var item = new ListBoxItem
            {
                Tag = name,
                Content = new TextBlock { Text = name, FontFamily = new FontFamily(name), FontSize = 13.5 },
            };
            _list.Items.Add(item);
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) _list.SelectedItem = item;
        }
    }

    private async void Filter(string text)
    {
        var (mono, _) = await LoadFonts();
        var q = text.Trim();
        var matches = q.Length == 0 || mono.Any(m => string.Equals(m, q, StringComparison.OrdinalIgnoreCase))
            ? mono
            : mono.Where(m => m.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Fill(matches);
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit(_box.Text);
                Close(focusBox: false);
                break;
            case Key.Down when !_popup.IsOpen:
                e.Handled = true;
                _drop.IsChecked = true;
                break;
            case Key.Down when _list.Items.Count > 0:
                e.Handled = true;
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus();
                break;
            case Key.Escape when _popup.IsOpen:
                e.Handled = true;
                Close(focusBox: true);
                break;
        }
    }

    private static string? ItemAt(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem { Tag: string name }) return name;
        return null;
    }

    private void Pick(string name)
    {
        _committing = true;
        _box.Text = name;
        _committing = false;
        Commit(name);
        Close(focusBox: true);
    }

    private void Close(bool focusBox)
    {
        _popup.IsOpen = false;
        if (focusBox)
        {
            _box.Focus();
            _box.CaretIndex = _box.Text.Length;
        }
    }

    private void Commit(string text)
    {
        var name = text.Trim();
        if (name.Length == 0)
        {
            _box.Text = _get();
            return;
        }
        if (name != _get()) _set(name);
        _ = CheckInstalledAsync();
    }
}
