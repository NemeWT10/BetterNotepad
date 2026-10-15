using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.Editor.Export;

/// <summary>
/// "Copy as thesis listing": caption "Listing 3.1. Description" (chapter + number, the next number is suggested),
/// code in Consolas 10 pt without indents, options remembered. Rules: university thesis guidelines
/// (caption Arial 10 pt, centered, 6 pt before / after, no period at the end).
/// </summary>
internal sealed class ListingDialog
{
    /// <summary>Text column of the thesis template.</summary>
    private const double TextWidthPt = 15.5 / 2.54 * 72;

    private readonly CodeEditor _ed;
    private readonly CopySettings _s = SettingsService.I.Get<CopySettings>();
    private readonly TextBox _chapter = NumberBox("listing.chapter", 2);
    private readonly TextBox _number = NumberBox("listing.number", 3);
    private readonly TextBox _description = new();
    private readonly TextBox _sourceText = new();
    private readonly TextBlock _preview = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Arial") };
    private readonly ContentControl _warning = new() { Focusable = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _error = new() { FontSize = 12, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly FrameworkElement _sourceRow;
    private CodeBlock _block = new([]);
    private bool _whole;

    public FrameworkElement Element { get; }

    private ListingDialog(CodeEditor ed)
    {
        _ed = ed;
        _chapter.Text = _s.ListingChapter.ToString();
        _number.Text = (_s.ListingNumber + 1).ToString();
        _chapter.TextChanged += (_, _) =>
        {
            if (int.TryParse(_chapter.Text, out var c))
                _number.Text = (c == _s.ListingChapter ? _s.ListingNumber + 1 : 1).ToString();
            UpdatePreview();
        };
        _number.TextChanged += (_, _) => UpdatePreview();

        _description.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        _description.Padding = FieldPadding;
        AutomationProperties.SetAutomationId(_description, "listing.description");
        AutomationProperties.SetName(_description, Loc.T("listing.description"));
        _description.TextChanged += (_, _) => UpdatePreview();

        _sourceText.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        _sourceText.Padding = FieldPadding;
        AutomationProperties.SetAutomationId(_sourceText, "listing.sourceText");
        _sourceText.Text = SourceText;
        _sourceText.TextChanged += (_, _) =>
        {
            _s.ListingSourceText = _sourceText.Text.Trim() == Loc.T("listing.sourceDefault") ? "" : _sourceText.Text.Trim();
        };

        _preview.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _error.Text = Loc.T("listing.badNumber");

        var root = new StackPanel();

        // chapter · number · description
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(Labeled(Loc.T("listing.chapter"), _chapter, 0));
        head.Children.Add(Labeled(Loc.T("listing.number"), _number, 2));
        head.Children.Add(Labeled(Loc.T("listing.description"), _description, 4));
        root.Children.Add(head);
        root.Children.Add(_error);

        // the caption exactly as it will be pasted
        var previewBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1) };
        previewBox.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        previewBox.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var previewStack = new StackPanel();
        previewStack.Children.Add(Caption(Loc.T("listing.preview")));
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.TextAlignment = TextAlignment.Center;
        _preview.Margin = new Thickness(0, 4, 0, 0);
        previewStack.Children.Add(_preview);
        previewBox.Child = previewStack;
        root.Children.Add(previewBox);
        root.Children.Add(_warning);

        // options (remembered)
        var optionsTitle = Caption(Loc.T("listing.options"));
        optionsTitle.Margin = new Thickness(2, 16, 0, 8);
        root.Children.Add(optionsTitle);
        var options = new Grid();
        options.ColumnDefinitions.Add(new ColumnDefinition());
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        options.ColumnDefinitions.Add(new ColumnDefinition());
        var items = new (string Id, Func<bool> Get, Action<bool> Set)[]
        {
            ("lineNumbers", () => _s.ListingLineNumbers, v => _s.ListingLineNumbers = v),
            ("colors", () => _s.ListingColors, v => _s.ListingColors = v),
            ("frame", () => _s.ListingFrame, v => _s.ListingFrame = v),
            ("captionBelow", () => _s.ListingCaptionBelow, v => _s.ListingCaptionBelow = v),
            ("dedent", () => _s.ListingDedent, v => { _s.ListingDedent = v; Recapture(); }),
            ("source", () => _s.ListingSource, v => { _s.ListingSource = v; UpdateSource(); }),
            ("wideSpacing", () => _s.ListingWideSpacing, v => _s.ListingWideSpacing = v),
        };
        for (var i = 0; i < items.Length; i++)
        {
            if (i % 2 == 0) options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var opt = Option(items[i].Id, items[i].Get, items[i].Set);
            Grid.SetRow(opt, i / 2);
            Grid.SetColumn(opt, i % 2 == 0 ? 0 : 2);
            options.Children.Add(opt);
        }
        root.Children.Add(options);
        _sourceRow = Labeled(Loc.T("listing.sourceText"), _sourceText, 0);
        _sourceRow.Margin = new Thickness(0, 2, 0, 4);
        root.Children.Add(_sourceRow);

        // font
        var font = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        font.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        font.ColumnDefinitions.Add(new ColumnDefinition());
        font.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fontSeg = Segment("listing.font", [("Consolas", "Consolas"), ("Courier New", "Courier New")], () => _s.ListingFont, v => { _s.ListingFont = v; Recapture(); });
        var sizeSeg = Segment("listing.size", [("9", "9 pt"), ("10", "10 pt"), ("11", "11 pt")], () => _s.ListingFontSize.ToString("0"),
            v => { _s.ListingFontSize = double.Parse(v); Recapture(); });
        Grid.SetColumn(sizeSeg, 2);
        font.Children.Add(fontSeg);
        font.Children.Add(sizeSeg);
        root.Children.Add(font);

        Element = root;
        UpdateSource();
        UpdatePreview();
    }

