namespace BetterNotepad.Imaging;

/// <summary>UI texts of the image viewer / editor. The app fills them from its PL / EN resources; defaults are a fallback.</summary>
public sealed class ImagingLabels
{
    // view
    public string ZoomIn { get; init; } = "Zoom in";
    public string ZoomOut { get; init; } = "Zoom out";
    public string ZoomLevel { get; init; } = "Zoom — click for 100 %";
    public string Fit { get; init; } = "Fit to window";
    public string ActualSize { get; init; } = "Actual size (100 %)";
    public string Rotate { get; init; } = "Rotate 90°";
    public string Play { get; init; } = "Play";
    public string Pause { get; init; } = "Pause";
    public string Edit { get; init; } = "Edit";
    public string Done { get; init; } = "Done";

    // tools
    public string Crop { get; init; } = "Crop";
    public string Pen { get; init; } = "Pen";
    public string Highlighter { get; init; } = "Highlighter";
    public string Line { get; init; } = "Line";
    public string Arrow { get; init; } = "Arrow";
    public string Rectangle { get; init; } = "Rectangle";
    public string Ellipse { get; init; } = "Ellipse";
    public string Text { get; init; } = "Text";
    public string Blur { get; init; } = "Blur";
    public string Pixelate { get; init; } = "Pixelate";
    public string ColorAndSize { get; init; } = "Color and thickness";
    public string RotateLeft { get; init; } = "Rotate left";
    public string RotateRight { get; init; } = "Rotate right";
    public string FlipHorizontal { get; init; } = "Flip horizontally";
    public string FlipVertical { get; init; } = "Flip vertically";
    public string Resize { get; init; } = "Resize";
    public string Undo { get; init; } = "Undo";
    public string Redo { get; init; } = "Redo";

    // panels
    public string Width { get; init; } = "Width";
    public string Height { get; init; } = "Height";
    public string KeepRatio { get; init; } = "Keep aspect ratio";
    public string Apply { get; init; } = "Apply";
    public string Cancel { get; init; } = "Cancel";
    public string ApplyCrop { get; init; } = "Apply crop (Enter)";
    public string CancelCrop { get; init; } = "Cancel (Esc)";
    public string Thin { get; init; } = "Thin";
    public string Medium { get; init; } = "Medium";
    public string Thick { get; init; } = "Thick";
    public string Pixels { get; init; } = "px";

    // context menu, status
    public string Copy { get; init; } = "Copy image";
    public string SaveAs { get; init; } = "Save as…";
    public string Copied { get; init; } = "Copied";
    public string Loading { get; init; } = "Opening image…";
    public string LoadFailed { get; init; } = "This image could not be opened.";
    public string TextHint { get; init; } = "Type, Enter to finish";

    /// <summary>"{0} × {1}" — pixel size in the info pill.</summary>
    public string SizeFormat { get; init; } = "{0} × {1}";

    /// <summary>"{0} frames" — animated images.</summary>
    public string FramesFormat { get; init; } = "{0} frames";
}

/// <summary>Settings of the image viewer.</summary>
public sealed class ImagingOptions
{
    public ImagingLabels Labels { get; set; } = new();

    /// <summary>The app's Animations switch (Motion.I.Enabled). Default: Windows' "Animation effects".</summary>
    public Func<bool> AnimationsEnabled { get; set; } = () => System.Windows.SystemParameters.ClientAreaAnimation;

    /// <summary>Plain mouse wheel zooms (default). False: wheel scrolls, Ctrl+wheel zooms (pinned windows).</summary>
    public bool WheelZooms { get; set; } = true;

    /// <summary>Show the size / format pill in the top-left corner.</summary>
    public bool ShowInfo { get; set; } = true;

    /// <summary>JPEG quality used by <see cref="ImageViewer.SaveAsync"/> (1..100).</summary>
    public int JpegQuality { get; set; } = 92;
}
