using System.Windows;
using System.Windows.Threading;
using BetterNotepad.Core.Commands;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;

namespace BetterNotepad.Modules.Imaging;

/// <summary>
/// Images (jpg, png, gif, bmp, ico, tiff, webp): a viewer with zoom, pan, fit, animated GIF and rotation, and a simple editor
/// (crop, rotate / flip, resize, drawings, text, blur / pixelate, undo) — BetterNotepad.Imaging. Edited pictures are unsaved
/// tabs like text: Ctrl+S, "Save as" in another format, "Save?" on close, and the edits survive a restart.
/// </summary>
public static class ImagingModule
{
    /// <summary>Test mode: what "Copy image" put on the (not touched) clipboard.</summary>
    internal static IDataObject? LastTestCopy { get; set; }

    public static void Register()
    {
        DocumentManager.I.RegisterFactory(new Factory());
        DocumentViewRegistry.RegisterView(DocumentKind.Image, d => new ImageDocumentView((ImageDocument)d));

        Cmd("image.edit", v => v.ToggleEdit());
        Cmd("image.info", v => v.ToggleInfo());
        Cmd("image.copy", v => v.Copy());
        Cmd("image.rotateRight", v => v.RotateBy(1));
        Cmd("image.rotateLeft", v => v.RotateBy(-1));
        Cmd("image.actualSize", v => v.ActualSize());

        // after the session is back: backups of images no tab shows any more (the app went down between a close and its cleanup)
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ImageBackup.DeleteOrphans);
        // quitting never asks: edits made in the last moment are written before the process ends
        if (Application.Current is { } app) app.Exit += (_, _) => FlushBackups();
    }

    internal static void FlushBackups()
    {
        foreach (var d in DocumentManager.I.Documents.OfType<ImageDocument>().ToList()) d.FlushBackup();
    }

    private static ImageDocumentView? ViewOf(CommandContext c) => AppCommands.WindowOf(c)?.ActiveView as ImageDocumentView;

    private static void Cmd(string id, Action<ImageDocumentView> run) => CommandRegistry.Register(new AppCommand
    {
        Id = id,
        TitleKey = "cmd." + id,
        CategoryKey = "cmd.cat.image",
        Kinds = [DocumentKind.Image],
        Execute = c =>
        {
            if (ViewOf(c) is { } v) run(v);
        },
        CanExecute = c => ViewOf(c) is not null,
    });

    private sealed class Factory : IDocumentFactory
    {
        public DocumentKind Kind => DocumentKind.Image;

        public Document Create(string path, Guid? id = null) => new ImageDocument(path, id);
    }
}
