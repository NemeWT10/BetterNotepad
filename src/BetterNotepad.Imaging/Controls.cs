using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BetterNotepad.Imaging;

/// <summary>
/// Icon button of the floating toolbar — the app's IconButton look on a panel: hover lifts (spring), press dips, a selected
/// tool sits on the accent fill with OnAccent ink and bounces when picked.
/// </summary>
internal sealed class ToolButton : ToggleButton
{
    private static ControlTemplate? _template;
    private static Style? _style;
    private readonly IconPath _icon;

    public ToolButton(IconKind icon, string tip, string? keys = null, bool toggles = false)
    {
        Toggles = toggles;
        _icon = new IconPath { Kind = icon };
        Content = _icon;
        Width = 34;
        Height = 34;
        Focusable = false;
        Cursor = Cursors.Hand;
        // the resting ink comes from a style (a local value would beat the template's hover / checked triggers)
        Style = _style ??= new Style(typeof(ToolButton)) { Setters = { new Setter(ForegroundProperty, new DynamicResourceExtension("Brush.PanelTextDim")) } };
        Template = _template ??= MakeTemplate();
        ToolTip = Ui.Tip(tip, keys);
        ToolTipService.SetInitialShowDelay(this, 450);
        MouseEnter += (_, _) => Anim.Hover(this, true);
        MouseLeave += (_, _) => Anim.Hover(this, false);
        Checked += (_, _) => Anim.Bounce(_icon);
    }

    public bool Toggles { get; }

    public IconKind Icon
    {
        get => _icon.Kind;
        set => _icon.Kind = value;
    }

    protected override void OnToggle()
    {
        if (Toggles) base.OnToggle();
    }

    private static ControlTemplate MakeTemplate() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
  <Grid x:Name="Root" Background="Transparent">
    <Border x:Name="Glow" CornerRadius="9" Background="{DynamicResource Brush.AccentFill}" Opacity="0"/>
    <Border x:Name="Bg" CornerRadius="9" Background="Transparent"/>
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
      <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Brush.PanelHover}"/>
      <Setter Property="Foreground" Value="{DynamicResource Brush.PanelText}"/>
    </Trigger>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Brush.PanelPressed}"/>
      <Setter TargetName="Root" Property="RenderTransformOrigin" Value="0.5,0.5"/>
      <Setter TargetName="Root" Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.93" ScaleY="0.93"/></Setter.Value></Setter>
    </Trigger>
    <Trigger Property="IsChecked" Value="True">
      <Setter TargetName="Glow" Property="Opacity" Value="1"/>
      <Setter TargetName="Bg" Property="Background" Value="Transparent"/>
      <Setter Property="Foreground" Value="{DynamicResource Brush.OnAccent}"/>
    </Trigger>
    <Trigger Property="IsEnabled" Value="False">
      <Setter Property="Opacity" Value="0.35"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
}

/// <summary>Small shared UI pieces drawn with the app's theme resources.</summary>
internal static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    public static object Tip(string title, string? keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(keys))
        {
            var chip = new Border
            {
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock { Text = keys, FontSize = 11 },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
            ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            row.Children.Add(chip);
        }
        return row;
    }

    /// <summary>The floating translucent panel (Base.xaml "Ov.Panel").</summary>
    public static Border Panel(UIElement child, double radius = 14, Thickness? padding = null)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(5),
            Child = child,
        };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        b.SetResourceReference(TextElement.ForegroundProperty, "Brush.PanelText");
        TextElement.SetFontFamily(b, Font);
        return b;
    }

    /// <summary>Soft shadow in the theme's shadow color (effects cannot hold a DynamicResource: the viewer recolors them).</summary>
    public static DropShadowEffect Shadow(double blur, double depth, double opacity) => new()
    {
        BlurRadius = blur,
        ShadowDepth = depth,
        Direction = 270,
        Opacity = opacity,
        Color = Application.Current?.TryFindResource("Color.Shadow") is Color c ? c : Colors.Black,
    };

    public static Rectangle VSeparator()
    {
        var r = new Rectangle { Width = 1, Margin = new Thickness(5, 7, 5, 7), IsHitTestVisible = false };
        r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.PanelSeparator");
        return r;
    }

    public static TextBlock Label(string text, double size = 12, bool dim = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Brush.PanelTextDim" : "Brush.PanelText");
        return t;
    }

    /// <summary>A text button in the accent fill (Apply), the app's PrimaryButton.</summary>
    public static Button Primary(string text, Action click)
    {
        var b = new Button { Content = text, Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(14, 6, 14, 6) };
        b.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
  <Grid x:Name="Root" RenderTransformOrigin="0.5,0.5">
    <Border CornerRadius="9" Background="{DynamicResource Brush.AccentFill}"/>
    <Border x:Name="Shine" CornerRadius="9" Background="White" Opacity="0"/>
    <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"
                      TextElement.Foreground="{DynamicResource Brush.OnAccent}" TextElement.FontWeight="SemiBold"/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Shine" Property="Opacity" Value="0.12"/></Trigger>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.96" ScaleY="0.96"/></Setter.Value></Setter>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Compact number field on a panel.</summary>
    public static TextBox NumberField()
    {
        var t = new TextBox
        {
            Width = 74,
            Height = 30,
            Padding = new Thickness(0),
            FontFamily = Font,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            SelectionOpacity = 0.45,
        };
        t.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
  <Border x:Name="Bd" CornerRadius="8" Background="{DynamicResource Brush.BgAlt}" BorderBrush="{DynamicResource Brush.Border}" BorderThickness="1">
    <ScrollViewer x:Name="PART_ContentHost" Margin="7,0" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.BorderStrong}"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        t.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text");
        t.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        t.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Accent");
        t.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        return t;
    }
}

/// <summary>A color dot of the curated palette; the chosen one gets an accent ring.</summary>
internal sealed class Swatch : ToggleButton
{
    private readonly Ellipse _ring = new() { StrokeThickness = 2, Opacity = 0 };

    public Swatch(Color color, string tip)
    {
        Color = color;
        Width = 30;
        Height = 30;
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = tip;
        var dot = new Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(color), StrokeThickness = 1 };
        // light colors get a hairline so white stays visible on a light panel
        dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.PanelBorder");
        _ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Accent");
        var g = new Grid { Background = Brushes.Transparent };
        g.Children.Add(_ring);
        g.Children.Add(dot);
        Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToggleButton"><ContentPresenter/></ControlTemplate>
""");
        Content = g;
        Checked += (_, _) =>
        {
            _ring.Opacity = 1;
            Anim.Bounce(dot, 1.22);
        };
        Unchecked += (_, _) => _ring.Opacity = 0;
        MouseEnter += (_, _) => Anim.Hover(dot, true);
        MouseLeave += (_, _) => Anim.Hover(dot, false);
    }

    public Color Color { get; }

    protected override void OnToggle()
    {
        if (IsChecked != true) base.OnToggle();
    }
}

/// <summary>A stroke-size choice: a dot of that size.</summary>
internal sealed class SizeChip : ToggleButton
{
    public SizeChip(double dot, string tip, int index)
    {
        Index = index;
        Width = 34;
        Height = 30;
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = tip;
        var e = new Ellipse { Width = dot, Height = dot };
        e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.PanelText");
        Content = e;
        Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
  <Border x:Name="Bd" CornerRadius="8" Background="Transparent"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.PanelHover}"/></Trigger>
    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.AccentSoft}"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        Checked += (_, _) => Anim.Bounce(e, 1.25);
    }

    public int Index { get; }

    protected override void OnToggle()
    {
        if (IsChecked != true) base.OnToggle();
    }
}
