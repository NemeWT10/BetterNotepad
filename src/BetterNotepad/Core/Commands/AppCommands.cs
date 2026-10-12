using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Editor.Find;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;

namespace BetterNotepad;

/// <summary>
/// The core commands (palette entries, menu items, rebindable shortcuts). Titles live in
/// Strings.Commands.cs under "cmd.&lt;id&gt;". Editor commands resolve their editor with
/// <see cref="EditorOf"/>, so they also work when run from the palette or a menu (editor not focused).
/// Shortcuts follow VS Code; never Ctrl+Alt+letter (AltGr on Polish keyboards).
/// </summary>
public static class AppCommands
{
    private const string CatGeneral = "cmd.cat.general";
    private const string CatFile = "cmd.cat.file";
    private const string CatEdit = "cmd.cat.edit";
    private const string CatView = "cmd.cat.view";
    private const string CatGo = "cmd.cat.go";
    private const string CatTools = "cmd.cat.tools";
    private const string CatTabs = "cmd.cat.tabs";
    private const string CatApp = "cmd.cat.app";

    // ───────────── context helpers (public: modules use them too) ─────────────

    public static MainWindow? WindowOf(CommandContext c) => c.Window as MainWindow;

    /// <summary>The focused editor, else the editor of the active tab.</summary>
    public static CodeEditor? EditorOf(CommandContext c) => c.Editor as CodeEditor ?? WindowOf(c)?.ActiveView?.Editor;

    public static TextDocument? TextOf(CommandContext c) => (c.Document ?? WindowOf(c)?.ActiveDocument) as TextDocument;

    private static Document? DocOf(CommandContext c) => c.Document ?? WindowOf(c)?.ActiveDocument;

    private static TabGroup? GroupOf(CommandContext c) => c.Group ?? WindowOf(c)?.Model.ActiveGroup;

    /// <summary>Word wrap currently on for this document (code and text have separate settings).</summary>
    public static bool IsWordWrapOn(TextDocument? doc)
    {
        var s = SettingsService.I.Get<EditorSettings>();
        return doc is null || Languages.Get(doc.LanguageId).IsCode ? s.WordWrapCode : s.WordWrapText;
    }

    public static bool IsLineNumbersOn(TextDocument? doc)
    {
        var s = SettingsService.I.Get<EditorSettings>();
        return doc is null || Languages.Get(doc.LanguageId).IsCode ? s.LineNumbersCode : s.LineNumbersText;
    }

    // ───────────── registration ─────────────

    private static void Reg(
        string id, string category, string? gesture, Action<CommandContext> run,
        CommandScope scope = CommandScope.Window, Func<CommandContext, bool>? can = null,
        DocumentKind[]? kinds = null, string? search = null, bool palette = true)
        => CommandRegistry.Register(new AppCommand
        {
            Id = id,
            TitleKey = "cmd." + id,
            CategoryKey = category,
            DefaultGesture = gesture,
            Scope = scope,
            Kinds = kinds,
            SearchTerms = search,
            ShowInPalette = palette,
            Execute = run,
            CanExecute = can,
        });

    public static void RegisterAll()
    {
        RegisterFile();
        RegisterTabs();
        RegisterGo();
        RegisterView();
        RegisterEdit();
        RegisterEditor();
        EditorCommands.Register();
        RegisterTools();
#if DEBUG
        foreach (var cmd in CommandRegistry.All)
        {
            if (!Loc.AllEntries.ContainsKey(cmd.TitleKey)) Log.Warn($"missing title string: {cmd.TitleKey}");
            if (!Loc.AllEntries.ContainsKey(cmd.CategoryKey)) Log.Warn($"missing category string: {cmd.CategoryKey}");
        }
#endif
    }

    // ───────────── file, app ─────────────

