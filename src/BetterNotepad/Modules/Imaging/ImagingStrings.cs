using System.Runtime.CompilerServices;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Imaging;

namespace BetterNotepad.Modules.Imaging;

/// <summary>Settings of the image tabs (section "imaging").</summary>
public sealed class ImagingSettings : SettingsSection, ISettingsSection
{
    public static string Key => "imaging";

    private int _jpegQuality = 92;
    private bool _metadataNoticeShown;

    /// <summary>JPEG quality of saved pictures (1..100); "Save as" JPEG asks with this as the default and remembers the answer.</summary>
    public int JpegQuality { get => _jpegQuality; set => Set(ref _jpegQuality, Math.Clamp(value, 1, 100)); }

    /// <summary>The one-time note "saved without EXIF / GPS" was shown.</summary>
    public bool MetadataNoticeShown { get => _metadataNoticeShown; set => Set(ref _metadataNoticeShown, value); }
}

/// <summary>Texts of the image viewer and editor (PL / EN) and the library's labels built from them.</summary>
internal static class ImagingStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        // viewer toolbar
        ["img.zoomIn"] = ("Powiększ", "Zoom in"),
        ["img.zoomOut"] = ("Pomniejsz", "Zoom out"),
        ["img.zoomLevel"] = ("Powiększenie — kliknij, aby zobaczyć 100%", "Zoom — click for 100%"),
        ["img.fit"] = ("Dopasuj do okna", "Fit to window"),
        ["img.actualSize"] = ("Rzeczywisty rozmiar (100%)", "Actual size (100%)"),
        ["img.rotate"] = ("Obróć o 90°", "Rotate 90°"),
        ["img.play"] = ("Odtwórz animację", "Play the animation"),
        ["img.pause"] = ("Wstrzymaj animację", "Pause the animation"),
        ["img.edit"] = ("Edytuj", "Edit"),
        ["img.done"] = ("Gotowe", "Done"),
        ["img.info"] = ("Informacje i metadane", "Info and metadata"),

        // editing tools
        ["img.crop"] = ("Kadruj", "Crop"),
        ["img.pen"] = ("Pióro", "Pen"),
        ["img.highlighter"] = ("Zakreślacz", "Highlighter"),
        ["img.line"] = ("Linia", "Line"),
        ["img.arrow"] = ("Strzałka", "Arrow"),
        ["img.rectangle"] = ("Prostokąt", "Rectangle"),
        ["img.ellipse"] = ("Elipsa", "Ellipse"),
        ["img.text"] = ("Tekst", "Text"),
        ["img.blur"] = ("Rozmycie", "Blur"),
        ["img.pixelate"] = ("Pikselizacja", "Pixelate"),
        ["img.colorAndSize"] = ("Kolor i grubość", "Color and thickness"),
        ["img.rotateLeft"] = ("Obróć w lewo", "Rotate left"),
        ["img.rotateRight"] = ("Obróć w prawo", "Rotate right"),
        ["img.flipH"] = ("Odbij w poziomie", "Flip horizontally"),
        ["img.flipV"] = ("Odbij w pionie", "Flip vertically"),
        ["img.resize"] = ("Zmień rozmiar", "Resize"),
        ["img.undo"] = ("Cofnij", "Undo"),
        ["img.redo"] = ("Ponów", "Redo"),
        ["img.width"] = ("Szerokość", "Width"),
        ["img.height"] = ("Wysokość", "Height"),
        ["img.keepRatio"] = ("Zachowaj proporcje", "Keep the aspect ratio"),
        ["img.apply"] = ("Zastosuj", "Apply"),
        ["img.cancel"] = ("Anuluj", "Cancel"),
        ["img.applyCrop"] = ("Przytnij do zaznaczenia", "Crop to the frame"),
        ["img.cancelCrop"] = ("Anuluj kadrowanie", "Cancel cropping"),
        ["img.thin"] = ("Cienka", "Thin"),
        ["img.medium"] = ("Średnia", "Medium"),
        ["img.thick"] = ("Gruba", "Thick"),
        ["img.px"] = ("px", "px"),
        ["img.textHint"] = ("Pisz, Enter kończy", "Type, Enter to finish"),

        // context menu, messages
        ["img.copy"] = ("Kopiuj obraz", "Copy image"),
        ["img.copied"] = ("Skopiowano obraz", "Image copied"),
        ["img.copyFailed"] = ("Nie udało się skopiować obrazu: {0}", "Couldn’t copy the image: {0}"),
        ["img.loading"] = ("Otwieranie obrazu…", "Opening the image…"),
        ["img.loadFailed"] = ("Nie udało się otworzyć tego obrazu — plik może być uszkodzony albo nie jest obrazem.",
            "This image could not be opened — the file may be damaged or not an image."),
        ["img.codecMissing"] = ("Windows nie ma dekodera obrazów {0}. Zainstaluj „Rozszerzenia obrazów {0}” ze Sklepu Microsoft, aby otworzyć ten plik.",
            "Windows has no decoder for {0} images. Install “{0} Image Extensions” from the Microsoft Store to open this file."),
        ["img.locked"] = ("Ta karta jest zablokowana — obrazu nie można edytować.", "This tab is locked — the picture can’t be edited."),

        // status bar
        ["img.status.px"] = ("{0} × {1} px", "{0} × {1} px"),
        ["img.status.zoomTip"] = ("Kliknij: dopasuj do okna ↔ 100%", "Click: fit to window ↔ 100%"),
        ["img.frames.one"] = ("{0} klatka", "{0} frame"),
        ["img.frames.few"] = ("{0} klatki", "{0} frames"),
        ["img.frames.many"] = ("{0} klatek", "{0} frames"),

        // info panel
        ["img.panel.title"] = ("Informacje o pliku", "File info"),
        ["img.panel.close"] = ("Zamknij panel (Esc)", "Close the panel (Esc)"),

        // saving
        ["img.fmt.ico"] = ("Ikona", "Icon"),
        ["img.save.jpegTitle"] = ("Jakość JPEG", "JPEG quality"),
        ["img.save.jpegText"] = ("Wyższa jakość to więcej szczegółów i większy plik. 90–95 to dobry wybór dla zdjęć.",
            "Higher quality keeps more detail and makes a larger file. 90–95 suits photos well."),
        ["img.save.quality"] = ("Jakość", "Quality"),
        ["img.save.smaller"] = ("mniejszy plik", "smaller file"),
        ["img.save.better"] = ("lepsza jakość", "better quality"),
        ["img.save.save"] = ("Zapisz", "Save"),
        ["img.save.framesTitle"] = ("Zapisać jeden obraz zamiast kilku?", "Save one picture instead of several?"),
        ["img.save.framesText"] = ("„{0}” zawiera kilka obrazów (klatki animacji, strony albo rozmiary ikony). Edytowany obraz zostanie zapisany jako jeden, a pozostałe znikną z pliku. Żeby je zachować, zapisz jako nowy plik.",
            "“{0}” holds several pictures (animation frames, pages or icon sizes). The edited picture is saved as a single one and the others are removed from the file. Save as a new file to keep them."),
        ["img.save.framesOne"] = ("Zapisz jeden obraz", "Save one picture"),
        ["img.save.framesSaveAs"] = ("Zapisz jako…", "Save as…"),
        ["img.save.cantWrite"] = ("Windows nie potrafi zapisywać plików {0}. Zapisz obraz w innym formacie.", "Windows can’t write {0} files. Save the picture in another format."),
        ["img.save.iconScaled"] = ("Ikona ma najwyżej 256 × 256 px — obraz zapisano pomniejszony do {0} × {1}.", "An icon is at most 256 × 256 px — the picture was saved scaled down to {0} × {1}."),
        ["img.save.noSource"] = ("Oryginalny plik zniknął z dysku, a obraz nie jest już w pamięci — nie ma czego zapisać.", "The original file is gone and the picture is no longer in memory — there is nothing to save."),

        // one-time note: saving drops EXIF / GPS
        ["img.meta.title"] = ("Zapisano bez metadanych", "Saved without metadata"),
        ["img.meta.text"] = ("Edytowane obrazy zapisujemy bez danych EXIF i GPS — bez informacji o aparacie, dacie i miejscu zrobienia zdjęcia. To dla Twojej prywatności. Obrót zdjęcia jest wbudowany w piksele, więc obraz wszędzie wygląda tak samo.",
            "Edited images are saved without EXIF and GPS data — no camera, date or place where the photo was taken. That’s for your privacy. The photo’s rotation is built into the pixels, so it looks the same everywhere."),
        ["img.meta.once"] = ("Ta informacja pojawia się tylko raz. Metadane oryginału zobaczysz w panelu „Informacje i metadane”.",
            "This note appears only once. The original’s metadata stays visible in the “Info and metadata” panel."),
        ["img.meta.ok"] = ("Rozumiem", "Got it"),

        // commands
        ["cmd.cat.image"] = ("Obraz", "Image"),
        ["cmd.image.edit"] = ("Edytuj obraz", "Edit image"),
        ["cmd.image.info"] = ("Informacje i metadane obrazu", "Image info and metadata"),
        ["cmd.image.copy"] = ("Kopiuj obraz", "Copy image"),
        ["cmd.image.rotateRight"] = ("Obróć obraz w prawo", "Rotate image right"),
        ["cmd.image.rotateLeft"] = ("Obróć obraz w lewo", "Rotate image left"),
        ["cmd.image.actualSize"] = ("Obraz w rzeczywistym rozmiarze (100%)", "Image at actual size (100%)"),
    });

    /// <summary>The viewer's labels in the current UI language.</summary>
    public static ImagingLabels Labels() => new()
    {
        ZoomIn = Loc.T("img.zoomIn"),
        ZoomOut = Loc.T("img.zoomOut"),
        ZoomLevel = Loc.T("img.zoomLevel"),
        Fit = Loc.T("img.fit"),
        ActualSize = Loc.T("img.actualSize"),
        Rotate = Loc.T("img.rotate"),
        Play = Loc.T("img.play"),
        Pause = Loc.T("img.pause"),
        Edit = Loc.T("img.edit"),
        Done = Loc.T("img.done"),
        Info = Loc.T("img.info"),
        Crop = Loc.T("img.crop"),
        Pen = Loc.T("img.pen"),
        Highlighter = Loc.T("img.highlighter"),
        Line = Loc.T("img.line"),
        Arrow = Loc.T("img.arrow"),
        Rectangle = Loc.T("img.rectangle"),
        Ellipse = Loc.T("img.ellipse"),
        Text = Loc.T("img.text"),
        Blur = Loc.T("img.blur"),
        Pixelate = Loc.T("img.pixelate"),
        ColorAndSize = Loc.T("img.colorAndSize"),
        RotateLeft = Loc.T("img.rotateLeft"),
        RotateRight = Loc.T("img.rotateRight"),
        FlipHorizontal = Loc.T("img.flipH"),
        FlipVertical = Loc.T("img.flipV"),
        Resize = Loc.T("img.resize"),
        Undo = Loc.T("img.undo"),
        Redo = Loc.T("img.redo"),
        Width = Loc.T("img.width"),
        Height = Loc.T("img.height"),
        KeepRatio = Loc.T("img.keepRatio"),
        Apply = Loc.T("img.apply"),
        Cancel = Loc.T("img.cancel"),
        ApplyCrop = Loc.T("img.applyCrop"),
        CancelCrop = Loc.T("img.cancelCrop"),
        Thin = Loc.T("img.thin"),
        Medium = Loc.T("img.medium"),
        Thick = Loc.T("img.thick"),
        Pixels = Loc.T("img.px"),
        Copy = Loc.T("img.copy"),
        SaveAs = Loc.T("cmd.file.saveAs"),
        Copied = Loc.T("img.copied"),
        Loading = Loc.T("img.loading"),
        LoadFailed = Loc.T("img.loadFailed"),
        CodecMissing = Loc.T("img.codecMissing"),
        TextHint = Loc.T("img.textHint"),
        SizeFormat = "{0} × {1}",
        FramesFormat = Loc.T("img.frames.many"),
    };
}
