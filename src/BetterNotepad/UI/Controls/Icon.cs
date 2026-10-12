using System.Windows;
using System.Windows.Controls;

namespace BetterNotepad.UI.Controls;

/// <summary>Line icon from Segoe Fluent Icons (Windows 11). Glyph codes live in <see cref="Glyphs"/>.</summary>
public class Icon : TextBlock
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Icon), new PropertyMetadata("", (d, e) => ((Icon)d).Text = (string)e.NewValue));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}

/// <summary>Segoe Fluent Icons code points used across the app.</summary>
public static class Glyphs
{
    public const string Add = "";
    public const string Close = "";
    public const string ChromeClose = "";
    public const string ChromeMinimize = "";
    public const string ChromeMaximize = "";
    public const string ChromeRestore = "";
    public const string Save = "";
    public const string Document = "";
    public const string Page = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Pinned = "";
    public const string Lock = "";
    public const string Unlock = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Edit = "";
    public const string View = "";
    public const string Split = ""; // DockRight: a window with a right pane
    public const string Columns = "";
    public const string Table = "";
    public const string Text = "";
    public const string Folder = "";
    public const string OpenFolder = "";
    public const string Copy = "";
    public const string Rename = "";
    public const string Checkmark = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";
    public const string ChevronUp = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Error = "";
    public const string Refresh = "";
    public const string Compare = ""; // Switch (two arrows)
    public const string Code = "";
    public const string Image = "";
    public const string Pdf = "";
    public const string Globe = "";
    public const string Keyboard = "";
    public const string Color = "";
    public const string Bookmark = "";
    public const string History = "";
    public const string Download = "";
    public const string Print = "";
    public const string More = "";
    public const string Filter = "";
    public const string Replace = "";
    public const string RegEx = "";
    public const string CaseSensitive = "";
    public const string WholeWord = "";
    public const string Selection = "";
    public const string ArrowUp = "";
    public const string ArrowDown = "";
    public const string Ghost = "";
    public const string Window = "";
    public const string NewWindow = "";
    public const string Shield = "";
    public const string Hex = "";
    public const string Delete = "";
    public const string Csv = "";
    public const string Markdown = "";
    public const string Sparkle = "";
    public const string Heart = "";
    public const string Clock = "";
    public const string List = "";
    public const string ChevronLeft = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string Cut = "";
    public const string Paste = "";
    public const string SaveAs = "";
    public const string SaveAll = "";
    public const string CloseAll = "";
    public const string CloseThin = "";
    public const string Power = "";
    public const string GoTo = "";
    public const string Comment = "";
    public const string Indent = "";
    public const string Outdent = "";
    public const string Merge = "";
    public const string Switch = "";
    public const string WordWrap = "";
    public const string Whitespace = "";
    public const string LineNumbers = "";
    public const string ZoomIn = "";
    public const string ZoomOut = "";
    public const string Zoom = "";
    public const string OpenWith = "";
    public const string CloseRight = "";
    public const string SplitRight = "";
    public const string SplitDown = "";
}