    private static void RegisterFile()
    {
        Reg("file.new", CatFile, "Ctrl+N", c => WindowOf(c)?.NewTab(GroupOf(c)), search: "new nowy plik notatka");
        Reg("file.open", CatFile, "Ctrl+O", c => WindowOf(c)?.OpenDialog(), search: "open otwórz");
        Reg("file.save", CatFile, "Ctrl+S", c => MainWindow.Fire(WindowOf(c)!.SaveAsync(DocOf(c))),
            can: c => WindowOf(c) is not null && DocOf(c)?.CanSave == true, search: "zapisz");
        Reg("file.saveAs", CatFile, "Ctrl+Shift+S", c => MainWindow.Fire(WindowOf(c)!.SaveAsync(DocOf(c), saveAs: true)),
            can: c => WindowOf(c) is not null && DocOf(c)?.CanSave == true, search: "zapisz jako");
        Reg("file.saveAll", CatFile, null, c => MainWindow.Fire(WindowOf(c)!.SaveAllAsync()), can: c => WindowOf(c) is not null);
        Reg("file.close", CatFile, "Ctrl+W", CloseActive, can: c => DocOf(c) is not null);
        Reg("file.closeAlt", CatFile, "Ctrl+F4", CloseActive, can: c => DocOf(c) is not null, palette: false);
        Reg("file.closeOthers", CatFile, null,
            c => MainWindow.Fire(WindowOf(c)!.CloseOthersAsync(GroupOf(c)!, DocOf(c)!)),
            can: c => DocOf(c) is not null && GroupOf(c) is { Tabs.Count: > 1 });
        Reg("file.closeToRight", CatFile, null,
            c => MainWindow.Fire(WindowOf(c)!.CloseToRightAsync(GroupOf(c)!, DocOf(c)!)),
            can: c => DocOf(c) is { } d && GroupOf(c) is { } g && g.Tabs.IndexOf(d) < g.Tabs.Count - 1);
        Reg("file.closeSaved", CatFile, null, c => MainWindow.Fire(WindowOf(c)!.CloseSavedAsync(GroupOf(c)!)), can: c => GroupOf(c) is not null);
        Reg("file.closeAll", CatFile, null, c => MainWindow.Fire(WindowOf(c)!.CloseAllAsync(GroupOf(c)!)), can: c => GroupOf(c) is { Tabs.Count: > 0 });
        Reg("file.reopenClosed", CatFile, "Ctrl+Shift+T", c => WindowOf(c)?.ReopenClosed());
        Reg("file.rename", CatFile, "F2", c => WindowOf(c)?.BeginRename(DocOf(c)), can: c => DocOf(c) is not null, search: "zmień nazwę rename");
        Reg("file.toggleLock", CatFile, null, c => WindowOf(c)?.SetLocked(DocOf(c)!, !DocOf(c)!.IsLocked),
            can: c => DocOf(c) is not null, search: "tylko do odczytu read-only kłódka lock unlock zablokuj odblokuj");
        Reg("file.reveal", CatFile, null, c => WindowOf(c)?.RevealInExplorer(DocOf(c)!),
            can: c => DocOf(c)?.FilePath is not null, search: "explorer eksplorator folder");
        Reg("file.copyPath", CatFile, null, c => WindowOf(c)?.CopyPath(DocOf(c)!, nameOnly: false), can: c => DocOf(c)?.FilePath is not null);
        Reg("file.copyName", CatFile, null, c => WindowOf(c)?.CopyPath(DocOf(c)!, nameOnly: true), can: c => DocOf(c) is not null);
        Reg("file.toggleTail", CatFile, null, ToggleTail, can: c => TextOf(c)?.FilePath is not null, search: "tail log śledzenie");
        Reg("file.compareWithDisk", CatFile, null, c => MainWindow.Fire(WindowOf(c)!.OpenCompareWithDisk(TextOf(c)!)),
            can: c => TextOf(c)?.FilePath is not null, search: "diff porównaj");
        Reg("file.compareTabs", CatFile, null, CompareTabs, can: c => TextOf(c) is not null && OtherTextTab(c) is not null, search: "diff porównaj");

        Reg("app.quit", CatApp, null, _ => App.Current.Quit(), search: "exit zakończ wyjdź");
        Reg("app.settings", CatApp, "Ctrl+,", c => WindowOf(c)?.ShowSettings(), search: "settings ustawienia opcje");
    }

    private static void CloseActive(CommandContext c)
    {
        if (WindowOf(c) is { } w && GroupOf(c) is { } g && DocOf(c) is { } d) MainWindow.Fire(w.CloseTabAsync(g, d));
    }

