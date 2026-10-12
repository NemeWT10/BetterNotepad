using System.Windows;

namespace BetterNotepad.Imaging;

/// <summary>Hooks for the render harness (BetterNotepad.Ui.Sample); not part of the app surface.</summary>
public sealed partial class ImageViewer
{
    internal void PushEdit(EditOp op) => _doc.Push(op);

    internal double RasterScale => _rasterScale;

    internal bool HasDetail => _detail is not null;

    internal bool HasPicture => _proxy is not null;

    internal Point ImageToStage(Point image) => ToScreen(image);

    internal void OpenStylePanel() => TogglePanel(_stylePanel, _styleButton);

    internal void OpenResizePanel() => TogglePanel(_resizePanel, _resizeButton);

    internal void SetSize(int index) => _sizeChips[index].IsChecked = true;

    internal void SetColor(int index) => _swatches[index].IsChecked = true;

    internal void TypeText(string text)
    {
        _textInput.Text = text;
        CommitText();
    }

    internal void ApplyCropForTest() => ApplyCrop();

    internal FrameworkElement ToolbarElement => _toolbar;

    internal Task WaitDetailAsync() => _detailTask ?? Task.CompletedTask;
}
