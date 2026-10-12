using System.Runtime.CompilerServices;

namespace BetterNotepad.Core.Localization;

/// <summary>Command palette (commands, quick open, go to line / symbol, language) and the Ctrl+Tab switcher.</summary>
internal static class PaletteStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // ── modes (the chip left of the input) ──
        ["pal.mode.files"] = ("Pliki", "Files"),
        ["pal.mode.commands"] = ("Polecenia", "Commands"),
        ["pal.mode.line"] = ("Linia", "Line"),
        ["pal.mode.symbols"] = ("Symbole", "Symbols"),
        ["pal.mode.language"] = ("Język", "Language"),

        // ── placeholders ──
        ["pal.ph.files"] = ("Szukaj w otwartych kartach i ostatnich plikach", "Search open tabs and recent files"),
        ["pal.ph.commands"] = ("Wpisz nazwę polecenia", "Type a command name"),
        ["pal.ph.line"] = ("Numer linii, opcjonalnie z kolumną: 42:7", "Line number, optionally with a column: 42:7"),
        ["pal.ph.symbols"] = ("Szukaj funkcji, klas i nagłówków w tym pliku", "Search functions, classes and headings in this file"),
        ["pal.ph.language"] = ("Wybierz język kolorowania składni", "Select a syntax highlighting language"),

        // ── group labels ──
        ["pal.group.recent"] = ("ostatnio używane", "recently used"),
        ["pal.group.other"] = ("pozostałe polecenia", "other commands"),
        ["pal.group.tabs"] = ("otwarte karty", "open tabs"),
        ["pal.group.recentFiles"] = ("ostatnio otwierane", "recently opened"),

        // ── empty states ──
        ["pal.empty.title"] = ("Brak wyników", "No results"),
        ["pal.empty.query"] = ("Nic nie pasuje do „{0}”.", "Nothing matches “{0}”."),
        ["pal.empty.files"] = ("Brak otwartych kart i ostatnich plików.", "No open tabs or recent files yet."),
        ["pal.empty.symbols"] = ("Ten plik nie zawiera rozpoznanych symboli.", "No symbols were found in this file."),
        ["pal.empty.noEditor"] = ("Aktywna karta nie jest edytorem tekstu.", "The active tab is not a text editor."),
        ["pal.empty.scanning"] = ("Szukanie symboli…", "Finding symbols…"),

        // ── go to line ──
        ["pal.line.prompt"] = ("Linia {0}, kolumna {1}. Wpisz numer od 1 do {2}.", "Line {0}, column {1}. Type a number between 1 and {2}."),
        ["pal.line.go"] = ("Przejdź do linii {0}", "Go to line {0}"),
        ["pal.line.goCol"] = ("Przejdź do linii {0}, kolumna {1}", "Go to line {0}, column {1}"),
        ["pal.line.last"] = ("ostatnia linia", "last line"),
        ["pal.line.invalid"] = ("To nie jest numer linii. Wpisz liczbę od 1 do {0}.", "That is not a line number. Type a number between 1 and {0}."),

        // ── files ──
        ["pal.file.unsaved"] = ("niezapisana notatka", "unsaved note"),
        ["pal.file.modified"] = ("zmieniony", "modified"),

        // ── language ──
        ["pal.lang.auto"] = ("Wykryj automatycznie", "Auto-detect"),
        ["pal.lang.autoDetail"] = ("na podstawie nazwy pliku i treści", "from the file name and content"),
        ["pal.lang.current"] = ("bieżący", "current"),
        ["pal.lang.changed"] = ("Język karty: {0}", "Tab language: {0}"),

        // ── symbol kinds ──
        ["pal.sym.function"] = ("funkcja", "function"),
        ["pal.sym.method"] = ("metoda", "method"),
        ["pal.sym.class"] = ("klasa", "class"),
        ["pal.sym.interface"] = ("interfejs", "interface"),
        ["pal.sym.struct"] = ("struktura", "struct"),
        ["pal.sym.enum"] = ("wyliczenie", "enum"),
        ["pal.sym.namespace"] = ("przestrzeń nazw", "namespace"),
        ["pal.sym.heading"] = ("nagłówek", "heading"),
        ["pal.sym.section"] = ("sekcja", "section"),
        ["pal.sym.label"] = ("etykieta", "label"),
        ["pal.sym.table"] = ("tabela", "table"),
        ["pal.sym.type"] = ("typ", "type"),
        ["pal.sym.key"] = ("klucz", "key"),
        ["pal.sym.line"] = ("linia {0}", "line {0}"),

        // ── footer hints ──
        ["pal.hint.select"] = ("wybierz", "select"),
        ["pal.hint.run"] = ("uruchom", "run"),
        ["pal.hint.open"] = ("otwórz", "open"),
        ["pal.hint.go"] = ("przejdź", "go"),
        ["pal.hint.pick"] = ("ustaw", "apply"),
        ["pal.hint.close"] = ("zamknij", "close"),
        ["pal.prefix.commands"] = ("polecenia", "commands"),
        ["pal.prefix.line"] = ("linia", "line"),
        ["pal.prefix.symbols"] = ("symbol", "symbol"),

        // ── Ctrl+Tab switcher ──
        ["switcher.title"] = ("Ostatnio używane karty", "Recently used tabs"),
        ["switcher.next"] = ("dalej", "next"),
        ["switcher.prev"] = ("wstecz", "back"),
        ["switcher.release"] = ("puść, aby przełączyć", "release to switch"),
    });
}