    private static void ToggleTail(CommandContext c)
    {
        if (TextOf(c) is not { } t || WindowOf(c) is not { } w) return;
        t.TailMode = !t.TailMode;
        if (t.TailMode) EditorOf(c)?.ScrollToEnd();
        w.Toasts.Show(Loc.T(t.TailMode ? "tail.on" : "tail.off", t.Title), ToastKind.Info, seconds: 1.8);
    }

    /// <summary>The tab to compare with: the other group's active tab, else the previously used one.</summary>
    private static TextDocument? OtherTextTab(CommandContext c)
    {
        var w = WindowOf(c);
        var current = TextOf(c);
        if (w is null || current is null) return null;
        var other = w.Model.Groups.Where(g => g != w.Model.ActiveGroup).Select(g => g.Active).OfType<TextDocument>().FirstOrDefault(d => d != current);
        return other ?? w.Model.ActiveGroup.Mru.OfType<TextDocument>().FirstOrDefault(d => d != current);
    }

    private static void CompareTabs(CommandContext c)
    {
        if (WindowOf(c) is { } w && TextOf(c) is { } a && OtherTextTab(c) is { } b) MainWindow.Fire(w.CompareTabs(a, b));
    }

    // ───────────── tabs ─────────────

    private static void RegisterTabs()
    {
        Reg("tabs.next", CatTabs, "Ctrl+Tab", c => WindowOf(c)?.OpenTabSwitcher(reverse: false), search: "ostatnio używane mru");
        Reg("tabs.prev", CatTabs, "Ctrl+Shift+Tab", c => WindowOf(c)?.OpenTabSwitcher(reverse: true), search: "ostatnio używane mru");
        Reg("tabs.nextInOrder", CatTabs, "Ctrl+PgDn", c => StepTab(c, +1));
        Reg("tabs.prevInOrder", CatTabs, "Ctrl+PgUp", c => StepTab(c, -1));
    }

    private static void StepTab(CommandContext c, int dir)
    {
        if (GroupOf(c) is not { Tabs.Count: > 1 } g || g.Active is null) return;
        var i = (g.Tabs.IndexOf(g.Active) + dir + g.Tabs.Count) % g.Tabs.Count;
        g.Active = g.Tabs[i];
    }

    // ───────────── palette, go to ─────────────

    private static void RegisterGo()
    {
        Reg("palette.commands", CatGo, "Ctrl+Shift+P", c => WindowOf(c)?.ShowPalette(PaletteMode.Commands), search: "command palette polecenia");
        Reg("palette.commandsAlt", CatGo, "F1", c => WindowOf(c)?.ShowPalette(PaletteMode.Commands), palette: false);
        Reg("palette.files", CatGo, "Ctrl+P", c => WindowOf(c)?.ShowPalette(PaletteMode.Files), search: "quick open pliki");
        Reg("editor.goToLine", CatGo, "Ctrl+G", c => WindowOf(c)?.ShowPalette(PaletteMode.GoToLine), can: c => EditorOf(c) is not null, search: "line linia");
        Reg("palette.symbols", CatGo, "Ctrl+Shift+O", c => WindowOf(c)?.ShowPalette(PaletteMode.Symbols), can: c => EditorOf(c) is not null, search: "symbol funkcja klasa");
        Reg("editor.changeLanguage", CatGo, null, c => WindowOf(c)?.ShowPalette(PaletteMode.Language), can: c => TextOf(c) is not null,
            search: "language język kolorowanie składni syntax highlighting");
    }

    // ───────────── view ─────────────

