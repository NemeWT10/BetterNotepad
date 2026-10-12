using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterNotepad.Markdown;

internal static class MdStyles
{
    private static Style? _checkBox;

    /// <summary>Compact rounded checkbox painted with the app's accent brushes (task lists).</summary>
    public static Style CheckBox => _checkBox ??= (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <Border x:Name="Box" Width="17" Height="17" CornerRadius="5" BorderThickness="1.25" SnapsToDevicePixels="True"
                        BorderBrush="{DynamicResource Brush.BorderStrong}" Background="Transparent">
                  <Path x:Name="Mark" Data="M3.2 7.9 L6.2 10.8 L11.6 4.8" Stroke="{DynamicResource Brush.OnAccent}" StrokeThickness="1.8"
                        StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/>
                  </Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Box" Property="Background" Value="{DynamicResource Brush.Accent}"/>
                    <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/>
                    <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    private static readonly Dictionary<AlertKind, Geometry> AlertGeometry = new()
    {
        [AlertKind.Note] = Geometry.Parse("M8 1.8a6.2 6.2 0 1 0 0 12.4A6.2 6.2 0 0 0 8 1.8z M8 7.2v3.6 M8 4.9h.01"),
        [AlertKind.Tip] = Geometry.Parse("M5.9 12.3h4.2 M6.5 14.2h3 M8 1.7a4.4 4.4 0 0 0-2.5 8c.5.4.9 1 .9 1.7v.9h3.2v-.9c0-.7.4-1.3.9-1.7A4.4 4.4 0 0 0 8 1.7z"),
        [AlertKind.Important] = Geometry.Parse("M3 2h10a1.5 1.5 0 0 1 1.5 1.5v6A1.5 1.5 0 0 1 13 11H9.4L6 14.2V11H3a1.5 1.5 0 0 1-1.5-1.5v-6A1.5 1.5 0 0 1 3 2z M8 4.4v3 M8 8.9h.01"),
        [AlertKind.Warning] = Geometry.Parse("M8 2.1l6.3 11H1.7L8 2.1z M8 6.4v3.2 M8 11.5h.01"),
        [AlertKind.Caution] = Geometry.Parse("M5.2 1.8h5.6l3.4 3.4v5.6l-3.4 3.4H5.2l-3.4-3.4V5.2l3.4-3.4z M8 4.9v3.9 M8 10.8h.01"),
    };

    /// <summary>Outline icon (1.5 px, round caps) for a callout title.</summary>
    public static Path AlertIcon(AlertKind kind, string brushKey)
    {
        var p = new Path
        {
            Data = AlertGeometry[kind],
            Width = 16,
            Height = 16,
            StrokeThickness = 1.45,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 9, 0),
        };
        p.SetResourceReference(Shape.StrokeProperty, brushKey);
        return p;
    }
}
