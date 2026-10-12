using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Modules.Sys;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// The bar of a pinned-on-top window: it slides in when the pointer reaches the top edge and pushes the content down
/// (so it never covers a view's own toolbar, e.g. the PDF bar). Drag area with the file name, ghost-mode opacity
/// (popover with a live preview), ghost mode, unpin, close tab.
/// </summary>
internal sealed class PinnedBar : Grid
{
    public const double BarHeight = 34;

    private readonly MainWindow _owner;
    private readonly Grid _inner = new() { Height = BarHeight, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _name = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Ellipse _dirty = new() { Width = 6, Height = 6, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dragArea = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Button _opacity;
    private readonly Button _ghost;
    private readonly Button _unpin;
    private readonly Button _close;
    private Popup? _popup;

    public PinnedBar(MainWindow owner)
    {
        _owner = owner;
        Height = 0;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;

        var back = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        back.SetResourceReference(Border.BackgroundProperty, "Brush.Chrome");
        back.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Children.Add(back);

        _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new DockPanel { LastChildFill = true };
        var grip = new Icon { Glyph = "", FontSize = 12, Margin = new Thickness(11, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        grip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        DockPanel.SetDock(grip, Dock.Left);
        _dirty.SetResourceReference(Shape.FillProperty, "Brush.AccentText");
        DockPanel.SetDock(_dirty, Dock.Left);
        _name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        left.Children.Add(grip);
        left.Children.Add(_dirty);
        left.Children.Add(_name);
        _dragArea.Child = left;
        _dragArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1) return;
            e.Handled = true;
            _owner.BeginPinDrag();
        };
        _inner.Children.Add(_dragArea);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        _opacity = BarButton("", 13, (_, _) => ToggleOpacityPopover());
        _ghost = BarButton(Glyphs.Ghost, 14, (_, _) => _owner.SetGhost(true));
        _unpin = BarButton(Glyphs.Unpin, 13, (_, _) => _owner.UnpinFromTop());
        _close = BarButton(Glyphs.Close, 10, (_, _) => _owner.ClosePinnedTab());
        foreach (var b in new[] { _opacity, _ghost, _unpin, _close }) buttons.Children.Add(b);
        Grid.SetColumn(buttons, 1);
        _inner.Children.Add(buttons);
        Children.Add(_inner);

        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += RefreshTexts;
            HotkeyService.StatusChanged += RefreshTexts;
            RefreshTexts();
        };
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= RefreshTexts;
            HotkeyService.StatusChanged -= RefreshTexts;
        };
        RefreshTexts();
    }

    /// <summary>The bar's content (fades with the slide).</summary>
    public UIElement Content => _inner;

    public bool PopoverOpen => _popup?.IsOpen == true;

    /// <summary>For tests and snapshots.</summary>
    internal string NameText => _name.Text;

    internal IReadOnlyList<Button> Buttons => [_opacity, _ghost, _unpin, _close];

    private static Button BarButton(string glyph, double size, RoutedEventHandler click)
    {
        var b = new Button { Width = 28, Height = 28, Margin = new Thickness(1, 0, 1, 0), Content = new Icon { Glyph = glyph, FontSize = size } };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += click;
        return b;
    }

