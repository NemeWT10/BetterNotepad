using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Modules.Explorer;

/// <summary>Texts of the folder tree (Explorer panel).</summary>
internal static class ExplorerStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["explorer.title"] = ("Eksplorator", "Explorer"),

        // ── empty state ──
        ["explorer.empty.title"] = ("Brak otwartego folderu", "No folder open"),
        ["explorer.empty.text"] = ("Otwórz folder, a jego pliki będą zawsze pod ręką — kliknięcie otwiera plik w karcie.",
                                   "Open a folder to keep its files at hand — a click opens a file in a tab."),
        ["explorer.empty.open"] = ("Otwórz folder…", "Open folder…"),
        ["explorer.empty.activeFolder"] = ("Folder aktywnego pliku", "Active file’s folder"),
        ["explorer.empty.activeFolderTip"] = ("Otwiera {0}", "Opens {0}"),
        ["explorer.empty.noActive"] = ("Aktywna karta nie jest zapisanym plikiem", "The active tab is not a saved file"),
        ["explorer.empty.toggle"] = ("pokaż / ukryj panel", "show / hide the panel"),

        // ── placeholder lines ──
        ["explorer.loading"] = ("Wczytywanie…", "Loading…"),
        ["explorer.denied"] = ("Brak dostępu do folderu", "Access denied"),
        ["explorer.gone"] = ("Folder już nie istnieje", "The folder no longer exists"),
        ["explorer.unreadable"] = ("Nie można odczytać folderu", "The folder cannot be read"),

        // ── header, menus ──
        ["explorer.newFile"] = ("Nowy plik…", "New file…"),
        ["explorer.newFolder"] = ("Nowy folder…", "New folder…"),
        ["explorer.refresh"] = ("Odśwież", "Refresh"),
        ["explorer.collapseAll"] = ("Zwiń wszystkie foldery", "Collapse folders"),
        ["explorer.more"] = ("Więcej akcji", "More actions"),
        ["explorer.open"] = ("Otwórz", "Open"),
        ["explorer.rename"] = ("Zmień nazwę…", "Rename…"),
        ["explorer.delete"] = ("Usuń", "Delete"),
        ["explorer.paste"] = ("Wklej", "Paste"),
        ["explorer.pasteNothing"] = ("W schowku nie ma plików ani obrazu", "There are no files or picture on the clipboard"),
        ["explorer.pasteIntoItself"] = ("Nie można wkleić folderu „{0}” do niego samego", "Can’t paste the folder “{0}” into itself"),
        ["explorer.pasteFailed"] = ("Nie udało się wkleić: {0}", "Paste failed: {0}"),
        ["explorer.pasted.one"] = ("Wklejono {0} element", "Pasted {0} item"),
        ["explorer.pasted.few"] = ("Wklejono {0} elementy", "Pasted {0} items"),
        ["explorer.pasted.many"] = ("Wklejono {0} elementów", "Pasted {0} items"),
        ["explorer.moved.one"] = ("Przeniesiono {0} element", "Moved {0} item"),
        ["explorer.moved.few"] = ("Przeniesiono {0} elementy", "Moved {0} items"),
        ["explorer.moved.many"] = ("Przeniesiono {0} elementów", "Moved {0} items"),
        ["explorer.pastedImageName"] = ("obraz {0}", "image {0}"),
        ["explorer.pastedImage"] = ("Zapisano obraz ze schowka: {0}", "Saved the clipboard picture: {0}"),
        ["explorer.copyPath"] = ("Kopiuj ścieżkę", "Copy path"),
        ["explorer.copyRelative"] = ("Kopiuj ścieżkę względną", "Copy relative path"),
        ["explorer.reveal"] = ("Pokaż w Eksploratorze plików", "Reveal in File Explorer"),
        ["explorer.openOther"] = ("Otwórz inny folder…", "Open another folder…"),
        ["explorer.close"] = ("Zamknij folder", "Close folder"),
        ["explorer.hide"] = ("Ukryj panel", "Hide panel"),
        ["explorer.dialogTitle"] = ("Otwórz folder", "Open folder"),

        // ── names typed in the tree ──
        ["explorer.name.exists"] = ("„{0}” już istnieje w tym folderze — wybierz inną nazwę.", "“{0}” already exists in this folder — choose a different name."),
        ["explorer.name.invalid"] = ("Nazwa nie może zawierać znaków \\ / : * ? \" < > |", "A name cannot contain \\ / : * ? \" < > |"),
        ["explorer.name.reserved"] = ("„{0}” to nazwa zarezerwowana przez Windows.", "“{0}” is a name reserved by Windows."),
        ["explorer.name.trailing"] = ("Nazwa nie może kończyć się kropką ani spacją.", "A name cannot end with a dot or a space."),
        ["explorer.name.tooLong"] = ("Nazwa jest za długa (najwyżej 255 znaków).", "The name is too long (255 characters at most)."),
        ["explorer.name.dot"] = ("Ta nazwa jest niedozwolona.", "This name is not allowed."),

        // ── file operations ──
        ["explorer.delete.fileTitle"] = ("Usunąć „{0}”?", "Delete “{0}”?"),
        ["explorer.delete.folderTitle"] = ("Usunąć folder „{0}”?", "Delete the folder “{0}”?"),
        ["explorer.delete.fileText"] = ("Plik trafi do Kosza — w razie potrzeby przywrócisz go stamtąd.",
                                        "The file goes to the Recycle Bin — you can restore it from there."),
        ["explorer.delete.folderText"] = ("Folder razem z całą zawartością trafi do Kosza — w razie potrzeby przywrócisz go stamtąd.",
                                          "The folder and everything in it go to the Recycle Bin — you can restore them from there."),
        ["explorer.delete.confirm"] = ("Przenieś do Kosza", "Move to Recycle Bin"),
        ["explorer.delete.failed"] = ("Nie udało się usunąć „{0}”: {1}", "Could not delete “{0}”: {1}"),
        ["explorer.create.failed"] = ("Nie udało się utworzyć „{0}”: {1}", "Could not create “{0}”: {1}"),
        ["explorer.folderGone"] = ("Folder „{0}” już nie istnieje", "The folder “{0}” no longer exists"),
        ["explorer.notFolder"] = ("„{0}” nie jest folderem", "“{0}” is not a folder"),
    });
}