    public static async void Show(CodeEditor ed)
    {
        var w = RichCopy.MainOf(ed);
        if (w is null) return;
        var dlg = new ListingDialog(ed);
        dlg.Recapture();
        if (dlg._block.Lines.Count == 0)
        {
            w.Toasts.Show(Loc.T("copy.empty"), ToastKind.Info);
            return;
        }
        var choice = await w.DialogLayer.Show(Loc.T("listing.title"),
            Loc.Plural(dlg._whole ? "listing.fromFile" : "listing.fromSelection", dlg._block.Lines.Count),
            [
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                new DialogButton("copy", Loc.T("listing.copy"), DialogButtonKind.Primary, IsDefault: true),
            ],
            dlg.Element, Glyphs.Code, validate: _ => Task.FromResult(dlg.Validate()), focus: dlg._description);
        if (choice == "copy") dlg.Copy(w);
    }

    /// <summary>For SelfTest / snapshots: the dialog content without the dialog host.</summary>
    internal static ListingDialog CreateForTest(CodeEditor ed)
    {
        var d = new ListingDialog(ed);
        d.Recapture();
        return d;
    }

    internal void SetDescription(string text) => _description.Text = text;

    internal void SetChapter(string text) => _chapter.Text = text;

    internal string NumberText => _number.Text;

    internal string PreviewText => _preview.Text;

    internal bool WarningShown => _warning.Content is not null;

    internal bool Validate()
    {
        var ok = Parse(out _, out _);
        _error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        _chapter.Tag = int.TryParse(_chapter.Text, out var c) && c >= 1 ? null : "error";
        _number.Tag = int.TryParse(_number.Text, out var n) && n >= 1 ? null : "error";
        if (!ok) Motion.Shake(_error);
        return ok;
    }

    internal bool Copy(UI.Windows.MainWindow? w)
    {
        if (!Parse(out var chapter, out var number)) return false;
        _s.ListingChapter = chapter;
        _s.ListingNumber = number;
        Recapture();
        if (!RichCopy.Put(_block, Style(CaptionText(chapter, number)))) return false;
        w?.Toasts.Show(Loc.T("listing.copied", $"{chapter}.{number}"), ToastKind.Success, seconds: 3.5);
        return true;
    }

    /// <summary>The block in the listing format (black Consolas 10 pt, no indents, caption in Arial).</summary>
    internal RichCodeStyle Style(string caption) => new()
    {
        FontFamily = _s.ListingFont,
        FontSize = _s.ListingFontSize,
        LineSpacing = _s.ListingWideSpacing ? 1.5 : 1,
        Text = Colors.Black,
        Border = _s.ListingFrame ? Colors.Black : null,
        Gutter = _s.ListingFrame ? Colors.Black : null,
        BorderWidth = 0.5,
        LineNumbers = _s.ListingLineNumbers,
        LineNumberColor = Color.FromRgb(0x59, 0x59, 0x59),
        PaddingX = _s.ListingFrame ? 4 : 2,
        PaddingY = _s.ListingFrame ? 3 : 0,
        Caption = caption,
        CaptionPlacement = _s.ListingCaptionBelow ? CaptionPlacement.Below : CaptionPlacement.Above,
        Source = _s.ListingSource ? SourceText : null,
    };

