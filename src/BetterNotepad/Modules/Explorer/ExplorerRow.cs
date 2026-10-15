using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.Modules.Explorer;

/// <summary>
/// One row of the folder tree (recycled by the virtualizing list): indent guides, chevron, type icon, name, and a dot
/// for files open in a tab (accent when unsaved). Hover and selection follow the app's lists: rounded, AccentSoft.
/// </summary>
public sealed class ExplorerRow : Border
{
    public const double RowHeight = 24;
    public const double Indent = 12;
    public const double Inset = 6;

    private readonly IndentGuides _guides = new();
    private readonly Icon _chevron = new() { Glyph = Glyphs.ChevronRight, FontSize = 9, Width = 16, TextAlignment = TextAlignment.Center };
    private readonly RotateTransform _turn = new();
    private readonly Icon _icon = new() { FontSize = 14, Width = 16, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 0, 1) };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Grid _line = new() { VerticalAlignment = VerticalAlignment.Stretch };
    private ExplorerNode? _node;
    private TextBox? _hosted;

    public ExplorerRow()
    {
        Height = RowHeight;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        Background = Brushes.Transparent;
        Margin = new Thickness(Inset, 0, Inset, 0);
        SnapsToDevicePixels = true;
        Focusable = false;

        _chevron.RenderTransform = _turn;
        _chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _guides.SetResourceReference(IndentGuides.StrokeProperty, "Brush.Border");
        _dot.SetResourceReference(Shape.FillProperty, "Brush.TextFaint");

        for (var i = 0; i < 4; i++)
            _line.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetColumn(_chevron, 0);
        Grid.SetColumn(_icon, 1);
        Grid.SetColumn(_name, 2);
        Grid.SetColumn(_dot, 3);
        _line.Children.Add(_chevron);
        _line.Children.Add(_icon);
        _line.Children.Add(_name);
        _line.Children.Add(_dot);

        var host = new Grid();
        host.Children.Add(_guides);
        host.Children.Add(_line);
        Child = host;

        DataContextChanged += (_, e) => Bind(e.NewValue as ExplorerNode, animate: false);
        MouseEnter += (_, _) => UpdateLook();
        MouseLeave += (_, _) => UpdateLook();
        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += OnLanguage;
            Bind(DataContext as ExplorerNode, animate: false);
        };
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguage;
            Bind(null, animate: false);
        };
    }

    public ExplorerNode? Node => _node;

    private void OnLanguage()
    {
        if (_node is { Role: NodeRole.Placeholder }) Refresh(animate: false);
    }

    private void Bind(ExplorerNode? node, bool animate)
    {
        if (!ReferenceEquals(_node, node))
        {
            if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
            _node = node;
            if (_node is not null && IsLoaded) _node.PropertyChanged += OnNodeChanged;
        }
        else if (_node is not null && IsLoaded)
        {
            // Loaded again with the same node: keep exactly one subscription
            _node.PropertyChanged -= OnNodeChanged;
            _node.PropertyChanged += OnNodeChanged;
        }
        Refresh(animate);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
        => Refresh(animate: e.PropertyName == nameof(ExplorerNode.IsExpanded));

    private void Refresh(bool animate)
    {
        var n = _node;
        if (n is null)
        {
            Unhost();
            return;
        }
        var depth = Math.Max(0, n.Depth);
        _guides.Depth = depth;
        _line.Margin = new Thickness(depth * Indent, 0, 2, 0);

        if (n.Role == NodeRole.Placeholder)
        {
            Unhost();
            _chevron.Visibility = Visibility.Hidden;
            _icon.Visibility = Visibility.Hidden;
            _dot.Visibility = Visibility.Collapsed;
            _name.Visibility = Visibility.Visible;
            _name.Text = Loc.T(n.TextKey ?? "explorer.loading");
            _name.FontStyle = FontStyles.Italic;
            _name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            ToolTip = null;
            UpdateLook();
            return;
        }

        _name.FontStyle = FontStyles.Normal;
        _icon.Visibility = Visibility.Visible;
        _chevron.Visibility = n.IsDirectory && n.Role == NodeRole.Item ? Visibility.Visible : Visibility.Hidden;
        var angle = n.IsExpanded ? 90 : 0;
        if (animate && Motion.I.Enabled && IsVisible)
            _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(140)) { EasingFunction = Motion.EaseOut });
        else
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
            _turn.Angle = angle;
        }

        var kind = n.IconFor(_hosted?.Text);
        if (kind == FileIconKind.Folder && n.IsExpanded) _icon.Glyph = ExplorerLook.FolderOpenGlyph;
        else _icon.Glyph = ExplorerLook.GlyphFor(kind);
        _icon.SetResourceReference(TextBlock.ForegroundProperty, ExplorerLook.TintFor(kind));

        _name.Text = n.Name;
        _dot.Visibility = !n.IsDirectory && (n.IsOpen || n.IsDirty) ? Visibility.Visible : Visibility.Collapsed;
        _dot.SetResourceReference(Shape.FillProperty, n.IsDirty ? "Brush.AccentGradient" : "Brush.TextFaint");
        ToolTip = n.Role == NodeRole.Item ? n.FullPath : null;

        if (n.IsEditing) Host();
        else Unhost();
        UpdateLook();
    }

    /// <summary>The inline name box of the panel takes the name's place while this row is edited.</summary>
    private void Host()
    {
        if (_node is null || ExplorerPanel.Of(this)?.EditorFor(_node) is not { } box) return;
        if (_hosted == box && box.Parent == _line) return;
        Unhost();
        if (box.Parent is Panel old) old.Children.Remove(box);
        _hosted = box;
        _hosted.TextChanged += OnTyping;
        Grid.SetColumn(box, 2);
        Grid.SetColumnSpan(box, 2);
        _line.Children.Add(box);
        _name.Visibility = Visibility.Collapsed;
        _dot.Visibility = Visibility.Collapsed;
    }

    private void Unhost()
    {
        _name.Visibility = Visibility.Visible;
        if (_hosted is null) return;
        _hosted.TextChanged -= OnTyping;
        if (_hosted.Parent == _line) _line.Children.Remove(_hosted);
        _hosted = null;
    }

    /// <summary>A new file's icon follows the typed extension.</summary>
    private void OnTyping(object sender, TextChangedEventArgs e)
    {
        if (_node is not { Role: NodeRole.NewItem } n) return;
        var kind = n.IconFor(_hosted?.Text);
        _icon.Glyph = ExplorerLook.GlyphFor(kind);
        _icon.SetResourceReference(TextBlock.ForegroundProperty, ExplorerLook.TintFor(kind));
    }

    internal void UpdateLook()
    {
        var n = _node;
        var selected = n is { IsSelected: true };
        var hover = IsMouseOver && n is { Role: not NodeRole.Placeholder };
        if (selected) SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
        else if (hover) SetResourceReference(BackgroundProperty, "Brush.TabHover");
        else Background = Brushes.Transparent;
        if (selected && n!.HasFocusRing) SetResourceReference(BorderBrushProperty, "Brush.AccentGlow");
        else BorderBrush = Brushes.Transparent;
        if (n is { Role: not NodeRole.Placeholder })
            _name.SetResourceReference(TextBlock.ForegroundProperty, selected || hover || n.IsOpen ? "Brush.Text" : "Brush.TextDim");
    }

    /// <summary>Vertical 1 px lines at every ancestor level (VS Code's indent guides).</summary>
    private sealed class IndentGuides : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(IndentGuides), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
            nameof(Depth), typeof(int), typeof(IndentGuides), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IndentGuides()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        public int Depth { get => (int)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            if (Depth <= 0 || Stroke is null) return;
            // the row's border is 1 px: guides sit on the chevron centers of the ancestor rows, top to bottom (rows touch)
            for (var k = 0; k < Depth; k++)
            {
                var x = Math.Round(k * Indent + 8 - 1) + 0.5;
                dc.DrawRectangle(Stroke, null, new Rect(x - 0.5, -1, 1, ActualHeight + 2));
            }
        }
    }
}