    /// <summary>Shows the pinned document's name and unsaved dot.</summary>
    public void Bind(Document? doc)
    {
        if (doc is null)
        {
            BindingOperations.ClearBinding(_name, TextBlock.TextProperty);
            _name.Text = "";
            _dirty.Visibility = Visibility.Collapsed;
            return;
        }
        _name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Document.Title)) { Source = doc, Mode = BindingMode.OneWay });
        _dirty.SetBinding(VisibilityProperty, new Binding(nameof(Document.IsDirty)) { Source = doc, Mode = BindingMode.OneWay, Converter = new BooleanToVisibilityConverter() });
        _dragArea.SetBinding(ToolTipProperty, new Binding(nameof(Document.ToolTipText)) { Source = doc, Mode = BindingMode.OneWay, StringFormat = "{0}\n" + Loc.T("pin.dragTip").Replace("{", "{{").Replace("}", "}}") });
    }

    private void RefreshTexts()
    {
        var chord = PinnedWindows.GhostChord();
        _opacity.ToolTip = Loc.T("pin.opacityTip");
        _ghost.ToolTip = chord is null ? Loc.T("pin.ghostTipNoKey") : Loc.T("pin.ghostTip", chord);
        _unpin.ToolTip = Loc.T("pin.unpinTip");
        _close.ToolTip = Loc.T("pin.closeTip");
        AutomationProperties.SetName(_opacity, Loc.T("pin.opacityTip"));
        AutomationProperties.SetName(_ghost, Loc.T("cmd.view.toggleGhost"));
        AutomationProperties.SetName(_unpin, Loc.T("pin.unpinTip"));
        AutomationProperties.SetName(_close, Loc.T("pin.closeTip"));
        if (_owner.ActiveDocument is { } doc) Bind(doc);
    }

    // ───────────── ghost-mode opacity popover ─────────────

    private void ToggleOpacityPopover()
    {
        if (PopoverOpen)
        {
            ClosePopover();
            return;
        }
        var card = CreateOpacityCard(_owner);
        _popup = new Popup
        {
            Child = card,
            PlacementTarget = _opacity,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -200,
            VerticalOffset = -6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        _popup.Opened += (_, _) => _owner.SetOpacityPreview(_owner.Model.GhostOpacity);
        _popup.Closed += (_, _) =>
        {
            _owner.SetOpacityPreview(null);
            _owner.SavePinState();
            _owner.OnPinPopoverClosed();
        };
        _popup.IsOpen = true;
        Motion.Appear(card, rise: 6, fromScale: 0.97, ms: 200);
    }

    public void ClosePopover()
    {
        if (_popup is { IsOpen: true } p) p.IsOpen = false;
    }

    /// <summary>The popover card: title + value, slider 15–100 %, hint. Changes apply live (preview) and are remembered per file.</summary>
    internal static Border CreateOpacityCard(MainWindow owner)
    {
        var card = new Border { Width = 252, Margin = new Thickness(14, 10, 14, 18) };
        card.SetResourceReference(StyleProperty, "Ov.Panel");
        var panel = new StackPanel { Margin = new Thickness(9, 7, 9, 9) };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Icon { Glyph = Glyphs.Ghost, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        var title = new TextBlock { Text = Loc.T("pin.opacityTitle"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        var value = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        Grid.SetColumn(title, 1);
        Grid.SetColumn(value, 2);
        head.Children.Add(icon);
        head.Children.Add(title);
        head.Children.Add(value);
        panel.Children.Add(head);

        var slider = new Slider { Minimum = 15, Maximum = 100, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, Value = Math.Round(owner.Model.GhostOpacity * 100) };
        slider.SetResourceReference(StyleProperty, "Slider");
        AutomationProperties.SetName(slider, Loc.T("pin.opacityTitle"));
        value.Text = Loc.T("pin.percent", slider.Value.ToString("0"));
        slider.ValueChanged += (_, e) =>
        {
            value.Text = Loc.T("pin.percent", e.NewValue.ToString("0"));
            owner.Model.GhostOpacity = e.NewValue / 100;
            if (owner.PinBar?.PopoverOpen == true) owner.SetOpacityPreview(owner.Model.GhostOpacity);
        };
        panel.Children.Add(slider);

        var hint = new TextBlock { Text = Loc.T("pin.opacityHint"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 16 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        panel.Children.Add(hint);
        card.Child = panel;
        return card;
    }
}

/// <summary>
/// Always-visible badge of a window in ghost mode with the way out: the configured global chord
/// ("Ghost · Win+Shift+G"), or the tray / palette when no chord works.
/// </summary>
internal sealed class GhostBadge : Border
{
    private readonly TextBlock _text = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 1) };

    public GhostBadge()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 10, 12, 0);
        Padding = new Thickness(9, 4, 11, 4);
        CornerRadius = new CornerRadius(11);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "Brush.AccentFill");
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.3, Color = Colors.Black };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Icon { Glyph = Glyphs.Ghost, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        row.Children.Add(icon);
        row.Children.Add(_text);
        Child = row;

        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += Refresh;
            HotkeyService.StatusChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= Refresh;
            HotkeyService.StatusChanged -= Refresh;
        };
        Refresh();
    }

    public string Text => _text.Text;

    public void Refresh() => _text.Text = PinnedWindows.GhostExitText("pin.ghostBadge", "pin.ghostBadgeTray", "pin.ghostBadgePalette");
}
