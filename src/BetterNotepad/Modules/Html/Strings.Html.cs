using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Modules.Html;

/// <summary>Texts of the HTML / SVG preview (PL + EN).</summary>
internal static class HtmlStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // the calm one-line notice above a page whose internet content was blocked
        ["html.blocked.one"] = ("Zablokowano {0} element z internetu", "Blocked {0} item from the internet"),
        ["html.blocked.few"] = ("Zablokowano {0} elementy z internetu", "Blocked {0} items from the internet"),
        ["html.blocked.many"] = ("Zablokowano {0} elementów z internetu", "Blocked {0} items from the internet"),
        ["html.blockedMore"] = ("i {0} więcej", "and {0} more"),
        ["html.blockedTip"] = ("Podgląd nie łączy się z siecią. Zablokowane adresy:\n{0}", "The preview makes no network connections. Blocked hosts:\n{0}"),
        ["html.blockedSettings"] = ("Ustawienia", "Settings"),
        ["html.blockedSettingsTip"] = ("Otwiera ustawienie „Zezwalaj na treści z internetu w podglądzie HTML”", "Opens the setting “Allow internet content in the HTML preview”"),
        ["html.blockedDismiss"] = ("Ukryj", "Dismiss"),

        // the preview itself
        ["html.runtimeMissing"] = ("Podgląd potrzebuje środowiska Microsoft Edge WebView2", "The preview needs the Microsoft Edge WebView2 Runtime"),
        ["html.runtimeMissingHint"] = ("Jest częścią Windows 11. Jeśli go brakuje, zainstaluj „WebView2 Runtime” od Microsoftu i otwórz kartę ponownie — edycja działa bez niego.",
            "It ships with Windows 11. If it is missing, install the “WebView2 Runtime” from Microsoft and reopen the tab — editing works without it."),
        ["html.runtimeDownload"] = ("Pobierz WebView2 Runtime", "Get WebView2 Runtime"),
        ["html.loadFailed"] = ("Nie udało się uruchomić podglądu", "The preview could not be started"),
        ["html.emptyPage"] = ("Nic do pokazania — zacznij pisać", "Nothing to show yet — start typing"),
        ["html.linkNotFound"] = ("Nie znaleziono: {0}", "Not found: {0}"),
        ["html.linkFailed"] = ("Nie udało się otworzyć: {0}", "Couldn’t open: {0}"),
        ["html.saveFirst"] = ("Najpierw zapisz plik, aby otworzyć go w przeglądarce.", "Save the file first to open it in a browser."),

        // commands (palette, shortcuts)
        ["cmd.html.devTools"] = ("Narzędzia deweloperskie podglądu (DevTools)", "Preview developer tools (DevTools)"),
        ["cmd.html.reload"] = ("Odśwież podgląd HTML", "Reload HTML preview"),
        ["cmd.html.openInBrowser"] = ("Otwórz w przeglądarce", "Open in browser"),

        // settings → Files
        ["settings.html.group"] = ("Podgląd HTML i SVG", "HTML and SVG preview"),
        ["settings.html.allowRemote"] = ("Zezwalaj na treści z internetu w podglądzie HTML", "Allow internet content in the HTML preview"),
        ["settings.html.allowRemoteDesc"] = ("Style, czcionki, skrypty i obrazy z CDN. Wyłączone: podgląd nie łączy się z siecią; pliki obok strony wczytują się zawsze.",
            "Styles, fonts, scripts and images from CDNs. Off: the preview makes no network connections; files next to the page always load."),
        ["settings.html.engine"] = ("Silnik podglądu", "Preview engine"),
        ["settings.html.engineDesc"] = ("Microsoft Edge WebView2 — uruchamiany tylko wtedy, gdy podgląd jest na ekranie, i zamykany, gdy żadna karta go nie potrzebuje.",
            "Microsoft Edge WebView2 — started only while a preview is on screen and closed when no tab needs it."),
        ["settings.html.engineMissing"] = ("Nie zainstalowano", "Not installed"),
        ["settings.html.engineGet"] = ("Pobierz", "Download"),

        // settings → About
        ["settings.about.news.070.html"] = ("Podgląd HTML i SVG jak w przeglądarce (Podgląd | Podział | Edycja): CSS, JS i obrazy względem pliku, odświeżanie na żywo podczas pisania, F12 = DevTools; treści z internetu domyślnie zablokowane.",
            "HTML and SVG preview like a browser (Preview | Split | Edit): CSS, JS and images relative to the file, live refresh while typing, F12 = DevTools; internet content blocked by default."),
        ["settings.about.lic.webview2"] = ("Podgląd HTML i SVG (silnik Microsoft Edge)", "HTML and SVG preview (the Microsoft Edge engine)"),
    });
}
