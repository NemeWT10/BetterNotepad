using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Editor.Export;

/// <summary>Texts of "Copy with formatting" and the thesis-listing dialog.</summary>
internal static class ExportStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["cmd.editor.copyFormatted"] = ("Kopiuj z formatowaniem", "Copy with formatting"),
        ["cmd.editor.copyAsListing"] = ("Kopiuj jako listing do pracy…", "Copy as thesis listing…"),

        ["copy.empty"] = ("Nic do skopiowania — plik jest pusty", "Nothing to copy — the file is empty"),
        ["copy.clipboardBusy"] = ("Schowek jest zajęty przez inny program — spróbuj jeszcze raz", "The clipboard is busy in another program — try again"),
        ["copy.formatted.done.one"] = ("Skopiowano {0} wiersz z formatowaniem", "Copied {0} line with formatting"),
        ["copy.formatted.done.few"] = ("Skopiowano {0} wiersze z formatowaniem", "Copied {0} lines with formatting"),
        ["copy.formatted.done.many"] = ("Skopiowano {0} wierszy z formatowaniem", "Copied {0} lines with formatting"),
        ["copy.formatted.doneFile.one"] = ("Skopiowano cały plik z formatowaniem ({0} wiersz)", "Copied the whole file with formatting ({0} line)"),
        ["copy.formatted.doneFile.few"] = ("Skopiowano cały plik z formatowaniem ({0} wiersze)", "Copied the whole file with formatting ({0} lines)"),
        ["copy.formatted.doneFile.many"] = ("Skopiowano cały plik z formatowaniem ({0} wierszy)", "Copied the whole file with formatting ({0} lines)"),

        // thesis listing
        ["listing.title"] = ("Kopiuj jako listing do pracy", "Copy as thesis listing"),
        ["listing.fromSelection.one"] = ("Zaznaczenie ({0} wiersz) — po skopiowaniu wklej do Worda lub Google Docs.", "Selection ({0} line) — then paste it into Word or Google Docs."),
        ["listing.fromSelection.few"] = ("Zaznaczenie ({0} wiersze) — po skopiowaniu wklej do Worda lub Google Docs.", "Selection ({0} lines) — then paste it into Word or Google Docs."),
        ["listing.fromSelection.many"] = ("Zaznaczenie ({0} wierszy) — po skopiowaniu wklej do Worda lub Google Docs.", "Selection ({0} lines) — then paste it into Word or Google Docs."),
        ["listing.fromFile.one"] = ("Cały plik ({0} wiersz) — po skopiowaniu wklej do Worda lub Google Docs.", "Whole file ({0} line) — then paste it into Word or Google Docs."),
        ["listing.fromFile.few"] = ("Cały plik ({0} wiersze) — po skopiowaniu wklej do Worda lub Google Docs.", "Whole file ({0} lines) — then paste it into Word or Google Docs."),
        ["listing.fromFile.many"] = ("Cały plik ({0} wierszy) — po skopiowaniu wklej do Worda lub Google Docs.", "Whole file ({0} lines) — then paste it into Word or Google Docs."),
        ["listing.chapter"] = ("Rozdział", "Chapter"),
        ["listing.number"] = ("Numer", "Number"),
        ["listing.description"] = ("Opis", "Description"),
        ["listing.preview"] = ("Podpis", "Caption"),
        ["listing.options"] = ("Opcje (zapamiętywane)", "Options (remembered)"),
        ["listing.copy"] = ("Kopiuj listing", "Copy listing"),
        ["listing.badNumber"] = ("Rozdział i numer muszą być liczbami od 1 wzwyż", "Chapter and number must be whole numbers from 1"),
        ["listing.copied"] = ("Skopiowano listing {0} — wklej do Worda lub Google Docs", "Copied listing {0} — paste it into Word or Google Docs"),
        ["listing.sourceDefault"] = ("Źródło: opracowanie własne", "Source: own work"),
        ["listing.sourceText"] = ("Tekst pod listingiem", "Text below the listing"),
        ["listing.opt.lineNumbers"] = ("Numery wierszy", "Line numbers"),
        ["listing.opt.lineNumbers.tip"] = ("Numery w osobnej kolumnie — nie kopiują się razem z kodem", "Numbers in a separate column — they are not copied with the code"),
        ["listing.opt.colors"] = ("Kolorowanie składni", "Syntax colors"),
        ["listing.opt.colors.tip"] = ("Ciemne, stonowane kolory, czytelne także w druku czarno-białym", "Dark, muted colors that stay legible in black-and-white print"),
        ["listing.opt.frame"] = ("Ramka", "Frame"),
        ["listing.opt.frame.tip"] = ("Cienka ramka 0,5 pt wokół kodu", "Thin 0.5 pt frame around the code"),
        ["listing.opt.captionBelow"] = ("Podpis pod listingiem", "Caption below"),
        ["listing.opt.captionBelow.tip"] = ("Domyślnie podpis jest nad listingiem, jak przy tabeli", "By default the caption is above the listing, as for a table"),
        ["listing.opt.dedent"] = ("Usuń wspólne wcięcie", "Remove common indent"),
        ["listing.opt.dedent.tip"] = ("Kod wyjęty z wnętrza klasy zaczyna się przy lewej krawędzi", "Code taken from inside a class starts at the left edge"),
        ["listing.opt.source"] = ("Źródło pod listingiem", "Source line below"),
        ["listing.opt.source.tip"] = ("Np. „Źródło: opracowanie własne”", "E.g. “Source: own work”"),
        ["listing.opt.wideSpacing"] = ("Interlinia 1,5", "1.5 line spacing"),
        ["listing.opt.wideSpacing.tip"] = ("Kod z interlinią 1,5 jak treść pracy; domyślnie pojedyncza", "Code at 1.5 lines like the body text; single by default"),
        ["listing.longLines.one"] = ("{0} wiersz jest dłuższy", "{0} line is longer"),
        ["listing.longLines.few"] = ("{0} wiersze są dłuższe", "{0} lines are longer"),
        ["listing.longLines.many"] = ("{0} wierszy jest dłuższych", "{0} lines are longer"),
        ["listing.longLinesTail"] = (" niż {0} znaków — w kolumnie 15,5 cm tekst się zawinie.", " than {0} characters — the text will wrap in a 15.5 cm column."),

        // settings → editor
        ["settings.editor.copyGroup"] = ("Kopiuj z formatowaniem", "Copy with formatting"),
        ["settings.editor.copyLineNumbers"] = ("Numery wierszy", "Line numbers"),
        ["settings.editor.copyLineNumbersDesc"] = ("W osobnej kolumnie — nie kopiują się razem z kodem", "In a separate column — not copied with the code"),
        ["settings.editor.copyThemeColors"] = ("Kolory motywu", "Theme colors"),
        ["settings.editor.copyThemeColorsDesc"] = ("Tło i kolory jak w edytorze zamiast jasnej palety do dokumentów", "Background and colors as in the editor instead of the light document palette"),
        ["settings.editor.copyDedent"] = ("Usuń wspólne wcięcie", "Remove common indent"),
        ["settings.editor.copyDedentDesc"] = ("Kod wyjęty z wnętrza klasy zaczyna się przy lewej krawędzi", "Code taken from inside a class starts at the left edge"),
        ["settings.editor.copyListingHint"] = ("Podpis i kod w formacie pracy dyplomowej. W menu kontekstowym edytora i w palecie poleceń; opcje zapamiętuje jego okno.",
            "Caption and code formatted for a thesis. In the editor's context menu and the command palette; its dialog remembers the options."),
    });
}
