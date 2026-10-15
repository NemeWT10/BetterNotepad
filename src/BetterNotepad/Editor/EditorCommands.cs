using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI;

namespace BetterNotepad.Editor;

/// <summary>
/// Commands of the IDE layer: multiple carets, folding, bookmarks. Titles: Strings.Editor.cs ("cmd.&lt;id&gt;").
/// Shortcuts follow VS Code (bookmarks: Notepad++ Ctrl+F2, F8 / Shift+F8 since F2 renames); never Ctrl+Alt+letter.
/// </summary>
public static class EditorCommands
{
    private const string CatEdit = "cmd.cat.edit";
    private const string CatSelection = "cmd.cat.selection";
    private const string CatView = "cmd.cat.view";
    private const string CatGo = "cmd.cat.go";

    private static void Reg(string id, string category, string? gesture, Action<CodeEditor> run,
        Func<CodeEditor, bool>? can = null, string? search = null, bool editable = false)
        => CommandRegistry.Register(new AppCommand
        {
            Id = id,
            TitleKey = "cmd." + id,
            CategoryKey = category,
            DefaultGesture = gesture,
            Scope = CommandScope.Editor,
            SearchTerms = search,
            Execute = c =>
            {
                if (AppCommands.EditorOf(c) is { } ed) run(ed);
            },
            CanExecute = c => AppCommands.EditorOf(c) is { } ed && (!editable || !ed.IsReadOnly) && (can?.Invoke(ed) ?? true),
        });

    public static void Register()
    {
        Reg("editor.addNextOccurrence", CatSelection, "Ctrl+D", e => e.Features.Carets.AddNextOccurrence(),
            search: "multi cursor kursor wiele następne wystąpienie next occurrence");
        Reg("editor.selectAllOccurrences", CatSelection, "Ctrl+Shift+L", e => e.Features.Carets.SelectAllOccurrences(),
            search: "multi cursor kursor wszystkie wystąpienia all occurrences");
        Reg("editor.addCursorAbove", CatSelection, "Ctrl+Alt+Up", e => e.Features.Carets.AddCaretVertical(-1), search: "multi cursor kursor kolumna column");
        Reg("editor.addCursorBelow", CatSelection, "Ctrl+Alt+Down", e => e.Features.Carets.AddCaretVertical(+1), search: "multi cursor kursor kolumna column");
        Reg("editor.collapseCursors", CatSelection, "Esc", e => e.Features.Carets.Clear(), e => e.Features.Carets.HasExtra,
            search: "multi cursor kursor jeden single");

        Reg("editor.triggerCompletion", CatEdit, "Ctrl+Space", e => e.Features.Completion.Refresh(openIfClosed: true, manual: true),
            search: "podpowiedzi completion suggest autouzupełnianie", editable: true);

        // rich copy for Word / Google Docs (Editor/Export)
        Reg("editor.copyFormatted", CatEdit, "Ctrl+Shift+C", Export.RichCopy.CopyFormatted,
            search: "kopiuj formatowanie word google docs dokument html rtf copy formatted rich");
        Reg("editor.copyAsListing", CatEdit, null, Export.RichCopy.CopyAsListing,
            search: "listing praca dyplomowa inżynierska magisterska licencjacka podpis thesis caption word google docs kopiuj copy");
        Reg("editor.copyAsImage", CatEdit, null, Export.CopyImage.Copy,
            search: "obraz png zdjęcie zrzut kodu slajd prezentacja czat image picture screenshot snippet slide chat share kopiuj copy");
        Reg("editor.saveAsImage", CatEdit, null, Export.CopyImage.Save,
            search: "obraz png zapisz plik eksport zrzut kodu slajd image picture screenshot snippet save export file");

        Reg("editor.fold", CatView, "Ctrl+Shift+[", e => e.Features.Fold(), e => e.Features.Folding.Manager is not null, search: "zwiń fold collapse");
        Reg("editor.unfold", CatView, "Ctrl+Shift+]", e => e.Features.Unfold(), e => e.Features.Folding.Manager is not null, search: "rozwiń unfold expand");
        Reg("editor.foldAll", CatView, null, e => e.Features.FoldAll(true), e => e.Features.Folding.Manager is not null, search: "zwiń wszystko fold all collapse");
        Reg("editor.unfoldAll", CatView, null, e => e.Features.FoldAll(false), e => e.Features.Folding.Manager is not null, search: "rozwiń wszystko unfold all expand");

        Reg("bookmarks.toggle", CatGo, "Ctrl+F2", e => e.Features.ToggleBookmark(), search: "zakładka bookmark");
        Reg("bookmarks.next", CatGo, "F8", e => GoToBookmark(e, backward: false), search: "zakładka bookmark następna next");
        Reg("bookmarks.prev", CatGo, "Shift+F8", e => GoToBookmark(e, backward: true), search: "zakładka bookmark poprzednia previous");
        Reg("bookmarks.clear", CatGo, null, e => e.Features.Bookmarks?.Clear(), e => e.Features.Bookmarks is { Count: > 0 }, search: "zakładki bookmarks usuń clear");
    }

    private static void GoToBookmark(CodeEditor e, bool backward)
    {
        if (e.Features.GoToBookmark(backward)) return;
        if (System.Windows.Window.GetWindow(e) is UI.Windows.MainWindow w)
        {
            var key = Keymap.GestureText("bookmarks.toggle");
            w.Toasts.Show(key.Length > 0 ? Loc.T("bookmarks.noneHint", key) : Loc.T("bookmarks.none"), ToastKind.Info, seconds: 2);
        }
    }
}
