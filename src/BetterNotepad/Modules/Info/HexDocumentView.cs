using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Hex;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Info;

/// <summary>Read-only hex view of a binary file: a slim bar (size, selection, go to offset) above <see cref="HexView"/>.</summary>
public sealed class HexDocumentView : Grid, IDocumentView
{
    private readonly FileInfoDocument _doc;
    private readonly HexView _hex = new();
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _goTo = new() { Width = 132, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _goToLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
    private bool _opened;

    public HexDocumentView(FileInfoDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── bar: "12 345 bytes · read only" … [Go to offset ____] ──
        var bar = new Grid { Height = 40, Margin = new Thickness(16, 0, 12, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _goToLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _goTo.SetResourceReference(StyleProperty, "Field");
        _goTo.Padding = new Thickness(8, 3, 8, 3);
        _goTo.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Font.Mono");
        _goTo.KeyDown += OnGoToKey;
        SetColumn(_goToLabel, 1);
        SetColumn(_goTo, 2);
        bar.Children.Add(_summary);
        bar.Children.Add(_goToLabel);
        bar.Children.Add(_goTo);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        var head = new Grid();
        head.Children.Add(bar);
        head.Children.Add(line);
        Children.Add(head);

        // ── the grid ──
        _hex.SetResourceReference(HexView.BackgroundProperty, "Brush.Editor.Bg");
        _hex.SetResourceReference(HexView.ForegroundProperty, "Brush.Editor.Text");
        _hex.SetResourceReference(HexView.FaintForegroundProperty, "Brush.TextFaint");
        _hex.SetResourceReference(HexView.SelectionBrushProperty, "Brush.Editor.Selection");
        _hex.SetResourceReference(HexView.InactiveSelectionBrushProperty, "Brush.Editor.SelectionInactive");
        _hex.SetResourceReference(HexView.CurrentLineBrushProperty, "Brush.Editor.CurrentLine");
        _hex.SetResourceReference(HexView.CaretBrushProperty, "Brush.Editor.Caret");
        _hex.SetResourceReference(HexView.SeparatorBrushProperty, "Brush.Border");
        _hex.AlwaysActiveSelection = true;
        _hex.SelectionChanged += (_, _) => UpdateSummary();
        SetRow(_hex, 1);
        Children.Add(_hex);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyTexts();
    }

    public Document Document => _doc;

    public FrameworkElement Element => this;

    public CodeEditor? Editor => null;

    /// <summary>For tests and snapshots.</summary>
    internal HexView Hex => _hex;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged += ApplyTexts;
        SettingsService.I.Changed += OnSettingsChanged;
        ApplySettings();
        Open();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= ApplyTexts;
        SettingsService.I.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(SettingsSection section, string? property)
    {
        if (section is EditorSettings or AppearanceSettings) ApplySettings();
    }

    private void ApplySettings()
    {
        var s = SettingsService.I.Get<EditorSettings>();
        _hex.CodeFontFamily = $"{s.FontFamily}, Cascadia Mono, Consolas";
        _hex.CodeFontSize = s.FontSize * 96.0 / 72.0 * s.Zoom;
        _hex.SmoothScrolling = Motion.I.Enabled;
    }

    private void Open()
    {
        if (_opened || _doc.FilePath is not { } path) return;
        try
        {
            _hex.OpenFile(path);
            _opened = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"hex: cannot open {path}: {ex.Message}");
            MainWindow.Of(this)?.Toasts.Show(Loc.T("hex.openFailed", ex.Message), ToastKind.Error);
        }
        UpdateSummary();
    }

    private void ApplyTexts()
    {
        _hex.OffsetLabel = Loc.T("hex.offset");
        _hex.TextLabel = Loc.T("hex.text");
        _hex.EmptyText = Loc.T("hex.empty");
        _goToLabel.Text = Loc.T("hex.goTo");
        _goTo.ToolTip = Loc.T("hex.goToTip");
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var size = Loc.Plural("hex.bytes", _hex.Length);
        var text = $"{size}  ·  {Loc.T("hex.readOnly")}";
        if (_hex.Length > 0)
        {
            var digits = HexFormat.OffsetDigits(_hex.Length);
            text += $"  ·  {Loc.T("hex.at", HexFormat.FormatOffset(_hex.CaretOffset, digits))}";
            if (_hex.SelectionLength > 1) text += $"  ·  {Loc.Plural("hex.selected", _hex.SelectionLength)}";
        }
        _summary.Text = text;
    }

    private void OnGoToKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _hex.Focus();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (HexFormat.TryParseOffset(_goTo.Text, defaultHex: true, out var offset) && offset < Math.Max(1, _hex.Length))
        {
            _hex.GoToOffset(offset);
            _hex.Focus();
        }
        else
        {
            Motion.Shake(_goTo);
            MainWindow.Of(this)?.Toasts.Show(Loc.T("hex.badOffset", _hex.Length > 0 ? HexFormat.FormatOffset(_hex.Length - 1, HexFormat.OffsetDigits(_hex.Length)) : "0"), ToastKind.Warning);
        }
    }

    public void OnActivated()
    {
        if (IsLoaded) Open();
    }

    public void OnDeactivated() { }

    public void CaptureState() { }

    public void FocusContent() => _hex.Focus();

    public void Dispose()
    {
        _hex.Close();
        _opened = false;
    }
}
