using System.Runtime.CompilerServices;

namespace BetterNotepad.Core.Localization;

/// <summary>Core UI strings (dialogs, toasts, status bar, tabs, find bar), each key as a (Polish, English) pair.</summary>
internal static class CoreStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // ── common ──
        ["common.cancel"] = ("Anuluj", "Cancel"),
        ["error.command"] = ("Polecenie „{0}” nie powiodło się: {1}", "Command “{0}” failed: {1}"),
        ["error.unexpected"] = ("Nieoczekiwany błąd: {0}", "Unexpected error: {0}"),
        ["stub.comingSoon"] = ("Ta funkcja pojawi się wkrótce.", "This feature is coming soon."),

        // ── window ──
        ["palette.pill"] = ("Szukaj plików i poleceń…", "Search files and commands…"),
        ["settings.title"] = ("Ustawienia", "Settings"),
        ["window.pinTip"] = ("Zawsze na wierzchu — kliknij, aby odpiąć", "Always on top — click to unpin"),

        // ── pinned on top / ghost mode ──
        ["tabmenu.pinOnTop"] = ("Przypnij na wierzchu", "Pin on top"),
        ["pin.dragTip"] = ("Przeciągnij, aby przesunąć · Alt+kółko zmienia rozmiar", "Drag to move · Alt+wheel resizes"),
        ["pin.opacityTip"] = ("Przezroczystość w trybie ducha", "Ghost mode opacity"),
        ["pin.ghostTip"] = ("Tryb ducha: półprzezroczyste okno, kliknięcia przechodzą pod spód ({0} wyłącza)", "Ghost mode: translucent, clicks pass through ({0} turns it off)"),
        ["pin.ghostTipNoKey"] = ("Tryb ducha: półprzezroczyste okno, kliknięcia przechodzą pod spód", "Ghost mode: translucent, clicks pass through"),
        ["pin.unpinTip"] = ("Odepnij — karta wraca do głównego okna", "Unpin — the tab goes back to the main window"),
        ["pin.closeTip"] = ("Zamknij kartę", "Close tab"),
        ["pin.opacityTitle"] = ("Przezroczystość trybu ducha", "Ghost mode opacity"),
        ["pin.percent"] = ("{0} %", "{0}%"),
        ["pin.opacityHint"] = ("Podgląd na żywo. Obowiązuje, gdy tryb ducha jest włączony; zapamiętywana dla tego pliku.", "Live preview. Used while ghost mode is on; remembered for this file."),
        ["pin.pinned"] = ("Przypięto na wierzchu. Alt+kółko zmienia rozmiar, pasek u góry pojawia się po najechaniu.", "Pinned on top. Alt+wheel resizes; the bar appears when you point at the top edge."),
        ["pin.ghostBadge"] = ("Duch · {0}", "Ghost · {0}"),
        ["pin.ghostBadgeTray"] = ("Duch · wyłącz w zasobniku", "Ghost · turn off in the tray"),
        ["pin.ghostBadgePalette"] = ("Duch · wyłącz w palecie poleceń", "Ghost · turn off in the palette"),
        ["pin.ghostOn"] = ("Tryb ducha włączony — kliknięcia przechodzą pod spód. {0} wyłącza go.", "Ghost mode on — clicks pass through. Press {0} to turn it off."),
        ["pin.ghostOnTray"] = ("Tryb ducha włączony — kliknięcia przechodzą pod spód. Wyłączysz go w menu ikony w zasobniku.", "Ghost mode on — clicks pass through. Turn it off from the tray icon menu."),
        ["pin.ghostOnPalette"] = ("Tryb ducha włączony — kliknięcia przechodzą pod spód. Wyłączysz go w palecie poleceń głównego okna.", "Ghost mode on — clicks pass through. Turn it off from the main window's command palette."),
        ["pin.ghostOff"] = ("Tryb ducha wyłączony", "Ghost mode off"),
        ["pin.nonePinned"] = ("Brak przypiętych okien. Przypnij kartę: menu karty ▸ Przypnij na wierzchu.", "No pinned windows. Pin a tab first: tab menu ▸ Pin on top."),
        ["tray.hintTitle"] = ("BetterNotepad działa w tle", "BetterNotepad is still running"),
        ["tray.hintText"] = ("Program nadal działa w zasobniku. Aby go zamknąć całkowicie, użyj menu ikony w zasobniku.", "The app keeps running in the tray. To quit it completely, use the tray icon menu."),

        // ── themes ──
        ["theme.aurora"] = ("Aurora", "Aurora"),
        ["theme.graphite"] = ("Graphite", "Graphite"),
        ["theme.daylight"] = ("Daylight", "Daylight"),
        ["theme.neon"] = ("Neon", "Neon"),
        ["theme.sunset"] = ("Sunset", "Sunset"),

        // ── tabs ──
        ["tab.new"] = ("Nowa karta", "New tab"),
        ["tab.untitledName"] = ("nowy {0}", "new {0}"),
        ["tab.untitledTip"] = ("{0} (niezapisana notatka)", "{0} (unsaved note)"),
        ["tab.copied"] = ("Skopiowano: {0}", "Copied: {0}"),
        ["tab.nothingToReopen"] = ("Brak zamkniętych kart do przywrócenia", "No closed tabs to reopen"),
        ["tail.on"] = ("Tryb śledzenia włączony: „{0}”", "Tail mode on: “{0}”"),
        ["tail.off"] = ("Tryb śledzenia wyłączony: „{0}”", "Tail mode off: “{0}”"),

        // ── views ──
        ["view.loading"] = ("Wczytywanie…", "Loading…"),
        ["editor.emptyHint"] = ("Zacznij pisać…  ·  {0} otwórz plik  ·  {1} polecenia", "Start typing…  ·  {0} open file  ·  {1} commands"),
        ["view.loadFailed"] = ("Nie udało się wczytać pliku: {0}", "Couldn’t load the file: {0}"),
        ["view.unsupported"] = ("Tego pliku nie da się wyświetlić.", "This file can’t be displayed."),
        ["view.preview"] = ("Podgląd", "Preview"),
        ["view.split"] = ("Podział", "Split"),
        ["view.edit"] = ("Edycja", "Edit"),
        ["view.table"] = ("Tabela", "Table"),
        ["view.text"] = ("Tekst", "Text"),

        // ── file dialogs ──
        ["dialog.openTitle"] = ("Otwórz plik", "Open file"),
        ["dialog.saveTitle"] = ("Zapisz jako", "Save as"),
        ["dialog.filterAll"] = ("Wszystkie pliki", "All files"),
        ["file.notFound"] = ("Nie znaleziono pliku „{0}”", "File “{0}” was not found"),

        // ── save ──
        ["save.done"] = ("Zapisano „{0}”", "Saved “{0}”"),
        ["save.failed"] = ("Nie udało się zapisać „{0}”: {1}", "Couldn’t save “{0}”: {1}"),
        ["save.locked"] = ("Ta karta jest zablokowana (tylko do odczytu).", "This tab is locked (read-only)."),
        ["save.unlock"] = ("Odblokuj", "Unlock"),
        ["save.saveAsElsewhere"] = ("Zapisz jako…", "Save as…"),
        ["save.deniedTitle"] = ("Plik jest chroniony", "The file is protected"),
        ["save.deniedText"] = ("Brak uprawnień do zapisu pliku „{0}”. Zapisać z uprawnieniami administratora (okno UAC)?", "You don’t have permission to write “{0}”. Save with administrator rights (UAC prompt)?"),
        ["save.asAdmin"] = ("Zapisz jako administrator", "Save as administrator"),
        ["save.adminFailed"] = ("Nie udało się zapisać jako administrator.", "Saving as administrator failed."),
        ["save.encodingTitle"] = ("Znaki spoza kodowania", "Characters outside the encoding"),
        ["save.encodingText"] = ("W tekście są znaki, których nie da się zapisać w kodowaniu {0}. Zapisać jako UTF-8?", "The text contains characters that can’t be stored in {0}. Save as UTF-8 instead?"),
        ["save.encodingUtf8"] = ("Zapisz jako UTF-8", "Save as UTF-8"),
        ["save.encodingKeep"] = ("Zapisz mimo to", "Save anyway"),

        // ── closing a tab ──
        ["close.saveTitle"] = ("Zapisać zmiany w „{0}”?", "Save changes to “{0}”?"),
        ["close.saveText"] = ("Jeśli ich nie zapiszesz, zmiany zostaną utracone.", "Your changes will be lost if you don’t save them."),
        ["close.save"] = ("Zapisz", "Save"),
        ["close.discard"] = ("Nie zapisuj", "Don’t save"),

        // ── rename, lock ──
        ["rename.done"] = ("Zmieniono nazwę na „{0}”", "Renamed to “{0}”"),
        ["rename.exists"] = ("Plik „{0}” już istnieje", "“{0}” already exists"),
        ["rename.failed"] = ("Nie udało się zmienić nazwy: {0}", "Rename failed: {0}"),
        ["rename.invalid"] = ("Nazwa zawiera niedozwolone znaki", "The name contains invalid characters"),
        ["lock.on"] = ("Zablokowano „{0}” (tylko do odczytu)", "Locked “{0}” (read-only)"),
        ["lock.off"] = ("Odblokowano „{0}”", "Unlocked “{0}”"),
        ["lock.attrTitle"] = ("Odblokować plik?", "Unlock the file?"),
        ["lock.attrText"] = ("Plik „{0}” ma w systemie Windows atrybut „tylko do odczytu”. Czy zdjąć go również z pliku?", "“{0}” has the read-only attribute set in Windows. Remove it from the file as well?"),
        ["lock.attrRemove"] = ("Zdejmij atrybut", "Remove attribute"),
        ["lock.attrAppOnly"] = ("Tylko w programie", "In the app only"),

        // ── changes made by other programs ──
        ["external.modifiedTitle"] = ("„{0}” zmieniono na dysku", "“{0}” changed on disk"),
        ["external.modifiedText"] = ("Plik został zmieniony przez inny program. Co zrobić?", "The file was changed by another program. What now?"),
        ["external.reload"] = ("Przeładuj z dysku", "Reload from disk"),
        ["external.keep"] = ("Zachowaj moją wersję", "Keep my version"),
        ["external.compare"] = ("Porównaj", "Compare"),
        ["external.reloaded"] = ("Przeładowano „{0}” z dysku", "Reloaded “{0}” from disk"),
        ["external.deletedTitle"] = ("„{0}” zniknął z dysku", "“{0}” was deleted from disk"),
        ["external.deletedText"] = ("Plik został usunięty przez inny program. Zachować jego treść w programie?", "The file was deleted by another program. Keep its content in the app?"),
        ["external.keepInApp"] = ("Zachowaj w programie", "Keep in the app"),
        ["external.closeTab"] = ("Zamknij kartę", "Close the tab"),

        // ── compare ──
        ["compare.diskSide"] = ("{0} (na dysku)", "{0} (on disk)"),
        ["compare.appSide"] = ("{0} (w programie)", "{0} (in the app)"),
        ["compare.tabName"] = ("Porównanie: {0}", "Diff: {0}"),
        ["compare.tabName2"] = ("Porównanie: {0} ↔ {1}", "Diff: {0} ↔ {1}"),

        // ── encoding, line endings, indentation ──
        ["encoding.reopenTitle"] = ("Otworzyć ponownie z kodowaniem?", "Reopen with encoding?"),
        ["encoding.reopenDirty"] = ("Niezapisane zmiany w tej karcie zostaną utracone. Otworzyć plik ponownie z innym kodowaniem?", "Unsaved changes in this tab will be lost. Reopen the file with another encoding?"),
        ["encoding.reopen"] = ("Otwórz ponownie", "Reopen"),
        ["encoding.changed"] = ("Kodowanie: {0}. Zapisz plik, aby je zastosować.", "Encoding set to {0}. Save the file to apply it."),
        ["menu.encodingReopen"] = ("Otwórz ponownie z kodowaniem", "Reopen with encoding"),
        ["menu.encodingSave"] = ("Zapisz z kodowaniem", "Save with encoding"),
        ["eol.crlf"] = ("Windows (CRLF)", "Windows (CRLF)"),
        ["eol.lf"] = ("Unix (LF)", "Unix (LF)"),
        ["eol.cr"] = ("Stary Mac (CR)", "Classic Mac (CR)"),
        ["eol.changed"] = ("Zakończenia wierszy: {0}", "Line endings: {0}"),
        ["indent.spaces"] = ("Wcięcie spacjami", "Indent using spaces"),
        ["indent.tabs"] = ("Wcięcie tabulatorami", "Indent using tabs"),
        ["indent.width"] = ("Szerokość wcięcia", "Indentation width"),

        // ── status bar ──
        ["status.position"] = ("Wiersz {0}, kol. {1}", "Ln {0}, Col {1}"),
        ["status.selected"] = ("(zaznaczono {0})", "({0} selected)"),
        ["status.goToLineTip"] = ("Przejdź do linii", "Go to line"),
        ["status.readOnly"] = ("Tylko do odczytu", "Read-only"),
        ["status.unlockTip"] = ("Kliknij, aby odblokować", "Click to unlock"),
        ["status.tail"] = ("Śledzenie", "Tail"),
        ["status.tailTip"] = ("Tryb śledzenia: plik przeładowuje się sam — kliknij, aby wyłączyć", "Tail mode: the file reloads itself — click to turn off"),
        ["status.large"] = ("Plik duży: tryb lekki", "Large file: light mode"),
        ["status.largeTip"] = ("Plik jest duży — kolorowanie wyłączone, aby edytor działał płynnie", "This file is large — highlighting is off to keep the editor fast"),
        ["status.spaces"] = ("Spacje: {0}", "Spaces: {0}"),
        ["status.tabs"] = ("Tabulator: {0}", "Tab size: {0}"),
        ["status.encodingTip"] = ("Kodowanie pliku — kliknij, aby zmienić", "File encoding — click to change"),
        ["status.eolTip"] = ("Zakończenia wierszy — kliknij, aby zmienić", "Line endings — click to change"),
        ["status.languageTip"] = ("Język (kolorowanie składni) — kliknij, aby zmienić", "Language mode — click to change"),
        ["status.zoomTip"] = ("Powiększenie edytora — kliknij, aby zresetować", "Editor zoom — click to reset"),

        // ── find bar ──
        ["find.placeholder"] = ("Znajdź", "Find"),
        ["find.replacePlaceholder"] = ("Zamień na", "Replace with"),
        ["find.toggleReplace"] = ("Pokaż / ukryj zamianę", "Toggle replace"),
        ["find.matchCase"] = ("Rozróżniaj wielkość liter", "Match case"),
        ["find.wholeWord"] = ("Całe słowa", "Whole word"),
        ["find.regex"] = ("Wyrażenie regularne", "Regular expression"),
        ["find.inSelection"] = ("Szukaj w zaznaczeniu", "Find in selection"),
        ["find.previous"] = ("Poprzednie (Shift+Enter)", "Previous (Shift+Enter)"),
        ["find.next"] = ("Następne (Enter)", "Next (Enter)"),
        ["find.close"] = ("Zamknij (Esc)", "Close (Esc)"),
        ["find.replaceOne"] = ("Zamień", "Replace"),
        ["find.replaceAll"] = ("Zamień wszystkie", "Replace all"),
        ["find.noResults"] = ("Brak wyników", "No results"),
        ["find.count"] = ("Wyniki: {0}", "{0} results"),
        ["find.countOf"] = ("{0} z {1}", "{0} of {1}"),
        ["find.badRegex"] = ("Nieprawidłowe wyrażenie regularne", "Invalid regular expression"),
        ["find.replaced.one"] = ("Zamieniono {0} wystąpienie", "Replaced {0} occurrence"),
        ["find.replaced.few"] = ("Zamieniono {0} wystąpienia", "Replaced {0} occurrences"),
        ["find.replaced.many"] = ("Zamieniono {0} wystąpień", "Replaced {0} occurrences"),
    });
}
