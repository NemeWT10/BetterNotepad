using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core.Localization;
using BetterNotepad.Editor;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.Documents.Views;

/// <summary>Fallback when no module can show a document (the Info module replaces this).</summary>
public sealed class UnsupportedView : Grid, IDocumentView
{
    public UnsupportedView(Document doc)
    {
        Document = doc;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Icon { Glyph = Glyphs.Document, FontSize = 40, Margin = new Thickness(0, 0, 0, 14) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        var title = new TextBlock { Text = doc.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var hint = new TextBlock { Text = Loc.T("view.unsupported"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(hint);
        Children.Add(stack);
    }

    public Document Document { get; }

    public FrameworkElement Element => this;

    public CodeEditor? Editor => null;

    public void OnActivated() { }

    public void OnDeactivated() { }

    public void CaptureState() { }

    public void FocusContent() => Focus();

    public void Dispose() { }
}