    private string SourceText => string.IsNullOrWhiteSpace(_s.ListingSourceText) ? Loc.T("listing.sourceDefault") : _s.ListingSourceText;

    /// <summary>"Listing 3.1. Description" — the description without a period at the end (guidelines).</summary>
    internal string CaptionText(int chapter, int number)
    {
        var desc = _description.Text.Trim().TrimEnd('.', ' ', ' ');
        var head = $"Listing {chapter}.{number}";
        return desc.Length == 0 ? head : head + ". " + desc;
    }

    private bool Parse(out int chapter, out int number)
    {
        number = 0;
        return int.TryParse(_chapter.Text, out chapter) && chapter is >= 1 and <= 99
               && int.TryParse(_number.Text, out number) && number is >= 1 and <= 999;
    }

    /// <summary>Characters of the chosen font that fit the 15.5 cm column.</summary>
    private int MaxChars => (int)Math.Floor(TextWidthPt / (_s.ListingFontSize * (_s.ListingFont == "Courier New" ? 0.6001 : 0.5498)));

    private void Recapture()
    {
        _block = RichCopy.Capture(_ed, _s.ListingColors ? CopyPalettes.Print : null, _s.ListingDedent, out _whole);
        var max = MaxChars;
        var longer = _block.CountLongerThan(max);
        _warning.Content = longer == 0 ? null : SettingsUi.Notice("warning", Loc.Plural("listing.longLines", longer) + Loc.T("listing.longLinesTail", max));
    }

    private void UpdatePreview()
        => _preview.Text = Parse(out var c, out var n) ? CaptionText(c, n) : CaptionText(0, 0).Replace("Listing 0.0", "Listing ?.?");

    private void UpdateSource() => _sourceRow.Visibility = _s.ListingSource ? Visibility.Visible : Visibility.Collapsed;

    // ───────────── building blocks ─────────────

    /// <summary>The Field template applies Padding twice (host margin + text view): 5,3 gives a ~36 px field like a button.</summary>
    private static readonly Thickness FieldPadding = new(5, 3, 5, 3);

    private static TextBox NumberBox(string id, int maxLength)
    {
        var box = new TextBox { MaxLength = maxLength, HorizontalContentAlignment = HorizontalAlignment.Center };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        box.Padding = FieldPadding;
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, Loc.T(id));
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string t || !t.Trim().All(char.IsAsciiDigit)) e.CancelCommand();
        });
        box.PreviewKeyDown += (_, e) =>
        {
            var step = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
            if (step == 0) return;
            e.Handled = true;
            var v = int.TryParse(box.Text, out var n) ? n : 0;
            box.Text = Math.Max(1, v + step).ToString();
            box.CaretIndex = box.Text.Length;
        };
        return box;
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        return t;
    }

    private static FrameworkElement Labeled(string label, UIElement control, int column)
    {
        var stack = new StackPanel();
        var l = Caption(label);
        l.Margin = new Thickness(2, 0, 0, 6);
        stack.Children.Add(l);
        stack.Children.Add(control);
        Grid.SetColumn(stack, column);
        return stack;
    }

    private static FrameworkElement Option(string id, Func<bool> get, Action<bool> set)
    {
        var label = Loc.T("listing.opt." + id);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = Loc.T("listing.opt." + id + ".tip") };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var cb = new CheckBox { IsChecked = get() };
        cb.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
        AutomationProperties.SetAutomationId(cb, "listing.opt." + id);
        AutomationProperties.SetName(cb, label);
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        text.MouseLeftButtonUp += (_, _) => cb.IsChecked = cb.IsChecked != true;
        Grid.SetColumn(cb, 1);
        grid.Children.Add(text);
        grid.Children.Add(cb);
        return grid;
    }

    private static Border Segment(string id, IReadOnlyList<(string Value, string Label)> options, Func<string> get, Action<string> set)
    {
        var host = new Border();
        host.SetResourceReference(FrameworkElement.StyleProperty, "SegmentHost");
        AutomationProperties.SetAutomationId(host, id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = id + "-" + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = get() == value };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
            AutomationProperties.SetAutomationId(rb, id + "." + value);
            var v = value;
            rb.Checked += (_, _) => set(v);
            row.Children.Add(rb);
        }
        host.Child = row;
        return host;
    }
}
