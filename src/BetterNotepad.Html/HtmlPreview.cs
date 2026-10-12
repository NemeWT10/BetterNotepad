using System.Windows.Media;

namespace BetterNotepad.Html;

/// <summary>Browser-exact preview of an HTML file (see <see cref="WebPreview"/>).</summary>
public sealed class HtmlPreview : WebPreview
{
    public HtmlPreview(WebPreviewOptions? options = null)
        : base(options)
    {
    }

    protected override bool IsSvg => false;

    protected override Color PageBackground() => Colors.White;

    protected override string Compose(string text, string configTag)
    {
        var marked = HtmlLineMarker.Mark(text, svg: false);
        return marked.Text.Insert(marked.HeadInsert, configTag);
    }
}

/// <summary>
/// SVG preview: the markup inline in a small page, fitted to the window over a soft checkerboard in the theme's colors
/// (Ctrl+wheel zooms, double-click on empty space fits again, double-click on a shape jumps to its source line).
/// </summary>
public sealed class SvgPreview : WebPreview
{
    public SvgPreview(WebPreviewOptions? options = null)
        : base(options)
    {
    }

    protected override bool IsSvg => true;

    protected override bool BrowserZoom => false;

    protected override Color PageBackground() => ThemeBackground;

    protected override string Compose(string text, string configTag)
    {
        var marked = HtmlLineMarker.Mark(text, svg: true);
        var bg = ThemeBackground;
        var fg = ThemeForeground;
        var dark = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B < 128;
        var cellA = Css(Blend(bg, fg, dark ? 0.07 : 0.035));
        var cellB = Css(Blend(bg, fg, dark ? 0.12 : 0.085));
        var edge = Css(Blend(bg, fg, dark ? 0.16 : 0.12));
        var shadow = dark ? "rgba(0,0,0,.45)" : "rgba(30,35,64,.14)";
        return "<!doctype html><html><head><meta charset=\"utf-8\">" + configTag + "<style>" +
               "html,body{margin:0;height:100%;background:" + Css(bg) + ";}" +
               "body{display:flex;overflow:auto;}" +
               ".bn-stage{margin:auto;flex:none;line-height:0;border-radius:6px;" +
               "background-color:" + cellA + ";background-image:conic-gradient(" + cellB + " 25%,transparent 0 50%," + cellB + " 0 75%,transparent 0);" +
               "background-size:16px 16px;box-shadow:0 0 0 1px " + edge + ",0 10px 30px " + shadow + ";}" +
               ".bn-stage>svg{display:block;overflow:hidden;}" +
               "::-webkit-scrollbar{width:10px;height:10px}::-webkit-scrollbar-thumb{background:" + edge + ";border-radius:5px;border:2px solid " + Css(bg) + "}" +
               "</style></head><body><div class=\"bn-stage\">" + marked.Text + "</div></body></html>";
    }
}