    private static void RegisterView()
    {
        Reg("view.splitRight", CatView, "Ctrl+\\", c => WindowOf(c)?.Split(Orientation.Horizontal), search: "podziel split");
        Reg("view.splitDown", CatView, null, c => WindowOf(c)?.Split(Orientation.Vertical), search: "podziel split");
        Reg("view.unsplit", CatView, null, c => WindowOf(c)?.Unsplit(), can: c => WindowOf(c)?.Model.Groups.Count > 1);
        Reg("view.focusOtherGroup", CatView, null, c => WindowOf(c)?.FocusOtherGroup(), can: c => WindowOf(c)?.Model.Groups.Count > 1);
        Reg("view.moveToNewWindow", CatView, null, c => WindowOf(c)?.MoveToNewWindow(GroupOf(c)!, DocOf(c)!),
            can: c => DocOf(c) is not null && GroupOf(c) is not null, search: "okno window");
        Reg("view.togglePin", CatView, null, TogglePin, can: c => DocOf(c) is not null, search: "przypnij pin");
        // pinned on top: a tab in its own compact always-on-top window; ghost mode = translucent + click-through
        Reg("view.pinOnTop", CatView, null, c => WindowOf(c)?.PinOnTop(GroupOf(c)!, DocOf(c)!),
            can: c => DocOf(c) is not null && GroupOf(c) is not null && WindowOf(c) is { Model.Pinned: false },
            search: "pin on top always on top zawsze na wierzchu przypnij okno pływające floating");
        Reg("view.unpinFromTop", CatView, null, c => WindowOf(c)?.UnpinFromTop(), can: c => WindowOf(c) is { Model.Pinned: true },
            search: "unpin odepnij wierzch top");
        Reg("view.toggleGhost", CatView, null, c =>
        {
            if (WindowOf(c) is { Model.Pinned: true } w) w.SetGhost(!w.Model.Ghost);
            else PinnedWindows.ToggleGhostGlobal();
        }, can: _ => PinnedWindows.All.Any(), search: "ghost duch przezroczysty przenikanie click-through transparent");
        Reg("view.cyclePreview", CatView, "Ctrl+Shift+V", CyclePreview,
            can: c => TextOf(c) is { } t && FileTypes.HasPreview(t.Kind) && DocumentViewRegistry.HasPreview(t.Kind), search: "preview podgląd podział split");
        Reg("view.wordWrap", CatView, "Alt+Z", c =>
        {
            var s = SettingsService.I.Get<EditorSettings>();
            if (TextOf(c) is not { } t || Languages.Get(t.LanguageId).IsCode) s.WordWrapCode = !s.WordWrapCode;
            else s.WordWrapText = !s.WordWrapText;
        }, can: c => TextOf(c) is not null, search: "zawijanie wrap");
        Reg("view.whitespace", CatView, null, _ =>
        {
            var s = SettingsService.I.Get<EditorSettings>();
            s.ShowWhitespace = !s.ShowWhitespace;
        }, search: "białe znaki whitespace");
        Reg("view.lineNumbers", CatView, null, c =>
        {
            var s = SettingsService.I.Get<EditorSettings>();
            if (TextOf(c) is not { } t || Languages.Get(t.LanguageId).IsCode) s.LineNumbersCode = !s.LineNumbersCode;
            else s.LineNumbersText = !s.LineNumbersText;
        }, can: c => TextOf(c) is not null);
        Reg("view.zoomIn", CatView, "Ctrl+=", c => Zoom(c, +1), search: "powiększ zoom");
        Reg("view.zoomInAlt", CatView, "Ctrl+NumPlus", c => Zoom(c, +1), palette: false);
        Reg("view.zoomOut", CatView, "Ctrl+-", c => Zoom(c, -1), search: "pomniejsz zoom");
        Reg("view.zoomOutAlt", CatView, "Ctrl+NumMinus", c => Zoom(c, -1), palette: false);
        Reg("view.zoomReset", CatView, "Ctrl+0", c => Zoom(c, 0), search: "zoom reset dopasuj fit width");
    }

    /// <summary>
    /// Zoom of the active tab: views with their own zoom (PDF: toolbar steps, reset = fit width) take it,
    /// text editors share the editor zoom. <paramref name="dir"/> 0 = reset.
    /// </summary>
    private static void Zoom(CommandContext c, int dir)
    {
        if (WindowOf(c)?.ActiveView is IZoomableView view)
        {
            if (dir == 0) view.ZoomReset();
            else view.ZoomStep(dir);
            return;
        }
        if (dir == 0) CodeEditor.ResetZoom();
        else CodeEditor.Zoom(dir * 0.1);
    }

    private static void TogglePin(CommandContext c)
    {
        if (DocOf(c) is not { } d || GroupOf(c) is not { } g) return;
        d.IsPinned = !d.IsPinned;
        g.Normalize(d);
    }

