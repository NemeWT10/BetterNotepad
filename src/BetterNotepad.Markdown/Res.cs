using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterNotepad.Markdown;

/// <summary>Resource keys the renderer paints with. They live in the app theme dictionary.</summary>
internal static class K
{
    public const string Bg = "Brush.Editor.Bg";
    public const string Selection = "Brush.Editor.Selection";
    public const string Text = "Brush.Text";
    public const string TextDim = "Brush.TextDim";
    public const string TextFaint = "Brush.TextFaint";
    public const string Border = "Brush.Border";
    public const string BorderStrong = "Brush.BorderStrong";
    public const string Accent = "Brush.Accent";
    public const string AccentText = "Brush.AccentText";
    public const string AccentSoft = "Brush.AccentSoft";
    public const string OnAccent = "Brush.OnAccent";
    public const string Info = "Brush.Info";
    public const string InfoSoft = "Brush.InfoSoft";
    public const string Success = "Brush.Success";
    public const string SuccessSoft = "Brush.SuccessSoft";
    public const string Warning = "Brush.Warning";
    public const string WarningSoft = "Brush.WarningSoft";
    public const string Danger = "Brush.Danger";
    public const string DangerSoft = "Brush.DangerSoft";
    public const string CodeBg = "Brush.Md.CodeBg";
    public const string QuoteBar = "Brush.Md.QuoteBar";
    public const string TableHeader = "Brush.Md.TableHeader";
    public const string TableStripe = "Brush.Md.TableStripe";
}

internal static class Res
{
    public static T Fg<T>(this T e, string key) where T : TextElement
    {
        e.SetResourceReference(TextElement.ForegroundProperty, key);
        return e;
    }

    public static T Bg<T>(this T e, string key) where T : TextElement
    {
        e.SetResourceReference(TextElement.BackgroundProperty, key);
        return e;
    }

    public static T BorderFrom<T>(this T e, string key) where T : Block
    {
        e.SetResourceReference(Block.BorderBrushProperty, key);
        return e;
    }

    private static readonly object[] Boxes = Enumerable.Range(0, 256).Select(i => (object)i).ToArray();

    public static object Box(int i) => (uint)i < 256 ? Boxes[i] : i;

    public static DependencyObject? ParentOf(DependencyObject d) => LogicalTreeHelper.GetParent(d);

    /// <summary>Source line (0-based) of a rendered element: the line tags are relative to the top-level block.</summary>
    public static bool TryResolve(DependencyObject? start, out MarkdownBlockInfo? block, out int line0)
    {
        block = null;
        line0 = 0;
        var rel = -1;
        for (var d = start; d != null; d = ParentOf(d))
        {
            var tag = d switch
            {
                FrameworkContentElement fce => fce.Tag,
                FrameworkElement fe => fe.Tag,
                _ => null,
            };
            if (tag is MarkdownBlockInfo b)
            {
                block = b;
                line0 = b.StartLine0 + Math.Max(0, rel);
                return true;
            }

            if (rel < 0 && tag is int i) rel = i;
        }

        return false;
    }

    public static readonly FontFamily Emoji = new("Segoe UI Emoji, Segoe UI Symbol");
}
