using BetterNotepad.Core.Settings;

namespace BetterNotepad.Editor.Export;

/// <summary>"Copy with formatting" and the thesis-listing mode (its options are remembered from its dialog).</summary>
public sealed class CopySettings : SettingsSection, ISettingsSection
{
    public static string Key => "copy";

    private bool _lineNumbers;
    private bool _themeColors;
    private bool _dedent = true;

    private string _listingFont = "Consolas";
    private double _listingFontSize = 10;
    private bool _listingColors;
    private bool _listingLineNumbers;
    private bool _listingFrame;
    private bool _listingCaptionBelow;
    private bool _listingSource;
    private string _listingSourceText = "";
    private bool _listingDedent = true;
    private bool _listingWideSpacing;
    private int _listingChapter = 1;
    private int _listingNumber;

    private bool _imageLineNumbers;
    private bool _imageLightTheme;

    // ── Copy with formatting ──
    public bool LineNumbers { get => _lineNumbers; set => Set(ref _lineNumbers, value); }

    /// <summary>Colors of the active theme instead of the light document palette.</summary>
    public bool ThemeColors { get => _themeColors; set => Set(ref _themeColors, value); }

    /// <summary>Remove the indentation shared by all copied lines.</summary>
    public bool Dedent { get => _dedent; set => Set(ref _dedent, value); }

    // ── thesis listing ──
    public string ListingFont { get => _listingFont; set => Set(ref _listingFont, string.IsNullOrWhiteSpace(value) ? "Consolas" : value); }
    public double ListingFontSize { get => _listingFontSize; set => Set(ref _listingFontSize, Math.Clamp(value, 7, 14)); }
    public bool ListingColors { get => _listingColors; set => Set(ref _listingColors, value); }
    public bool ListingLineNumbers { get => _listingLineNumbers; set => Set(ref _listingLineNumbers, value); }
    public bool ListingFrame { get => _listingFrame; set => Set(ref _listingFrame, value); }
    public bool ListingCaptionBelow { get => _listingCaptionBelow; set => Set(ref _listingCaptionBelow, value); }
    public bool ListingSource { get => _listingSource; set => Set(ref _listingSource, value); }

    /// <summary>Empty = the default "Źródło: opracowanie własne" / "Source: own work".</summary>
    public string ListingSourceText { get => _listingSourceText; set => Set(ref _listingSourceText, value ?? ""); }

    public bool ListingDedent { get => _listingDedent; set => Set(ref _listingDedent, value); }

    /// <summary>Code at 1.5 lines like the body text (default: single, the guidelines set 1.5 for body text only).</summary>
    public bool ListingWideSpacing { get => _listingWideSpacing; set => Set(ref _listingWideSpacing, value); }

    /// <summary>Last copied listing: chapter and number within it (the next one is suggested).</summary>
    public int ListingChapter { get => _listingChapter; set => Set(ref _listingChapter, Math.Clamp(value, 1, 99)); }
    public int ListingNumber { get => _listingNumber; set => Set(ref _listingNumber, Math.Clamp(value, 0, 999)); }

    // ── Copy as image ──
    public bool ImageLineNumbers { get => _imageLineNumbers; set => Set(ref _imageLineNumbers, value); }

    /// <summary>Always the light Daylight colors instead of the active theme.</summary>
    public bool ImageLightTheme { get => _imageLightTheme; set => Set(ref _imageLightTheme, value); }
}
