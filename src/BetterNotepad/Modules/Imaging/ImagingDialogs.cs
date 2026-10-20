using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Imaging;

/// <summary>The image tab's in-window dialogs: JPEG quality for "Save as", the one-time "saved without metadata" note.</summary>
internal static class ImagingDialogs
{
    /// <summary>The note's dialog is up (tests and snapshots).</summary>
    internal static bool MetadataNoticeOpen { get; private set; }

    /// <summary>"Save as" JPEG: a quality slider (1–100, the last choice preselected). Null = cancelled.</summary>
    public static async Task<int?> AskJpegQuality(MainWindow w, int initial)
    {
        var (panel, slider) = QualityPanel(initial);
        var answer = await w.DialogLayer.Show(Loc.T("img.save.jpegTitle"), Loc.T("img.save.jpegText"),
        [
            new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            new DialogButton("save", Loc.T("img.save.save"), DialogButtonKind.Primary, IsDefault: true),
        ], extra: panel, glyph: Glyphs.Image, focus: slider);
        return answer == "save" ? (int)Math.Round(slider.Value) : null;
    }

    /// <summary>The slider row of the quality dialog (also rendered by the snapshots).</summary>
    internal static (FrameworkElement Panel, Slider Slider) QualityPanel(int initial)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = Text(Loc.T("img.save.quality"), 12.5, "Brush.TextDim");
        label.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(label);
        var value = Text(initial.ToString(Loc.I.Culture), 20, "Brush.Text");
        value.FontWeight = FontWeights.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right; // over the slider's end
        value.Margin = new Thickness(0, 0, 2, -2);
        value.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var slider = new Slider { Minimum = 1, Maximum = 100, Value = Math.Clamp(initial, 1, 100), SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 8, 0, 4) };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "Slider");
        slider.ValueChanged += (_, e) => value.Text = ((int)Math.Round(e.NewValue)).ToString(Loc.I.Culture);
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        grid.Children.Add(slider);

        var smaller = Text(Loc.T("img.save.smaller"), 11.5, "Brush.TextFaint");
        var better = Text(Loc.T("img.save.better"), 11.5, "Brush.TextFaint");
        better.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(smaller, 2);
        Grid.SetRow(better, 2);
        Grid.SetColumn(better, 1);
        grid.Children.Add(smaller);
        grid.Children.Add(better);
        return (grid, slider);
    }

    /// <summary>
    /// The first save that dropped EXIF / GPS (the source had some): a short note, once. Not awaited — the "Saved" toast shows
    /// too, and the user reads it in their own time.
    /// </summary>
    public static void MetadataNoticeOnce(MainWindow? w)
    {
        var s = SettingsService.I.Get<ImagingSettings>();
        if (s.MetadataNoticeShown || w is null) return;
        s.MetadataNoticeShown = true;
        _ = ShowMetadataNotice(w);
    }

    internal static async Task ShowMetadataNotice(MainWindow w)
    {
        MetadataNoticeOpen = true;
        try
        {
            var note = Text(Loc.T("img.meta.once"), 12, "Brush.TextFaint");
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 17;
            await w.DialogLayer.Show(Loc.T("img.meta.title"), Loc.T("img.meta.text"),
            [
                new DialogButton("ok", Loc.T("img.meta.ok"), DialogButtonKind.Primary, IsDefault: true, IsCancel: true),
            ], extra: note, glyph: Glyphs.Shield);
        }
        finally
        {
            MetadataNoticeOpen = false;
        }
    }

    private static TextBlock Text(string text, double size, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