    private static void CyclePreview(CommandContext c)
    {
        if (TextOf(c) is not { } t) return;
        t.ViewMode = t.Kind == DocumentKind.Csv
            ? (t.ViewMode == ViewMode.Preview ? ViewMode.Edit : ViewMode.Preview)
            : t.ViewMode switch { ViewMode.Preview => ViewMode.Split, ViewMode.Split => ViewMode.Edit, _ => ViewMode.Preview };
    }

    // ───────────── edit (clipboard, undo) ─────────────

    private static void RegisterEdit()
    {
        // Ctrl+Z / Y / X / C / V / A are handled by the editor itself; these exist for menus and the palette
        Reg("edit.undo", CatEdit, null, c => EditorOf(c)?.Undo(), can: c => EditorOf(c) is { CanUndo: true });
        Reg("edit.redo", CatEdit, null, c => EditorOf(c)?.Redo(), can: c => EditorOf(c) is { CanRedo: true });
        Reg("edit.cut", CatEdit, null, c => EditorOf(c)?.Cut(), can: c => EditorOf(c) is { IsReadOnly: false });
        Reg("edit.copy", CatEdit, null, c => EditorOf(c)?.Copy(), can: c => EditorOf(c) is not null);
        Reg("edit.paste", CatEdit, null, c => EditorOf(c)?.Paste(), can: c => EditorOf(c) is { IsReadOnly: false });
        Reg("edit.selectAll", CatEdit, null, c => EditorOf(c)?.SelectAll(), can: c => EditorOf(c) is not null);
    }

    // ───────────── editor ─────────────

    private static void RegisterEditor()
    {
        Reg("editor.find", CatEdit, "Ctrl+F", c => FindOf(c)?.Open(false), can: c => FindOf(c) is not null, search: "szukaj search");
        Reg("editor.replace", CatEdit, "Ctrl+H", c => FindOf(c)?.Open(true), can: c => FindOf(c) is not null, search: "zamień replace");
        Reg("editor.findNext", CatEdit, "F3", c => FindOf(c)?.FindNext(), can: c => FindOf(c) is not null);
        Reg("editor.findPrev", CatEdit, "Shift+F3", c => FindOf(c)?.FindPrevious(), can: c => FindOf(c) is not null);

        var ed = CommandScope.Editor;
        Func<CommandContext, bool> hasEditor = c => EditorOf(c) is { IsReadOnly: false };
        Reg("editor.moveLineUp", CatEdit, "Alt+Up", c => EditorOps.MoveLines(EditorOf(c)!, -1), ed, hasEditor);
        Reg("editor.moveLineDown", CatEdit, "Alt+Down", c => EditorOps.MoveLines(EditorOf(c)!, +1), ed, hasEditor);
        Reg("editor.copyLineUp", CatEdit, "Shift+Alt+Up", c => EditorOps.CopyLines(EditorOf(c)!, -1), ed, hasEditor);
        Reg("editor.copyLineDown", CatEdit, "Shift+Alt+Down", c => EditorOps.CopyLines(EditorOf(c)!, +1), ed, hasEditor);
        Reg("editor.deleteLine", CatEdit, "Ctrl+Shift+K", c => EditorOps.DeleteLines(EditorOf(c)!), ed, hasEditor);
        Reg("editor.insertLineBelow", CatEdit, "Ctrl+Enter", c => EditorOps.InsertLine(EditorOf(c)!, above: false), ed, hasEditor);
        Reg("editor.insertLineAbove", CatEdit, "Ctrl+Shift+Enter", c => EditorOps.InsertLine(EditorOf(c)!, above: true), ed, hasEditor);
        Reg("editor.toggleComment", CatEdit, "Ctrl+/", c => EditorOps.ToggleLineComment(EditorOf(c)!, LanguageOf(c)), ed, hasEditor, search: "komentarz comment");
        Reg("editor.blockComment", CatEdit, "Shift+Alt+A", c => EditorOps.ToggleBlockComment(EditorOf(c)!, LanguageOf(c)), ed, hasEditor, search: "komentarz comment");
        Reg("editor.indent", CatEdit, "Ctrl+]", c => EditorOps.Indent(EditorOf(c)!, outdent: false), ed, hasEditor, search: "wcięcie indent");
        Reg("editor.outdent", CatEdit, "Ctrl+[", c => EditorOps.Indent(EditorOf(c)!, outdent: true), ed, hasEditor, search: "wcięcie outdent");
        Reg("editor.selectLine", CatEdit, "Ctrl+L", c => EditorOps.SelectLine(EditorOf(c)!), ed, c => EditorOf(c) is not null);
    }

