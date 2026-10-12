using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;

namespace BetterNotepad.UI.Controls;

/// <summary>
/// One tab: state icon (saved / unsaved / changed on disk), name, lock and pin marks, close button.
/// The active tab joins the content below and wears the accent line on top.
/// </summary>
public sealed class TabChip : Border
{
    private readonly Icon _state = new() { FontSize = 13 };
    // unsaved: a small accent dot on the disk's corner (with a ring in the tab's own color, so it reads as a badge)
    private readonly System.Windows.Shapes.Ellipse _dot = new()
    {
        Width = 7.5, Height = 7.5, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, -3, -3, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
    private readonly Icon _lock = new() { Glyph = Glyphs.Lock, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Icon _pin = new() { Glyph = Glyphs.Pinned, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _close;
    private readonly Border _accent = new() { Height = 2, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(1), Margin = new Thickness(10, 0, 10, 0), Opacity = 0 };
    private readonly Grid _layout = new();
    private TextBox? _renameBox;
    private bool _isActive;
    private bool _groupActive = true;
    private bool _wasDirty;

    public TabChip(Document doc)
    {
        Document = doc;
        Height = 32;
        MinWidth = 120;
        MaxWidth = 240;
        Margin = new Thickness(0, 6, 2, 0);
        Padding = new Thickness(10, 0, 5, 0);
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        BorderThickness = new Thickness(1, 1, 1, 0);
        BorderBrush = Brushes.Transparent;
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        Focusable = false;

        _close = new Button
        {
            Width = 22,
            Height = 22,
            Content = new Icon { Glyph = Glyphs.Close, FontSize = 9 },
            Opacity = 0,
        };
        _close.SetResourceReference(StyleProperty, "IconButton");
        _close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(this);
        };

        // state icon + badge; pin and close share the last slot (a pinned tab shows its pin, the X replaces it on hover)
        var stateHost = new Grid { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        stateHost.Children.Add(_state);
        stateHost.Children.Add(_dot);
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.AccentGradient");
        var end = new Grid { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        end.Children.Add(_pin);
        end.Children.Add(_close);

        // the title column takes what is left, so a shrunk tab trims its title instead of overlapping the next tab
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 4; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var cells = new UIElement[] { stateHost, _title, _lock, end };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            row.Children.Add(cells[i]);
        }

        _accent.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradientH");
        _layout.Children.Add(row);
        _layout.Children.Add(_accent);
        _accent.Margin = new Thickness(-1, 0, 4, 0); // symmetric inset (9 px) inside the 1 px border
        Child = _layout;

        _lock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        _pin.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");

        ToolTipService.SetInitialShowDelay(this, 700);
        ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Bottom);

        MouseEnter += (_, _) => UpdateVisual();
        MouseLeave += (_, _) => UpdateVisual();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Refresh();
        _wasDirty = doc.IsDirty;
    }

    public Document Document { get; }

    public event Action<TabChip>? CloseRequested;

    public event Action<TabChip, string>? RenameCommitted;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            UpdateVisual();
            if (value) Motion.GrowX(_accent);
        }
    }

    /// <summary>When the window is split, only the focused group shows the strong accent.</summary>
    public bool GroupActive
    {
        get => _groupActive;
        set
        {
            _groupActive = value;
            UpdateVisual();
        }
    }

    public bool IsRenaming => _renameBox is not null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Document.PropertyChanged += OnDocumentChanged;
        Loc.LanguageChanged += Refresh;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Document.PropertyChanged -= OnDocumentChanged;
        Loc.LanguageChanged -= Refresh;
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Document.Title) or nameof(Document.IsDirty) or nameof(Document.IsPinned)
            or nameof(Document.IsLocked) or nameof(Document.External) or nameof(Document.FilePath) or nameof(Document.ToolTipText))
            Refresh();
    }

    public void Refresh()
    {
        _title.Text = Document.Title;
        ToolTip = Document.ToolTipText;
        _lock.Visibility = Document.IsLocked ? Visibility.Visible : Visibility.Collapsed;

        var dirty = Document.IsDirty;
        _dot.Visibility = dirty && Document.External == ExternalChange.None ? Visibility.Visible : Visibility.Collapsed;
        if (Document.External != ExternalChange.None)
        {
            _state.Glyph = Glyphs.Warning;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        }
        else if (dirty)
        {
            _state.Glyph = Glyphs.Save;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        }
        else if (Document.IsUntitled)
        {
            _state.Glyph = Glyphs.Page;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        }
        else
        {
            _state.Glyph = Glyphs.Save;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        }

        // saved → the disk does a little hop (dopamine on Ctrl+S)
        if (_wasDirty && !dirty) Motion.Bounce(_state);
        _wasDirty = dirty;
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        var hover = IsMouseOver;
        if (_isActive)
        {
            SetResourceReference(BackgroundProperty, "Brush.TabActive");
            SetResourceReference(BorderBrushProperty, "Brush.Border");
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            _accent.Opacity = _groupActive ? 1 : 0.35;
        }
        else
        {
            if (hover) SetResourceReference(BackgroundProperty, "Brush.TabHover");
            else Background = Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
            _title.SetResourceReference(TextBlock.ForegroundProperty, hover ? "Brush.Text" : "Brush.TextDim");
            _accent.Opacity = 0;
        }
        _dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, _isActive ? "Brush.TabActive" : "Brush.Chrome");
        if (Document.IsDirty) _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text"); // unsaved names stay readable
        var pinned = Document.IsPinned;
        var showClose = hover || (_isActive && !pinned);
        _close.Opacity = showClose ? 1 : 0;
        _close.IsHitTestVisible = showClose;
        _pin.Visibility = pinned && !showClose ? Visibility.Visible : pinned ? Visibility.Hidden : Visibility.Collapsed;
    }

    // ───────────── inline rename ─────────────

    public void BeginRename()
    {
        if (_renameBox is not null) return;
        var name = Document.Title;
        _renameBox = new TextBox
        {
            Text = name,
            MinWidth = 120,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
        };
        _renameBox.SetResourceReference(StyleProperty, "Field");
        _renameBox.Padding = new Thickness(6, 2, 6, 2);
        var panel = (Grid)_layout.Children[0];
        _title.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_renameBox, 1); // takes the title's place
        panel.Children.Add(_renameBox);
        _renameBox.Focus();
        var dot = Path.GetFileNameWithoutExtension(name).Length;
        _renameBox.Select(0, dot > 0 ? dot : name.Length);
        _renameBox.PreviewKeyDown += OnRenameKey;
        _renameBox.LostKeyboardFocus += (_, _) => EndRename(commit: true);
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EndRename(commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndRename(commit: false);
        }
    }

    private void EndRename(bool commit)
    {
        if (_renameBox is null) return;
        var box = _renameBox;
        _renameBox = null;
        var text = box.Text.Trim();
        var panel = (Grid)_layout.Children[0];
        panel.Children.Remove(box);
        _title.Visibility = Visibility.Visible;
        if (commit && text.Length > 0 && text != Document.Title) RenameCommitted?.Invoke(this, text);
    }

    public void ShakeRename() => Motion.Shake(this);
}
