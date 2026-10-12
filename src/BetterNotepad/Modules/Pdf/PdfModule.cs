using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Modules.Info;

namespace BetterNotepad.Modules.Pdf;

/// <summary>PDF module: pages with thumbnails, zoom, search, text copy, print and PDF → Word (BetterNotepad.Pdf / PDFium).</summary>
public static class PdfModule
{
    public static void Register()
    {
        DocumentManager.I.RegisterFactory(new Factory());
        DocumentViewRegistry.RegisterView(DocumentKind.Pdf, d => new PdfDocumentView((FileInfoDocument)d));
    }

    private sealed class Factory : IDocumentFactory
    {
        public DocumentKind Kind => DocumentKind.Pdf;

        public Document Create(string path, Guid? id = null) => new FileInfoDocument(path, DocumentKind.Pdf, id);
    }
}

internal static class PdfStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["pdf.thumbnails"] = ("Miniatury stron", "Page thumbnails"),
        ["pdf.pageTip"] = ("Numer strony — wpisz i naciśnij Enter", "Page number — type it and press Enter"),
        ["pdf.ofPages"] = ("/ {0}", "/ {0}"),
        ["pdf.zoomIn"] = ("Powiększ", "Zoom in"),
        ["pdf.zoomOut"] = ("Pomniejsz", "Zoom out"),
        ["pdf.ctrlWheel"] = ("Ctrl + kółko", "Ctrl + wheel"),
        ["pdf.fitWidth"] = ("Dopasuj do szerokości", "Fit width"),
        ["pdf.searchHint"] = ("Szukaj w PDF (Ctrl+F)", "Search the PDF (Ctrl+F)"),
        ["pdf.prevHit"] = ("Poprzednie wystąpienie (Shift+F3)", "Previous match (Shift+F3)"),
        ["pdf.nextHit"] = ("Następne wystąpienie (F3)", "Next match (F3)"),
        ["pdf.noHits"] = ("brak", "none"),
        ["pdf.print"] = ("Drukuj…", "Print…"),
        ["pdf.printing"] = ("Drukowanie…", "Printing…"),
        ["pdf.printed"] = ("Wysłano do drukarki", "Sent to the printer"),
        ["pdf.printFailed"] = ("Nie udało się wydrukować: {0}", "Could not print: {0}"),
        ["pdf.toWord"] = ("Konwertuj do Worda (.docx)…", "Convert to Word (.docx)…"),
        ["pdf.wordFilter"] = ("Dokument Word (*.docx)", "Word document (*.docx)"),
        ["pdf.converting"] = ("Konwersja do Worda…", "Converting to Word…"),
        ["pdf.converted"] = ("Zapisano {0} ({1})", "Saved {0} ({1})"),
        ["pdf.viaWord"] = ("przez Microsoft Word", "via Microsoft Word"),
        ["pdf.viaBuiltIn"] = ("wbudowana konwersja", "built-in conversion"),
        ["pdf.convertFailed"] = ("Nie udało się przekonwertować pliku do Worda.", "Could not convert the file to Word."),
        ["pdf.convertTimedOut"] = ("Word nie odpowiedział na czas — konwersja przerwana.", "Word did not respond in time — conversion stopped."),
        ["pdf.convertCanceled"] = ("Konwersja anulowana.", "Conversion canceled."),
        ["pdf.copied"] = ("Skopiowano tekst", "Text copied"),
        ["pdf.copiedPages"] = ("Skopiowano tekst z {0} stron", "Copied the text of {0} pages"),
        ["pdf.copiedPartial"] = ("Zaznaczenie jest bardzo duże — skopiowano tekst stron {0}–{1} (limit {2} mln znaków).",
            "The selection is very large — copied the text of pages {0}–{1} (limit {2} million characters)."),
        ["pdf.copying"] = ("Kopiowanie tekstu…", "Copying the text…"),
        ["pdf.noText"] = ("Zaznaczony fragment nie zawiera tekstu (np. skan).", "The selection has no text (a scan, for example)."),
        ["pdf.opening"] = ("Otwieranie PDF…", "Opening the PDF…"),
        ["pdf.unsupportedSecurity"] = ("Ten PDF używa nieobsługiwanego zabezpieczenia — nie można go wyświetlić.", "This PDF uses an unsupported security handler — it can't be displayed."),
        ["pdf.pw.title"] = ("PDF chroniony hasłem", "Password-protected PDF"),
        ["pdf.pw.text"] = ("„{0}” jest zabezpieczony. Wpisz hasło, aby go otworzyć.", "“{0}” is protected. Enter its password to open it."),
        ["pdf.pw.placeholder"] = ("Hasło", "Password"),
        ["pdf.pw.show"] = ("Pokaż hasło", "Show password"),
        ["pdf.pw.hide"] = ("Ukryj hasło", "Hide password"),
        ["pdf.pw.open"] = ("Otwórz", "Open"),
        ["pdf.pw.wrong"] = ("Nieprawidłowe hasło. Spróbuj jeszcze raz.", "Incorrect password. Try again."),
        ["pdf.pw.empty"] = ("Wpisz hasło.", "Enter the password."),
        ["pdf.pw.caps"] = ("Caps Lock jest włączony", "Caps Lock is on"),
        ["pdf.pw.note"] = ("Hasło zostaje tylko w pamięci, do zamknięcia programu — nigdzie go nie zapisujemy.",
            "The password stays in memory only, until you close the app — it is never saved."),
        ["pdf.locked.title"] = ("Ten PDF jest chroniony hasłem", "This PDF is password protected"),
        ["pdf.locked.text"] = ("Wpisz hasło, aby zobaczyć strony. Po ponownym uruchomieniu programu zapytamy o nie znowu.",
            "Enter its password to see the pages. After a restart you will be asked again."),
        ["pdf.locked.unlock"] = ("Wpisz hasło", "Enter password"),
        ["pdf.accessDenied"] = ("Brak dostępu do pliku.", "Access to the file was denied."),
        ["pdf.invalid"] = ("To nie jest poprawny plik PDF albo jest uszkodzony.", "This is not a valid PDF file, or it is damaged."),
        ["pdf.openFailed"] = ("Nie udało się otworzyć PDF: {0}", "Could not open the PDF: {0}"),
    });
}
