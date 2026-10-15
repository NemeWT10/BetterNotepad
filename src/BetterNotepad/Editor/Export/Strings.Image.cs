using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.Editor.Export;

/// <summary>Texts of "Copy as image" / "Save code as PNG".</summary>
internal static class ImageStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["cmd.editor.copyAsImage"] = ("Kopiuj jako obraz", "Copy as image"),
        ["cmd.editor.saveAsImage"] = ("Zapisz kod jako obraz PNG…", "Save code as PNG image…"),

        ["copyImage.done.one"] = ("Skopiowano kod jako obraz ({0} wiersz)", "Copied the code as an image ({0} line)"),
        ["copyImage.done.few"] = ("Skopiowano kod jako obraz ({0} wiersze)", "Copied the code as an image ({0} lines)"),
        ["copyImage.done.many"] = ("Skopiowano kod jako obraz ({0} wierszy)", "Copied the code as an image ({0} lines)"),
        ["copyImage.doneFile.one"] = ("Skopiowano cały plik jako obraz ({0} wiersz)", "Copied the whole file as an image ({0} line)"),
        ["copyImage.doneFile.few"] = ("Skopiowano cały plik jako obraz ({0} wiersze)", "Copied the whole file as an image ({0} lines)"),
        ["copyImage.doneFile.many"] = ("Skopiowano cały plik jako obraz ({0} wierszy)", "Copied the whole file as an image ({0} lines)"),
        ["copyImage.doneTruncated"] = ("Skopiowano obraz — mieści tylko pierwsze {0} z {1} wierszy. Zaznacz krótszy fragment, by objąć całość.",
            "Copied as an image — it fits only the first {0} of {1} lines. Select a shorter part to include all of it."),
        ["copyImage.truncated"] = ("Obraz mieści tylko pierwsze {0} z {1} wierszy. Zaznacz krótszy fragment, by objąć całość.",
            "The image fits only the first {0} of {1} lines. Select a shorter part to include all of it."),
        ["copyImage.saveAction"] = ("Zapisz jako PNG…", "Save as PNG…"),
        ["copyImage.saveTitle"] = ("Zapisz kod jako obraz", "Save code as image"),
        ["copyImage.filter"] = ("Obraz PNG", "PNG image"),
        ["copyImage.defaultName"] = ("kod", "code"),
        ["copyImage.saved"] = ("Zapisano obraz {0}", "Saved image {0}"),
        ["copyImage.openFolder"] = ("Pokaż w folderze", "Show in folder"),
        ["copyImage.saveFailed"] = ("Nie udało się zapisać obrazu: {0}", "Could not save the image: {0}"),
        ["copyImage.renderFailed"] = ("Nie udało się utworzyć obrazu: {0}", "Could not create the image: {0}"),

        // settings → editor
        ["settings.editor.imageGroup"] = ("Kopiuj jako obraz", "Copy as image"),
        ["settings.editor.imageLineNumbers"] = ("Numery wierszy", "Line numbers"),
        ["settings.editor.imageLineNumbersDesc"] = ("Przygaszone numery po lewej, zgodne z numeracją w pliku", "Dimmed numbers on the left, matching the file's numbering"),
        ["settings.editor.imageLight"] = ("Zawsze jasny", "Always light"),
        ["settings.editor.imageLightDesc"] = ("Jasna karta w kolorach motywu Daylight zamiast kolorów aktualnego motywu", "A light card in Daylight colors instead of the current theme's"),
        ["settings.editor.imageHintTitle"] = ("Obraz PNG w skali 2×", "A 2× PNG image"),
        ["settings.editor.imageHint"] = ("Zaznaczony kod albo cały plik — na slajd lub na czat. W menu kontekstowym edytora i w palecie poleceń, także „Zapisz kod jako obraz PNG…”.",
            "The selected code or the whole file — for slides and chats. In the editor's context menu and the command palette, along with “Save code as PNG image…”."),
    });
}