    private static FindBar? FindOf(CommandContext c) => (WindowOf(c)?.ActiveView as TextDocumentView)?.Find;

    private static string LanguageOf(CommandContext c) => TextOf(c)?.LanguageId ?? Languages.PlainText;

    // ───────────── tools (palette only) ─────────────

    private static void RegisterTools()
    {
        Func<CommandContext, bool> hasEditor = c => EditorOf(c) is { IsReadOnly: false };
        Reg("tools.sortAsc", CatTools, null, c => Transform(c, s => EditorOps.SortLines(s, false), true), can: hasEditor, search: "sort sortuj");
        Reg("tools.sortDesc", CatTools, null, c => Transform(c, s => EditorOps.SortLines(s, true), true), can: hasEditor, search: "sort sortuj");
        Reg("tools.uniqueLines", CatTools, null, c => Transform(c, EditorOps.UniqueLines, true), can: hasEditor, search: "unique duplikaty deduplikuj");
        Reg("tools.trimTrailing", CatTools, null, c => Transform(c, EditorOps.TrimTrailing, true), can: hasEditor, search: "trim spacje");
        Reg("tools.upper", CatTools, null, c => Transform(c, s => s.ToUpper(Loc.I.Culture)), can: hasEditor, search: "uppercase wielkie litery");
        Reg("tools.lower", CatTools, null, c => Transform(c, s => s.ToLower(Loc.I.Culture)), can: hasEditor, search: "lowercase małe litery");
        Reg("tools.title", CatTools, null, c => Transform(c, EditorOps.TitleCase), can: hasEditor, search: "title case");
        Reg("tools.base64Encode", CatTools, null, c => Transform(c, EditorOps.Base64Encode), can: hasEditor, search: "base64");
        Reg("tools.base64Decode", CatTools, null, c => Transform(c, EditorOps.Base64Decode), can: hasEditor, search: "base64");
        Reg("tools.urlEncode", CatTools, null, c => Transform(c, EditorOps.UrlEncode), can: hasEditor, search: "url percent");
        Reg("tools.urlDecode", CatTools, null, c => Transform(c, EditorOps.UrlDecode), can: hasEditor, search: "url percent");
        Reg("tools.formatDocument", CatTools, "Shift+Alt+F", c => Format(c, minify: false), can: hasEditor, search: "format json xml pretty print formatuj");
        Reg("tools.minify", CatTools, null, c => Format(c, minify: true), can: hasEditor, search: "minify json xml zminifikuj");
    }

    private static void Transform(CommandContext c, Func<string, string> fn, bool wholeLines = false)
    {
        if (EditorOf(c) is not { } ed) return;
        var w = WindowOf(c);
        try
        {
            EditorOps.Transform(ed, fn, wholeLines);
        }
        catch (Exception ex)
        {
            w?.Toasts.Show(Loc.T("tools.failed", ex.Message), ToastKind.Error);
        }
    }

    /// <summary>Whole-document JSON / XML format or minify, chosen by the document's language.</summary>
    private static void Format(CommandContext c, bool minify)
    {
        if (EditorOf(c) is not { } ed || TextOf(c) is not { } text) return;
        var w = WindowOf(c);
        Func<string, string>? fn = text.LanguageId switch
        {
            "json" => s => EditorOps.JsonFormat(s, minify),
            "xml" => s => EditorOps.XmlFormat(s, minify),
            _ => null,
        };
        if (fn is null)
        {
            w?.Toasts.Show(Loc.T("tools.formatUnsupported"), ToastKind.Warning);
            return;
        }
        try
        {
            var doc = ed.Document;
            var input = doc.Text;
            var nl = text.Eol.Text();
            var output = fn(input).Replace("\r\n", "\n").Replace("\n", nl);
            if (output != input) doc.Replace(0, doc.TextLength, output);
        }
        catch (Exception ex)
        {
            w?.Toasts.Show(Loc.T("tools.failed", ex.Message), ToastKind.Error);
        }
    }
}
