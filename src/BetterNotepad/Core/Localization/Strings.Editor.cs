using System.Runtime.CompilerServices;

namespace BetterNotepad.Core.Localization;

/// <summary>Texts of the editor's IDE layer: multiple carets, folding, bookmarks, completion.</summary>
internal static class EditorStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["cmd.cat.selection"] = ("Zaznaczenie i kursory", "Selection and cursors"),

        // multiple carets
        ["cmd.editor.addNextOccurrence"] = ("Dodaj następne wystąpienie do zaznaczenia", "Add next occurrence to selection"),
        ["cmd.editor.selectAllOccurrences"] = ("Zaznacz wszystkie wystąpienia", "Select all occurrences"),
        ["cmd.editor.addCursorAbove"] = ("Dodaj kursor powyżej", "Add cursor above"),
        ["cmd.editor.addCursorBelow"] = ("Dodaj kursor poniżej", "Add cursor below"),
        ["cmd.editor.collapseCursors"] = ("Zostaw jeden kursor", "Remove extra cursors"),

        // folding
        ["cmd.editor.fold"] = ("Zwiń blok", "Fold"),
        ["cmd.editor.unfold"] = ("Rozwiń blok", "Unfold"),
        ["cmd.editor.foldAll"] = ("Zwiń wszystkie bloki", "Fold all"),
        ["cmd.editor.unfoldAll"] = ("Rozwiń wszystkie bloki", "Unfold all"),

        // bookmarks
        ["cmd.bookmarks.toggle"] = ("Przełącz zakładkę w linii", "Toggle bookmark"),
        ["cmd.bookmarks.next"] = ("Następna zakładka", "Next bookmark"),
        ["cmd.bookmarks.prev"] = ("Poprzednia zakładka", "Previous bookmark"),
        ["cmd.bookmarks.clear"] = ("Usuń wszystkie zakładki", "Clear all bookmarks"),
        // completion popover (kind of a suggestion)
        ["cmd.editor.triggerCompletion"] = ("Pokaż podpowiedzi", "Trigger suggestions"),
        ["completion.keyword"] = ("słowo kluczowe", "keyword"),
        ["completion.word"] = ("z dokumentu", "in document"),

        ["bookmarks.none"] = ("Brak zakładek", "No bookmarks"),
        ["bookmarks.noneHint"] = ("Brak zakładek — {0} dodaje zakładkę w linii", "No bookmarks — {0} adds one on the current line"),
    });
}
