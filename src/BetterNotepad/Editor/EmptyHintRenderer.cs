using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor;

/// <summary>
/// A faint hint on the first line of an empty untitled tab ("Start typing… · Ctrl+P open file · Ctrl+Shift+P commands").
/// Drawn by the text view itself, so it sits exactly where the text will start; gone with the first character.
/// </summary>
internal sealed class EmptyHintRenderer(CodeEditor editor) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public static string Text => Loc.T("editor.emptyHint", Keymap.GestureText("palette.files"), Keymap.GestureText("palette.commands"));

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (textView.Document is not { TextLength: 0 } || editor.Model is not { IsUntitled: true } || editor.IsReadOnly) return;
        var lineHeight = textView.DefaultLineHeight;
        var size = Math.Max(11, editor.FontSize * 0.92);
        var color = ThemeManager.I.Current.TextFaint;
        var text = new FormattedText(Text, Loc.I.Culture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B)), VisualTreeHelper.GetDpi(textView).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(40, textView.ActualWidth - 16),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(4 - textView.HorizontalOffset, (lineHeight - text.Height) / 2 - textView.VerticalOffset));
    }
}
