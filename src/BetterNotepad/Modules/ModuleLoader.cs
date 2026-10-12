namespace BetterNotepad.Modules;

/// <summary>
/// Registration point for feature modules (Markdown, PDF, HTML, images, metadata, system…).
/// Each module adds one line here that registers its document factory, views, previews, commands
/// and settings sections. Localized strings are registered by the modules themselves from a
/// [ModuleInitializer]. Called once at startup, after the core commands and before the session loads.
/// </summary>
public static class ModuleLoader
{
    public static void RegisterAll()
    {
        Markdown.MarkdownModule.Register();
        Pdf.PdfModule.Register(); // pages, thumbnails, zoom, search, copy, print, PDF -> Word
        Info.InfoModule.Register(); // binary files as hex, everything else non-viewable as an Info tab (size, hashes, metadata)
        Sys.SystemModule.Register(); // tray, hotkey, autostart, file types, install, updates
    }
}
