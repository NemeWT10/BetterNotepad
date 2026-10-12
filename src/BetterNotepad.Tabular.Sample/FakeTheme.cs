using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace BetterNotepad.Tabular.Sample;

/// <summary>Brush.* resources copied from the app's Aurora / Daylight palettes (Palettes.cs, ThemeManager.Build) plus the app's slim ScrollBar style (Base.xaml).</summary>
internal static class FakeTheme
{
    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static ResourceDictionary Aurora() => Build(
        bg: "#0F1117", border: "#252A37", borderStrong: "#353B4D", text: "#EEF0F7", textDim: "#A2A8B8", textFaint: "#6A7185",
        editorBg: "#12141C", editorText: "#E4E7F0", selection: "#4D7C5CFF", selectionInactive: "#2A7C5CFF", currentLine: "#0CFFFFFF", caret: "#B39DFF");

    public static ResourceDictionary Daylight() => Build(
        bg: "#F5F6FA", border: "#E2E5EE", borderStrong: "#CBD0DD", text: "#161A26", textDim: "#586074", textFaint: "#7E8699",
        editorBg: "#FFFFFF", editorText: "#1B2030", selection: "#334F46E5", selectionInactive: "#1A4F46E5", currentLine: "#0A4F46E5", caret: "#4F46E5");

    private static ResourceDictionary Build(string bg, string border, string borderStrong, string text, string textDim, string textFaint,
        string editorBg, string editorText, string selection, string selectionInactive, string currentLine, string caret)
    {
        var d = new ResourceDictionary();

        void C(string name, string hex)
        {
            var c = H(hex);
            d["Color." + name] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            d["Brush." + name] = b;
        }

        C("Bg", bg);
        C("Border", border);
        C("BorderStrong", borderStrong);
        C("Text", text);
        C("TextDim", textDim);
        C("TextFaint", textFaint);
        C("Editor.Bg", editorBg);
        C("Editor.Text", editorText);
        C("Editor.Selection", selection);
        C("Editor.SelectionInactive", selectionInactive);
        C("Editor.CurrentLine", currentLine);
        C("Editor.Caret", caret);

        d.MergedDictionaries.Add(ScrollBarStyle());
        return d;
    }

    /// <summary>The vertical part of the app's slim scrollbar style (Base.xaml), verbatim.</summary>
    private static ResourceDictionary ScrollBarStyle() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style TargetType="ScrollBar">
                <Setter Property="Width" Value="10"/>
                <Setter Property="MinWidth" Value="10"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ScrollBar">
                            <Track x:Name="PART_Track" IsDirectionReversed="True">
                                <Track.DecreaseRepeatButton>
                                    <RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/>
                                </Track.DecreaseRepeatButton>
                                <Track.IncreaseRepeatButton>
                                    <RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/>
                                </Track.IncreaseRepeatButton>
                                <Track.Thumb>
                                    <Thumb>
                                        <Thumb.Template>
                                            <ControlTemplate TargetType="Thumb">
                                                <Border x:Name="T" CornerRadius="4" Background="{DynamicResource Brush.BorderStrong}" Margin="2" Opacity="0.75"/>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property="IsMouseOver" Value="True">
                                                        <Setter TargetName="T" Property="Opacity" Value="1"/>
                                                    </Trigger>
                                                    <Trigger Property="IsDragging" Value="True">
                                                        <Setter TargetName="T" Property="Background" Value="{DynamicResource Brush.TextFaint}"/>
                                                    </Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
        </ResourceDictionary>
        """);
}
