using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Modules.Sys;

/// <summary>UI strings of the system module (tray, hotkey, file types, installation, updates), each key a (Polish, English) pair.</summary>
internal static class SystemStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // ── commands ──
        ["cmd.sys.checkUpdates"] = ("Sprawdź aktualizacje", "Check for updates"),
        ["cmd.sys.registerFileTypes"] = ("Dodaj BetterNotepad do „Otwórz za pomocą” i menu kontekstowego", "Add BetterNotepad to “Open with” and the context menu"),
        ["cmd.sys.openDefaultApps"] = ("Otwórz ustawienia aplikacji domyślnych Windows", "Open Windows default apps settings"),
        ["cmd.sys.install"] = ("Zainstaluj BetterNotepad", "Install BetterNotepad"),

        // ── hotkey ──
        ["hotkey.inUse"] = ("Skrót {0} jest już używany przez inny program. Wybierz inny w Ustawieniach ▸ System.", "{0} is already used by another program. Pick a different one in Settings ▸ System."),

        // ── tray ──
        ["tray.menu.newNote"] = ("Nowa notatka", "New note"),
        ["tray.menu.ghostOff"] = ("Wyłącz tryb ducha", "Turn off ghost mode"),
        ["tray.menu.show"] = ("Pokaż BetterNotepad", "Show BetterNotepad"),
        ["tray.menu.hide"] = ("Schowaj do zasobnika", "Hide to tray"),
        ["tray.menu.recent"] = ("Ostatnie pliki", "Recent files"),
        ["tray.menu.settings"] = ("Ustawienia", "Settings"),
        ["tray.menu.exit"] = ("Zakończ", "Exit"),
        ["tray.menu.update"] = ("Dostępna aktualizacja {0}", "Update {0} available"),

        // ── file types ──
        ["assoc.appDescription"] = ("Lekki notatnik i edytor kodu dla Windows 11.", "A light notepad and code editor for Windows 11."),
        ["assoc.type.Text"] = ("Dokument tekstowy", "Text document"),
        ["assoc.type.Code"] = ("Plik z kodem źródłowym", "Source code file"),
        ["assoc.type.Markdown"] = ("Dokument Markdown", "Markdown document"),
        ["assoc.type.Pdf"] = ("Dokument PDF", "PDF document"),
        ["assoc.type.Image"] = ("Obraz", "Image"),
        ["assoc.contextVerb"] = ("Otwórz w BetterNotepad", "Open in BetterNotepad"),
        ["assoc.registered"] = ("Dodano do „Otwórz za pomocą”. W ustawieniach Windows wybierz programy domyślne.", "Added to “Open with”. Pick the default apps in Windows settings."),
        ["assoc.failed"] = ("Nie udało się zarejestrować typów plików.", "Couldn’t register the file types."),

        // ── installation ──
        ["install.title"] = ("Zainstalować BetterNotepad?", "Install BetterNotepad?"),
        ["install.text"] = ("Instalacja kopiuje program do Twojego profilu, żeby był zawsze pod ręką. Możesz też uruchamiać go bez instalacji.", "Installing copies the app to your profile so it is always at hand. You can also keep running it without installing."),
        ["install.detail.startMenu"] = ("Skrót w menu Start", "Start menu shortcut"),
        ["install.detail.apps"] = ("Wpis w Ustawienia ▸ Aplikacje (odinstalujesz go stamtąd)", "Entry in Settings ▸ Apps (uninstall from there)"),
        ["install.detail.noAdmin"] = ("Bez uprawnień administratora", "No administrator rights needed"),
        ["install.associations"] = ("Dodaj do „Otwórz za pomocą” i menu prawego przycisku", "Add to “Open with” and the right-click menu"),
        ["install.install"] = ("Zainstaluj", "Install"),
        ["install.skip"] = ("Uruchom bez instalacji", "Run without installing"),
        ["install.installing"] = ("Instaluję…", "Installing…"),
        ["install.failed"] = ("Instalacja nie powiodła się: {0}", "Installation failed: {0}"),
        ["install.done"] = ("Zainstalowano w profilu użytkownika.", "Installed to your profile."),
        ["install.shortcutDescription"] = ("Lekki notatnik i edytor kodu", "A light notepad and code editor"),
        ["install.uninstallTitle"] = ("Odinstaluj BetterNotepad", "Uninstall BetterNotepad"),
        ["install.uninstallText"] = ("Usunąć BetterNotepad z tego komputera? Twoje ustawienia i notatki zostaną zachowane.", "Remove BetterNotepad from this computer? Your settings and notes are kept."),
        ["install.uninstalledText"] = ("BetterNotepad został odinstalowany. Ustawienia i notatki zostały w:\n{0}", "BetterNotepad was uninstalled. Your settings and notes remain in:\n{0}"),

        // ── updates ──
        ["update.checking"] = ("Sprawdzam aktualizacje…", "Checking for updates…"),
        ["update.upToDate"] = ("Masz najnowszą wersję ({0}).", "You’re up to date ({0})."),
        ["update.offline"] = ("Nie udało się połączyć z GitHubem. Sprawdź połączenie z internetem.", "Couldn’t reach GitHub. Check your internet connection."),
        ["update.failed"] = ("Nie udało się sprawdzić aktualizacji: {0}", "Couldn’t check for updates: {0}"),
        ["update.testMode"] = ("Tryb testowy: sprawdzanie aktualizacji jest wyłączone.", "Test mode: update checks are off."),
        ["update.available.title"] = ("Dostępna wersja {0}", "Version {0} is available"),
        ["update.available.text"] = ("Masz wersję {0}. Program pobierze nową, sprawdzi sumę SHA-256 i uruchomi się ponownie — otwarte karty zostaną.", "You have {0}. The app will download the new version, verify its SHA-256 and restart — your open tabs stay."),
        ["update.notes"] = ("Co nowego", "What’s new"),
        ["update.noNotes"] = ("Brak opisu zmian.", "No release notes."),
        ["update.install"] = ("Aktualizuj", "Update"),
        ["update.later"] = ("Później", "Later"),
        ["update.skip"] = ("Pomiń tę wersję", "Skip this version"),
        ["update.downloading"] = ("Pobieram wersję {0}…", "Downloading version {0}…"),
        ["update.ready"] = ("Wersja {0} jest zainstalowana. Uruchom ponownie, aby jej użyć.", "Version {0} is installed. Restart to use it."),
        ["update.restartNow"] = ("Uruchom ponownie", "Restart now"),
        ["update.devBuild"] = ("To wersja deweloperska — aktualizacja działa w wydanym pliku exe.", "This is a development build — updating works in the released exe."),
        ["update.balloonTitle"] = ("Dostępna aktualizacja BetterNotepad", "A BetterNotepad update is available"),
        ["update.balloonText"] = ("Wersja {0} czeka. Otwórz program, aby ją zainstalować.", "Version {0} is ready. Open the app to install it."),
        ["update.err.hash"] = ("Pobrany plik nie zgadza się z sumą SHA-256 — aktualizacja przerwana.", "The downloaded file doesn’t match its SHA-256 — update cancelled."),
        ["update.err.noChecksum"] = ("Wydanie nie ma sumy SHA-256 — aktualizacja przerwana.", "The release has no SHA-256 — update cancelled."),
        ["update.err.noAsset"] = ("Wydanie nie zawiera pliku BetterNotepad.exe.", "The release has no BetterNotepad.exe."),
        ["update.err.download"] = ("Nie udało się pobrać aktualizacji.", "Couldn’t download the update."),
        ["update.err.write"] = ("Nie można podmienić pliku programu (brak uprawnień do folderu).", "Couldn’t replace the program file (no write access to its folder)."),
        ["update.err.restart"] = ("Nie udało się uruchomić programu ponownie.", "Couldn’t restart the app."),

        // ── settings page additions ──
        ["settings.system.hotkeyInUse"] = ("Skrót {0} jest już używany przez inny program. Wybierz inny.", "{0} is already used by another program. Pick a different one."),
        ["settings.system.typesGroup"] = ("Typy plików", "File types"),
        ["settings.system.fileTypes"] = ("Dodaj do „Otwórz za pomocą”", "Add to “Open with”"),
        ["settings.system.fileTypesDesc"] = ("Pliki tekstowe, kod, Markdown, PDF i obrazy. Windows 11 sam wybiera program domyślny — zrobisz to poniżej.", "Text, code, Markdown, PDF and image files. Windows 11 lets only you choose the default app — do that below."),
        ["settings.system.contextMenu"] = ("„Otwórz w BetterNotepad” w menu prawego przycisku", "“Open in BetterNotepad” in the right-click menu"),
        ["settings.system.contextMenuDesc"] = ("Pojawia się w menu Eksploratora (po „Pokaż więcej opcji”).", "Appears in the Explorer menu (under “Show more options”)."),
        ["settings.system.defaultApps"] = ("Programy domyślne", "Default apps"),
        ["settings.system.defaultAppsDesc"] = ("Otwiera stronę BetterNotepad w ustawieniach Windows, gdzie wskażesz typy plików.", "Opens BetterNotepad’s page in Windows settings, where you choose the file types."),
        ["settings.system.defaultAppsButton"] = ("Otwórz ustawienia", "Open settings"),
        ["settings.system.installGroup"] = ("Instalacja", "Installation"),
        ["settings.system.install"] = ("Zainstaluj w profilu", "Install to your profile"),
        ["settings.system.installedAt"] = ("Zainstalowano w {0}. Odinstalujesz w Ustawienia ▸ Aplikacje.", "Installed in {0}. Uninstall it from Settings ▸ Apps."),
        ["settings.system.notInstalled"] = ("Uruchomiono z {0}. Instalacja dodaje skrót w menu Start i wpis w Aplikacjach.", "Running from {0}. Installing adds a Start menu shortcut and an Apps entry."),
        ["settings.system.installButton"] = ("Zainstaluj", "Install"),
        ["settings.updates.pending"] = ("Dostępna wersja {0}.", "Version {0} is available."),
    });
}
